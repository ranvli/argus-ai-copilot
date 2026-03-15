using Argus.AI;
using Argus.App.Configuration;
using Argus.App.Diagnostics;
using Argus.App.Services;
using Argus.Context;
using Argus.Core.Contracts.Services;
using Argus.Infrastructure;
using Argus.Infrastructure.Storage;
using Argus.Transcription;
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
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                // Additional sources (user secrets, key vault, etc.) go here.
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

                services.Configure<RoutingOptions>(
                    ctx.Configuration.GetSection(RoutingOptions.SectionName));

                services.Configure<StorageOptions>(
                    ctx.Configuration.GetSection(StorageOptions.SectionName));

                // ── Infrastructure: SQLite, repositories, artifact storage ─────
                // DbInitializer is registered inside this call as a hosted service
                // and will run before SessionCoordinatorService starts.
                services.AddArgusInfrastructure();

                // ── AI layer: providers, discovery, model selection ────────────
                services.AddArgusAI(ctx.Configuration);

                // ── Context layer: active window tracking ─────────────────────
                services.AddArgusContext();

                // ── Audio capture + transcription pipeline ─────────────────────
                services.AddArgusTranscription();

                // ── Core application services ──────────────────────────────────
                services.AddSingleton<IAppBootstrapper, AppBootstrapper>();

                // ── Application state ──────────────────────────────────────────
                services.AddSingleton<IAppStateService, AppStateService>();

                // ── System-tray ────────────────────────────────────────────────
                // Registered as both ITrayService and IHostedService.
                // A single instance is shared via the factory delegate.
                services.AddSingleton<TrayService>();
                services.AddSingleton<ITrayService>(sp => sp.GetRequiredService<TrayService>());
                services.AddHostedService(sp => sp.GetRequiredService<TrayService>());

                // ── Session coordinator ────────────────────────────────────────
                // Singleton so it can be injected as ISessionCoordinator anywhere.
                // Also registered as IHostedService for the BackgroundService pump.
                services.AddSingleton<AssistantReactionService>();
                services.AddSingleton<IAssistantReactionPublisher>(
                    sp => sp.GetRequiredService<AssistantReactionService>());
                services.AddSingleton<SessionCoordinatorService>();
                services.AddSingleton<ISessionCoordinator>(
                    sp => sp.GetRequiredService<SessionCoordinatorService>());
                services.AddSingleton<ISessionStatePublisher>(
                    sp => sp.GetRequiredService<SessionCoordinatorService>());
                services.AddSingleton<IAudioStatusPublisher>(
                    sp => sp.GetRequiredService<SessionCoordinatorService>());
                services.AddHostedService(
                    sp => sp.GetRequiredService<SessionCoordinatorService>());

                // ── Startup diagnostics ────────────────────────────────────────
                // Singleton: holds the result; also IHostedService to run the check.
                services.AddSingleton<StartupDiagnosticsService>();
                services.AddSingleton<IStartupDiagnosticsService>(
                    sp => sp.GetRequiredService<StartupDiagnosticsService>());
                services.AddHostedService(
                    sp => sp.GetRequiredService<StartupDiagnosticsService>());

                // ── Application windows ────────────────────────────────────────
                services.AddSingleton<MainWindow>();
            });
}
