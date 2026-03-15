using Argus.AI.Discovery;
using Argus.AI.Selection;
using Argus.App.Configuration;
using Argus.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;

namespace Argus.App.Diagnostics;

/// <summary>
/// Runs once at application startup.
/// Checks storage, database, and provider availability, then exposes a
/// <see cref="StartupDiagnosticsResult"/> singleton via <see cref="IStartupDiagnosticsService"/>.
/// </summary>
public sealed class StartupDiagnosticsService : IHostedService, IStartupDiagnosticsService
{
    private readonly IPathProvider _pathProvider;
    private readonly IProviderDiscoveryService _discovery;
    private readonly IModelSelectionService _selection;
    private readonly IOptions<RoutingOptions> _routingOptions;
    private readonly ILogger<StartupDiagnosticsService> _logger;

    private volatile StartupDiagnosticsResult? _result;

    public StartupDiagnosticsResult? Result => _result;

    public event EventHandler<StartupDiagnosticsResult>? DiagnosticsReady;

    public StartupDiagnosticsService(
        IPathProvider pathProvider,
        IProviderDiscoveryService discovery,
        IModelSelectionService selection,
        IOptions<RoutingOptions> routingOptions,
        ILogger<StartupDiagnosticsService> logger)
    {
        _pathProvider   = pathProvider;
        _discovery      = discovery;
        _selection      = selection;
        _routingOptions = routingOptions;
        _logger         = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartupDiagnosticsService: beginning diagnostics run.");

        var resolved = _pathProvider.ResolvedPaths;

        _logger.LogInformation(
            "Storage mode: {Mode}. DataRoot={DataRoot}, CacheRoot={CacheRoot}, ArtifactRoot={ArtifactRoot}",
            resolved.ModeDisplay, resolved.DataRoot, resolved.CacheRoot, resolved.ArtifactRoot);

        var (storageOk, storageError) = CheckStorage(resolved);
        var (dbOk, dbError)           = CheckDatabase(resolved);
        var providerResult            = await RunProviderDiscoveryAsync(cancellationToken);
        var routing                   = BuildEffectiveRouting(providerResult);

        var result = new StartupDiagnosticsResult
        {
            CapturedAt        = DateTimeOffset.UtcNow,
            StoragePaths      = resolved,
            StorageAvailable  = storageOk,
            StorageError      = storageError,
            DatabaseAvailable = dbOk,
            DatabaseError     = dbError,
            ProviderDiscovery = providerResult,
            EffectiveRouting  = routing
        };

        _result = result;

        _logger.LogInformation(
            "Diagnostics complete. Storage={StorageOk}, DB={DbOk}, " +
            "Ollama={OllamaStatus}, OpenAI={OpenAiStatus}, RoutingMode={Mode}",
            storageOk, dbOk,
            providerResult.OllamaAvailability,
            providerResult.OpenAiAvailability,
            routing.Mode);

        DiagnosticsReady?.Invoke(this, result);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Storage ───────────────────────────────────────────────────────────────

    private (bool ok, string? error) CheckStorage(ResolvedStoragePaths resolved)
    {
        try
        {
            // Probe all three roots
            ProbeRoot(resolved.DataRoot);
            ProbeRoot(resolved.CacheRoot);
            ProbeRoot(resolved.ArtifactRoot);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Storage check failed.");
            return (false, ex.Message);
        }
    }

    private void ProbeRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            _logger.LogInformation("Created root directory: {Path}", root);
        }

        var probe = Path.Combine(root, ".write-probe");
        File.WriteAllText(probe, "probe");
        File.Delete(probe);
    }

    // ── Database ──────────────────────────────────────────────────────────────

    private (bool ok, string? error) CheckDatabase(ResolvedStoragePaths resolved)
    {
        try
        {
            var dbPath = resolved.DatabasePath;
            return (File.Exists(dbPath), File.Exists(dbPath) ? null : "Database file not found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database check failed.");
            return (false, ex.Message);
        }
    }

    // ── Provider discovery ────────────────────────────────────────────────────

    private async Task<ProviderDiscoveryResult> RunProviderDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            return await _discovery.DiscoverAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provider discovery threw an unexpected exception.");
            return new ProviderDiscoveryResult
            {
                OllamaAvailability = ProviderAvailability.Error,
                OllamaError        = ex.Message,
                OpenAiAvailability = ProviderAvailability.NotConfigured
            };
        }
    }

    // ── Model selection ───────────────────────────────────────────────────────

    private EffectiveRoutingResult BuildEffectiveRouting(ProviderDiscoveryResult providerResult)
    {
        try
        {
            var mode = _routingOptions.Value.Mode;
            return _selection.Select(providerResult, mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model selection failed.");
            return new EffectiveRoutingResult { Mode = _routingOptions.Value.Mode };
        }
    }
}

