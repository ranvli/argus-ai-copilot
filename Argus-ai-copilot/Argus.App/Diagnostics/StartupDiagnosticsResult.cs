using Argus.AI.Discovery;
using Argus.AI.Selection;

namespace Argus.App.Diagnostics;

/// <summary>
/// Point-in-time snapshot of the application's health and capability status,
/// produced once during startup and held as a singleton for UI consumption.
/// </summary>
public sealed class StartupDiagnosticsResult
{
    // ── When ──────────────────────────────────────────────────────────────────

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Storage ───────────────────────────────────────────────────────────────

    /// <summary>Path of the data folder (e.g. %LocalAppData%\ArgusAI\data\).</summary>
    public string DataFolderPath { get; init; } = string.Empty;

    /// <summary>True when the data folder exists and is writable.</summary>
    public bool StorageAvailable { get; init; }

    public string? StorageError { get; init; }

    // ── Database ──────────────────────────────────────────────────────────────

    /// <summary>Full path to the SQLite database file.</summary>
    public string DatabasePath { get; init; } = string.Empty;

    /// <summary>True when the database file exists and was opened successfully.</summary>
    public bool DatabaseAvailable { get; init; }

    public string? DatabaseError { get; init; }

    // ── Provider discovery ────────────────────────────────────────────────────

    public ProviderDiscoveryResult? ProviderDiscovery { get; init; }

    // ── Effective routing ─────────────────────────────────────────────────────

    public EffectiveRoutingResult? EffectiveRouting { get; init; }

    // ── Convenience ───────────────────────────────────────────────────────────

    public bool OllamaAvailable =>
        ProviderDiscovery?.OllamaAvailability == ProviderAvailability.Available;

    public bool OpenAiAvailable =>
        ProviderDiscovery?.OpenAiAvailability == ProviderAvailability.Available;

    public bool AnyProviderAvailable =>
        ProviderDiscovery?.AnyProviderAvailable == true;
}
