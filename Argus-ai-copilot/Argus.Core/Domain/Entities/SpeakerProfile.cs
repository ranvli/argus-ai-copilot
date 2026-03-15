using Argus.Core.Domain.Enums;

namespace Argus.Core.Domain.Entities;

public class SpeakerProfile
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public SpeakerType Type { get; set; } = SpeakerType.Unknown;
    public string? VoiceEmbeddingRef { get; set; }
    public string? AvatarPath { get; set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
