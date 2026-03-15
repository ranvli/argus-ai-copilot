namespace Argus.Core.Contracts.Services;

public class MeetingDetectedEventArgs(string applicationName, string windowTitle) : EventArgs
{
    public string ApplicationName { get; } = applicationName;
    public string WindowTitle { get; } = windowTitle;
    public DateTimeOffset DetectedAt { get; } = DateTimeOffset.UtcNow;
}

public interface IMeetingDetectionService
{
    event EventHandler<MeetingDetectedEventArgs>? MeetingDetected;
    event EventHandler? MeetingEnded;

    bool IsInMeeting { get; }
    string? ActiveMeetingApplication { get; }

    Task StartMonitoringAsync(CancellationToken ct = default);
    Task StopMonitoringAsync();
}
