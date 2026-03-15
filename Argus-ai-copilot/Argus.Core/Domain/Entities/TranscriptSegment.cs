using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;

namespace Argus.Core.Domain.Entities;

public class TranscriptSegment
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Text { get; set; } = string.Empty;
    public SpeakerType SpeakerType { get; set; } = SpeakerType.Unknown;
    public string? SpeakerLabel { get; set; }
    public Guid? SpeakerProfileId { get; set; }
    public TimeRange Range { get; set; } = new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    public ConfidenceScore Confidence { get; set; } = ConfidenceScore.None;
    public string? Language { get; set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
}
