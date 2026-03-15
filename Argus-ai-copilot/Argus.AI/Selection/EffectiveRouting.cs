using Argus.AI.Configuration;
using Argus.AI.Discovery;

namespace Argus.AI.Selection;

/// <summary>
/// The resolved model assignment for a single workflow + capability pair.
/// </summary>
public sealed class EffectiveCapabilityAssignment
{
    public AiCapability Capability { get; init; }

    /// <summary>The model that will be used, or null if none is available.</summary>
    public DiscoveredModelInfo? Model { get; init; }

    public bool IsAvailable => Model is not null;

    /// <summary>Human-readable reason when no model could be assigned.</summary>
    public string? UnavailableReason { get; init; }

    public string DisplayText =>
        Model is not null
            ? $"{Model.Provider} / {Model.ModelId}"
            : UnavailableReason ?? "No model available";
}

/// <summary>
/// Effective routing decisions for a single workflow across all its required capabilities.
/// </summary>
public sealed class EffectiveWorkflowRouting
{
    public AiWorkflow Workflow { get; init; }

    public IReadOnlyList<EffectiveCapabilityAssignment> Assignments { get; init; } = [];

    /// <summary>True when every required capability has a model assigned.</summary>
    public bool IsFullyAvailable => Assignments.All(a => a.IsAvailable);

    /// <summary>Primary (chat) model display text, used as the headline in the UI.</summary>
    public string PrimaryModelDisplay =>
        Assignments.FirstOrDefault(a => a.Capability == AiCapability.Chat)?.DisplayText
        ?? Assignments.FirstOrDefault()?.DisplayText
        ?? "No model";
}

/// <summary>
/// The complete set of routing decisions produced by <see cref="IModelSelectionService"/>.
/// </summary>
public sealed class EffectiveRoutingResult
{
    public RoutingMode Mode { get; init; }

    public IReadOnlyList<EffectiveWorkflowRouting> Workflows { get; init; } = [];

    public EffectiveWorkflowRouting? ForWorkflow(AiWorkflow workflow) =>
        Workflows.FirstOrDefault(w => w.Workflow == workflow);
}
