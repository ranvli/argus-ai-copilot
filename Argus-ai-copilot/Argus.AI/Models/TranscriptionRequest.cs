namespace Argus.AI.Models;

public sealed class TranscriptionRequest
{
    /// <summary>Path to the audio file on disk (WAV, MP3, M4A, etc.).</summary>
    public string AudioFilePath { get; init; } = string.Empty;

    /// <summary>BCP-47 language hint, e.g. "en", "es". Null = auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Whether to request word-level timestamps from the provider.</summary>
    public bool WordTimestamps { get; init; }

    /// <summary>Optional speaker diarisation prompt context.</summary>
    public string? Prompt { get; init; }
}
