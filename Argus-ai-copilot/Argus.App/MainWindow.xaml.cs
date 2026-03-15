using Argus.App.Services;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows;

namespace Argus.App;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly IAppBootstrapper _bootstrapper;

    public MainWindow(ILogger<MainWindow> logger, IAppBootstrapper bootstrapper)
    {
        _logger = logger;
        _bootstrapper = bootstrapper;

        InitializeComponent();

        _logger.LogInformation(
            "MainWindow initialized. Bootstrap complete: {IsBootstrapped}",
            _bootstrapper.IsInitialized);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Tray-first: intercept the close gesture and hide the window instead.
        // This preserves the singleton instance so the tray can re-show it
        // without recreating it. Actual shutdown goes through the tray Exit action.
        e.Cancel = true;
        Hide();
        _logger.LogDebug("MainWindow hidden (close intercepted for tray-first mode).");

        base.OnClosing(e);
    }
}
