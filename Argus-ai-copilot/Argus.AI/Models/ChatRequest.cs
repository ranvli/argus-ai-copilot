namespace Argus.AI.Models;

public sealed class ChatRequest
{
    /// <summary>Full conversation history, ordered oldest-first.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>Optional system instruction prepended before the conversation.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Sampling temperature [0,2]. Null = provider default.</summary>
    public float? Temperature { get; init; }

    /// <summary>Max tokens to generate. Null = provider default.</summary>
    public int? MaxTokens { get; init; }
}

public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
}

public enum ChatRole { System, User, Assistant }
