using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;

namespace Argus.Core.Contracts.Services;

/// <summary>
/// Payload for <see cref="ISessionCoordinator.SessionStateChanged"/>.
/// </summary>
public sealed class SessionStateChangedEventArgs : EventArgs
{
    public SessionLifecycleState PreviousState { get; init; }
    public SessionLifecycleState NewState      { get; init; }

    /// <summary>The session involved in the transition, if one is active.</summary>
    public Session? Session { get; init; }
}

/// <summary>
/// Public surface of the session coordinator.
/// Inject this wherever another service needs to observe or drive session lifecycle
/// without taking a hard dependency on the <c>Argus.App</c> assembly.
/// </summary>
public interface ISessionCoordinator
{
    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>Current lifecycle state.</summary>
    SessionLifecycleState State { get; }

    /// <summary>The currently active session, or <c>null</c> when Idle/Completed.</summary>
    Session? ActiveSession { get; }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new session, persists it, and transitions to <see cref="SessionLifecycleState.Listening"/>.
    /// Throws <see cref="InvalidOperationException"/> if a session is already active.
    /// </summary>
    Task<Session> StartSessionAsync(
        string title,
        SessionType type      = SessionType.FreeForm,
        ListeningMode mode    = ListeningMode.Microphone,
        CancellationToken ct  = default);

    /// <summary>
    /// Suspends the active session. Transitions to <see cref="SessionLifecycleState.Paused"/>.
    /// No-op if already paused or not listening.
    /// </summary>
    Task PauseSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Resumes a paused session. Transitions back to <see cref="SessionLifecycleState.Listening"/>.
    /// </summary>
    Task ResumeSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops and finalises the active session. Transitions through
    /// <see cref="SessionLifecycleState.Stopping"/> → <see cref="SessionLifecycleState.Completed"/> → Idle.
    /// </summary>
    Task StopSessionAsync(CancellationToken ct = default);

    // ── Ingestion placeholders ────────────────────────────────────────────────

    /// <summary>
    /// Accepts a transcript segment produced by the transcription pipeline.
    /// Persists it and associates it with the active session.
    /// No-op when no session is active.
    /// </summary>
    Task IngestTranscriptSegmentAsync(TranscriptSegment segment, CancellationToken ct = default);

    /// <summary>
    /// Records screenshot metadata for the active session.
    /// The binary file must already have been saved via <c>IArtifactStorage</c>.
    /// No-op when no session is active.
    /// </summary>
    Task IngestScreenshotMetadataAsync(ScreenshotArtifact artifact, CancellationToken ct = default);

    /// <summary>
    /// Records a domain event for the active (or recently ended) session.
    /// </summary>
    Task RecordAppEventAsync(AppEvent appEvent, CancellationToken ct = default);

    // ── Notifications ─────────────────────────────────────────────────────────

    /// <summary>Raised on every state transition.</summary>
    event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
}
