using Argus.Transcription.SherpaOnnx; // Importing the shared enum
using Argus.Audio.Capture;
using Argus.Audio.Devices;
using Argus.Context.WindowContext;
using Argus.Core.Contracts.Repositories;
using Argus.Core.Contracts.Services;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Infrastructure.Storage;
using Argus.Transcription.Intent;
using Argus.Transcription.Pipeline;
using Argus.Transcription.SherpaOnnx;
using Argus.Transcription.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App.Services;

/// <summary>
/// Long-running background service and authoritative session coordinator.
/// Owns the session lifecycle state machine, subscribes to active-window
/// changes, drives the transcription pipeline, and publishes snapshots for the UI.
///
/// State machine:
///   Idle ──StartSession──▶ Listening ──Pause──▶ Paused
///                               ▲                  │
///                               └────Resume─────────┘
///                               │
///                             Stop
///                               │
///                               ▼
///                           Stopping ──(finalise)──▶ Completed ──▶ Idle
/// </summary>
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

    // All state fields are accessed only via operations serialised through _gate.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionLifecycleState _state = SessionLifecycleState.Idle;
    private Session? _activeSession;
    private int _sessionEventCount;
    private string _activeProcessName = string.Empty;
    private string _activeWindowTitle  = string.Empty;
    private int    _activeProcessId;

    // ── Transcription pipeline ─────────────────────────────────────────────
    // Owned by the coordinator; created fresh for each session.
    // _pipelineScope MUST outlive the pipeline — it owns the Transient capture sources.
    // Dispose only after pipeline.StopAsync() returns.
    private ITranscriptionPipeline? _pipeline;
    private AsyncServiceScope?      _pipelineScope;
    private CancellationTokenSource? _pipelineStoppingCts;
    private int _transcriptSegmentCount;

    // ── ISessionCoordinator ───────────────────────────────────────────────────

    public SessionLifecycleState State  => _state;
    public Session? ActiveSession       => _activeSession;

    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

    // ── ISessionStatePublisher ────────────────────────────────────────────────

    private SessionStateSnapshot _snapshot = SessionStateSnapshot.Idle;
    public SessionStateSnapshot Snapshot => _snapshot;
    public event EventHandler<SessionStateSnapshot>? SnapshotChanged;

    // ── IAudioStatusPublisher ─────────────────────────────────────────────────

    private AudioStatusSnapshot _audioStatus = AudioStatusSnapshot.Idle;
    public AudioStatusSnapshot AudioStatus => _audioStatus;
    public event EventHandler<AudioStatusSnapshot>? AudioStatusChanged;

    // ─────────────────────────────────────────────────────────────────────────

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
        ISherpaOnnxProvisioningService sherpaProvisioning)
    {
        _logger           = logger;
        _appState         = appState;
        _scopeFactory     = scopeFactory;
        _artifactStorage  = artifactStorage;
        _windowTracker    = windowTracker;
        _deviceDiscovery  = deviceDiscovery;
        _transcriptBuffer = transcriptBuffer;
        _intentDetector   = intentDetector;
        _assistantReaction = assistantReaction;
        _micSettings      = micSettings;
        _sherpaProvisioning = sherpaProvisioning;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} started.", nameof(SessionCoordinatorService));

        _appState.ModeChanged              += OnAppModeChanged;
        _windowTracker.ActiveWindowChanged += OnActiveWindowChanged;

        // Seed window state from whatever is already visible.
        if (_windowTracker.Current is { } initial)
        {
            _activeProcessName = initial.ProcessName;
            _activeWindowTitle  = initial.WindowTitle;
            _activeProcessId   = initial.ProcessId;
            PublishSnapshot();
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _appState.ModeChanged              -= OnAppModeChanged;
            _windowTracker.ActiveWindowChanged -= OnActiveWindowChanged;

            if (_state is SessionLifecycleState.Listening or SessionLifecycleState.Paused)
            {
                _logger.LogWarning("Host shutting down with active session — forcing stop.");
                await StopSessionAsync(CancellationToken.None);
            }
        }

        _logger.LogInformation("{Service} stopped.", nameof(SessionCoordinatorService));
    }

    // ── ISessionCoordinator: commands ─────────────────────────────────────────

    public async Task<Session> StartSessionAsync(
        string title,
        SessionType type     = SessionType.FreeForm,
        ListeningMode mode   = ListeningMode.Microphone,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not SessionLifecycleState.Idle)
                throw new InvalidOperationException(
                    $"Cannot start a session while in state '{_state}'. Stop the current session first.");

            var session = new Session
            {
                Title              = string.IsNullOrWhiteSpace(title)
                                        ? $"Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"
                                        : title,
                Type               = type,
                ListeningMode      = mode,
                StartedAt          = DateTimeOffset.UtcNow,
                LifecycleState     = SessionLifecycleState.Listening,
                ApplicationContext = _activeProcessName
            };

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events   = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

            await sessions.AddAsync(session, ct);
            await events.AddAsync(new AppEvent
            {
                Type      = AppEventType.SessionStarted,
                SessionId = session.Id,
                Details   = $"Title='{session.Title}' Type={session.Type} Mode={session.ListeningMode} App={_activeProcessName}"
            }, ct);

            _artifactStorage.EnsureSessionFolders(session.Id);

            _activeSession          = session;
            _sessionEventCount      = 1;
            _transcriptSegmentCount = 0;
            _transcriptBuffer.Clear();

            await TransitionAsync(SessionLifecycleState.Listening, session, ct);
            _appState.StartListening();

            // ── Start the transcription pipeline ──────────────────────────
            // Pass CancellationToken.None: the pipeline lifetime is managed by
            // _pipelineStoppingCts inside StartPipelineAsync — not by the caller's ct.
            await StartPipelineAsync(session.Id);

            _logger.LogInformation(
                "Session started. Id={Id} Title='{Title}' Type={Type} Mode={Mode}",
                session.Id, session.Title, session.Type, session.ListeningMode);

            return session;
        }
        finally { _gate.Release(); }
    }

    public async Task PauseSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not SessionLifecycleState.Listening)
            {
                _logger.LogWarning("PauseSession called in state '{State}' — ignored.", _state);
                return;
            }

            // Pause audio before persisting so state is consistent
            _pipeline?.Pause();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events   = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

            await events.AddAsync(new AppEvent
            {
                Type      = AppEventType.SessionPaused,
                SessionId = _activeSession?.Id,
                Details   = $"PausedAt={DateTimeOffset.UtcNow:O}"
            }, ct);

            if (_activeSession is not null)
            {
                _activeSession.LifecycleState = SessionLifecycleState.Paused;
                _activeSession.UpdatedAt      = DateTimeOffset.UtcNow;
                await sessions.UpdateAsync(_activeSession, ct);
            }

            _sessionEventCount++;
            await TransitionAsync(SessionLifecycleState.Paused, _activeSession, ct);
            _appState.PauseListening();

            _logger.LogInformation("Session paused. Id={Id}", _activeSession?.Id);
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is not SessionLifecycleState.Paused)
            {
                _logger.LogWarning("ResumeSession called in state '{State}' — ignored.", _state);
                return;
            }

            _pipeline?.Resume();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events   = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

            await events.AddAsync(new AppEvent
            {
                Type      = AppEventType.SessionResumed,
                SessionId = _activeSession?.Id,
                Details   = $"ResumedAt={DateTimeOffset.UtcNow:O}"
            }, ct);

            if (_activeSession is not null)
            {
                _activeSession.LifecycleState = SessionLifecycleState.Listening;
                _activeSession.UpdatedAt      = DateTimeOffset.UtcNow;
                await sessions.UpdateAsync(_activeSession, ct);
            }

            _sessionEventCount++;
            await TransitionAsync(SessionLifecycleState.Listening, _activeSession, ct);
            _appState.StartListening();

            _logger.LogInformation("Session resumed. Id={Id}", _activeSession?.Id);
        }
        finally { _gate.Release(); }
    }

    public async Task StopSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state is SessionLifecycleState.Idle or SessionLifecycleState.Completed)
            {
                _logger.LogWarning("StopSession called in state '{State}' — ignored.", _state);
                return;
            }

            var session = _activeSession;

            await TransitionAsync(SessionLifecycleState.Stopping, session, ct);
            _appState.StopListening();

            // ── Stop the transcription pipeline (flushes remaining chunks) ──
            await StopPipelineAsync(ct);

            if (session is not null)
            {
                session.EndedAt        = DateTimeOffset.UtcNow;
                session.UpdatedAt      = DateTimeOffset.UtcNow;
                session.LifecycleState = SessionLifecycleState.Completed;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
                var events   = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

                await sessions.UpdateAsync(session, ct);
                await events.AddAsync(new AppEvent
                {
                    Type      = AppEventType.SessionEnded,
                    SessionId = session.Id,
                    Details   = $"Duration={session.Duration?.Duration.TotalSeconds:F1}s Events={_sessionEventCount} Segments={_transcriptSegmentCount}"
                }, ct);

                _logger.LogInformation(
                    "Session ended. Id={Id} Duration={Duration:F1}s Events={Events} Segments={Segments}",
                    session.Id,
                    session.Duration?.Duration.TotalSeconds ?? 0d,
                    _sessionEventCount,
                    _transcriptSegmentCount);
            }

            _activeSession          = null;
            _sessionEventCount      = 0;
            _transcriptSegmentCount = 0;
            _audioStatus            = AudioStatusSnapshot.Idle;
            AudioStatusChanged?.Invoke(this, _audioStatus);

            await TransitionAsync(SessionLifecycleState.Completed, session, ct);
            await TransitionAsync(SessionLifecycleState.Idle, null, ct);
        }
        finally { _gate.Release(); }
    }

    // ── ISessionCoordinator: ingestion ────────────────────────────────────────

    public async Task IngestTranscriptSegmentAsync(TranscriptSegment segment, CancellationToken ct = default)
    {
        if (_activeSession is null || _state is not SessionLifecycleState.Listening)
        {
            _logger.LogWarning("IngestTranscriptSegment called with no active listening session — dropped.");
            return;
        }

        segment.SessionId = _activeSession.Id;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITranscriptRepository>();
        await repo.AddAsync(segment, ct);

        _logger.LogDebug(
            "Transcript segment ingested. SessionId={SessionId} Speaker={Speaker} Chars={Chars}",
            _activeSession.Id, segment.SpeakerType, segment.Text.Length);
    }

    public async Task IngestScreenshotMetadataAsync(ScreenshotArtifact artifact, CancellationToken ct = default)
    {
        if (_activeSession is null || _state is not SessionLifecycleState.Listening)
        {
            _logger.LogWarning("IngestScreenshotMetadata called with no active listening session — dropped.");
            return;
        }

        artifact.SessionId = _activeSession.Id;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScreenshotRepository>();
        await repo.AddAsync(artifact, ct);

        _logger.LogDebug(
            "Screenshot metadata ingested. SessionId={SessionId} File={File}",
            _activeSession.Id, artifact.FilePath);
    }

    public async Task RecordAppEventAsync(AppEvent appEvent, CancellationToken ct = default)
    {
        appEvent.SessionId ??= _activeSession?.Id;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();
        await repo.AddAsync(appEvent, ct);

        _sessionEventCount++;

        _logger.LogDebug("AppEvent recorded. Type={Type} SessionId={SessionId}",
            appEvent.Type, appEvent.SessionId);
    }

    // ── Pipeline management ───────────────────────────────────────────────────

    private async Task StartPipelineAsync(Guid sessionId)
    {
        _logger.LogInformation("[Pipeline.Start] Acquiring pipeline scope for SessionId={Id}", sessionId);
        var discoveredMicDevice = default(Argus.Audio.Devices.AudioDeviceInfo);
        var discoveredOutputDevice = default(Argus.Audio.Devices.AudioDeviceInfo);

        try
        {
            // ── Enumerate all audio endpoints at pipeline start ───────────────
            // This gives us a complete picture of the audio hardware in the log.
            if (_deviceDiscovery is Argus.Audio.Devices.WindowsAudioDeviceDiscovery winDisc)
                winDisc.LogAllEndpoints();

            // ── Device discovery ────────────────────────────────────────────
            var micDevice    = _deviceDiscovery.GetDefaultInputDevice();
            var outputDevice = _deviceDiscovery.GetDefaultOutputDevice();
            discoveredMicDevice = micDevice;
            discoveredOutputDevice = outputDevice;

            if (micDevice is null)
            {
                _logger.LogWarning("[Pipeline.Start] No microphone found — audio capture skipped.");
                _audioStatus = new AudioStatusSnapshot
                {
                    MicrophoneStatus  = AudioCaptureStatus.NoDevice,
                    SystemAudioStatus = AudioCaptureStatus.NoDevice
                };
                AudioStatusChanged?.Invoke(this, _audioStatus);
                return;
            }

            _logger.LogInformation(
                "[Pipeline.Start] Devices: mic='{Mic}'  output='{Output}'",
                micDevice.Name, outputDevice?.Name ?? "none");

            // ── Build sources ────────────────────────────────────────────────
            // IMPORTANT: do NOT use `await using` or `using` here.
            // _pipelineScope must outlive this method — it owns the Transient
            // MicrophoneCaptureSource and SystemAudioCaptureSource instances.
            // Disposing the scope while capture is running would call Dispose()
            // on those sources and stop WASAPI recording immediately.
            // Scope is disposed in StopPipelineAsync, after pipeline.StopAsync() returns.
            _pipelineScope = _scopeFactory.CreateAsyncScope();
            var sp = _pipelineScope.Value.ServiceProvider;

            _logger.LogInformation("[Pipeline.Start] Resolving capture sources from long-lived scope.");

            var micSource = sp.GetRequiredService<MicrophoneCaptureSource>();

            // Acquire MMDevice handles. The MMDeviceEnumerator itself can be
            // short-lived — handles are owned by the source objects.
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var mmMic = enumerator.GetDevice(micDevice.Id);
            micSource.SetDevice(mmMic);

            // ── Apply mic backend settings ─────────────────────────────────────
            // Honor _micSettings.Backend exactly — never override it here.
            //   WaveIn / Wasapi → explicit; MicrophoneCaptureSource.StartAsync will
            //                     open exactly that backend or throw a clear error.
            //   Auto            → MicrophoneCaptureSource.StartAsync probes both
            //                     backends for ~1.5 s and selects the one with signal.
            micSource.SelectedBackend    = _micSettings.Backend;
            micSource.WaveInDeviceNumber = _micSettings.WaveInDeviceNumber;
            micSource.WasapiContCapture  = _micSettings.Backend is MicBackend.Wasapi or MicBackend.Auto;

            _logger.LogInformation(
                "[Pipeline.Start] MicrophoneCaptureSource configured: '{Device}' " +
                "RequestedBackend={Requested} WaveInDevice={WaveInDevice}",
                micDevice.Name, _micSettings.Backend, _micSettings.WaveInDeviceNumber);

            SystemAudioCaptureSource? sysSource = null;
            if (outputDevice is not null)
            {
                sysSource = sp.GetRequiredService<SystemAudioCaptureSource>();
                var mmOutput = enumerator.GetDevice(outputDevice.Id);
                sysSource.SetDevice(mmOutput);
                _logger.LogInformation("[Pipeline.Start] SystemAudioCaptureSource configured: '{Device}'", outputDevice.Name);
            }

            // ── Create independent stopping CTS for the pipeline ─────────────
            // This CTS is NOT linked to any external CancellationToken.
            // The only way to cancel it is to call StopPipelineAsync.
            _pipelineStoppingCts?.Dispose();
            _pipelineStoppingCts = new CancellationTokenSource();
            _logger.LogInformation("[Pipeline.Start] Pipeline stopping CTS created (not linked to any external token).");

            // ── Wire pipeline ────────────────────────────────────────────────
            var pipeline = sp.GetRequiredService<ITranscriptionPipeline>();
            if (pipeline is Argus.Transcription.Pipeline.TranscriptionPipeline concrete)
            {
                concrete.SetSources(micSource, sysSource);

                // ── Mic-only debug mode ───────────────────────────────────────
                // Suppresses loopback while diagnosing the microphone capture path.
                // Remove or set to false once mic is confirmed working.
                concrete.SkipSystemAudioCapture = true;
            }

            _pipeline = pipeline;
            _pipeline.StatusChanged    += OnPipelineStatusChanged;
            _pipeline.SegmentsProduced += OnSegmentsProduced;

            // ── WASAPI isolation test ─────────────────────────────────────────
            // Isolation test confirmed: fresh handle records real signal.
            // COM/MMDevice stale-handle bug is now fixed (store ID, resolve fresh
            // in StartAsync).  Test disabled; source quality is the new focus.
            // Uncomment to re-run if signal disappears again.
            //
            // using var isoEnum  = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            // var isoDevice      = isoEnum.GetDevice(micDevice.Id);
            // await Argus.Audio.Capture.WasapiIsolationTest.RunAsync(
            //     isoDevice, _logger, durationSeconds: 5, ct: _pipelineStoppingCts.Token);
            // await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("[Pipeline.Start] Calling pipeline.StartAsync for SessionId={Id}", sessionId);
            if (_sherpaProvisioning.State is SherpaModelProvisioningState.Provisioning or SherpaModelProvisioningState.Error)
            {
                _audioStatus = new AudioStatusSnapshot
                {
                    MicrophoneStatus = AudioCaptureStatus.Capturing,
                    MicrophoneDevice = micDevice.Name,
                    SystemAudioStatus = outputDevice is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.Idle,
                    SystemAudioDevice = outputDevice?.Name ?? string.Empty,
                    TranscriptionStatus = TranscriptionPipelineStatus.Error,
                    TranscriptionConfigured = true,
                    TranscriptionProvider = "SherpaOnnx",
                    TranscriptionModel = "multilingual-streaming",
                    TranscriptionError = _sherpaProvisioning.LastError ?? "Sherpa model provisioning is not ready.",
                    TranscriptionLanguageMode = "forced/es",
                    SherpaProvisioningState = _sherpaProvisioning.State,
                    SherpaModelRoot = _sherpaProvisioning.ModelRoot
                };
                AudioStatusChanged?.Invoke(this, _audioStatus);
                return;
            }

            await _pipeline.StartAsync(sessionId, _pipelineStoppingCts.Token);
            _logger.LogInformation("[Pipeline.Start] pipeline.StartAsync returned. Capture sources are running.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Pipeline.Start] Failed to start transcription pipeline — session continues without audio.");
            _audioStatus = new AudioStatusSnapshot
            {
                MicrophoneStatus = discoveredMicDevice is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.Capturing,
                MicrophoneDevice = discoveredMicDevice?.Name ?? string.Empty,
                SystemAudioStatus = discoveredOutputDevice is null ? AudioCaptureStatus.NoDevice : AudioCaptureStatus.Idle,
                SystemAudioDevice = discoveredOutputDevice?.Name ?? string.Empty,
                TranscriptionStatus = TranscriptionPipelineStatus.Error,
                TranscriptionConfigured = true,
                TranscriptionProvider = "SherpaOnnx",
                TranscriptionModel = "multilingual-streaming",
                TranscriptionError = ex.Message,
                TranscriptionLanguageMode = "forced/es",
                SherpaProvisioningState = _sherpaProvisioning.State,
                SherpaModelRoot = _sherpaProvisioning.ModelRoot
            };
            AudioStatusChanged?.Invoke(this, _audioStatus);
            // Clean up the scope since startup failed.
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
        if (_pipeline is null)
        {
            _logger.LogDebug("[Pipeline.Stop] StopPipelineAsync called but pipeline is null — nothing to stop.");
            return;
        }

        _logger.LogInformation("[Pipeline.Stop] Signalling pipeline stopping CTS.");
        try { _pipelineStoppingCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed — ignore */ }

        _pipeline.StatusChanged    -= OnPipelineStatusChanged;
        _pipeline.SegmentsProduced -= OnSegmentsProduced;

        try
        {
            _logger.LogInformation("[Pipeline.Stop] Calling pipeline.StopAsync.");
            await _pipeline.StopAsync(ct);
            _logger.LogInformation("[Pipeline.Stop] pipeline.StopAsync completed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pipeline.Stop] Error stopping transcription pipeline.");
        }
        finally
        {
            if (_pipeline is IAsyncDisposable ad)
            {
                try { await ad.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Pipeline.Stop] Error disposing pipeline."); }
            }
            _pipeline = null;

            // Dispose capture sources by disposing the scope AFTER the pipeline
            // has fully stopped. This is the correct disposal order.
            _logger.LogInformation("[Pipeline.Stop] Disposing pipeline scope (capture sources will be disposed now).");
            if (_pipelineScope is { } pipelineScope)
                await pipelineScope.DisposeAsync();
            _pipelineScope = null;

            _pipelineStoppingCts?.Dispose();
            _pipelineStoppingCts = null;

            _logger.LogInformation("[Pipeline.Stop] Pipeline scope and stopping CTS disposed.");
        }
    }

    // ── Pipeline event handlers ───────────────────────────────────────────────

    private void OnPipelineStatusChanged(object? sender, AudioStatusSnapshot status)
    {
        _audioStatus = status;
        AudioStatusChanged?.Invoke(this, status);
    }

    private void OnSegmentsProduced(object? sender, IReadOnlyList<TranscriptSegment> segments)
    {
        if (_activeSession is null) return;

        var meaningfulSegments = TranscriptTextFilter.FilterMeaningfulSegments(segments);
        if (meaningfulSegments.Count == 0)
        {
            _logger.LogDebug("[UIBridge] Dropping junk-only transcript batch before UI/buffer publishing.");
            return;
        }

        _transcriptSegmentCount += meaningfulSegments.Count;

        _logger.LogInformation(
            "[UIBridge] Re-raising transcript segments to UI. Count={Count} First='{First}'",
            meaningfulSegments.Count,
            meaningfulSegments.Count > 0
                ? (meaningfulSegments[0].Text.Length > 120 ? meaningfulSegments[0].Text[..120] + "…" : meaningfulSegments[0].Text)
                : string.Empty);

        // Push to rolling buffer and check for intent
        _transcriptBuffer.Push(meaningfulSegments);
        var recentText = _transcriptBuffer.GetRecentText(10);
        var intent     = _intentDetector.Detect(meaningfulSegments);
        var assistantTriggered = intent.HasIntent;

        var newestSegmentAt = meaningfulSegments.Max(segment => segment.CreatedAt);
        var newestSegmentAgeMs = Math.Max(0, (DateTimeOffset.UtcNow - newestSegmentAt).TotalMilliseconds);
        _logger.LogInformation(
            "[UiBridgeLatency] segments={Count} newestSegmentAgeMs={AgeMs:F1} intent={Intent} assistantTriggered={Triggered}",
            meaningfulSegments.Count,
            newestSegmentAgeMs,
            intent.Intent,
            assistantTriggered);

        if (intent.HasIntent)
        {
            _assistantReaction.OnIntentDetected(intent, recentText);
        }

        // Persist each segment and publish to UI via snapshot update
        _ = PersistSegmentsAsync(meaningfulSegments);

        // Notify the UI with the latest segment texts
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

    // ── Window change handler ─────────────────────────────────────────────────

    private void OnActiveWindowChanged(object? sender, ActiveWindowChangedEventArgs e)
    {
        _activeProcessName = e.Current.ProcessName;
        _activeWindowTitle  = e.Current.WindowTitle;
        _activeProcessId   = e.Current.ProcessId;

        if (_state == SessionLifecycleState.Listening && _activeSession is not null)
        {
            var appEvent = new AppEvent
            {
                Type      = AppEventType.ActiveWindowChanged,
                SessionId = _activeSession.Id,
                Details   = $"App={e.Current.ProcessName} PID={e.Current.ProcessId} Title={e.Current.WindowTitle}"
            };
            _ = RecordAppEventAsync(appEvent);
        }

        PublishSnapshot();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private Task TransitionAsync(
        SessionLifecycleState next, Session? session, CancellationToken ct)
    {
        var previous = _state;
        _state = next;
        _appState.SyncLifecycleState(next);

        _logger.LogDebug("SessionLifecycle {Prev} → {Next}", previous, next);

        SessionStateChanged?.Invoke(this, new SessionStateChangedEventArgs
        {
            PreviousState = previous,
            NewState      = next,
            Session       = session
        });

        PublishSnapshot();
        return Task.CompletedTask;
    }

    private void PublishSnapshot()
    {
        var snap = new SessionStateSnapshot
        {
            LifecycleState          = _state,
            SessionId               = _activeSession?.Id,
            SessionTitle            = _activeSession?.Title,
            SessionStartedAt        = _activeSession?.StartedAt,
            AppEventCount           = _sessionEventCount,
            TranscriptSegmentCount  = _transcriptSegmentCount,
            ActiveProcessName       = _activeProcessName,
            ActiveProcessId         = _activeProcessId,
            ActiveWindowTitle       = _activeWindowTitle
        };

        _snapshot = snap;
        SnapshotChanged?.Invoke(this, snap);
    }

    private void OnAppModeChanged(object? sender, AppMode mode)
    {
        switch (mode)
        {
            case AppMode.Listening when _state == SessionLifecycleState.Idle:
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

    // ── Extra events ──────────────────────────────────────────────────────────

    /// <summary>
    /// Raised whenever new transcript segments arrive from the pipeline.
    /// UI subscribers must marshal to the UI thread before updating controls.
    /// </summary>
    public event EventHandler<IReadOnlyList<TranscriptSegment>>? TranscriptSegmentsReceived;
}
