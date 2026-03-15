using Argus.AI.Configuration;
using Argus.AI.Providers;
using Microsoft.Extensions.Logging;

namespace Argus.Transcription.Whisper;

/// <summary>
/// Implements <see cref="ILocalTranscriptionModelFactory"/> so that
/// <c>ModelResolver</c> in Argus.AI can create <see cref="WhisperLocalTranscriptionModel"/>
/// instances without taking a direct reference on Argus.Transcription.
///
/// Registered as a singleton in <c>TranscriptionServiceExtensions.AddArgusTranscription()</c>.
/// </summary>
internal sealed class WhisperTranscriptionModelFactory : ILocalTranscriptionModelFactory
{
    private readonly WhisperModelService _modelService;
    private readonly ILoggerFactory _loggerFactory;

    // Cache one instance per profile name to reuse the same WhisperFactory
    private readonly Dictionary<string, ITranscriptionModel> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _lock = new();

    public WhisperTranscriptionModelFactory(
        WhisperModelService modelService,
        ILoggerFactory loggerFactory)
    {
        _modelService = modelService;
        _loggerFactory = loggerFactory;
    }

    public ITranscriptionModel Create(ProviderProfile profile)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(profile.Name, out var cached))
                return cached;

            var instance = new WhisperLocalTranscriptionModel(
                profile,
                _modelService,
                _loggerFactory.CreateLogger<WhisperLocalTranscriptionModel>());

            _cache[profile.Name] = instance;
            return instance;
        }
    }
}
