namespace Argus.Audio.Devices;

/// <summary>
/// Discovers and resolves Windows audio endpoint devices via WASAPI.
/// </summary>
public interface IAudioDeviceDiscovery
{
    /// <summary>
    /// Returns all currently active input (microphone) devices.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    /// <summary>
    /// Returns all currently active output (speaker/loopback) devices.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>
    /// Returns the Windows default communications/multimedia microphone,
    /// or <c>null</c> if no input device is present.
    /// </summary>
    AudioDeviceInfo? GetDefaultInputDevice();

    /// <summary>
    /// Returns the Windows default multimedia output device (used for loopback capture),
    /// or <c>null</c> if no output device is present.
    /// </summary>
    AudioDeviceInfo? GetDefaultOutputDevice();
}
