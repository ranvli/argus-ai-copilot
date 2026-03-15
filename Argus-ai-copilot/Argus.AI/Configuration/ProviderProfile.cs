namespace Argus.AI.Configuration;

/// <summary>
/// Describes one AI provider + model combination.
/// Multiple profiles can coexist (e.g. "ollama-mistral", "openai-gpt4o").
/// </summary>
public sealed class ProviderProfile
{
    /// <summary>Unique name referenced from workflow mappings, e.g. "ollama-mistral".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Provider kind used to select the implementation class, e.g. "Ollama", "OpenAI".</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Model identifier passed to the provider API, e.g. "mistral", "gpt-4o".</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Base URL for the provider API. Required for local providers such as Ollama.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Name of the environment variable or user-secret that holds the API key.
    /// Resolved at startup — never stored as a plain string in appsettings.
    /// </summary>
    public string? ApiKeySettingName { get; init; }

    /// <summary>HTTP request timeout in seconds. Defaults to 30.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>When false the profile is skipped during resolution.</summary>
    public bool Enabled { get; init; } = true;
}
