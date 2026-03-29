using SherpaOnnx;
using System.Text.Json;

if (args.Length < 2)
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "usage: <model.int8.onnx> <tokens.txt>" }));
    return 2;
}

var modelPath = args[0];
var tokensPath = args[1];

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
    var config = new OfflineRecognizerConfig
    {
        FeatConfig = new FeatureConfig
        {
            SampleRate = 16_000,
            FeatureDim = 80
        },
        ModelConfig = new OfflineModelConfig
        {
            NumThreads = 1,
            Debug = 0,
            Provider = "cpu",
            Tokens = tokensPath,
            ModelType = "",
            ModelingUnit = "cjkchar",
            BpeVocab = string.Empty,
            Omnilingual = new OfflineOmnilingualAsrCtcModelConfig
            {
                Model = modelPath
            }
        },
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
        family = "omnilingual_offline_ctc"
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
