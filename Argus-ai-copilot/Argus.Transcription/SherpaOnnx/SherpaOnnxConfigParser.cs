using System.Text.Json;
using Argus.AI.Configuration;

namespace Argus.Transcription.SherpaOnnx;

internal static class SherpaOnnxConfigParser
{
    private const string ExpectedLayoutHint = "Place a sherpa-onnx model folder containing the configured model file and tokens.txt under the configured SherpaModelsRoot or %LocalAppData%\\ArgusAI\\models\\sherpa-onnx\\<ModelId>.";

    public static SherpaOnnxAssetValidationResult ValidateAssets(ProviderProfile profile, string profileRoot, string modelFileName)
    {
        var modelPath = Path.Combine(profileRoot, modelFileName);
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

    public static SherpaOnnxBackendConfig Parse(ProviderProfile profile, string profileRoot, string modelFamily, string modelFileName)
    {
        var modelPath = Path.Combine(profileRoot, modelFileName);
        var tokensPath = Path.Combine(profileRoot, "tokens.txt");
        if (!File.Exists(modelPath) || !File.Exists(tokensPath))
            throw new FileNotFoundException(
                $"Sherpa omnilingual model files were not found for '{profile.Name}'. Expected '{modelPath}' and '{tokensPath}'.");

        return modelFamily.Trim().ToLowerInvariant() switch
        {
            "omnilingual_offline_ctc" => new SherpaOnnxBackendConfig(
                Family: "omnilingual_offline_ctc",
                Provider: "cpu",
                NumThreads: Environment.ProcessorCount,
                Debug: false,
                Tokens: tokensPath,
                ModelType: string.Empty,
                ModelingUnit: "cjkchar",
                BpeVocab: string.Empty,
                Model: modelPath),

            "wenet_ctc" => new SherpaOnnxBackendConfig(
                Family: "wenet_ctc",
                Provider: "cpu",
                NumThreads: Environment.ProcessorCount,
                Debug: false,
                Tokens: tokensPath,
                ModelType: string.Empty,
                ModelingUnit: string.Empty,
                BpeVocab: string.Empty,
                Model: modelPath),

            "zipformer_ctc" => new SherpaOnnxBackendConfig(
                Family: "zipformer_ctc",
                Provider: "cpu",
                NumThreads: Environment.ProcessorCount,
                Debug: false,
                Tokens: tokensPath,
                ModelType: string.Empty,
                ModelingUnit: string.Empty,
                BpeVocab: string.Empty,
                Model: modelPath),

            "sense_voice" => new SherpaOnnxBackendConfig(
                Family: "sense_voice",
                Provider: "cpu",
                NumThreads: Environment.ProcessorCount,
                Debug: false,
                Tokens: tokensPath,
                ModelType: string.Empty,
                ModelingUnit: string.Empty,
                BpeVocab: string.Empty,
                Model: modelPath),

            _ => throw new InvalidOperationException($"Unsupported Sherpa model family '{modelFamily}'.")
        };
    }
}
