namespace Argus.Audio.Capture;

/// <summary>
/// A point-in-time snapshot of the audio pipeline status for UI binding.
/// </summary>
public sealed class AudioStatusSnapshot
{
    public static readonly AudioStatusSnapshot Idle = new();

    public AudioCaptureStatus MicrophoneStatus  { get; init; } = AudioCaptureStatus.Idle;
    public string  MicrophoneDevice             { get; init; } = string.Empty;
    public string? MicrophoneError              { get; init; }
    public MicBackend ActiveMicBackend          { get; init; } = MicBackend.WaveIn;
    public float MicNativeRms                   { get; init; }
    public float MicConvertedRms                { get; init; }

    public AudioCaptureStatus SystemAudioStatus { get; init; } = AudioCaptureStatus.NoDevice;
    public string  SystemAudioDevice            { get; init; } = string.Empty;
    public string? SystemAudioError             { get; init; }
    public float SystemAudioNativeRms            { get; init; }
    public float SystemAudioConvertedRms         { get; init; }

    public TranscriptionPipelineStatus TranscriptionStatus { get; init; } = TranscriptionPipelineStatus.Idle;
    public string? TranscriptionError { get; init; }
    public int PendingChunks { get; init; }
    public int TotalSegments { get; init; }

    public bool TranscriptionConfigured { get; init; }
    public string TranscriptionProvider { get; init; } = string.Empty;
    public string TranscriptionModel { get; init; } = string.Empty;
    public string TranscriptionLanguageMode { get; init; } = string.Empty;
    public DateTimeOffset? LastTranscriptionAt { get; init; }

    public WhisperModelDownloadState WhisperDownloadState { get; init; } = WhisperModelDownloadState.NotApplicable;
    public string WhisperModelPath { get; init; } = string.Empty;
    public SherpaModelProvisioningState SherpaProvisioningState { get; init; } = SherpaModelProvisioningState.NotApplicable;
    public string SherpaModelRoot { get; init; } = string.Empty;
    public SherpaNativeReadinessState SherpaNativeReadinessState { get; init; } = SherpaNativeReadinessState.NotChecked;

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

    public string SystemAudioLevelDisplay
    {
        get
        {
            if (SystemAudioStatus != AudioCaptureStatus.Capturing) return string.Empty;
            var rms    = SystemAudioConvertedRms;
            var filled = Math.Clamp((int)Math.Round(rms * 50), 0, 10);
            var bar    = new string('█', filled) + new string('░', 10 - filled);
            var label  = rms < 0.002f ? "SILENT" : $"RMS {rms:F3}";
            return $"{bar}  {label}";
        }
    }

    public string SystemAudioSignalDebugDisplay =>
        SystemAudioStatus == AudioCaptureStatus.Capturing
            ? $"[WASAPI loopback]  native {SystemAudioNativeRms:F4}  →  conv {SystemAudioConvertedRms:F4}"
            : string.Empty;

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

    public string SherpaProvisioningStateDisplay => SherpaProvisioningState switch
    {
        SherpaModelProvisioningState.NotApplicable => string.Empty,
        SherpaModelProvisioningState.NotChecked    => "Not checked",
        SherpaModelProvisioningState.Provisioning  => "⬇ Provisioning Sherpa model…",
        SherpaModelProvisioningState.Ready         => "✔ Sherpa model ready",
        SherpaModelProvisioningState.Error         => "✘ Sherpa model error",
        _                                          => string.Empty
    };

    public string SherpaNativeReadinessDisplay => SherpaNativeReadinessState switch
    {
        SherpaNativeReadinessState.NotChecked       => "Not checked",
        SherpaNativeReadinessState.PreflightRunning => "Running native preflight…",
        SherpaNativeReadinessState.PreflightPassed  => "Native preflight passed",
        SherpaNativeReadinessState.PreflightFailed  => "Native preflight failed",
        SherpaNativeReadinessState.Ready            => "Ready",
        _                                           => string.Empty
    };
}

public enum TranscriptionPipelineStatus
{
    Idle,
    Transcribing,
    Error,
    NoProvider
}

public enum WhisperModelDownloadState
{
    NotApplicable,
    NotChecked,
    Downloading,
    Ready,
    Failed
}

public enum SherpaModelProvisioningState
{
    NotApplicable,
    NotChecked,
    Provisioning,
    Ready,
    Error
}

public enum SherpaNativeReadinessState
{
    NotChecked,
    PreflightRunning,
    PreflightPassed,
    PreflightFailed,
    Ready
}
