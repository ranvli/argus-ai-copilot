using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Argus.AI.Configuration;
using Argus.AI.Implementations;
using Argus.AI.Models;
using Argus.AI.Providers;
using Microsoft.Extensions.Logging;

namespace Argus.AI.Implementations.Ollama;

/// <summary>
/// Ollama chat completion using the /api/chat endpoint.
/// Streaming is line-by-line NDJSON; non-streaming awaits the final response.
/// HTTP calls are minimal stubs that will work against a real Ollama instance.
/// </summary>
internal sealed class OllamaChatModel : ProviderBase, IChatModel
{
    private readonly ILogger<OllamaChatModel> _logger;

    public OllamaChatModel(ProviderProfile profile, IHttpClientFactory httpFactory, ILogger<OllamaChatModel> logger)
        : base(profile, httpFactory, HttpClientNames.Ollama)
    {
        _logger = logger;
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        try
        {
            var body = BuildBody(request, stream: false);
            using var response = await Http.PostAsync(
                $"{Profile.Endpoint!.TrimEnd('/')}/api/chat",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return new ChatResponse { Content = content, ModelUsed = ModelId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OllamaChatModel.CompleteAsync failed");
            return ChatResponse.Error(ex.Message);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(request, stream: true);
        HttpResponseMessage? response = null;

        string? connectError = null;
        try
        {
            response = await Http.PostAsync(
                $"{Profile.Endpoint!.TrimEnd('/')}/api/chat",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OllamaChatModel.StreamAsync failed to connect");
            connectError = ex.Message;
        }

        if (connectError is not null)
        {
            yield return $"[error: {connectError}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            string token;
            try
            {
                using var doc = JsonDocument.Parse(line);
                token = doc.RootElement
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;

                var done = doc.RootElement.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean();
                if (done) break;
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrEmpty(token))
                yield return token;
        }

        response.Dispose();
    }

    private object BuildBody(ChatRequest request, bool stream)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });

        foreach (var m in request.Messages)
            messages.Add(new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content });

        return new
        {
            model = ModelId,
            messages,
            stream,
            options = new
            {
                temperature = request.Temperature,
                num_predict = request.MaxTokens
            }
        };
    }
}
