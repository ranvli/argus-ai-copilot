namespace Argus.AI.Providers;

public interface IChatModel
{
    string ModelId { get; }

    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);
}
