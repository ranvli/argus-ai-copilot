namespace Argus.Infrastructure.Storage;

/// <summary>
/// The effective resolved paths for every ArgusAI storage category,
/// together with human-readable selection reasons for diagnostics.
/// </summary>
public sealed class ResolvedStoragePaths
{
    // ── Selected mode ─────────────────────────────────────────────────────────

    public StorageMode Mode { get; init; }

    // ── Effective roots ───────────────────────────────────────────────────────

    /// <summary>Root used for database and logs (fast, reliable storage).</summary>
    public required string DataRoot { get; init; }

    /// <summary>Root used for transient cache files.</summary>
    public required string CacheRoot { get; init; }

    /// <summary>Root used for large binary artifacts (audio, screenshots, recordings).</summary>
    public required string ArtifactRoot { get; init; }

    // ── Derived paths (mirrors IPathProvider contract) ────────────────────────

    public string DataFolder      => Path.Combine(DataRoot,     "ArgusAI", "data");
    public string LogsFolder      => Path.Combine(DataRoot,     "ArgusAI", "logs");
    public string CacheFolder     => Path.Combine(CacheRoot,    "ArgusAI", "cache");
    public string ArtifactsFolder => Path.Combine(ArtifactRoot, "ArgusAI", "artifacts");
    public string DatabasePath    => Path.Combine(DataFolder,   "argus.db");
    public string WhisperModelsFolder => Path.Combine(DataRoot, "ArgusAI", "models", "whisper");

    // ── Selection reasons (shown in diagnostics) ──────────────────────────────

    public string DataRootReason     { get; init; } = string.Empty;
    public string CacheRootReason    { get; init; } = string.Empty;
    public string ArtifactRootReason { get; init; } = string.Empty;

    // ── Display helpers ───────────────────────────────────────────────────────

    public string ModeDisplay => Mode switch
    {
        StorageMode.Default      => "Default (LocalAppData)",
        StorageMode.AutoBestDrive => "AutoBestDrive",
        StorageMode.Custom        => "Custom",
        _                         => Mode.ToString()
    };
}
