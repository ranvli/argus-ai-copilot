using Argus.Audio.Capture;

namespace Argus.Transcription.SherpaOnnx;

public interface ISherpaOnnxProvisioningService
{
    SherpaModelProvisioningState State { get; }
    string ModelRoot { get; }
    string? LastError { get; }

    Task<SherpaOnnxAssetValidationResult> EnsureProvisionedAsync(CancellationToken ct = default);
}
