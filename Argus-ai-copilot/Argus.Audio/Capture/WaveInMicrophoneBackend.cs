using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Argus.Audio.Capture;

/// <summary>
/// Microphone capture using the legacy WinMM WaveIn (MME) API via NAudio's
/// <see cref="WaveInEvent"/>.  Bypasses the WASAPI shared-mode mix graph and
/// is therefore immune to exclusive-mode lock-out by other applications.
///
/// WaveInEvent delivers audio as 16-bit PCM at the device's native rate
/// (commonly 44100 or 48000 Hz, mono or stereo).  We convert to
/// 16 kHz mono PCM16 using the same managed chain as the WASAPI backend.
///
/// Device selection uses a WaveIn device index (0 = Windows default).
/// </summary>
public sealed class WaveInMicrophoneBackend : IDisposable
{
    public sealed class NativeCallbackInfo : EventArgs
    {
        public float Rms { get; init; }
        public float Peak { get; init; }
        public float ZeroRatio { get; init; }
        public int BytesRecorded { get; init; }
    }

    private sealed class StartupCallbackBufferEntry
    {
        public required byte[] Buffer { get; init; }
        public required int Sequence { get; init; }
        public required int BytesRecorded { get; init; }
        public required float NativeRms { get; init; }
        public required float NativePeak { get; init; }
    }

    private readonly ILogger _logger;

    private const int TargetSampleRate    = 16_000;
    private const int TargetChannels      = 1;
    private const int TargetBitsPerSample = 16;
    private const int DrainBlockBytes     = 512;

    private readonly int      _deviceNumber;   // 0 = default
    private readonly TimeSpan _chunkDuration;
    private readonly int      _chunkBytes;
    private readonly string   _debugFolder;
    private readonly bool     _debugEnabled;
    private readonly object   _pipelineLock = new();

    private WaveInEvent?          _waveIn;
    private BufferedWaveProvider? _captureBuffer;
    private IWaveProvider?        _pcm16Provider;
    private MemoryStream          _pcmBuffer        = new();
    private MemoryStream          _nativeDebugBuffer = new();
    private int                   _nativeDebugTarget;
    private int                   _debugNativeIndex;
    private int                   _debugConvIndex;
    private Guid                  _sessionId;
    private volatile bool         _paused;
    private volatile bool         _stopping;
    private WaveFormat?           _nativeFormat;
    private bool                  _startupDeferredLogged;
    private readonly List<StartupCallbackBufferEntry> _startupBufferedCallbacks = [];
    private bool                  _startupRawHadSignal;
    private int                   _callbackBoundarySequence;

    // ── Debug-WAV gate state (reset each session) ─────────────────────────────
    // Saves only on the first occurrence of each failure class — no periodic noise.
    private const int  DebugMaxFiles         = 10;
    private const long DebugMaxBytes         = 50L * 1024 * 1024; // 50 MB
    private bool            _dbgFirstSilentSaved;
    private bool            _dbgFirstConvFailureSaved;
    private bool            _dbgFirstAllZeroConvSaved;
    private AudioChunkClass _dbgPrevClass = AudioChunkClass.HealthyAudio;

    // ── Per-session log throttle (suppress repeated identical warnings) ────────
    private DateTimeOffset _lastSilentWarnAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastConvErrAt    = DateTimeOffset.MinValue;
    private bool           _firstSamplesLogged;
    private bool           _firstChunkLogged;

    // ── Diagnostic experiment knobs (set before Start; reset each session) ─────
    /// <summary>
    /// Multiplies every PCM16 sample by this factor before chunk emission.
    /// 1.0 = off. Try 4.0 or 8.0 when Whisper reports BLANK_AUDIO on low-level input.
    /// Pre-gain and post-gain RMS/Peak are logged on every chunk at Information level
    /// while this is != 1.0.
    /// </summary>
    public float DiagGain { get; set; } = 1f;

