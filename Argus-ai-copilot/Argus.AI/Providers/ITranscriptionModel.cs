using Argus.AI.Models;

namespace Argus.AI.Providers;

public interface ITranscriptionModel
{
    string ProviderId { get; }
    string ModelId { get; }

    Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default);
}
