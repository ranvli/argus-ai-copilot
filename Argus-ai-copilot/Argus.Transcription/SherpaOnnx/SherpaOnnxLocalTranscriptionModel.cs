using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Argus.Transcription.Configuration;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using System.Diagnostics;

namespace Argus.Transcription.SherpaOnnx;

internal sealed class SherpaOnnxLocalTranscriptionModel : ITranscriptionModel
{
    private readonly ProviderProfile _profile;
    private readonly SherpaOnnxModelService _modelService;
    private readonly ISherpaOnnxPreflightService _preflight;
    private readonly TranscriptionRuntimeSettings _runtimeSettings;
    private readonly ILogger<SherpaOnnxLocalTranscriptionModel> _logger;
    private readonly Lock _lock = new();

    private OfflineRecognizer? _recognizer;
    private SherpaOnnxBackendConfig? _config;
    private string _profileRoot = string.Empty;
    private SherpaOnnxAssetValidationResult? _assetValidation;

    public SherpaOnnxLocalTranscriptionModel(
        ProviderProfile profile,
        SherpaOnnxModelService modelService,
        ISherpaOnnxPreflightService preflight,
        TranscriptionRuntimeSettings runtimeSettings,
        ILogger<SherpaOnnxLocalTranscriptionModel> logger)
    {
        _profile = profile;
        _modelService = modelService;
        _preflight = preflight;
        _runtimeSettings = runtimeSettings;
        _logger = logger;
    }

    public string ProviderId => "SherpaOnnx";
    public string ModelId => _profile.ModelId;
    public bool SupportsInMemoryAudio => true;

