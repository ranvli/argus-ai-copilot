namespace Argus.Infrastructure.Storage;

/// <summary>
/// Strongly typed result returned by every artifact save operation.
/// Carries the resolved full path and the session context so callers
/// can immediately populate domain entity properties.
/// </summary>
public sealed class ArtifactSaveResult
{
    /// <summary>Full absolute path of the file written to disk.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Path relative to the artifacts root — suitable for storing in the database.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>File name without directory component.</summary>
    public string FileName => Path.GetFileName(FullPath);

    /// <summary>Session this artifact belongs to.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Artifact category.</summary>
    public ArtifactSubfolder Subfolder { get; init; }

    /// <summary>File size in bytes, populated after the write completes.</summary>
    public long FileSizeBytes { get; init; }
}
