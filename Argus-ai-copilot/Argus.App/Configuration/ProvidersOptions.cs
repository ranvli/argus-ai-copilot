namespace Argus.App.Configuration;

/// <summary>Bound to the "Providers" section of appsettings.json.</summary>
public sealed class ProvidersOptions
{
    public const string SectionName = "Providers";

    public AiProviderOptions AI { get; init; } = new();
    public SpeechProviderOptions Speech { get; init; } = new();
}

public sealed class AiProviderOptions
{
    public string Name { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
}

public sealed class SpeechProviderOptions
{
    public string Name { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
}
