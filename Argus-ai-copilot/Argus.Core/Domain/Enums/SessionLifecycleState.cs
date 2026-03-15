namespace Argus.Core.Domain.Enums;

/// <summary>
/// The five lifecycle states a session can be in.
/// This is the authoritative session-level state, distinct from
/// <c>AppMode</c> which is the UI/tray-facing operating mode.
/// </summary>
public enum SessionLifecycleState
{
    /// <summary>No active session. The app is waiting for the user to start one.</summary>
    Idle,

    /// <summary>A session is active and capturing input.</summary>
    Listening,

    /// <summary>A session is active but capture is temporarily suspended.</summary>
    Paused,

    /// <summary>A session is wrapping up: capture has stopped, finalisation is in progress.</summary>
    Stopping,

    /// <summary>A session has been fully finalised and persisted. Ready to transition back to Idle.</summary>
    Completed
}
