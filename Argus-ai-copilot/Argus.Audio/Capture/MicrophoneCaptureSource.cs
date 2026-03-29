using Argus.Audio.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Argus.Audio.Capture;

/// <summary>
/// Captures microphone audio and converts it to 16 kHz mono 16-bit PCM for Whisper.
///
/// Supports two backend implementations selected via <see cref="SelectedBackend"/>:
///
///   WASAPI (default): WasapiCapture → managed chain (ToSampleProvider →
///     StereoToMonoSampleProvider → WdlResamplingSampleProvider → SampleToWaveProvider16)
///
///   WaveIn (fallback): WaveInEvent requesting PCM16 44100Hz → same managed chain.
///     Bypasses the WASAPI shared-mode mix graph — immune to exclusive-mode lock-out
///     by Teams, Discord, etc.
///
///   Auto: probes both backends for 1.5 s each at session start and selects the one
///     that delivers a non-zero signal. Falls back to WaveIn if both are silent.
///
/// IMPORTANT — drain loop block size:
///   WdlResamplingSampleProvider returns 0 if it cannot fill the entire requested
///   buffer. We use DrainBlockBytes = 512 (256 PCM16 samples @ 16 kHz) to ensure
///   the resampler can always satisfy the request from a single callback.
///
/// Debug artifacts (written when DebugAudioEnabled = true):
///   %LocalAppData%\ArgusAI\debug\audio\mic_native_NNNN_*.wav  — raw WASAPI bytes
///   %LocalAppData%\ArgusAI\debug\audio\mic_conv_NNNN_*.wav    — converted PCM16 (WASAPI path)
///   %LocalAppData%\ArgusAI\debug\audio\mic_wavein_*           — WaveIn path artifacts
/// </summary>
public sealed class MicrophoneCaptureSource : IAudioCaptureSource, IDisposable
{
    private readonly ILogger<MicrophoneCaptureSource> _logger;

    // Output: mono 16 kHz 16-bit PCM — Whisper's native format
    private const int TargetSampleRate    = 16_000;
    private const int TargetChannels      = 1;
    private const int TargetBitsPerSample = 16;

    // Must be small so WdlResamplingSampleProvider can always fill the request
    // from a single WASAPI callback.  256 output PCM16 samples = 512 bytes.
    private const int DrainBlockBytes = 512;
    private const int StartupValidationCallbackCount = 5;
    private const int WasapiStartupValidationCallbackCount = 7;
    private const int WasapiStartupIgnoredInitialDeadCallbacks = 2;
    private const int StartupRetryCount = 2;
    private static readonly TimeSpan StartupValidationTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan WasapiStartupValidationTimeout = TimeSpan.FromSeconds(2.25);
    private static readonly TimeSpan WasapiStartupRetryValidationTimeout = TimeSpan.FromSeconds(3.0);
    private const float DeadChunkRmsThreshold = 0.0015f;
    private const float DeadChunkPeakThreshold = 0.005f;

    private readonly record struct StartupValidationDecision(
        bool Healthy,
        string Reason,
        bool Retryable,
        int ObservedCallbacks,
        int EffectiveCallbacks,
        int EffectiveDeadCallbacks,
        int IgnoredWarmupDeadCallbacks,
        float EffectiveAverageRms,
        float EffectiveAveragePeak);

    private sealed class StartupCallbackBufferEntry
    {
        public required byte[] Buffer { get; init; }
        public required int Sequence { get; init; }
        public required int BytesRecorded { get; init; }
        public required float NativeRms { get; init; }
        public required float NativePeak { get; init; }
    }

    // Debug artifact control
    public static bool DebugAudioEnabled = true;
    private readonly string _debugFolder;
    private int _debugNativeIndex;
    private int _debugConvIndex;
    // Accumulate up to 3 s of native bytes for the next debug WAV write
    private MemoryStream _nativeDebugBuffer = new();
    private WaveFormat?  _nativeFormat;
    private int          _nativeDebugTargetBytes;   // filled after StartAsync

    // ── Debug-WAV gate state (reset each session) ─────────────────────────────
    private const int  DebugMaxFiles         = 10;
    private const long DebugMaxBytes         = 50L * 1024 * 1024; // 50 MB
    private bool            _dbgFirstSilentSaved;
    private bool            _dbgFirstConvFailureSaved;
    private bool            _dbgFirstAllZeroConvSaved;
    private AudioChunkClass _dbgPrevClass = AudioChunkClass.HealthyAudio;

    // ── Per-session log throttle (suppress repeated identical warnings) ────────
    private DateTimeOffset _lastSilentWarnAt   = DateTimeOffset.MinValue;
    private DateTimeOffset _lastConvErrAt       = DateTimeOffset.MinValue;
    private bool           _firstSamplesLogged;
    private bool           _firstChunkLogged;

    private readonly TimeSpan _chunkDuration;
    private readonly int      _chunkBytes;

    // ── Backend selection ─────────────────────────────────────────────────────

    /// <summary>
    /// Which backend to use. Set before <see cref="StartAsync"/>.
    /// Default is <see cref="MicBackend.Wasapi"/>.
    /// </summary>
    public MicBackend SelectedBackend { get; set; } = MicBackend.Wasapi;

    /// <summary>
    /// WaveIn device number (0 = Windows default). Used when backend is WaveIn or Auto.
    /// </summary>
    public int WaveInDeviceNumber { get; set; } = 0;

    /// <summary>
    /// The backend that is actually running after <see cref="StartAsync"/> completes.
    /// </summary>
    public MicBackend ActiveBackend { get; private set; } = MicBackend.Wasapi;

    // ── WASAPI continuous raw-capture WAV ──────────────────────────────────────
    // When true, records the first 5 s of raw WASAPI bytes to a WAV file so the
    // source quality (headset DSP, telephony processing) can be evaluated directly.
    // File: %LocalAppData%\ArgusAI\debug\audio\mic_wasapi_cont_*.wav
    public bool WasapiContCapture { get; set; } = false;

    private WaveFileWriter? _contWavWriter;
    private DateTimeOffset  _contWavStartedAt;
    private bool            _contWavClosed;
    private const double    ContWavDurationSeconds = 5.0;

    // ── Per-callback level progression log (first 5 s, then suppressed) ──────
    private DateTimeOffset _levelLogWindowEnd;   // set on first callback each session
    private bool           _levelLogWindowActive;
    private int            _levelLogCallbackCount;

    // ── WaveIn diagnostic experiment knobs ────────────────────────────────────
    // Applied inside WaveInMicrophoneBackend before chunk emission.
    // Set before StartAsync; default values leave the signal unchanged.

    /// <summary>
    /// PCM16 gain multiplier applied before chunk emission (WaveIn path only).
    /// 1.0 = off. 4.0 or 8.0 boosts a low-level mic signal for Whisper acceptance testing.
    /// Ignored when <see cref="WaveInDiagNormalize"/> is true.
    /// </summary>
    public float WaveInDiagGain { get; set; } = 1f;

    /// <summary>
    /// When true, peak-normalizes each chunk to 90 % full-scale before emission.
    /// Overrides <see cref="WaveInDiagGain"/>. For temporary Whisper acceptance testing only.
    /// </summary>
    public bool WaveInDiagNormalize { get; set; } = false;

    // ── WASAPI path ───────────────────────────────────────────────────────────

    // Device ID string set by SetDevice(); resolved to a fresh MMDevice inside StartAsync.
    // We intentionally do NOT hold a long-lived MMDevice reference — COM RCWs obtained
    // from a previous WASAPI session become stale and cause InvalidCastException when
    // passed to WasapiCapture.
    private string? _targetDeviceId;

    private WasapiCapture?        _capture;
    private BufferedWaveProvider? _captureBuffer;
    // Managed conversion chain — no MediaFoundation
    private IWaveProvider?        _pcm16Provider;
    private MemoryStream          _pcmBuffer = new();
    private Guid                  _sessionId;
    private volatile bool         _paused;
    private volatile bool         _stopping;   // set true in StopAsync; gates OnDataAvailable
    private readonly object       _captureLock = new();  // serialises buffer writes vs stop/drain
    private string?               _deviceName;

