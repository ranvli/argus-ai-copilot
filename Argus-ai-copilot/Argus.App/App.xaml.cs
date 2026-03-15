using Argus.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;

namespace Argus.App;

public partial class App
{
    private IHost _host = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Tray-first: keep the process alive even when no windows are open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _host = Program.CreateHostBuilder().Build();
        await _host.StartAsync();

        _host.Services.GetRequiredService<IAppBootstrapper>().Initialize();

        // MainWindow is NOT shown here. The tray "Open Dashboard" action shows it.
        // TrayService.StartAsync() makes the tray icon appear instead.
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();

        base.OnExit(e);
    }
}
