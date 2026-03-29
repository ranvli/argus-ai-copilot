namespace Argus.Audio.Capture;

/// <summary>
/// A point-in-time snapshot of the audio pipeline status for UI binding.
/// </summary>
public sealed class AudioStatusSnapshot
{
    public static readonly AudioStatusSnapshot Idle = new();

    // ── Microphone ────────────────────────────────────────────────────────────

    public AudioCaptureStatus MicrophoneStatus  { get; init; } = AudioCaptureStatus.Idle;
    public string  MicrophoneDevice             { get; init; } = string.Empty;
    public string? MicrophoneError              { get; init; }

    /// <summary>Which backend is actively capturing microphone audio.</summary>
    public MicBackend ActiveMicBackend { get; init; } = MicBackend.WaveIn;

    /// <summary>RMS of the most-recent native (pre-conversion) buffer. 0 when idle.</summary>
    public float MicNativeRms    { get; init; }

    /// <summary>RMS of the most-recent converted PCM16 chunk sent to Whisper. 0 when idle.</summary>
    public float MicConvertedRms { get; init; }

    // ── System audio ──────────────────────────────────────────────────────────

    public AudioCaptureStatus SystemAudioStatus { get; init; } = AudioCaptureStatus.NoDevice;
    public string  SystemAudioDevice            { get; init; } = string.Empty;
    public string? SystemAudioError             { get; init; }

    // ── Transcription ─────────────────────────────────────────────────────────

    public TranscriptionPipelineStatus TranscriptionStatus { get; init; } = TranscriptionPipelineStatus.Idle;
    public string? TranscriptionError { get; init; }

    /// <summary>Number of audio chunks queued, waiting to be sent to the transcription provider.</summary>
    public int PendingChunks { get; init; }

    /// <summary>Total number of transcript segments produced this session.</summary>
    public int TotalSegments { get; init; }

    // ── Transcription provider diagnostics ───────────────────────────────────

    /// <summary>
    /// Whether a transcription provider was successfully resolved at pipeline start.
    /// False means every audio chunk will be dropped — UI should warn the user.
    /// </summary>
    public bool TranscriptionConfigured { get; init; }

    /// <summary>Provider name used for transcription, e.g. "OpenAI", "WhisperNet".</summary>
    public string TranscriptionProvider { get; init; } = string.Empty;

    /// <summary>Model ID used for transcription, e.g. "base.en".</summary>
    public string TranscriptionModel { get; init; } = string.Empty;

    /// <summary>Human-readable transcription language mode, e.g. forced/es, locked/en, or auto.</summary>
    public string TranscriptionLanguageMode { get; init; } = string.Empty;

    /// <summary>UTC timestamp of the last successfully transcribed chunk.</summary>
    public DateTimeOffset? LastTranscriptionAt { get; init; }

    /// <summary>Download / readiness state of the local Whisper model file.</summary>
    public WhisperModelDownloadState WhisperDownloadState { get; init; } = WhisperModelDownloadState.NotApplicable;

    /// <summary>Full path to the local Whisper GGML model file, or empty when not applicable.</summary>
    public string WhisperModelPath { get; init; } = string.Empty;

    // ── Display helpers ───────────────────────────────────────────────────────

    public string MicrophoneStatusDisplay => MicrophoneStatus switch
    {
        AudioCaptureStatus.Capturing   => MicrophoneDevice.Length > 0
                                            ? $"● Capturing  — {MicrophoneDevice}"
                                            : "● Capturing",
        AudioCaptureStatus.Paused      => "⏸ Paused",
        AudioCaptureStatus.DeviceError => $"⚠ {MicrophoneError ?? "device error"}",
        AudioCaptureStatus.NoDevice    => "⚠ No microphone found",
        _                              => "Idle"
    };

    /// <summary>
    /// A compact level meter string for the UI, e.g. "█████░░░░░  RMS 0.082".
    /// Returns empty string when not capturing.
    /// </summary>
    public string MicLevelDisplay
    {
        get
        {
            if (MicrophoneStatus != AudioCaptureStatus.Capturing) return string.Empty;
            var rms    = MicConvertedRms;
            var filled = Math.Clamp((int)Math.Round(rms * 50), 0, 10);
            var bar    = new string('█', filled) + new string('░', 10 - filled);
            var label  = rms < 0.002f ? "SILENT" : $"RMS {rms:F3}";
            return $"{bar}  {label}";
        }
    }

    /// <summary>
    /// One-line native vs converted RMS summary for debugging.
    /// </summary>
    public string MicSignalDebugDisplay
    {
        get
        {
            if (MicrophoneStatus != AudioCaptureStatus.Capturing) return string.Empty;
            return $"[{ActiveMicBackend}]  native {MicNativeRms:F4}  →  conv {MicConvertedRms:F4}";
        }
    }

    public string SystemAudioStatusDisplay => SystemAudioStatus switch
    {
        AudioCaptureStatus.Capturing   => SystemAudioDevice.Length > 0
                                            ? $"● Capturing  — {SystemAudioDevice}"
                                            : "● Capturing",
        AudioCaptureStatus.Paused      => "⏸ Paused",
        AudioCaptureStatus.DeviceError => $"⚠ {SystemAudioError ?? "device error"}",
        AudioCaptureStatus.NoDevice    => "Not available",
        _                              => "Idle"
    };

    public string TranscriptionStatusDisplay => TranscriptionStatus switch
    {
        TranscriptionPipelineStatus.Transcribing => $"⚙ Transcribing  ({PendingChunks} queued)",
        TranscriptionPipelineStatus.Idle         => PendingChunks > 0
                                                        ? $"Idle  ({PendingChunks} queued)"
                                                        : "Idle",
        TranscriptionPipelineStatus.Error        => $"⚠ {TranscriptionError ?? "unknown error"}",
        TranscriptionPipelineStatus.NoProvider   => "⚠ No provider configured — audio is not being transcribed",
        _                                        => "Idle"
    };

    public string TranscriptionProviderDisplay =>
        TranscriptionConfigured
            ? $"{TranscriptionProvider} / {TranscriptionModel}"
            : "Not configured";

    public string TranscriptionLanguageModeDisplay =>
        string.IsNullOrWhiteSpace(TranscriptionLanguageMode)
            ? "auto"
            : TranscriptionLanguageMode;

    public string WhisperDownloadStateDisplay => WhisperDownloadState switch
    {
        WhisperModelDownloadState.NotApplicable => string.Empty,
        WhisperModelDownloadState.NotChecked    => "Not checked",
        WhisperModelDownloadState.Downloading   => "⬇ Downloading model…",
        WhisperModelDownloadState.Ready         => "✔ Model ready",
        WhisperModelDownloadState.Failed        => "✘ Download failed",
        _                                       => string.Empty
    };
}

/// <summary>Transcription pipeline operational state.</summary>
public enum TranscriptionPipelineStatus
{
    Idle,
    Transcribing,
    Error,
    NoProvider
}

/// <summary>Download / readiness state of a local Whisper GGML model file.</summary>
public enum WhisperModelDownloadState
{
    /// <summary>Not applicable — provider is not WhisperNet.</summary>
    NotApplicable,
    /// <summary>Model file has not yet been checked.</summary>
    NotChecked,
    /// <summary>Model file is currently being downloaded.</summary>
    Downloading,
    /// <summary>Model file is present and the factory is initialised.</summary>
    Ready,
    /// <summary>Download or initialisation failed.</summary>
    Failed
}
