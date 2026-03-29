namespace Argus.Transcription.Configuration;

public sealed class TranscriptionRuntimeSettings
{
    public const string SectionName = "ArgusAI:Transcription";

    /// <summary>
    /// Optional forced speech-to-text language hint, e.g. "es".
    /// When set, the transcription pipeline uses this instead of auto-detect / session lock.
    /// </summary>
    public string? ForcedLanguage { get; set; }

    /// <summary>
    /// Preferred Whisper runtime backend. Supported values: auto, cuda, cpu.
    /// </summary>
    public string RuntimePreference { get; set; } = "auto";

    /// <summary>
    /// Number of meaningful chunks to probe before automatic language lock can stabilize.
    /// </summary>
    public int AutoLanguageProbeChunkCount { get; set; } = 3;

    /// <summary>
    /// Enables the automatic language probe/stabilization path when forced language is not active.
    /// Disabled by default while forced-language diagnosis is in use.
    /// </summary>
    public bool EnableAutoLanguageProbe { get; set; } = false;

    /// <summary>
    /// Enables partial transcript emission for streaming/local backends that support it.
    /// </summary>
    public bool EnablePartialTranscripts { get; set; } = true;

    /// <summary>
    /// Root folder for embedded sherpa-onnx models.
    /// </summary>
    public string SherpaModelsRoot { get; set; } = string.Empty;
}
