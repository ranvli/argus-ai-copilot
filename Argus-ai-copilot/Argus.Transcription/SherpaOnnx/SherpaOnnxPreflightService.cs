using System.Diagnostics;
using System.Text.Json;
using Argus.Audio.Capture;
using Microsoft.Extensions.Logging;

namespace Argus.Transcription.SherpaOnnx;

public sealed class SherpaOnnxPreflightService : ISherpaOnnxPreflightService
{
    private readonly SherpaOnnxModelService _modelService;
    private readonly ILogger<SherpaOnnxPreflightService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SherpaOnnxPreflightService(
        SherpaOnnxModelService modelService,
        ILogger<SherpaOnnxPreflightService> logger)
    {
        _modelService = modelService;
        _logger = logger;
    }

    public SherpaNativeReadinessState State { get; private set; } = SherpaNativeReadinessState.NotChecked;
    public string? LastError { get; private set; }
    public bool IsSafeToUse => State == SherpaNativeReadinessState.Ready;

    public async Task<bool> RunPreflightAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = _modelService.GetProfileRoot(SherpaOnnxModelService.DefaultModelId);
            var model = Path.Combine(root, "model.int8.onnx");
            var tokens = Path.Combine(root, "tokens.txt");
            var family = _modelService.GetConfiguredModelFamily();

            State = SherpaNativeReadinessState.PreflightRunning;
            LastError = null;
            _logger.LogInformation("[SherpaPreflight] state={State} step=start root={Root}", State, root);

            var helperPath = ResolveHelperPath();
            _logger.LogInformation("[SherpaPreflight] helperPath={HelperPath} exists={Exists}", helperPath, File.Exists(helperPath));
            if (!File.Exists(helperPath))
            {
                State = SherpaNativeReadinessState.PreflightFailed;
                LastError = $"Sherpa preflight helper is missing: {helperPath}";
                _logger.LogError("[SherpaPreflight] state={State} step=missing_helper path={Path}", State, helperPath);
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"\"{model}\" \"{tokens}\" \"{family}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            _logger.LogInformation(
                "[SherpaPreflight] step=exit exitCode={ExitCode} stdout={Stdout} stderr={Stderr}",
                process.ExitCode,
                stdout,
                stderr);

            if (process.ExitCode != 0)
            {
                State = SherpaNativeReadinessState.PreflightFailed;
                LastError = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                _logger.LogError("[SherpaPreflight] state={State} step=child_failed error={Error}", State, LastError);
                return false;
            }

            using var doc = JsonDocument.Parse(stdout);
            var ok = doc.RootElement.TryGetProperty("ok", out var okValue) && okValue.GetBoolean();
            if (!ok)
            {
                State = SherpaNativeReadinessState.PreflightFailed;
                LastError = stdout;
                _logger.LogError("[SherpaPreflight] state={State} step=reported_failure error={Error}", State, LastError);
                return false;
            }

            State = SherpaNativeReadinessState.PreflightPassed;
            _logger.LogInformation("[SherpaPreflight] state={State} step=passed", State);
            State = SherpaNativeReadinessState.Ready;
            return true;
        }
        catch (OperationCanceledException ex)
        {
            State = SherpaNativeReadinessState.PreflightFailed;
            LastError = "Sherpa native preflight timed out.";
            _logger.LogError(ex, "[SherpaPreflight] state={State} step=timeout error={Error}", State, LastError);
            return false;
        }
        catch (Exception ex)
        {
            State = SherpaNativeReadinessState.PreflightFailed;
            LastError = ex.Message;
            _logger.LogError(ex, "[SherpaPreflight] state={State} step=exception error={Error}", State, LastError);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ResolveHelperPath()
    {
        var exeName = OperatingSystem.IsWindows() ? "Argus.SherpaPreflight.exe" : "Argus.SherpaPreflight";
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, exeName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Argus.SherpaPreflight", "bin", "Debug", "net10.0", exeName)),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Argus.SherpaPreflight", "bin", "Release", "net10.0", exeName))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
