namespace Argus.AI.Models;

public sealed class ChatResponse
{
    public string Content { get; init; } = string.Empty;
    public string? ModelUsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    public static ChatResponse Error(string message) =>
        new() { IsError = true, ErrorMessage = message };
}
