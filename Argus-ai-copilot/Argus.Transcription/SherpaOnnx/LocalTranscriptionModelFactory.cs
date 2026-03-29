using Argus.AI.Configuration;
using Argus.AI.Providers;
using Argus.Transcription.Whisper;
using Microsoft.Extensions.Logging;

namespace Argus.Transcription.SherpaOnnx;

internal sealed class LocalTranscriptionModelFactory : ILocalTranscriptionModelFactory
{
    private readonly WhisperModelService _whisperModelService;
    private readonly SherpaOnnxModelService _sherpaModelService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, ITranscriptionModel> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public LocalTranscriptionModelFactory(
        WhisperModelService whisperModelService,
        SherpaOnnxModelService sherpaModelService,
        ILoggerFactory loggerFactory)
    {
        _whisperModelService = whisperModelService;
        _sherpaModelService = sherpaModelService;
        _loggerFactory = loggerFactory;
    }

    public bool CanCreate(ProviderProfile profile)
        => profile.Provider.Equals("WhisperNet", StringComparison.OrdinalIgnoreCase)
           || profile.Provider.Equals("SherpaOnnx", StringComparison.OrdinalIgnoreCase);

    public ITranscriptionModel Create(ProviderProfile profile)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(profile.Name, out var cached))
                return cached;

            ITranscriptionModel instance = profile.Provider.ToUpperInvariant() switch
            {
                "WHISPERNET" => new WhisperLocalTranscriptionModel(
                    profile,
                    _whisperModelService,
                    _loggerFactory.CreateLogger<WhisperLocalTranscriptionModel>()),

                "SHERPAONNX" => new SherpaOnnxLocalTranscriptionModel(
                    profile,
                    _sherpaModelService,
                    _loggerFactory.CreateLogger<SherpaOnnxLocalTranscriptionModel>()),

                _ => throw new NotSupportedException($"Unsupported local transcription provider '{profile.Provider}'.")
            };

            _cache[profile.Name] = instance;
            return instance;
        }
    }
}
