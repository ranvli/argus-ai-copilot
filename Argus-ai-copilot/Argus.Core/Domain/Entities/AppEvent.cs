using Argus.Core.Domain.Enums;

namespace Argus.Core.Domain.Entities;

public class AppEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public AppEventType Type { get; set; }
    public Guid? SessionId { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
