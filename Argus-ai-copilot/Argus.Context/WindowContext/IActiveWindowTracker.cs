namespace Argus.Context.WindowContext;

/// <summary>
/// Tracks the currently active (foreground) window.
/// Raises <see cref="ActiveWindowChanged"/> only when the window actually changes.
/// </summary>
public interface IActiveWindowTracker
{
    /// <summary>The most recently observed active window. Null before the first poll.</summary>
    ActiveWindowInfo? Current { get; }

    /// <summary>
    /// Raised on a background thread whenever the foreground window changes.
    /// The argument carries both the previous and new snapshots.
    /// </summary>
    event EventHandler<ActiveWindowChangedEventArgs>? ActiveWindowChanged;
}

/// <summary>Event payload for <see cref="IActiveWindowTracker.ActiveWindowChanged"/>.</summary>
public sealed class ActiveWindowChangedEventArgs : EventArgs
{
    public required ActiveWindowInfo? Previous { get; init; }
    public required ActiveWindowInfo  Current  { get; init; }
}