    public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default)
    {
        if (request.AudioPcm16.IsEmpty)
            return Task.FromResult(TranscriptionResponse.Error("SherpaOnnxLocal requires in-memory PCM audio."));

        if (!_runtimeSettings.EnableSherpaInProcessDecodeAfterPreflight)
            return Task.FromResult(TranscriptionResponse.Error(
                "SherpaOnnxLocal live decode is disabled by configuration. Set ArgusAI:Transcription:EnableSherpaInProcessDecodeAfterPreflight=true to allow in-process decode after preflight passes."));

        if (!_preflight.IsSafeToUse)
            return Task.FromResult(TranscriptionResponse.Error(
                _preflight.LastError ?? "SherpaOnnxLocal live decode is blocked because native preflight has not passed."));

        try
        {
            EnsureInitialized();

            var total = Stopwatch.StartNew();
            var samples = ConvertPcm16ToFloatSamples(request.AudioPcm16.Span);

            var asrStopwatch = Stopwatch.StartNew();
            var (text, timestamps) = RecognizeText(samples);
            asrStopwatch.Stop();

            var language = NormalizeLanguage(request.Language) ?? "auto";
            _logger.LogInformation(
                "[SherpaASRRoute] language={Language} model={Model} family={Family}",
                language,
                _config!.Model,
                "omnilingual_offline_ctc");

            var segments = BuildSegments(text, timestamps, language);
            var fullText = string.Join(" ", segments.Select(s => s.Text)).Trim();

            _logger.LogInformation(
                "[SherpaSTT] partial={Partial} text={Text}",
                false,
                fullText.Length > 120 ? fullText[..120] + "…" : fullText);

            _logger.LogInformation(
                "[SherpaLatency] asrMs={AsrMs:F1} totalMs={TotalMs:F1} inMemory={InMemory}",
                asrStopwatch.Elapsed.TotalMilliseconds,
                total.Elapsed.TotalMilliseconds,
                true);

            return Task.FromResult(new TranscriptionResponse
            {
                FullText = fullText,
                Segments = segments,
                ModelUsed = _profile.ModelId,
                DetectedLanguage = language,
                Runtime = "in-process",
                UsedInMemoryAudio = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SherpaSTT] Embedded sherpa-onnx transcription failed. ModelId={ModelId}", _profile.ModelId);
            return Task.FromResult(TranscriptionResponse.Error($"SherpaOnnxLocal transcription error: {ex.Message}"));
        }
    }

    private void EnsureInitialized()
    {
        if (_recognizer is not null)
            return;

        lock (_lock)
        {
            if (_recognizer is not null)
                return;

            _profileRoot = _modelService.GetProfileRoot(_profile.ModelId);
            _modelService.LogModelRoot(_profile.ModelId);
            _assetValidation = _modelService.ValidateAssets(_profile);
            if (!_assetValidation.IsValid)
                throw new FileNotFoundException(_assetValidation.ToUserMessage(), _assetValidation.ModelPath);

            _config = _modelService.GetBackendConfig(_profile);

            _recognizer = new OfflineRecognizer(BuildRecognizerConfig(_config));

            _logger.LogInformation(
                "[SherpaSTT] provider={Provider} modelId={ModelId} family={Family} inProcess={InProcess} profileRoot={Root}",
                ProviderId,
                _profile.ModelId,
                _config.Family,
                true,
                _profileRoot);
        }
    }

    private static float[] ConvertPcm16ToFloatSamples(ReadOnlySpan<byte> pcm16)
    {
        var count = pcm16.Length / 2;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var low = pcm16[i * 2];
            var high = pcm16[(i * 2) + 1];
            var sample = (short)(low | (high << 8));
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    private (string Text, float[] Timestamps) RecognizeText(float[] speechSamples)
    {
        using var stream = _recognizer!.CreateStream();
        stream.AcceptWaveform(16_000, speechSamples);
        _recognizer.Decode(stream);
        var result = stream.Result;
        return (result.Text?.Trim() ?? string.Empty, result.Timestamps ?? []);
    }

    private static IReadOnlyList<TranscriptSegment> BuildSegments(
        string text,
        float[] timestamps,
        string language)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var start = timestamps.Length > 0 ? timestamps[0] : 0f;
        var end = timestamps.Length > 0 ? timestamps[^1] : Math.Max(start + 0.5f, 0.5f);

        return
        [
            new TranscriptSegment
            {
                Text = text,
                SpeakerType = SpeakerType.Unknown,
                SpeakerLabel = "Speaker 1",
                Range = new TimeRange(DateTimeOffset.UtcNow.AddSeconds(start), DateTimeOffset.UtcNow.AddSeconds(end)),
                Language = language,
                Confidence = ConfidenceScore.Full
            }
        ];
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
            "portuguese" => "pt",
            "italian" => "it",
            "german" => "de",
            "mandarin" or "chinese" or "zh-cn" or "zh-hans" => "zh",
            _ when trimmed.Length is >= 2 and <= 10 => trimmed,
            _ => null
        };
    }

    private static OfflineRecognizerConfig BuildRecognizerConfig(SherpaOnnxBackendConfig config)
    {
        var modelConfig = new OfflineModelConfig
        {
            NumThreads = config.NumThreads,
            Debug = config.Debug ? 1 : 0,
            Provider = config.Provider,
            Tokens = config.Tokens,
            ModelType = config.ModelType,
            ModelingUnit = config.ModelingUnit,
            BpeVocab = config.BpeVocab
        };

        switch (config.Family)
        {
            case "omnilingual_offline_ctc":
                modelConfig.Omnilingual = new OfflineOmnilingualAsrCtcModelConfig
                {
                    Model = config.Model
                };
                break;
            case "wenet_ctc":
                modelConfig.WenetCtc = new OfflineWenetCtcModelConfig
                {
                    Model = config.Model
                };
                break;
            case "zipformer_ctc":
                modelConfig.ZipformerCtc = new OfflineZipformerCtcModelConfig
                {
                    Model = config.Model
                };
                break;
            case "sense_voice":
                modelConfig.SenseVoice = new OfflineSenseVoiceModelConfig
                {
                    Model = config.Model,
                    Language = "auto",
                    UseInverseTextNormalization = 0
                };
                break;
            default:
                throw new InvalidOperationException($"Unsupported Sherpa family '{config.Family}'.");
        }

        return new OfflineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = 16_000,
                FeatureDim = 80
            },
            ModelConfig = modelConfig,
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            BlankPenalty = 0f
        };
    }
}
