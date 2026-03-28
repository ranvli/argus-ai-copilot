using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Argus.Audio.Capture;

/// <summary>
/// Probes WASAPI and WaveIn microphone backends for 1.5 seconds each
/// and reports per-backend health metrics used by
/// <see cref="MicrophoneCaptureSource"/> in Auto mode.
/// </summary>
public sealed class MicBackendProber
{
    private readonly ILogger _logger;

    public MicBackendProber(ILogger logger) => _logger = logger;

    public sealed class ProbeResult
    {
        public MicBackend Backend { get; init; }
        public float AverageRms { get; init; }
        public float AveragePeak { get; init; }
        public int CallbackCount { get; init; }
        public int AllZeroCallbacks { get; init; }
        public bool FirstCallbackAllZero { get; init; }
        public float HealthScore { get; init; }
        public string DeviceName { get; init; } = string.Empty;
        public float NativeRms => AverageRms;
        public float NativePeak => AveragePeak;
        public bool HasSignal => AveragePeak > 0.002f || AverageRms > 0.001f;

        public override string ToString()
            => $"{Backend} device='{DeviceName}' avg_rms={AverageRms:F4} avg_peak={AveragePeak:F4} callbacks={CallbackCount} all_zero={AllZeroCallbacks} first_zero={FirstCallbackAllZero} score={HealthScore:F2} signal={HasSignal}";
    }

    /// <summary>
    /// Probes both backends with a 1.5 s capture window.
    /// Returns results for WASAPI first, then WaveIn.
    /// </summary>
    public async Task<(ProbeResult Wasapi, ProbeResult WaveIn)> ProbeAsync(
        string?   wasapiDeviceId,
        int       waveInDeviceNumber,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[BackendProbe] Starting backend probe (1.5s each)...");

        var wasapiResult = await ProbeWasapiAsync(wasapiDeviceId, ct);
        var waveInResult = await ProbeWaveInAsync(waveInDeviceNumber, ct);

        _logger.LogInformation(
            "[BackendProbe] Results:  WASAPI={W}  WaveIn={V}",
            wasapiResult, waveInResult);

        return (wasapiResult, waveInResult);
    }

    private async Task<ProbeResult> ProbeWasapiAsync(string? deviceId, CancellationToken ct)
    {
        float totalRms = 0f;
        float totalPeak = 0f;
        int callbackCount = 0;
        int allZeroCallbacks = 0;
        bool firstCallbackAllZero = false;
        string deviceName = "Default WASAPI";

        WasapiCapture? capture = null;

        try
        {
            // Always open a fresh MMDevice handle for the probe.
            // Reusing a caller-supplied MMDevice RCW causes InvalidCastException
            // when the COM object was obtained in a prior session.
            using var freshEnum = new MMDeviceEnumerator();
            MMDevice freshDevice = string.IsNullOrWhiteSpace(deviceId)
                ? freshEnum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : freshEnum.GetDevice(deviceId);

            deviceName = freshDevice.FriendlyName;
            capture    = new WasapiCapture(freshDevice,
                             useEventSync:                  false,
                             audioBufferMillisecondsLength: 100);
            var fmt = capture.WaveFormat;

            _logger.LogInformation(
                "[BackendProbe.WASAPI] Opening WasapiCapture: device='{D}' " +
                "useEventSync=false audioBufferMs=100 shareMode=Shared fmt={F}",
                deviceName, AudioChunkDiagnostics.FormatSummary(fmt));

            BufferedWaveProvider buffer = new BufferedWaveProvider(fmt)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration          = TimeSpan.FromSeconds(5)
            };

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                var span = e.Buffer.AsSpan(0, e.BytesRecorded);
                var rms  = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                    ? AudioChunkDiagnostics.ComputeRmsFloat32(span)
                    : AudioChunkDiagnostics.ComputeRms(span);
                var peak = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                    ? AudioChunkDiagnostics.ComputePeakFloat32(span)
                    : AudioChunkDiagnostics.ComputePeak(span);
                var zeroRatio = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                    ? AudioChunkDiagnostics.ComputeMinMaxZeroFloat32(span).ZeroRatio
                    : AudioChunkDiagnostics.ComputeMinMaxZero(span).ZeroRatio;

                callbackCount++;
                totalRms += rms;
                totalPeak += peak;

                if (zeroRatio >= 1f)
                {
                    allZeroCallbacks++;
                    if (callbackCount == 1)
                        firstCallbackAllZero = true;
                }
            };

