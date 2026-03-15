using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App.Services;

/// <summary>
/// Long-running background service that will coordinate Argus listening sessions.
/// Placeholder only — no processing logic yet.
///
/// Future responsibilities:
///   - React to IAppStateService.ModeChanged events.
///   - Coordinate audio capture  →  transcription  →  AI processing pipeline.
///   - Manage session context and memory across turns.
/// </summary>
internal sealed class SessionCoordinatorService : BackgroundService
{
    private readonly ILogger<SessionCoordinatorService> _logger;
    private readonly IAppStateService _appState;

    public SessionCoordinatorService(
        ILogger<SessionCoordinatorService> logger,
        IAppStateService appState)
    {
        _logger = logger;
        _appState = appState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Service} running.", nameof(SessionCoordinatorService));

        // TODO: subscribe to _appState.ModeChanged and drive the session pipeline.
        // For now, suspend until the host requests cancellation.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on clean shutdown — not an error.
        }

        _logger.LogInformation("{Service} stopped.", nameof(SessionCoordinatorService));
    }
}
