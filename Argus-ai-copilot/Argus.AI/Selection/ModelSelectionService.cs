using Argus.AI.Configuration;
using Argus.AI.Discovery;

namespace Argus.AI.Selection;

/// <summary>
/// Selects the best available model for each workflow × capability pair
/// according to the requested <see cref="RoutingMode"/>.
/// </summary>
public sealed class ModelSelectionService : IModelSelectionService
{
    // Which capabilities each workflow primarily needs.
    private static readonly Dictionary<AiWorkflow, AiCapability[]> WorkflowCapabilities = new()
    {
        [AiWorkflow.RealtimeAssist]  = [AiCapability.Chat],
        [AiWorkflow.MemoryQuery]     = [AiCapability.Chat, AiCapability.Embeddings],
        [AiWorkflow.MeetingSummary]  = [AiCapability.Chat],
        [AiWorkflow.ScreenExplain]   = [AiCapability.Vision, AiCapability.Chat]
    };

    public EffectiveRoutingResult Select(ProviderDiscoveryResult discovery, RoutingMode mode)
    {
        var workflows = WorkflowCapabilities
            .Select(kvp => BuildWorkflowRouting(kvp.Key, kvp.Value, discovery, mode))
            .ToList()
            .AsReadOnly();

        return new EffectiveRoutingResult
        {
            Mode = mode,
            Workflows = workflows
        };
    }

    private static EffectiveWorkflowRouting BuildWorkflowRouting(
        AiWorkflow workflow,
        AiCapability[] capabilities,
        ProviderDiscoveryResult discovery,
        RoutingMode mode)
    {
        var assignments = capabilities
            .Select(cap => ResolveCapability(cap, discovery, mode))
            .ToList()
            .AsReadOnly();

        return new EffectiveWorkflowRouting
        {
            Workflow = workflow,
            Assignments = assignments
        };
    }

    private static EffectiveCapabilityAssignment ResolveCapability(
        AiCapability capability,
        ProviderDiscoveryResult discovery,
        RoutingMode mode)
    {
        var localModels = discovery.OllamaModels
            .Where(m => m.IsAvailable && m.Capabilities.Contains(capability))
            .ToList();

        var cloudAvailable =
            discovery.OpenAiAvailability == ProviderAvailability.Available &&
            CloudSupports(capability);

        DiscoveredModelInfo? chosen = mode switch
        {
            RoutingMode.PreferLocal  => PickLocal(localModels) ?? (cloudAvailable ? MakeCloudEntry(capability) : null),
            RoutingMode.PreferCloud  => cloudAvailable ? MakeCloudEntry(capability) : PickLocal(localModels),
            RoutingMode.Manual       => PickLocal(localModels),   // respects only what's explicitly configured
            _                        => PickBest(localModels, cloudAvailable, capability)   // Automatic
        };

        if (chosen is not null)
            return new EffectiveCapabilityAssignment { Capability = capability, Model = chosen };

        var reason = BuildUnavailableReason(capability, discovery, mode);
        return new EffectiveCapabilityAssignment
        {
            Capability = capability,
            UnavailableReason = reason
        };
    }

    // ── Model picking helpers ─────────────────────────────────────────────────

    private static DiscoveredModelInfo? PickLocal(List<DiscoveredModelInfo> locals)
    {
        if (locals.Count == 0) return null;
        // Prefer largest (most capable) model; fall back to first
        return locals.MaxBy(m => m.SizeInBillions ?? 0) ?? locals[0];
    }

    /// <summary>Automatic: prefer local for chat/embeddings, prefer cloud for vision/transcription/tts.</summary>
    private static DiscoveredModelInfo? PickBest(
        List<DiscoveredModelInfo> locals,
        bool cloudAvailable,
        AiCapability capability)
    {
        bool localFriendly =
            capability == AiCapability.Chat ||
            capability == AiCapability.Embeddings;

        if (localFriendly)
            return PickLocal(locals) ?? (cloudAvailable ? MakeCloudEntry(capability) : null);

        return cloudAvailable ? MakeCloudEntry(capability) : PickLocal(locals);
    }

    // ── Cloud model descriptors ───────────────────────────────────────────────

    private static bool CloudSupports(AiCapability capability) => capability switch
    {
        AiCapability.Chat          => true,
        AiCapability.Vision        => true,
        AiCapability.Transcription => true,
        AiCapability.Tts           => true,
        AiCapability.Embeddings    => true,
        _                          => false
    };

    private static DiscoveredModelInfo MakeCloudEntry(AiCapability capability)
    {
        var modelId = capability switch
        {
            AiCapability.Chat          => "gpt-4o",
            AiCapability.Vision        => "gpt-4o",
            AiCapability.Transcription => "whisper-1",
            AiCapability.Tts           => "tts-1",
            AiCapability.Embeddings    => "text-embedding-3-small",
            _                          => "gpt-4o"
        };

        return new DiscoveredModelInfo
        {
            Provider = "OpenAI",
            ModelId = modelId,
            IsLocal = false,
            IsAvailable = true,
            Capabilities = [capability]
        };
    }

    // ── Reason text ──────────────────────────────────────────────────────────

    private static string BuildUnavailableReason(
        AiCapability capability,
        ProviderDiscoveryResult discovery,
        RoutingMode mode)
    {
        if (mode == RoutingMode.PreferLocal || mode == RoutingMode.Automatic || mode == RoutingMode.Manual)
        {
            if (discovery.OllamaAvailability == ProviderAvailability.Unreachable)
                return "Ollama not running";
            if (discovery.OllamaAvailability == ProviderAvailability.NoModels)
                return "No Ollama models installed";
        }

        if (mode == RoutingMode.PreferCloud || mode == RoutingMode.Automatic)
        {
            if (discovery.OpenAiAvailability == ProviderAvailability.NotConfigured)
                return "OpenAI not configured";
        }

        return $"No model available for {capability}";
    }
}
