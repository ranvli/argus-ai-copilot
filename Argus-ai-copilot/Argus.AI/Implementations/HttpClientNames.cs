namespace Argus.AI.Implementations;

/// <summary>
/// Named HttpClient keys registered in DI via AddHttpClient().
/// Each provider gets its own client so base-address and headers stay isolated.
/// </summary>
internal static class HttpClientNames
{
    public const string Ollama = "Ollama";
    public const string OpenAi = "OpenAI";
}
