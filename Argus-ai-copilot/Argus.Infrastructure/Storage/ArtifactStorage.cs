namespace Argus.Infrastructure.Storage;

public sealed class ArtifactStorage : IArtifactStorage
{
    private readonly IPathProvider _paths;

    public ArtifactStorage(IPathProvider paths)
    {
        _paths = paths;
    }

    public Task<string> SaveScreenshotAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default) =>
        WriteFileAsync(sessionId, "Screenshots", Guid.NewGuid().ToString("N"), ".png", imageStream, ct);

    public Task<string> SaveThumbnailAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default) =>
        WriteFileAsync(sessionId, "Thumbnails", Guid.NewGuid().ToString("N") + ".thumb", ".jpg", imageStream, ct);

    public Task<string> SaveAudioChunkAsync(Guid sessionId, Stream audioStream, string extension = ".wav", CancellationToken ct = default) =>
        WriteFileAsync(sessionId, "Audio", Guid.NewGuid().ToString("N"), extension, audioStream, ct);

    public Task<string> SaveRecordingAsync(Guid sessionId, Stream recordingStream, string extension = ".wav", CancellationToken ct = default) =>
        WriteFileAsync(sessionId, "Recordings", Guid.NewGuid().ToString("N"), extension, recordingStream, ct);

    public Task DeleteSessionArtifactsAsync(Guid sessionId, CancellationToken ct = default)
    {
        foreach (var subfolder in new[] { "Screenshots", "Thumbnails", "Audio", "Recordings" })
        {
            var dir = _paths.GetSessionFolder(sessionId, subfolder);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        return Task.CompletedTask;
    }

    private async Task<string> WriteFileAsync(
        Guid sessionId, string subfolder, string stem, string extension,
        Stream source, CancellationToken ct)
    {
        var dir = _paths.GetSessionFolder(sessionId, subfolder);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, stem + extension);
        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await source.CopyToAsync(fs, ct);
        return filePath;
    }
}
