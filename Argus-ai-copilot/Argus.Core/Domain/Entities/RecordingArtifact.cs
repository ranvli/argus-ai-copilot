using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;

namespace Argus.Core.Domain.Entities;

public class RecordingArtifact
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public ArtifactType Type { get; set; } = ArtifactType.AudioRecording;
    public string FilePath { get; set; } = string.Empty;
    public TimeRange Range { get; set; } = new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    public long FileSizeBytes { get; set; }
    public string? MimeType { get; set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
}
