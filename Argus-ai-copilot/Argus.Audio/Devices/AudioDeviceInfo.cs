namespace Argus.Audio.Devices;

/// <summary>
/// Describes a Windows audio endpoint device discovered via WASAPI MMDevice API.
/// </summary>
public sealed class AudioDeviceInfo
{
    /// <summary>WASAPI endpoint device ID — e.g. "{0.0.1.00000000}.{guid}".</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Friendly name shown to the user — e.g. "Microphone (Realtek Audio)".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether this is an input (capture) or output (render) device.</summary>
    public AudioDeviceKind Kind { get; init; }

    /// <summary>True if this is the current Windows default device for its data-flow.</summary>
    public bool IsDefault { get; init; }

    /// <summary>True if the device is present and in an active state.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Human-readable display combining name and default marker.</summary>
    public string DisplayName => IsDefault ? $"{Name}  (default)" : Name;

    public override string ToString() => $"[{Kind}] {DisplayName}  Id={Id}";
}

/// <summary>Direction of audio flow for a Windows endpoint device.</summary>
public enum AudioDeviceKind
{
    /// <summary>Microphone or other input device (eCapture).</summary>
    Input,

    /// <summary>Speaker, headphones, or other output device (eRender).</summary>
    Output
}
