using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Argus.Audio.Capture;

/// <summary>
/// Captures system audio (all audio playing through an output device) using
/// WASAPI loopback mode via NAudio's <see cref="WasapiLoopbackCapture"/>.
///
/// Conversion uses a fully-managed chain (no MediaFoundation dependency):
///   WasapiLoopbackCapture (native: IeeeFloat 48kHz stereo on nearly all hardware)
///   → BufferedWaveProvider
///   → WaveToSampleProvider  (handles IeeeFloat and PCM → float32 ISampleProvider)
///   → StereoToMonoSampleProvider  (only when native is stereo)
///   → WdlResamplingSampleProvider (16 kHz, fully-managed sinc resampler)
///   → SampleToWaveProvider16      (float32 → int16 PCM)
///
/// Call <see cref="SetDevice"/> before <see cref="StartAsync"/> to target a specific
/// output device; otherwise the Windows default playback device is used.
/// </summary>
public sealed class SystemAudioCaptureSource : IAudioCaptureSource, IDisposable
{
    private readonly ILogger<SystemAudioCaptureSource> _logger;

    private const int TargetSampleRate    = 16_000;
    private const int TargetChannels      = 1;
    private const int TargetBitsPerSample = 16;

    private readonly TimeSpan _chunkDuration;
    private readonly int      _chunkBytes;

    private MMDevice?              _targetDevice;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider?  _captureBuffer;
    // Managed conversion chain — no MediaFoundation
    private IWaveProvider?         _pcm16Provider;
    private MemoryStream           _pcmBuffer = new();
    private Guid                   _sessionId;
    private volatile bool          _paused;
    private string?                _deviceName;

    // ── IAudioCaptureSource ───────────────────────────────────────────────────

    public string DisplayName => _deviceName ?? "System Audio (loopback)";
    public AudioCaptureStatus Status { get; private set; } = AudioCaptureStatus.NoDevice;
    public event EventHandler<AudioChunk>? ChunkReady;

    public SystemAudioCaptureSource(ILogger<SystemAudioCaptureSource> logger, TimeSpan? chunkDuration = null)
    {
        _logger        = logger;
        _chunkDuration = chunkDuration ?? TimeSpan.FromSeconds(2);
        _chunkBytes    = TargetSampleRate
                       * TargetChannels
                       * (TargetBitsPerSample / 8)
                       * (int)_chunkDuration.TotalSeconds;
    }

    /// <summary>
    /// Targets a specific WASAPI output device for loopback capture.
    /// Must be called before <see cref="StartAsync"/>.
    /// If not called, the Windows default playback device is used.
    /// </summary>
    public void SetDevice(MMDevice device)
    {
        _targetDevice = device;
        _deviceName   = device.FriendlyName;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (Status == AudioCaptureStatus.Capturing)
        {
            _logger.LogWarning("SystemAudioCaptureSource.StartAsync called while already capturing.");
            return;
        }

        _sessionId = sessionId;
        _paused    = false;

        try
        {
            _capture = _targetDevice is not null
                ? new WasapiLoopbackCapture(_targetDevice)
                : new WasapiLoopbackCapture();

            if (_deviceName is null)
                _deviceName = "Default Playback Device";

            var nativeFormat = _capture.WaveFormat;
            _logger.LogInformation(
                "SystemAudioCaptureSource: device='{Device}' native={Rate}Hz/{Bits}bit/{Ch}ch  session={Id}",
                _deviceName, nativeFormat.SampleRate, nativeFormat.BitsPerSample,
                nativeFormat.Channels, sessionId);

            _captureBuffer = new BufferedWaveProvider(nativeFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration          = TimeSpan.FromSeconds(10)
            };

            // Build a fully-managed conversion chain: no MediaFoundation involved.
            // Step 1: get a float32 ISampleProvider from whatever native format WASAPI gives us.
            ISampleProvider sampleProvider = _captureBuffer.ToSampleProvider();

            // Step 2: collapse to mono if the native source is multi-channel.
            if (nativeFormat.Channels > 1)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);

            // Step 3: resample to 16 kHz using the fully-managed WDL sinc resampler.
            if (nativeFormat.SampleRate != TargetSampleRate)
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

            // Step 4: convert float32 → int16 PCM.
            _pcm16Provider = new SampleToWaveProvider16(sampleProvider);

            _logger.LogInformation(
                "[SystemAudioSource.StartAsync] Conversion chain: {NativeRate}Hz/{NativeBits}bit/{NativeCh}ch → {TargetRate}Hz/16bit/1ch (managed, no MFT)",
                nativeFormat.SampleRate, nativeFormat.BitsPerSample, nativeFormat.Channels,
                TargetSampleRate);

            _pcmBuffer = new MemoryStream();

            _capture.DataAvailable    += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            Status = AudioCaptureStatus.Capturing;
            _logger.LogInformation(
                "[SystemAudioSource.StartAsync] Loopback capture started. Device='{Device}' ChunkDuration={Dur}s SessionId={Id}",
                _deviceName, _chunkDuration.TotalSeconds, sessionId);
        }
        catch (Exception ex)
        {
            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(
                ex, "[SystemAudioSource.StartAsync] Failed to start loopback on device '{Device}'.", _deviceName);
            DisposeCapture();
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is AudioCaptureStatus.Idle or AudioCaptureStatus.NoDevice)
            return;

