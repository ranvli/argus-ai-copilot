namespace Argus.Transcription.SherpaOnnx;

internal sealed record SherpaOnnxBackendConfig(
    string Family,
    string Provider,
    int NumThreads,
    bool Debug,
    string Tokens,
    string ModelType,
    string ModelingUnit,
    string BpeVocab,
    string Model);
