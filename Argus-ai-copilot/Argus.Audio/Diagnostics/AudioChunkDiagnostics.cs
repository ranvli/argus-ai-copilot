namespace Argus.Audio.Diagnostics;

/// <summary>
/// Signal-level diagnostics for raw PCM16 (16-bit signed, mono, 16 kHz) audio chunks.
/// Used to verify that captured audio actually contains sound before it reaches Whisper.
/// </summary>
public static class AudioChunkDiagnostics
{
    /// <summary>
    /// Computes the Root Mean Square (RMS) level of a PCM16 byte array.
    /// <para>
    /// Returns a value in [0.0, 1.0] where:
    ///   - 0.000–0.005 = effectively silent (near-silence, background noise floor)
    ///   - 0.005–0.015 = very quiet (soft breathing, distant ambient)
    ///   - 0.015–0.050 = quiet speech or whisper
    ///   - 0.050–0.200 = normal conversational speech  ← healthy range
    ///   - 0.200–0.500 = loud speech / raised voice
    ///   - 0.500–1.000 = very loud / near clipping
    /// </para>
    /// </summary>
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

        var rms = Math.Sqrt(sumSq / sampleCount);
        return (float)(rms / 32768.0);
    }

    /// <summary>
    /// Finds the peak absolute amplitude of a PCM16 byte array.
    /// <para>
    /// Returns a value in [0.0, 1.0].
    /// For healthy speech the peak should be at least 0.05.
    /// A peak below 0.002 almost certainly indicates a silent/failed capture.
    /// </para>
    /// </summary>
    public static float ComputePeak(ReadOnlySpan<byte> pcm16Bytes)
    {
        if (pcm16Bytes.Length < 2) return 0f;

        short maxAbs = 0;

        for (int i = 0; i + 1 < pcm16Bytes.Length; i += 2)
        {
            var sample = (short)(pcm16Bytes[i] | (pcm16Bytes[i + 1] << 8));
            var abs    = sample == short.MinValue ? short.MaxValue : (short)Math.Abs(sample);
            if (abs > maxAbs) maxAbs = abs;
        }

        return maxAbs / 32768f;
    }

    /// <summary>
    /// Returns a human-readable signal diagnosis string based on RMS and peak values.
    /// </summary>
    public static string Diagnose(float rms, float peak)
    {
        if (peak < 0.002f) return "SILENT — capture may be broken (zeros)";
        if (rms  < 0.005f) return "near-silence — possible device issue or muted mic";
        if (rms  < 0.015f) return "very quiet — check mic gain / boost";
        if (rms  < 0.050f) return "quiet speech";
        if (rms  < 0.200f) return "normal speech ✓";
        if (rms  < 0.500f) return "loud speech";
        return "clipping / very loud";
    }
}
