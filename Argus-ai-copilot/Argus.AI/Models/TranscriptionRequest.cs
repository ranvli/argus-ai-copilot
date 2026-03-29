namespace Argus.AI.Models;

public sealed class TranscriptionRequest
{
    /// <summary>Path to the audio file on disk (WAV, MP3, M4A, etc.).</summary>
    public string AudioFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Optional in-memory mono PCM16 16kHz audio payload for embedded/local STT backends.
    /// When present, backends should prefer this over <see cref="AudioFilePath"/>.
    /// </summary>
    public ReadOnlyMemory<byte> AudioPcm16 { get; init; }

    /// <summary>
    /// Sample rate for <see cref="AudioPcm16"/>. Defaults to 16kHz.
    /// </summary>
    public int AudioSampleRate { get; init; } = 16_000;

    /// <summary>
    /// Number of channels for <see cref="AudioPcm16"/>. Defaults to mono.
    /// </summary>
    public int AudioChannels { get; init; } = 1;

    /// <summary>
    /// Indicates whether the request should stay fully in-memory when the backend supports it.
    /// </summary>
    public bool PreferInMemoryAudio { get; init; }

    /// <summary>BCP-47 language hint, e.g. "en", "es". Null = auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Whether to request word-level timestamps from the provider.</summary>
    public bool WordTimestamps { get; init; }

    /// <summary>Optional speaker diarisation prompt context.</summary>
    public string? Prompt { get; init; }
}