    /// <summary>
    /// When true, peak-normalizes the chunk to 90 % full-scale before chunk emission.
    /// Mutually exclusive with <see cref="DiagGain"/>: if both are set, normalize wins.
    /// Intended for temporary Whisper acceptance testing only.
    /// </summary>
    public bool DiagNormalize { get; set; } = false;

    // True when the direct PCM path is active (no resampler); used to suppress the
    // ConversionDestroyedAudio false-positive that is meaningless on this path.
    private bool _usingDirectPcm;

    // Continuous 5-second capture WAV written straight from OnDataAvailable callbacks
    // (pre-pipeline, pre-gain) so we can verify what the driver is actually delivering.
    private WaveFileWriter? _contWavWriter;
    private int             _contWavBytesRemaining;   // counts down to zero then closes

    // ── Diagnostics ───────────────────────────────────────────────────────────
    public float NativeRms    { get; private set; }
    public float NativePeak   { get; private set; }
    public float ConvertedRms { get; private set; }

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<AudioChunk>? ChunkReady;
    public event EventHandler<NativeCallbackInfo>? NativeCallbackObserved;

    public bool StartupValidationPending { get; set; }
    public int RequestedSampleRate { get; set; } = 44_100;

    public WaveInMicrophoneBackend(
        ILogger logger,
        int deviceNumber,
        TimeSpan chunkDuration,
        string debugFolder,
        bool debugEnabled)
    {
        _logger        = logger;
        _deviceNumber  = deviceNumber;
        _chunkDuration = chunkDuration;
        _chunkBytes    = TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8)
                       * (int)chunkDuration.TotalSeconds;
        _debugFolder   = debugFolder;
        _debugEnabled  = debugEnabled;
    }

    public string DeviceName { get; private set; } = string.Empty;

    public static WaveFormat ResolvePreferredInputFormat(ILogger logger, int deviceNumber)
    {
        logger.LogDebug(
            "[WaveInBackend.Open] Preferring 44100Hz/16bit/mono for device #{Idx} during WaveIn startup.",
            deviceNumber);
        return new WaveFormat(44100, 16, 1);
    }

    public void Start(Guid sessionId)
    {
        _sessionId = sessionId;
        _paused    = false;
        _stopping  = false;

        // Reset per-session debug gate
        _dbgFirstSilentSaved      = false;
        _dbgFirstConvFailureSaved = false;
        _dbgFirstAllZeroConvSaved = false;
        _dbgPrevClass             = AudioChunkClass.HealthyAudio;
        _lastSilentWarnAt         = DateTimeOffset.MinValue;
        _lastConvErrAt            = DateTimeOffset.MinValue;
        _firstSamplesLogged       = false;
        _firstChunkLogged         = false;
        _usingDirectPcm           = false;
        _startupDeferredLogged    = false;
        _startupRawHadSignal      = false;
        _callbackBoundarySequence = 0;
        _startupBufferedCallbacks.Clear();
        CloseContWav();   // ensure any stale writer from a prior session is closed

        var preferredFormat = ResolvePreferredInputFormat(_logger, _deviceNumber);
        var requestFormat = RequestedSampleRate == 16_000
            ? new WaveFormat(16_000, 16, 1)
            : preferredFormat;

        DeviceName    = GetDeviceName(_deviceNumber);
        _nativeFormat = requestFormat;

        _logger.LogInformation(
            "[WaveInBackend.Open] Backend=WaveIn DeviceIndex={Idx} DeviceName='{Name}' " +
            "Format={Rate}Hz/{Bits}bit/{Ch}ch SessionId={Id}",
            _deviceNumber, DeviceName,
            requestFormat.SampleRate, requestFormat.BitsPerSample, requestFormat.Channels,
            sessionId);

        _waveIn = new WaveInEvent
        {
            DeviceNumber       = _deviceNumber,
            WaveFormat         = requestFormat,
            BufferMilliseconds = 50
        };

        // ── Full WaveIn capability dump (all devices, once per session) ──────
        var totalDevices = WaveIn.DeviceCount;
        _logger.LogInformation("[WaveInBackend] WaveIn device count: {Count}", totalDevices);
        for (int di = 0; di < totalDevices; di++)
        {
            try
            {
                var c = WaveIn.GetCapabilities(di);
                _logger.LogInformation(
                    "[WaveInBackend]   WaveIn[{Idx}] '{Name}'  Channels={Ch}  " +
                    "ManufacturerGuid={Mfr}  ProductGuid={Prod}",
                    di, c.ProductName, c.Channels, c.ManufacturerGuid, c.ProductGuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WaveInBackend]   WaveIn[{Idx}] capability query failed.", di);
            }
        }

        lock (_pipelineLock)
        {
            _nativeDebugTarget = requestFormat.SampleRate
                               * requestFormat.Channels
                               * (requestFormat.BitsPerSample / 8)
                               * 3;
            _nativeDebugBuffer = new MemoryStream();
            _captureBuffer = new BufferedWaveProvider(requestFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(10)
            };

            if (requestFormat.SampleRate == TargetSampleRate &&
                requestFormat.BitsPerSample == TargetBitsPerSample &&
                requestFormat.Channels == TargetChannels)
            {
                _pcm16Provider  = _captureBuffer;
                _usingDirectPcm = true;

                _logger.LogInformation(
                    "[WaveInBackend] Direct PCM path enabled: native format already matches target {Rate}Hz/16bit/1ch",
                    TargetSampleRate);
            }
            else
            {
                ISampleProvider sp = _captureBuffer.ToSampleProvider();

                if (requestFormat.Channels > 1)
                    sp = new StereoToMonoSampleProvider(sp);

                if (requestFormat.SampleRate != TargetSampleRate)
                    sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);

                _pcm16Provider = new SampleToWaveProvider16(sp);

                _logger.LogInformation(
                    "[WaveInBackend] Managed conversion path: {Fmt} → {Rate}Hz/16bit/1ch DrainBlock={Block}B",
                    AudioChunkDiagnostics.FormatSummary(requestFormat), TargetSampleRate, DrainBlockBytes);
            }

            _pcmBuffer = new MemoryStream();
        }

        _waveIn.DataAvailable    += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        if (_debugEnabled)
        {
            try
            {
                Directory.CreateDirectory(_debugFolder);
                var contPath = Path.Combine(_debugFolder,
                    $"mic_wavein_continuous_{DateTimeOffset.UtcNow:HHmmss}.wav");
                _contWavWriter         = new WaveFileWriter(contPath, requestFormat);
                _contWavBytesRemaining = requestFormat.SampleRate
                                       * requestFormat.Channels
                                       * (requestFormat.BitsPerSample / 8)
                                       * 5;
                _logger.LogInformation(
                    "[WaveInBackend] Continuous 5s capture WAV opened: {Path}", contPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WaveInBackend] Could not open continuous WAV writer.");
            }
        }

        _logger.LogInformation("[WaveInBackend] Recording started. Device='{Device}'", DeviceName);
    }

    public void Stop()
    {
        _stopping = true;

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable    -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.StopRecording();
        }

        lock (_pipelineLock)
        {
            DrainConverterLocked();
            FlushBuffer(isFinal: true);
        }

        DisposeInternals();
        _logger.LogInformation("[WaveInBackend] Stopped. Device='{Device}'", DeviceName);
    }

    public void Pause()  { _paused = true;  }
    public void Resume() { _paused = false; }
    public void ReleaseStartupBufferedAudio()
    {
        lock (_pipelineLock)
        {
            if (_stopping || StartupValidationPending)
                return;

            FlushStartupBufferedCallbacksLocked();
        }
    }

    // ── Internal callbacks ────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stopping || _paused || e.BytesRecorded == 0) return;

        try
        {
            NativeCallbackInfo? callbackInfo = null;
            var callbackSequence = 0;
            var deferPipelineFeed = false;

            lock (_pipelineLock)
            {
                if (_stopping || _captureBuffer is null || _pcm16Provider is null) return;

                var span = e.Buffer.AsSpan(0, e.BytesRecorded);
                var rms  = AudioChunkDiagnostics.ComputeRms(span);
                var peak = AudioChunkDiagnostics.ComputePeak(span);
                var (min, max, zeroRatio) = AudioChunkDiagnostics.ComputeMinMaxZero(span);
                NativeRms  = rms;
                NativePeak = peak;

                _logger.LogDebug(
                    "[WaveInNative] Device='{D}' Bytes={B} RMS={R:F4} Peak={P:F4} " +
                    "Min={Min:F4} Max={Max:F4} Zeros={Z:P0}",
                    DeviceName, e.BytesRecorded, rms, peak, min, max, zeroRatio);

                callbackInfo = new NativeCallbackInfo
                {
                    Rms = rms,
                    Peak = peak,
                    ZeroRatio = zeroRatio,
                    BytesRecorded = e.BytesRecorded
                };
                callbackSequence = ++_callbackBoundarySequence;

                if (!_firstSamplesLogged)
                {
                    _firstSamplesLogged = true;
                    var sampleCount = Math.Min(32, span.Length / 2);
                    var sb = new System.Text.StringBuilder();
                    for (int si = 0; si < sampleCount; si++)
                    {
                        var s = (short)(span[si * 2] | (span[si * 2 + 1] << 8));
                        sb.Append($"{s} ");
                    }
                    _logger.LogInformation(
                        "[WaveInNative] First {N} samples (session start): [{Samples}]  " +
                        "Bytes={Bytes} RMS={Rms:F4}",
                        sampleCount, sb.ToString().TrimEnd(), e.BytesRecorded, rms);
                }

                var now = DateTimeOffset.UtcNow;
                if (peak < 0.0001f)
                {
                    if ((now - _lastSilentWarnAt).TotalSeconds >= 60)
                    {
                        _lastSilentWarnAt = now;
                        _logger.LogWarning(
                            "[WaveInNative] ALL-ZERO buffer — device '{D}' index={Idx} is delivering no audio. " +
                            "Possible causes: wrong device index, device not selected as Windows default, " +
                            "or exclusive mode held by another app.",
                            DeviceName, _deviceNumber);
                    }
                }
                else if (peak < 0.002f)
                {
                    if ((now - _lastSilentWarnAt).TotalSeconds >= 60)
                    {
                        _lastSilentWarnAt = now;
                        _logger.LogWarning(
                            "[WaveInNative] NEAR-SILENT buffer — peak={P:F4}. Device='{D}'",
                            peak, DeviceName);
                    }
                }

                if (_debugEnabled)
                {
                    if (_nativeDebugBuffer.Length < _nativeDebugTarget)
                        _nativeDebugBuffer.Write(e.Buffer, 0, e.BytesRecorded);

                    if (_contWavWriter is not null && _contWavBytesRemaining > 0)
                    {
                        var toWrite = Math.Min(e.BytesRecorded, _contWavBytesRemaining);
                        _contWavWriter.Write(e.Buffer, 0, toWrite);
                        _contWavBytesRemaining -= toWrite;
                        if (_contWavBytesRemaining <= 0)
                        {
                            _logger.LogInformation(
                                "[WaveInBackend] Continuous 5s WAV complete: {Path}",
                                _contWavWriter.Filename);
                            CloseContWav();
                        }
                    }
                }

                if (StartupValidationPending)
                {
                    BufferStartupCallbackLocked(e.Buffer, e.BytesRecorded, rms, peak);
                    if (!_startupDeferredLogged)
                    {
                        _startupDeferredLogged = true;
                        _logger.LogInformation(
                            "[WaveInBackend] Deferring pipeline feed because startup validation is still pending.");
                    }
                    _logger.LogInformation(
                        "[WaveInStartupCallback] deliveredToOuterValidator=true cb={N} rms={Rms:F4} peak={Peak:F4}",
                        callbackSequence,
                        rms,
                        peak);
                    deferPipelineFeed = true;
                }

                if (!deferPipelineFeed)
                {
                    FlushStartupBufferedCallbacksLocked();

                    _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    var bytesRead = DrainConverterLocked();
                    LogCallbackBoundary(
                        callbackSequence,
                        rms,
                        peak,
                        e.BytesRecorded,
                        e.BytesRecorded,
                        bytesRead,
                        _pcmBuffer.Length,
                        "live");
                }
            }

            if (callbackInfo is not null)
            {
                try   { NativeCallbackObserved?.Invoke(this, callbackInfo); }
                catch (Exception ex)
                { _logger.LogWarning(ex, "[WaveInBackend] NativeCallbackObserved handler threw."); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WaveInBackend] OnDataAvailable error. Device='{D}'", DeviceName);
        }
    }

    private int DrainConverterLocked()
    {
        if (_pcm16Provider is null) return 0;
        var temp = new byte[DrainBlockBytes];
        int read;
        var totalRead = 0;
        while ((read = _pcm16Provider.Read(temp, 0, temp.Length)) > 0)
        {
            totalRead += read;
            _pcmBuffer.Write(temp, 0, read);
            TryEmitChunk();
        }
        return totalRead;
    } 
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
        var dur         = TimeSpan.FromSeconds((double)raw.Length / bytesPerSec);
        if (isFinal && dur >= TimeSpan.FromSeconds(1))
            EmitChunk(raw, dur);
        _pcmBuffer = new MemoryStream();
    }

    private void EmitChunk(byte[] data, TimeSpan duration)
    {
        var preRms  = AudioChunkDiagnostics.ComputeRms(data);
        var prePeak = AudioChunkDiagnostics.ComputePeak(data);

        // ── Diagnostic gain / normalize (experimental — leave at defaults for normal use) ──
        if (DiagNormalize && prePeak > 0.001f)
        {
            // Peak-normalize to 90 % full-scale.
            var scale = (short.MaxValue * 0.9f) / (prePeak * short.MaxValue);
            ApplyGainInPlace(data, scale);
        }
        else if (DiagGain != 1f && DiagGain > 0f)
        {
            ApplyGainInPlace(data, DiagGain);
        }

        var convRms  = AudioChunkDiagnostics.ComputeRms(data);
        var convPeak = AudioChunkDiagnostics.ComputePeak(data);
        var (convMin, convMax, convZeroRatio) = AudioChunkDiagnostics.ComputeMinMaxZero(data);
        ConvertedRms = convRms;

        // Log pre/post whenever gain is active so the effect is always visible.
        if (DiagNormalize || DiagGain != 1f)
        {
            _logger.LogDebug(
                "[WaveInGain] Device='{D}' PreRMS={PR:F4} PrePeak={PP:F4} → " +
                "PostRMS={CR:F4} PostPeak={CP:F4}  Mode={Mode}",
                DeviceName, preRms, prePeak, convRms, convPeak,
                DiagNormalize ? "Normalize" : $"Gain×{DiagGain:F1}");
        }

        var classification = AudioChunkDiagnostics.ClassifyChunk(NativePeak, convPeak);

        if (!_firstChunkLogged)
        {
            if (_startupRawHadSignal && IsClearlyDeadChunk(convRms, convPeak))
            {
                _logger.LogError(
                    "[MicInvariant] Raw callback had signal but emitted chunk was silent. Audio lost inside Argus pipeline.");
            }

            _firstChunkLogged = true;
            _logger.LogInformation(
                "[WaveInChunk] FIRST CHUNK emitted. Device='{D}' Duration={Dur:F2}s Bytes={B} " +
                "NativeRMS={NR:F4} NativePeak={NP:F4} ConvRMS={CR:F4} ConvPeak={CP:F4} CLASS={Class}",
                DeviceName, duration.TotalSeconds, data.Length,
                NativeRms, NativePeak, convRms, convPeak, classification);
        }

        if (!_usingDirectPcm && NativePeak > 0.01f && convPeak < 0.0005f)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastConvErrAt).TotalSeconds >= 60)
            {
                _lastConvErrAt = now;
                _logger.LogError(
                    "[WaveInChunk] ConversionDestroyedAudio suspected — native peak={NP:F4}, native rms={NR:F4}, " +
                    "but converted peak={CP:F4}, converted rms={CR:F4}. " +
                    "WaveIn conversion chain is destroying real signal.",
                    NativePeak, NativeRms, convPeak, convRms);
            }
        }

        _logger.LogDebug(
            "[WaveInChunk] Device='{D}' Duration={Dur:F2}s Bytes={B} " +
            "NativeRMS={NR:F4} | " +
            "ConvRMS={CR:F4} ConvPeak={CP:F4} ConvMin={CMin:F4} ConvMax={CMax:F4} ConvZeros={CZ:P0} | " +
            "CLASS={Class}",
            DeviceName, duration.TotalSeconds, data.Length,
            NativeRms,
            convRms, convPeak, convMin, convMax, convZeroRatio,
            classification);

        if (classification == AudioChunkClass.NativeZero)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastSilentWarnAt).TotalSeconds >= 60)
            {
                _lastSilentWarnAt = now;
                _logger.LogError(
                    "[WaveInChunk] CLASS=NativeZero — device '{D}' index={Idx} delivers all-zero audio. " +
                    "Try a different WaveIn device index. Available: {Devices}",
                    DeviceName, _deviceNumber,
                    string.Join(", ", WaveInMicrophoneBackend.EnumerateDevices().Select(d => $"[{d.Index}] {d.Name}")));
            }
        }
        else if (!_usingDirectPcm && classification == AudioChunkClass.ConversionDestroyedAudio)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastConvErrAt).TotalSeconds >= 60)
            {
                _lastConvErrAt = now;
                _logger.LogError(
                    "[WaveInChunk] CLASS=ConversionDestroyedAudio — native RMS={NR:F4} but conv Peak={CP:F4}. " +
                    "The resampling chain is destroying the signal.",
                    NativeRms, convPeak);
            }
        }

        if (_debugEnabled)
        {
            DebugSaveReason? reason = null;

            bool isSilent     = classification is AudioChunkClass.NativeZero or AudioChunkClass.NativeNearSilent;
            bool isConvFail   = classification == AudioChunkClass.ConversionDestroyedAudio;
            bool isTransition = _dbgPrevClass == AudioChunkClass.HealthyAudio && isSilent;
            bool isAllZeroConv = convZeroRatio >= 1.0f;

            if (isSilent && !_dbgFirstSilentSaved)
            {
                reason = DebugSaveReason.FirstSilentChunk;
                _dbgFirstSilentSaved = true;
            }
            else if (isConvFail && !_dbgFirstConvFailureSaved)
            {
                reason = DebugSaveReason.ConversionDestroyedAudio;
                _dbgFirstConvFailureSaved = true;
            }
            else if (isTransition)
            {
                reason = DebugSaveReason.StateTransition;
            }
            else if (isAllZeroConv && !_dbgFirstAllZeroConvSaved)
            {
                reason = DebugSaveReason.FirstSilentChunk;   // reuse — ConvZeros=100%
                _dbgFirstAllZeroConvSaved = true;
            }

            _dbgPrevClass = classification;

            if (reason.HasValue)
            {
                try
                {
                    Directory.CreateDirectory(_debugFolder);
                    var idx     = System.Threading.Interlocked.Increment(ref _debugConvIndex);
                    var stamp   = DateTimeOffset.UtcNow;
                    var convFmt = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);

                    // Converted (Whisper-ready) WAV
                    var convPath = Path.Combine(_debugFolder,
                        $"mic_wavein_conv_{idx:D4}_{stamp:HHmmss}_{convRms:F3}.wav");
                    using (var w = new WaveFileWriter(convPath, convFmt))
                        w.Write(data, 0, data.Length);

                    // Native WAV snapshot (whatever is in the rolling buffer right now)
                    string? nativePath = null;
                    if (_nativeFormat is not null && _nativeDebugBuffer.Length > 0)
                    {
                        var nativeIdx  = System.Threading.Interlocked.Increment(ref _debugNativeIndex);
                        nativePath     = Path.Combine(_debugFolder,
                            $"mic_wavein_native_{nativeIdx:D4}_{stamp:HHmmss}.wav");
                        var nativeSnap    = _nativeDebugBuffer.ToArray();
                        var nativeBytes   = nativeSnap.Length;
                        var bytesPerFrame = (_nativeFormat.BitsPerSample / 8) * _nativeFormat.Channels;
                        var nativeFrames  = nativeBytes / bytesPerFrame;
                        var nativeDurSec  = (double)nativeFrames / _nativeFormat.SampleRate;
                        using var wn = new WaveFileWriter(nativePath, _nativeFormat);
                        wn.Write(nativeSnap, 0, nativeBytes);
                        // Reset so next event gets a fresh window (cap resets on next OnDataAvailable)
                        _nativeDebugBuffer = new MemoryStream();

                        _logger.LogInformation(
                            "[WaveInDebug] NativeSnap: {Bytes}B  {Frames} frames  {Dur:F3}s  " +
                            "@ {Rate}Hz/{Bits}bit/{Ch}ch",
                            nativeBytes, nativeFrames, nativeDurSec,
                            _nativeFormat.SampleRate, _nativeFormat.BitsPerSample, _nativeFormat.Channels);

                        if (nativeDurSec < 0.5)
                            _logger.LogWarning(
                                "[WaveInDebug] SHORT native snapshot: {Dur:F3}s (expected ≥ {Expected:F1}s). " +
                                "Buffer had only {Bytes}B when save triggered.",
                                nativeDurSec, _chunkDuration.TotalSeconds, nativeBytes);
                    }

                    _logger.LogInformation(
                        "[WaveInDebug] Saved debug WAV. Reason={Reason} CLASS={Class} " +
                        "NativeRMS={NR:F4} ConvRMS={CR:F4} ConvZeros={CZ:P0} " +
                        "Conv={ConvPath} Native={NativePath}",
                        reason.Value, classification,
                        NativeRms, convRms, convZeroRatio,
                        convPath, nativePath ?? "(none)");

                    AudioChunkDiagnostics.RotateDebugFolder(_debugFolder, DebugMaxFiles, DebugMaxBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WaveInDebug] Failed to write debug WAV.");
                }
            }
        }

        var chunk = new AudioChunk
        {
            SessionId  = _sessionId,
            Data       = data,
            CapturedAt = DateTimeOffset.UtcNow,
            Duration   = duration,
            Source     = AudioSource.Microphone
        };
        try   { ChunkReady?.Invoke(this, chunk); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "[WaveInBackend] ChunkReady handler threw."); }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception,
                "[WaveInBackend] Recording stopped with error. Device='{D}'", DeviceName);
        else
            _logger.LogInformation(
                "[WaveInBackend] Recording stopped cleanly. Device='{D}'", DeviceName);
    }

    private void DisposeInternals()
    {
        CloseContWav();
        _pcm16Provider = null;
        _captureBuffer = null;
        _startupBufferedCallbacks.Clear();

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable    -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    public void Dispose()
    {
        DisposeInternals();
        _pcmBuffer.Dispose();
        _nativeDebugBuffer.Dispose();
    }

    private void CloseContWav()
    {
        if (_contWavWriter is null) return;
        try   { _contWavWriter.Dispose(); }
        catch { /* best-effort */ }
        _contWavWriter         = null;
        _contWavBytesRemaining = 0;
    }

    /// <summary>
    /// Multiplies every PCM16 sample in <paramref name="data"/> by <paramref name="gain"/>
    /// in-place, clamping to the Int16 range.
    /// </summary>
    private static void ApplyGainInPlace(byte[] data, float gain)
    {
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            var sample  = (short)(data[i] | (data[i + 1] << 8));
            var boosted = Math.Clamp((int)MathF.Round(sample * gain), short.MinValue, short.MaxValue);
            var result  = (short)boosted;
            data[i]     = (byte)(result & 0xFF);
            data[i + 1] = (byte)((result >> 8) & 0xFF);
        }
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private void BufferStartupCallbackLocked(byte[] buffer, int bytesRecorded, float nativeRms, float nativePeak)
    {
        var copy = new byte[bytesRecorded];
        Buffer.BlockCopy(buffer, 0, copy, 0, bytesRecorded);

        var sequence = ++_callbackBoundarySequence;
        _startupBufferedCallbacks.Add(new StartupCallbackBufferEntry
        {
            Buffer = copy,
            Sequence = sequence,
            BytesRecorded = bytesRecorded,
            NativeRms = nativeRms,
            NativePeak = nativePeak
        });

        _startupRawHadSignal |= !IsClearlyDeadChunk(nativeRms, nativePeak);

        LogCallbackBoundary(
            sequence,
            nativeRms,
            nativePeak,
            bytesRecorded,
            0,
            0,
            _pcmBuffer.Length,
            "startup_buffered");
    }

    private void FlushStartupBufferedCallbacksLocked()
    {
        if (_captureBuffer is null || _pcm16Provider is null || _startupBufferedCallbacks.Count == 0)
            return;

        foreach (var callback in _startupBufferedCallbacks)
        {
            _captureBuffer.AddSamples(callback.Buffer, 0, callback.BytesRecorded);
            var bytesRead = DrainConverterLocked();
            LogCallbackBoundary(
                callback.Sequence,
                callback.NativeRms,
                callback.NativePeak,
                callback.BytesRecorded,
                callback.BytesRecorded,
                bytesRead,
                _pcmBuffer.Length,
                "startup_flush");
        }

        _startupBufferedCallbacks.Clear();
    }

    private void LogCallbackBoundary(
        int callbackNumber,
        float nativeRms,
        float nativePeak,
        int bytesRecorded,
        int bytesHandedToCaptureBuffer,
        int bytesReadFromConversionProvider,
        long chunkBufferBytes,
        string phase)
    {
        if (callbackNumber > 5)
            return;

        _logger.LogInformation(
            "[MicCallbackBoundary] backend=WaveIn cb={Callback} phase={Phase} nativeRms={NativeRms:F4} nativePeak={NativePeak:F4} bytesRecorded={BytesRecorded} bytesHanded={BytesHanded} bytesRead={BytesRead} chunkBufferBytes={ChunkBufferBytes}",
            callbackNumber,
            phase,
            nativeRms,
            nativePeak,
            bytesRecorded,
            bytesHandedToCaptureBuffer,
            bytesReadFromConversionProvider,
            chunkBufferBytes);
    }

    private static bool IsClearlyDeadChunk(float rms, float peak)
        => rms < 0.0015f && peak < 0.005f;

    private static string GetDeviceName(int deviceNumber)
    {
        try
        {
            var caps = WaveIn.GetCapabilities(deviceNumber);
            return caps.ProductName;
        }
        catch
        {
            return deviceNumber == 0 ? "Default WaveIn" : $"WaveIn #{deviceNumber}";
        }
    }

    /// <summary>
    /// Returns the number of available WaveIn devices.
    /// </summary>
    public static int DeviceCount => WaveIn.DeviceCount;

    /// <summary>
    /// Returns friendly names of all available WaveIn input devices.
    /// </summary>
    public static IReadOnlyList<(int Index, string Name)> EnumerateDevices()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            try   { list.Add((i, WaveIn.GetCapabilities(i).ProductName)); }
            catch { list.Add((i, $"WaveIn device #{i}")); }
        }
        return list;
    }
}
