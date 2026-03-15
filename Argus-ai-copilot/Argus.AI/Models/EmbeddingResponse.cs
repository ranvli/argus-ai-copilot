namespace Argus.AI.Models;

public sealed class EmbeddingResponse
{
    /// <summary>
    /// One float vector per input text, in the same order as the request.
    /// </summary>
    public IReadOnlyList<float[]> Vectors { get; init; } = [];

    public string? ModelUsed { get; init; }
    public int Dimensions => Vectors.Count > 0 ? Vectors[0].Length : 0;
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    public static EmbeddingResponse Error(string message) =>
        new() { IsError = true, ErrorMessage = message };
}
