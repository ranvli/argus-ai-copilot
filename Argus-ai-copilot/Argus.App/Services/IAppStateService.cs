namespace Argus.App.Services;

/// <summary>Represents the operating mode of the Argus session.</summary>
public enum AppMode
{
    Idle,
    Listening,
    Processing
}

/// <summary>
/// Tracks and exposes the live runtime state of the Argus session.
/// Inject this wherever UI or background services need to read or change state.
/// </summary>
public interface IAppStateService
{
    /// <summary>True while the session is actively capturing input.</summary>
    bool IsListening { get; }

    /// <summary>True while listening is suspended but not fully stopped.</summary>
    bool IsPaused { get; }

    /// <summary>The current operating mode.</summary>
    AppMode CurrentMode { get; }

    /// <summary>Begin or resume capturing input.</summary>
    void StartListening();

    /// <summary>Suspend capturing without resetting the session.</summary>
    void PauseListening();

    /// <summary>Stop capturing and reset to idle.</summary>
    void StopListening();

    /// <summary>Raised on the calling thread whenever <see cref="CurrentMode"/> changes.</summary>
    event EventHandler<AppMode>? ModeChanged;
}
