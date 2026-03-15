using Argus.AI.Configuration;
using Argus.AI.Selection;

namespace Argus.App.Configuration;

/// <summary>
/// Application-level routing preferences, bound to the "Routing" configuration section.
/// </summary>
public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    /// <summary>Global default routing mode used when no per-workflow override is set.</summary>
    public RoutingMode Mode { get; set; } = RoutingMode.Automatic;

    /// <summary>
    /// Optional per-workflow overrides.
    /// Key = <see cref="AiWorkflow"/> name; Value = <see cref="RoutingMode"/> name.
    /// Parsed at runtime; unrecognised values fall back to <see cref="Mode"/>.
    /// </summary>
    public Dictionary<string, string> WorkflowOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective <see cref="RoutingMode"/> for a specific workflow,
    /// applying the per-workflow override if configured.
    /// </summary>
    public RoutingMode ResolveMode(AiWorkflow workflow)
    {
        if (WorkflowOverrides.TryGetValue(workflow.ToString(), out var raw) &&
            Enum.TryParse<RoutingMode>(raw, ignoreCase: true, out var overrideMode))
        {
            return overrideMode;
        }

        return Mode;
    }
}
