namespace Argus.Audio.Capture;

/// <summary>
/// Abstracts a single audio capture source (microphone or system audio).
/// Implementations collect audio into <see cref="AudioChunk"/> objects and
/// raise <see cref="ChunkReady"/> when a full chunk is available.
/// </summary>
public interface IAudioCaptureSource
{
    /// <summary>Human-readable name, e.g. "Microphone" or "System Audio (WASAPI loopback)".</summary>
    string DisplayName { get; }

    /// <summary>Current operational status of this capture source.</summary>
    AudioCaptureStatus Status { get; }

    /// <summary>
    /// Raised on a background thread every time a complete audio chunk is ready.
    /// Subscribers must not block this event for long.
    /// </summary>
    event EventHandler<AudioChunk>? ChunkReady;

    /// <summary>
    /// Opens the capture device and begins collecting audio.
    /// </summary>
    /// <param name="sessionId">The session that owns this capture run.</param>
    /// <param name="ct">Cancellation token; cancelling stops capture cleanly.</param>
    Task StartAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Stops capture and flushes any partial chunk.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Temporarily suspends sample collection without closing the device.</summary>
    void Pause();

    /// <summary>Resumes a paused capture source.</summary>
    void Resume();
}
