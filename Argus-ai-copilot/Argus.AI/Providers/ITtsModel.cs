using Argus.AI.Models;

namespace Argus.AI.Providers;

public interface ITtsModel
{
    string ProviderId { get; }
    string ModelId { get; }

    Task<TtsResponse> SynthesizeAsync(TtsRequest request, CancellationToken ct = default);
}
