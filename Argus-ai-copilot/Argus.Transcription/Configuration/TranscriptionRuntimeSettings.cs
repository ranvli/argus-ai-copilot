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

    /// <summary>
    /// Enables automatic provisioning of sherpa-onnx model assets at startup.
    /// </summary>
    public bool EnableSherpaAutoProvisioning { get; set; } = true;

    /// <summary>
    /// Optional override URL for the sherpa-onnx omnilingual model package.
    /// When empty, the built-in official release URL is used.
    /// </summary>
    public string SherpaModelPackageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Allows in-process Sherpa decode once the out-of-process native preflight has passed.
    /// Useful for debugging until the final isolated decode architecture lands.
    /// </summary>
    public bool EnableSherpaInProcessDecodeAfterPreflight { get; set; } = true;

    /// <summary>
    /// Minimum PCM16 RMS treated as speech-like by the microphone staging policy.
    /// </summary>
    public float MicLowActivityRmsThreshold { get; set; } = 0.0005f;

    /// <summary>
    /// Minimum PCM16 peak treated as speech-like by the microphone staging policy.
    /// </summary>
    public float MicLowActivityPeakThreshold { get; set; } = 0.0020f;

    /// <summary>
    /// Low-latency microphone chunk duration used for Sherpa speech transcription.
    /// </summary>
    public int SherpaChunkDurationMs { get; set; } = 900;

    /// <summary>
    /// When true, Sherpa microphone chunks are sent immediately unless the signal is clearly dead.
    /// </summary>
    public bool EnableSherpaLowLatencyMode { get; set; } = true;

    /// <summary>
    /// Active Sherpa model family. Supported values: omnilingual_offline_ctc, wenet_ctc, zipformer_ctc, sense_voice.
    /// </summary>
    public string SherpaModelFamily { get; set; } = "omnilingual_offline_ctc";

    /// <summary>
    /// File name of the primary Sherpa model under the profile root.
    /// </summary>
    public string SherpaModelFileName { get; set; } = "model.int8.onnx";
}
