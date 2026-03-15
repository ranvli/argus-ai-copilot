namespace Argus.Infrastructure.Storage;

/// <summary>
/// Resolves well-known storage paths used by ArgusAI.
/// The effective roots depend on the configured <see cref="StorageMode"/>;
/// in Default mode everything lives under <c>%LocalAppData%\ArgusAI\</c>.
/// </summary>
public interface IPathProvider
{
    // ── Mode and diagnostics ──────────────────────────────────────────────────

    /// <summary>The resolved paths and selection reasons produced at startup.</summary>
    ResolvedStoragePaths ResolvedPaths { get; }

    // ── Root ──────────────────────────────────────────────────────────────────

    /// <summary>Legacy alias — root of the data + logs tree.</summary>
    string AppDataRoot { get; }

    // ── Top-level sub-folders ─────────────────────────────────────────────────

    /// <summary>Folder for the SQLite database.</summary>
    string DataFolder { get; }

    /// <summary>Folder for large binary artifact files.</summary>
    string ArtifactsFolder { get; }

    /// <summary>Folder for structured log files.</summary>
    string LogsFolder { get; }

    /// <summary>Folder for transient cache files.</summary>
    string CacheFolder { get; }

    // ── Derived paths ─────────────────────────────────────────────────────────

    /// <summary>Full path to the SQLite database file.</summary>
    string DatabasePath { get; }

    /// <summary>Folder for local Whisper GGML model files.</summary>
    string WhisperModelsFolder { get; }

    /// <summary>
    /// Returns the full path to a session-scoped artifact sub-folder.
    /// </summary>
    string GetSessionArtifactFolder(Guid sessionId, ArtifactSubfolder subfolder);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Creates all required base directories if they do not already exist.</summary>
    void EnsureDirectoriesExist();
}

/// <summary>Typed names for artifact sub-folders inside a session directory.</summary>
public enum ArtifactSubfolder
{
    Screenshots,
    Thumbnails,
    Audio,
    Recordings,
    Transcripts
}
