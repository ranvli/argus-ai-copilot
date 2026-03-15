namespace Argus.AI.Discovery;

/// <summary>
/// Probes all configured AI providers and returns a point-in-time snapshot
/// of what is reachable and what models are available.
/// </summary>
public interface IProviderDiscoveryService
{
    /// <summary>
    /// Runs a full discovery pass and returns a fresh result.
    /// This makes live network calls; cache the result if repeated access is needed.
    /// </summary>
    Task<ProviderDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
}
