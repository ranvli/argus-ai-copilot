using Argus.AI.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Argus.AI.Discovery;

/// <summary>
/// Probes Ollama (via HTTP) and OpenAI (via configuration) and returns a
/// <see cref="ProviderDiscoveryResult"/> describing what is available.
/// </summary>
public sealed class ProviderDiscoveryService : IProviderDiscoveryService
{
    private const string DefaultOllamaEndpoint = "http://localhost:11434";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderDiscoveryService> _logger;

    public ProviderDiscoveryService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ProviderDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ProviderDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting provider discovery.");

        var ollamaTask = DiscoverOllamaAsync(cancellationToken);
        var openAiResult = DiscoverOpenAi();

        var (ollamaAvailability, ollamaEndpoint, ollamaModels, ollamaError) = await ollamaTask;

        var result = new ProviderDiscoveryResult
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            OllamaAvailability = ollamaAvailability,
            OllamaEndpoint = ollamaEndpoint,
            OllamaModels = ollamaModels,
            OllamaError = ollamaError,
            OpenAiAvailability = openAiResult.Availability,
            OpenAiKeyPresent = openAiResult.KeyPresent
        };

        _logger.LogInformation(
            "Provider discovery complete. Ollama={OllamaStatus}, OpenAI={OpenAiStatus}, Models={ModelCount}",
            result.OllamaAvailability,
            result.OpenAiAvailability,
            result.AllModels.Count);

        return result;
    }

    // ── Ollama ────────────────────────────────────────────────────────────────

    private async Task<(ProviderAvailability availability, string endpoint,
        IReadOnlyList<DiscoveredModelInfo> models, string? error)>
        DiscoverOllamaAsync(CancellationToken cancellationToken)
    {
        // Resolve endpoint: prefer what's in AiOptions profiles, fall back to default.
        var endpoint = ResolveOllamaEndpoint();

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);

            var response = await client.GetAsync("api/tags", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Ollama /api/tags returned {Status}: {Body}", response.StatusCode, errorBody);
                return (ProviderAvailability.Error, endpoint, [], $"HTTP {(int)response.StatusCode}");
            }

            var tagsResponse = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                cancellationToken: cancellationToken);

            if (tagsResponse?.Models is not { Count: > 0 })
            {
                _logger.LogInformation("Ollama is reachable but has no models installed.");
                return (ProviderAvailability.NoModels, endpoint, [], null);
            }

            var models = tagsResponse.Models
                .Select(m => new DiscoveredModelInfo
                {
                    Provider = "Ollama",
                    ModelId = m.Name,
                    IsLocal = true,
                    IsAvailable = true,
                    Capabilities = InferOllamaCapabilities(m.Name),
                    SizeInBillions = EstimateParamCount(m.Name),
                    DiskSizeBytes = m.Size
                })
                .ToList()
                .AsReadOnly();

            _logger.LogInformation("Ollama: found {Count} model(s).", models.Count);
            return (ProviderAvailability.Available, endpoint, models, null);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Ollama probe timed out at {Endpoint}.", endpoint);
            return (ProviderAvailability.Unreachable, endpoint, [], "Connection timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Ollama probe failed at {Endpoint}: {Message}", endpoint, ex.Message);
            return (ProviderAvailability.Unreachable, endpoint, [], ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error probing Ollama at {Endpoint}.", endpoint);
            return (ProviderAvailability.Error, endpoint, [], ex.Message);
        }
    }

    private string ResolveOllamaEndpoint()
    {
        // Check ArgusAI:Profiles for an Ollama entry
        var profiles = _configuration
            .GetSection($"{AiOptions.SectionName}:Profiles")
            .Get<List<ProviderProfile>>();

        var ollamaProfile = profiles?.FirstOrDefault(p =>
            p.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) && p.Enabled);

        return ollamaProfile?.Endpoint ?? DefaultOllamaEndpoint;
    }

    private static IReadOnlyList<AiCapability> InferOllamaCapabilities(string modelName)
    {
        var caps = new List<AiCapability> { AiCapability.Chat, AiCapability.Embeddings };

        // Vision models typically carry "vision" or "llava" in their name
        if (modelName.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("bakllava", StringComparison.OrdinalIgnoreCase) ||
            modelName.Contains("moondream", StringComparison.OrdinalIgnoreCase))
        {
            caps.Add(AiCapability.Vision);
        }

        return caps.AsReadOnly();
    }

    private static double? EstimateParamCount(string modelName)
    {
        // Try to parse patterns like "llama3:8b", "mistral:7b-instruct", etc.
        var colon = modelName.IndexOf(':');
        var tag = colon >= 0 ? modelName[(colon + 1)..] : modelName;

        foreach (var part in tag.Split('-', '_'))
        {
            if (part.EndsWith("b", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(part[..^1], out var billions))
            {
                return billions;
            }
        }

        return null;
    }

    // ── OpenAI ────────────────────────────────────────────────────────────────

    private (ProviderAvailability Availability, bool KeyPresent) DiscoverOpenAi()
    {
        // Check environment variable first, then configuration
        var keyFromEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var keyFromConfig = _configuration["OpenAI:ApiKey"]
            ?? _configuration["ArgusAI:Profiles:0:ApiKey"];

        var keyPresent = !string.IsNullOrWhiteSpace(keyFromEnv) ||
                         !string.IsNullOrWhiteSpace(keyFromConfig);

        if (!keyPresent)
        {
            _logger.LogInformation("OpenAI: no API key found in environment or configuration.");
            return (ProviderAvailability.NotConfigured, false);
        }

        _logger.LogInformation("OpenAI: API key present (not validated online).");
        return (ProviderAvailability.Available, true);
    }

    // ── Ollama JSON models ────────────────────────────────────────────────────

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelEntry>? Models { get; set; }
    }

    private sealed class OllamaModelEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
