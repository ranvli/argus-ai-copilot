using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;

namespace Argus.Core.Domain.Entities;

public class Session
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public SessionType Type { get; set; } = SessionType.Unknown;
    public ListeningMode ListeningMode { get; set; } = ListeningMode.Microphone;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool IsActive => EndedAt is null;
    public TimeRange? Duration => EndedAt.HasValue ? new TimeRange(StartedAt, EndedAt.Value) : null;
    public string? ApplicationContext { get; set; }
    public List<TranscriptSegment> Transcript { get; set; } = [];
    public List<ScreenshotArtifact> Screenshots { get; set; } = [];
    public List<RecordingArtifact> Recordings { get; set; } = [];
    public SessionSummary? Summary { get; set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
