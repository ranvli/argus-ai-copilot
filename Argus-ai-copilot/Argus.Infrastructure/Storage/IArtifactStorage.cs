namespace Argus.Infrastructure.Storage;

/// <summary>
/// Writes and manages binary artifact files on disk.
/// Only file-I/O is performed here; database metadata is the caller's responsibility.
/// </summary>
public interface IArtifactStorage
{
    /// <summary>Saves a screenshot image (PNG). Returns a strongly typed result with the full path and size.</summary>
    Task<ArtifactSaveResult> SaveScreenshotAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default);

    /// <summary>Saves a JPEG thumbnail. Returns a strongly typed result.</summary>
    Task<ArtifactSaveResult> SaveThumbnailAsync(Guid sessionId, Stream imageStream, CancellationToken ct = default);

    /// <summary>Saves a raw audio chunk (PCM/WAV). Returns a strongly typed result.</summary>
    Task<ArtifactSaveResult> SaveAudioChunkAsync(Guid sessionId, Stream audioStream, string extension = ".wav", CancellationToken ct = default);

    /// <summary>Saves a full session recording. Returns a strongly typed result.</summary>
    Task<ArtifactSaveResult> SaveRecordingAsync(Guid sessionId, Stream recordingStream, string extension = ".wav", CancellationToken ct = default);

    /// <summary>Saves a plain-text or JSON transcript export file.</summary>
    Task<ArtifactSaveResult> SaveTranscriptExportAsync(Guid sessionId, Stream content, string extension = ".txt", CancellationToken ct = default);

    /// <summary>
    /// Ensures a session artifact folder exists on disk.
    /// Call when a new session starts so folders are ready before any capture occurs.
    /// </summary>
    void EnsureSessionFolders(Guid sessionId);

    /// <summary>Deletes all artifact files and sub-folders for the given session.</summary>
    Task DeleteSessionArtifactsAsync(Guid sessionId, CancellationToken ct = default);
}

