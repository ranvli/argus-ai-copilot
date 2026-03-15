namespace Argus.Audio.Capture;

/// <summary>
/// A fixed-size chunk of raw PCM audio data ready for transcription.
/// Format is always 16-bit signed PCM, mono, 16 kHz — the format Whisper expects.
/// </summary>
public sealed class AudioChunk
{
    /// <summary>Unique id for tracing through the pipeline.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Session this chunk belongs to.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Raw PCM bytes: 16-bit signed, mono, 16 000 Hz.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>When capture of this chunk started (UTC).</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Nominal duration of the audio contained in this chunk.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Source of the audio (microphone / system / mixed).</summary>
    public AudioSource Source { get; init; } = AudioSource.Microphone;
}

/// <summary>Which physical or virtual device produced this audio.</summary>
public enum AudioSource
{
    Microphone,
    SystemAudio,
    Mixed
}
