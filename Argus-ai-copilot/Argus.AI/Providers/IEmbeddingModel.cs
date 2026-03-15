using Argus.AI.Models;

namespace Argus.AI.Providers;

public interface IEmbeddingModel
{
    string ProviderId { get; }
    string ModelId { get; }

    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}
