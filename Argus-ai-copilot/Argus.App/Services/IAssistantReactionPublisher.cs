using Argus.Transcription.Intent;

namespace Argus.App.Services;

/// <summary>
/// Snapshot of the assistant's latest reaction.
/// </summary>
public sealed class AssistantReactionSnapshot
{
    public static readonly AssistantReactionSnapshot Empty = new();

    /// <summary>Wake/help phrase that triggered this reaction.</summary>
    public string WakePhrase { get; init; } = string.Empty;

    /// <summary>Detected intent category.</summary>
    public DetectedIntent Intent { get; init; } = DetectedIntent.None;

    /// <summary>The assistant's suggestion/reply text.</summary>
    public string Suggestion { get; init; } = string.Empty;

    /// <summary>Whether the last reaction attempt failed.</summary>
    public bool IsError { get; init; }

    /// <summary>Error message when <see cref="IsError"/> is true.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>UTC time of this reaction.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>
/// Publishes assistant reaction state changes to UI subscribers.
/// Implemented by <see cref="AssistantReactionService"/>.
/// </summary>
public interface IAssistantReactionPublisher
{
    AssistantReactionSnapshot Current { get; }

    /// <summary>Raised on a thread-pool thread whenever a new reaction is ready.</summary>
    event EventHandler<AssistantReactionSnapshot>? ReactionChanged;
}
