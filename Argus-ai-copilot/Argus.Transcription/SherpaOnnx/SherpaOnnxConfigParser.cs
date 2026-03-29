using System.Text.Json;
using Argus.AI.Configuration;

namespace Argus.Transcription.SherpaOnnx;

internal static class SherpaOnnxConfigParser
{
    private const string ExpectedLayoutHint = "Place a sherpa profile folder containing profile.json, tokens.txt, VAD/LID/diarization models, and routed ASR model files under the configured SherpaModelsRoot or %LocalAppData%\\ArgusAI\\models\\sherpa-onnx\\<ModelId>.";

    public static SherpaOnnxAssetValidationResult ValidateAssets(ProviderProfile profile, string profileRoot)
    {
        var profileJsonPath = Path.Combine(profileRoot, "profile.json");
        var profileJsonExists = File.Exists(profileJsonPath);

        if (!profileJsonExists)
        {
            return new SherpaOnnxAssetValidationResult
            {
                IsValid = false,
                ProfileRoot = profileRoot,
                ProfileJsonPath = profileJsonPath,
                ProfileJsonExists = false,
                Reason = "profile_json_missing",
                MissingFiles = [profileJsonPath],
                ExpectedLayoutHint = ExpectedLayoutHint
            };
        }

        using var stream = File.OpenRead(profileJsonPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var missing = new List<string>();

        static string ReadString(JsonElement root, string name, string fallback = "")
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        static void AddIfMissing(List<string> missing, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                missing.Add(path);
        }

        AddIfMissing(missing, Path.Combine(profileRoot, ReadString(root, "tokens", "tokens.txt")));

        if (root.TryGetProperty("vad", out var vadElement))
            AddIfMissing(missing, Path.Combine(profileRoot, ReadString(vadElement, "model", "silero_vad.onnx")));

        if (root.TryGetProperty("lid", out var lidElement))
        {
            AddIfMissing(missing, Path.Combine(profileRoot, ReadString(lidElement, "encoder", "lid-encoder.onnx")));
            AddIfMissing(missing, Path.Combine(profileRoot, ReadString(lidElement, "decoder", "lid-decoder.onnx")));
        }

        if (root.TryGetProperty("diarization", out var diarizationElement))
        {
            AddIfMissing(missing, Path.Combine(profileRoot, ReadString(diarizationElement, "segmentationModel", "diarization-segmentation.onnx")));
            AddIfMissing(missing, Path.Combine(profileRoot, ReadString(diarizationElement, "embeddingModel", "speaker-embedding.onnx")));
        }

        if (root.TryGetProperty("routes", out var routesElement) && routesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in routesElement.EnumerateObject())
            {
                var route = item.Value;
                AddIfMissing(missing, Path.Combine(profileRoot, ReadString(route, "model", "")));
                AddIfMissing(missing, Path.Combine(profileRoot, ReadString(route, "encoder", "")));
                AddIfMissing(missing, Path.Combine(profileRoot, ReadString(route, "decoder", "")));
                AddIfMissing(missing, Path.Combine(profileRoot, ReadString(route, "joiner", "")));
            }
        }

        return new SherpaOnnxAssetValidationResult
        {
            IsValid = missing.Count == 0,
            ProfileRoot = profileRoot,
            ProfileJsonPath = profileJsonPath,
            ProfileJsonExists = true,
            Reason = missing.Count == 0 ? "ok" : "referenced_assets_missing",
            MissingFiles = missing,
            ExpectedLayoutHint = ExpectedLayoutHint
        };
    }

