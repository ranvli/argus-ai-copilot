using Argus.AI.Configuration;
using Argus.AI.Implementations.Ollama;
using Argus.AI.Implementations.OpenAi;
using Argus.AI.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.AI.Resolvers;

/// <summary>
/// Resolves the correct provider implementation for a given workflow + capability
/// by consulting <see cref="AiOptions"/> and constructing instances on first use.
/// Instances are cached per profile name so the same ProviderProfile always
/// returns the same implementation object.
/// </summary>
internal sealed class ModelResolver : IModelResolver
{
    private readonly AiOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ModelResolver> _logger;
    private readonly ILocalTranscriptionModelFactory? _localTxFactory;

    // Cache resolved instances by profile name so HTTP clients are reused.
    private readonly Dictionary<string, object> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public ModelResolver(
        IOptions<AiOptions> options,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        ILocalTranscriptionModelFactory? localTxFactory = null)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ModelResolver>();
        _localTxFactory = localTxFactory;
    }

    public IChatModel ResolveChatModel(AiWorkflow workflow) =>
        Resolve<IChatModel>(workflow, AiCapability.Chat);

    public IVisionModel ResolveVisionModel(AiWorkflow workflow) =>
        Resolve<IVisionModel>(workflow, AiCapability.Vision);

    public ITranscriptionModel ResolveTranscriptionModel(AiWorkflow workflow) =>
        Resolve<ITranscriptionModel>(workflow, AiCapability.Transcription);

    public IEmbeddingModel ResolveEmbeddingModel(AiWorkflow workflow) =>
        Resolve<IEmbeddingModel>(workflow, AiCapability.Embeddings);

    public ITtsModel ResolveTtsModel(AiWorkflow workflow) =>
        Resolve<ITtsModel>(workflow, AiCapability.Tts);

    // ─────────────────────────────────────────────────────────────────────────

    private T Resolve<T>(AiWorkflow workflow, AiCapability capability) where T : class
    {
        var profileName = _options.ResolveProfileName(workflow, capability);
        if (profileName is null)
        {
            _logger.LogError(
                "[ModelResolver] No profile configured for Workflow={Workflow} Capability={Capability}. " +
                "Check ArgusAI:Defaults or ArgusAI:WorkflowMappings in appsettings.json.",
                workflow, capability);
            throw new InvalidOperationException(
                $"No provider profile configured for workflow '{workflow}' / capability '{capability}'. " +
                "Check ArgusAI:Defaults or ArgusAI:WorkflowMappings in appsettings.json.");
        }

        var cacheKey = $"{typeof(T).Name}:{profileName}";

        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return (T)cached;

            var profile = _options.FindProfile(profileName);
            if (profile is null)
            {
                _logger.LogError(
                    "[ModelResolver] Profile '{ProfileName}' not found or Enabled=false. " +
                    "Workflow={Workflow} Capability={Capability}. " +
                    "Verify the profile exists in ArgusAI:Profiles and Enabled=true.",
                    profileName, workflow, capability);
                throw new InvalidOperationException(
                    $"Provider profile '{profileName}' not found or is disabled in ArgusAI:Profiles. " +
                    $"Workflow={workflow} Capability={capability}.");
            }

            _logger.LogInformation(
                "[ModelResolver] Resolved {Capability}/{Workflow} → Profile='{Profile}' Provider={Provider} ModelId={ModelId}",
                capability, workflow, profile.Name, profile.Provider, profile.ModelId);

            var instance = Build<T>(profile);
            _cache[cacheKey] = instance;
            return instance;
        }
    }

    private T Build<T>(ProviderProfile profile) where T : class
    {
        var provider = profile.Provider.ToUpperInvariant();

        return (typeof(T).Name, provider) switch
        {
            (nameof(IChatModel),         "OLLAMA")           => (T)(object)new OllamaChatModel(profile, _httpFactory, _loggerFactory.CreateLogger<OllamaChatModel>()),
            (nameof(IChatModel),         "OPENAI")           => (T)(object)new OpenAiChatModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiChatModel>()),
            (nameof(IVisionModel),       "OPENAI")           => (T)(object)new OpenAiVisionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiVisionModel>()),
            // Transcription: OpenAI Whisper API, or any OpenAI-compatible endpoint
            // (whisper.cpp server, LocalAI, Faster-Whisper-Server, etc.)
            (nameof(ITranscriptionModel),"OPENAI")           => (T)(object)new OpenAiTranscriptionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiTranscriptionModel>()),
            (nameof(ITranscriptionModel),"WHISPER")          => (T)(object)new OpenAiTranscriptionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiTranscriptionModel>()),
            (nameof(ITranscriptionModel),"LOCALAI")          => (T)(object)new OpenAiTranscriptionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiTranscriptionModel>()),
            (nameof(ITranscriptionModel),"OPENAI_COMPATIBLE") => (T)(object)new OpenAiTranscriptionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiTranscriptionModel>()),
            // Fully local Whisper.net transcription — no cloud dependency
            (nameof(ITranscriptionModel),"WHISPERNET")       => BuildLocalWhisper<T>(profile),
            (nameof(IEmbeddingModel),    "OLLAMA")           => (T)(object)new OllamaEmbeddingModel(profile, _httpFactory, _loggerFactory.CreateLogger<OllamaEmbeddingModel>()),
            _ => throw new NotSupportedException(
                $"No implementation for capability '{typeof(T).Name}' with provider '{provider}'. " +
                $"Profile='{profile.Name}' ModelId='{profile.ModelId}' Endpoint='{profile.Endpoint}'.")
        };
    }

    private T BuildLocalWhisper<T>(ProviderProfile profile) where T : class
    {
        if (_localTxFactory is null)
            throw new InvalidOperationException(
                $"Provider=WhisperNet requires ILocalTranscriptionModelFactory to be registered. " +
                $"Call AddArgusTranscription() before AddArgusAI() in your DI setup. " +
                $"Profile='{profile.Name}'");

        return (T)_localTxFactory.Create(profile);
    }
}
