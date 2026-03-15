namespace Argus.Infrastructure.Storage;

/// <summary>
/// Controls how ArgusAI selects root directories for its storage categories.
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// All storage rooted under <c>%LocalAppData%\ArgusAI\</c>.
    /// Safe, predictable, always works. This is the default.
    /// </summary>
    Default,

    /// <summary>
    /// Evaluates available fixed local drives and distributes storage:
    /// database/cache/logs go to the fastest stable drive,
    /// artifacts go to the drive with the most free space.
    /// Excludes removable and network drives.
    /// </summary>
    AutoBestDrive,

    /// <summary>
    /// Uses the explicit root paths supplied in <see cref="StorageOptions"/>.
    /// Any path left null falls back to the Default location.
    /// </summary>
    Custom
}
