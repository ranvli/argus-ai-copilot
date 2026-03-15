using System.Text;
using System.Text.Json;
using Argus.AI.Configuration;
using Argus.AI.Implementations;
using Argus.AI.Models;
using Argus.AI.Providers;
using Microsoft.Extensions.Logging;

namespace Argus.AI.Implementations.Ollama;

/// <summary>
/// Ollama embedding using the /api/embed endpoint.
/// </summary>
internal sealed class OllamaEmbeddingModel : ProviderBase, IEmbeddingModel
{
    private readonly ILogger<OllamaEmbeddingModel> _logger;

    public OllamaEmbeddingModel(ProviderProfile profile, IHttpClientFactory httpFactory, ILogger<OllamaEmbeddingModel> logger)
        : base(profile, httpFactory, HttpClientNames.Ollama)
    {
        _logger = logger;
    }

    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
    {
        if (request.Inputs.Count == 0)
            return new EmbeddingResponse { ModelUsed = ModelId };

        try
        {
            var body = new { model = ModelId, input = request.Inputs };
            using var response = await Http.PostAsync(
                $"{Profile.Endpoint!.TrimEnd('/')}/api/embed",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var embeddings = doc.RootElement.GetProperty("embeddings");

            var vectors = new List<float[]>();
            foreach (var item in embeddings.EnumerateArray())
            {
                var vec = item.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                vectors.Add(vec);
            }

            return new EmbeddingResponse { Vectors = vectors, ModelUsed = ModelId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OllamaEmbeddingModel.EmbedAsync failed");
            return EmbeddingResponse.Error(ex.Message);
        }
    }
}
