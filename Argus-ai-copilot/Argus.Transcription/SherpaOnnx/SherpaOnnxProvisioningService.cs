using System.Formats.Tar;
using Argus.Audio.Capture;
using Argus.Transcription.Configuration;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Transcription.SherpaOnnx;

public sealed class SherpaOnnxProvisioningService : ISherpaOnnxProvisioningService
{
    private const string DefaultOmnilingualPackageUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-omnilingual-asr-1600-languages-300M-ctc-int8-2025-11-12.tar.bz2";

    private readonly SherpaOnnxModelService _modelService;
    private readonly TranscriptionRuntimeSettings _runtimeSettings;
    private readonly ILogger<SherpaOnnxProvisioningService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SherpaOnnxProvisioningService(
        SherpaOnnxModelService modelService,
        IOptions<TranscriptionRuntimeSettings> runtimeSettings,
        ILogger<SherpaOnnxProvisioningService> logger)
    {
        _modelService = modelService;
        _runtimeSettings = runtimeSettings.Value;
        _logger = logger;
    }

    public SherpaModelProvisioningState State { get; private set; } = SherpaModelProvisioningState.NotChecked;
    public string ModelRoot { get; private set; } = string.Empty;
    public string? LastError { get; private set; }

    public async Task<SherpaOnnxAssetValidationResult> EnsureProvisionedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = _modelService.GetProfileRoot("multilingual-streaming");
            ModelRoot = root;

            var validation = _modelService.ValidateDefaultAssets();
            if (validation.IsValid)
            {
                State = SherpaModelProvisioningState.Ready;
                LastError = null;
                return validation;
            }

            if (!_runtimeSettings.EnableSherpaAutoProvisioning)
            {
                State = SherpaModelProvisioningState.Error;
                LastError = validation.ToUserMessage();
                return validation;
            }

            State = SherpaModelProvisioningState.Provisioning;
            LastError = null;

            var packageUrl = string.IsNullOrWhiteSpace(_runtimeSettings.SherpaModelPackageUrl)
                ? DefaultOmnilingualPackageUrl
                : _runtimeSettings.SherpaModelPackageUrl.Trim();

            _logger.LogInformation(
                "[SherpaProvisioning] action=download url={Url} root={Root}",
                packageUrl,
                root);

            var tempDir = Path.Combine(Path.GetTempPath(), "argus-sherpa-bootstrap", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var archivePath = Path.Combine(tempDir, "model.tar.bz2");
                using (var client = new HttpClient())
                using (var response = await client.GetAsync(packageUrl, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                await ExtractTarBz2Async(archivePath, tempDir, ct).ConfigureAwait(false);
                CopyProvisionedFiles(tempDir, root);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }

            validation = _modelService.ValidateDefaultAssets();
            if (!validation.IsValid)
            {
                State = SherpaModelProvisioningState.Error;
                LastError = validation.ToUserMessage();
                return validation;
            }

            State = SherpaModelProvisioningState.Ready;
            LastError = null;
            _logger.LogInformation("[SherpaProvisioning] action=ready root={Root}", root);
            return validation;
        }
        catch (Exception ex)
        {
            State = SherpaModelProvisioningState.Error;
            LastError = ex.Message;
            _logger.LogError(ex, "[SherpaProvisioning] action=failed error={Error}", ex.Message);
            return _modelService.ValidateDefaultAssets();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ExtractTarBz2Async(string archivePath, string destinationRoot, CancellationToken ct)
    {
        var tarPath = Path.Combine(destinationRoot, "model.tar");
        await using (var input = File.OpenRead(archivePath))
        await using (var bz2 = new BZip2InputStream(input))
        await using (var output = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await bz2.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        TarFile.ExtractToDirectory(tarPath, destinationRoot, overwriteFiles: true);
    }

    private static void CopyProvisionedFiles(string extractedRoot, string targetRoot)
    {
        var model = Directory.GetFiles(extractedRoot, "model.int8.onnx", SearchOption.AllDirectories).FirstOrDefault();
        var tokens = Directory.GetFiles(extractedRoot, "tokens.txt", SearchOption.AllDirectories).FirstOrDefault();

        if (model is null || tokens is null)
            throw new FileNotFoundException("Provisioned Sherpa package did not contain model.int8.onnx and tokens.txt.");

        Directory.CreateDirectory(targetRoot);
        File.Copy(model, Path.Combine(targetRoot, "model.int8.onnx"), overwrite: true);
        File.Copy(tokens, Path.Combine(targetRoot, "tokens.txt"), overwrite: true);
    }
}
