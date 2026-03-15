using Argus.App.Configuration;
using Argus.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App;

/// <summary>
/// Composition root for the Generic Host.
/// The WPF-generated entry point (App.xaml → App.g.cs) calls App.OnStartup,
/// which calls CreateHostBuilder().Build() before showing any UI.
/// </summary>
internal static class Program
{
    internal static IHostBuilder CreateHostBuilder() =>
        Host.CreateDefaultBuilder()

            // ── Configuration ─────────────────────────────────────────────────
            // CreateDefaultBuilder already adds:
            //   appsettings.json, appsettings.{Environment}.json,
            //   environment variables, and command-line args.
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                // Add extra sources here when needed.
                // Example: cfg.AddUserSecrets<App>();
            })

            // ── Logging ───────────────────────────────────────────────────────
            .ConfigureLogging((ctx, logging) =>
            {
                logging.ClearProviders();
                logging.AddDebug();

                if (ctx.HostingEnvironment.IsDevelopment())
                    logging.AddConsole();
            })

            // ── Services ──────────────────────────────────────────────────────
            .ConfigureServices((ctx, services) =>
            {
                // ── Strong-typed configuration sections ───────────────────────
                services.Configure<ApplicationOptions>(
                    ctx.Configuration.GetSection(ApplicationOptions.SectionName));

                services.Configure<ProvidersOptions>(
                    ctx.Configuration.GetSection(ProvidersOptions.SectionName));

                // ── Core application services ──────────────────────────────────
                services.AddSingleton<IAppBootstrapper, AppBootstrapper>();

                // ── Application state ──────────────────────────────────────────
                services.AddSingleton<IAppStateService, AppStateService>();

                // ── System-tray ────────────────────────────────────────────────
                // Registered as both ITrayService (for injection by other services)
                // and IHostedService (for host lifecycle). A single instance is
                // shared via the factory delegate — no duplicate construction.
                services.AddSingleton<TrayService>();
                services.AddSingleton<ITrayService>(sp => sp.GetRequiredService<TrayService>());
                services.AddHostedService(sp => sp.GetRequiredService<TrayService>());

                // ── Background session coordinator ─────────────────────────────
                services.AddHostedService<SessionCoordinatorService>();

                // ── Application windows ────────────────────────────────────────
                services.AddSingleton<MainWindow>();
            });
}