    public static SherpaOnnxBackendConfig Parse(ProviderProfile profile, string profileRoot)
    {
        var jsonPath = Path.Combine(profileRoot, "profile.json");
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException(
                $"Sherpa profile configuration was not found for '{profile.Name}'. Expected '{jsonPath}'.");

        using var stream = File.OpenRead(jsonPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        static string ReadString(JsonElement root, string name, string fallback = "")
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        static int ReadInt(JsonElement root, string name, int fallback)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
                ? result
                : fallback;

        static float ReadFloat(JsonElement root, string name, float fallback)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var result)
                ? result
                : fallback;

        static bool ReadBool(JsonElement root, string name, bool fallback)
            => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        var provider = ReadString(root, "provider", "cpu");
        var numThreads = ReadInt(root, "numThreads", Environment.ProcessorCount);
        var debug = ReadBool(root, "debug", false);
        var tokens = Path.Combine(profileRoot, ReadString(root, "tokens", "tokens.txt"));
        var modelType = ReadString(root, "modelType", "zipformer2_ctc");
        var modelingUnit = ReadString(root, "modelingUnit", "bpe");
        var bpeVocabValue = ReadString(root, "bpeVocab", "");
        var bpeVocab = string.IsNullOrWhiteSpace(bpeVocabValue) ? string.Empty : Path.Combine(profileRoot, bpeVocabValue);

        var vadElement = root.GetProperty("vad");
        var vad = new SherpaOnnxVadConfig(
            Path.Combine(profileRoot, ReadString(vadElement, "model", "silero_vad.onnx")),
            ReadFloat(vadElement, "threshold", 0.5f),
            ReadFloat(vadElement, "minSilenceDuration", 0.25f),
            ReadFloat(vadElement, "minSpeechDuration", 0.25f),
            ReadInt(vadElement, "windowSize", 512),
            ReadFloat(vadElement, "maxSpeechDuration", 30f));

        var lidElement = root.GetProperty("lid");
        var lid = new SherpaOnnxLidConfig(
            Path.Combine(profileRoot, ReadString(lidElement, "encoder", "lid-encoder.onnx")),
            Path.Combine(profileRoot, ReadString(lidElement, "decoder", "lid-decoder.onnx")),
            ReadInt(lidElement, "tailPaddings", 12));

        var diarizationElement = root.GetProperty("diarization");
        var diarization = new SherpaOnnxDiarizationConfig(
            Path.Combine(profileRoot, ReadString(diarizationElement, "segmentationModel", "diarization-segmentation.onnx")),
            Path.Combine(profileRoot, ReadString(diarizationElement, "embeddingModel", "speaker-embedding.onnx")),
            ReadInt(diarizationElement, "numClusters", 2),
            ReadFloat(diarizationElement, "threshold", 0.6f),
            ReadFloat(diarizationElement, "minDurationOn", 0.2f),
            ReadFloat(diarizationElement, "minDurationOff", 0.2f));

        var routes = new Dictionary<string, SherpaOnnxAsrRouteConfig>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("routes", out var routesElement) && routesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in routesElement.EnumerateObject())
            {
                var route = item.Value;
                var modelValue = ReadString(route, "model", "");
                var encoderValue = ReadString(route, "encoder", "");
                var decoderValue = ReadString(route, "decoder", "");
                var joinerValue = ReadString(route, "joiner", "");

                routes[item.Name] = new SherpaOnnxAsrRouteConfig(
                    ReadString(route, "family", "zipformer2_ctc"),
                    string.IsNullOrWhiteSpace(modelValue) ? string.Empty : Path.Combine(profileRoot, modelValue),
                    string.IsNullOrWhiteSpace(encoderValue) ? string.Empty : Path.Combine(profileRoot, encoderValue),
                    string.IsNullOrWhiteSpace(decoderValue) ? string.Empty : Path.Combine(profileRoot, decoderValue),
                    string.IsNullOrWhiteSpace(joinerValue) ? string.Empty : Path.Combine(profileRoot, joinerValue),
                    ReadString(route, "language", item.Name),
                    ReadString(route, "task", "transcribe"));
            }
        }

        if (routes.Count == 0)
            throw new InvalidOperationException($"Sherpa profile '{profile.Name}' does not define any ASR routes in '{jsonPath}'.");

        return new SherpaOnnxBackendConfig(provider, numThreads, debug, tokens, modelType, modelingUnit, bpeVocab, vad, lid, diarization, routes);
    }
}
