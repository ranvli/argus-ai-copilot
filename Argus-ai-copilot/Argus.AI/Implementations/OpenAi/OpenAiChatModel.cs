using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Microsoft.Extensions.Logging;

namespace Argus.AI.Implementations.OpenAi;

/// <summary>
/// OpenAI-compatible chat completion (/v1/chat/completions).
/// Works against OpenAI, Azure OpenAI, or any OpenAI-compatible proxy.
/// Streaming uses SSE; non-streaming awaits the full response.
/// </summary>
internal sealed class OpenAiChatModel : ProviderBase, IChatModel
{
    private readonly ILogger<OpenAiChatModel> _logger;

    public OpenAiChatModel(
        ProviderProfile profile,
        IHttpClientFactory httpFactory,
        ILogger<OpenAiChatModel> logger)
        : base(profile, httpFactory, HttpClientNames.OpenAi)
    {
        _logger = logger;
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        try
        {
            var body = BuildBody(request, stream: false);
            using var response = await Http.PostAsync(
                "v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : (JsonElement?)null;
            var promptTokens = usage?.TryGetProperty("prompt_tokens", out var pt) == true ? pt.GetInt32() : (int?)null;
            var completionTokens = usage?.TryGetProperty("completion_tokens", out var ct2) == true ? ct2.GetInt32() : (int?)null;

            return new ChatResponse
            {
                Content = content,
                ModelUsed = ModelId,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAiChatModel.CompleteAsync failed");
            return ChatResponse.Error(ex.Message);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(request, stream: true);
        HttpResponseMessage? response = null;

        string? connectError = null;
        try
        {
            response = await Http.PostAsync(
                "v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAiChatModel.StreamAsync failed to connect");
            connectError = ex.Message;
        }

        if (connectError is not null)
        {
            yield return $"[error: {connectError}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            string token;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                token = delta.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            }
            catch { continue; }

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
            temperature = request.Temperature,
            max_tokens = request.MaxTokens
        };
    }
}
