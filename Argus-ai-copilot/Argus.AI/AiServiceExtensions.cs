using Argus.AI.Configuration;
using Argus.AI.Discovery;
using Argus.AI.Implementations;
using Argus.AI.Providers;
using Argus.AI.Resolvers;
using Argus.AI.Selection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Argus.AI;

public static class AiServiceExtensions
{
    /// <summary>
    /// Registers all Argus.AI services: options, named HTTP clients, and the model resolver.
    /// Call this from Program.cs / ConfigureServices.
    /// </summary>
    public static IServiceCollection AddArgusAI(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Bind AiOptions from "ArgusAI" config section ──────────────────────
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        // ── Named HTTP clients, one per provider kind ─────────────────────────
        // Base addresses and headers are set at runtime from the resolved profile
        // inside each implementation; the named client just provides a clean
        // HttpClient with sensible defaults.
        services
            .AddHttpClient(HttpClientNames.Ollama)
            .ConfigureHttpClient((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<AiOptions>>().Value;
                // Apply the first enabled Ollama profile's endpoint, if any
                var profile = opts.Profiles.FirstOrDefault(p =>
                    p.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) && p.Enabled);
                if (profile?.Endpoint is not null)
                    client.BaseAddress = new Uri(profile.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(
                    profile?.TimeoutSeconds > 0 ? profile.TimeoutSeconds : 30);
            });

        services
            .AddHttpClient(HttpClientNames.OpenAi)
            .ConfigureHttpClient((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<AiOptions>>().Value;
                var profile = opts.Profiles.FirstOrDefault(p =>
                    p.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) && p.Enabled);

                var endpoint = profile?.Endpoint ?? "https://api.openai.com/";
                client.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(
                    profile?.TimeoutSeconds > 0 ? profile.TimeoutSeconds : 60);

                // Resolve API key: env var name stored in ApiKeySettingName
                if (!string.IsNullOrWhiteSpace(profile?.ApiKeySettingName))
                {
                    var apiKey = Environment.GetEnvironmentVariable(profile.ApiKeySettingName)
                        ?? sp.GetRequiredService<IConfiguration>()[profile.ApiKeySettingName];
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
            });

        // ── Model resolver (singleton — caches instances per profile) ─────────
        services.AddSingleton<IModelResolver, ModelResolver>();

        // ── Provider discovery + model selection ──────────────────────────────
        services.AddTransient<IProviderDiscoveryService, ProviderDiscoveryService>();
        services.AddSingleton<IModelSelectionService, ModelSelectionService>();

        return services;
    }
}
