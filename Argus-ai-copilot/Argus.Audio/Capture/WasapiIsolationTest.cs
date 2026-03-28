using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Argus.Audio.Capture;

/// <summary>
/// Minimal isolated WASAPI microphone recorder used purely for diagnosis.
///
/// Run this BEFORE starting the real pipeline (no loopback, no prober, no
/// conversion chain) to confirm that the endpoint delivers real audio at the
/// NAudio layer.  If this returns non-zero samples and the full pipeline
/// returns zeros, the bug is in the pipeline setup (AEC, prober timing,
/// format handling).  If this also returns zeros, the bug is below NAudio
/// (driver, OS audio engine, device state).
///
/// Usage (from SessionCoordinatorService or a test button):
///
///   var result = await WasapiIsolationTest.RunAsync(mmMicDevice, logger);
///   logger.LogInformation("IsolationTest: {Result}", result);
///
/// The test writes a WAV file to %LocalAppData%\ArgusAI\debug\audio\
/// so the sample can be opened in Audacity.
/// </summary>
public static class WasapiIsolationTest
{
    public sealed class Result
    {
        public string  DeviceName       { get; init; } = string.Empty;
        public string  NativeFormat     { get; init; } = string.Empty;
        public int     CallbackCount    { get; init; }
        public long    TotalBytesRead   { get; init; }
        public float   PeakValue        { get; init; }
        public float   RmsValue         { get; init; }
        public bool    HasSignal        => PeakValue > 0.002f;
        public string? WavFilePath      { get; init; }
        public string? ErrorMessage     { get; init; }

        public override string ToString() =>
            $"device='{DeviceName}' fmt={NativeFormat} callbacks={CallbackCount} " +
            $"bytes={TotalBytesRead} peak={PeakValue:F4} rms={RmsValue:F4} " +
            $"signal={HasSignal} wav={WavFilePath ?? "(none)"}" +
            (ErrorMessage is not null ? $" ERROR={ErrorMessage}" : string.Empty);
    }

    /// <summary>
    /// Opens <paramref name="device"/> in a completely isolated WASAPI session
    /// (no prober, no loopback, no conversion chain) and records for
    /// <paramref name="durationSeconds"/> seconds.
    ///
    /// The device is opened fresh from the provided ID — the caller's
    /// MMDevice object is NOT used so there is no shared COM state with an
    /// existing capture session.
    /// </summary>
    public static async Task<Result> RunAsync(
        MMDevice    device,
        ILogger     logger,
        int         durationSeconds = 5,
        CancellationToken ct        = default)
    {
        var deviceId   = device.ID;
        var deviceName = device.FriendlyName;

        logger.LogInformation(
            "[IsolationTest] Starting {Dur}s isolated WASAPI capture on '{Name}' (id={Id}). " +
            "No loopback, no prober, no conversion chain.",
            durationSeconds, deviceName, deviceId);

        // ── Resolve debug folder ─────────────────────────────────────────────
        var appData     = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var debugFolder = Path.Combine(appData, "ArgusAI", "debug", "audio");
        Directory.CreateDirectory(debugFolder);

        int       callbackCount  = 0;
        long      totalBytes     = 0;
        float     maxPeak        = 0f;
        double    sumSq          = 0.0;
        long      sampleCount    = 0;
        bool      firstLogged    = false;
        WaveFormat? capturedFmt  = null;

        // Accumulate raw bytes for a WAV artifact (cap at 10 s worth).
        var rawAccum   = new MemoryStream();
        long accumCap  = 0;

        WasapiCapture? capture = null;
        string?        error   = null;
        string?        wavPath = null;

        try
        {
            // Open a BRAND NEW MMDevice handle — do NOT reuse the caller's object.
            // This avoids any lingering COM state from the prober or a previous session.
            using var freshEnum = new MMDeviceEnumerator();
            var freshDevice     = freshEnum.GetDevice(deviceId);

            capture    = new WasapiCapture(freshDevice, useEventSync: false, audioBufferMillisecondsLength: 100);
            capturedFmt = capture.WaveFormat;
            accumCap    = (long)(capturedFmt.AverageBytesPerSecond * (durationSeconds + 1));

            logger.LogInformation(
                "[IsolationTest] Endpoint opened. Format={Rate}Hz/{Bits}bit/{Ch}ch Enc={Enc}",
                capturedFmt.SampleRate, capturedFmt.BitsPerSample,
                capturedFmt.Channels, capturedFmt.Encoding);

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                callbackCount++;
                totalBytes += e.BytesRecorded;

                var span    = e.Buffer.AsSpan(0, e.BytesRecorded);
                bool isFloat = IsIeeeFloat(capturedFmt!);

                var peak = isFloat
                    ? AudioChunkDiagnostics.ComputePeakFloat32(span)
                    : AudioChunkDiagnostics.ComputePeak(span);
                var rms  = isFloat
                    ? AudioChunkDiagnostics.ComputeRmsFloat32(span)
                    : AudioChunkDiagnostics.ComputeRms(span);

                if (peak > maxPeak) maxPeak = peak;
                sumSq        += rms * rms;
                sampleCount++;

                // Accumulate raw bytes for WAV write
                if (rawAccum.Length < accumCap)
                    rawAccum.Write(e.Buffer, 0, e.BytesRecorded);

                // Log first callback and first non-zero callback
                if (!firstLogged)
                {
                    firstLogged = true;
                    var dump = BuildSampleDump(span, isFloat, 16);
                    logger.LogInformation(
                        "[IsolationTest] First callback: bytes={B} peak={P:F4} rms={R:F4} samples=[{S}]",
                        e.BytesRecorded, peak, rms, dump);
                }
                else if (peak > 0.002f && callbackCount <= 20)
                {
                    logger.LogInformation(
                        "[IsolationTest] NON-ZERO at callback #{N}: peak={P:F4} rms={R:F4}",
                        callbackCount, peak, rms);
                }
            };

            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    logger.LogError(e.Exception, "[IsolationTest] Recording stopped with error.");
                else
                    logger.LogInformation("[IsolationTest] Recording stopped cleanly.");
            };

