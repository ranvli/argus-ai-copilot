namespace Argus.AI.Models;

public sealed class TtsResponse
{
    /// <summary>Raw audio bytes in the format requested.</summary>
    public byte[] AudioBytes { get; init; } = [];

    /// <summary>MIME type of the audio, e.g. "audio/mpeg".</summary>
    public string MimeType { get; init; } = "audio/mpeg";

    public string? ModelUsed { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    public static TtsResponse Error(string message) =>
        new() { IsError = true, ErrorMessage = message };
}
