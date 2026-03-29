namespace Argus.Transcription.SherpaOnnx;

public sealed class SherpaOnnxAssetValidationResult
{
    public bool IsValid { get; init; }
    public string ProfileRoot { get; init; } = string.Empty;
    public string ModelPath { get; init; } = string.Empty;
    public string TokensPath { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public IReadOnlyList<string> MissingFiles { get; init; } = [];
    public string? ExpectedLayoutHint { get; init; }

    public string ToUserMessage()
    {
        if (IsValid)
            return "SherpaOnnx assets ready.";

        if (MissingFiles.Count == 0)
            return $"SherpaOnnx assets missing. Root='{ProfileRoot}'. Model='{ModelPath}'. Tokens='{TokensPath}'. Reason={Reason}. ExpectedLayout={ExpectedLayoutHint}.";

        return $"SherpaOnnx assets missing. Root='{ProfileRoot}'. Missing: {string.Join(", ", MissingFiles)}. ExpectedLayout={ExpectedLayoutHint}.";
    }
}
