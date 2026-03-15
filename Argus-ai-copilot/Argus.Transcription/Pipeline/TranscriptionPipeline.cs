using System.Threading.Channels;
using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Audio.Capture;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Argus.Transcription.Whisper;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Argus.Transcription.Pipeline;

/// <summary>
/// Wires microphone and optional system-audio capture sources to the transcription provider.
///
/// Design:
///   - Both sources share one bounded channel; chunks from either source are processed
///     sequentially by a single drain loop to avoid provider concurrency issues.
///   - Call <see cref="SetSources"/> before <see cref="StartAsync"/> to inject the
///     pre-configured capture sources (with devices already assigned).
///   - The transcription model is resolved once at <see cref="StartAsync"/> time using the
///     <see cref="AiWorkflow.SpeechTranscription"/> workflow (independent of chat routing).
///     If resolution fails, <see cref="AudioStatusSnapshot.TranscriptionConfigured"/> is false
///     and the UI shows a clear warning. Audio capture still runs so no audio is lost.
///   - Results are raised via <see cref="SegmentsProduced"/>; the coordinator persists them.
/// </summary>
public sealed class TranscriptionPipeline : ITranscriptionPipeline, IAsyncDisposable
{
    private readonly IModelResolver _modelResolver;
    private readonly ILogger<TranscriptionPipeline> _logger;
    private readonly WhisperModelService? _whisperModelService;

    // Capture sources â€” injected by the coordinator after device discovery.
    private MicrophoneCaptureSource?  _mic;
    private SystemAudioCaptureSource? _sysAudio;

    // Resolved once at StartAsync â€” null means no provider is available.
    private ITranscriptionModel? _transcriptionModel;
    private string _transcriptionProvider = string.Empty;
    private string _transcriptionModelId  = string.Empty;

    // Bounded channel: at most 20 queued chunks (~100 s of audio at 5 s chunks).
    private readonly Channel<AudioChunk> _chunkChannel =
        Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(20)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private CancellationTokenSource? _drainCts;
    private Task?                    _drainTask;
    private Guid                     _sessionId;
    private int                      _segmentCount;
    private DateTimeOffset?          _lastTranscriptionAt;
    private string?                  _lastTranscriptionError;

    // â”€â”€ ITranscriptionPipeline â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public AudioStatusSnapshot Status { get; private set; } = AudioStatusSnapshot.Idle;
    public event EventHandler<AudioStatusSnapshot>? StatusChanged;
    public event EventHandler<IReadOnlyList<TranscriptSegment>>? SegmentsProduced;

    public TranscriptionPipeline(
        IModelResolver modelResolver,
        ILogger<TranscriptionPipeline> logger,
        WhisperModelService? whisperModelService = null)
    {
        _modelResolver       = modelResolver;
        _logger              = logger;
        _whisperModelService = whisperModelService;
    }

    /// <summary>
    /// Injects the pre-configured capture sources. Must be called before <see cref="StartAsync"/>.
    /// <paramref name="sysAudio"/> may be null if system audio capture is not desired.
    /// </summary>
    public void SetSources(MicrophoneCaptureSource mic, SystemAudioCaptureSource? sysAudio = null)
    {
        _mic      = mic;
        _sysAudio = sysAudio;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_mic is null)
            throw new InvalidOperationException("SetSources must be called before StartAsync.");

        _sessionId             = sessionId;
        _segmentCount          = 0;
        _lastTranscriptionAt   = null;
        _lastTranscriptionError = null;

        // â”€â”€ Probe transcription provider once at start time â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // This is the critical check. We resolve now so:
        //  1. The UI can immediately show whether transcription is configured.
        //  2. We log once at Error level if it is not, rather than warning per chunk.
        //  3. ProcessChunkAsync uses the cached instance â€” no per-chunk resolution.
        ResolveTranscriptionProvider();

        // â”€â”€ Start drain loop â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        _drainCts  = new CancellationTokenSource();
        _drainTask = Task.Run(() => DrainChannelAsync(_drainCts.Token));

        _logger.LogInformation(
            "[Pipeline.StartAsync] Drain loop started. SessionId={Id}", sessionId);

