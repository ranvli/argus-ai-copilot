namespace Argus.Infrastructure.Storage;

/// <summary>
/// Resolves effective storage roots according to the configured <see cref="StorageMode"/>.
/// The result is computed once at startup and cached for the application lifetime.
/// </summary>
public interface IStoragePathResolver
{
    /// <summary>
    /// Returns the effective resolved paths for the current configuration.
    /// This is called once at startup by <see cref="StorageAwarePathProvider"/>.
    /// </summary>
    ResolvedStoragePaths Resolve();
}
