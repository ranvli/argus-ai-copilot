using Argus.AI.Configuration;

namespace Argus.AI.Implementations;

/// <summary>
/// Shared base for all provider implementations.
/// Provides access to the resolved <see cref="ProviderProfile"/> and a pre-configured
/// <see cref="HttpClient"/> scoped to that profile's endpoint and timeout.
/// </summary>
internal abstract class ProviderBase
{
    protected ProviderProfile Profile { get; }
    protected HttpClient Http { get; }

    protected ProviderBase(ProviderProfile profile, IHttpClientFactory httpFactory, string httpClientName)
    {
        Profile = profile;
        Http = httpFactory.CreateClient(httpClientName);
    }

    public string ProviderId => Profile.Provider;
    public string ModelId => Profile.ModelId;
}
