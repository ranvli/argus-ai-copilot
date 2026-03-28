using NAudio.Wave;

namespace Argus.Audio.Diagnostics;

/// <summary>
/// Classifies a captured audio chunk into one of four diagnostic categories.
/// </summary>
public enum AudioChunkClass
{
    /// <summary>Native buffer is all-zero — device is not delivering any audio.</summary>
    NativeZero,

    /// <summary>Native buffer has a tiny signal below the speech floor.</summary>
    NativeNearSilent,

    /// <summary>Native signal was present but the conversion chain produced silence.</summary>
    ConversionDestroyedAudio,

    /// <summary>Both native and converted signals are in a usable range.</summary>
    HealthyAudio,
}

/// <summary>
/// Identifies why a debug WAV file was saved — used in log messages so
/// the reason is always explicit rather than implied.
/// </summary>
public enum DebugSaveReason
{
    /// <summary>First chunk in this session where native audio is all-zero or near-silent.</summary>
    FirstSilentChunk,

    /// <summary>First chunk in this session where the conversion chain destroyed a good signal.</summary>
    ConversionDestroyedAudio,

    /// <summary>Audio state transitioned from healthy to silent (or vice-versa).</summary>
    StateTransition,

    /// <summary>Periodic rate-limited sample — one every N chunks to confirm ongoing state.</summary>
    PeriodicSample,
}

/// <summary>
/// Signal-level diagnostics for audio chunks.
/// Supports both PCM16 (post-conversion) and IEEE float32 (native WASAPI) buffers.
/// </summary>
public static class AudioChunkDiagnostics
{
    // PCM16 diagnostics -------------------------------------------------------

    public static float ComputeRms(ReadOnlySpan<byte> pcm16Bytes)
    {
        if (pcm16Bytes.Length < 2) return 0f;
        var sampleCount = pcm16Bytes.Length / 2;
        double sumSq = 0.0;
        for (int i = 0; i + 1 < pcm16Bytes.Length; i += 2)
        {
            var sample = (short)(pcm16Bytes[i] | (pcm16Bytes[i + 1] << 8));
            sumSq += (double)sample * sample;
        }
        return (float)(Math.Sqrt(sumSq / sampleCount) / 32768.0);
    }

    public static float ComputePeak(ReadOnlySpan<byte> pcm16Bytes)
    {
        if (pcm16Bytes.Length < 2) return 0f;
        short maxAbs = 0;
        for (int i = 0; i + 1 < pcm16Bytes.Length; i += 2)
        {
            var sample = (short)(pcm16Bytes[i] | (pcm16Bytes[i + 1] << 8));
            var abs = sample == short.MinValue ? short.MaxValue : (short)Math.Abs(sample);
            if (abs > maxAbs) maxAbs = abs;
        }
        return maxAbs / 32768f;
    }

    /// <summary>Min sample, max sample, and ratio of exactly-zero samples for PCM16.</summary>
    public static (float Min, float Max, float ZeroRatio) ComputeMinMaxZero(ReadOnlySpan<byte> pcm16Bytes)
    {
        if (pcm16Bytes.Length < 2) return (0f, 0f, 1f);
        int sampleCount = pcm16Bytes.Length / 2;
        short minVal = short.MaxValue;
        short maxVal = short.MinValue;
        int zeroCount = 0;
        for (int i = 0; i + 1 < pcm16Bytes.Length; i += 2)
        {
            var s = (short)(pcm16Bytes[i] | (pcm16Bytes[i + 1] << 8));
            if (s < minVal) minVal = s;
            if (s > maxVal) maxVal = s;
            if (s == 0) zeroCount++;
        }
        return (minVal / 32768f, maxVal / 32768f, (float)zeroCount / sampleCount);
    }

    // IEEE float32 diagnostics (native WASAPI buffer) -------------------------

    public static float ComputeRmsFloat32(ReadOnlySpan<byte> ieeeBytes)
    {
        if (ieeeBytes.Length < 4) return 0f;
        var sampleCount = ieeeBytes.Length / 4;
        double sumSq = 0.0;
        for (int i = 0; i + 3 < ieeeBytes.Length; i += 4)
        {
            var sample = BitConverter.ToSingle(ieeeBytes[i..]);
            sumSq += (double)sample * sample;
        }
        return (float)Math.Sqrt(sumSq / sampleCount);
    }

