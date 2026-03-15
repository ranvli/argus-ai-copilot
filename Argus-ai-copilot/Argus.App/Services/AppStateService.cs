using Microsoft.Extensions.Logging;

namespace Argus.App.Services;

internal sealed class AppStateService : IAppStateService
{
    private readonly ILogger<AppStateService> _logger;

    public bool IsListening { get; private set; }
    public bool IsPaused { get; private set; }
    public AppMode CurrentMode { get; private set; } = AppMode.Idle;

    public event EventHandler<AppMode>? ModeChanged;

    public AppStateService(ILogger<AppStateService> logger) => _logger = logger;

    public void StartListening()
    {
        IsListening = true;
        IsPaused = false;
        SetMode(AppMode.Listening);
        _logger.LogInformation("Listening started.");
    }

    public void PauseListening()
    {
        if (!IsListening)
            return;

        IsPaused = true;
        SetMode(AppMode.Idle);
        _logger.LogInformation("Listening paused.");
    }

    public void StopListening()
    {
        IsListening = false;
        IsPaused = false;
        SetMode(AppMode.Idle);
        _logger.LogInformation("Listening stopped.");
    }

    private void SetMode(AppMode mode)
    {
        if (CurrentMode == mode)
            return;

        CurrentMode = mode;
        _logger.LogDebug("App mode → {Mode}.", mode);
        ModeChanged?.Invoke(this, mode);
    }
}
