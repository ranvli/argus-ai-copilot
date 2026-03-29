using SherpaOnnx;
using System.Text.Json;

if (args.Length < 3)
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "usage: <model.onnx> <tokens.txt> <family>" }));
    return 2;
}

var modelPath = args[0];
var tokensPath = args[1];
var family = args[2];

if (!File.Exists(modelPath) || !File.Exists(tokensPath))
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        error = "required_model_files_missing",
        model = modelPath,
        tokens = tokensPath
    }));
    return 3;
}

try
{
    var modelConfig = new OfflineModelConfig
    {
        NumThreads = 1,
        Debug = 0,
        Provider = "cpu",
        Tokens = tokensPath,
        ModelType = "",
        ModelingUnit = family.Equals("omnilingual_offline_ctc", StringComparison.OrdinalIgnoreCase) ? "cjkchar" : string.Empty,
        BpeVocab = string.Empty
    };

    switch (family.Trim().ToLowerInvariant())
    {
        case "omnilingual_offline_ctc":
            modelConfig.Omnilingual = new OfflineOmnilingualAsrCtcModelConfig { Model = modelPath };
            break;
        case "wenet_ctc":
            modelConfig.WenetCtc = new OfflineWenetCtcModelConfig { Model = modelPath };
            break;
        case "zipformer_ctc":
            modelConfig.ZipformerCtc = new OfflineZipformerCtcModelConfig { Model = modelPath };
            break;
        case "sense_voice":
            modelConfig.SenseVoice = new OfflineSenseVoiceModelConfig { Model = modelPath, Language = "auto", UseInverseTextNormalization = 0 };
            break;
        default:
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = $"unsupported_family:{family}" }));
            return 4;
    }

    var config = new OfflineRecognizerConfig
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

    using var recognizer = new OfflineRecognizer(config);
    using var stream = recognizer.CreateStream();
    var silence = new float[16_000 / 4];
    stream.AcceptWaveform(16_000, silence);
    recognizer.Decode(stream);
    var result = stream.Result;

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        text = result.Text ?? string.Empty,
        model = modelPath,
        tokens = tokensPath,
        recognizer = "OfflineRecognizer",
        family
    }));

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        error = ex.Message,
        exceptionType = ex.GetType().FullName,
        model = modelPath,
        tokens = tokensPath
    }));
    return 1;
}
