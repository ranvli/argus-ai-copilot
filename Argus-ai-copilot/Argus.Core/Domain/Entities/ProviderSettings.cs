namespace Argus.Core.Domain.Entities;

public class ProviderSettings
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string ProviderKey { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? ModelId { get; set; }
    public Dictionary<string, string> ExtraProperties { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
