using Microsoft.Extensions.Logging;

namespace Argus.Infrastructure.Storage;

/// <summary>
/// Evaluates available fixed local drives and produces a ranked list
/// of <see cref="DriveCandidate"/> records for use by <see cref="StoragePathResolver"/>.
///
/// Scoring heuristics (higher = better):
/// <list type="bullet">
///   <item>SSD / fixed drive: +40 pts</item>
///   <item>System drive (same as Windows): +10 pts</item>
///   <item>Free space ≥ 10 GB: +20 pts; ≥ 50 GB: +10 more pts</item>
///   <item>Drive letters near C: slight tie-breaking preference</item>
/// </list>
/// Network and removable drives are always excluded.
/// </summary>
public sealed class DriveEvaluator
{
    private readonly ILogger<DriveEvaluator> _logger;

    public DriveEvaluator(ILogger<DriveEvaluator> logger) => _logger = logger;

    /// <summary>
    /// Returns all eligible fixed local drives, scored and sorted descending.
    /// </summary>
    public IReadOnlyList<DriveCandidate> GetCandidates(long minFreeSpaceMb)
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(
            Environment.SpecialFolder.Windows)) ?? "C:\\";

        var candidates = new List<DriveCandidate>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;

                // Exclude removable, network, CD-ROM
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Ram)) continue;

                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var freeMb = drive.AvailableFreeSpace / (1024.0 * 1024);

                if (freeMb < minFreeSpaceMb)
                {
                    _logger.LogDebug(
                        "Drive {Root} skipped: {FreeMb:F0} MB free < {MinMb} MB threshold.",
                        drive.RootDirectory.FullName, freeMb, minFreeSpaceMb);
                    continue;
                }

                var score = ScoreDrive(drive, freeGb, systemRoot);
                var reason = BuildReason(drive, freeGb, score);

                candidates.Add(new DriveCandidate
                {
                    RootPath  = drive.RootDirectory.FullName,
                    DriveType = drive.DriveType,
                    Label     = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                                    ? drive.Name
                                    : $"{drive.Name} ({drive.VolumeLabel})",
                    TotalSizeBytes = drive.TotalSize,
                    FreeSizeBytes  = drive.AvailableFreeSpace,
                    Score          = score,
                    SelectionReason = reason
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not evaluate drive {Name}.", drive.Name);
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        _logger.LogInformation(
            "Drive evaluation: {Count} candidate(s) found. Top: {Top}",
            candidates.Count,
            candidates.Count > 0 ? candidates[0].Label : "none");

        return candidates.AsReadOnly();
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static int ScoreDrive(DriveInfo drive, double freeGb, string systemRoot)
    {
        int score = 0;

        // Fixed drive gets baseline points
        if (drive.DriveType == DriveType.Fixed) score += 40;

        // System drive is a known-good location
        var isSystemDrive = string.Equals(
            drive.RootDirectory.FullName,
            systemRoot,
            StringComparison.OrdinalIgnoreCase);
        if (isSystemDrive) score += 10;

        // Free space bonus
        if (freeGb >= 10) score += 20;
        if (freeGb >= 50) score += 10;
        if (freeGb >= 200) score += 5;

        // Prefer drives with shorter root paths (C:\ > D:\ > ... is arbitrary;
        // this just creates stable ordering between equal-score drives)
        score -= drive.RootDirectory.FullName.Length;

        return score;
    }

    private static string BuildReason(DriveInfo drive, double freeGb, int score)
    {
        var parts = new List<string>
        {
            $"{drive.DriveType}",
            $"{freeGb:F1} GB free",
            $"score={score}"
        };
        if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
            parts.Insert(0, drive.VolumeLabel);
        return string.Join(", ", parts);
    }
}

/// <summary>A scored drive candidate produced by <see cref="DriveEvaluator"/>.</summary>
public sealed class DriveCandidate
{
    /// <summary>Drive root path, e.g. <c>C:\</c>.</summary>
    public required string RootPath { get; init; }

    public DriveType DriveType { get; init; }

    /// <summary>Volume label + drive letter, e.g. <c>C:\ (OS)</c>.</summary>
    public required string Label { get; init; }

    public long TotalSizeBytes { get; init; }
    public long FreeSizeBytes  { get; init; }

    /// <summary>Composite score — higher is preferred.</summary>
    public int Score { get; init; }

    /// <summary>Human-readable explanation of why this drive was scored this way.</summary>
    public required string SelectionReason { get; init; }

    public double FreeGb => FreeSizeBytes / (1024.0 * 1024 * 1024);
}
