namespace Argus.AI.Discovery;

/// <summary>
/// Overall availability state for a single provider.
/// </summary>
public enum ProviderAvailability
{
    /// <summary>Provider is reachable and has at least one usable model.</summary>
    Available,

    /// <summary>Provider is reachable but has no models installed / returned.</summary>
    NoModels,

    /// <summary>Provider host is running but responded with an error.</summary>
    Error,

    /// <summary>Provider host could not be reached (not installed or not running).</summary>
    Unreachable,

    /// <summary>Provider requires credentials that have not been configured.</summary>
    NotConfigured
}

/// <summary>
/// Full result of a provider discovery pass, covering all known providers.
/// </summary>
public sealed class ProviderDiscoveryResult
{
    /// <summary>When this discovery snapshot was taken.</summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Ollama ────────────────────────────────────────────────────────────────

    public ProviderAvailability OllamaAvailability { get; init; }

    /// <summary>Base URL that was probed, e.g. "http://localhost:11434".</summary>
    public string OllamaEndpoint { get; init; } = string.Empty;

    /// <summary>Models returned by Ollama's /api/tags endpoint.</summary>
    public IReadOnlyList<DiscoveredModelInfo> OllamaModels { get; init; } = [];

    /// <summary>Human-readable error detail when OllamaAvailability is Error/Unreachable.</summary>
    public string? OllamaError { get; init; }

    // ── OpenAI ────────────────────────────────────────────────────────────────

    public ProviderAvailability OpenAiAvailability { get; init; }

    /// <summary>Set when the API key is present but we have not verified it online.</summary>
    public bool OpenAiKeyPresent { get; init; }

    // ── Aggregates ────────────────────────────────────────────────────────────

    /// <summary>All discovered models from all providers, flattened.</summary>
    public IReadOnlyList<DiscoveredModelInfo> AllModels =>
        [.. OllamaModels];

    public bool AnyProviderAvailable =>
        OllamaAvailability == ProviderAvailability.Available ||
        OpenAiAvailability == ProviderAvailability.Available;
}
