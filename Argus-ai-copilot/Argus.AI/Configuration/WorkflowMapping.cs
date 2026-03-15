namespace Argus.AI.Configuration;

/// <summary>
/// Maps a single workflow to the provider profile name to use for each capability.
/// Any capability not listed falls back to the global default in <see cref="AiOptions"/>.
/// </summary>
public sealed class WorkflowMapping
{
    /// <summary>The workflow this mapping applies to.</summary>
    public AiWorkflow Workflow { get; init; }

    /// <summary>
    /// Capability-to-profile-name overrides for this workflow.
    /// Key: capability name (matches <see cref="AiCapability"/> enum names, case-insensitive).
    /// Value: profile name from <see cref="AiOptions.Profiles"/>.
    /// </summary>
    public Dictionary<string, string> CapabilityProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
