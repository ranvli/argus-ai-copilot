using System.Net.Http.Headers;
using System.Text.Json;
using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Argus.AI.Implementations.OpenAi;

/// <summary>
/// OpenAI Whisper transcription via /v1/audio/transcriptions.
/// Sends the audio file as multipart/form-data and maps the verbose_json
/// response to domain <see cref="TranscriptSegment"/> objects.
/// </summary>
internal sealed class OpenAiTranscriptionModel : ProviderBase, ITranscriptionModel
{
    private readonly ILogger<OpenAiTranscriptionModel> _logger;

    public OpenAiTranscriptionModel(
        ProviderProfile profile,
        IHttpClientFactory httpFactory,
        ILogger<OpenAiTranscriptionModel> logger)
        : base(profile, httpFactory, HttpClientNames.OpenAi)
    {
        _logger = logger;
    }

    public async Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(request.AudioFilePath))
                return TranscriptionResponse.Error($"Audio file not found: {request.AudioFilePath}");

            await using var fileStream = File.OpenRead(request.AudioFilePath);
            var fileName = Path.GetFileName(request.AudioFilePath);

            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", fileName);
            form.Add(new StringContent(ModelId), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");

            if (!string.IsNullOrWhiteSpace(request.Language))
                form.Add(new StringContent(request.Language), "language");

            if (!string.IsNullOrWhiteSpace(request.Prompt))
                form.Add(new StringContent(request.Prompt), "prompt");

            using var response = await Http.PostAsync("v1/audio/transcriptions", form, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var fullText = doc.RootElement.TryGetProperty("text", out var t)
                ? t.GetString() ?? string.Empty
                : string.Empty;

            var detectedLanguage = doc.RootElement.TryGetProperty("language", out var lang)
                ? lang.GetString()
                : null;

            var segments = new List<TranscriptSegment>();
            if (doc.RootElement.TryGetProperty("segments", out var segs))
            {
                foreach (var seg in segs.EnumerateArray())
                {
                    var start = seg.TryGetProperty("start", out var s) ? s.GetDouble() : 0d;
                    var end   = seg.TryGetProperty("end",   out var e) ? e.GetDouble() : 0d;
                    var text  = seg.TryGetProperty("text",  out var tx) ? tx.GetString() ?? string.Empty : string.Empty;

                    var epoch = DateTimeOffset.UtcNow;
                    segments.Add(new TranscriptSegment
                    {
                        Text        = text.Trim(),
                        SpeakerType = SpeakerType.Unknown,
                        Language    = detectedLanguage,
                        Range       = new TimeRange(
                            epoch.AddSeconds(start),
                            epoch.AddSeconds(end)),
                        Confidence  = ConfidenceScore.None
                    });
                }
            }

            return new TranscriptionResponse
            {
                FullText          = fullText,
                Segments          = segments,
                ModelUsed         = ModelId,
                DetectedLanguage  = detectedLanguage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAiTranscriptionModel.TranscribeAsync failed");
            return TranscriptionResponse.Error(ex.Message);
        }
    }
}
