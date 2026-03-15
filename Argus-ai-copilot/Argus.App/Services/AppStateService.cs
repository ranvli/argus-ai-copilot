using Argus.Core.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Argus.App.Services;

internal sealed class AppStateService : IAppStateService
{
    private readonly ILogger<AppStateService> _logger;

    public bool IsListening  { get; private set; }
    public bool IsPaused     { get; private set; }
    public AppMode CurrentMode { get; private set; } = AppMode.Idle;
    public SessionLifecycleState LifecycleState { get; private set; } = SessionLifecycleState.Idle;

    public event EventHandler<AppMode>? ModeChanged;
    public event EventHandler<SessionLifecycleState>? LifecycleStateChanged;

    public AppStateService(ILogger<AppStateService> logger) => _logger = logger;

    public void StartListening()
    {
        IsListening = true;
        IsPaused    = false;
        SetMode(AppMode.Listening);
        _logger.LogInformation("Listening started.");
    }

    public void PauseListening()
    {
        if (!IsListening) return;
        IsPaused = true;
        SetMode(AppMode.Idle);
        _logger.LogInformation("Listening paused.");
    }

    public void StopListening()
    {
        IsListening = false;
        IsPaused    = false;
        SetMode(AppMode.Idle);
        _logger.LogInformation("Listening stopped.");
    }

    public void SyncLifecycleState(SessionLifecycleState state)
    {
        if (LifecycleState == state) return;

        var previous = LifecycleState;
        LifecycleState = state;
        _logger.LogDebug("LifecycleState {Prev} → {Next}.", previous, state);
        LifecycleStateChanged?.Invoke(this, state);
    }

    private void SetMode(AppMode mode)
    {
        if (CurrentMode == mode) return;
        CurrentMode = mode;
        _logger.LogDebug("AppMode → {Mode}.", mode);
        ModeChanged?.Invoke(this, mode);
    }
}
