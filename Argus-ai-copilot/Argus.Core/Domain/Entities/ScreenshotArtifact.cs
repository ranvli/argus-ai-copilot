using Argus.Core.Domain.ValueObjects;

namespace Argus.Core.Domain.Entities;

public class ScreenshotArtifact
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public MonitorDescriptor Monitor { get; set; } = new(0, string.Empty, 0, 0);
    public DateTimeOffset CapturedAt { get; set; }
    public string? OcrText { get; set; }
    public string? ActiveWindowTitle { get; set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
}
