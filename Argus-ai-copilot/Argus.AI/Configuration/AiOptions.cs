namespace Argus.AI.Configuration;

/// <summary>
/// Root options object bound to the "ArgusAI" configuration section.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "ArgusAI";

    /// <summary>All named provider profiles available in this installation.</summary>
    public List<ProviderProfile> Profiles { get; init; } = [];

    /// <summary>
    /// Fallback profile names to use when a workflow has no explicit mapping
    /// for a capability. Key = capability name, Value = profile name.
    /// </summary>
    public Dictionary<string, string> Defaults { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-workflow overrides. Workflows not listed here use Defaults.</summary>
    public List<WorkflowMapping> WorkflowMappings { get; init; } = [];

    // ── Convenience lookup helpers ────────────────────────────────────────────

    public ProviderProfile? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Enabled);

    public string? ResolveProfileName(AiWorkflow workflow, AiCapability capability)
    {
        var capKey = capability.ToString();

        // 1. Check workflow-level override
        var wfMap = WorkflowMappings.FirstOrDefault(m => m.Workflow == workflow);
        if (wfMap?.CapabilityProfiles.TryGetValue(capKey, out var wfProfile) == true && !string.IsNullOrWhiteSpace(wfProfile))
            return wfProfile;

        // 2. Fall back to global default
        if (Defaults.TryGetValue(capKey, out var defaultProfile) && !string.IsNullOrWhiteSpace(defaultProfile))
            return defaultProfile;

        return null;
    }
}
