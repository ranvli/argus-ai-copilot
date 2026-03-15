namespace Argus.Infrastructure.Storage;

public sealed class ArtifactStorage : IArtifactStorage
{
    private readonly IPathProvider _paths;

    public ArtifactStorage(IPathProvider paths) => _paths = paths;

    public Task<ArtifactSaveResult> SaveScreenshotAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default) =>
        WriteAsync(sessionId, ArtifactSubfolder.Screenshots, Guid.NewGuid().ToString("N"), ".png", imageStream, ct);

    public Task<ArtifactSaveResult> SaveThumbnailAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default) =>
        WriteAsync(sessionId, ArtifactSubfolder.Thumbnails, Guid.NewGuid().ToString("N"), ".jpg", imageStream, ct);

    public Task<ArtifactSaveResult> SaveAudioChunkAsync(Guid sessionId, Stream audioStream, string extension = ".wav", CancellationToken ct = default) =>
        WriteAsync(sessionId, ArtifactSubfolder.Audio, Guid.NewGuid().ToString("N"), extension, audioStream, ct);

    public Task<ArtifactSaveResult> SaveRecordingAsync(Guid sessionId, Stream recordingStream, string extension = ".wav", CancellationToken ct = default) =>
        WriteAsync(sessionId, ArtifactSubfolder.Recordings, Guid.NewGuid().ToString("N"), extension, recordingStream, ct);

    public Task<ArtifactSaveResult> SaveTranscriptExportAsync(Guid sessionId, Stream content, string extension = ".txt", CancellationToken ct = default) =>
        WriteAsync(sessionId, ArtifactSubfolder.Transcripts, Guid.NewGuid().ToString("N"), extension, content, ct);

    public void EnsureSessionFolders(Guid sessionId)
    {
        foreach (ArtifactSubfolder sub in Enum.GetValues<ArtifactSubfolder>())
            Directory.CreateDirectory(_paths.GetSessionArtifactFolder(sessionId, sub));
    }

    public Task DeleteSessionArtifactsAsync(Guid sessionId, CancellationToken ct = default)
    {
        foreach (ArtifactSubfolder sub in Enum.GetValues<ArtifactSubfolder>())
        {
            var dir = _paths.GetSessionArtifactFolder(sessionId, sub);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ArtifactSaveResult> WriteAsync(
        Guid sessionId, ArtifactSubfolder subfolder,
        string stem, string extension,
        Stream source, CancellationToken ct)
    {
        var dir = _paths.GetSessionArtifactFolder(sessionId, subfolder);
        Directory.CreateDirectory(dir);

        var fileName = stem + extension;
        var fullPath = Path.Combine(dir, fileName);

        await using var fs = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81_920, useAsync: true);
        await source.CopyToAsync(fs, ct);

        // Relative path uses forward slashes for portability in the DB.
        var relativePath = Path.GetRelativePath(_paths.ArtifactsFolder, fullPath)
            .Replace('\\', '/');

        return new ArtifactSaveResult
        {
            FullPath     = fullPath,
            RelativePath = relativePath,
            SessionId    = sessionId,
            Subfolder    = subfolder,
            FileSizeBytes = new FileInfo(fullPath).Length
        };
    }
}

