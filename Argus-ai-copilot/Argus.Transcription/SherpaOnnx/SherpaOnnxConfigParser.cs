using System.Text.Json;
using Argus.AI.Configuration;

namespace Argus.Transcription.SherpaOnnx;

internal static class SherpaOnnxConfigParser
{
    private const string ExpectedLayoutHint = "Place an official sherpa-onnx omnilingual model folder containing model.int8.onnx and tokens.txt under the configured SherpaModelsRoot or %LocalAppData%\\ArgusAI\\models\\sherpa-onnx\\<ModelId>.";

    public static SherpaOnnxAssetValidationResult ValidateAssets(ProviderProfile profile, string profileRoot)
    {
        var modelPath = Path.Combine(profileRoot, "model.int8.onnx");
        var tokensPath = Path.Combine(profileRoot, "tokens.txt");
        var missing = new List<string>();

        if (!File.Exists(modelPath))
            missing.Add(modelPath);

        if (!File.Exists(tokensPath))
            missing.Add(tokensPath);

        return new SherpaOnnxAssetValidationResult
        {
            IsValid = missing.Count == 0,
            ProfileRoot = profileRoot,
            ModelPath = modelPath,
            TokensPath = tokensPath,
            Reason = missing.Count == 0 ? "ok" : "required_model_files_missing",
            MissingFiles = missing,
            ExpectedLayoutHint = ExpectedLayoutHint
        };
    }

    public static SherpaOnnxBackendConfig Parse(ProviderProfile profile, string profileRoot)
    {
        var modelPath = Path.Combine(profileRoot, "model.int8.onnx");
        var tokensPath = Path.Combine(profileRoot, "tokens.txt");
        if (!File.Exists(modelPath) || !File.Exists(tokensPath))
            throw new FileNotFoundException(
                $"Sherpa omnilingual model files were not found for '{profile.Name}'. Expected '{modelPath}' and '{tokensPath}'.");

        return new SherpaOnnxBackendConfig(
            Provider: "cpu",
            NumThreads: Environment.ProcessorCount,
            Debug: false,
            Tokens: tokensPath,
            ModelType: "omnilingual_ctc",
            ModelingUnit: "bpe",
            BpeVocab: string.Empty,
            Model: modelPath);
    }
}
