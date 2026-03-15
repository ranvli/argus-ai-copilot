using Argus.Infrastructure.Persistence;
using Argus.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.Infrastructure.Data;

/// <summary>
/// Hosted service that runs once at application startup.
/// Ensures the local folder structure exists and applies any pending
/// EF Core migrations so the SQLite database is always up to date.
/// Runs before any other hosted service processes requests.
/// </summary>
public sealed class DbInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPathProvider _paths;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(
        IServiceScopeFactory scopeFactory,
        IPathProvider paths,
        ILogger<DbInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _paths = paths;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ArgusAI data folder: {Root}", _paths.AppDataRoot);

        // 1. Ensure all local folders exist (idempotent, safe on first run).
        _paths.EnsureDirectoriesExist();
        _logger.LogDebug("Local directory structure verified");

        // 2. Apply EF Core migrations — creates the DB on first run, migrates on upgrade.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ArgusDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            _logger.LogInformation("Applying {Count} pending migration(s): {Names}",
                pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("Database migrations applied successfully");
        }
        else
        {
            // EnsureCreated is used as a fallback when no migrations exist yet
            // (e.g. during development before the first `dotnet ef migrations add`).
            await db.Database.EnsureCreatedAsync(cancellationToken);
            _logger.LogDebug("Database schema is up to date");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
