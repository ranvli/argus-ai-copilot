namespace Argus.Transcription.SherpaOnnx;

internal sealed record SherpaOnnxBackendConfig(
    string Provider,
    int NumThreads,
    bool Debug,
    string Tokens,
    string ModelType,
    string ModelingUnit,
    string BpeVocab,
    SherpaOnnxVadConfig Vad,
    SherpaOnnxLidConfig Lid,
    SherpaOnnxDiarizationConfig Diarization,
    IReadOnlyDictionary<string, SherpaOnnxAsrRouteConfig> Routes);

internal sealed record SherpaOnnxVadConfig(
    string Model,
    float Threshold,
    float MinSilenceDuration,
    float MinSpeechDuration,
    int WindowSize,
    float MaxSpeechDuration);

internal sealed record SherpaOnnxLidConfig(
    string Encoder,
    string Decoder,
    int TailPaddings);

internal sealed record SherpaOnnxDiarizationConfig(
    string SegmentationModel,
    string EmbeddingModel,
    int NumClusters,
    float Threshold,
    float MinDurationOn,
    float MinDurationOff);

internal sealed record SherpaOnnxAsrRouteConfig(
    string Family,
    string Model,
    string Encoder,
    string Decoder,
    string Joiner,
    string Language,
    string Task);
