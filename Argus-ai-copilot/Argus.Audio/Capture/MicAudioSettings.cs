namespace Argus.Audio.Capture;

/// <summary>
/// Singleton settings object that controls microphone capture backend selection.
/// Injected into SessionCoordinatorService and read by the UI.
///
/// Change these before starting a session.  They are applied once per session start.
/// </summary>
public sealed class MicAudioSettings
{
    /// <summary>
    /// Which backend to use for microphone capture.
    /// Default is <see cref="MicBackend.WaveIn"/> — the most reliable option
    /// when WASAPI shared-mode delivers silence.
    /// </summary>
    public MicBackend Backend { get; set; } = MicBackend.WaveIn;

    /// <summary>
    /// WaveIn device index (0 = Windows default microphone).
    /// Only used when <see cref="Backend"/> is <see cref="MicBackend.WaveIn"/>.
    /// </summary>
    public int WaveInDeviceNumber { get; set; } = 0;
}
