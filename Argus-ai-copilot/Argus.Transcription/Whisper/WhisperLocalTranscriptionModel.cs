using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
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
        try
        {
            if (!File.Exists(request.AudioFilePath))
                return TranscriptionResponse.Error($"Audio file not found: {request.AudioFilePath}");

            WhisperFactory factory;
            try
            {
                factory = await _modelService.EnsureModelAsync(_profile.ModelId, ct)
                    .ConfigureAwait(false);
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
            var processorBuilder = factory.CreateBuilder();

            if (!string.IsNullOrWhiteSpace(request.Language))
                processorBuilder = processorBuilder.WithLanguage(request.Language);

            using var processor = processorBuilder.Build();

            await using var audioStream =
                new FileStream(request.AudioFilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 65536, useAsync: true);

            _logger.LogDebug(
                "[WhisperLocal] Starting ProcessAsync. ModelId={ModelId} File='{File}'",
                _profile.ModelId, request.AudioFilePath);

            await foreach (var seg in processor.ProcessAsync(audioStream, ct).ConfigureAwait(false))
            {
                var text = seg.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(text))
                    continue;

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
                    Language    = seg.Language
                });
            }

            var fullText = textBuilder.ToString().Trim();

            _logger.LogInformation(
                "[WhisperLocal] Transcription complete. ModelId={ModelId} Segments={Count} TextLength={Len}",
                _profile.ModelId, segments.Count, fullText.Length);

            return new TranscriptionResponse
            {
                FullText  = fullText,
                Segments  = segments.AsReadOnly(),
                ModelUsed = _profile.ModelId
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
}
