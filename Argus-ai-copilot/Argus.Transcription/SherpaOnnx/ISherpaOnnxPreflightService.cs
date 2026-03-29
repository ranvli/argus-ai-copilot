using Argus.Audio.Capture;

namespace Argus.Transcription.SherpaOnnx;

public interface ISherpaOnnxPreflightService
{
    SherpaNativeReadinessState State { get; }
    string? LastError { get; }
    bool IsSafeToUse { get; }

    Task<bool> RunPreflightAsync(CancellationToken ct = default);
}
