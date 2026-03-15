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

    // Cache resolved instances by profile name so HTTP clients are reused.
    private readonly Dictionary<string, object> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public ModelResolver(
        IOptions<AiOptions> options,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
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
        var profileName = _options.ResolveProfileName(workflow, capability)
            ?? throw new InvalidOperationException(
                $"No provider profile configured for workflow '{workflow}' / capability '{capability}'. " +
                "Check ArgusAI:Defaults or ArgusAI:WorkflowMappings in appsettings.json.");

        var cacheKey = $"{typeof(T).Name}:{profileName}";

        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return (T)cached;

            var profile = _options.FindProfile(profileName)
                ?? throw new InvalidOperationException(
                    $"Provider profile '{profileName}' not found or is disabled in ArgusAI:Profiles.");

            var instance = Build<T>(profile);
            _cache[cacheKey] = instance;
            return instance;
        }
    }

    private T Build<T>(ProviderProfile profile) where T : class
    {
        var provider = profile.Provider;

        return (typeof(T).Name, provider.ToUpperInvariant()) switch
        {
            (nameof(IChatModel),         "OLLAMA") => (T)(object)new OllamaChatModel(profile, _httpFactory, _loggerFactory.CreateLogger<OllamaChatModel>()),
            (nameof(IChatModel),         "OPENAI") => (T)(object)new OpenAiChatModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiChatModel>()),
            (nameof(IVisionModel),       "OPENAI") => (T)(object)new OpenAiVisionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiVisionModel>()),
            (nameof(ITranscriptionModel),"OPENAI") => (T)(object)new OpenAiTranscriptionModel(profile, _httpFactory, _loggerFactory.CreateLogger<OpenAiTranscriptionModel>()),
            (nameof(IEmbeddingModel),    "OLLAMA") => (T)(object)new OllamaEmbeddingModel(profile, _httpFactory, _loggerFactory.CreateLogger<OllamaEmbeddingModel>()),
            _ => throw new NotSupportedException(
                $"No implementation for capability '{typeof(T).Name}' with provider '{provider}'.")
        };
    }
}
