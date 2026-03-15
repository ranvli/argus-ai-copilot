using Argus.AI.Models;

namespace Argus.AI.Providers;

public interface IVisionModel
{
    string ProviderId { get; }
    string ModelId { get; }

    Task<VisionResponse> AnalyzeAsync(VisionRequest request, CancellationToken ct = default);
}
