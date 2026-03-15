using Argus.AI.Models;

namespace Argus.AI.Providers;

public interface IChatModel
{
    string ProviderId { get; }
    string ModelId { get; }

    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct = default);
}
