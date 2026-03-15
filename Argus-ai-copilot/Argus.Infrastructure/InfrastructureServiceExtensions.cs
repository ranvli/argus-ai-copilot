using Argus.Core.Contracts.Repositories;
using Argus.Infrastructure.Data;
using Argus.Infrastructure.Persistence;
using Argus.Infrastructure.Repositories;
using Argus.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Infrastructure;

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers all Argus.Infrastructure services:
    /// path provider, artifact storage, EF Core / SQLite, all repositories,
    /// and the startup database initializer.
    /// Call this from <c>Program.cs</c> inside <c>ConfigureServices</c>.
    /// </summary>
    public static IServiceCollection AddArgusInfrastructure(this IServiceCollection services)
    {
        // ── Path provider ─────────────────────────────────────────────────────
        // Singleton — stateless, only computes paths from Environment.SpecialFolder.
        services.AddSingleton<IPathProvider, PathProvider>();

        // ── Artifact storage ──────────────────────────────────────────────────
        // Singleton — pure file-I/O helper, thread-safe.
        services.AddSingleton<IArtifactStorage, ArtifactStorage>();

        // ── EF Core DbContext — SQLite ─────────────────────────────────────────
        // Path is resolved via IPathProvider so it is always under %LocalAppData%\ArgusAI\data\.
        services.AddDbContext<ArgusDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IPathProvider>();
            options.UseSqlite($"Data Source={paths.DatabasePath}");
        });

        // ── Repositories ───────────────────────────────────────────────────────
        // Scoped — one instance per DI scope (matches DbContext lifetime).
        services.AddScoped<ISessionRepository,        SessionRepository>();
        services.AddScoped<ITranscriptRepository,     TranscriptRepository>();
        services.AddScoped<IScreenshotRepository,     ScreenshotRepository>();
        services.AddScoped<IRecordingRepository,      RecordingRepository>();
        services.AddScoped<ISessionSummaryRepository, SessionSummaryRepository>();
        services.AddScoped<ISettingsRepository,       SettingsRepository>();
        services.AddScoped<IAppEventRepository,       AppEventRepository>();
        services.AddScoped<ISpeakerProfileRepository, SpeakerProfileRepository>();

        // ── DB initializer ────────────────────────────────────────────────────
        // Runs once at startup: creates directories + applies EF migrations.
        // Registered as the FIRST hosted service so it completes before anything
        // else (session coordinator, tray service) tries to use the database.
        services.AddHostedService<DbInitializer>();

        return services;
    }
}

