using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Argus.Audio.Capture;

/// <summary>
/// Captures system audio (all audio playing through an output device) using
/// WASAPI loopback mode via NAudio's <see cref="WasapiLoopbackCapture"/>.
/// </summary>
public sealed class SystemAudioCaptureSource : IAudioCaptureSource, IDisposable
{
    private readonly ILogger<SystemAudioCaptureSource> _logger;
    private readonly Lock _stateLock = new();

    private const int TargetSampleRate    = 16_000;
    private const int TargetChannels      = 1;
    private const int TargetBitsPerSample = 16;
    private const int DrainBlockBytes     = 512;
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(2);

    private TimeSpan _chunkDuration;
    private int      _chunkBytes;

    private MMDevice?              _targetDevice;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider?  _captureBuffer;
    private IWaveProvider?         _pcm16Provider;
    private MemoryStream           _pcmBuffer = new();
    private Guid                   _sessionId;
    private volatile bool          _paused;
    private volatile bool          _stopping;
    private volatile bool          _disposed;
    private string?                _deviceName;
    private string?                _deviceId;
    private WaveFormat?            _nativeFormat;

    private float _nativeRms;
    private float _nativePeak;
    private float _convertedRms;
    private DateTimeOffset? _lastCallbackAt;
    private DateTimeOffset? _lastHealthySignalAt;

    private long _callbackSequence;
    private long _callbacksInWindow;
    private long _bytesInWindow;
    private double _rmsSumInWindow;
    private float _maxPeakInWindow;
    private long _zeroCallbacksInWindow;
    private long _nearSilentCallbacksInWindow;
    private long _emittedChunksInWindow;
    private long _droppedChunksInWindow;
    private DateTimeOffset _healthWindowStartedAt = DateTimeOffset.UtcNow;
    private int _zeroReadStreak;
    private int _conversionSequence;

    public string DisplayName => _deviceName ?? "System Audio (loopback)";
    public AudioCaptureStatus Status { get; private set; } = AudioCaptureStatus.NoDevice;
    public event EventHandler<AudioChunk>? ChunkReady;

    public float NativeRms => _nativeRms;
    public float NativePeak => _nativePeak;
    public float ConvertedRms => _convertedRms;
    public DateTimeOffset? LastCallbackAt => _lastCallbackAt;
    public DateTimeOffset? LastHealthySignalAt => _lastHealthySignalAt;

    public SystemAudioCaptureSource(ILogger<SystemAudioCaptureSource> logger, TimeSpan? chunkDuration = null)
    {
        _logger = logger;
        SetChunkDuration(chunkDuration ?? TimeSpan.FromSeconds(1));
    }

