namespace Argus.AI.Models;

public sealed class EmbeddingRequest
{
    /// <summary>Text passages to embed. Each entry produces one embedding vector.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];
}
