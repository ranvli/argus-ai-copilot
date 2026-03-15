namespace Argus.Infrastructure.Storage;

/// <summary>
/// Resolves well-known local-app-data folder paths used by ArgusAI.
/// All paths are rooted at <c>%LocalAppData%\ArgusAI\</c>.
/// </summary>
public interface IPathProvider
{
    // ── Root ──────────────────────────────────────────────────────────────────

    /// <summary>%LocalAppData%\ArgusAI\</summary>
    string AppDataRoot { get; }

    // ── Top-level sub-folders ─────────────────────────────────────────────────

    /// <summary>%LocalAppData%\ArgusAI\data\  — SQLite database lives here.</summary>
    string DataFolder { get; }

    /// <summary>%LocalAppData%\ArgusAI\artifacts\  — binary artifact files.</summary>
    string ArtifactsFolder { get; }

    /// <summary>%LocalAppData%\ArgusAI\logs\  — structured log files.</summary>
    string LogsFolder { get; }

    /// <summary>%LocalAppData%\ArgusAI\cache\  — transient cache files.</summary>
    string CacheFolder { get; }

    // ── Derived paths ─────────────────────────────────────────────────────────

    /// <summary>Full path to the SQLite database file.</summary>
    string DatabasePath { get; }

    /// <summary>
    /// Returns the full path to a session-scoped artifact sub-folder.
    /// The sub-folder is one of: screenshots, thumbnails, audio, recordings.
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