    // ── WaveIn path ───────────────────────────────────────────────────────────

    private WaveInMicrophoneBackend? _waveInBackend;
    private int _waveInRequestedSampleRate = 44_100;
    private int _startupCallbackCount;
    private int _startupDeadCallbackCount;
    private float _startupCallbackRmsTotal;
    private float _startupCallbackPeakTotal;
    private long _startupBytesReceived;
    private volatile bool _startupValidationPending;
    private TaskCompletionSource<StartupValidationDecision>? _startupValidationTcs;
    private readonly List<StartupCallbackBufferEntry> _startupBufferedCallbacks = [];
    private readonly List<(float Rms, float Peak)> _startupSignalSamples = [];
    private int _startupValidationAttempt;
    private bool _startupRawHadSignal;
    private int _callbackBoundarySequence;
    private bool _startupUnexpectedRecordingStopped;
    private Exception? _startupFailureException;
    private bool _useWasapiFastPath;
    private bool _wasapiFastPathFirstCallbackLogged;
    private bool _wasapiFastPathFirstChunkLogged;

    // ── Live signal levels (updated every chunk, read by the UI) ─────────────

    /// <summary>RMS of the most-recently-delivered native (pre-conversion) buffer. [0–1]</summary>
    public float NativeRms
        => _waveInBackend is not null ? _waveInBackend.NativeRms : _nativeRmsWasapi;
    private float _nativeRmsWasapi;

    /// <summary>Peak of the most-recently-delivered native (pre-conversion) buffer. [0–1]</summary>
    public float NativePeak
        => _waveInBackend is not null ? _waveInBackend.NativePeak : _nativePeakWasapi;
    private float _nativePeakWasapi;

    /// <summary>RMS of the most-recently-emitted converted PCM16 chunk. [0–1]</summary>
    public float ConvertedRms
        => _waveInBackend is not null ? _waveInBackend.ConvertedRms : _convertedRmsWasapi;
    private float _convertedRmsWasapi;

    // ── IAudioCaptureSource ───────────────────────────────────────────────────

    public string DisplayName => _deviceName ?? "Microphone";
    public AudioCaptureStatus Status { get; private set; } = AudioCaptureStatus.Idle;
    public event EventHandler<AudioChunk>? ChunkReady;

    public MicrophoneCaptureSource(ILogger<MicrophoneCaptureSource> logger, TimeSpan? chunkDuration = null)
    {
        _logger        = logger;
        _chunkDuration = chunkDuration ?? TimeSpan.FromSeconds(2);
        _chunkBytes    = TargetSampleRate
                       * TargetChannels
                       * (TargetBitsPerSample / 8)
                       * (int)_chunkDuration.TotalSeconds;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _debugFolder = Path.Combine(appData, "ArgusAI", "debug", "audio");
        if (DebugAudioEnabled)
            Directory.CreateDirectory(_debugFolder);
    }

