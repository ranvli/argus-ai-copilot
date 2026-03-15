namespace Argus.AI.Models;

public sealed class VisionResponse
{
    public string Description { get; init; } = string.Empty;
    public string? ModelUsed { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    public static VisionResponse Error(string message) =>
        new() { IsError = true, ErrorMessage = message };
}
