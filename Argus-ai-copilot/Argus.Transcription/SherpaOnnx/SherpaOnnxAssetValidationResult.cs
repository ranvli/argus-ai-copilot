namespace Argus.Transcription.SherpaOnnx;

public sealed class SherpaOnnxAssetValidationResult
{
    public bool IsValid { get; init; }
    public string ProfileRoot { get; init; } = string.Empty;
    public string ProfileJsonPath { get; init; } = string.Empty;
    public bool ProfileJsonExists { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> MissingFiles { get; init; } = [];

    public string ToUserMessage()
    {
        if (IsValid)
            return "SherpaOnnx assets ready.";

        if (MissingFiles.Count == 0)
            return $"SherpaOnnx assets missing. Root='{ProfileRoot}'. Profile='{ProfileJsonPath}'. Reason={Reason}.";

        return $"SherpaOnnx assets missing. Root='{ProfileRoot}'. Missing: {string.Join(", ", MissingFiles)}";
    }
}
