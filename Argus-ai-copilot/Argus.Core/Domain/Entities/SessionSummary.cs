namespace Argus.Core.Domain.Entities;

public class SessionSummary
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string ShortSummary { get; set; } = string.Empty;
    public string? FullSummary { get; set; }
    public List<string> ActionItems { get; set; } = [];
    public List<string> KeyTopics { get; set; } = [];
    public string? Sentiment { get; set; }
    public string? ModelUsed { get; set; }
    public DateTimeOffset GeneratedAt { get; private set; } = DateTimeOffset.UtcNow;
}
