using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Windows.Forms;

namespace Argus.App.Services;

// ┌─────────────────────────────────────────────────────────────────────────────┐
// │  WHY System.Windows.Forms IS USED HERE                                      │
// │                                                                              │
// │  System.Windows.Forms.NotifyIcon is the most reliable zero-friction way     │
// │  to put an icon in the Windows system tray. It requires                     │
// │  <UseWindowsForms>true</UseWindowsForms> in the project file but does NOT   │
// │  require a WinForms Application.Run() message loop — the WPF Dispatcher's  │
// │  underlying Win32 pump handles all tray/context-menu messages on the same   │
// │  STA thread.                                                                 │
// │                                                                              │
// │  ISOLATION CONTRACT:                                                         │
// │    • System.Windows.Forms is referenced ONLY in this file.                  │
// │    • Every other file in Argus.App touches only ITrayService (pure C#).     │
// │    • If you later switch to Hardcodet.NotifyIcon.Wpf or a Win32 P/Invoke   │
// │      wrapper, you replace this file alone — nothing else changes.           │
// └─────────────────────────────────────────────────────────────────────────────┘

internal sealed class TrayService : IHostedService, ITrayService, IDisposable
{
    private readonly ILogger<TrayService> _logger;
    private readonly IAppStateService _appState;
    private readonly MainWindow _mainWindow;

    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _itemStartListening;
    private ToolStripMenuItem? _itemPauseListening;

    public TrayService(
        ILogger<TrayService> logger,
        IAppStateService appState,
        MainWindow mainWindow)
    {
        _logger = logger;
        _appState = appState;
        _mainWindow = mainWindow;
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TrayService starting.");

        // NotifyIcon must be created on an STA thread. WPF's main thread is STA,
        // so Dispatcher.Invoke guarantees we're on the right thread.
        System.Windows.Application.Current.Dispatcher.Invoke(CreateTrayIcon);

        _appState.ModeChanged += OnModeChanged;

        _logger.LogInformation("Tray icon visible. Application running in tray.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TrayService stopping.");

        _appState.ModeChanged -= OnModeChanged;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_notifyIcon is not null)
                _notifyIcon.Visible = false;
        });

        return Task.CompletedTask;
    }

    // ── ITrayService ──────────────────────────────────────────────────────────

    public void Show() =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_notifyIcon is not null)
                _notifyIcon.Visible = true;
        });

    public void Hide() =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_notifyIcon is not null)
                _notifyIcon.Visible = false;
        });

    public void SetTooltip(string tooltip) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_notifyIcon is not null)
                // NotifyIcon tooltip is capped at 127 characters.
                _notifyIcon.Text = tooltip[..Math.Min(tooltip.Length, 127)];
        });

    public void ShowBalloonTip(string title, string message) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
            _notifyIcon?.ShowBalloonTip(3_000, title, message, ToolTipIcon.Info));

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }

    // ── Private: build the tray icon ──────────────────────────────────────────

    private void CreateTrayIcon()
    {
        _itemStartListening = new ToolStripMenuItem("Start Listening", null, OnStartListeningClicked)
        {
            Enabled = true
        };

        _itemPauseListening = new ToolStripMenuItem("Pause Listening", null, OnPauseListeningClicked)
        {
            Enabled = false
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open Dashboard",    null, OnOpenDashboardClicked);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_itemStartListening);
        contextMenu.Items.Add(_itemPauseListening);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit",              null, OnExitClicked);

        _notifyIcon = new NotifyIcon
        {
            Text             = "Argus AI Copilot",
            // TODO: replace SystemIcons.Application with an embedded argus.ico resource.
            Icon             = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Visible          = true
        };

        // Double-click the icon → open dashboard.
        _notifyIcon.DoubleClick += (_, _) => OnOpenDashboardClicked(null, EventArgs.Empty);
    }

    // ── Private: menu action handlers ────────────────────────────────────────

    private void OnOpenDashboardClicked(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    private void OnStartListeningClicked(object? sender, EventArgs e) =>
        _appState.StartListening();

    private void OnPauseListeningClicked(object? sender, EventArgs e) =>
        _appState.PauseListening();

    private void OnExitClicked(object? sender, EventArgs e)
    {
        // Dispatch to the WPF main thread.
        // App.OnExit will stop the host cleanly (including this TrayService).
        System.Windows.Application.Current.Dispatcher.Invoke(
            System.Windows.Application.Current.Shutdown);
    }

    // ── Private: react to state changes ──────────────────────────────────────

    private void OnModeChanged(object? sender, AppMode mode)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_itemStartListening is null || _itemPauseListening is null || _notifyIcon is null)
                return;

            var activelyListening = _appState.IsListening && !_appState.IsPaused;
            _itemStartListening.Enabled  = !activelyListening;
            _itemPauseListening.Enabled  =  activelyListening;

            _notifyIcon.Text = activelyListening
                ? "Argus AI Copilot — Listening"
                : "Argus AI Copilot";

            _logger.LogDebug("Tray menu updated for mode {Mode}.", mode);
        });
    }
}
