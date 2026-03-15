using Argus.AI.Configuration;

namespace Argus.AI.Discovery;

/// <summary>
/// Metadata about a single AI model discovered at runtime.
/// </summary>
public sealed class DiscoveredModelInfo
{
    /// <summary>Human-readable provider name, e.g. "Ollama" or "OpenAI".</summary>
    public required string Provider { get; init; }

    /// <summary>The model identifier as reported by the provider, e.g. "llama3:8b".</summary>
    public required string ModelId { get; init; }

    /// <summary>True when the model runs locally (no internet required).</summary>
    public bool IsLocal { get; init; }

    /// <summary>Capabilities this model supports.</summary>
    public IReadOnlyList<AiCapability> Capabilities { get; init; } = [];

    /// <summary>Approximate parameter count in billions, if known.</summary>
    public double? SizeInBillions { get; init; }

    /// <summary>Model size on disk in bytes, if reported by the provider.</summary>
    public long? DiskSizeBytes { get; init; }

    /// <summary>Whether this model is currently reachable / usable.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Optional note explaining why the model is unavailable.</summary>
    public string? UnavailableReason { get; init; }

    public override string ToString() =>
        $"{Provider}/{ModelId}{(IsLocal ? " [local]" : " [cloud]")}{(IsAvailable ? "" : " (unavailable)")}";
}
