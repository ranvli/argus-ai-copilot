using Argus.Infrastructure.Storage;
using Argus.Transcription.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Transcription.SherpaOnnx;

public sealed class SherpaOnnxModelService
{
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
}
