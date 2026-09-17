using Argus.AI;
using Argus.App.Configuration;
using Argus.App.Diagnostics;
using Argus.App.Services;
using Argus.Context;
using Argus.Core.Contracts.Services;
using Argus.Infrastructure;
using Argus.Infrastructure.Storage;
using Argus.Transcription;
using Argus.Transcription.Configuration;
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

            .ConfigureAppConfiguration((ctx, cfg) =>
            {
            })

            .ConfigureLogging((ctx, logging) =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddFilter("Microsoft", LogLevel.Warning);
                logging.AddFilter("System", LogLevel.Warning);
                logging.AddFilter("Argus", LogLevel.Debug);
                logging.AddDebug();

                if (ctx.HostingEnvironment.IsDevelopment())
                    logging.AddConsole();

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var logDirectory = Path.Combine(localAppData, "ArgusAI", "logs");
                logging.AddProvider(new DiagnosticFileLoggerProvider(logDirectory));
            })

            .ConfigureServices((ctx, services) =>
            {
                services.Configure<ApplicationOptions>(
                    ctx.Configuration.GetSection(ApplicationOptions.SectionName));

                services.Configure<ProvidersOptions>(
                    ctx.Configuration.GetSection(ProvidersOptions.SectionName));

                services.Configure<RoutingOptions>(
                    ctx.Configuration.GetSection(RoutingOptions.SectionName));

                services.Configure<StorageOptions>(
                    ctx.Configuration.GetSection(StorageOptions.SectionName));

                services.Configure<TranscriptionRuntimeSettings>(
                    ctx.Configuration.GetSection(TranscriptionRuntimeSettings.SectionName));

                services.AddArgusInfrastructure();
                services.AddArgusAI(ctx.Configuration);
                services.AddArgusContext();
                services.AddArgusTranscription();

                services.AddSingleton<IAppBootstrapper, AppBootstrapper>();
                services.AddSingleton<IAppStateService, AppStateService>();

                services.AddSingleton<TrayService>();
                services.AddSingleton<ITrayService>(sp => sp.GetRequiredService<TrayService>());
                services.AddHostedService(sp => sp.GetRequiredService<TrayService>());

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

                services.AddSingleton<StartupDiagnosticsService>();
                services.AddSingleton<IStartupDiagnosticsService>(
                    sp => sp.GetRequiredService<StartupDiagnosticsService>());
                services.AddHostedService(
                    sp => sp.GetRequiredService<StartupDiagnosticsService>());

                services.AddSingleton<SherpaModelBootstrapService>();
                services.AddHostedService(sp => sp.GetRequiredService<SherpaModelBootstrapService>());
                services.AddSingleton<SherpaNativePreflightHostedService>();
                services.AddHostedService(sp => sp.GetRequiredService<SherpaNativePreflightHostedService>());

                services.AddSingleton<MainWindow>();
            });
}
