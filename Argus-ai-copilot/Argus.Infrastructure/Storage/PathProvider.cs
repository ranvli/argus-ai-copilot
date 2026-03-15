namespace Argus.Infrastructure.Storage;

/// <summary>
/// Implements <see cref="IPathProvider"/> by delegating all path logic to a
/// <see cref="ResolvedStoragePaths"/> instance produced by <see cref="IStoragePathResolver"/>.
///
/// In Default mode this is functionally identical to the previous hard-coded
/// <c>%LocalAppData%\ArgusAI\</c> layout — no existing behaviour changes.
/// </summary>
public sealed class PathProvider : IPathProvider
{
    private readonly ResolvedStoragePaths _resolved;

    public PathProvider(IStoragePathResolver resolver)
    {
        _resolved = resolver.Resolve();
    }

    // ── IPathProvider ─────────────────────────────────────────────────────────

    public ResolvedStoragePaths ResolvedPaths => _resolved;

    /// <summary>Root of the data+logs tree (legacy alias for DataRoot).</summary>
    public string AppDataRoot => Path.Combine(_resolved.DataRoot, "ArgusAI");

    public string DataFolder      => _resolved.DataFolder;
    public string ArtifactsFolder => _resolved.ArtifactsFolder;
    public string LogsFolder      => _resolved.LogsFolder;
    public string CacheFolder     => _resolved.CacheFolder;
    public string DatabasePath    => _resolved.DatabasePath;
    public string WhisperModelsFolder => _resolved.WhisperModelsFolder;

    public string GetSessionArtifactFolder(Guid sessionId, ArtifactSubfolder subfolder) =>
        Path.Combine(
            ArtifactsFolder,
            subfolder.ToString().ToLowerInvariant(),
            sessionId.ToString("N"));

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogsFolder);
        Directory.CreateDirectory(CacheFolder);
        Directory.CreateDirectory(WhisperModelsFolder);

        foreach (ArtifactSubfolder sub in Enum.GetValues<ArtifactSubfolder>())
            Directory.CreateDirectory(
                Path.Combine(ArtifactsFolder, sub.ToString().ToLowerInvariant()));
    }
}
