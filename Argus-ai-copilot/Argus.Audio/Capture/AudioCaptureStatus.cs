namespace Argus.Audio.Capture;

/// <summary>
/// The runtime state of an audio capture source.
/// </summary>
public enum AudioCaptureStatus
{
    /// <summary>Capture has not been started.</summary>
    Idle,

    /// <summary>Capture is actively recording.</summary>
    Capturing,

    /// <summary>Capture is suspended (session paused).</summary>
    Paused,

    /// <summary>Device could not be opened or was lost during capture.</summary>
    DeviceError,

    /// <summary>No suitable capture device was found on this system.</summary>
    NoDevice
}
