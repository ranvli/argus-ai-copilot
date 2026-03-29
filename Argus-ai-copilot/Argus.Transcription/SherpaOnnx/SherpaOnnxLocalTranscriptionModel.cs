using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Core.Domain.Entities;
using Argus.Core.Domain.Enums;
using Argus.Core.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using System.Diagnostics;

namespace Argus.Transcription.SherpaOnnx;

internal sealed class SherpaOnnxLocalTranscriptionModel : ITranscriptionModel
{
    private static readonly string[] SupportedLanguages = ["es", "en", "pt", "it", "de", "zh"];

    private readonly ProviderProfile _profile;
    private readonly SherpaOnnxModelService _modelService;
    private readonly ILogger<SherpaOnnxLocalTranscriptionModel> _logger;
    private readonly Lock _lock = new();

    private OnlineRecognizer? _recognizer;
    private VoiceActivityDetector? _vad;
    private SpokenLanguageIdentification? _lid;
    private OfflineSpeakerDiarization? _diarization;
    private SherpaOnnxBackendConfig? _config;
    private string _profileRoot = string.Empty;

    public SherpaOnnxLocalTranscriptionModel(
        ProviderProfile profile,
        SherpaOnnxModelService modelService,
        ILogger<SherpaOnnxLocalTranscriptionModel> logger)
    {
        _profile = profile;
        _modelService = modelService;
        _logger = logger;
    }

    public string ProviderId => "SherpaOnnx";
    public string ModelId => _profile.ModelId;
    public bool SupportsInMemoryAudio => true;

