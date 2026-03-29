using Argus.Transcription.SherpaOnnx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App.Diagnostics;

public sealed class SherpaNativePreflightHostedService : IHostedService
{
    private readonly ISherpaOnnxProvisioningService _provisioning;
    private readonly ISherpaOnnxPreflightService _preflight;
    private readonly ILogger<SherpaNativePreflightHostedService> _logger;

    public SherpaNativePreflightHostedService(
        ISherpaOnnxProvisioningService provisioning,
        ISherpaOnnxPreflightService preflight,
        ILogger<SherpaNativePreflightHostedService> logger)
    {
        _provisioning = provisioning;
        _preflight = preflight;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_provisioning.IsReady)
        {
            _logger.LogWarning("[SherpaPreflight] skipped reason=provisioning_not_ready state={State}", _provisioning.State);
            return;
        }

        await _preflight.RunPreflightAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
