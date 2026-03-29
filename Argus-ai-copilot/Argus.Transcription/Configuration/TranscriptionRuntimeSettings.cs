namespace Argus.Transcription.Configuration;

public sealed class TranscriptionRuntimeSettings
{
    public const string SectionName = "ArgusAI:Transcription";

    /// <summary>
    /// Optional forced speech-to-text language hint, e.g. "es".
    /// When set, the transcription pipeline uses this instead of auto-detect / session lock.
    /// </summary>
    public string? ForcedLanguage { get; set; }
}
