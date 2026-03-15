namespace Argus.App.Services;

/// <summary>
/// Runs one-time application initialization tasks before the main window appears.
/// Inject this wherever startup state needs to be verified or acted on.
/// </summary>
public interface IAppBootstrapper
{
    /// <summary>
    /// Executes all startup initialization steps.
    /// Safe to call multiple times — only runs once.
    /// </summary>
    void Initialize();

    /// <summary>True after <see cref="Initialize"/> has completed successfully.</summary>
    bool IsInitialized { get; }
}
