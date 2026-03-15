namespace Argus.Infrastructure.Storage;

/// <summary>
/// Resolves well-known LocalAppData folder paths for Argus artifacts.
/// </summary>
public interface IPathProvider
{
    /// <summary>Root folder: %LOCALAPPDATA%\Argus\</summary>
    string AppDataRoot { get; }

    /// <summary>SQLite database file path.</summary>
    string DatabasePath { get; }

    /// <summary>Returns the artifact folder for a given session and sub-folder name.</summary>
    string GetSessionFolder(Guid sessionId, string subfolder);

    /// <summary>Ensures all required base directories exist.</summary>
    void EnsureDirectoriesExist();
}
