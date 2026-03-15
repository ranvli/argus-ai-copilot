namespace Argus.Infrastructure.Storage;

public sealed class PathProvider : IPathProvider
{
    private static readonly string _root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus");

    public string AppDataRoot => _root;

    public string DatabasePath => Path.Combine(_root, "argus.db");

    public string GetSessionFolder(Guid sessionId, string subfolder) =>
        Path.Combine(_root, "Artifacts", subfolder, sessionId.ToString());

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Artifacts", "Screenshots"));
        Directory.CreateDirectory(Path.Combine(_root, "Artifacts", "Thumbnails"));
        Directory.CreateDirectory(Path.Combine(_root, "Artifacts", "Audio"));
        Directory.CreateDirectory(Path.Combine(_root, "Artifacts", "Recordings"));
    }
}
