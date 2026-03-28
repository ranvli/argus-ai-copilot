using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Argus.Audio.Capture;

public sealed class RawMicDiagnosticRecorder
{
    private readonly ILogger _logger;
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(5);
    private readonly string _debugFolder;

    public RawMicDiagnosticRecorder(ILogger logger)
    {
        _logger = logger;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _debugFolder = Path.Combine(appData, "ArgusAI", "debug", "audio");
        Directory.CreateDirectory(_debugFolder);
    }

    public sealed class Result
    {
        public string FilePath { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public string Format { get; init; } = string.Empty;
        public int CallbackCount { get; init; }
        public long TotalBytesWritten { get; init; }
        public float AverageRms { get; init; }
        public float MaxPeak { get; init; }
        public bool AnyNonZeroSignalSeen { get; init; }
    }

    public async Task<Result> RecordWaveInAsync(int deviceNumber, CancellationToken ct = default)
    {
        var format = new WaveFormat(44100, 16, 1);
        var deviceName = GetWaveInName(deviceNumber);
        var filePath = Path.Combine(_debugFolder, $"raw_wavein_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.wav");

        using var writer = new WaveFileWriter(filePath, format);
        using var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = format,
            BufferMilliseconds = 50
        };

        var stoppedTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        long totalBytesWritten = 0;
        float rmsTotal = 0f;
        float maxPeak = 0f;
        bool anyNonZeroSignalSeen = false;

        waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
                return;

            writer.Write(e.Buffer, 0, e.BytesRecorded);
            callbackCount++;
            totalBytesWritten += e.BytesRecorded;

            var span = e.Buffer.AsSpan(0, e.BytesRecorded);
            var rms = AudioChunkDiagnostics.ComputeRms(span);
            var peak = AudioChunkDiagnostics.ComputePeak(span);
            rmsTotal += rms;
            maxPeak = Math.Max(maxPeak, peak);
            anyNonZeroSignalSeen |= peak > 0.0001f;
        };

        waveIn.RecordingStopped += (_, e) => stoppedTcs.TrySetResult(e.Exception);

        _logger.LogInformation(
            "[RawMicDiagnostic] Starting WaveIn raw capture. Device='{Device}' Format={Format} Path={Path}",
            deviceName, AudioChunkDiagnostics.FormatSummary(format), filePath);

        waveIn.StartRecording();
        try
        {
            await Task.Delay(CaptureDuration, ct).ConfigureAwait(false);
        }
        finally
        {
            waveIn.StopRecording();
        }

        var stopResult = await stoppedTcs.Task.ConfigureAwait(false);
        if (stopResult is not null)
            throw stopResult;

        var result = new Result
        {
            FilePath = filePath,
            DeviceName = deviceName,
            Format = AudioChunkDiagnostics.FormatSummary(format),
            CallbackCount = callbackCount,
            TotalBytesWritten = totalBytesWritten,
            AverageRms = callbackCount > 0 ? rmsTotal / callbackCount : 0f,
            MaxPeak = maxPeak,
            AnyNonZeroSignalSeen = anyNonZeroSignalSeen
        };

        _logger.LogInformation(
            "[RawMicDiagnostic] WaveIn saved. Path={Path} Device='{Device}' Format={Format} Callbacks={Callbacks} TotalBytes={TotalBytes} AvgRms={AvgRms:F4} MaxPeak={MaxPeak:F4} AnyNonZeroSignal={AnyNonZero}",
            result.FilePath, result.DeviceName, result.Format, result.CallbackCount, result.TotalBytesWritten,
            result.AverageRms, result.MaxPeak, result.AnyNonZeroSignalSeen);

        return result;
    }

    public async Task<Result> RecordWasapiAsync(string? deviceId = null, CancellationToken ct = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            : enumerator.GetDevice(deviceId);

        using var capture = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 100);
        var format = capture.WaveFormat;
        var filePath = Path.Combine(_debugFolder, $"raw_wasapi_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.wav");
        using var writer = new WaveFileWriter(filePath, format);

        var stoppedTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        long totalBytesWritten = 0;
        float rmsTotal = 0f;
        float maxPeak = 0f;
        bool anyNonZeroSignalSeen = false;
        var isFloat = WasapiIsolationTest.IsIeeeFloat(format);

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
                return;

            writer.Write(e.Buffer, 0, e.BytesRecorded);
            callbackCount++;
            totalBytesWritten += e.BytesRecorded;

            var span = e.Buffer.AsSpan(0, e.BytesRecorded);
            var rms = isFloat
                ? AudioChunkDiagnostics.ComputeRmsFloat32(span)
                : AudioChunkDiagnostics.ComputeRms(span);
            var peak = isFloat
                ? AudioChunkDiagnostics.ComputePeakFloat32(span)
                : AudioChunkDiagnostics.ComputePeak(span);
            rmsTotal += rms;
            maxPeak = Math.Max(maxPeak, peak);
            anyNonZeroSignalSeen |= peak > 0.0001f;
        };

        capture.RecordingStopped += (_, e) => stoppedTcs.TrySetResult(e.Exception);

        _logger.LogInformation(
            "[RawMicDiagnostic] Starting WASAPI raw capture. Device='{Device}' Format={Format} Path={Path}",
            device.FriendlyName, AudioChunkDiagnostics.FormatSummary(format), filePath);

        capture.StartRecording();
        try
        {
            await Task.Delay(CaptureDuration, ct).ConfigureAwait(false);
        }
        finally
        {
            capture.StopRecording();
        }

        var stopResult = await stoppedTcs.Task.ConfigureAwait(false);
        if (stopResult is not null)
            throw stopResult;

        var result = new Result
        {
            FilePath = filePath,
            DeviceName = device.FriendlyName,
            Format = AudioChunkDiagnostics.FormatSummary(format),
            CallbackCount = callbackCount,
            TotalBytesWritten = totalBytesWritten,
            AverageRms = callbackCount > 0 ? rmsTotal / callbackCount : 0f,
            MaxPeak = maxPeak,
            AnyNonZeroSignalSeen = anyNonZeroSignalSeen
        };

        _logger.LogInformation(
            "[RawMicDiagnostic] WASAPI saved. Path={Path} Device='{Device}' Format={Format} Callbacks={Callbacks} TotalBytes={TotalBytes} AvgRms={AvgRms:F4} MaxPeak={MaxPeak:F4} AnyNonZeroSignal={AnyNonZero}",
            result.FilePath, result.DeviceName, result.Format, result.CallbackCount, result.TotalBytesWritten,
            result.AverageRms, result.MaxPeak, result.AnyNonZeroSignalSeen);

        return result;
    }

    private static string GetWaveInName(int deviceNumber)
    {
        try
        {
            return WaveIn.GetCapabilities(deviceNumber).ProductName;
        }
        catch
        {
            return deviceNumber == 0 ? "Default WaveIn" : $"WaveIn #{deviceNumber}";
        }
    }
}
