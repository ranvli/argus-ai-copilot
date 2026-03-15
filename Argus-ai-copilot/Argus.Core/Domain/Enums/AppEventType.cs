namespace Argus.Core.Domain.Enums;

public enum AppEventType
{
    SessionStarted,
    SessionPaused,
    SessionResumed,
    SessionEnded,
    MeetingDetected,
    MeetingEnded,
    SpeakerChanged,
    ActiveWindowChanged,
    HotkeyTriggered,
    ErrorOccurred
}
