using Argus.App.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.App.Services;

internal sealed class AppBootstrapper : IAppBootstrapper
{
    private readonly ILogger<AppBootstrapper> _logger;
    private readonly ApplicationOptions _appOptions;
    private readonly ProvidersOptions _providerOptions;

    public bool IsInitialized { get; private set; }

    public AppBootstrapper(
        ILogger<AppBootstrapper> logger,
        IOptions<ApplicationOptions> appOptions,
        IOptions<ProvidersOptions> providerOptions)
    {
        _logger = logger;
        _appOptions = appOptions.Value;
        _providerOptions = providerOptions.Value;
    }

    public void Initialize()
    {
        if (IsInitialized)
            return;

        _logger.LogInformation(
            "Starting {AppName} v{Version}",
            _appOptions.Name,
            _appOptions.Version);

        // ── Future initialization steps go here ───────────────────────────────
        // ValidateConfiguration();
        // PrewarmAiProvider(_providerOptions.AI);
        // LoadUserPreferences();
        // InitializeTelemetry();
        // ─────────────────────────────────────────────────────────────────────

        IsInitialized = true;

        _logger.LogInformation("Bootstrap complete. AI provider: '{Provider}'",
            string.IsNullOrEmpty(_providerOptions.AI.Name) ? "(not configured)" : _providerOptions.AI.Name);
    }
}
