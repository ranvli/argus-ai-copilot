using Argus.Context.WindowContext;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Context;

public static class ContextServiceExtensions
{
    /// <summary>
    /// Registers all Argus.Context services.
    /// Call from Program.cs / ConfigureServices.
    /// </summary>
    public static IServiceCollection AddArgusContext(this IServiceCollection services)
    {
        // Singleton: shared tracker held by all consumers.
        // Also registered as IHostedService so the BackgroundService pump runs.
        services.AddSingleton<ActiveWindowTracker>();
        services.AddSingleton<IActiveWindowTracker>(
            sp => sp.GetRequiredService<ActiveWindowTracker>());
        services.AddHostedService(
            sp => sp.GetRequiredService<ActiveWindowTracker>());

        return services;
    }
}
