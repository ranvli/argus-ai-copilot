using System.Diagnostics;
using System.Threading.Channels;
using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Audio.Capture;
using Argus.Audio.Diagnostics;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Argus.Transcription.Configuration;
using Argus.Transcription.Text;
using Argus.Transcription.Whisper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace Argus.Transcription.Pipeline;

public sealed class TranscriptionPipeline : ITranscriptionPipeline, IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly IModelResolver _modelResolver;
    private readonly ILogger<TranscriptionPipeline> _logger;
    private readonly WhisperModelService? _whisperModelService;
    private readonly TranscriptionRuntimeSettings _runtimeSettings;

    private readonly string _debugAudioFolder;
    private static readonly bool DebugAudioEnabled = true;
    private int _debugFileIndex;
    private bool _dbgFirstSilentSaved;

    public bool SkipSystemAudioCapture { get; set; } = false;

    private MicrophoneCaptureSource? _mic;
    private SystemAudioCaptureSource? _sysAudio;

    private ITranscriptionModel? _transcriptionModel;
    private string _transcriptionProvider = string.Empty;
    private string _transcriptionModelId = string.Empty;

    private const int ChannelCapacity = 20;
    private readonly Channel<AudioChunk> _chunkChannel =
        Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly object _queueMetricsLock = new();
    private readonly Queue<(AudioSource Source, DateTimeOffset CapturedAt)> _queueMirror = new();
    private long _micEnqueued;
    private long _systemEnqueued;
    private long _micProcessed;
    private long _systemProcessed;
    private long _micDropped;
    private long _systemDropped;
    private DateTimeOffset? _lastMicChunkAt;
    private DateTimeOffset? _lastSystemChunkAt;
    private DateTimeOffset _lastPipelineHealthAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDualHealthAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStatusPublishedAt = DateTimeOffset.MinValue;
    private volatile bool _drainLoopAlive;

    private CancellationTokenSource? _drainCts;
    private Task? _drainTask;
    private Guid _sessionId;
    private int _segmentCount;
    private DateTimeOffset? _lastTranscriptionAt;
    private string? _lastTranscriptionError;
    private AudioChunk? _pendingMicChunk;
    private string? _lockedLanguage;
    private string? _languageCandidate;
    private int _languageCandidateHits;
    private int _languageProbeChunksObserved;
    private bool _disposing;
    private bool _stopped;
    private bool _disposed;
    private int _highQueueStreak;
    private DateTimeOffset _lastProviderBottleneckLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBacklogWarningAt = DateTimeOffset.MinValue;

    private const float ChunkNormalizationTargetPeak = 0.45f;
    private const float ChunkNormalizationMinPeak = 0.015f;
    private const float ChunkNormalizationMinRms = 0.002f;
    private const float ChunkNormalizationMaxGain = 6.0f;
    private const int LanguageLockRequiredHits = 3;
    private const int HighQueueWarningThreshold = 3;
    private const int CriticalQueueWarningThreshold = 5;
    private const int HighQueueWarningStreak = 3;
    private static readonly TimeSpan MaxMergedMicDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PipelineHealthInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DualHealthInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusPublishInterval = TimeSpan.FromMilliseconds(350);

    public AudioStatusSnapshot Status { get; private set; } = AudioStatusSnapshot.Idle;
    public event EventHandler<AudioStatusSnapshot>? StatusChanged;
    public event EventHandler<IReadOnlyList<TranscriptSegment>>? SegmentsProduced;

    public TranscriptionPipeline(
        IModelResolver modelResolver,
        ILogger<TranscriptionPipeline> logger,
        IOptions<TranscriptionRuntimeSettings> runtimeSettings,
        WhisperModelService? whisperModelService = null)
    {
        _modelResolver = modelResolver;
        _logger = logger;
        _runtimeSettings = runtimeSettings.Value;
        _whisperModelService = whisperModelService;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _debugAudioFolder = Path.Combine(appData, "ArgusAI", "debug", "audio");
        if (DebugAudioEnabled)
            Directory.CreateDirectory(_debugAudioFolder);
    }

    public void SetSources(MicrophoneCaptureSource? mic, SystemAudioCaptureSource? sysAudio = null)
    {
        _mic = mic;
        _sysAudio = sysAudio;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_mic is null && (_sysAudio is null || SkipSystemAudioCapture))
            throw new InvalidOperationException("At least one capture source must be configured before StartAsync.");

        _sessionId = sessionId;
        _segmentCount = 0;
        _lastTranscriptionAt = null;
        _lastTranscriptionError = null;
        _dbgFirstSilentSaved = false;
        _pendingMicChunk = null;
        _lockedLanguage = null;
        _languageCandidate = null;
        _languageCandidateHits = 0;
        _languageProbeChunksObserved = 0;
        _highQueueStreak = 0;
        _stopped = false;
        ResetPipelineDiagnostics();

        ResolveTranscriptionProvider();

        _drainCts = new CancellationTokenSource();
        _drainTask = Task.Run(() => DrainChannelAsync(_drainCts.Token));

        _logger.LogInformation(
            "[Audio.Session] sessionId={SessionId} microphone={MicEnabled} systemAudio={SystemEnabled} provider={Provider} model={Model} utc={Utc:O}",
            sessionId, _mic is not null, _sysAudio is not null && !SkipSystemAudioCapture,
            _transcriptionProvider, _transcriptionModelId, DateTimeOffset.UtcNow);

        if (_mic is not null)
        {
            _logger.LogInformation(
                "[Audio.Source.Start] sessionId={SessionId} source=Microphone requestedBackend={Backend} device='{Device}'",
                sessionId, _mic.SelectedBackend, _mic.DisplayName);
            _mic.ChunkReady += OnChunkReady;
            try
            {
                await _mic.StartAsync(sessionId, CancellationToken.None);
                _logger.LogInformation(
                    "[Audio.Source.Started] sessionId={SessionId} source=Microphone backend={Backend} device='{Device}' status={Status}",
                    sessionId, _mic.ActiveBackend, _mic.DisplayName, _mic.Status);
            }
            catch (Exception ex)
            {
                _mic.ChunkReady -= OnChunkReady;
                _logger.LogError(ex,
                    "[Audio.Source.StartFailed] sessionId={SessionId} source=Microphone device='{Device}'",
                    sessionId, _mic.DisplayName);
                PublishStatus(micError: ex.Message, force: true);
                throw;
            }
        }

        if (_sysAudio is not null && SkipSystemAudioCapture)
        {
            _logger.LogWarning(
                "[Pipeline.CapturePlan] sessionId={SessionId} systemAudioSuppressed=true reason=SkipSystemAudioCapture",
                sessionId);
        }
        else if (_sysAudio is not null)
        {
            _logger.LogInformation(
                "[Audio.Source.Start] sessionId={SessionId} source=SystemAudio backend=WasapiLoopback device='{Device}'",
                sessionId, _sysAudio.DisplayName);
            _sysAudio.ChunkReady += OnChunkReady;
            try
            {
                await _sysAudio.StartAsync(sessionId, CancellationToken.None);
                _logger.LogInformation(
                    "[Audio.Source.Started] sessionId={SessionId} source=SystemAudio backend=WasapiLoopback device='{Device}' status={Status}",
                    sessionId, _sysAudio.DisplayName, _sysAudio.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Audio.Source.StartFailed] sessionId={SessionId} source=SystemAudio device='{Device}' continuingWithoutSystemAudio=true",
                    sessionId, _sysAudio.DisplayName);
                _sysAudio.ChunkReady -= OnChunkReady;
                _sysAudio = null;
            }
        }

        PublishStatus(force: true);
        LogPipelineHealth(force: true);
        LogDualCaptureHealth(force: true);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed || _stopped) return;

        _logger.LogInformation("[Audio.Session.Stop] sessionId={SessionId}", _sessionId);

        if (_mic is not null)
        {
            _mic.ChunkReady -= OnChunkReady;
            await _mic.StopAsync(ct);
        }

        if (_sysAudio is not null)
        {
            _sysAudio.ChunkReady -= OnChunkReady;
            await _sysAudio.StopAsync(ct);
        }

        _chunkChannel.Writer.TryComplete();

        if (_drainTask is not null)
        {
            try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (TimeoutException)
            {
                _logger.LogWarning("[Pipeline.Stop] sessionId={SessionId} drainTimeout=true", _sessionId);
            }
            catch (OperationCanceledException) { }
        }

        _drainCts?.Cancel();
        _drainCts?.Dispose();
        _drainCts = null;
        _transcriptionModel = null;
        _stopped = true;

        LogPipelineHealth(force: true);
        LogDualCaptureHealth(force: true);
        PublishStatus(force: true);
    }

    public void Pause()
    {
        _mic?.Pause();
        _sysAudio?.Pause();
        PublishStatus(force: true);
    }

    public void Resume()
    {
        _mic?.Resume();
        _sysAudio?.Resume();
        PublishStatus(force: true);
    }

    private void ResolveTranscriptionProvider()
    {
        _transcriptionModel = null;
        _transcriptionProvider = string.Empty;
        _transcriptionModelId = string.Empty;

        try
        {
            var model = _modelResolver.ResolveTranscriptionModel(AiWorkflow.SpeechTranscription);
            _transcriptionModel = model;
            _transcriptionProvider = model.ProviderId;
            _transcriptionModelId = model.ModelId;

            _logger.LogInformation(
                "[Pipeline.Provider] provider={Provider} modelId={ModelId} sherpaFamily={Family} chunkMs={ChunkMs} lowLatency={LowLatency}",
                model.ProviderId, model.ModelId, _runtimeSettings.SherpaModelFamily,
                _runtimeSettings.SherpaChunkDurationMs, _runtimeSettings.EnableSherpaLowLatencyMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline.Provider] resolution_failed=true");
        }
    }

    private void OnChunkReady(object? sender, AudioChunk chunk)
    {
        var rms = AudioChunkDiagnostics.ComputeRms(chunk.Data);
        var peak = AudioChunkDiagnostics.ComputePeak(chunk.Data);
        var (_, _, zeroRatio) = AudioChunkDiagnostics.ComputeMinMaxZero(chunk.Data);

        string? dropReason = null;
        if (chunk.Data.Length == 0) dropReason = "zero_bytes";
        else if (chunk.Duration <= TimeSpan.Zero) dropReason = "invalid_duration";
        else if (zeroRatio >= 1.0f) dropReason = "all_zero_pcm";
        else if (peak < 0.0001f) dropReason = "dead_signal";

        if (dropReason is not null)
        {
            IncrementDropped(chunk.Source);
            _logger.LogDebug(
                "[Audio.ChunkDrop] sessionId={SessionId} source={Source} chunkId={ChunkId} reason={Reason} bytes={Bytes} durationMs={DurationMs:F1} rms={Rms:F6} peak={Peak:F6} zeroRatio={ZeroRatio:F4}",
                _sessionId, chunk.Source, chunk.Id, dropReason, chunk.Data.Length,
                chunk.Duration.TotalMilliseconds, rms, peak, zeroRatio);
            LogPipelineHealth(force: false);
            LogDualCaptureHealth(force: false);
            PublishStatus();
            return;
        }

        _logger.LogDebug(
            "[Audio.Chunk] sessionId={SessionId} chunkId={ChunkId} source={Source} durationMs={DurationMs:F1} bytes={Bytes} rms={Rms:F6} peak={Peak:F6} zeroRatio={ZeroRatio:F4}",
            _sessionId, chunk.Id, chunk.Source, chunk.Duration.TotalMilliseconds,
            chunk.Data.Length, rms, peak, zeroRatio);

        if (chunk.Source == AudioSource.Microphone) _lastMicChunkAt = DateTimeOffset.UtcNow;
        if (chunk.Source == AudioSource.SystemAudio) _lastSystemChunkAt = DateTimeOffset.UtcNow;

        int pendingBefore;
        AudioSource? evictedSource = null;
        lock (_queueMetricsLock)
        {
            pendingBefore = _queueMirror.Count;
            if (_queueMirror.Count >= ChannelCapacity)
            {
                evictedSource = _queueMirror.Dequeue().Source;
                IncrementDropped(evictedSource.Value);
            }
        }

        var written = _chunkChannel.Writer.TryWrite(chunk);
        if (written)
        {
            lock (_queueMetricsLock)
                _queueMirror.Enqueue((chunk.Source, chunk.CapturedAt));
            IncrementEnqueued(chunk.Source);

            _logger.LogDebug(
                "[Pipeline.Enqueue] sessionId={SessionId} chunkId={ChunkId} source={Source} queueDepthBefore={Before} queueDepthAfter={After} evictedOldestSource={EvictedSource}",
                _sessionId, chunk.Id, chunk.Source, pendingBefore, (int)_chunkChannel.Reader.Count,
                evictedSource?.ToString() ?? "none");
        }
        else
        {
            IncrementDropped(chunk.Source);
            _logger.LogWarning(
                "[Pipeline.Enqueue] sessionId={SessionId} chunkId={ChunkId} source={Source} accepted=false queueDepth={Depth}",
                _sessionId, chunk.Id, chunk.Source, (int)_chunkChannel.Reader.Count);
        }

        LogPipelineHealth(force: false);
        LogDualCaptureHealth(force: false);
        PublishStatus();
    }

    private async Task DrainChannelAsync(CancellationToken ct)
    {
        _drainLoopAlive = true;
        _logger.LogInformation("[Pipeline.DrainLoop] sessionId={SessionId} alive=true", _sessionId);

        try
        {
            await foreach (var chunk in _chunkChannel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                lock (_queueMetricsLock)
                {
                    if (_queueMirror.Count > 0)
                        _queueMirror.Dequeue();
                }

                var pending = (int)_chunkChannel.Reader.Count;
                var queueAgeMs = Math.Max(0, (DateTimeOffset.UtcNow - chunk.CapturedAt).TotalMilliseconds);
                ObserveBacklog(pending);

                _logger.LogDebug(
                    "[Pipeline.Dequeue] sessionId={SessionId} chunkId={ChunkId} source={Source} queueDepthBefore={Depth} queueAgeMs={QueueAgeMs:F1}",
                    _sessionId, chunk.Id, chunk.Source, pending + 1, queueAgeMs);

                await ProcessChunkAsync(chunk, pending, ct);
                IncrementProcessed(chunk.Source);
                LogPipelineHealth(force: false);
                LogDualCaptureHealth(force: false);
                PublishStatus();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline.DrainLoop] sessionId={SessionId} unexpected_exception=true", _sessionId);
        }
        finally
        {
            _drainLoopAlive = false;
            _logger.LogInformation("[Pipeline.DrainLoop] sessionId={SessionId} alive=false", _sessionId);
        }
    }

    private async Task ProcessChunkAsync(AudioChunk chunk, int queueBefore, CancellationToken ct)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var queueAgeMs = Math.Max(0, (DateTimeOffset.UtcNow - chunk.CapturedAt).TotalMilliseconds);
        var stageAction = "not_applicable";
        var stageDelayMs = 0d;
        var wavWriteMs = 0d;
        var providerMs = 0d;
        var emittedSegments = 0;
        var emittedOrDropped = "dropped";
        string? detectedLanguage = null;

        if (chunk.Source == AudioSource.Microphone)
        {
            var staged = StageMicrophoneChunk(chunk);
            stageAction = staged.Action;
            stageDelayMs = staged.StageDelay.TotalMilliseconds;
            if (staged.Chunk is null)
            {
                IncrementDropped(chunk.Source);
                LogChunkLatency(chunk, queueAgeMs, queueBefore, stageAction, stageDelayMs, wavWriteMs, providerMs,
                    totalStopwatch.Elapsed.TotalMilliseconds, emittedSegments, detectedLanguage, emittedOrDropped);
                return;
            }
            chunk = staged.Chunk;
        }

        if (_transcriptionModel is null)
        {
            IncrementDropped(chunk.Source);
            LogChunkLatency(chunk, queueAgeMs, queueBefore, stageAction, stageDelayMs, wavWriteMs, providerMs,
                totalStopwatch.Elapsed.TotalMilliseconds, emittedSegments, detectedLanguage, emittedOrDropped);
            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.NoProvider, force: true);
            return;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"argus_{chunk.Id:N}.wav");
        var canUseInMemoryAudio = _transcriptionModel.SupportsInMemoryAudio;

        try
        {
            var inputRms = AudioChunkDiagnostics.ComputeRms(chunk.Data);
            var inputPeak = AudioChunkDiagnostics.ComputePeak(chunk.Data);
            var whisperPcm = NormalizeChunkForWhisper(chunk.Data, out var appliedGain, out var outputRms, out var outputPeak);
            var whisperDuration = TimeSpan.FromSeconds((double)whisperPcm.Length / (16_000 * 2));

            if (inputPeak < 0.002f && DebugAudioEnabled && !_dbgFirstSilentSaved)
            {
                _dbgFirstSilentSaved = true;
                var idx = Interlocked.Increment(ref _debugFileIndex);
                var sourceName = chunk.Source == AudioSource.Microphone ? "mic" : "sys";
                var debugFile = Path.Combine(_debugAudioFolder,
                    $"{sourceName}_{idx:D4}_{DateTimeOffset.UtcNow:HHmmss}_{inputRms:F3}.wav");
                WriteWav(debugFile, whisperPcm);
                _logger.LogDebug(
                    "[Audio.DebugArtifact] sessionId={SessionId} chunkId={ChunkId} source={Source} reason=first_silent path='{Path}'",
                    _sessionId, chunk.Id, chunk.Source, debugFile);
            }

            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.Transcribing);
            if (!canUseInMemoryAudio)
            {
                var wavWriteStopwatch = Stopwatch.StartNew();
                WriteWav(tempFile, whisperPcm);
                wavWriteStopwatch.Stop();
                wavWriteMs = wavWriteStopwatch.Elapsed.TotalMilliseconds;
            }

            _logger.LogDebug(
                "[ChunkGain] sessionId={SessionId} chunkId={ChunkId} source={Source} inputRms={InputRms:F4} inputPeak={InputPeak:F4} appliedGain={Gain:F2} outputRms={OutputRms:F4} outputPeak={OutputPeak:F4}",
                _sessionId, chunk.Id, chunk.Source, inputRms, inputPeak, appliedGain, outputRms, outputPeak);

            var (requestLanguage, languageMode) = ResolveRequestLanguage();
            var request = new TranscriptionRequest
            {
                AudioFilePath = canUseInMemoryAudio ? string.Empty : tempFile,
                AudioPcm16 = whisperPcm,
                AudioSampleRate = 16_000,
                AudioChannels = 1,
                PreferInMemoryAudio = canUseInMemoryAudio,
                Language = requestLanguage,
                WordTimestamps = false
            };

            if (_transcriptionProvider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[Sherpa.Input] sessionId={SessionId} chunkId={ChunkId} source={Source} durationMs={DurationMs:F1} rms={Rms:F6} peak={Peak:F6} queueBefore={QueueBefore}",
                    _sessionId, chunk.Id, chunk.Source, whisperDuration.TotalMilliseconds, outputRms, outputPeak, queueBefore);
            }

            var providerStopwatch = Stopwatch.StartNew();
            var response = await _transcriptionModel.TranscribeAsync(request, ct);
            providerStopwatch.Stop();
            providerMs = providerStopwatch.Elapsed.TotalMilliseconds;
            LogProviderBottleneck(chunk, languageMode, providerMs, whisperDuration, queueBefore);

            if (_transcriptionProvider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[Sherpa.Output] sessionId={SessionId} chunkId={ChunkId} source={Source} elapsedMs={ElapsedMs:F1} isError={IsError} textLength={TextLength} preview='{Preview}'",
                    _sessionId, chunk.Id, chunk.Source, providerMs, response.IsError,
                    response.FullText?.Length ?? 0,
                    string.IsNullOrWhiteSpace(response.FullText)
                        ? string.Empty
                        : response.FullText.Length > 100 ? response.FullText[..100] + "…" : response.FullText);
            }

            if (response.IsError)
            {
                _lastTranscriptionError = response.ErrorMessage;
                PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.Error,
                    transcriptionError: response.ErrorMessage, force: true);
                LogChunkLatency(chunk, queueAgeMs, queueBefore, stageAction, stageDelayMs, wavWriteMs, providerMs,
                    totalStopwatch.Elapsed.TotalMilliseconds, emittedSegments, detectedLanguage, "error");
                return;
            }

            _lastTranscriptionError = null;
            _lastTranscriptionAt = DateTimeOffset.UtcNow;
            detectedLanguage = SanitizeLanguage(response.DetectedLanguage);
            var validSegments = TranscriptTextFilter.FilterMeaningfulSegments(response.Segments);

            var deadMicSignal = chunk.Source == AudioSource.Microphone
                && IsClearlyDeadSignal(inputRms, inputPeak)
                && IsClearlyDeadSignal(outputRms, outputPeak);
            if (deadMicSignal)
            {
                IncrementDropped(chunk.Source);
                ResetLanguageCandidate();
                LogChunkLatency(chunk, queueAgeMs, queueBefore, stageAction, stageDelayMs, wavWriteMs, providerMs,
                    totalStopwatch.Elapsed.TotalMilliseconds, emittedSegments, detectedLanguage, "dead_signal_drop");
                return;
            }

            if (!_transcriptionProvider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase) &&
                _runtimeSettings.EnableAutoLanguageProbe)
            {
                var effectiveText = TranscriptTextFilter.IsMeaningfulText(response.FullText)
                    ? response.FullText.Trim()
                    : string.Join(" ", validSegments.Select(segment => segment.Text.Trim()));
                UpdateLanguageLock(detectedLanguage, effectiveText, validSegments, inputRms, inputPeak, chunk.Id);
            }

            if (!TranscriptTextFilter.IsMeaningfulText(response.FullText) && validSegments.Count == 0)
            {
                IncrementDropped(chunk.Source);
                emittedOrDropped = "dropped";
            }
            else if (validSegments.Count == 0 && TranscriptTextFilter.IsMeaningfulText(response.FullText))
            {
                var synthetic = new TranscriptSegment
                {
                    SessionId = _sessionId,
                    Text = response.FullText.Trim(),
                    SpeakerType = SpeakerTypeForSource(chunk.Source),
                    SpeakerLabel = SpeakerLabelForSource(chunk.Source),
                    Language = response.DetectedLanguage,
                    Range = new TimeRange(chunk.CapturedAt, chunk.CapturedAt + chunk.Duration),
                    Confidence = ConfidenceScore.None
                };
                var list = (IReadOnlyList<TranscriptSegment>)[synthetic];
                _segmentCount++;
                emittedSegments = 1;
                emittedOrDropped = "emitted";
                SegmentsProduced?.Invoke(this, list);
            }
            else if (validSegments.Count > 0)
            {
                var anchored = StampSegments(validSegments, chunk);
                _segmentCount += anchored.Count;
                emittedSegments = anchored.Count;
                emittedOrDropped = "emitted";
                SegmentsProduced?.Invoke(this, anchored);
            }

            LogChunkLatency(chunk, queueAgeMs, queueBefore, stageAction, stageDelayMs, wavWriteMs, providerMs,
                totalStopwatch.Elapsed.TotalMilliseconds, emittedSegments, detectedLanguage, emittedOrDropped);
            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.Idle);
        }
        catch (OperationCanceledException)
        {
            LogChunkLatency(chunk, queueAgeMs, queueBefore, stageAction, stageDelayMs, wavWriteMs, providerMs,
                totalStopwatch.Elapsed.TotalMilliseconds, emittedSegments, detectedLanguage, "cancelled");
        }
        catch (Exception ex)
        {
            _lastTranscriptionError = ex.Message;
            _logger.LogError(ex,
                "[Pipeline.TxException] sessionId={SessionId} chunkId={ChunkId} source={Source} provider={Provider} modelId={ModelId}",
                _sessionId, chunk.Id, chunk.Source, _transcriptionProvider, _transcriptionModelId);
            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.Error,
                transcriptionError: ex.Message, force: true);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { }
        }
    }

    private static void WriteWav(string path, byte[] pcm16kMono16bit)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        writer.Write(pcm16kMono16bit, 0, pcm16kMono16bit.Length);
    }

    private ChunkStageDecision StageMicrophoneChunk(AudioChunk chunk)
    {
        var metrics = AnalyzeSpeechActivity(chunk.Data);
        var lowActivity = metrics.IsLowActivity;

        if (IsSherpaLowLatencyMode() && !IsClearlyDeadSignal(metrics.RawRms, metrics.RawPeak))
        {
            _pendingMicChunk = null;
            LogLatencyPolicy("sherpa_send_immediately", chunk.Duration, metrics);
            return new ChunkStageDecision(chunk, "sherpa_send_immediately", TimeSpan.Zero);
        }

        if (_pendingMicChunk is null)
        {
            if (!lowActivity)
                return new ChunkStageDecision(chunk, "send_immediately", TimeSpan.Zero);

            _pendingMicChunk = chunk;
            LogLatencyPolicy("buffer_first_low_signal", chunk.Duration, metrics);
            return new ChunkStageDecision(null, "buffer_first_low_signal", TimeSpan.Zero);
        }

        var merged = MergeChunks(_pendingMicChunk, chunk);
        _pendingMicChunk = null;
        var mergedMetrics = AnalyzeSpeechActivity(merged.Data);
        var stageDelay = chunk.CapturedAt - merged.CapturedAt;

        if (merged.Duration > MaxMergedMicDuration)
            return new ChunkStageDecision(null, "drop_low_signal_exceeded_max_duration", stageDelay);

        if (mergedMetrics.IsLowActivity)
            return new ChunkStageDecision(null, "drop_low_signal_after_single_merge", stageDelay);

        return new ChunkStageDecision(merged, lowActivity ? "single_merge_and_send" : "merge_and_send", stageDelay);
    }

    private SpeechActivityMetrics AnalyzeSpeechActivity(byte[] data)
    {
        var rawRms = AudioChunkDiagnostics.ComputeRms(data);
        var rawPeak = AudioChunkDiagnostics.ComputePeak(data);
        _ = NormalizeChunkForWhisper(data, out var appliedGain, out var normalizedRms, out var normalizedPeak);
        var rmsThreshold = _runtimeSettings.MicLowActivityRmsThreshold;
        var peakThreshold = _runtimeSettings.MicLowActivityPeakThreshold;
        var rawSpeechLike = rawRms >= rmsThreshold || rawPeak >= peakThreshold;
        var normalizedSpeechLike = normalizedRms >= rmsThreshold || normalizedPeak >= peakThreshold;
        return new SpeechActivityMetrics(rawRms, rawPeak, normalizedRms, normalizedPeak, appliedGain,
            !(rawSpeechLike || normalizedSpeechLike));
    }

    private static AudioChunk MergeChunks(AudioChunk first, AudioChunk second)
    {
        var mergedData = new byte[first.Data.Length + second.Data.Length];
        Buffer.BlockCopy(first.Data, 0, mergedData, 0, first.Data.Length);
        Buffer.BlockCopy(second.Data, 0, mergedData, first.Data.Length, second.Data.Length);
        return new AudioChunk
        {
            SessionId = first.SessionId,
            CapturedAt = first.CapturedAt,
            Duration = first.Duration + second.Duration,
            Source = first.Source,
            Data = mergedData
        };
    }

    private static byte[] NormalizeChunkForWhisper(
        byte[] pcm16,
        out float appliedGain,
        out float outputRms,
        out float outputPeak)
    {
        var inputRms = AudioChunkDiagnostics.ComputeRms(pcm16);
        var inputPeak = AudioChunkDiagnostics.ComputePeak(pcm16);
        appliedGain = 1f;

        if (inputPeak >= ChunkNormalizationMinPeak && inputRms >= ChunkNormalizationMinRms &&
            inputPeak < ChunkNormalizationTargetPeak)
        {
            appliedGain = MathF.Min(ChunkNormalizationTargetPeak / inputPeak, ChunkNormalizationMaxGain);
        }

        if (appliedGain <= 1.01f)
        {
            outputRms = inputRms;
            outputPeak = inputPeak;
            return pcm16;
        }

        var normalized = new byte[pcm16.Length];
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            var boosted = Math.Clamp((int)MathF.Round(sample * appliedGain), short.MinValue, short.MaxValue);
            var result = (short)boosted;
            normalized[i] = (byte)(result & 0xFF);
            normalized[i + 1] = (byte)((result >> 8) & 0xFF);
        }

        outputRms = AudioChunkDiagnostics.ComputeRms(normalized);
        outputPeak = AudioChunkDiagnostics.ComputePeak(normalized);
        return normalized;
    }

    private void LogLatencyPolicy(string action, TimeSpan duration, SpeechActivityMetrics metrics)
    {
        _logger.LogDebug(
            "[LatencyPolicy] sessionId={SessionId} action={Action} durationMs={DurationMs:F1} rawRms={RawRms:F4} rawPeak={RawPeak:F4} normalizedRms={NormalizedRms:F4} normalizedPeak={NormalizedPeak:F4} gain={Gain:F2}",
            _sessionId, action, duration.TotalMilliseconds, metrics.RawRms, metrics.RawPeak,
            metrics.NormalizedRms, metrics.NormalizedPeak, metrics.AppliedGain);
    }

    private static string? SanitizeLanguage(string? detectedLanguage)
    {
        if (string.IsNullOrWhiteSpace(detectedLanguage)) return null;
        var trimmed = detectedLanguage.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "spanish" or "espanol" or "español" => "es",
            "english" => "en",
            _ when trimmed.Length is >= 2 and <= 10 => trimmed,
            _ => null
        };
    }

    private static bool IsValidTranscription(string? text)
    {
        if (!TranscriptTextFilter.IsMeaningfulText(text)) return false;
        var trimmed = text.Trim();
        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 && trimmed.Count(char.IsLetter) >= 6;
    }

    private void UpdateLanguageLock(
        string? detectedLanguage,
        string? text,
        IReadOnlyList<TranscriptSegment> validSegments,
        float inputRms,
        float inputPeak,
        Guid chunkId)
    {
        if (_lockedLanguage is not null) return;

        var hasReliableSegmentLanguage = validSegments.Any(segment =>
            string.Equals(SanitizeLanguage(segment.Language), detectedLanguage, StringComparison.Ordinal));
        var textValid = IsValidTranscription(text);
        var deadSignal = IsClearlyDeadSignal(inputRms, inputPeak);

        if (detectedLanguage is null || !hasReliableSegmentLanguage || !textValid || deadSignal)
        {
            ResetLanguageCandidate();
            return;
        }

        _languageProbeChunksObserved++;
        if (!string.Equals(_languageCandidate, detectedLanguage, StringComparison.Ordinal))
        {
            _languageCandidate = detectedLanguage;
            _languageCandidateHits = 1;
        }
        else
        {
            _languageCandidateHits++;
        }

        if (_languageCandidateHits >= LanguageLockRequiredHits)
        {
            _lockedLanguage = _languageCandidate;
            ResetLanguageCandidate();
        }
    }

    private void ResetLanguageCandidate()
    {
        _languageCandidate = null;
        _languageCandidateHits = 0;
    }

    private static bool IsClearlyDeadSignal(float rms, float peak)
        => peak < 0.0015f && rms < 0.0008f;

    private bool IsSherpaLowLatencyMode()
        => _runtimeSettings.EnableSherpaLowLatencyMode
           && _transcriptionProvider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase);

    private readonly record struct SpeechActivityMetrics(
        float RawRms,
        float RawPeak,
        float NormalizedRms,
        float NormalizedPeak,
        float AppliedGain,
        bool IsLowActivity);

    private readonly record struct ChunkStageDecision(
        AudioChunk? Chunk,
        string Action,
        TimeSpan StageDelay);

    private (string? Language, string Mode) ResolveRequestLanguage()
    {
        var forcedLanguage = SanitizeLanguage(_runtimeSettings.ForcedLanguage);
        if (_transcriptionProvider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase))
            return forcedLanguage is not null ? (forcedLanguage, "requested-not-enforced") : (null, "unknown");
        if (forcedLanguage is not null) return (forcedLanguage, "forced");
        if (_lockedLanguage is not null) return (_lockedLanguage, "locked");
        return (null, "auto");
    }

    private void LogProviderBottleneck(
        AudioChunk chunk,
        string languageMode,
        double providerMs,
        TimeSpan audioDuration,
        int queueBefore)
    {
        if (providerMs < 5_000) return;
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastProviderBottleneckLogAt).TotalSeconds < 15) return;
        _lastProviderBottleneckLogAt = now;
        _logger.LogWarning(
            "[Pipeline.BottleneckDiagnosis] sessionId={SessionId} chunkId={ChunkId} source={Source} dominant=provider_inference providerMs={ProviderMs:F1} audioDurationMs={AudioDurationMs:F1} queueBefore={QueueBefore} languageMode={LanguageMode} provider={Provider} model={Model}",
            _sessionId, chunk.Id, chunk.Source, providerMs, audioDuration.TotalMilliseconds, queueBefore,
            languageMode, _transcriptionProvider, _transcriptionModelId);
    }

    private void ObserveBacklog(int queueBefore)
    {
        _highQueueStreak = queueBefore >= HighQueueWarningThreshold ? _highQueueStreak + 1 : 0;
        var now = DateTimeOffset.UtcNow;
        if ((queueBefore >= CriticalQueueWarningThreshold || _highQueueStreak >= HighQueueWarningStreak) &&
            (now - _lastBacklogWarningAt).TotalSeconds >= 2)
        {
            _lastBacklogWarningAt = now;
            _logger.LogWarning(
                "[Pipeline.Backlog] sessionId={SessionId} queueDepth={QueueDepth} streak={Streak} provider={Provider} model={Model}",
                _sessionId, queueBefore, _highQueueStreak, _transcriptionProvider, _transcriptionModelId);
        }
    }

    private void LogChunkLatency(
        AudioChunk chunk,
        double queueAgeMs,
        int queueBefore,
        string stageAction,
        double stageDelayMs,
        double wavWriteMs,
        double providerMs,
        double totalMs,
        int emittedSegments,
        string? detectedLanguage,
        string result)
    {
        _logger.LogInformation(
            "[ChunkLatency] sessionId={SessionId} chunkId={ChunkId} source={Source} queueAgeMs={QueueAgeMs:F1} stageAction={StageAction} stageDelayMs={StageDelayMs:F1} wavWriteMs={WavWriteMs:F1} providerMs={ProviderMs:F1} totalMs={TotalMs:F1} queueBefore={QueueBefore} queueAfter={QueueAfter} emittedSegments={Segments} detectedLanguage={DetectedLanguage} result={Result}",
            _sessionId, chunk.Id, chunk.Source, queueAgeMs, stageAction, stageDelayMs, wavWriteMs,
            providerMs, totalMs, queueBefore, (int)_chunkChannel.Reader.Count, emittedSegments,
            detectedLanguage ?? "(none)", result);
    }

    private string GetTranscriptionLanguageModeDisplay()
    {
        var forcedLanguage = SanitizeLanguage(_runtimeSettings.ForcedLanguage);
        if (_transcriptionProvider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase))
            return forcedLanguage is not null ? $"requested/{forcedLanguage} actual/unknown" : "actual/unknown";
        if (forcedLanguage is not null) return $"forced/{forcedLanguage}";
        if (_lockedLanguage is not null) return $"locked/{_lockedLanguage}";
        return _runtimeSettings.EnableAutoLanguageProbe ? "auto/probe-ready" : "auto/disabled";
    }

    private List<TranscriptSegment> StampSegments(IReadOnlyList<TranscriptSegment> raw, AudioChunk chunk)
    {
        var result = new List<TranscriptSegment>(raw.Count);
        foreach (var seg in raw)
        {
            seg.SessionId = _sessionId;
            seg.SpeakerType = SpeakerTypeForSource(chunk.Source);
            seg.SpeakerLabel = string.IsNullOrWhiteSpace(seg.SpeakerLabel)
                ? SpeakerLabelForSource(chunk.Source)
                : seg.SpeakerLabel;

            if (seg.Range.Start.Year < 2000)
            {
                var offset = seg.Range.Start - DateTimeOffset.UnixEpoch;
                seg.Range = new TimeRange(
                    chunk.CapturedAt + offset,
                    chunk.CapturedAt + (seg.Range.End - DateTimeOffset.UnixEpoch));
            }
            result.Add(seg);
        }
        return result;
    }

    private static SpeakerType SpeakerTypeForSource(AudioSource source) => source switch
    {
        AudioSource.Microphone => SpeakerType.LocalUser,
        AudioSource.SystemAudio => SpeakerType.SystemAudio,
        _ => SpeakerType.Unknown
    };

    private static string SpeakerLabelForSource(AudioSource source) => source switch
    {
        AudioSource.Microphone => "Me",
        AudioSource.SystemAudio => "SystemAudio",
        _ => "Unknown"
    };

    private void ResetPipelineDiagnostics()
    {
        lock (_queueMetricsLock) _queueMirror.Clear();
        _micEnqueued = _systemEnqueued = _micProcessed = _systemProcessed = 0;
        _micDropped = _systemDropped = 0;
        _lastMicChunkAt = null;
        _lastSystemChunkAt = null;
        _lastPipelineHealthAt = DateTimeOffset.MinValue;
        _lastDualHealthAt = DateTimeOffset.MinValue;
        _lastStatusPublishedAt = DateTimeOffset.MinValue;
        _drainLoopAlive = false;
    }

    private void IncrementEnqueued(AudioSource source)
    {
        if (source == AudioSource.Microphone) Interlocked.Increment(ref _micEnqueued);
        else if (source == AudioSource.SystemAudio) Interlocked.Increment(ref _systemEnqueued);
    }

    private void IncrementProcessed(AudioSource source)
    {
        if (source == AudioSource.Microphone) Interlocked.Increment(ref _micProcessed);
        else if (source == AudioSource.SystemAudio) Interlocked.Increment(ref _systemProcessed);
    }

    private void IncrementDropped(AudioSource source)
    {
        if (source == AudioSource.Microphone) Interlocked.Increment(ref _micDropped);
        else if (source == AudioSource.SystemAudio) Interlocked.Increment(ref _systemDropped);
    }

    private void LogPipelineHealth(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastPipelineHealthAt < PipelineHealthInterval) return;
        _lastPipelineHealthAt = now;

        double oldestQueueAgeMs = 0;
        lock (_queueMetricsLock)
        {
            if (_queueMirror.Count > 0)
                oldestQueueAgeMs = Math.Max(0, (now - _queueMirror.Peek().CapturedAt).TotalMilliseconds);
        }

        _logger.LogInformation(
            "[Pipeline.Health] sessionId={SessionId} queueDepth={QueueDepth} micEnqueued={MicEnqueued} systemEnqueued={SystemEnqueued} micProcessed={MicProcessed} systemProcessed={SystemProcessed} micDropped={MicDropped} systemDropped={SystemDropped} oldestQueueAgeMs={OldestQueueAgeMs:F1} drainLoopAlive={DrainLoopAlive}",
            _sessionId, (int)_chunkChannel.Reader.Count,
            Interlocked.Read(ref _micEnqueued), Interlocked.Read(ref _systemEnqueued),
            Interlocked.Read(ref _micProcessed), Interlocked.Read(ref _systemProcessed),
            Interlocked.Read(ref _micDropped), Interlocked.Read(ref _systemDropped),
            oldestQueueAgeMs, _drainLoopAlive);
    }

    private void LogDualCaptureHealth(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastDualHealthAt < DualHealthInterval) return;
        _lastDualHealthAt = now;

        var micChunkAgeMs = _lastMicChunkAt.HasValue ? (now - _lastMicChunkAt.Value).TotalMilliseconds : -1;
        var systemCallbackAgeMs = _sysAudio?.LastCallbackAt is { } sysAt ? (now - sysAt).TotalMilliseconds : -1;
        var systemChunkAgeMs = _lastSystemChunkAt.HasValue ? (now - _lastSystemChunkAt.Value).TotalMilliseconds : -1;
        var micExpected = _mic is not null;
        var systemExpected = _sysAudio is not null && !SkipSystemAudioCapture;
        var micAlive = !micExpected || (_mic!.Status == AudioCaptureStatus.Capturing && (micChunkAgeMs < 5000 || micChunkAgeMs < 0));
        var systemAlive = !systemExpected || (_sysAudio!.Status == AudioCaptureStatus.Capturing && systemCallbackAgeMs >= 0 && systemCallbackAgeMs < 5000);
        var bothAlive = micAlive && systemAlive;

        _logger.LogInformation(
            "[DualCapture.Health] sessionId={SessionId} micExpected={MicExpected} micStatus={MicStatus} micBackend={MicBackend} micLastChunkAgeMs={MicChunkAgeMs:F1} micRms={MicRms:F6} systemExpected={SystemExpected} systemStatus={SystemStatus} systemLastCallbackAgeMs={SystemCallbackAgeMs:F1} systemLastChunkAgeMs={SystemChunkAgeMs:F1} systemRms={SystemRms:F6} bothAlive={BothAlive}",
            _sessionId, micExpected, _mic?.Status ?? AudioCaptureStatus.NoDevice,
            _mic?.ActiveBackend.ToString() ?? "none", micChunkAgeMs, _mic?.ConvertedRms ?? 0f,
            systemExpected, _sysAudio?.Status ?? AudioCaptureStatus.NoDevice,
            systemCallbackAgeMs, systemChunkAgeMs, _sysAudio?.ConvertedRms ?? 0f, bothAlive);

        if (!bothAlive)
        {
            var missing = !micAlive ? "Microphone" : !systemAlive ? "SystemAudio" : "unknown";
            _logger.LogWarning(
                "[DualCapture.Warning] sessionId={SessionId} missingSource={MissingSource} micLastChunkAgeMs={MicChunkAgeMs:F1} systemLastCallbackAgeMs={SystemCallbackAgeMs:F1} otherSourceHealthy={OtherHealthy}",
                _sessionId, missing, micChunkAgeMs, systemCallbackAgeMs,
                missing == "Microphone" ? systemAlive : micAlive);
        }
    }

    private void PublishStatus(
        string? micError = null,
        string? transcriptionError = null,
        TranscriptionPipelineStatus transcriptionStatus = TranscriptionPipelineStatus.Idle,
        bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastStatusPublishedAt < StatusPublishInterval)
            return;
        _lastStatusPublishedAt = now;

        var whisperState = _whisperModelService?.DownloadState
            ?? (_transcriptionProvider.Equals("WhisperNet", StringComparison.OrdinalIgnoreCase)
                ? WhisperModelDownloadState.NotChecked
                : WhisperModelDownloadState.NotApplicable);

        Status = new AudioStatusSnapshot
        {
            MicrophoneStatus = _mic?.Status ?? AudioCaptureStatus.NoDevice,
            MicrophoneDevice = _mic?.DisplayName ?? string.Empty,
            MicrophoneError = micError,
            ActiveMicBackend = _mic?.ActiveBackend ?? MicBackend.WaveIn,
            MicNativeRms = _mic?.NativeRms ?? 0f,
            MicConvertedRms = _mic?.ConvertedRms ?? 0f,
            SystemAudioStatus = _sysAudio?.Status ?? AudioCaptureStatus.NoDevice,
            SystemAudioDevice = _sysAudio?.DisplayName ?? string.Empty,
            SystemAudioNativeRms = _sysAudio?.NativeRms ?? 0f,
            SystemAudioConvertedRms = _sysAudio?.ConvertedRms ?? 0f,
            TranscriptionStatus = transcriptionStatus,
            TranscriptionError = transcriptionError ?? _lastTranscriptionError,
            PendingChunks = (int)_chunkChannel.Reader.Count,
            TotalSegments = _segmentCount,
            TranscriptionConfigured = _transcriptionModel is not null,
            TranscriptionProvider = _transcriptionProvider,
            TranscriptionModel = _transcriptionModelId,
            TranscriptionLanguageMode = GetTranscriptionLanguageModeDisplay(),
            LastTranscriptionAt = _lastTranscriptionAt,
            WhisperDownloadState = whisperState,
            WhisperModelPath = _whisperModelService?.ModelPath ?? string.Empty
        };

        StatusChanged?.Invoke(this, Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _disposing) return;
            _disposing = true;
        }
        finally
        {
            _disposeGate.Release();
        }

        try
        {
            if (!_stopped) await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Pipeline.DisposeAsync] stop_failed_nonfatal=true");
        }
        finally
        {
            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _mic = null;
                _sysAudio = null;
                _disposed = true;
                _disposing = false;
            }
            finally
            {
                _disposeGate.Release();
            }
        }
    }
}
