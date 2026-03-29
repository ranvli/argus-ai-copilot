using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using WhisperFactory = Whisper.net.WhisperFactory;

namespace Argus.Transcription.Whisper;

/// <summary>
/// Fully local Whisper transcription model powered by <see href="https://github.com/sandrohanea/whisper.net">Whisper.net</see>.
///
/// On the first call to <see cref="TranscribeAsync"/> the model file is downloaded (once)
/// and cached at <c>%LocalAppData%\ArgusAI\models\whisper\ggml-{modelId}.bin</c>.
/// Subsequent calls reuse the same <see cref="WhisperFactory"/> — no repeated I/O.
///
/// Audio is read directly from the WAV file written by the transcription pipeline;
/// no network traffic is required.
/// </summary>
internal sealed class WhisperLocalTranscriptionModel : ITranscriptionModel
{
    private readonly ProviderProfile _profile;
    private readonly WhisperModelService _modelService;
    private readonly ILogger<WhisperLocalTranscriptionModel> _logger;

    public string ProviderId => "WhisperNet";
    public string ModelId    => _profile.ModelId;

    public WhisperLocalTranscriptionModel(
        ProviderProfile profile,
        WhisperModelService modelService,
        ILogger<WhisperLocalTranscriptionModel> logger)
    {
        _profile      = profile;
        _modelService = modelService;
        _logger       = logger;
    }

    public async Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request, CancellationToken ct = default)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var ensureModelMs = 0d;
        var buildMs = 0d;
        var audioOpenMs = 0d;
        var processMs = 0d;
        try
        {
            if (!File.Exists(request.AudioFilePath))
                return TranscriptionResponse.Error($"Audio file not found: {request.AudioFilePath}");

            WhisperFactory factory;
            try
            {
                var ensureModelStopwatch = Stopwatch.StartNew();
                factory = await _modelService.EnsureModelAsync(_profile.ModelId, ct)
                    .ConfigureAwait(false);
                ensureModelStopwatch.Stop();
                ensureModelMs = ensureModelStopwatch.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WhisperLocal] Failed to obtain Whisper factory. ModelId={ModelId}", _profile.ModelId);
                return TranscriptionResponse.Error(
                    $"Whisper model initialisation failed ({_profile.ModelId}): {ex.Message}");
            }

            var segments = new List<TranscriptSegment>();
            var textBuilder = new System.Text.StringBuilder();

            // Build a processor for this request (processors are not thread-safe; one per call).
            // In Whisper.net 1.8.0 ProcessAsync returns IAsyncEnumerable<SegmentData>.
            var buildStopwatch = Stopwatch.StartNew();
            var processorBuilder = factory.CreateBuilder();

            if (!string.IsNullOrWhiteSpace(request.Language))
                processorBuilder = processorBuilder.WithLanguage(request.Language);

            using var processor = processorBuilder.Build();
            buildStopwatch.Stop();
            buildMs = buildStopwatch.Elapsed.TotalMilliseconds;

            var audioOpenStopwatch = Stopwatch.StartNew();
            await using var audioStream =
                new FileStream(request.AudioFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 65536, useAsync: true);
            audioOpenStopwatch.Stop();
            audioOpenMs = audioOpenStopwatch.Elapsed.TotalMilliseconds;

            _logger.LogDebug(
                "[WhisperLocal] Starting ProcessAsync. ModelId={ModelId} File='{File}'",
                _profile.ModelId, request.AudioFilePath);

            var processStopwatch = Stopwatch.StartNew();
            await foreach (var seg in processor.ProcessAsync(audioStream, ct).ConfigureAwait(false))
            {
                var text = seg.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(text))
                    continue;

                var segmentLanguage = NormalizeLanguage(seg.Language);

                _logger.LogInformation(
                    "[WhisperLocal.Language] segmentLanguage={Lang} text={Preview}",
                    segmentLanguage ?? "(none)",
                    Preview(text));

                textBuilder.Append(text).Append(' ');

                var now      = DateTimeOffset.UtcNow;
                var startOff = now + seg.Start;
                var endOff   = now + seg.End;

                segments.Add(new TranscriptSegment
                {
                    SessionId   = Guid.Empty,       // filled in by the pipeline's StampSegments
                    Text        = text,
                    SpeakerType = SpeakerType.Unknown,
                    Range       = new TimeRange(startOff, endOff),
                    Language    = segmentLanguage
                });
            }
            processStopwatch.Stop();
            processMs = processStopwatch.Elapsed.TotalMilliseconds;

            var fullText = textBuilder.ToString().Trim();
            var detectedLanguage = ResolveDetectedLanguage(segments);

            if (detectedLanguage is not null)
            {
                foreach (var segment in segments.Where(static s => string.IsNullOrWhiteSpace(s.Language)))
                    segment.Language = detectedLanguage;
            }

            _logger.LogInformation(
                "[WhisperLocal] Transcription complete. ModelId={ModelId} Segments={Count} TextLength={Len}",
                _profile.ModelId, segments.Count, fullText.Length);

            _logger.LogInformation(
                "[WhisperTiming] modelId={ModelId} requestLanguage={RequestLanguage} ensureModelMs={EnsureModelMs:F1} buildMs={BuildMs:F1} audioOpenMs={AudioOpenMs:F1} processMs={ProcessMs:F1} totalMs={TotalMs:F1} segments={Segments} detectedLanguage={DetectedLanguage}",
                _profile.ModelId,
                request.Language ?? "(auto)",
                ensureModelMs,
                buildMs,
                audioOpenMs,
                processMs,
                totalStopwatch.Elapsed.TotalMilliseconds,
                segments.Count,
                detectedLanguage ?? "(none)");

            return new TranscriptionResponse
            {
                FullText         = fullText,
                Segments         = segments.AsReadOnly(),
                ModelUsed        = _profile.ModelId,
                DetectedLanguage = detectedLanguage
            };
        }
        catch (OperationCanceledException)
        {
            return TranscriptionResponse.Error("Transcription was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[WhisperLocal] Unexpected error during transcription. ModelId={ModelId}", _profile.ModelId);
            return TranscriptionResponse.Error($"Whisper transcription error: {ex.Message}");
        }
    }

    private string? ResolveDetectedLanguage(IReadOnlyList<TranscriptSegment> segments)
    {
        var segmentLanguage = segments
            .Select(static segment => NormalizeLanguage(segment.Language))
            .FirstOrDefault(static language => language is not null);

        if (segmentLanguage is not null)
        {
            _logger.LogInformation(
                "[WhisperLocal.Language] detectedLanguage={Lang} source=segment",
                segmentLanguage);
            return segmentLanguage;
        }

        _logger.LogInformation("[WhisperLocal.Language] detectedLanguage=(none) source=unavailable");
        return null;
    }

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var trimmed = language.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "spanish" or "espanol" or "español" => "es",
            "english" => "en",
            _ when trimmed.Length is >= 2 and <= 10 => trimmed,
            _ => null
        };
    }

    private static string Preview(string text)
        => text.Length <= 100 ? text : text[..100] + "…";
}
