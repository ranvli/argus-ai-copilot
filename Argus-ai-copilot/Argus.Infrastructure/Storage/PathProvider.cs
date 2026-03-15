namespace Argus.Infrastructure.Storage;

/// <summary>
/// Resolves all well-known local-app-data paths for ArgusAI.
/// Root: <c>%LocalAppData%\ArgusAI\</c>
/// </summary>
public sealed class PathProvider : IPathProvider
{
    // Computed once at class-load time — Environment.SpecialFolder is stable for the process lifetime.
    private static readonly string _root =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArgusAI");

    public string AppDataRoot    => _root;
    public string DataFolder     => Path.Combine(_root, "data");
    public string ArtifactsFolder => Path.Combine(_root, "artifacts");
    public string LogsFolder     => Path.Combine(_root, "logs");
    public string CacheFolder    => Path.Combine(_root, "cache");
    public string DatabasePath   => Path.Combine(DataFolder, "argus.db");

    public string GetSessionArtifactFolder(Guid sessionId, ArtifactSubfolder subfolder) =>
        Path.Combine(ArtifactsFolder, subfolder.ToString().ToLowerInvariant(), sessionId.ToString("N"));

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogsFolder);
        Directory.CreateDirectory(CacheFolder);

        foreach (ArtifactSubfolder sub in Enum.GetValues<ArtifactSubfolder>())
            Directory.CreateDirectory(Path.Combine(ArtifactsFolder, sub.ToString().ToLowerInvariant()));
    }
}

