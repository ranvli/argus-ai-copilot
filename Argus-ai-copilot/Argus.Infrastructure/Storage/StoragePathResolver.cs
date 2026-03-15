using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Infrastructure.Storage;

/// <summary>
/// Implements <see cref="IStoragePathResolver"/> by evaluating drives and
/// applying the configured <see cref="StorageMode"/>.
/// </summary>
public sealed class StoragePathResolver : IStoragePathResolver
{
    private static readonly string DefaultLocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private readonly StorageOptions _options;
    private readonly DriveEvaluator _driveEvaluator;
    private readonly ILogger<StoragePathResolver> _logger;

    public StoragePathResolver(
        IOptions<StorageOptions> options,
        DriveEvaluator driveEvaluator,
        ILogger<StoragePathResolver> logger)
    {
        _options       = options.Value;
        _driveEvaluator = driveEvaluator;
        _logger        = logger;
    }

    public ResolvedStoragePaths Resolve()
    {
        _logger.LogInformation("Resolving storage paths. Mode={Mode}", _options.Mode);

        return _options.Mode switch
        {
            StorageMode.AutoBestDrive => ResolveAutoBestDrive(),
            StorageMode.Custom        => ResolveCustom(),
            _                         => ResolveDefault()
        };
    }

    // ── Default ───────────────────────────────────────────────────────────────

    private ResolvedStoragePaths ResolveDefault()
    {
        const string reason = "Default (LocalApplicationData)";
        _logger.LogInformation("Storage mode: Default → {Root}", DefaultLocalAppData);

        return new ResolvedStoragePaths
        {
            Mode             = StorageMode.Default,
            DataRoot         = DefaultLocalAppData,
            CacheRoot        = DefaultLocalAppData,
            ArtifactRoot     = DefaultLocalAppData,
            DataRootReason   = reason,
            CacheRootReason  = reason,
            ArtifactRootReason = reason
        };
    }

    // ── AutoBestDrive ─────────────────────────────────────────────────────────

    private ResolvedStoragePaths ResolveAutoBestDrive()
    {
        var candidates = _driveEvaluator.GetCandidates(_options.MinFreeSpaceMb);

        if (candidates.Count == 0)
        {
            _logger.LogWarning(
                "AutoBestDrive: no eligible drives found (min free space {MinMb} MB). " +
                "Falling back to Default.", _options.MinFreeSpaceMb);
            return ResolveDefault();
        }

        // Data (db + logs): best overall score — first in the sorted list.
        var dataDrive = candidates[0];

        // Cache: same as data drive (locality preferred for small files).
        var cacheDrive = candidates[0];

        // Artifacts: drive with the most free space that meets the larger threshold.
        var artifactDrive = SelectArtifactDrive(candidates) ?? dataDrive;

        LogSelections(dataDrive, cacheDrive, artifactDrive);

        return new ResolvedStoragePaths
        {
            Mode               = StorageMode.AutoBestDrive,
            DataRoot           = dataDrive.RootPath,
            CacheRoot          = cacheDrive.RootPath,
            ArtifactRoot       = artifactDrive.RootPath,
            DataRootReason     = $"AutoBestDrive: {dataDrive.Label} — {dataDrive.SelectionReason}",
            CacheRootReason    = $"AutoBestDrive: {cacheDrive.Label} — {cacheDrive.SelectionReason}",
            ArtifactRootReason = $"AutoBestDrive: {artifactDrive.Label} — {artifactDrive.SelectionReason}"
        };
    }

    private DriveCandidate? SelectArtifactDrive(IReadOnlyList<DriveCandidate> candidates)
    {
        long minBytes = _options.MinArtifactFreeSpaceMb * 1024L * 1024L;

        // Among drives that meet the artifact threshold, pick the one with the most free space.
        return candidates
            .Where(c => c.FreeSizeBytes >= minBytes)
            .OrderByDescending(c => c.FreeSizeBytes)
            .FirstOrDefault();
    }

    private void LogSelections(DriveCandidate data, DriveCandidate cache, DriveCandidate artifact)
    {
        _logger.LogInformation(
            "AutoBestDrive selections: Data={DataDrive} ({DataFreeGb:F1}GB), " +
            "Cache={CacheDrive} ({CacheFreeGb:F1}GB), " +
            "Artifacts={ArtifactDrive} ({ArtifactFreeGb:F1}GB)",
            data.Label,     data.FreeGb,
            cache.Label,    cache.FreeGb,
            artifact.Label, artifact.FreeGb);
    }

    // ── Custom ────────────────────────────────────────────────────────────────

    private ResolvedStoragePaths ResolveCustom()
    {
        var dataRoot     = Normalise(_options.DataRootOverride,     DefaultLocalAppData, "DataRoot");
        var cacheRoot    = Normalise(_options.CacheRootOverride,    DefaultLocalAppData, "CacheRoot");
        var artifactRoot = Normalise(_options.ArtifactRootOverride, DefaultLocalAppData, "ArtifactRoot");

        return new ResolvedStoragePaths
        {
            Mode               = StorageMode.Custom,
            DataRoot           = dataRoot,
            CacheRoot          = cacheRoot,
            ArtifactRoot       = artifactRoot,
            DataRootReason     = _options.DataRootOverride is not null
                                     ? $"Custom override: {dataRoot}"
                                     : "Custom (no override set, using Default)",
            CacheRootReason    = _options.CacheRootOverride is not null
                                     ? $"Custom override: {cacheRoot}"
                                     : "Custom (no override set, using Default)",
            ArtifactRootReason = _options.ArtifactRootOverride is not null
                                     ? $"Custom override: {artifactRoot}"
                                     : "Custom (no override set, using Default)"
        };
    }

    private string Normalise(string? configured, string fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            _logger.LogDebug("Custom storage {Name}: no override configured, using Default.", name);
            return fallback;
        }

        _logger.LogInformation("Custom storage {Name}: {Path}", name, configured);
        return configured;
    }
}
