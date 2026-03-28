namespace Argus.Audio.Capture;

/// <summary>
/// Selects which OS audio API is used to capture microphone input.
/// </summary>
public enum MicBackend
{
    /// <summary>
    /// Probe both WASAPI and WaveIn for 1 second each and choose whichever
    /// produces a non-zero RMS signal. Falls back to WaveIn if both are silent.
    /// </summary>
    Auto,

    /// <summary>
    /// Windows Audio Session API (WASAPI) shared-mode capture.
    /// Uses NAudio's WasapiCapture with the managed conversion chain.
    /// May deliver silence if the device is held in exclusive mode by another app.
    /// </summary>
    Wasapi,

    /// <summary>
    /// Legacy WinMM WaveIn (MME) capture via NAudio's WaveInEvent.
    /// Bypasses the WASAPI shared-mode graph and is immune to exclusive-mode lock-out.
    /// Uses the device's native 16-bit PCM format directly — no float32 conversion needed.
    /// </summary>
    WaveIn
}
