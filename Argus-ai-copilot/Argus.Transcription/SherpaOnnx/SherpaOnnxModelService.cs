using Argus.Infrastructure.Storage;
using Argus.Transcription.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Argus.AI.Configuration;

namespace Argus.Transcription.SherpaOnnx;

public sealed class SherpaOnnxModelService
{
    public const string DefaultModelId = "omnilingual-offline-ctc";

    private readonly IPathProvider _pathProvider;
    private readonly ILogger<SherpaOnnxModelService> _logger;
    private readonly TranscriptionRuntimeSettings _runtimeSettings;

    public SherpaOnnxModelService(
        IPathProvider pathProvider,
        IOptions<TranscriptionRuntimeSettings> runtimeSettings,
        ILogger<SherpaOnnxModelService> logger)
    {
        _pathProvider = pathProvider;
        _runtimeSettings = runtimeSettings.Value;
        _logger = logger;
    }

    public string GetSherpaModelsRoot()
    {
        var configured = _runtimeSettings.SherpaModelsRoot?.Trim();
        var root = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(_pathProvider.AppDataRoot, "models", "sherpa-onnx");

        Directory.CreateDirectory(root);
        return root;
    }

    public string GetProfileRoot(string modelId)
    {
        var profileRoot = Path.Combine(GetSherpaModelsRoot(), modelId);
        Directory.CreateDirectory(profileRoot);
        return profileRoot;
    }

    public void LogModelRoot(string modelId)
    {
        _logger.LogInformation(
            "[SherpaModelRoot] modelId={ModelId} root={Root}",
            modelId,
            GetProfileRoot(modelId));
    }

    public SherpaOnnxAssetValidationResult ValidateAssets(ProviderProfile profile)
    {
        var root = GetProfileRoot(profile.ModelId);
        var result = SherpaOnnxConfigParser.ValidateAssets(profile, root);

        _logger.LogInformation(
            "[SherpaAssets] root={Root} modelExists={ModelExists} tokensExists={TokensExists}",
            result.ProfileRoot,
            File.Exists(result.ModelPath),
            File.Exists(result.TokensPath));

        foreach (var missing in result.MissingFiles)
        {
            _logger.LogError("[SherpaAssets] missing={Missing}", missing);
        }

        if (!result.IsValid)
        {
            _logger.LogError(
                "[SherpaStartup] validationFailed reason={Reason}",
                result.Reason ?? "unknown");
        }

        return result;
    }

    public SherpaOnnxAssetValidationResult ValidateDefaultAssets()
        => SherpaOnnxConfigParser.ValidateAssets(new Argus.AI.Configuration.ProviderProfile
        {
            Name = "SherpaOnnxLocal",
            Provider = "SherpaOnnx",
            ModelId = DefaultModelId,
            Enabled = true
        }, GetProfileRoot(DefaultModelId));
}