        _logger.LogInformation(
            "[SystemAudioSource.StopAsync] Stopping loopback capture. Device='{Device}' Status={Status}",
            _deviceName, Status);
        _capture?.StopRecording();

        await Task.Delay(200, CancellationToken.None);

        DrainConverter();
        FlushBuffer(isFinal: true);
        DisposeCapture();
        Status = AudioCaptureStatus.Idle;
        _logger.LogInformation("[SystemAudioSource.StopAsync] Loopback capture stopped and disposed.");
    }

    public void Pause()
    {
        if (Status == AudioCaptureStatus.Capturing)
        {
            _paused = true;
            Status  = AudioCaptureStatus.Paused;
            _logger.LogDebug("SystemAudioCaptureSource: paused.");
        }
    }

    public void Resume()
    {
        if (Status == AudioCaptureStatus.Paused)
        {
            _paused = false;
            Status  = AudioCaptureStatus.Capturing;
            _logger.LogDebug("SystemAudioCaptureSource: resumed.");
        }
    }

    // ── NAudio callbacks ──────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_paused || e.BytesRecorded == 0) return;
        if (_captureBuffer is null || _pcm16Provider is null) return;

        try
        {
            _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            DrainConverter();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SystemAudioSource.OnDataAvailable] Exception processing audio data. Device='{Device}'", _deviceName);
        }
    }

    private void DrainConverter()
    {
        if (_pcm16Provider is null) return;
        var temp = new byte[4096];
        int read;
        while ((read = _pcm16Provider.Read(temp, 0, temp.Length)) > 0)
        {
            _pcmBuffer.Write(temp, 0, read);
            TryEmitChunk();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(
                e.Exception,
                "[SystemAudioSource.RecordingStopped] Recording stopped with error. Device='{Device}'",
                _deviceName);
        }
        else
        {
            _logger.LogInformation(
                "[SystemAudioSource.RecordingStopped] Recording stopped cleanly. Status={Status} Device='{Device}'",
                Status, _deviceName);
        }
    }

    // ── Chunk helpers ─────────────────────────────────────────────────────────

    private void TryEmitChunk()
    {
        while (_pcmBuffer.Length >= _chunkBytes)
        {
            var raw       = _pcmBuffer.ToArray();
            var chunkData = new byte[_chunkBytes];
            Buffer.BlockCopy(raw, 0, chunkData, 0, _chunkBytes);

            var remaining = raw.Length - _chunkBytes;
            var next      = new MemoryStream(Math.Max(remaining, 0));
            if (remaining > 0) next.Write(raw, _chunkBytes, remaining);
            _pcmBuffer = next;

            EmitChunk(chunkData, _chunkDuration);
        }
    }

    private void FlushBuffer(bool isFinal)
    {
        if (_pcmBuffer.Length == 0) return;
        var raw         = _pcmBuffer.ToArray();
        var bytesPerSec = TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8);
        var duration    = TimeSpan.FromSeconds((double)raw.Length / bytesPerSec);
        if (isFinal && duration >= TimeSpan.FromSeconds(1))
            EmitChunk(raw, duration);
        _pcmBuffer = new MemoryStream();
    }

    private void EmitChunk(byte[] data, TimeSpan duration)
    {
        var rms      = AudioChunkDiagnostics.ComputeRms(data);
        var peak     = AudioChunkDiagnostics.ComputePeak(data);
        var diagnosis = AudioChunkDiagnostics.Diagnose(rms, peak);

        _logger.LogInformation(
            "[SysChunk] Source=SystemAudio Duration={Dur:F2}s Bytes={Bytes} " +
            "SampleRate={Rate} Channels=1 Format=PCM16 " +
            "RMS={Rms:F4} Peak={Peak:F4} Signal={Signal}",
            duration.TotalSeconds, data.Length,
            TargetSampleRate,
            rms, peak, diagnosis);

        var chunk = new AudioChunk
        {
            SessionId  = _sessionId,
            Data       = data,
            CapturedAt = DateTimeOffset.UtcNow,
            Duration   = duration,
            Source     = AudioSource.SystemAudio
        };
        try   { ChunkReady?.Invoke(this, chunk); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "SystemAudioCaptureSource: ChunkReady handler threw."); }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    private void DisposeCapture()
    {
        // The managed chain providers are not IDisposable — just null the reference.
        _pcm16Provider = null;
        _captureBuffer = null;

        if (_capture is not null)
        {
            _capture.DataAvailable    -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }

    public void Dispose()
    {
        _logger.LogInformation(
            "[SystemAudioSource.Dispose] Dispose() called. Status={Status} Device='{Device}'",
            Status, _deviceName ?? "(not set)");
        FlushBuffer(isFinal: false);
        DisposeCapture();
        _pcmBuffer.Dispose();
        _targetDevice?.Dispose();
        _logger.LogInformation("[SystemAudioSource.Dispose] Disposal complete.");
    }
}
