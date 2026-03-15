namespace Argus.Context.WindowContext;

/// <summary>
/// A point-in-time snapshot of the foreground window.
/// All string properties are never null — empty string signals "unknown".
/// </summary>
public sealed class ActiveWindowInfo
{
    /// <summary>Win32 window handle.</summary>
    public nint WindowHandle { get; init; }

    /// <summary>Window title bar text.</summary>
    public string WindowTitle { get; init; } = string.Empty;

    /// <summary>Executable file name without extension, e.g. "chrome" or "Teams".</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>OS process identifier, or 0 if unavailable.</summary>
    public int ProcessId { get; init; }

    /// <summary>Full path to the process executable, if accessible.</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>When this snapshot was taken (UTC).</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>A short display string suitable for logging and UI labels.</summary>
    public string DisplayText =>
        string.IsNullOrWhiteSpace(WindowTitle)
            ? ProcessName
            : $"{ProcessName}: {WindowTitle}";

    public override string ToString() =>
        $"[{CapturedAt:HH:mm:ss}] {DisplayText}";
}
