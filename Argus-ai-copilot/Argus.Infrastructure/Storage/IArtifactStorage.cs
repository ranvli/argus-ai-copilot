namespace Argus.Infrastructure.Storage;

/// <summary>
/// Stores and manages binary artifact files on disk.
/// </summary>
public interface IArtifactStorage
{
    /// <summary>Saves a screenshot PNG. Returns the full path written.</summary>
    Task<string> SaveScreenshotAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default);

    /// <summary>Saves a JPEG thumbnail. Returns the full path written.</summary>
    Task<string> SaveThumbnailAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default);

    /// <summary>Saves a raw audio chunk (e.g. PCM/WAV). Returns the full path written.</summary>
    Task<string> SaveAudioChunkAsync(Guid sessionId, Stream audioStream, string extension = ".wav", CancellationToken ct = default);

    /// <summary>Saves a full session recording. Returns the full path written.</summary>
    Task<string> SaveRecordingAsync(Guid sessionId, Stream recordingStream, string extension = ".wav", CancellationToken ct = default);

    /// <summary>Deletes all artifact files associated with a session.</summary>
    Task DeleteSessionArtifactsAsync(Guid sessionId, CancellationToken ct = default);
}
