using Argus.Core.Domain.Enums;

namespace Argus.App.Services;

/// <summary>
/// An immutable snapshot of the current session and active-window state,
/// produced by <see cref="SessionCoordinatorService"/> and consumed by the UI.
/// Replacing the whole record makes WPF databinding simple — compare the reference.
/// </summary>
public sealed class SessionStateSnapshot
{
    public static readonly SessionStateSnapshot Idle = new();

    // ── Session ───────────────────────────────────────────────────────────────

    public SessionLifecycleState LifecycleState { get; init; } = SessionLifecycleState.Idle;
    public Guid? SessionId                       { get; init; }
    public string? SessionTitle                  { get; init; }
    public DateTimeOffset? SessionStartedAt      { get; init; }
    public int AppEventCount                     { get; init; }
    public int TranscriptSegmentCount            { get; init; }

    // ── Active window ─────────────────────────────────────────────────────────

    public string ActiveProcessName  { get; init; } = string.Empty;
    public int    ActiveProcessId    { get; init; }
    public string ActiveWindowTitle  { get; init; } = string.Empty;

    // ── Derived display helpers ───────────────────────────────────────────────

    public bool IsActive =>
        LifecycleState is SessionLifecycleState.Listening or SessionLifecycleState.Paused;

    public string StateLabel => LifecycleState switch
    {
        SessionLifecycleState.Idle      => "Idle",
        SessionLifecycleState.Listening => "● Listening",
        SessionLifecycleState.Paused    => "⏸ Paused",
        SessionLifecycleState.Stopping  => "Stopping…",
        SessionLifecycleState.Completed => "Completed",
        _                               => LifecycleState.ToString()
    };

    /// <summary>
    /// Computes elapsed time relative to <paramref name="now"/>.
    /// Pass <see cref="DateTimeOffset.UtcNow"/> from a UI timer so the value
    /// is always fresh without requiring a new snapshot.
    /// </summary>
    public string GetDurationDisplay(DateTimeOffset now)
    {
        if (SessionStartedAt is null) return "—";
        var elapsed = now - SessionStartedAt.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    /// <summary>
    /// Convenience overload that uses the current UTC time.
    /// Kept for back-compat; prefer <see cref="GetDurationDisplay(DateTimeOffset)"/>
    /// when calling from a timer callback.
    /// </summary>
    public string SessionDurationDisplay => GetDurationDisplay(DateTimeOffset.UtcNow);
}
