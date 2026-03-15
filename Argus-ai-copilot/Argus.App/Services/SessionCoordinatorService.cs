using Argus.Core.Contracts.Repositories;
using Argus.Core.Contracts.Services;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App.Services;

/// <summary>
/// Long-running background service and authoritative session coordinator.
/// Owns the session lifecycle state machine and delegates persistence to
/// scoped repository instances created per operation.
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
    : BackgroundService, ISessionCoordinator
{
    private readonly ILogger<SessionCoordinatorService> _logger;
    private readonly IAppStateService _appState;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IArtifactStorage _artifactStorage;

    // State is accessed only from async operations that serialize through _gate.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionLifecycleState _state = SessionLifecycleState.Idle;
    private Session? _activeSession;

    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

    // ── ISessionCoordinator ───────────────────────────────────────────────────

    public SessionLifecycleState State => _state;
    public Session? ActiveSession      => _activeSession;

    // ─────────────────────────────────────────────────────────────────────────

    public SessionCoordinatorService(
        ILogger<SessionCoordinatorService> logger,
        IAppStateService appState,
        IServiceScopeFactory scopeFactory,
        IArtifactStorage artifactStorage)
    {
        _logger          = logger;
        _appState        = appState;
        _scopeFactory    = scopeFactory;
        _artifactStorage = artifactStorage;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} started.", nameof(SessionCoordinatorService));

        // Subscribe to tray-driven AppState changes so the tray buttons can also
        // drive the coordinator (Start Listening / Pause Listening).
        _appState.ModeChanged += OnAppModeChanged;

        try
        {
            // Keep the service alive. All real work is event-driven or called directly.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on clean shutdown.
        }
        finally
        {
            _appState.ModeChanged -= OnAppModeChanged;

            // If the host is shutting down while a session is active, stop it cleanly.
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
                Title         = string.IsNullOrWhiteSpace(title)
                                    ? $"Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"
                                    : title,
                Type          = type,
                ListeningMode = mode,
                StartedAt     = DateTimeOffset.UtcNow
            };

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var events   = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

            await sessions.AddAsync(session, ct);

            await events.AddAsync(new AppEvent
            {
                Type      = AppEventType.SessionStarted,
                SessionId = session.Id,
                Details   = $"Title='{session.Title}' Type={session.Type} Mode={session.ListeningMode}"
            }, ct);

            // Prepare on-disk session artifact folders.
            _artifactStorage.EnsureSessionFolders(session.Id);

            _activeSession = session;
            await TransitionAsync(SessionLifecycleState.Listening, session, ct);

            _appState.StartListening();
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

            await using var scope = _scopeFactory.CreateAsyncScope();
            var events = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

            await events.AddAsync(new AppEvent
            {
                Type      = AppEventType.SessionPaused,
                SessionId = _activeSession?.Id,
                Details   = $"PausedAt={DateTimeOffset.UtcNow:O}"
            }, ct);

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

            await using var scope = _scopeFactory.CreateAsyncScope();
            var events = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

            await events.AddAsync(new AppEvent
            {
                Type      = AppEventType.SessionResumed,
                SessionId = _activeSession?.Id,
                Details   = $"ResumedAt={DateTimeOffset.UtcNow:O}"
            }, ct);

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

            // ── Stopping ──────────────────────────────────────────────────────
            await TransitionAsync(SessionLifecycleState.Stopping, session, ct);
            _appState.StopListening();

            if (session is not null)
            {
                session.EndedAt   = DateTimeOffset.UtcNow;
                session.UpdatedAt = DateTimeOffset.UtcNow;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
                var events   = scope.ServiceProvider.GetRequiredService<IAppEventRepository>();

                await sessions.UpdateAsync(session, ct);
                await events.AddAsync(new AppEvent
                {
                    Type      = AppEventType.SessionEnded,
                    SessionId = session.Id,
                    Details   = $"Duration={session.Duration?.Duration.TotalSeconds:F1}s"
                }, ct);

                _logger.LogInformation(
                    "Session ended. Id={Id} Duration={Duration:F1}s",
                    session.Id,
                    session.Duration?.Duration.TotalSeconds ?? 0d);
            }

            // ── Completed → Idle ──────────────────────────────────────────────
            await TransitionAsync(SessionLifecycleState.Completed, session, ct);
            _activeSession = null;
            await TransitionAsync(SessionLifecycleState.Idle, null, ct);
        }
        finally { _gate.Release(); }
    }

    // ── ISessionCoordinator: ingestion placeholders ───────────────────────────

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

        _logger.LogDebug("AppEvent recorded. Type={Type} SessionId={SessionId}",
            appEvent.Type, appEvent.SessionId);
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

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles tray-initiated AppMode changes so the tray "Start Listening" and
    /// "Pause Listening" buttons continue to work without calling the coordinator directly.
    /// The coordinator is the authority — AppState just signals intent.
    /// </summary>
    private void OnAppModeChanged(object? sender, AppMode mode)
    {
        // Only react to tray-driven transitions when we are NOT already mid-operation.
        // We fire-and-forget here because this is an event handler on the dispatcher thread.
        // The _gate semaphore inside each command prevents any race condition.
        switch (mode)
        {
            case AppMode.Listening when _state == SessionLifecycleState.Idle:
                _ = StartSessionAsync($"Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
                break;

            case AppMode.Listening when _state == SessionLifecycleState.Paused:
                _ = ResumeSessionAsync();
                break;

            case AppMode.Idle when _state == SessionLifecycleState.Listening:
                // Tray pause button sets AppMode.Idle — treat as pause, not stop.
                _ = PauseSessionAsync();
                break;
        }
    }
}

