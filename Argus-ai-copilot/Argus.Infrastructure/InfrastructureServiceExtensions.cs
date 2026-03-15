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
    /// storage options, drive evaluator, path resolver, path provider,
    /// artifact storage, EF Core / SQLite, all repositories,
    /// and the startup database initializer.
    /// Call this from <c>Program.cs</c> inside <c>ConfigureServices</c>.
    /// </summary>
    public static IServiceCollection AddArgusInfrastructure(this IServiceCollection services)
    {
        // ── Storage options ───────────────────────────────────────────────────
        // Ensure StorageOptions is registered with default values.
        // Program.cs binds the "Storage" configuration section via Configure<StorageOptions>.
        services.AddOptions<StorageOptions>();

        // ── Drive evaluator ───────────────────────────────────────────────────
        // Transient — called once at startup, no state kept after resolution.
        services.AddTransient<DriveEvaluator>();

        // ── Storage path resolver ─────────────────────────────────────────────
        // Singleton — resolves once, result is immutable.
        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();

        // ── Path provider ─────────────────────────────────────────────────────
        // Singleton — delegates to the resolver; all path properties are derived
        // from the single ResolvedStoragePaths computed on first construction.
        services.AddSingleton<IPathProvider, PathProvider>();

        // ── Artifact storage ──────────────────────────────────────────────────
        services.AddSingleton<IArtifactStorage, ArtifactStorage>();

        // ── EF Core DbContext — SQLite ─────────────────────────────────────────
        services.AddDbContext<ArgusDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IPathProvider>();
            options.UseSqlite($"Data Source={paths.DatabasePath}");
        });

        // ── Repositories ───────────────────────────────────────────────────────
        services.AddScoped<ISessionRepository,        SessionRepository>();
        services.AddScoped<ITranscriptRepository,     TranscriptRepository>();
        services.AddScoped<IScreenshotRepository,     ScreenshotRepository>();
        services.AddScoped<IRecordingRepository,      RecordingRepository>();
        services.AddScoped<ISessionSummaryRepository, SessionSummaryRepository>();
        services.AddScoped<ISettingsRepository,       SettingsRepository>();
        services.AddScoped<IAppEventRepository,       AppEventRepository>();
        services.AddScoped<ISpeakerProfileRepository, SpeakerProfileRepository>();

        // ── DB initializer ────────────────────────────────────────────────────
        services.AddHostedService<DbInitializer>();

        return services;
    }
}
