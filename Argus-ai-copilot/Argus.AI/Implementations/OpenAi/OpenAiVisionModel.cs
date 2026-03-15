using System.Text;
using System.Text.Json;
using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Microsoft.Extensions.Logging;

namespace Argus.AI.Implementations.OpenAi;

/// <summary>
/// OpenAI vision via /v1/chat/completions with image_url content parts.
/// Pass either a file path (read + base64-encode) or raw bytes.
/// </summary>
internal sealed class OpenAiVisionModel : ProviderBase, IVisionModel
{
    private readonly ILogger<OpenAiVisionModel> _logger;

    public OpenAiVisionModel(
        ProviderProfile profile,
        IHttpClientFactory httpFactory,
        ILogger<OpenAiVisionModel> logger)
        : base(profile, httpFactory, HttpClientNames.OpenAi)
    {
        _logger = logger;
    }

    public async Task<VisionResponse> AnalyzeAsync(VisionRequest request, CancellationToken ct = default)
    {
        try
        {
            byte[] imageBytes;
            string mimeType;

            if (request.ImageBytes is not null)
            {
                imageBytes = request.ImageBytes;
                mimeType = request.MimeType ?? "image/png";
            }
            else if (!string.IsNullOrWhiteSpace(request.ImagePath))
            {
                imageBytes = await File.ReadAllBytesAsync(request.ImagePath, ct);
                mimeType = InferMime(request.ImagePath);
            }
            else
            {
                return VisionResponse.Error("VisionRequest must supply ImagePath or ImageBytes.");
            }

            var b64 = Convert.ToBase64String(imageBytes);
            var dataUrl = $"data:{mimeType};base64,{b64}";

            var body = new
            {
                model = ModelId,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = request.Prompt },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    }
                }
            };

            using var response = await Http.PostAsync(
                "v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var description = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return new VisionResponse { Description = description, ModelUsed = ModelId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAiVisionModel.AnalyzeAsync failed");
            return VisionResponse.Error(ex.Message);
        }
    }

    private static string InferMime(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "image/png"
        };
}