            capture.StartRecording();
            await Task.Delay(1500, ct).ConfigureAwait(false);
            capture.StopRecording();
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* probe cancelled — return what we have */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BackendProbe.WASAPI] Probe failed for device '{D}'.", deviceName);
        }
        finally
        {
            capture?.Dispose();
        }

        var result = new ProbeResult
        {
            Backend              = MicBackend.Wasapi,
            AverageRms           = callbackCount > 0 ? totalRms / callbackCount : 0f,
            AveragePeak          = callbackCount > 0 ? totalPeak / callbackCount : 0f,
            CallbackCount        = callbackCount,
            AllZeroCallbacks     = allZeroCallbacks,
            FirstCallbackAllZero = firstCallbackAllZero,
            HealthScore          = ComputeHealthScore(
                callbackCount > 0 ? totalRms / callbackCount : 0f,
                callbackCount > 0 ? totalPeak / callbackCount : 0f,
                callbackCount,
                allZeroCallbacks,
                firstCallbackAllZero),
            DeviceName           = deviceName
        };

        _logger.LogInformation("[BackendProbe.WASAPI] → {R}", result);
        return result;
    }

    private async Task<ProbeResult> ProbeWaveInAsync(int deviceNumber, CancellationToken ct)
    {
        float totalRms = 0f;
        float totalPeak = 0f;
        int callbackCount = 0;
        int allZeroCallbacks = 0;
        bool firstCallbackAllZero = false;
        string deviceName = WaveInMicrophoneBackend.DeviceCount > 0
            ? GetWaveInName(deviceNumber) : "no WaveIn devices";

        WaveInEvent? waveIn = null;

        try
        {
            if (WaveInMicrophoneBackend.DeviceCount == 0)
            {
                _logger.LogWarning("[BackendProbe.WaveIn] No WaveIn devices found.");
                return new ProbeResult
                {
                    Backend    = MicBackend.WaveIn,
                    DeviceName = "no WaveIn devices"
                };
            }

            var fmt = WaveInMicrophoneBackend.ResolvePreferredInputFormat(_logger, deviceNumber);

            deviceName = GetWaveInName(deviceNumber);

            _logger.LogInformation(
                "[BackendProbe.WaveIn] device #{N} '{D}' fmt={F}",
                deviceNumber, deviceName, AudioChunkDiagnostics.FormatSummary(fmt));

            waveIn = new WaveInEvent
            {
                DeviceNumber       = deviceNumber,
                WaveFormat         = fmt,
                BufferMilliseconds = 50
            };

            waveIn.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                var span = e.Buffer.AsSpan(0, e.BytesRecorded);
                var rms  = AudioChunkDiagnostics.ComputeRms(span);
                var peak = AudioChunkDiagnostics.ComputePeak(span);
                var zeroRatio = AudioChunkDiagnostics.ComputeMinMaxZero(span).ZeroRatio;

                callbackCount++;
                totalRms += rms;
                totalPeak += peak;

                if (zeroRatio >= 1f)
                {
                    allZeroCallbacks++;
                    if (callbackCount == 1)
                        firstCallbackAllZero = true;
                }
            };

            waveIn.StartRecording();
            await Task.Delay(1500, ct).ConfigureAwait(false);
            waveIn.StopRecording();
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* probe cancelled */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BackendProbe.WaveIn] Probe failed. Device #{N} '{D}'.", deviceNumber, deviceName);
        }
        finally
        {
            waveIn?.Dispose();
        }

        var result = new ProbeResult
        {
            Backend              = MicBackend.WaveIn,
            AverageRms           = callbackCount > 0 ? totalRms / callbackCount : 0f,
            AveragePeak          = callbackCount > 0 ? totalPeak / callbackCount : 0f,
            CallbackCount        = callbackCount,
            AllZeroCallbacks     = allZeroCallbacks,
            FirstCallbackAllZero = firstCallbackAllZero,
            HealthScore          = ComputeHealthScore(
                callbackCount > 0 ? totalRms / callbackCount : 0f,
                callbackCount > 0 ? totalPeak / callbackCount : 0f,
                callbackCount,
                allZeroCallbacks,
                firstCallbackAllZero),
            DeviceName           = deviceName
        };

        _logger.LogInformation("[BackendProbe.WaveIn] → {R}", result);
        return result;
    }

    private static string GetWaveInName(int deviceNumber)
    {
        try   { return WaveIn.GetCapabilities(deviceNumber).ProductName; }
        catch { return deviceNumber == 0 ? "Default WaveIn" : $"WaveIn #{deviceNumber}"; }
    }

    private static float ComputeHealthScore(
        float averageRms,
        float averagePeak,
        int callbackCount,
        int allZeroCallbacks,
        bool firstCallbackAllZero)
    {
        if (callbackCount <= 0)
            return 0f;

        var zeroRatio         = (float)allZeroCallbacks / callbackCount;
        var nonZeroCallbacks  = callbackCount - allZeroCallbacks;
        var firstZeroPenalty  = firstCallbackAllZero ? 5f : 0f;
        var score = (averageRms * 1000f)
                  + (averagePeak * 250f)
                  + (nonZeroCallbacks * 0.5f)
                  - (allZeroCallbacks * 1.0f)
                  - (zeroRatio * 20f)
                  - firstZeroPenalty;

        return MathF.Max(0f, score);
    }
}
