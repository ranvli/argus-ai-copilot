using Argus.AI.Discovery;
using Argus.AI.Selection;
using Argus.Infrastructure.Storage;

namespace Argus.App.Diagnostics;

/// <summary>
/// Point-in-time snapshot of the application's health and capability status,
/// produced once during startup and held as a singleton for UI consumption.
/// </summary>
public sealed class StartupDiagnosticsResult
{
    // ── When ──────────────────────────────────────────────────────────────────

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Storage paths ─────────────────────────────────────────────────────────

    /// <summary>The fully resolved storage paths with selection mode and reasons.</summary>
    public ResolvedStoragePaths? StoragePaths { get; init; }

    /// <summary>True when the data folder exists and is writable.</summary>
    public bool StorageAvailable { get; init; }

    public string? StorageError { get; init; }

    // ── Database ──────────────────────────────────────────────────────────────

    /// <summary>True when the database file exists and was opened successfully.</summary>
    public bool DatabaseAvailable { get; init; }

    public string? DatabaseError { get; init; }

    // ── Provider discovery ────────────────────────────────────────────────────

    public ProviderDiscoveryResult? ProviderDiscovery { get; init; }

    // ── Effective routing ─────────────────────────────────────────────────────

    public EffectiveRoutingResult? EffectiveRouting { get; init; }

    // ── Convenience aliases (used by existing UI code) ────────────────────────

    public string DataFolderPath => StoragePaths?.DataFolder ?? string.Empty;
    public string DatabasePath   => StoragePaths?.DatabasePath ?? string.Empty;

    public bool OllamaAvailable =>
        ProviderDiscovery?.OllamaAvailability == ProviderAvailability.Available;

    public bool OpenAiAvailable =>
        ProviderDiscovery?.OpenAiAvailability == ProviderAvailability.Available;

    public bool AnyProviderAvailable =>
        ProviderDiscovery?.AnyProviderAvailable == true;
}
