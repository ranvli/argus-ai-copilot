using Argus.Audio.Capture;
using Argus.Core.Domain.Entities;

namespace Argus.Transcription.Pipeline;

/// <summary>
/// Controls the audio ingestion + transcription pipeline for an active session.
/// </summary>
public interface ITranscriptionPipeline
{
    /// <summary>Current audio and transcription status.</summary>
    AudioStatusSnapshot Status { get; }

    /// <summary>Raised whenever the status snapshot changes.</summary>
    event EventHandler<AudioStatusSnapshot>? StatusChanged;

    /// <summary>
    /// Raised on the thread pool whenever new transcript segments arrive.
    /// The caller is responsible for persisting and forwarding to the UI.
    /// </summary>
    event EventHandler<IReadOnlyList<TranscriptSegment>>? SegmentsProduced;

    /// <summary>Starts audio capture and the transcription processing loop.</summary>
    Task StartAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Stops capture and flushes remaining queued chunks.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Pauses audio capture without stopping the pipeline.</summary>
    void Pause();

    /// <summary>Resumes paused audio capture.</summary>
    void Resume();
}