    public Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default)
    {
        if (request.AudioPcm16.IsEmpty)
            return Task.FromResult(TranscriptionResponse.Error("SherpaOnnxLocal requires in-memory PCM audio."));

        try
        {
            EnsureInitialized();

            var total = Stopwatch.StartNew();
            var samples = ConvertPcm16ToFloatSamples(request.AudioPcm16.Span);

            var vadStopwatch = Stopwatch.StartNew();
            var speechSamples = ExtractSpeechSamples(samples);
            vadStopwatch.Stop();

            if (speechSamples.Length == 0)
            {
                _logger.LogInformation("[SherpaSTT] partial=False text=(empty) reason=no_speech_detected");
                return Task.FromResult(new TranscriptionResponse
                {
                    FullText = string.Empty,
                    Segments = [],
                    ModelUsed = _profile.ModelId,
                    Runtime = "in-process",
                    UsedInMemoryAudio = true
                });
            }

            var lidStopwatch = Stopwatch.StartNew();
            var language = ResolveLanguage(request, speechSamples);
            lidStopwatch.Stop();

            var route = SelectRoute(language);
            _logger.LogInformation(
                "[SherpaASRRoute] language={Language} model={Model} family={Family}",
                language,
                route.Model.Length > 0 ? route.Model : route.Encoder,
                route.Family);

            var asrStopwatch = Stopwatch.StartNew();
            var (text, timestamps) = RecognizeText(speechSamples);
            asrStopwatch.Stop();

            var diarizationStopwatch = Stopwatch.StartNew();
            var diarizationSegments = GetDiarizationSegments(speechSamples);
            diarizationStopwatch.Stop();

            var segments = BuildSegments(text, timestamps, diarizationSegments, language);
            var fullText = string.Join(" ", segments.Select(s => s.Text)).Trim();

            _logger.LogInformation(
                "[SherpaSTT] partial={Partial} text={Text}",
                false,
                fullText.Length > 120 ? fullText[..120] + "…" : fullText);

            _logger.LogInformation(
                "[SherpaLatency] vadMs={VadMs:F1} lidMs={LidMs:F1} asrMs={AsrMs:F1} diarizationMs={DiarizationMs:F1} totalMs={TotalMs:F1} inMemory={InMemory}",
                vadStopwatch.Elapsed.TotalMilliseconds,
                lidStopwatch.Elapsed.TotalMilliseconds,
                asrStopwatch.Elapsed.TotalMilliseconds,
                diarizationStopwatch.Elapsed.TotalMilliseconds,
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
            _config = SherpaOnnxConfigParser.Parse(_profile, _profileRoot);

            _recognizer = new OnlineRecognizer(BuildRecognizerConfig(_config, SelectRoute("es")));
            _vad = new VoiceActivityDetector(BuildVadConfig(_config), 60f);
            _lid = new SpokenLanguageIdentification(BuildLidConfig(_config));
            _diarization = new OfflineSpeakerDiarization(BuildDiarizationConfig(_config));

            _logger.LogInformation(
                "[SherpaSTT] provider={Provider} modelId={ModelId} inProcess={InProcess} profileRoot={Root}",
                ProviderId,
                _profile.ModelId,
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

    private float[] ExtractSpeechSamples(float[] samples)
    {
        _vad!.Reset();
        _vad.AcceptWaveform(samples);
        _vad.Flush();

        var result = new List<float>(samples.Length);
        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            result.AddRange(segment.Samples);
            _vad.Pop();
        }

        return result.ToArray();
    }

    private string ResolveLanguage(TranscriptionRequest request, float[] speechSamples)
    {
        if (!string.IsNullOrWhiteSpace(request.Language))
            return NormalizeLanguage(request.Language) ?? request.Language!;

        using var stream = _lid!.CreateStream();
        stream.AcceptWaveform(request.AudioSampleRate, speechSamples);
        var result = _lid.Compute(stream);
        var detected = NormalizeLanguage(result.Lang) ?? "und";

        _logger.LogInformation(
            "[SherpaLID] detected={Detected} confidence={Confidence}",
            detected,
            "n/a");

        return SupportedLanguages.Contains(detected, StringComparer.OrdinalIgnoreCase)
            ? detected
            : "en";
    }

    private SherpaOnnxAsrRouteConfig SelectRoute(string language)
    {
        if (_config!.Routes.TryGetValue(language, out var route))
            return route;

        if (_config.Routes.TryGetValue("default", out var fallback))
            return fallback;

        return _config.Routes.Values.First();
    }

    private (string Text, float[] Timestamps) RecognizeText(float[] speechSamples)
    {
        using var stream = _recognizer!.CreateStream();
        stream.AcceptWaveform(16_000, speechSamples);
        stream.InputFinished();

        while (_recognizer.IsReady(stream))
            _recognizer.Decode(stream);

        var result = _recognizer.GetResult(stream);
        return (result.Text?.Trim() ?? string.Empty, result.Timestamps ?? []);
    }

    private OfflineSpeakerDiarizationSegment[] GetDiarizationSegments(float[] speechSamples)
    {
        var segments = _diarization!.Process(speechSamples);
        foreach (var segment in segments)
        {
            _logger.LogInformation(
                "[SherpaDiarization] speaker=Speaker {Speaker} start={Start:F2}s end={End:F2}s",
                segment.Speaker + 1,
                segment.Start,
                segment.End);
        }

        return segments;
    }

    private static IReadOnlyList<TranscriptSegment> BuildSegments(
        string text,
        float[] timestamps,
        OfflineSpeakerDiarizationSegment[] diarizationSegments,
        string language)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var diarization = diarizationSegments.FirstOrDefault();
        var start = timestamps.Length > 0 ? timestamps[0] : 0f;
        var end = timestamps.Length > 0 ? timestamps[^1] : Math.Max(start + 0.5f, 0.5f);
        if (diarizationSegments.Length > 0)
        {
            start = diarization.Start;
            end = diarization.End;
        }

        return
        [
            new TranscriptSegment
            {
                Text = text,
                SpeakerType = SpeakerType.Unknown,
                SpeakerLabel = diarizationSegments.Length > 0 ? $"Speaker {diarization.Speaker + 1}" : "Speaker 1",
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

    private static OnlineRecognizerConfig BuildRecognizerConfig(SherpaOnnxBackendConfig config, SherpaOnnxAsrRouteConfig route)
    {
        return new OnlineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = 16_000,
                FeatureDim = 80
            },
            ModelConfig = new OnlineModelConfig
            {
                NumThreads = config.NumThreads,
                Debug = config.Debug ? 1 : 0,
                Provider = config.Provider,
                Tokens = config.Tokens,
                ModelType = config.ModelType,
                ModelingUnit = config.ModelingUnit,
                BpeVocab = config.BpeVocab,
                Transducer = new OnlineTransducerModelConfig
                {
                    Encoder = route.Encoder,
                    Decoder = route.Decoder,
                    Joiner = route.Joiner
                },
                Paraformer = new OnlineParaformerModelConfig
                {
                    Encoder = route.Encoder,
                    Decoder = route.Decoder
                },
                Zipformer2Ctc = new OnlineZipformer2CtcModelConfig
                {
                    Model = route.Model
                }
            },
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            EnableEndpoint = 1,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 1.2f,
            Rule3MinUtteranceLength = 8.0f,
            BlankPenalty = 0f
        };
    }

    private static VadModelConfig BuildVadConfig(SherpaOnnxBackendConfig config)
        => new()
        {
            SampleRate = 16_000,
            NumThreads = config.NumThreads,
            Provider = config.Provider,
            Debug = config.Debug ? 1 : 0,
            SileroVad = new SileroVadModelConfig
            {
                Model = config.Vad.Model,
                Threshold = config.Vad.Threshold,
                MinSilenceDuration = config.Vad.MinSilenceDuration,
                MinSpeechDuration = config.Vad.MinSpeechDuration,
                WindowSize = config.Vad.WindowSize,
                MaxSpeechDuration = config.Vad.MaxSpeechDuration
            }
        };

    private static SpokenLanguageIdentificationConfig BuildLidConfig(SherpaOnnxBackendConfig config)
        => new()
        {
            NumThreads = config.NumThreads,
            Debug = config.Debug ? 1 : 0,
            Provider = config.Provider,
            Whisper = new SpokenLanguageIdentificationWhisperConfig
            {
                Encoder = config.Lid.Encoder,
                Decoder = config.Lid.Decoder,
                TailPaddings = config.Lid.TailPaddings
            }
        };

    private static OfflineSpeakerDiarizationConfig BuildDiarizationConfig(SherpaOnnxBackendConfig config)
        => new()
        {
            MinDurationOn = config.Diarization.MinDurationOn,
            MinDurationOff = config.Diarization.MinDurationOff,
            Clustering = new FastClusteringConfig
            {
                NumClusters = config.Diarization.NumClusters,
                Threshold = config.Diarization.Threshold
            },
            Segmentation = new OfflineSpeakerSegmentationModelConfig
            {
                NumThreads = config.NumThreads,
                Debug = config.Debug ? 1 : 0,
                Provider = config.Provider,
                Pyannote = new OfflineSpeakerSegmentationPyannoteModelConfig
                {
                    Model = config.Diarization.SegmentationModel
                }
            },
            Embedding = new SpeakerEmbeddingExtractorConfig
            {
                Model = config.Diarization.EmbeddingModel,
                NumThreads = config.NumThreads,
                Debug = config.Debug ? 1 : 0,
                Provider = config.Provider
            }
        };
}