    /// <summary>
    /// Targets a specific WASAPI device. Must be called before <see cref="StartAsync"/>.
    /// Stores only the device ID; a fresh <see cref="MMDevice"/> handle is opened
    /// inside <see cref="StartAsync"/> to avoid stale COM object errors.
    /// If not called, the Windows default microphone is used.
    /// </summary>
    public void SetDevice(MMDevice device)
    {
        _targetDeviceId = device.ID;
        _deviceName     = device.FriendlyName;
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (Status == AudioCaptureStatus.Capturing)
        {
            _logger.LogWarning("MicrophoneCaptureSource.StartAsync called while already capturing.");
            return;
        }

        _sessionId = sessionId;
        _paused    = false;

        // Reset per-session debug gate
        _dbgFirstSilentSaved      = false;
        _dbgFirstConvFailureSaved = false;
        _dbgFirstAllZeroConvSaved = false;
        _dbgPrevClass             = AudioChunkClass.HealthyAudio;
        _lastSilentWarnAt         = DateTimeOffset.MinValue;
        _lastConvErrAt            = DateTimeOffset.MinValue;
        _firstSamplesLogged       = false;
        _firstChunkLogged         = false;
        ResetStartupValidationState();
        _useWasapiFastPath = false;
        _wasapiFastPathFirstCallbackLogged = false;
        _wasapiFastPathFirstChunkLogged = false;

        // Reset continuous WAV state for new session
        CloseContWav();
        _contWavClosed      = false;
        _levelLogWindowActive = false;
        _levelLogCallbackCount = 0;
        _stopping = false;

        // ── Backend selection ─────────────────────────────────────────────────
        var resolvedBackend = SelectedBackend;
        if (resolvedBackend == MicBackend.Auto)
        {
            _logger.LogInformation(
                "[MicSource.Start] Backend=Auto — probing WASAPI and WaveIn for 1.5s each...");
            var prober = new MicBackendProber(_logger);
            var (wasapiResult, waveInResult) = await prober.ProbeAsync(_targetDeviceId, WaveInDeviceNumber, ct);
            _logger.LogInformation(
                "[MicSource.Start] Auto probe complete. WASAPI: {W}  WaveIn: {V}",
                wasapiResult, waveInResult);

            resolvedBackend = ChooseBackend(wasapiResult, waveInResult);

            _logger.LogInformation(
                "[MicBackendDecision] requested=Auto chosen={Chosen} " +
                "reason=score wasapi={WasapiScore:F2}(avgRms={WasapiRms:F4},avgPeak={WasapiPeak:F4},callbacks={WasapiCallbacks},allZero={WasapiAllZero},firstZero={WasapiFirstZero}) " +
                "waveIn={WaveInScore:F2}(avgRms={WaveInRms:F4},avgPeak={WaveInPeak:F4},callbacks={WaveInCallbacks},allZero={WaveInAllZero},firstZero={WaveInFirstZero})",
                BackendName(resolvedBackend),
                wasapiResult.HealthScore, wasapiResult.AverageRms, wasapiResult.AveragePeak, wasapiResult.CallbackCount, wasapiResult.AllZeroCallbacks, wasapiResult.FirstCallbackAllZero,
                waveInResult.HealthScore, waveInResult.AverageRms, waveInResult.AveragePeak, waveInResult.CallbackCount, waveInResult.AllZeroCallbacks, waveInResult.FirstCallbackAllZero);

            // Give the driver time to fully release the WASAPI endpoint that the
            // prober just closed.  Without this gap the real WasapiCapture may open
            // while the driver is still tearing down the previous session, causing
            // the first N callbacks to contain zeros.
            if (resolvedBackend == MicBackend.Wasapi)
            {
                _logger.LogInformation(
                    "[MicSource.Start] Waiting 300 ms for driver endpoint teardown before real WASAPI open.");
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // ── Explicit-backend contract enforcement ─────────────────────────────
        // Only Auto is permitted to resolve to a backend other than what was
        // requested.  If a caller set SelectedBackend to WaveIn or Wasapi and
        // the code somehow arrived at a different value, that is a configuration
        // bug — fail loudly rather than silently using the wrong backend.
        if (SelectedBackend != MicBackend.Auto && resolvedBackend != SelectedBackend)
        {
            var msg = $"[MicSource.Start] Backend mismatch: requested={SelectedBackend} " +
                      $"but resolved={resolvedBackend}. " +
                      "Only Auto may resolve to a different backend. This is a configuration bug.";
            _logger.LogError(msg);
            Status = AudioCaptureStatus.DeviceError;
            throw new InvalidOperationException(msg);
        }

        if (SelectedBackend != MicBackend.Auto)
        {
            _logger.LogInformation(
                "[MicBackendDecision] requested={Requested} chosen={Chosen} " +
                "Reason={Reason}",
                BackendName(SelectedBackend),
                BackendName(resolvedBackend),
                $"explicit_{BackendName(SelectedBackend)}");
        }

        await StartWithStartupValidationAsync(resolvedBackend, sessionId, ct);
    }

    private Task StartWaveInAsync(Guid sessionId)
    {
        try
        {
            _stopping = false;
            _waveInBackend = new WaveInMicrophoneBackend(
                _logger,
                WaveInDeviceNumber,
                _chunkDuration,
                _debugFolder,
                DebugAudioEnabled);

            _waveInBackend.RequestedSampleRate = _waveInRequestedSampleRate;
            _waveInBackend.DiagGain      = WaveInDiagGain;
            _waveInBackend.DiagNormalize = WaveInDiagNormalize;
            _waveInBackend.StartupValidationPending = _startupValidationPending;
            _waveInBackend.NativeCallbackObserved += OnWaveInNativeCallbackObserved;
            _waveInBackend.ChunkReady += OnWaveInChunkReady;
            _waveInBackend.Start(sessionId);
            _deviceName = _waveInBackend.DeviceName;

            Status = AudioCaptureStatus.Capturing;
            _logger.LogInformation(
                "[MicrophoneSource.StartAsync] Capture started. Backend=WaveIn Device='{Device}' ChunkDuration={Dur}s SessionId={Id}",
                _deviceName, _chunkDuration.TotalSeconds, sessionId);
        }
        catch (Exception ex)
        {
            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(ex,
                "[MicrophoneSource.StartAsync] WaveIn failed to start. Device #{Num}",
                WaveInDeviceNumber);
            _waveInBackend?.NativeCallbackObserved -= OnWaveInNativeCallbackObserved;
            _waveInBackend?.ChunkReady -= OnWaveInChunkReady;
            _waveInBackend?.Dispose();
            _waveInBackend = null;
            throw;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is AudioCaptureStatus.Idle or AudioCaptureStatus.NoDevice)
            return;

        _logger.LogInformation(
            "[MicSource.Stop] Stopping. Backend={Backend} Device='{Device}' Status={Status}",
            ActiveBackend, _deviceName, Status);

        await StopCurrentBackendAsync();

        _logger.LogInformation("[MicSource.Stop] Stopped. Backend={Backend}", ActiveBackend);
    }

    public void Pause()
    {
        if (Status == AudioCaptureStatus.Capturing)
        {
            _paused = true;
            Status  = AudioCaptureStatus.Paused;
            _waveInBackend?.Pause();
            _logger.LogDebug("MicrophoneCaptureSource: paused. Backend={Backend}", ActiveBackend);
        }
    }

    public void Resume()
    {
        if (Status == AudioCaptureStatus.Paused)
        {
            _paused = false;
            Status  = AudioCaptureStatus.Capturing;
            _waveInBackend?.Resume();
            _logger.LogDebug("MicrophoneCaptureSource: resumed. Backend={Backend}", ActiveBackend);
        }
    }

    // ── NAudio callbacks ──────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stopping)
        {
            _logger.LogDebug("[MicSource.Stop] dropping late callback because stop is in progress");
            return;
        }
        if (_paused || e.BytesRecorded == 0) return;
        if (!_useWasapiFastPath && (_captureBuffer is null || _pcm16Provider is null)) return;

        lock (_captureLock)
        {
        // Re-check after acquiring the lock — StopAsync may have just set _stopping.
        if (_stopping) return;

        try
        {
            // ── Raw signal diagnostic on native bytes ─────────────────────────
            var rawSpan    = e.Buffer.AsSpan(0, e.BytesRecorded);
            // NOTE: WASAPI often returns WaveFormatExtensible whose Encoding == Extensible,
            // not IeeeFloat, even when the sub-format IS IEEE float.  Use the shared
            // helper that checks both the plain Encoding and the ExtensibleSubFormat.
            var isFloat    = _nativeFormat is not null && WasapiIsolationTest.IsIeeeFloat(_nativeFormat);
            var nativeRms  = isFloat
                ? AudioChunkDiagnostics.ComputeRmsFloat32(rawSpan)
                : AudioChunkDiagnostics.ComputeRms(rawSpan);
            var nativePeak = isFloat
                ? AudioChunkDiagnostics.ComputePeakFloat32(rawSpan)
                : AudioChunkDiagnostics.ComputePeak(rawSpan);
            var (nativeMin, nativeMax, nativeZeroRatio) = isFloat
                ? AudioChunkDiagnostics.ComputeMinMaxZeroFloat32(rawSpan)
                : AudioChunkDiagnostics.ComputeMinMaxZero(rawSpan);

            _nativeRmsWasapi  = nativeRms;
            _nativePeakWasapi = nativePeak;

            _logger.LogDebug(
                "[MicNative/WASAPI] Device='{Dev}' Bytes={Bytes} RMS={Rms:F4} Peak={Peak:F4} " +
                "Min={Min:F4} Max={Max:F4} Zeros={Zeros:P0}",
                _deviceName, e.BytesRecorded, nativeRms, nativePeak, nativeMin, nativeMax, nativeZeroRatio);

            if (_useWasapiFastPath && !_wasapiFastPathFirstCallbackLogged)
            {
                _wasapiFastPathFirstCallbackLogged = true;
                _logger.LogInformation(
                    "[WasapiFastPath] firstCallback rms={Rms:F4} peak={Peak:F4}",
                    nativeRms,
                    nativePeak);
            }

            // ── One-time first-samples log (once per session, not every chunk) ─
            if (!_firstSamplesLogged)
            {
                _firstSamplesLogged = true;
                var sampleCount = Math.Min(32, rawSpan.Length / (isFloat ? 4 : 2));
                var sb = new System.Text.StringBuilder();
                for (int si = 0; si < sampleCount; si++)
                {
                    if (isFloat)
                    {
                        var f = BitConverter.ToSingle(rawSpan[(si * 4)..]);
                        sb.Append($"{f:F4} ");
                    }
                    else
                    {
                        var s = (short)(rawSpan[si * 2] | (rawSpan[si * 2 + 1] << 8));
                        sb.Append($"{s} ");
                    }
                }
                _logger.LogDebug(
                    "[MicNative/WASAPI] First {N} samples (session start): [{Samples}]  " +
                    "Format={IsFloat} Bytes={Bytes} RMS={Rms:F4}",
                    sampleCount, sb.ToString().TrimEnd(),
                    isFloat ? "IeeeFloat32" : "PCM16",
                    e.BytesRecorded, nativeRms);
            }

            // ── Per-callback level progression log (active for first 5 s) ─────
            var now = DateTimeOffset.UtcNow;
            if (!_levelLogWindowActive)
            {
                _levelLogWindowActive = true;
                _levelLogWindowEnd    = now.AddSeconds(5);
                _logger.LogDebug(
                    "[MicLevel/WASAPI] ─── Starting 5-second level-progression window ─────────────────────────────");
            }
            if (now <= _levelLogWindowEnd)
            {
                _levelLogCallbackCount++;
                _logger.LogDebug(
                    "[MicLevel/WASAPI] cb={N,4}  RMS={Rms:F4}  Peak={Peak:F4}  Zeros={Zeros:P0}  Bytes={Bytes}",
                    _levelLogCallbackCount, nativeRms, nativePeak, nativeZeroRatio, e.BytesRecorded);
            }
            else if (_levelLogCallbackCount > 0)
            {
                // Log once when the window closes
                _logger.LogDebug(
                    "[MicLevel/WASAPI] ─── 5-second level window closed after {N} callbacks ─────────────────────",
                    _levelLogCallbackCount);
                _levelLogCallbackCount = 0;   // zero = window closed, suppresses further messages
            }

            // ── Throttled warnings — at most once per minute ──────────────────
            if (nativePeak < 0.0001f)
            {
                if ((now - _lastSilentWarnAt).TotalSeconds >= 60)
                {
                    _lastSilentWarnAt = now;
                    _logger.LogWarning(
                        "[MicNative/WASAPI] ALL-ZERO buffer — device '{Dev}' may be in exclusive mode " +
                        "or wrong endpoint. Consider switching to WaveIn backend.",
                        _deviceName);
                }
            }
            else if (nativePeak < 0.002f)
            {
                if ((now - _lastSilentWarnAt).TotalSeconds >= 60)
                {
                    _lastSilentWarnAt = now;
                    _logger.LogWarning(
                        "[MicNative/WASAPI] NEAR-SILENT buffer — peak={Peak:F4}. Device='{Dev}'",
                        nativePeak, _deviceName);
                }
            }

            // ── Accumulate native bytes for debug pre-conversion WAV ──────────
            // Cap: only accumulate up to the target; reset happens in EmitChunk
            // after save.  Do NOT reset-then-write here — EmitChunk runs
            // synchronously in this call stack and would see only one callback.
            if (DebugAudioEnabled && _nativeFormat is not null)
            {
                if (_nativeDebugBuffer.Length < _nativeDebugTargetBytes)
                    _nativeDebugBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            }

            // ── Continuous raw WASAPI WAV (first ContWavDurationSeconds of session) ─
            if (WasapiContCapture && !_contWavClosed && _nativeFormat is not null)
            {
                if (_contWavWriter is null)
                {
                    Directory.CreateDirectory(_debugFolder);
                    var stamp   = DateTimeOffset.UtcNow;
                    var path    = Path.Combine(_debugFolder,
                        $"mic_wasapi_cont_{stamp:HHmmss}.wav");
                    _contWavWriter  = new WaveFileWriter(path, _nativeFormat);
                    _contWavStartedAt = stamp;
                    _logger.LogInformation(
                        "[MicContWav] Opened continuous WAV → '{Path}' ({Dur}s)",
                        path, ContWavDurationSeconds);
                }

                _contWavWriter.Write(e.Buffer, 0, e.BytesRecorded);

                if ((now - _contWavStartedAt).TotalSeconds >= ContWavDurationSeconds)
                    CloseContWav();
            }

            if (_startupValidationPending)
            {
                BufferStartupCallbackLocked(e.Buffer, e.BytesRecorded, nativeRms, nativePeak);
                ObserveStartupCallback(nativeRms, nativePeak, e.BytesRecorded);

                if (!_startupValidationPending)
                    FlushStartupBufferedCallbacksLocked();

                return;
            }

            int bytesRead;
            int bytesHanded;
            if (_useWasapiFastPath)
            {
                bytesHanded = e.BytesRecorded;
                bytesRead = AppendWasapiFastPathPcm16Locked(e.Buffer, e.BytesRecorded);
            }
            else
            {
                _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                bytesHanded = e.BytesRecorded;
                bytesRead = DrainConverterLocked();
            }

            LogCallbackBoundary(
                "WASAPI",
                ++_callbackBoundarySequence,
                nativeRms,
                nativePeak,
                e.BytesRecorded,
                bytesHanded,
                bytesRead,
                _pcmBuffer.Length,
                "live");
        }
        catch (Exception ex)
        {
            if (_startupValidationPending)
            {
                _startupFailureException = ex;
                _startupValidationTcs?.TrySetResult(new StartupValidationDecision(
                    Healthy: false,
                    Reason: "startup_exception",
                    Retryable: false,
                    ObservedCallbacks: _startupCallbackCount,
                    EffectiveCallbacks: _startupCallbackCount,
                    EffectiveDeadCallbacks: _startupDeadCallbackCount,
                    IgnoredWarmupDeadCallbacks: 0,
                    EffectiveAverageRms: _startupCallbackCount > 0 ? _startupCallbackRmsTotal / _startupCallbackCount : 0f,
                    EffectiveAveragePeak: _startupCallbackCount > 0 ? _startupCallbackPeakTotal / _startupCallbackCount : 0f));
            }
            _logger.LogError(ex, "[MicSource.OnDataAvailable] Exception. Device='{Device}'", _deviceName);
        }
        } // lock (_captureLock)
    }

    private int DrainConverterLocked()
    {
        if (_pcm16Provider is null) return 0;

        // Use small fixed-size blocks so WdlResamplingSampleProvider can always
        // satisfy the request from the current callback's buffered input.
        // A large block (e.g. 4096) causes it to return 0 because it needs more
        // input than one callback delivers — resulting in permanent silence.
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            if (_startupValidationPending && !_stopping)
            {
                _startupFailureException = e.Exception;
                _startupUnexpectedRecordingStopped = true;
                _startupValidationTcs?.TrySetResult(new StartupValidationDecision(
                    Healthy: false,
                    Reason: "unexpected_recording_stop",
                    Retryable: false,
                    ObservedCallbacks: _startupCallbackCount,
                    EffectiveCallbacks: _startupCallbackCount,
                    EffectiveDeadCallbacks: _startupDeadCallbackCount,
                    IgnoredWarmupDeadCallbacks: 0,
                    EffectiveAverageRms: _startupCallbackCount > 0 ? _startupCallbackRmsTotal / _startupCallbackCount : 0f,
                    EffectiveAveragePeak: _startupCallbackCount > 0 ? _startupCallbackPeakTotal / _startupCallbackCount : 0f));
            }

            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(
                e.Exception,
                "[MicrophoneSource.RecordingStopped] Recording stopped with error. Device='{Device}'",
                _deviceName);
        }
        else
        {
            if (_startupValidationPending && !_stopping)
            {
                _startupUnexpectedRecordingStopped = true;
                _startupFailureException ??= new InvalidOperationException(
                    $"Microphone recording stopped unexpectedly during startup on device '{_deviceName}'.");
                _startupValidationTcs?.TrySetResult(new StartupValidationDecision(
                    Healthy: false,
                    Reason: "unexpected_recording_stop",
                    Retryable: false,
                    ObservedCallbacks: _startupCallbackCount,
                    EffectiveCallbacks: _startupCallbackCount,
                    EffectiveDeadCallbacks: _startupDeadCallbackCount,
                    IgnoredWarmupDeadCallbacks: 0,
                    EffectiveAverageRms: _startupCallbackCount > 0 ? _startupCallbackRmsTotal / _startupCallbackCount : 0f,
                    EffectiveAveragePeak: _startupCallbackCount > 0 ? _startupCallbackPeakTotal / _startupCallbackCount : 0f));
            }

            _logger.LogInformation(
                "[MicrophoneSource.RecordingStopped] Recording stopped cleanly. Status={Status} Device='{Device}'",
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
        var convRms  = AudioChunkDiagnostics.ComputeRms(data);
        var convPeak = AudioChunkDiagnostics.ComputePeak(data);
        var (convMin, convMax, convZeroRatio) = AudioChunkDiagnostics.ComputeMinMaxZero(data);

        _convertedRmsWasapi = convRms;

        var classification = AudioChunkDiagnostics.ClassifyChunk(_nativePeakWasapi, convPeak);

        if (!_firstChunkLogged)
        {
            if (_startupRawHadSignal && IsClearlyDeadChunk(convRms, convPeak))
            {
                _logger.LogError(
                    "[MicInvariant] Raw callback had signal but emitted chunk was silent. Audio lost inside Argus pipeline.");
            }

            if (_useWasapiFastPath && !_wasapiFastPathFirstChunkLogged)
            {
                _wasapiFastPathFirstChunkLogged = true;
                _logger.LogInformation(
                    "[WasapiFastPath] firstChunk rms={Rms:F4} peak={Peak:F4}",
                    convRms,
                    convPeak);
            }

            _firstChunkLogged = true;
            _logger.LogInformation(
                "[MicChunk/WASAPI] FIRST CHUNK emitted. Device='{Dev}' Duration={Dur:F2}s Bytes={B} " +
                "NativeRMS={NR:F4} NativePeak={NP:F4} ConvRMS={CR:F4} ConvPeak={CP:F4} CLASS={Class}",
                _deviceName, duration.TotalSeconds, data.Length,
                _nativeRmsWasapi, _nativePeakWasapi, convRms, convPeak, classification);
        }

        _logger.LogDebug(
            "[MicChunk/WASAPI] Device='{Dev}' Duration={Dur:F2}s Bytes={Bytes} " +
            "NativeRMS={NativeRms:F4} | " +
            "ConvRMS={Rms:F4} ConvPeak={Peak:F4} ConvMin={Min:F4} ConvMax={Max:F4} ConvZeros={Zeros:P0} | " +
            "CLASS={Class}",
            _deviceName, duration.TotalSeconds, data.Length,
            _nativeRmsWasapi,
            convRms, convPeak, convMin, convMax, convZeroRatio,
            classification);

        if (classification == AudioChunkClass.NativeZero)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastSilentWarnAt).TotalSeconds >= 60)
            {
                _lastSilentWarnAt = now;
                _logger.LogError(
                    "[MicChunk/WASAPI] CLASS=NativeZero — WASAPI device '{Dev}' delivers all-zero audio. " +
                    "This device may be locked in exclusive mode by another app (Teams/Discord/etc).",
                    _deviceName);
            }
        }
        else if (classification == AudioChunkClass.ConversionDestroyedAudio)
        {
            // Only report if the native signal was genuinely strong (> 1 % FS peak).
            // The first chunk after startup can arrive with a low native RMS that is
            // correctly HealthyAudio by ConvPeak but the classification disagrees.
            // Suppress the error for weak-native chunks to avoid misleading logs.
            if (_nativePeakWasapi > 0.01f)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastConvErrAt).TotalSeconds >= 60)
                {
                    _lastConvErrAt = now;
                    _logger.LogError(
                        "[MicChunk/WASAPI] CLASS=ConversionDestroyedAudio — native Peak={NP:F4} but conv Peak={CP:F4}. " +
                        "The managed resampling chain is destroying the signal.",
                        _nativePeakWasapi, convPeak);
                }
            }
            else
            {
                _logger.LogDebug(
                    "[MicChunk/WASAPI] CLASS=ConversionDestroyedAudio suppressed — native signal weak " +
                    "(NativePeak={NP:F4} ≤ 0.01). Conv chain not implicated.",
                    _nativePeakWasapi);
            }
        }

        // ── Write debug WAVs only when the gate triggers ─────────────────────
        if (DebugAudioEnabled)
        {
            DebugSaveReason? reason = null;

            bool isSilent      = classification is AudioChunkClass.NativeZero or AudioChunkClass.NativeNearSilent;
            bool isConvFail    = classification == AudioChunkClass.ConversionDestroyedAudio;
            bool isTransition  = _dbgPrevClass == AudioChunkClass.HealthyAudio && isSilent;
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
                    var idx   = System.Threading.Interlocked.Increment(ref _debugConvIndex);
                    var stamp = DateTimeOffset.UtcNow;
                    var fmt   = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);

                    // Converted (Whisper-ready) WAV
                    var convPath = Path.Combine(_debugFolder,
                        $"mic_conv_{idx:D4}_{stamp:HHmmss}_{convRms:F3}.wav");
                    using (var w = new WaveFileWriter(convPath, fmt))
                        w.Write(data, 0, data.Length);

                    // Native WAV snapshot (whatever is in the rolling buffer right now)
                    string? nativePath = null;
                    if (_nativeFormat is not null && _nativeDebugBuffer.Length > 0)
                    {
                        var nativeIdx     = System.Threading.Interlocked.Increment(ref _debugNativeIndex);
                        nativePath        = Path.Combine(_debugFolder,
                            $"mic_native_{nativeIdx:D4}_{stamp:HHmmss}.wav");
                        var nativeSnap    = _nativeDebugBuffer.ToArray();
                        var nativeBytes   = nativeSnap.Length;
                        var bytesPerFrame = (_nativeFormat.BitsPerSample / 8) * _nativeFormat.Channels;
                        var nativeFrames  = nativeBytes / bytesPerFrame;
                        var nativeDurSec  = (double)nativeFrames / _nativeFormat.SampleRate;
                        using var wn = new WaveFileWriter(nativePath, _nativeFormat);
                        wn.Write(nativeSnap, 0, nativeBytes);
                        // Reset so next event gets a fresh window (cap resets on next OnDataAvailable)
                        _nativeDebugBuffer = new MemoryStream();

                        _logger.LogDebug(
                            "[MicDebug] NativeSnap: {Bytes}B  {Frames} frames  {Dur:F3}s  " +
                            "@ {Rate}Hz/{Bits}bit/{Ch}ch",
                            nativeBytes, nativeFrames, nativeDurSec,
                            _nativeFormat.SampleRate, _nativeFormat.BitsPerSample, _nativeFormat.Channels);

                        if (nativeDurSec < 0.5)
                            _logger.LogWarning(
                                "[MicDebug] SHORT native snapshot: {Dur:F3}s (expected ≥ {Expected:F1}s). " +
                                "Buffer had only {Bytes}B when save triggered.",
                                nativeDurSec, _chunkDuration.TotalSeconds, nativeBytes);
                    }

                    _logger.LogDebug(
                        "[MicDebug] Saved debug WAV. Reason={Reason} CLASS={Class} " +
                        "NativeRMS={NR:F4} ConvRMS={CR:F4} ConvZeros={CZ:P0} " +
                        "Conv={ConvPath} Native={NativePath}",
                        reason.Value, classification,
                        _nativeRmsWasapi, convRms, convZeroRatio,
                        convPath, nativePath ?? "(none)");

                    AudioChunkDiagnostics.RotateDebugFolder(_debugFolder, DebugMaxFiles, DebugMaxBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[MicDebug] Failed to write debug WAV.");
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
        { _logger.LogWarning(ex, "MicrophoneCaptureSource: ChunkReady handler threw."); }
    }

    private void OnWaveInNativeCallbackObserved(object? sender, WaveInMicrophoneBackend.NativeCallbackInfo info)
    {
        ObserveStartupCallback(info.Rms, info.Peak, info.BytesRecorded);
    }

    private void OnWaveInChunkReady(object? sender, AudioChunk chunk)
    {
        try   { ChunkReady?.Invoke(this, chunk); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "MicrophoneCaptureSource: ChunkReady handler threw."); }
    }

    private async Task StartWithStartupValidationAsync(MicBackend initialBackend, Guid sessionId, CancellationToken ct)
    {
        var currentBackend = initialBackend;
        var allowFailover  = SelectedBackend == MicBackend.Auto;
        var failoverTried  = false;

        while (true)
        {
            var lastDecision = new StartupValidationDecision(
                Healthy: false,
                Reason: "startup_not_evaluated",
                Retryable: true,
                ObservedCallbacks: 0,
                EffectiveCallbacks: 0,
                EffectiveDeadCallbacks: 0,
                IgnoredWarmupDeadCallbacks: 0,
                EffectiveAverageRms: 0f,
                EffectiveAveragePeak: 0f);

            for (int attempt = 0; attempt <= StartupRetryCount; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                ActiveBackend = currentBackend;
                if (currentBackend == MicBackend.WaveIn)
                    _waveInRequestedSampleRate = attempt == 0 ? 44_100 : 16_000;

                BeginStartupValidation(attempt);

                var validationTimeout = GetStartupValidationTimeout(currentBackend, attempt);
                _logger.LogInformation(
                    "[MicStartupValidationConfig] backend={Backend} attempt={Attempt} timeout={Timeout:F2}s requiredCallbacks={RequiredCallbacks} ignoredInitialDeadCallbacks={IgnoredDeadCallbacks} deadRmsThreshold={DeadRmsThreshold:F4} deadPeakThreshold={DeadPeakThreshold:F4}",
                    BackendName(currentBackend),
                    attempt + 1,
                    validationTimeout.TotalSeconds,
                    GetRequiredStartupValidationCallbacks(currentBackend, attempt),
                    GetStartupIgnoredInitialDeadCallbacks(currentBackend, attempt),
                    DeadChunkRmsThreshold,
                    DeadChunkPeakThreshold);

                if (currentBackend == MicBackend.WaveIn)
                    await StartWaveInAsync(sessionId).ConfigureAwait(false);
                else
                    await StartWasapiAsync(sessionId).ConfigureAwait(false);

                var startupTask = _startupValidationTcs?.Task ?? Task.FromResult(new StartupValidationDecision(
                    Healthy: true,
                    Reason: "validation_not_required",
                    Retryable: false,
                    ObservedCallbacks: 0,
                    EffectiveCallbacks: 0,
                    EffectiveDeadCallbacks: 0,
                    IgnoredWarmupDeadCallbacks: 0,
                    EffectiveAverageRms: 0f,
                    EffectiveAveragePeak: 0f));
                var completed = await Task.WhenAny(startupTask, Task.Delay(validationTimeout, ct)).ConfigureAwait(false);
                var startupDecision = completed == startupTask
                    ? await startupTask.ConfigureAwait(false)
                    : CompleteStartupValidationAfterTimeout();

                lastDecision = startupDecision;

                if (currentBackend == MicBackend.WaveIn)
                    LogWaveInRuntimeStartup(startupDecision.Healthy, !startupDecision.Healthy && attempt == 0);

                if (startupDecision.Healthy)
                    return;

                await StopCurrentBackendAsync().ConfigureAwait(false);

                if (attempt < StartupRetryCount)
                {
                    if (!startupDecision.Retryable)
                        break;

                    _logger.LogWarning(
                        "[MicSource.StartupRetry] backend={Backend} attempt={Attempt} reason={Reason} retryable={Retryable}",
                        BackendName(currentBackend), attempt + 1, startupDecision.Reason, startupDecision.Retryable);
                    continue;
                }

                break;
            }

            if (!allowFailover || failoverTried)
            {
                Status = AudioCaptureStatus.DeviceError;
                throw new InvalidOperationException(
                    $"Microphone startup failed. Backend={BackendName(currentBackend)} remained unavailable after {StartupRetryCount + 1} startup attempts. LastReason={lastDecision.Reason}. ObservedCallbacks={lastDecision.ObservedCallbacks}. EffectiveCallbacks={lastDecision.EffectiveCallbacks}. Explicit backend selection prevents automatic fallback.");
            }

            var nextBackend = currentBackend == MicBackend.WaveIn ? MicBackend.Wasapi : MicBackend.WaveIn;
            _logger.LogWarning(
                "[MicBackendFailover] from={From} to={To} reason=startup_dead_after_retries",
                BackendName(currentBackend), BackendName(nextBackend));

            currentBackend = nextBackend;
            failoverTried  = true;
        }
    }

    private void BeginStartupValidation(int attempt)
    {
        ResetStartupValidationState();
        _startupValidationAttempt = attempt;
        _startupValidationPending = true;
        _startupValidationTcs = new TaskCompletionSource<StartupValidationDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ObserveStartupCallback(float rms, float peak, int bytesRecorded)
    {
        if (!_startupValidationPending || _startupValidationTcs?.Task.IsCompleted == true)
            return;

        _startupCallbackCount++;
        _startupCallbackRmsTotal += rms;
        _startupCallbackPeakTotal += peak;
        _startupBytesReceived += bytesRecorded;
        _startupSignalSamples.Add((rms, peak));

        if (IsClearlyDeadChunk(rms, peak))
            _startupDeadCallbackCount++;

        if (_startupCallbackCount < GetRequiredStartupValidationCallbacks(ActiveBackend, _startupValidationAttempt))
            return;

        var decision = EvaluateStartupStreamHealth();
        if (!decision.Healthy && decision.Reason == "warming_up")
            return;

        CompleteStartupValidation(decision);
    }

    private StartupValidationDecision CompleteStartupValidationAfterTimeout()
    {
        var decision = EvaluateStartupStreamHealth();
        CompleteStartupValidation(decision);
        return decision;
    }

    private StartupValidationDecision EvaluateStartupStreamHealth()
    {
        var finalAttempt = _startupValidationAttempt >= StartupRetryCount;
        var requiredCallbacks = GetRequiredStartupValidationCallbacks(ActiveBackend, _startupValidationAttempt);
        var ignoredWarmupDeadCallbacks = GetStartupIgnoredInitialDeadCallbacks(ActiveBackend, _startupValidationAttempt);
        var effectiveSamples = GetEffectiveStartupSamples(ignoredWarmupDeadCallbacks);
        var effectiveCallbackCount = effectiveSamples.Count;
        var effectiveDeadCallbackCount = effectiveSamples.Count(static sample => IsClearlyDeadChunk(sample.Rms, sample.Peak));
        var effectiveAverageRms = effectiveCallbackCount > 0 ? effectiveSamples.Average(static sample => sample.Rms) : 0f;
        var effectiveAveragePeak = effectiveCallbackCount > 0 ? effectiveSamples.Average(static sample => sample.Peak) : 0f;
        var meaningfulSignalCallbacks = effectiveCallbackCount - effectiveDeadCallbackCount;

        if (_startupCallbackCount <= 0)
            return new StartupValidationDecision(false, "no_callbacks_yet", !finalAttempt, 0, 0, 0, 0, 0f, 0f);

        if (_startupBytesReceived <= 0)
            return new StartupValidationDecision(false, "no_bytes_received", !finalAttempt, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (_startupUnexpectedRecordingStopped)
            return new StartupValidationDecision(false, "unexpected_recording_stop", false, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (_startupFailureException is not null)
            return new StartupValidationDecision(false, "startup_exception", false, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (meaningfulSignalCallbacks > 0 && !IsClearlyDeadChunk(effectiveAverageRms, effectiveAveragePeak))
            return new StartupValidationDecision(true, "meaningful_signal_observed", false, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (_startupCallbackCount < requiredCallbacks)
            return new StartupValidationDecision(false, "warming_up", !finalAttempt, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (effectiveCallbackCount <= 0)
            return new StartupValidationDecision(false, finalAttempt ? "all_callbacks_dead" : "warming_up", !finalAttempt, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (effectiveDeadCallbackCount >= effectiveCallbackCount)
            return new StartupValidationDecision(false, finalAttempt ? "all_callbacks_dead" : "warming_up", !finalAttempt, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        if (IsClearlyDeadChunk(effectiveAverageRms, effectiveAveragePeak))
            return new StartupValidationDecision(false, finalAttempt ? "average_signal_effectively_zero" : "warming_up", !finalAttempt, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);

        return new StartupValidationDecision(false, finalAttempt ? "insufficient_meaningful_signal" : "warming_up", !finalAttempt, _startupCallbackCount, effectiveCallbackCount, effectiveDeadCallbackCount, ignoredWarmupDeadCallbacks, effectiveAverageRms, effectiveAveragePeak);
    }

    private void CompleteStartupValidation(StartupValidationDecision decision)
    {
        var avgRms  = _startupCallbackCount > 0 ? _startupCallbackRmsTotal / _startupCallbackCount : 0f;
        var avgPeak = _startupCallbackCount > 0 ? _startupCallbackPeakTotal / _startupCallbackCount : 0f;

        if (decision.Healthy)
            ReleaseStartupValidationGate();

        _logger.LogInformation(
            "[MicStartupValidationDecision] backend={Backend} healthy={Healthy} reason={Reason} retryable={Retryable} observedCallbacks={ObservedCallbacks} effectiveCallbacks={EffectiveCallbacks} effectiveDeadCallbacks={EffectiveDeadCallbacks} ignoredInitialDeadCallbacks={IgnoredInitialDeadCallbacks} effectiveAvgRms={EffectiveAvgRms:F4} effectiveAvgPeak={EffectiveAvgPeak:F4} requiredCallbacks={RequiredCallbacks} deadRmsThreshold={DeadRmsThreshold:F4} deadPeakThreshold={DeadPeakThreshold:F4}",
            BackendName(ActiveBackend),
            decision.Healthy,
            decision.Reason,
            decision.Retryable,
            decision.ObservedCallbacks,
            decision.EffectiveCallbacks,
            decision.EffectiveDeadCallbacks,
            decision.IgnoredWarmupDeadCallbacks,
            decision.EffectiveAverageRms,
            decision.EffectiveAveragePeak,
            GetRequiredStartupValidationCallbacks(ActiveBackend, _startupValidationAttempt),
            DeadChunkRmsThreshold,
            DeadChunkPeakThreshold);

        _logger.LogInformation(
            "[MicStartupValidation] backend={Backend} callbacks={Callbacks} bytes={Bytes} avgRms={AvgRms:F4} avgPeak={AvgPeak:F4} deadCallbacks={DeadCallbacks} healthy={Healthy} stoppedUnexpectedly={StoppedUnexpectedly} failure={Failure}",
            BackendName(ActiveBackend),
            _startupCallbackCount,
            _startupBytesReceived,
            avgRms,
            avgPeak,
            _startupDeadCallbackCount,
            decision.Healthy,
            _startupUnexpectedRecordingStopped,
            _startupFailureException?.Message ?? "(none)");

        _startupValidationTcs?.TrySetResult(decision);
    }

    private static int GetRequiredStartupValidationCallbacks(MicBackend backend, int attempt)
        => backend == MicBackend.Wasapi
            ? WasapiStartupValidationCallbackCount + (attempt * 2)
            : StartupValidationCallbackCount;

    private static int GetStartupIgnoredInitialDeadCallbacks(MicBackend backend, int attempt)
        => backend == MicBackend.Wasapi
            ? Math.Min(WasapiStartupIgnoredInitialDeadCallbacks + attempt, 4)
            : 0;

    private static TimeSpan GetStartupValidationTimeout(MicBackend backend, int attempt)
    {
        if (backend != MicBackend.Wasapi)
            return StartupValidationTimeout;

        return attempt switch
        {
            0 => WasapiStartupValidationTimeout,
            1 => WasapiStartupRetryValidationTimeout,
            _ => TimeSpan.FromSeconds(4.0)
        };
    }

    private List<(float Rms, float Peak)> GetEffectiveStartupSamples(int ignoredWarmupDeadCallbacks)
    {
        if (_startupSignalSamples.Count == 0)
            return [];

        var effectiveSamples = new List<(float Rms, float Peak)>(_startupSignalSamples.Count);
        var ignoredDeadCallbacks = 0;

        foreach (var sample in _startupSignalSamples)
        {
            if (ignoredDeadCallbacks < ignoredWarmupDeadCallbacks && IsClearlyDeadChunk(sample.Rms, sample.Peak))
            {
                ignoredDeadCallbacks++;
                continue;
            }

            effectiveSamples.Add(sample);
        }

        return effectiveSamples;
    }

    private void ReleaseStartupValidationGate()
    {
        _startupValidationPending = false;

        if (_waveInBackend is not null)
        {
            _waveInBackend.StartupValidationPending = false;
            _waveInBackend.ReleaseStartupBufferedAudio();
        }
    }

    private void LogWaveInRuntimeStartup(bool healthy, bool retryingFallback)
    {
        var avgRms = _startupCallbackCount > 0 ? _startupCallbackRmsTotal / _startupCallbackCount : 0f;
        var avgPeak = _startupCallbackCount > 0 ? _startupCallbackPeakTotal / _startupCallbackCount : 0f;

        _logger.LogInformation(
            "[WaveInRuntimeStartup] format={Rate}/16/1 callbacks={Callbacks} avgRms={AvgRms:F4} avgPeak={AvgPeak:F4} healthy={Healthy} retryingFallback={RetryingFallback}",
            _waveInRequestedSampleRate,
            _startupCallbackCount,
            avgRms,
            avgPeak,
            healthy,
            retryingFallback);
    }

    private async Task StartWasapiAsync(Guid sessionId)
    {
        try
        {
            _stopping = false;
            using var freshEnum = new MMDeviceEnumerator();
            MMDevice freshDevice;
            if (!string.IsNullOrWhiteSpace(_targetDeviceId))
            {
                freshDevice = freshEnum.GetDevice(_targetDeviceId);
                _logger.LogInformation(
                    "[MicSource.Open] Resolved fresh device handle for id='{Id}'", _targetDeviceId);
            }
            else
            {
                freshDevice = freshEnum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                _logger.LogInformation(
                    "[MicSource.Open] Using default capture endpoint: '{Name}'",
                    freshDevice.FriendlyName);
            }

            _deviceName   = freshDevice.FriendlyName;
            _nativeFormat = null;

            try
            {
                var isMuted   = freshDevice.AudioEndpointVolume.Mute;
                var volScalar = freshDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                _logger.LogInformation(
                    "[MicSource.Open] Endpoint state — Mute={Mute}  MasterVolume={Vol:P0}  " +
                    "DataFlow=Capture  Device='{Name}'",
                    isMuted, volScalar, freshDevice.FriendlyName);
                if (isMuted)
                    _logger.LogWarning(
                        "[MicSource.Open] ENDPOINT IS MUTED — '{Name}' will deliver silence until unmuted.",
                        freshDevice.FriendlyName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "[MicSource.Open] Could not read endpoint volume/mute — device may not expose AudioEndpointVolume.");
            }

            _logger.LogInformation(
                "[MicSource.Open] Opening WasapiCapture — device='{Dev}' " +
                "useEventSync=false audioBufferMs=100 shareMode=Shared SessionId={Id}",
                _deviceName, sessionId);

            _capture      = new WasapiCapture(freshDevice,
                                useEventSync:                  false,
                                audioBufferMillisecondsLength: 100);
            _nativeFormat = _capture.WaveFormat;
            var nativeFormat = _nativeFormat;

            _useWasapiFastPath = IsWasapiDirectFloat32MonoFastPath(nativeFormat);

            _logger.LogInformation(
                "[MicSource.Open] Backend=WASAPI DeviceId='{DevId}' DeviceName='{Name}' " +
                "Format={Rate}Hz/{Bits}bit/{Ch}ch ({Enc}) SessionId={Id}",
                _targetDeviceId ?? freshDevice.ID, _deviceName,
                nativeFormat.SampleRate, nativeFormat.BitsPerSample, nativeFormat.Channels,
                nativeFormat.Encoding, sessionId);

            _logger.LogInformation(
                "[WasapiFastPath] nativeFormat={Format}",
                AudioChunkDiagnostics.FormatSummary(nativeFormat));

            if (_useWasapiFastPath)
            {
                _logger.LogInformation("[WasapiFastPath] usingDirectFloat32ToPcm16=True");
            }

            _nativeDebugTargetBytes = nativeFormat.SampleRate
                                    * nativeFormat.Channels
                                    * (nativeFormat.BitsPerSample / 8)
                                    * 3;
            _nativeDebugBuffer = new MemoryStream();

            if (_useWasapiFastPath)
            {
                _captureBuffer = null;
                _pcm16Provider = null;
                _logger.LogInformation(
                    "[MicSource.Start] WASAPI fast path active: {Summary} → direct float32 mono to PCM16 chunk buffer",
                    AudioChunkDiagnostics.FormatSummary(nativeFormat));
            }
            else
            {
                _captureBuffer = new BufferedWaveProvider(nativeFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration          = TimeSpan.FromSeconds(10)
                };

                ISampleProvider sampleProvider = _captureBuffer.ToSampleProvider();
                if (nativeFormat.Channels > 1)
                    sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
                if (nativeFormat.SampleRate != TargetSampleRate)
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TargetSampleRate);

                _pcm16Provider = new SampleToWaveProvider16(sampleProvider);

                _logger.LogInformation(
                    "[MicSource.Start] chain: {Summary} → {TargetRate}Hz/16bit/1ch DrainBlock={Block}B (managed)",
                    AudioChunkDiagnostics.FormatSummary(nativeFormat), TargetSampleRate, DrainBlockBytes);
            }

            _pcmBuffer = new MemoryStream();
            _capture.DataAvailable    += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            Status = AudioCaptureStatus.Capturing;
            _logger.LogInformation(
                "[MicrophoneSource.StartAsync] Capture started. Backend=WASAPI Device='{Device}' ChunkDuration={Dur}s SessionId={Id}",
                _deviceName, _chunkDuration.TotalSeconds, sessionId);
        }
        catch (Exception ex)
        {
            Status = AudioCaptureStatus.DeviceError;
            _logger.LogError(ex, "[MicrophoneSource.StartAsync] Failed to start on device '{Device}'.", _deviceName);
            DisposeCapture();
            throw;
        }

        await Task.CompletedTask;
    }

    private async Task StopCurrentBackendAsync()
    {
        if (_waveInBackend is not null)
        {
            _waveInBackend.NativeCallbackObserved -= OnWaveInNativeCallbackObserved;
            _waveInBackend.ChunkReady -= OnWaveInChunkReady;
            _waveInBackend.Stop();
            _waveInBackend.Dispose();
            _waveInBackend = null;
            Status = AudioCaptureStatus.Idle;
            return;
        }

        _stopping = true;

        if (_capture is not null)
        {
            _capture.DataAvailable    -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.StopRecording();
        }

        await Task.Delay(200, CancellationToken.None);

        lock (_captureLock)
        {
            DrainConverterLocked();
            FlushBuffer(isFinal: true);
        }

        DisposeCapture();
        Status = AudioCaptureStatus.Idle;
    }

    private void ResetStartupValidationState()
    {
        _startupCallbackCount = 0;
        _startupDeadCallbackCount = 0;
        _startupCallbackRmsTotal = 0f;
        _startupCallbackPeakTotal = 0f;
        _startupBytesReceived = 0;
        _startupValidationPending = false;
        _startupValidationTcs = null;
        _startupBufferedCallbacks.Clear();
        _startupSignalSamples.Clear();
        _startupRawHadSignal = false;
        _callbackBoundarySequence = 0;
        _startupUnexpectedRecordingStopped = false;
        _startupFailureException = null;
        _firstSamplesLogged = false;
        _firstChunkLogged = false;
        _levelLogWindowActive = false;
        _levelLogCallbackCount = 0;
    }

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
            "WASAPI",
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
        if (_startupBufferedCallbacks.Count == 0)
            return;

        foreach (var callback in _startupBufferedCallbacks)
        {
            int bytesRead;
            int bytesHanded;
            if (_useWasapiFastPath)
            {
                bytesHanded = callback.BytesRecorded;
                bytesRead = AppendWasapiFastPathPcm16Locked(callback.Buffer, callback.BytesRecorded);
            }
            else
            {
                if (_captureBuffer is null || _pcm16Provider is null)
                    break;

                _captureBuffer.AddSamples(callback.Buffer, 0, callback.BytesRecorded);
                bytesHanded = callback.BytesRecorded;
                bytesRead = DrainConverterLocked();
            }

            LogCallbackBoundary(
                "WASAPI",
                callback.Sequence,
                callback.NativeRms,
                callback.NativePeak,
                callback.BytesRecorded,
                bytesHanded,
                bytesRead,
                _pcmBuffer.Length,
                "startup_flush");
        }

        _startupBufferedCallbacks.Clear();
    }

    private void LogCallbackBoundary(
        string backend,
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

        _logger.LogDebug(
            "[MicCallbackBoundary] backend={Backend} cb={Callback} phase={Phase} nativeRms={NativeRms:F4} nativePeak={NativePeak:F4} bytesRecorded={BytesRecorded} bytesHanded={BytesHanded} bytesRead={BytesRead} chunkBufferBytes={ChunkBufferBytes}",
            backend,
            callbackNumber,
            phase,
            nativeRms,
            nativePeak,
            bytesRecorded,
            bytesHandedToCaptureBuffer,
            bytesReadFromConversionProvider,
            chunkBufferBytes);
    }

    private int AppendWasapiFastPathPcm16Locked(byte[] buffer, int bytesRecorded)
    {
        var pcm16 = ConvertFloat32MonoToPcm16(buffer, bytesRecorded);
        _pcmBuffer.Write(pcm16, 0, pcm16.Length);
        TryEmitChunk();
        return pcm16.Length;
    }

    private static bool IsWasapiDirectFloat32MonoFastPath(WaveFormat format)
        => format.SampleRate == TargetSampleRate
        && format.Channels == TargetChannels
        && format.BitsPerSample == 32
        && WasapiIsolationTest.IsIeeeFloat(format);

    private static byte[] ConvertFloat32MonoToPcm16(byte[] buffer, int bytesRecorded)
    {
        var sampleCount = bytesRecorded / 4;
        var pcm16 = new byte[sampleCount * 2];

        for (int i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToSingle(buffer, i * 4);
            var scaled = Math.Clamp((int)MathF.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
            var pcm = (short)scaled;
            pcm16[i * 2] = (byte)(pcm & 0xFF);
            pcm16[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return pcm16;
    }

    private static bool IsClearlyDeadChunk(float rms, float peak)
        => rms < DeadChunkRmsThreshold && peak < DeadChunkPeakThreshold;

    private static string BackendName(MicBackend backend)
        => backend switch
        {
            MicBackend.Wasapi => "WASAPI",
            MicBackend.WaveIn => "WaveIn",
            _ => "Auto"
        };

    private static MicBackend ChooseBackend(
        MicBackendProber.ProbeResult wasapiResult,
        MicBackendProber.ProbeResult waveInResult)
    {
        if (waveInResult.HealthScore > wasapiResult.HealthScore)
            return MicBackend.WaveIn;

        if (wasapiResult.HealthScore > waveInResult.HealthScore)
            return MicBackend.Wasapi;

        if (waveInResult.AveragePeak > wasapiResult.AveragePeak)
            return MicBackend.WaveIn;

        if (wasapiResult.AveragePeak > waveInResult.AveragePeak)
            return MicBackend.Wasapi;

        return wasapiResult.CallbackCount >= waveInResult.CallbackCount
            ? MicBackend.Wasapi
            : MicBackend.WaveIn;
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    private void CloseContWav()
    {
        if (_contWavWriter is null) return;
        try
        {
            var durationSec = _contWavWriter.TotalTime.TotalSeconds;
            var path        = _contWavWriter.Filename;
            _contWavWriter.Dispose();
            _logger.LogInformation(
                "[MicContWav] Closed continuous WAV '{Path}' — {Dur:F2}s recorded.",
                path, durationSec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MicContWav] Error closing continuous WAV.");
        }
        finally
        {
            _contWavWriter = null;
            _contWavClosed = true;
        }
    }

    private void DisposeCapture()
    {
        CloseContWav();

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
            "[MicSource.Dispose] Status={Status} Backend={Backend} Device='{Device}'",
            Status, ActiveBackend, _deviceName ?? "(not set)");

        if (_waveInBackend is not null)
        {
            _waveInBackend.NativeCallbackObserved -= OnWaveInNativeCallbackObserved;
            _waveInBackend.ChunkReady -= OnWaveInChunkReady;
            _waveInBackend.Dispose();
            _waveInBackend = null;
        }
        else
        {
            FlushBuffer(isFinal: false);
            DisposeCapture();
        }

        _pcmBuffer.Dispose();
        _nativeDebugBuffer.Dispose();
        // _targetDeviceId is a string — nothing to dispose.
        _logger.LogInformation("[MicSource.Dispose] Done.");
    }
}
