using Argus.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Threading;

namespace Argus.App;

public partial class App
{
    private IHost _host = null!;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Global exception handlers ─────────────────────────────────────────
        // Installed before the host starts so any crash during startup is also caught.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Tray-first: keep the process alive even when no windows are open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _host = Program.CreateHostBuilder().Build();
        await _host.StartAsync();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("[App] Host started. Global exception handlers are active.");

        _host.Services.GetRequiredService<IAppBootstrapper>().Initialize();

        // MainWindow is NOT shown here. The tray "Open Dashboard" action shows it.
        // TrayService.StartAsync() makes the tray icon appear instead.
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("[App] OnExit called. ExitCode={Code}", e.ApplicationExitCode);
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();
        base.OnExit(e);
    }

    // ── Global exception handlers ─────────────────────────────────────────────

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Logs the exception and keeps the app alive so we can read the log.
        Log($"[App] UNHANDLED DISPATCHER EXCEPTION: {e.Exception}");
        e.Handled = true;   // prevent immediate process termination
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // IsTerminating=true means the CLR is about to kill the process (exit 0xFFFFFFFF).
        Log($"[App] UNHANDLED APPDOMAIN EXCEPTION (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log($"[App] UNOBSERVED TASK EXCEPTION: {e.Exception}");
        e.SetObserved();   // prevents the CLR from re-throwing on finaliser thread
    }

    private void Log(string message)
    {
        // _logger may be null if the crash happens before the host is built.
        if (_logger is not null)
            _logger.LogError(message);
        else
            System.Diagnostics.Debug.WriteLine(message);
    }
}
