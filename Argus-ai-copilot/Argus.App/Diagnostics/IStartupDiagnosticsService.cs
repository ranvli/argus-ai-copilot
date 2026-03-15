namespace Argus.App.Diagnostics;

/// <summary>
/// Holds the startup diagnostics snapshot and exposes it to the rest of the application.
/// The snapshot is produced once by <see cref="StartupDiagnosticsService"/> on startup
/// and never changes during the application's lifetime.
/// </summary>
public interface IStartupDiagnosticsService
{
    /// <summary>
    /// The diagnostics result. Null until the hosted service has completed its startup run.
    /// </summary>
    StartupDiagnosticsResult? Result { get; }

    /// <summary>Raised once when the diagnostics run completes.</summary>
    event EventHandler<StartupDiagnosticsResult> DiagnosticsReady;
}
