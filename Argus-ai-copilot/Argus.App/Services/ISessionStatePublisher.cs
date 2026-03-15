namespace Argus.App.Services;

/// <summary>
/// Exposes the live session + active-window snapshot to UI consumers within Argus.App.
/// Implemented by <see cref="SessionCoordinatorService"/>.
/// </summary>
public interface ISessionStatePublisher
{
    /// <summary>The current session and window state snapshot.</summary>
    SessionStateSnapshot Snapshot { get; }

    /// <summary>Raised on any state change. Always fired on a thread-pool thread.</summary>
    event EventHandler<SessionStateSnapshot>? SnapshotChanged;
}
