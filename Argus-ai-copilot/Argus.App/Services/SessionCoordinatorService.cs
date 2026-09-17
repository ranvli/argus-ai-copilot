using Argus.Audio.Capture;
using Argus.Audio.Devices;
using Argus.Context.WindowContext;
using Argus.Core.Contracts.Repositories;
using Argus.Core.Contracts.Services;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Infrastructure.Storage;
using Argus.Transcription.Configuration;
using Argus.Transcription.Intent;
using Argus.Transcription.Pipeline;
using Argus.Transcription.SherpaOnnx;
using Argus.Transcription.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App.Services;

internal sealed class SessionCoordinatorService
    : BackgroundService, ISessionCoordinator, ISessionStatePublisher, IAudioStatusPublisher
{
    private readonly ILogger<SessionCoordinatorService> _logger;
    private readonly IAppStateService _appState;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IArtifactStorage _artifactStorage;
    private readonly IActiveWindowTracker _windowTracker;
    private readonly IAudioDeviceDiscovery _deviceDiscovery;
    private readonly TranscriptBuffer _transcriptBuffer;
    private readonly IntentDetectionService _intentDetector;
    private readonly AssistantReactionService _assistantReaction;
    private readonly MicAudioSettings _micSettings;
    private readonly ISherpaOnnxProvisioningService _sherpaProvisioning;
    private readonly ISherpaOnnxPreflightService _sherpaPreflight;
    private readonly TranscriptionRuntimeSettings _runtimeSettings;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionLifecycleState _state = SessionLifecycleState.Idle;
    private Session? _activeSession;
    private int _sessionEventCount;
    private string _activeProcessName = string.Empty;
    private string _activeWindowTitle = string.Empty;
    private int _activeProcessId;

    private ITranscriptionPipeline? _pipeline;
    private AsyncServiceScope? _pipelineScope;
    private CancellationTokenSource? _pipelineStoppingCts;
    private int _transcriptSegmentCount;

    public SessionLifecycleState State => _state;
    public Session? ActiveSession => _activeSession;
    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

    private SessionStateSnapshot _snapshot = SessionStateSnapshot.Idle;
    public SessionStateSnapshot Snapshot => _snapshot;
    public event EventHandler<SessionStateSnapshot>? SnapshotChanged;

    private AudioStatusSnapshot _audioStatus = AudioStatusSnapshot.Idle;
    public AudioStatusSnapshot AudioStatus => _audioStatus;
    public event EventHandler<AudioStatusSnapshot>? AudioStatusChanged;

    public SessionCoordinatorService(
        ILogger<SessionCoordinatorService> logger,
        IAppStateService appState,
        IServiceScopeFactory scopeFactory,
        IArtifactStorage artifactStorage,
        IActiveWindowTracker windowTracker,
        IAudioDeviceDiscovery deviceDiscovery,
        TranscriptBuffer transcriptBuffer,
        IntentDetectionService intentDetector,
        AssistantReactionService assistantReaction,
        MicAudioSettings micSettings,
        ISherpaOnnxProvisioningService sherpaProvisioning,
        ISherpaOnnxPreflightService sherpaPreflight,
        Microsoft.Extensions.Options.IOptions<TranscriptionRuntimeSettings> runtimeSettings)
    {
        _logger = logger;
        _appState = appState;
        _scopeFactory = scopeFactory;
        _artifactStorage = artifactStorage;
        _windowTracker = windowTracker;
        _deviceDiscovery = deviceDiscovery;
        _transcriptBuffer = transcriptBuffer;
        _intentDetector = intentDetector;
        _assistantReaction = assistantReaction;
        _micSettings = micSettings;
        _sherpaProvisioning = sherpaProvisioning;
        _sherpaPreflight = sherpaPreflight;
        _runtimeSettings = runtimeSettings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} started.", nameof(SessionCoordinatorService));
        _appState.ModeChanged += OnAppModeChanged;
        _windowTracker.ActiveWindowChanged += OnActiveWindowChanged;

        if (_windowTracker.Current is { } initial)
        {
            _activeProcessName = initial.ProcessName;
            _activeWindowTitle = initial.WindowTitle;
            _activeProcessId = initial.ProcessId;
            PublishSnapshot();
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        finally
        {
            _appState.ModeChanged -= OnAppModeChanged;
            _windowTracker.ActiveWindowChanged -= OnActiveWindowChanged;
            if (_state is SessionLifecycleState.Listening or SessionLifecycleState.Paused)
                await StopSessionAsync(CancellationToken.None);
        }
    }

    public async Task<Session> StartSessionAsync(
        string title,
        SessionType type = SessionType.FreeForm,
        ListeningMode mode = ListeningMode.MicrophoneAndSystem,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not SessionLifecycleState.Idle)
                throw new InvalidOperationException($"Cannot start a session while in state '{_state}'.");

            if (!_sherpaProvisioning.IsReady || !_sherpaPreflight.IsSafeToUse)
            {
                var reason = !_sherpaProvisioning.IsReady
                    ? _sherpaProvisioning.LastError ?? $"Provisioning Sherpa model... State={_sherpaProvisioning.State}. Root={_sherpaProvisioning.ModelRoot}"
                    : _sherpaPreflight.LastError ?? "Sherpa model loaded but failed native preflight.";

                _audioStatus = new AudioStatusSnapshot
                {
                    TranscriptionStatus = TranscriptionPipelineStatus.Error,
                    TranscriptionConfigured = true,
                    TranscriptionProvider = "SherpaOnnx",
                    TranscriptionModel = SherpaOnnxModelService.DefaultModelId,
                    TranscriptionError = reason,
                    TranscriptionLanguageMode = "requested/es actual/unknown",
                    SherpaProvisioningState = _sherpaProvisioning.State,
                    SherpaModelRoot = _sherpaProvisioning.ModelRoot,
                    SherpaNativeReadinessState = _sherpaPreflight.State
                };
                AudioStatusChanged?.Invoke(this, _audioStatus);
                throw new InvalidOperationException(reason);
            }

            var session = new Session
            {
                Title = string.IsNullOrWhiteSpace(title)
                    ? $"Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"
                    : title,
                Type = type,
                ListeningMode = mode,
                StartedAt = DateTimeOffset.UtcNow,
                LifecycleState = SessionLifecycleState.Listening,
                ApplicationContext = _activeProcessName
            };

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();
            await sessions.AddAsync(session, ct);
            await events.AddAsync(new AppEvent
            {
                Type = AppEventType.SessionStarted,
                SessionId = session.Id,
                Details = $"Title='{session.Title}' Type={session.Type} Mode={session.ListeningMode} App={_activeProcessName}"
            }, ct);

            _artifactStorage.EnsureSessionFolders(session.Id);
            _activeSession = session;
            _sessionEventCount = 1;
            _transcriptSegmentCount = 0;
            _transcriptBuffer.Clear();

            _logger.LogInformation(
                "[Audio.Session] sessionId={SessionId} mode={Mode} startedAtUtc={Utc:O}",
                session.Id, session.ListeningMode, session.StartedAt);

            await TransitionAsync(SessionLifecycleState.Listening, session, ct);
            _appState.StartListening();
            await StartPipelineAsync(session.Id, session.ListeningMode);

            return session;
        }
        finally { _gate.Release(); }
    }

    public async Task PauseSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not SessionLifecycleState.Listening) return;
            _pipeline?.Pause();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();
            await events.AddAsync(new AppEvent
            {
                Type = AppEventType.SessionPaused,
                SessionId = _activeSession?.Id,
                Details = $"PausedAt={DateTimeOffset.UtcNow:O}"
            }, ct);

            if (_activeSession is not null)
            {
                _activeSession.LifecycleState = SessionLifecycleState.Paused;
                _activeSession.UpdatedAt = DateTimeOffset.UtcNow;
                await sessions.UpdateAsync(_activeSession, ct);
            }

            _sessionEventCount++;
            await TransitionAsync(SessionLifecycleState.Paused, _activeSession, ct);
            _appState.PauseListening();
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not SessionLifecycleState.Paused) return;
            _pipeline?.Resume();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();
            await events.AddAsync(new AppEvent
            {
                Type = AppEventType.SessionResumed,
                SessionId = _activeSession?.Id,
                Details = $"ResumedAt={DateTimeOffset.UtcNow:O}"
            }, ct);

            if (_activeSession is not null)
            {
                _activeSession.LifecycleState = SessionLifecycleState.Listening;
                _activeSession.UpdatedAt = DateTimeOffset.UtcNow;
                await sessions.UpdateAsync(_activeSession, ct);
            }

            _sessionEventCount++;
            await TransitionAsync(SessionLifecycleState.Listening, _activeSession, ct);
            _appState.StartListening();
        }
        finally { _gate.Release(); }
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is SessionLifecycleState.Idle or SessionLifecycleState.Completed) return;
            var session = _activeSession;
            await TransitionAsync(SessionLifecycleState.Stopping, session, ct);
            _appState.StopListening();
            await StopPipelineAsync(ct);

            if (session is not null)
            {
                session.EndedAt = DateTimeOffset.UtcNow;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                session.LifecycleState = SessionLifecycleState.Completed;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
                var events = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();
                await sessions.UpdateAsync(session, ct);
                await events.AddAsync(new AppEvent
                {
                    Type = AppEventType.SessionEnded,
                    SessionId = session.Id,
                    Details = $"Duration={session.Duration?.Duration.TotalSeconds:F1}s Events={_sessionEventCount} Segments={_transcriptSegmentCount}"
                }, ct);

                _logger.LogInformation(
                    "[Audio.Session.Stop] sessionId={SessionId} durationSec={Duration:F1} segments={Segments}",
                    session.Id, session.Duration?.Duration.TotalSeconds ?? 0d, _transcriptSegmentCount);
            }

            _activeSession = null;
            _sessionEventCount = 0;
            _transcriptSegmentCount = 0;
            _audioStatus = AudioStatusSnapshot.Idle;
            AudioStatusChanged?.Invoke(this, _audioStatus);
            await TransitionAsync(SessionLifecycleState.Completed, session, ct);
            await TransitionAsync(SessionLifecycleState.Idle, null, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task IngestTranscriptSegmentAsync(TranscriptSegment segment, CancellationToken ct = default)
    {
        if (_activeSession is null || _state is not SessionLifecycleState.Listening) return;
        segment.SessionId = _activeSession.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITranscriptRepository>();
        await repo.AddAsync(segment, ct);
    }

    public async Task IngestScreenshotMetadataAsync(ScreenshotArtifact artifact, CancellationToken ct = default)
    {
        if (_activeSession is null || _state is not SessionLifecycleState.Listening) return;
        artifact.SessionId = _activeSession.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScreenshotRepository>();
        await repo.AddAsync(artifact, ct);
    }

    public async Task RecordAppEventAsync(AppEvent appEvent, CancellationToken ct = default)
    {
        appEvent.SessionId ??= _activeSession?.Id;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();
        await repo.AddAsync(appEvent, ct);
        _sessionEventCount++;
    }

    private async Task StartPipelineAsync(Guid sessionId, ListeningMode mode)
    {
        var wantsMic = mode is ListeningMode.Microphone or ListeningMode.MicrophoneAndSystem;
        var wantsSystem = mode is ListeningMode.SystemAudio or ListeningMode.MicrophoneAndSystem;

        _logger.LogInformation(
            "[Pipeline.CapturePlan] sessionId={SessionId} mode={Mode} microphone={Microphone} systemAudio={SystemAudio}",
            sessionId, mode, wantsMic, wantsSystem);

        AudioDeviceInfo? discoveredMicDevice = null;
        AudioDeviceInfo? discoveredOutputDevice = null;

        try
        {
            if (_deviceDiscovery is WindowsAudioDeviceDiscovery winDisc)
                winDisc.LogAllEndpoints();

            discoveredMicDevice = wantsMic ? _deviceDiscovery.GetDefaultInputDevice() : null;
            discoveredOutputDevice = wantsSystem ? _deviceDiscovery.GetDefaultOutputDevice() : null;

            if (wantsMic && discoveredMicDevice is null)
                _logger.LogWarning("[Pipeline.CapturePlan] sessionId={SessionId} source=Microphone requested=true available=false", sessionId);
            if (wantsSystem && discoveredOutputDevice is null)
                _logger.LogWarning("[Pipeline.CapturePlan] sessionId={SessionId} source=SystemAudio requested=true available=false", sessionId);

            if ((wantsMic && discoveredMicDevice is null) && (wantsSystem && discoveredOutputDevice is null))
                throw new InvalidOperationException("No requested audio capture devices are available.");

            _pipelineScope = _scopeFactory.CreateAsyncScope();
            var sp = _pipelineScope.Value.ServiceProvider;
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();

            MicrophoneCaptureSource? micSource = null;
            if (wantsMic && discoveredMicDevice is not null)
            {
                micSource = sp.GetRequiredService<MicrophoneCaptureSource>();
                using var mmMic = enumerator.GetDevice(discoveredMicDevice.Id);
                micSource.SetDevice(mmMic);
                micSource.SelectedBackend = _micSettings.Backend;
                micSource.WaveInDeviceNumber = _micSettings.WaveInDeviceNumber;
                micSource.WasapiContCapture = _micSettings.Backend is MicBackend.Wasapi or MicBackend.Auto;
                micSource.SetChunkDuration(TimeSpan.FromMilliseconds(_runtimeSettings.SherpaChunkDurationMs));

                _logger.LogInformation(
                    "[Mic.StartPlan] sessionId={SessionId} requestedBackend={Backend} waveInDevice={WaveInDevice} device='{Device}' chunkMs={ChunkMs}",
                    sessionId, _micSettings.Backend, _micSettings.WaveInDeviceNumber,
                    discoveredMicDevice.Name, _runtimeSettings.SherpaChunkDurationMs);
            }

            SystemAudioCaptureSource? sysSource = null;
            if (wantsSystem && discoveredOutputDevice is not null)
            {
                sysSource = sp.GetRequiredService<SystemAudioCaptureSource>();
                var mmOutput = enumerator.GetDevice(discoveredOutputDevice.Id);
                sysSource.SetDevice(mmOutput);
                sysSource.SetChunkDuration(TimeSpan.FromMilliseconds(_runtimeSettings.SherpaChunkDurationMs));

                _logger.LogInformation(
                    "[SystemAudio.StartPlan] sessionId={SessionId} device='{Device}' chunkMs={ChunkMs}",
                    sessionId, discoveredOutputDevice.Name, _runtimeSettings.SherpaChunkDurationMs);
            }

            _pipelineStoppingCts?.Dispose();
            _pipelineStoppingCts = new CancellationTokenSource();

            var pipeline = sp.GetRequiredService<ITranscriptionPipeline>();
            if (pipeline is TranscriptionPipeline concrete)
            {
                concrete.SetSources(micSource, sysSource);
                concrete.SkipSystemAudioCapture = false;
            }

            _pipeline = pipeline;
            _pipeline.StatusChanged += OnPipelineStatusChanged;
            _pipeline.SegmentsProduced += OnSegmentsProduced;

            if (_sherpaProvisioning.State is SherpaModelProvisioningState.Provisioning or SherpaModelProvisioningState.Error
                || !_sherpaPreflight.IsSafeToUse)
            {
                var error = _sherpaProvisioning.State is SherpaModelProvisioningState.Provisioning or SherpaModelProvisioningState.Error
                    ? _sherpaProvisioning.LastError ?? "Provisioning Sherpa model..."
                    : _sherpaPreflight.LastError ?? "Sherpa model loaded but failed native preflight.";

                _audioStatus = new AudioStatusSnapshot
                {
                    MicrophoneStatus = micSource is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.Idle,
                    MicrophoneDevice = discoveredMicDevice?.Name ?? string.Empty,
                    SystemAudioStatus = sysSource is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.Idle,
                    SystemAudioDevice = discoveredOutputDevice?.Name ?? string.Empty,
                    TranscriptionStatus = TranscriptionPipelineStatus.Error,
                    TranscriptionConfigured = true,
                    TranscriptionProvider = "SherpaOnnx",
                    TranscriptionModel = SherpaOnnxModelService.DefaultModelId,
                    TranscriptionError = error,
                    TranscriptionLanguageMode = "requested/es actual/unknown",
                    SherpaProvisioningState = _sherpaProvisioning.State,
                    SherpaModelRoot = _sherpaProvisioning.ModelRoot,
                    SherpaNativeReadinessState = _sherpaPreflight.State
                };
                AudioStatusChanged?.Invoke(this, _audioStatus);
                return;
            }

            await _pipeline.StartAsync(sessionId, _pipelineStoppingCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Pipeline.Start] sessionId={SessionId} mode={Mode} failed=true",
                sessionId, mode);

            _audioStatus = new AudioStatusSnapshot
            {
                MicrophoneStatus = wantsMic
                    ? discoveredMicDevice is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.DeviceError
                    : AudioCaptureStatus.NoDevice,
                MicrophoneDevice = discoveredMicDevice?.Name ?? string.Empty,
                MicrophoneError = wantsMic ? ex.Message : null,
                SystemAudioStatus = wantsSystem
                    ? discoveredOutputDevice is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.DeviceError
                    : AudioCaptureStatus.NoDevice,
                SystemAudioDevice = discoveredOutputDevice?.Name ?? string.Empty,
                SystemAudioError = wantsSystem ? ex.Message : null,
                TranscriptionStatus = TranscriptionPipelineStatus.Error,
                TranscriptionConfigured = true,
                TranscriptionProvider = "SherpaOnnx",
                TranscriptionModel = SherpaOnnxModelService.DefaultModelId,
                TranscriptionError = ex.Message,
                TranscriptionLanguageMode = "requested/es actual/unknown",
                SherpaProvisioningState = _sherpaProvisioning.State,
                SherpaModelRoot = _sherpaProvisioning.ModelRoot,
                SherpaNativeReadinessState = _sherpaPreflight.State
            };
            AudioStatusChanged?.Invoke(this, _audioStatus);

            if (_pipelineScope is { } failedScope)
                await failedScope.DisposeAsync();
            _pipelineScope = null;
            _pipelineStoppingCts?.Dispose();
            _pipelineStoppingCts = null;
            _pipeline = null;
        }
    }

    private async Task StopPipelineAsync(CancellationToken ct)
    {
        if (_pipeline is null) return;

        try { _pipelineStoppingCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        _pipeline.StatusChanged -= OnPipelineStatusChanged;
        _pipeline.SegmentsProduced -= OnSegmentsProduced;

        try { await _pipeline.StopAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Pipeline.Stop] failed_nonfatal=true"); }
        finally
        {
            if (_pipeline is IAsyncDisposable ad)
            {
                try { await ad.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Pipeline.Dispose] failed_nonfatal=true"); }
            }
            _pipeline = null;

            if (_pipelineScope is { } pipelineScope)
                await pipelineScope.DisposeAsync();
            _pipelineScope = null;

            _pipelineStoppingCts?.Dispose();
            _pipelineStoppingCts = null;
        }
    }

    private void OnPipelineStatusChanged(object? sender, AudioStatusSnapshot status)
    {
        _audioStatus = status;
        _logger.LogDebug(
            "[UI.AudioStatus] sessionId={SessionId} micStatus={MicStatus} micRms={MicRms:F6} systemStatus={SystemStatus} systemRms={SystemRms:F6} txStatus={TxStatus} queue={Queue}",
            _activeSession?.Id, status.MicrophoneStatus, status.MicConvertedRms,
            status.SystemAudioStatus, status.SystemAudioConvertedRms,
            status.TranscriptionStatus, status.PendingChunks);
        AudioStatusChanged?.Invoke(this, status);
    }

    private void OnSegmentsProduced(object? sender, IReadOnlyList<TranscriptSegment> segments)
    {
        if (_activeSession is null) return;

        var meaningfulSegments = TranscriptTextFilter.FilterMeaningfulSegments(segments);
        if (meaningfulSegments.Count == 0) return;

        _transcriptSegmentCount += meaningfulSegments.Count;
        _transcriptBuffer.Push(meaningfulSegments);
        var recentText = _transcriptBuffer.GetRecentText(10);
        var intent = _intentDetector.Detect(meaningfulSegments);

        _logger.LogInformation(
            "[Transcript.Batch] sessionId={SessionId} count={Count} speakerTypes={SpeakerTypes} intent={Intent}",
            _activeSession.Id,
            meaningfulSegments.Count,
            string.Join(",", meaningfulSegments.Select(s => s.SpeakerType).Distinct()),
            intent.Intent);

        if (intent.HasIntent)
            _assistantReaction.OnIntentDetected(intent, recentText);

        _ = PersistSegmentsAsync(meaningfulSegments);
        TranscriptSegmentsReceived?.Invoke(this, meaningfulSegments);
        PublishSnapshot();
    }

    private async Task PersistSegmentsAsync(IReadOnlyList<TranscriptSegment> segments)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITranscriptRepository>();
            await repo.AddRangeAsync(segments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} transcript segment(s).", segments.Count);
        }
    }

    private void OnActiveWindowChanged(object? sender, ActiveWindowChangedEventArgs e)
    {
        _activeProcessName = e.Current.ProcessName;
        _activeWindowTitle = e.Current.WindowTitle;
        _activeProcessId = e.Current.ProcessId;

        if (_state == SessionLifecycleState.Listening && _activeSession is not null)
        {
            _ = RecordAppEventAsync(new AppEvent
            {
                Type = AppEventType.ActiveWindowChanged,
                SessionId = _activeSession.Id,
                Details = $"App={e.Current.ProcessName} PID={e.Current.ProcessId} Title={e.Current.WindowTitle}"
            });
        }

        PublishSnapshot();
    }

    private Task TransitionAsync(SessionLifecycleState next, Session? session, CancellationToken ct)
    {
        var previous = _state;
        _state = next;
        _appState.SyncLifecycleState(next);
        SessionStateChanged?.Invoke(this, new SessionStateChangedEventArgs
        {
            PreviousState = previous,
            NewState = next,
            Session = session
        });
        PublishSnapshot();
        return Task.CompletedTask;
    }

    private void PublishSnapshot()
    {
        var snap = new SessionStateSnapshot
        {
            LifecycleState = _state,
            SessionId = _activeSession?.Id,
            SessionTitle = _activeSession?.Title,
            SessionStartedAt = _activeSession?.StartedAt,
            AppEventCount = _sessionEventCount,
            TranscriptSegmentCount = _transcriptSegmentCount,
            ActiveProcessName = _activeProcessName,
            ActiveProcessId = _activeProcessId,
            ActiveWindowTitle = _activeWindowTitle
        };
        _snapshot = snap;
        SnapshotChanged?.Invoke(this, snap);
    }

    private void OnAppModeChanged(object? sender, AppMode mode)
    {
        switch (mode)
        {
            case AppMode.Listening when _state == SessionLifecycleState.Idle:
                if (!_sherpaProvisioning.IsReady || !_sherpaPreflight.IsSafeToUse)
                    break;
                _ = StartSessionAsync($"Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
                break;
            case AppMode.Listening when _state == SessionLifecycleState.Paused:
                _ = ResumeSessionAsync();
                break;
            case AppMode.Idle when _state == SessionLifecycleState.Listening:
                _ = PauseSessionAsync();
                break;
        }
    }

    public event EventHandler<IReadOnlyList<TranscriptSegment>>? TranscriptSegmentsReceived;
}
