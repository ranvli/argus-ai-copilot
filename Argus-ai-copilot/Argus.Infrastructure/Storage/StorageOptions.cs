namespace Argus.Infrastructure.Storage;

/// <summary>
/// Application storage preferences, bound to the <c>"Storage"</c> configuration section.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// How storage roots are chosen.
    /// Defaults to <see cref="StorageMode.Default"/> (LocalApplicationData).
    /// </summary>
    public StorageMode Mode { get; set; } = StorageMode.Default;

    /// <summary>
    /// Custom root for database and logs.
    /// Used when <see cref="Mode"/> is <see cref="StorageMode.Custom"/>.
    /// Null means fall back to Default.
    /// </summary>
    public string? DataRootOverride { get; set; }

    /// <summary>
    /// Custom root for transient cache files.
    /// Used when <see cref="Mode"/> is <see cref="StorageMode.Custom"/>.
    /// Null means fall back to Default.
    /// </summary>
    public string? CacheRootOverride { get; set; }

    /// <summary>
    /// Custom root for large binary artifact files (audio, screenshots, recordings).
    /// Used when <see cref="Mode"/> is <see cref="StorageMode.Custom"/>.
    /// Null means fall back to Default.
    /// </summary>
    public string? ArtifactRootOverride { get; set; }

    /// <summary>
    /// Minimum free space in MB a drive must have to be considered for AutoBestDrive.
    /// Defaults to 512 MB.
    /// </summary>
    public long MinFreeSpaceMb { get; set; } = 512;

    /// <summary>
    /// Minimum free space in MB a drive must have to be selected for large artifacts.
    /// Defaults to 2048 MB (2 GB).
    /// </summary>
    public long MinArtifactFreeSpaceMb { get; set; } = 2048;
}
