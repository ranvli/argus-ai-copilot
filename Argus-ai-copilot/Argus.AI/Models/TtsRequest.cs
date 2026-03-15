namespace Argus.AI.Models;

public sealed class TtsRequest
{
    public string Text { get; init; } = string.Empty;

    /// <summary>Voice identifier — provider-specific, e.g. "alloy", "nova".</summary>
    public string? VoiceId { get; init; }

    /// <summary>Desired audio format, e.g. "mp3", "wav", "opus".</summary>
    public string Format { get; init; } = "mp3";

    /// <summary>Playback speed multiplier [0.25, 4.0]. Null = provider default.</summary>
    public float? Speed { get; init; }
}