    public void SetChunkDuration(TimeSpan chunkDuration)
    {
        if (chunkDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(chunkDuration));

        _chunkDuration = chunkDuration;
        _chunkBytes = Math.Max(1, (int)Math.Round(
            TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8d) * chunkDuration.TotalSeconds));
    }

    public void SetDevice(MMDevice device)
    {
        _targetDevice = device;
        _deviceName   = device.FriendlyName;
        _deviceId     = device.ID;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Status == AudioCaptureStatus.Capturing)
        {
            _logger.LogWarning("[SystemAudio.Start] duplicate_start sessionId={SessionId} device='{Device}'", sessionId, _deviceName);
            return;
        }

        _sessionId = sessionId;
        _paused = false;
        _stopping = false;
        ResetDiagnostics();

        try
        {
            _capture = _targetDevice is not null
                ? new WasapiLoopbackCapture(_targetDevice)
                : new WasapiLoopbackCapture();

            _deviceName ??= "Default Playback Device";
            _nativeFormat = _capture.WaveFormat;
            var nativeFormat = _nativeFormat;

            bool loopbackIsDefault = false;
            try
            {
                using var tmpEnum = new MMDeviceEnumerator();
                var defaultDev = tmpEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId ??= defaultDev.ID;
                loopbackIsDefault = _targetDevice is null || _targetDevice.ID == defaultDev.ID;
            }
            catch { }

            _logger.LogInformation(
                "[SystemAudio.Start] sessionId={SessionId} source=SystemAudio backend=WasapiLoopback deviceId='{DeviceId}' device='{Device}' isDefault={IsDefault} native={Rate}Hz/{Bits}bit/{Channels}ch/{Encoding} target=16000Hz/16bit/1ch chunkMs={ChunkMs:F0} expectedChunkBytes={ChunkBytes} utc={Utc:O} thread={Thread}",
                sessionId,
                _deviceId ?? "(unknown)",
                _deviceName,
                loopbackIsDefault,
                nativeFormat.SampleRate,
                nativeFormat.BitsPerSample,
                nativeFormat.Channels,
                nativeFormat.Encoding,
                _chunkDuration.TotalMilliseconds,
                _chunkBytes,
                DateTimeOffset.UtcNow,
                Environment.CurrentManagedThreadId);

            _captureBuffer = new BufferedWaveProvider(nativeFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(10)
            };

            ISampleProvider sampleProvider = _captureBuffer.ToSampleProvider();
            if (nativeFormat.Channels > 1)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
            if (nativeFormat.SampleRate != TargetSampleRate)
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);
            _pcm16Provider = new SampleToWaveProvider16(sampleProvider);

            _pcmBuffer = new MemoryStream();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            Status = AudioCaptureStatus.Capturing;
            _logger.LogInformation(
                "[Audio.Source.Start] sessionId={SessionId} source=SystemAudio backend=WasapiLoopback device='{Device}' status={Status}",
                sessionId, _deviceName, Status);
        }
        catch (Exception ex)
        {
            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(ex,
                "[SystemAudio.Start] failed sessionId={SessionId} source=SystemAudio device='{Device}'",
                sessionId, _deviceName);
            lock (_stateLock)
            {
                DisposeCaptureLocked();
            }
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (Status is AudioCaptureStatus.Idle or AudioCaptureStatus.NoDevice &&
            _capture is null && _captureBuffer is null && _pcm16Provider is null)
            return;

        _stopping = true;
        _logger.LogInformation(
            "[Audio.Source.Stop] sessionId={SessionId} source=SystemAudio backend=WasapiLoopback device='{Device}' status={Status}",
            _sessionId, _deviceName, Status);

        try { _capture?.StopRecording(); }
        catch (ObjectDisposedException) { }

        await Task.Delay(200, CancellationToken.None);

        lock (_stateLock)
        {
            if (_disposed) return;
            DrainConverterLocked();
            FlushBufferLocked(isFinal: true);
            DisposeCaptureLocked();
            Status = AudioCaptureStatus.Idle;
        }

        _stopping = false;
        LogHealthSummary(force: true);
    }

    public void Pause()
    {
        if (Status == AudioCaptureStatus.Capturing)
        {
            _paused = true;
            Status = AudioCaptureStatus.Paused;
        }
    }

    public void Resume()
    {
        if (Status == AudioCaptureStatus.Paused)
        {
            _paused = false;
            Status = AudioCaptureStatus.Capturing;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _stopping || _paused || e.BytesRecorded == 0) return;

        try
        {
            lock (_stateLock)
            {
                if (_disposed || _stopping || _captureBuffer is null || _pcm16Provider is null)
                    return;

                var now = DateTimeOffset.UtcNow;
                var span = e.Buffer.AsSpan(0, e.BytesRecorded);
                var isFloat = _nativeFormat is not null &&
                              (_nativeFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                               WasapiIsolationTest.IsIeeeFloat(_nativeFormat));
                var nRms = isFloat
                    ? AudioChunkDiagnostics.ComputeRmsFloat32(span)
                    : AudioChunkDiagnostics.ComputeRms(span);
                var nPeak = isFloat
                    ? AudioChunkDiagnostics.ComputePeakFloat32(span)
                    : AudioChunkDiagnostics.ComputePeak(span);
                var (_, _, nZeros) = isFloat
                    ? AudioChunkDiagnostics.ComputeMinMaxZeroFloat32(span)
                    : AudioChunkDiagnostics.ComputeMinMaxZero(span);

                _nativeRms = nRms;
                _nativePeak = nPeak;
                _lastCallbackAt = now;
                if (nPeak >= 0.002f)
                    _lastHealthySignalAt = now;

                var seq = Interlocked.Increment(ref _callbackSequence);
                _callbacksInWindow++;
                _bytesInWindow += e.BytesRecorded;
                _rmsSumInWindow += nRms;
                _maxPeakInWindow = Math.Max(_maxPeakInWindow, nPeak);
                if (nPeak < 0.0001f) _zeroCallbacksInWindow++;
                else if (nPeak < 0.002f) _nearSilentCallbacksInWindow++;

                if (seq <= 10)
                {
                    _logger.LogDebug(
                        "[SystemAudio.Callback] sessionId={SessionId} seq={Seq} bytes={Bytes} rms={Rms:F6} peak={Peak:F6} zeroRatio={ZeroRatio:F4} pcmBufferBytes={PcmBytes} utc={Utc:O} thread={Thread}",
                        _sessionId, seq, e.BytesRecorded, nRms, nPeak, nZeros, _pcmBuffer.Length, now,
                        Environment.CurrentManagedThreadId);
                }

                var before = _pcmBuffer.Length;
                _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                var bytesRead = DrainConverterLocked();
                var after = _pcmBuffer.Length;
                var conversionSeq = ++_conversionSequence;

                if (conversionSeq <= 5 || (nPeak >= 0.005f && bytesRead == 0))
                {
                    _logger.LogDebug(
                        "[Audio.Convert] sessionId={SessionId} source=SystemAudio seq={Seq} nativeBytes={NativeBytes} nativeRms={NativeRms:F6} nativePeak={NativePeak:F6} bytesAddedToBuffer={Added} bytesReadFromConverter={Read} pcmBufferBytesBefore={Before} pcmBufferBytesAfter={After}",
                        _sessionId, conversionSeq, e.BytesRecorded, nRms, nPeak, e.BytesRecorded, bytesRead, before, after);
                }

                if (nPeak >= 0.005f && bytesRead == 0)
                {
                    _zeroReadStreak++;
                    if (_zeroReadStreak >= 3)
                    {
                        _logger.LogError(
                            "[Audio.InvariantViolation] sessionId={SessionId} source=SystemAudio type=signal_lost_during_conversion nativePeak={NativePeak:F6} zeroReadStreak={Streak} pcmBufferBytes={PcmBytes}",
                            _sessionId, nPeak, _zeroReadStreak, _pcmBuffer.Length);
                    }
                }
                else if (bytesRead > 0)
                {
                    _zeroReadStreak = 0;
                }

                LogHealthSummary(force: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[SystemAudio.Callback] exception sessionId={SessionId} source=SystemAudio device='{Device}'",
                _sessionId, _deviceName);
        }
    }

    private int DrainConverterLocked()
    {
        if (_disposed || _pcm16Provider is null) return 0;
        var temp = new byte[DrainBlockBytes];
        var totalRead = 0;
        int read;
        while ((read = _pcm16Provider.Read(temp, 0, temp.Length)) > 0)
        {
            if (_disposed) return totalRead;
            totalRead += read;
            _pcmBuffer.Write(temp, 0, read);
            TryEmitChunk();
        }
        return totalRead;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(e.Exception,
                "[Audio.Source.StopUnexpected] sessionId={SessionId} source=SystemAudio device='{Device}'",
                _sessionId, _deviceName);
        }
        else
        {
            _logger.LogInformation(
                "[Audio.Source.Stopped] sessionId={SessionId} source=SystemAudio device='{Device}' status={Status}",
                _sessionId, _deviceName, Status);
        }
    }

    private void TryEmitChunk()
    {
        if (_disposed) return;

        while (_pcmBuffer.Length >= _chunkBytes)
        {
            var raw = _pcmBuffer.ToArray();
            var chunkData = new byte[_chunkBytes];
            Buffer.BlockCopy(raw, 0, chunkData, 0, _chunkBytes);

            var remaining = raw.Length - _chunkBytes;
            var next = new MemoryStream(Math.Max(remaining, 0));
            if (remaining > 0) next.Write(raw, _chunkBytes, remaining);
            _pcmBuffer = next;

            EmitChunk(chunkData, _chunkDuration);
        }
    }

    private void FlushBufferLocked(bool isFinal)
    {
        if (_disposed || !_pcmBuffer.CanRead || _pcmBuffer.Length == 0) return;

        var raw = _pcmBuffer.ToArray();
        var bytesPerSec = TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8);
        var duration = TimeSpan.FromSeconds((double)raw.Length / bytesPerSec);
        if (isFinal && duration >= TimeSpan.FromSeconds(1))
            EmitChunk(raw, duration);
        _pcmBuffer = new MemoryStream();
    }

    private void EmitChunk(byte[] data, TimeSpan duration)
    {
        var convRms = AudioChunkDiagnostics.ComputeRms(data);
        var convPeak = AudioChunkDiagnostics.ComputePeak(data);
        var (_, _, convZeroRatio) = AudioChunkDiagnostics.ComputeMinMaxZero(data);
        _convertedRms = convRms;
        var classification = AudioChunkDiagnostics.ClassifyChunk(_nativePeak, convPeak);

        string? dropReason = null;
        if (data.Length == 0) dropReason = "zero_bytes";
        else if (duration <= TimeSpan.Zero) dropReason = "invalid_duration";
        else if (convZeroRatio >= 1.0f) dropReason = "all_zero_pcm";
        else if (convPeak < 0.0001f) dropReason = "dead_signal";

        if (dropReason is not null)
        {
            _droppedChunksInWindow++;
            _logger.LogDebug(
                "[Audio.ChunkDrop] sessionId={SessionId} source=SystemAudio reason={Reason} bytes={Bytes} durationMs={DurationMs:F1} rms={Rms:F6} peak={Peak:F6} zeroRatio={ZeroRatio:F4}",
                _sessionId, dropReason, data.Length, duration.TotalMilliseconds, convRms, convPeak, convZeroRatio);
            return;
        }

        var chunk = new AudioChunk
        {
            SessionId = _sessionId,
            Data = data,
            CapturedAt = DateTimeOffset.UtcNow,
            Duration = duration,
            Source = AudioSource.SystemAudio
        };

        _emittedChunksInWindow++;
        _logger.LogDebug(
            "[Audio.Chunk] sessionId={SessionId} chunkId={ChunkId} source=SystemAudio durationMs={DurationMs:F1} bytes={Bytes} rms={Rms:F6} peak={Peak:F6} zeroRatio={ZeroRatio:F4} classification={Class}",
            _sessionId, chunk.Id, duration.TotalMilliseconds, data.Length, convRms, convPeak, convZeroRatio, classification);

        try { ChunkReady?.Invoke(this, chunk); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Audio.Chunk] handler_exception sessionId={SessionId} chunkId={ChunkId} source=SystemAudio",
                _sessionId, chunk.Id);
        }
    }

    private void LogHealthSummary(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _healthWindowStartedAt;
        if (!force && elapsed < HealthLogInterval) return;

        var callbacks = _callbacksInWindow;
        var avgRms = callbacks > 0 ? _rmsSumInWindow / callbacks : 0d;
        var callbackAgeMs = _lastCallbackAt.HasValue ? (now - _lastCallbackAt.Value).TotalMilliseconds : -1;
        var healthyAgeMs = _lastHealthySignalAt.HasValue ? (now - _lastHealthySignalAt.Value).TotalMilliseconds : -1;

        _logger.LogInformation(
            "[SystemAudio.Health] sessionId={SessionId} status={Status} callbacks={Callbacks} bytes={Bytes} avgRms={AvgRms:F6} maxPeak={MaxPeak:F6} zeroCallbacks={ZeroCallbacks} nearSilentCallbacks={NearSilentCallbacks} emittedChunks={EmittedChunks} droppedChunks={DroppedChunks} lastCallbackAgeMs={CallbackAgeMs:F1} lastHealthySignalAgeMs={HealthyAgeMs:F1}",
            _sessionId, Status, callbacks, _bytesInWindow, avgRms, _maxPeakInWindow,
            _zeroCallbacksInWindow, _nearSilentCallbacksInWindow, _emittedChunksInWindow,
            _droppedChunksInWindow, callbackAgeMs, healthyAgeMs);

        _healthWindowStartedAt = now;
        _callbacksInWindow = 0;
        _bytesInWindow = 0;
        _rmsSumInWindow = 0;
        _maxPeakInWindow = 0;
        _zeroCallbacksInWindow = 0;
        _nearSilentCallbacksInWindow = 0;
        _emittedChunksInWindow = 0;
        _droppedChunksInWindow = 0;
    }

    private void ResetDiagnostics()
    {
        _callbackSequence = 0;
        _callbacksInWindow = 0;
        _bytesInWindow = 0;
        _rmsSumInWindow = 0;
        _maxPeakInWindow = 0;
        _zeroCallbacksInWindow = 0;
        _nearSilentCallbacksInWindow = 0;
        _emittedChunksInWindow = 0;
        _droppedChunksInWindow = 0;
        _healthWindowStartedAt = DateTimeOffset.UtcNow;
        _lastCallbackAt = null;
        _lastHealthySignalAt = null;
        _nativeRms = 0;
        _nativePeak = 0;
        _convertedRms = 0;
        _zeroReadStreak = 0;
        _conversionSequence = 0;
    }

    private void DisposeCaptureLocked()
    {
        _pcm16Provider = null;
        _captureBuffer = null;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        MemoryStream? pcmBufferToDispose = null;
        MMDevice? targetDeviceToDispose = null;

        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;

            try { FlushBufferLocked(isFinal: false); }
            catch (ObjectDisposedException) { }

            DisposeCaptureLocked();
            pcmBufferToDispose = _pcmBuffer;
            _pcmBuffer = new MemoryStream();
            targetDeviceToDispose = _targetDevice;
            _targetDevice = null;
            Status = AudioCaptureStatus.Idle;
        }

        pcmBufferToDispose?.Dispose();
        targetDeviceToDispose?.Dispose();
    }
}
