using Argus.Core.Contracts.Repositories;
using Argus.Infrastructure.Persistence;
using Argus.Infrastructure.Repositories;
using Argus.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Infrastructure;

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers all Argus.Infrastructure services: EF Core/SQLite, repositories, and artifact storage.
    /// Call this from Program.cs / ConfigureServices.
    /// </summary>
    public static IServiceCollection AddArgusInfrastructure(this IServiceCollection services)
    {
        // ── Path provider (singleton — stateless, path resolution only) ────────
        services.AddSingleton<IPathProvider, PathProvider>();

        // ── Artifact storage (singleton — file I/O helpers) ───────────────────
        services.AddSingleton<IArtifactStorage, ArtifactStorage>();

        // ── EF Core DbContext — SQLite, path resolved at runtime ───────────────
        services.AddDbContext<ArgusDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IPathProvider>();
            paths.EnsureDirectoriesExist();
            options.UseSqlite($"Data Source={paths.DatabasePath}");
        });

        // ── Repositories ───────────────────────────────────────────────────────
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ITranscriptRepository, TranscriptRepository>();
        services.AddScoped<IScreenshotRepository, ScreenshotRepository>();
        services.AddScoped<IRecordingRepository, RecordingRepository>();
        services.AddScoped<ISessionSummaryRepository, SessionSummaryRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        return services;
    }

    /// <summary>
    /// Applies any pending EF Core migrations at startup.
    /// Call after the host is built, before starting it — or inside IHostedService.
    /// </summary>
    public static void ApplyArgusDbMigrations(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();
        db.Database.Migrate();
    }
}
