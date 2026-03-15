namespace Argus.AI.Selection;

/// <summary>
/// Controls how the model selection service chooses a provider for each capability.
/// </summary>
public enum RoutingMode
{
    /// <summary>
    /// Use local models when available; fall back to cloud.
    /// Chooses the best model regardless of provider origin.
    /// </summary>
    Automatic,

    /// <summary>
    /// Always prefer a local model (e.g. Ollama).
    /// Falls back to cloud only when no local model covers the required capability.
    /// </summary>
    PreferLocal,

    /// <summary>
    /// Always prefer a cloud model (e.g. OpenAI).
    /// Falls back to local only when cloud is not configured.
    /// </summary>
    PreferCloud,

    /// <summary>
    /// Use exactly the profiles specified in AiOptions.WorkflowMappings / Defaults.
    /// No automatic fallback logic; if a profile is absent the capability is unavailable.
    /// </summary>
    Manual
}