        // â”€â”€ Start microphone â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        _logger.LogInformation(
            "[Pipeline.StartAsync] Starting microphone capture: '{Device}'", _mic.DisplayName);
        _mic.ChunkReady += OnChunkReady;
        try
        {
            await _mic.StartAsync(sessionId, CancellationToken.None);
            _logger.LogInformation(
                "[Pipeline.StartAsync] Microphone capture started. Status={Status}", _mic.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline.StartAsync] Microphone capture failed to start.");
            PublishStatus(micError: ex.Message);
        }

        // â”€â”€ Start system audio (best-effort) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (_sysAudio is not null)
        {
            _logger.LogInformation(
                "[Pipeline.StartAsync] Starting system audio capture: '{Device}'", _sysAudio.DisplayName);
            _sysAudio.ChunkReady += OnChunkReady;
            try
            {
                await _sysAudio.StartAsync(sessionId, CancellationToken.None);
                _logger.LogInformation(
                    "[Pipeline.StartAsync] System audio capture started. Status={Status}", _sysAudio.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "[Pipeline.StartAsync] System audio capture failed â€” continuing without it.");
                _sysAudio.ChunkReady -= OnChunkReady;
                _sysAudio = null;
            }
        }

        PublishStatus();
        _logger.LogInformation(
            "[Pipeline.StartAsync] Pipeline fully started. Mic={MicDevice} SysAudio={SysDevice} " +
            "Transcription={TxProvider}/{TxModel} Configured={Configured} SessionId={Id}",
            _mic.DisplayName,
            _sysAudio?.DisplayName ?? "none",
            _transcriptionProvider.Length > 0 ? _transcriptionProvider : "none",
            _transcriptionModelId.Length > 0 ? _transcriptionModelId : "none",
            _transcriptionModel is not null,
            sessionId);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[Pipeline.StopAsync] Beginning stop. SessionId={Id}", _sessionId);

        if (_mic is not null)
        {
            _logger.LogInformation("[Pipeline.StopAsync] Stopping microphone capture.");
            _mic.ChunkReady -= OnChunkReady;
            await _mic.StopAsync(ct);
            _logger.LogInformation("[Pipeline.StopAsync] Microphone capture stopped.");
        }

        if (_sysAudio is not null)
        {
            _logger.LogInformation("[Pipeline.StopAsync] Stopping system audio capture.");
            _sysAudio.ChunkReady -= OnChunkReady;
            await _sysAudio.StopAsync(ct);
            _logger.LogInformation("[Pipeline.StopAsync] System audio capture stopped.");
        }

        // Signal drain loop to finish after processing remaining queued chunks.
        _logger.LogInformation("[Pipeline.StopAsync] Completing channel writer.");
        _chunkChannel.Writer.TryComplete();

        if (_drainTask is not null)
        {
            _logger.LogInformation("[Pipeline.StopAsync] Waiting for drain loop to finish (max 30s).");
            try   { await _drainTask.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (TimeoutException) { _logger.LogWarning("[Pipeline.StopAsync] Drain loop did not finish within 30s â€” forcing cancel."); }
            catch (OperationCanceledException) { _logger.LogDebug("[Pipeline.StopAsync] Drain wait cancelled on shutdown."); }
        }

        _logger.LogInformation("[Pipeline.StopAsync] Cancelling drain CTS.");
        _drainCts?.Cancel();
        _drainCts?.Dispose();
        _drainCts = null;

        _transcriptionModel = null;

        PublishStatus();
        _logger.LogInformation("[Pipeline.StopAsync] Stop complete. SessionId={Id} TotalSegments={Count}",
            _sessionId, _segmentCount);
    }

    public void Pause()
    {
        _mic?.Pause();
        _sysAudio?.Pause();
        PublishStatus();
    }

    public void Resume()
    {
        _mic?.Resume();
        _sysAudio?.Resume();
        PublishStatus();
    }

    // â”€â”€ Provider resolution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ResolveTranscriptionProvider()
    {
        _transcriptionModel    = null;
        _transcriptionProvider = string.Empty;
        _transcriptionModelId  = string.Empty;

        try
        {
            // Use the dedicated SpeechTranscription workflow â€” independent of chat/reasoning routing.
            var model = _modelResolver.ResolveTranscriptionModel(AiWorkflow.SpeechTranscription);
            _transcriptionModel    = model;
            _transcriptionProvider = model.ProviderId;
            _transcriptionModelId  = model.ModelId;

            _logger.LogInformation(
                "[Pipeline.Provider] Transcription provider resolved. Provider={Provider} ModelId={ModelId}",
                model.ProviderId, model.ModelId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(
                "[Pipeline.Provider] TRANSCRIPTION IS NOT CONFIGURED. " +
                "Audio will be captured but no text will be produced. " +
                "Fix: set ArgusAI:Defaults:Transcription or add a SpeechTranscription WorkflowMapping " +
                "pointing at an enabled profile with Provider=OpenAI or Provider=Whisper. " +
                "Detail: {Message}",
                ex.Message);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(
                "[Pipeline.Provider] Provider type is not supported for transcription. " +
                "Supported providers: OpenAI, Whisper, LocalAI, OpenAI_Compatible. " +
                "Detail: {Message}",
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Pipeline.Provider] Unexpected error resolving transcription provider.");
        }
    }

    // â”€â”€ Capture callback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnChunkReady(object? sender, AudioChunk chunk)
    {
        var written = _chunkChannel.Writer.TryWrite(chunk);
        var pending = (int)_chunkChannel.Reader.Count;

        if (written)
        {
            _logger.LogDebug(
                "[Pipeline.ChunkQueued] Source={Source} ChunkId={Id} Duration={Dur:F1}s Pending={Pending}",
                chunk.Source, chunk.Id, chunk.Duration.TotalSeconds, pending);
        }
        else
        {
            _logger.LogWarning(
                "[Pipeline.ChunkDropped] Channel full â€” oldest chunk dropped. Source={Source} Pending={Pending}",
                chunk.Source, pending);
        }

        PublishStatus();
    }

    // â”€â”€ Drain loop â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task DrainChannelAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Pipeline.DrainLoop] Drain loop running. SessionId={Id}", _sessionId);

        try
        {
            await foreach (var chunk in _chunkChannel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogDebug("[Pipeline.DrainLoop] Cancellation requested â€” exiting drain loop.");
                    break;
                }

                var pending = (int)_chunkChannel.Reader.Count;
                _logger.LogDebug(
                    "[Pipeline.ChunkDequeued] Source={Source} ChunkId={Id} Duration={Dur:F1}s RemainingInQueue={Remaining}",
                    chunk.Source, chunk.Id, chunk.Duration.TotalSeconds, pending);

                await ProcessChunkAsync(chunk, ct);
                PublishStatus();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[Pipeline.DrainLoop] Drain loop cancelled. SessionId={Id}", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline.DrainLoop] Unexpected exception in drain loop. SessionId={Id}", _sessionId);
        }

        _logger.LogInformation("[Pipeline.DrainLoop] Drain loop finished. SessionId={Id}", _sessionId);
    }

    private async Task ProcessChunkAsync(AudioChunk chunk, CancellationToken ct)
    {
        // If provider was not resolved at start, set NoProvider status clearly and return.
        if (_transcriptionModel is null)
        {
            _logger.LogDebug(
                "[Pipeline.Chunk] Dropping chunk {Id} â€” no transcription provider configured.",
                chunk.Id);
            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.NoProvider);
            return;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"argus_{chunk.Id:N}.wav");

        try
        {
            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.Transcribing);
            WriteWav(tempFile, chunk.Data);

            _logger.LogInformation(
                "[Pipeline.TxRequest] Sending chunk to provider. ChunkId={Id} Source={Source} " +
                "Duration={Dur:F1}s Provider={Provider} ModelId={ModelId} TempFile={File}",
                chunk.Id, chunk.Source, chunk.Duration.TotalSeconds,
                _transcriptionProvider, _transcriptionModelId, tempFile);

            var request = new TranscriptionRequest
            {
                AudioFilePath  = tempFile,
                Language       = null,
                WordTimestamps = false
            };

            var response = await _transcriptionModel.TranscribeAsync(request, ct);

            if (response.IsError)
            {
                _lastTranscriptionError = response.ErrorMessage;
                _logger.LogError(
                    "[Pipeline.TxError] Transcription provider returned error. " +
                    "ChunkId={Id} Provider={Provider} ModelId={ModelId} Error={Error}",
                    chunk.Id, _transcriptionProvider, _transcriptionModelId, response.ErrorMessage);
                PublishStatus(
                    transcriptionStatus: TranscriptionPipelineStatus.Error,
                    transcriptionError: response.ErrorMessage);
                return;
            }

            _lastTranscriptionError = null;
            _lastTranscriptionAt    = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "[Pipeline.TxResponse] Transcription complete. ChunkId={Id} Segments={SegCount} " +
                "FullTextLength={Len} Language={Lang} Preview={Preview}",
                chunk.Id,
                response.Segments.Count,
                response.FullText.Length,
                response.DetectedLanguage ?? "auto",
                response.FullText.Length > 100
                    ? response.FullText[..100] + "â€¦"
                    : response.FullText);

            if (response.Segments.Count == 0 && response.FullText.Length > 0)
            {
                // Provider returned text but no segments â€” synthesise one segment.
                _logger.LogDebug(
                    "[Pipeline.TxResponse] Provider returned text with no segment timestamps â€” synthesising segment.");
                var synthetic = new TranscriptSegment
                {
                    SessionId   = _sessionId,
                    Text        = response.FullText.Trim(),
                    SpeakerType = SpeakerType.Unknown,
                    Language    = response.DetectedLanguage,
                    Range       = new TimeRange(chunk.CapturedAt, chunk.CapturedAt + chunk.Duration),
                    Confidence  = ConfidenceScore.None
                };
                var list = (IReadOnlyList<TranscriptSegment>)[synthetic];
                _segmentCount++;
                _logger.LogInformation(
                    "[Pipeline.Segments] Emitting 1 synthesised segment. SessionId={Id} Text='{Text}'",
                    _sessionId, synthetic.Text.Length > 80 ? synthetic.Text[..80] + "â€¦" : synthetic.Text);
                SegmentsProduced?.Invoke(this, list);
            }
            else if (response.Segments.Count > 0)
            {
                var anchored = StampSegments(response.Segments, chunk);
                _segmentCount += anchored.Count;

                _logger.LogInformation(
                    "[Pipeline.Segments] Emitting {Count} segment(s). SessionId={Id} Preview='{Preview}'",
                    anchored.Count,
                    _sessionId,
                    response.FullText.Length > 80 ? response.FullText[..80] + "â€¦" : response.FullText);

                SegmentsProduced?.Invoke(this, anchored);
            }
            else
            {
                _logger.LogDebug(
                    "[Pipeline.TxResponse] Provider returned empty transcription for chunk {Id} â€” " +
                    "likely silence or below detection threshold.", chunk.Id);
            }

            PublishStatus(transcriptionStatus: TranscriptionPipelineStatus.Idle);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[Pipeline.Chunk] ProcessChunk cancelled for {Id}.", chunk.Id);
        }
        catch (Exception ex)
        {
            _lastTranscriptionError = ex.Message;
            _logger.LogError(ex,
                "[Pipeline.TxException] Unexpected exception processing chunk {Id}. " +
                "Provider={Provider} ModelId={ModelId}",
                chunk.Id, _transcriptionProvider, _transcriptionModelId);
            PublishStatus(
                transcriptionStatus: TranscriptionPipelineStatus.Error,
                transcriptionError: ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { /* best-effort temp file cleanup */ }
        }
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static void WriteWav(string path, byte[] pcm16kMono16bit)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1));
        writer.Write(pcm16kMono16bit, 0, pcm16kMono16bit.Length);
    }

    private List<TranscriptSegment> StampSegments(IReadOnlyList<TranscriptSegment> raw, AudioChunk chunk)
    {
        var result = new List<TranscriptSegment>(raw.Count);
        foreach (var seg in raw)
        {
            seg.SessionId = _sessionId;
            if (seg.Range.Start.Year < 2000)
            {
                var offset = seg.Range.Start - DateTimeOffset.UnixEpoch;
                seg.Range  = new TimeRange(
                    chunk.CapturedAt + offset,
                    chunk.CapturedAt + (seg.Range.End - DateTimeOffset.UnixEpoch));
            }
            result.Add(seg);
        }
        return result;
    }

    private void PublishStatus(
        string?                     micError            = null,
        string?                     transcriptionError  = null,
        TranscriptionPipelineStatus transcriptionStatus = TranscriptionPipelineStatus.Idle)
    {
        var pending = (int)_chunkChannel.Reader.Count;

        var whisperState = _whisperModelService?.DownloadState
            ?? (_transcriptionProvider.Equals("WhisperNet", StringComparison.OrdinalIgnoreCase)
                ? WhisperModelDownloadState.NotChecked
                : WhisperModelDownloadState.NotApplicable);

        Status = new AudioStatusSnapshot
        {
            MicrophoneStatus        = _mic?.Status          ?? AudioCaptureStatus.NoDevice,
            MicrophoneDevice        = _mic?.DisplayName      ?? string.Empty,
            MicrophoneError         = micError,
            SystemAudioStatus       = _sysAudio?.Status      ?? AudioCaptureStatus.NoDevice,
            SystemAudioDevice       = _sysAudio?.DisplayName ?? string.Empty,
            TranscriptionStatus     = transcriptionStatus,
            TranscriptionError      = transcriptionError ?? _lastTranscriptionError,
            PendingChunks           = pending,
            TotalSegments           = _segmentCount,
            TranscriptionConfigured = _transcriptionModel is not null,
            TranscriptionProvider   = _transcriptionProvider,
            TranscriptionModel      = _transcriptionModelId,
            LastTranscriptionAt     = _lastTranscriptionAt,
            WhisperDownloadState    = whisperState,
            WhisperModelPath        = _whisperModelService?.ModelPath ?? string.Empty
        };

        StatusChanged?.Invoke(this, Status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _mic?.Dispose();
        _sysAudio?.Dispose();
    }
}
