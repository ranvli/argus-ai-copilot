using Argus.Transcription.SherpaOnnx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Argus.App.Diagnostics;

public sealed class SherpaModelBootstrapService : IHostedService
{
    private readonly ISherpaOnnxProvisioningService _provisioning;
    private readonly ILogger<SherpaModelBootstrapService> _logger;

    public SherpaModelBootstrapService(
        ISherpaOnnxProvisioningService provisioning,
        ILogger<SherpaModelBootstrapService> logger)
    {
        _provisioning = provisioning;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SherpaBootstrap] action=start");
        var result = await _provisioning.EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsValid)
        {
            _logger.LogInformation("[SherpaBootstrap] action=ready root={Root}", result.ProfileRoot);
        }
        else
        {
            _logger.LogWarning("[SherpaBootstrap] action=not_ready reason={Reason}", result.Reason ?? "unknown");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
