using Argus.Audio.Capture;
using Argus.Core.Domain.Entities;

namespace Argus.App.Services;

/// <summary>
/// Exposes the live audio capture, transcription status, and transcript segments to UI consumers.
/// Implemented by <see cref="SessionCoordinatorService"/>.
/// </summary>
public interface IAudioStatusPublisher
{
    /// <summary>The current audio and transcription status snapshot.</summary>
    AudioStatusSnapshot AudioStatus { get; }

    /// <summary>Raised on a thread-pool thread whenever the audio status changes.</summary>
    event EventHandler<AudioStatusSnapshot>? AudioStatusChanged;

    /// <summary>Raised on a thread-pool thread whenever new transcript segments are produced.</summary>
    event EventHandler<IReadOnlyList<TranscriptSegment>>? TranscriptSegmentsReceived;
}
