using Argus.AI.Discovery;

namespace Argus.AI.Selection;

/// <summary>
/// Chooses the best available model for each workflow and capability combination,
/// respecting the active <see cref="RoutingMode"/>.
/// </summary>
public interface IModelSelectionService
{
    /// <summary>
    /// Produces the effective routing decisions for all workflows given the
    /// current discovery snapshot and the requested routing mode.
    /// </summary>
    EffectiveRoutingResult Select(ProviderDiscoveryResult discovery, RoutingMode mode);
}
