using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Argus.Context.WindowContext;

/// <summary>
/// Polls the Win32 foreground window on a configurable interval and raises
/// <see cref="ActiveWindowChanged"/> whenever the window handle or title changes.
///
/// Uses <c>GetForegroundWindow</c> + <c>GetWindowText</c> + process introspection —
/// no event hooks that require a message loop.
/// </summary>
public sealed class ActiveWindowTracker : BackgroundService, IActiveWindowTracker
{
    private readonly ILogger<ActiveWindowTracker> _logger;
    private readonly TimeSpan _pollInterval;

    private ActiveWindowInfo? _current;

    public ActiveWindowInfo? Current => _current;

    public event EventHandler<ActiveWindowChangedEventArgs>? ActiveWindowChanged;

    public ActiveWindowTracker(ILogger<ActiveWindowTracker> logger, TimeSpan? pollInterval = null)
    {
        _logger       = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Service} started. Poll interval = {Interval}ms.",
            nameof(ActiveWindowTracker), _pollInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
                PollForegroundWindow();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error during foreground window poll.");
                // Back off briefly to avoid a tight error loop.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("{Service} stopped.", nameof(ActiveWindowTracker));
    }

    // ── Polling logic ─────────────────────────────────────────────────────────

    private void PollForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        var title = GetWindowTitle(hwnd);
        var (processName, exePath, processId) = GetProcessInfo(hwnd);

        var previous = _current;

        // Suppress noise: ignore changes where both handle AND title match the last snapshot.
        if (previous is not null &&
            previous.WindowHandle == hwnd &&
            previous.WindowTitle  == title)
        {
            return;
        }

        var current = new ActiveWindowInfo
        {
            WindowHandle   = hwnd,
            WindowTitle    = title,
            ProcessName    = processName,
            ProcessId      = processId,
            ExecutablePath = exePath,
            CapturedAt     = DateTimeOffset.UtcNow
        };

        _current = current;

        _logger.LogDebug("Active window: {Display}", current.DisplayText);

        ActiveWindowChanged?.Invoke(this, new ActiveWindowChangedEventArgs
        {
            Previous = previous,
            Current  = current
        });
    }

    // ── Win32 helpers ─────────────────────────────────────────────────────────

    private static string GetWindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static (string processName, string exePath, int processId) GetProcessInfo(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return (string.Empty, string.Empty, 0);

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName ?? string.Empty;

            string exePath;
            try   { exePath = process.MainModule?.FileName ?? string.Empty; }
            catch { exePath = string.Empty; }   // Access denied on elevated processes

            return (name, exePath, (int)pid);
        }
        catch
        {
            return (string.Empty, string.Empty, 0);
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