            capture.StartRecording();
            logger.LogInformation("[IsolationTest] Recording started — waiting {Dur}s...", durationSeconds);

            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[IsolationTest] Cancelled.");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "[IsolationTest] Exception during isolated capture.");
        }
        finally
        {
            try { capture?.StopRecording(); } catch { /* best-effort */ }
            await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
            capture?.Dispose();
        }

        // ── Write WAV artifact ───────────────────────────────────────────────
        if (capturedFmt is not null && rawAccum.Length > 0 && error is null)
        {
            try
            {
                var stamp   = DateTimeOffset.UtcNow;
                wavPath     = Path.Combine(debugFolder,
                    $"isolation_test_{stamp:yyyyMMdd_HHmmss}_peak{maxPeak:F3}.wav");
                var raw     = rawAccum.ToArray();
                using var wf = new WaveFileWriter(wavPath, capturedFmt);
                wf.Write(raw, 0, raw.Length);
                logger.LogInformation(
                    "[IsolationTest] WAV written: {Path}  ({Bytes}B, {Dur:F2}s)",
                    wavPath, raw.Length,
                    raw.Length / (double)capturedFmt.AverageBytesPerSecond);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[IsolationTest] Could not write WAV artifact.");
            }
        }

        var overallRms = sampleCount > 0 ? (float)Math.Sqrt(sumSq / sampleCount) : 0f;

        var result = new Result
        {
            DeviceName     = deviceName,
            NativeFormat   = capturedFmt is not null
                ? AudioChunkDiagnostics.FormatSummary(capturedFmt)
                : "(unknown)",
            CallbackCount  = callbackCount,
            TotalBytesRead = totalBytes,
            PeakValue      = maxPeak,
            RmsValue       = overallRms,
            WavFilePath    = wavPath,
            ErrorMessage   = error,
        };

        logger.LogInformation(
            "[IsolationTest] Done. {Result}  HasSignal={Signal}",
            result, result.HasSignal);

        if (!result.HasSignal && error is null)
            logger.LogWarning(
                "[IsolationTest] RESULT: zero signal in isolated capture. " +
                "The endpoint itself is delivering silence — this is a driver or OS audio engine issue, " +
                "NOT a pipeline bug.  Possible causes: AEC from another app, exclusive-mode held by " +
                "another app, driver silence on open.");

        rawAccum.Dispose();
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true for plain IeeeFloat AND for WaveFormatExtensible whose
    /// sub-format is IEEE float (Guid 00000003-0000-0010-8000-00aa00389b71).
    /// </summary>
    internal static bool IsIeeeFloat(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        if (fmt is WaveFormatExtensible ext)
            return ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        return false;
    }

    private static string BuildSampleDump(ReadOnlySpan<byte> span, bool isFloat, int count)
    {
        var sb    = new System.Text.StringBuilder();
        int taken = 0;
        if (isFloat)
        {
            for (int i = 0; i + 3 < span.Length && taken < count; i += 4, taken++)
                sb.Append($"{BitConverter.ToSingle(span[i..]):F4} ");
        }
        else
        {
            for (int i = 0; i + 1 < span.Length && taken < count; i += 2, taken++)
                sb.Append($"{(short)(span[i] | (span[i + 1] << 8))} ");
        }
        return sb.ToString().TrimEnd();
    }
}