    public static float ComputePeakFloat32(ReadOnlySpan<byte> ieeeBytes)
    {
        if (ieeeBytes.Length < 4) return 0f;
        float maxAbs = 0f;
        for (int i = 0; i + 3 < ieeeBytes.Length; i += 4)
        {
            var abs = Math.Abs(BitConverter.ToSingle(ieeeBytes[i..]));
            if (abs > maxAbs) maxAbs = abs;
        }
        return maxAbs;
    }

    /// <summary>Min sample, max sample, and ratio of exactly-zero samples for IEEE float32.</summary>
    public static (float Min, float Max, float ZeroRatio) ComputeMinMaxZeroFloat32(ReadOnlySpan<byte> ieeeBytes)
    {
        if (ieeeBytes.Length < 4) return (0f, 0f, 1f);
        int sampleCount = ieeeBytes.Length / 4;
        float minVal = float.MaxValue;
        float maxVal = float.MinValue;
        int zeroCount = 0;
        for (int i = 0; i + 3 < ieeeBytes.Length; i += 4)
        {
            var s = BitConverter.ToSingle(ieeeBytes[i..]);
            if (s < minVal) minVal = s;
            if (s > maxVal) maxVal = s;
            if (s == 0f) zeroCount++;
        }
        return (minVal, maxVal, (float)zeroCount / sampleCount);
    }

    // Chunk classification ----------------------------------------------------

    /// <summary>
    /// Classifies a chunk given its native-buffer peak and converted-buffer peak.
    /// </summary>
    public static AudioChunkClass ClassifyChunk(float nativePeak, float convertedPeak)
    {
        if (nativePeak < 0.0001f)  return AudioChunkClass.NativeZero;
        if (nativePeak < 0.002f)   return AudioChunkClass.NativeNearSilent;
        if (convertedPeak < 0.002f) return AudioChunkClass.ConversionDestroyedAudio;
        return AudioChunkClass.HealthyAudio;
    }

    // Shared helpers ----------------------------------------------------------

    /// <summary>
    /// Returns a human-readable signal diagnosis string based on RMS and peak values.
    /// Works for both PCM16-normalised (0-1) and float32 (0-1) values.
    /// </summary>
    public static string Diagnose(float rms, float peak)
    {
        if (peak < 0.002f) return "SILENT";
        if (rms  < 0.005f) return "NEAR_SILENT";
        if (rms  < 0.015f) return "VERY_QUIET";
        if (rms  < 0.050f) return "QUIET_SPEECH";
        if (rms  < 0.200f) return "NORMAL_SPEECH";
        if (rms  < 0.500f) return "LOUD_SPEECH";
        return "CLIPPING";
    }

    /// <summary>
    /// Produces a compact one-line summary of a WaveFormat for logging.
    /// Example: "IeeeFloat 48000Hz 32bit 2ch stereo"
    /// </summary>
    public static string FormatSummary(WaveFormat fmt)
        => $"{fmt.Encoding} {fmt.SampleRate}Hz {fmt.BitsPerSample}bit {fmt.Channels}ch" +
           (fmt.Channels == 1 ? " mono" : fmt.Channels == 2 ? " stereo" : $" {fmt.Channels}-channel");

    /// <summary>
    /// Formats a full per-chunk diagnostic line covering native and converted stats.
    /// Suitable for a single log entry per chunk.
    /// </summary>
    public static string FormatChunkDiag(
        string label,
        float nativeRms,  float nativePeak,
        float nativeMin,  float nativeMax,  float nativeZeroRatio,
        float convRms,    float convPeak,
        AudioChunkClass classification)
        => $"[{label}] " +
           $"native RMS={nativeRms:F4} Peak={nativePeak:F4} Min={nativeMin:F4} Max={nativeMax:F4} Zeros={nativeZeroRatio:P0}  |  " +
           $"conv RMS={convRms:F4} Peak={convPeak:F4}  |  CLASS={classification}";

    /// <summary>
    /// Deletes the oldest *.wav files in <paramref name="folder"/> until the file count
    /// is below <paramref name="maxFiles"/> AND the total size is below <paramref name="maxBytes"/>.
    /// Files are sorted by <see cref="FileInfo.CreationTimeUtc"/> ascending (oldest first).
    /// </summary>
    public static void RotateDebugFolder(string folder, int maxFiles, long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(folder)
                .GetFiles("*.wav")
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            long totalBytes = files.Sum(f => f.Length);

            foreach (var f in files)
            {
                if (files.Count - files.IndexOf(f) <= maxFiles && totalBytes <= maxBytes)
                    break;
                try
                {
                    totalBytes -= f.Length;
                    f.Delete();
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort — never block the audio thread */ }
    }
}