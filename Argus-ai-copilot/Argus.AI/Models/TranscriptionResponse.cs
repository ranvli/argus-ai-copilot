using Argus.Core.Domain.Entities;

namespace Argus.AI.Models;

public sealed class TranscriptionResponse
{
    /// <summary>Flat transcript text.</summary>
    public string FullText { get; init; } = string.Empty;

    /// <summary>Timed and optionally diarised segments mapped to domain entities.</summary>
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = [];

    public string? ModelUsed { get; init; }
    public string? DetectedLanguage { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    public static TranscriptionResponse Error(string message) =>
        new() { IsError = true, ErrorMessage = message };
}
