using Argus.AI.Configuration;
using Argus.AI.Discovery;
using Argus.App.Diagnostics;
using Argus.App.Services;
using Argus.Audio.Capture;
using Argus.Core.Contracts.Services;
using Argus.Core.Domain.Entities;
using Argus.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Argus.App;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly IAppBootstrapper _bootstrapper;
    private readonly IStartupDiagnosticsService _diagnostics;
    private readonly ISessionCoordinator _coordinator;
    private readonly ISessionStatePublisher _statePublisher;
    private readonly IAudioStatusPublisher _audioPublisher;
    private readonly IAssistantReactionPublisher _reactionPublisher;
    private readonly MicAudioSettings _micSettings;
    private readonly RawMicDiagnosticRecorder _rawMicDiagnosticRecorder;

    // Ticks every second to refresh the live session duration label.
    private readonly DispatcherTimer _durationTimer;

    // The last snapshot received — held so the timer can re-read StartedAt.
    private SessionStateSnapshot _currentSnapshot = SessionStateSnapshot.Idle;

    // Running count of segments shown (for the cap check).
    private int _displayedSegments;
    private const int MaxDisplayedSegments = 50;
    private bool _rawMicRecordingInProgress;

    private static readonly SolidColorBrush OkBrush      = new(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush WarnBrush    = new(System.Windows.Media.Color.FromRgb(0xF5, 0x7C, 0x00));
    private static readonly SolidColorBrush ErrorBrush   = new(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush NeutralBrush = new(System.Windows.Media.Color.FromRgb(0x75, 0x75, 0x75));
    private static readonly SolidColorBrush ListeningBrush = new(System.Windows.Media.Color.FromRgb(0x0D, 0x87, 0x4E));

    public MainWindow(
        ILogger<MainWindow> logger,
        IAppBootstrapper bootstrapper,
        IStartupDiagnosticsService diagnostics,
        ISessionCoordinator coordinator,
        ISessionStatePublisher statePublisher,
        IAudioStatusPublisher audioPublisher,
        IAssistantReactionPublisher reactionPublisher,
        MicAudioSettings micSettings)
    {
        _logger            = logger;
        _bootstrapper      = bootstrapper;
        _diagnostics       = diagnostics;
        _coordinator       = coordinator;
        _statePublisher    = statePublisher;
        _audioPublisher    = audioPublisher;
        _reactionPublisher = reactionPublisher;
        _micSettings       = micSettings;
        _rawMicDiagnosticRecorder = new RawMicDiagnosticRecorder(logger);

        InitializeComponent();

        _logger.LogInformation(
            "MainWindow initialized. Bootstrap complete: {IsBootstrapped}",
            _bootstrapper.IsInitialized);

        // ?? Duration timer ????????????????????????????????????????????????????
        _durationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _durationTimer.Tick += (_, _) => RefreshDurationLabel();

        // ?? Populate backend override controls ????????????????????????????????
        PopulateMicBackendComboBox();
        PopulateWaveInDeviceComboBox();

        // Subscribe to session state changes
        _statePublisher.SnapshotChanged += OnSnapshotChanged;

        // Subscribe to audio/transcription status changes
        _audioPublisher.AudioStatusChanged += OnAudioStatusChanged;

        // Subscribe to live transcript segments
        _audioPublisher.TranscriptSegmentsReceived += OnTranscriptSegmentsReceived;

        // Subscribe to assistant reactions
        _reactionPublisher.ReactionChanged += OnReactionChanged;

        // Apply the current snapshots immediately
        ApplySnapshot(_statePublisher.Snapshot);
        ApplyAudioStatus(_audioPublisher.AudioStatus);
        ApplyReaction(_reactionPublisher.Current);

        // Diagnostics: apply if already available, otherwise subscribe
        if (_diagnostics.Result is not null)
            ApplyDiagnostics(_diagnostics.Result);
        else
            _diagnostics.DiagnosticsReady += OnDiagnosticsReady;

        UpdateRawMicDiagnosticUiState(isRecording: false);
    }

    // ?? Backend override controls ?????????????????????????????????????????????

    private void PopulateMicBackendComboBox()
    {
        MicBackendComboBox.Items.Clear();
        MicBackendComboBox.Items.Add(MicBackend.WaveIn);
        MicBackendComboBox.Items.Add(MicBackend.Wasapi);
        MicBackendComboBox.Items.Add(MicBackend.Auto);

        // Select current setting (default is WaveIn)
        MicBackendComboBox.SelectedItem = _micSettings.Backend;
    }

    private void PopulateWaveInDeviceComboBox()
    {
        WaveInDeviceComboBox.Items.Clear();

        var devices = WaveInMicrophoneBackend.EnumerateDevices();
        if (devices.Count == 0)
        {
            WaveInDeviceComboBox.Items.Add("(no WaveIn devices found)");
            WaveInDeviceComboBox.SelectedIndex = 0;
            return;
        }

        foreach (var (index, name) in devices)
            WaveInDeviceComboBox.Items.Add($"[{index}] {name}");

        // Select current setting
        int sel = Math.Clamp(_micSettings.WaveInDeviceNumber, 0, devices.Count - 1);
        WaveInDeviceComboBox.SelectedIndex = sel;

        _logger.LogInformation(
            "[MainWindow] WaveIn devices enumerated: {Count}  Selected=[{Sel}] {Name}",
            devices.Count, sel, devices[sel].Name);
    }

    private void MicBackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MicBackendComboBox.SelectedItem is MicBackend backend)
        {
            _micSettings.Backend = backend;
            _logger.LogInformation("[MainWindow] Mic backend override set to: {Backend}", backend);
        }
    }

    private void WaveInDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = WaveInDeviceComboBox.SelectedIndex;
        if (idx >= 0)
        {
            _micSettings.WaveInDeviceNumber = idx;
            _logger.LogInformation("[MainWindow] WaveIn device override set to index: {Index}", idx);
        }
    }

    private void OnSnapshotChanged(object? sender, SessionStateSnapshot snap)
    {
        Dispatcher.InvokeAsync(() => ApplySnapshot(snap));
    }

    // -- Audio status ----------------------------------------------------------

    private void OnAudioStatusChanged(object? sender, AudioStatusSnapshot audio)
    {
        Dispatcher.InvokeAsync(() => ApplyAudioStatus(audio));
    }

    private void ApplyAudioStatus(AudioStatusSnapshot audio)
    {
        // Microphone dot + status text
        MicDot.Fill = audio.MicrophoneStatus switch
        {
            AudioCaptureStatus.Capturing   => ListeningBrush,
            AudioCaptureStatus.Paused      => WarnBrush,
            AudioCaptureStatus.DeviceError => ErrorBrush,
            AudioCaptureStatus.NoDevice    => ErrorBrush,
            _                              => NeutralBrush
        };
        MicStatusText.Text  = audio.MicrophoneStatusDisplay;
        MicDeviceText.Text  = string.IsNullOrWhiteSpace(audio.MicrophoneDevice) ? "—" : audio.MicrophoneDevice;

        // Active backend label (always visible so you can confirm what's running)
        MicActiveBackendText.Text = audio.MicrophoneStatus == AudioCaptureStatus.Capturing
            ? audio.ActiveMicBackend.ToString()
            : "—";
        MicActiveBackendText.Foreground = audio.ActiveMicBackend == MicBackend.WaveIn
            ? OkBrush
            : WarnBrush;

        // Live mic level meter (only visible while capturing)
        var isCapturing = audio.MicrophoneStatus == AudioCaptureStatus.Capturing;
        MicLevelPanel.Visibility       = isCapturing ? Visibility.Visible : Visibility.Collapsed;
        MicSignalDebugPanel.Visibility = isCapturing ? Visibility.Visible : Visibility.Collapsed;

        if (isCapturing)
        {
            MicLevelText.Text       = audio.MicLevelDisplay;
            MicLevelText.Foreground = audio.MicConvertedRms < 0.002f ? ErrorBrush
                                    : audio.MicConvertedRms < 0.015f ? WarnBrush
                                    : OkBrush;
            MicSignalDebugText.Text = audio.MicSignalDebugDisplay;
        }

        // System audio dot + status text
        SystemAudioDot.Fill = audio.SystemAudioStatus switch
        {
            AudioCaptureStatus.Capturing   => ListeningBrush,
            AudioCaptureStatus.Paused      => WarnBrush,
            AudioCaptureStatus.DeviceError => ErrorBrush,
            _                              => NeutralBrush
        };
        SystemAudioStatusText.Text  = audio.SystemAudioStatusDisplay;
        SystemAudioDeviceText.Text  = string.IsNullOrWhiteSpace(audio.SystemAudioDevice) ? "—" : audio.SystemAudioDevice;

        // Transcription dot + text
        TranscriptionDot.Fill = audio.TranscriptionStatus switch
        {
            TranscriptionPipelineStatus.Transcribing => ListeningBrush,
            TranscriptionPipelineStatus.Error        => ErrorBrush,
            TranscriptionPipelineStatus.NoProvider   => WarnBrush,
            _                                        => NeutralBrush
        };
        TranscriptionStatusText.Text = audio.TranscriptionStatusDisplay;

        // Provider / model
        TranscriptionProviderText.Text = audio.TranscriptionConfigured
            ? $"{audio.TranscriptionProviderDisplay}  [{audio.TranscriptionLanguageModeDisplay}]"
            : "? Not configured";

        // Whisper model download state (only shown for WhisperNet provider)
        var isWhisperNet = audio.WhisperDownloadState != WhisperModelDownloadState.NotApplicable;
        WhisperDownloadStatePanel.Visibility = isWhisperNet ? Visibility.Visible : Visibility.Collapsed;
        WhisperModelPathPanel.Visibility     = isWhisperNet ? Visibility.Visible : Visibility.Collapsed;
        if (isWhisperNet)
        {
            WhisperDownloadStateText.Text = audio.WhisperDownloadStateDisplay;
            WhisperDownloadStateText.Foreground = audio.WhisperDownloadState switch
            {
                WhisperModelDownloadState.Ready      => OkBrush,
                WhisperModelDownloadState.Downloading => WarnBrush,
                WhisperModelDownloadState.Failed     => ErrorBrush,
                _                                    => NeutralBrush
            };
            WhisperModelPathText.Text = string.IsNullOrWhiteSpace(audio.WhisperModelPath)
                ? "—"
                : audio.WhisperModelPath;
        }

        // Queue length
        TranscriptionQueueText.Text = audio.PendingChunks > 0
            ? $"{audio.PendingChunks} chunk{(audio.PendingChunks == 1 ? "" : "s")} queued"
            : "0 chunks";

        // Last transcript time
        LastTranscriptTimeText.Text = audio.LastTranscriptionAt.HasValue
            ? audio.LastTranscriptionAt.Value.ToLocalTime().ToString("HH:mm:ss")
            : "—";

        // Last transcription error
        LastTranscriptionErrorText.Text = string.IsNullOrWhiteSpace(audio.TranscriptionError)
            ? "—"
            : audio.TranscriptionError;
        LastTranscriptionErrorText.Foreground = string.IsNullOrWhiteSpace(audio.TranscriptionError)
            ? NeutralBrush
            : ErrorBrush;

        // Not-configured warning banner
        if (!audio.TranscriptionConfigured && audio.MicrophoneStatus == AudioCaptureStatus.Capturing)
        {
            TranscriptionNotConfiguredBanner.Visibility  = Visibility.Visible;
            TranscriptionNotConfiguredText.Text =
                "? No transcription provider is configured. Audio is being captured but will not be transcribed.\n" +
                "WhisperNetLocal (fully local, no API key) should be enabled by default. " +
                "Check that ArgusAI:Profiles contains a WhisperNetLocal profile with \"Enabled\": true " +
                "and that ArgusAI:Defaults:Transcription is set to \"WhisperNetLocal\" in appsettings.json.";
        }
        else
        {
            TranscriptionNotConfiguredBanner.Visibility = Visibility.Collapsed;
        }
    }

    // -- Assistant reaction ----------------------------------------------------

    private void OnReactionChanged(object? sender, AssistantReactionSnapshot reaction)
    {
        Dispatcher.InvokeAsync(() => ApplyReaction(reaction));
    }

    private void ApplyReaction(AssistantReactionSnapshot reaction)
    {
        // Wake phrase
        AssistantWakePhraseText.Text = string.IsNullOrWhiteSpace(reaction.WakePhrase)
            ? "—"
            : $"\"{reaction.WakePhrase}\"";

        // Intent
        AssistantIntentText.Text = reaction.Intent == Argus.Transcription.Intent.DetectedIntent.None
            ? "—"
            : reaction.Intent.ToString();

        // Suggestion or error
        if (reaction.IsError)
        {
            AssistantSuggestionText.Text       = reaction.ErrorMessage ?? "Unknown error";
            AssistantSuggestionText.Foreground = ErrorBrush;
        }
        else if (!string.IsNullOrWhiteSpace(reaction.Suggestion))
        {
            AssistantSuggestionText.Text       = reaction.Suggestion;
            AssistantSuggestionText.Foreground = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x21, 0x21, 0x21));
        }
        else
        {
            AssistantSuggestionText.Text       = "—";
            AssistantSuggestionText.Foreground = NeutralBrush;
        }

        // Timestamp
        AssistantTimeText.Text = reaction.At.HasValue
            ? reaction.At.Value.ToLocalTime().ToString("HH:mm:ss")
            : "—";

        // Show the card once there's been at least one interaction
        AssistantCard.Visibility = reaction.Intent != Argus.Transcription.Intent.DetectedIntent.None
                                   || reaction.IsError
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // -- Live transcript -------------------------------------------------------

    private void OnTranscriptSegmentsReceived(object? sender, IReadOnlyList<TranscriptSegment> segments)
    {
        _logger.LogInformation(
            "[MainWindow.Transcript] Received {Count} segment(s)",
            segments.Count);
        Dispatcher.InvokeAsync(() => AppendTranscriptSegments(segments));
    }

    private void AppendTranscriptSegments(IReadOnlyList<TranscriptSegment> segments)
    {
        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;

            var text = seg.Text.Trim();
            if (string.Equals(text, "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "[inaudible]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _logger.LogInformation(
                "[MainWindow.Transcript] Appending segment. Text='{Text}'",
                text.Length > 120 ? text[..120] + "…" : text);

            // Show the list panel + scroll container on first segment
            if (_displayedSegments == 0)
            {
                TranscriptPlaceholder.Visibility  = Visibility.Collapsed;
                TranscriptListBorder.Visibility   = Visibility.Visible;
            }

            // Trim oldest entries when cap is reached
            if (_displayedSegments >= MaxDisplayedSegments
                && TranscriptItemsPanel.Children.Count > 0)
            {
                TranscriptItemsPanel.Children.RemoveAt(0);
                _displayedSegments--;
            }

            var row = BuildTranscriptRow(seg);
            TranscriptItemsPanel.Children.Add(row);
            _displayedSegments++;
        }

        // Auto-scroll to the latest segment
        TranscriptScrollViewer.ScrollToBottom();
    }

    private static Border BuildTranscriptRow(TranscriptSegment seg)
    {
        var timeLabel = new TextBlock
        {
            Text              = seg.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
            FontSize          = 10,
            Foreground        = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, 2, 8, 0),
            MinWidth          = 56
        };

        var speakerLabel = new TextBlock
        {
            Text              = seg.SpeakerType.ToString(),
            FontSize          = 10,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x42, 0x42)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, 2, 8, 0),
            MinWidth          = 60
        };

        var languageLabel = new TextBlock
        {
            Text              = string.IsNullOrWhiteSpace(seg.Language) ? "?" : seg.Language!.Trim().ToLowerInvariant(),
            FontSize          = 10,
            Foreground        = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x65, 0xC0)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, 2, 8, 0),
            MinWidth          = 24
        };

        var textBlock = new TextBlock
        {
            Text         = seg.Text,
            FontSize     = 12,
            Foreground   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x21, 0x21)),
            TextWrapping = TextWrapping.Wrap
        };

        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        content.Children.Add(timeLabel);
        content.Children.Add(speakerLabel);
        content.Children.Add(languageLabel);
        content.Children.Add(textBlock);

        return new Border
        {
            Child           = content,
            Padding         = new Thickness(4, 3, 4, 3),
            BorderBrush     = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private void ApplySnapshot(SessionStateSnapshot snap)
    {
        _currentSnapshot = snap;

        // -- State label and dot -----------------------------------------------
        SessionStateText.Text = snap.StateLabel;
        SessionStateDot.Fill = snap.LifecycleState switch
        {
            Core.Domain.Enums.SessionLifecycleState.Listening => ListeningBrush,
            Core.Domain.Enums.SessionLifecycleState.Paused    => WarnBrush,
            Core.Domain.Enums.SessionLifecycleState.Stopping  => WarnBrush,
            Core.Domain.Enums.SessionLifecycleState.Completed => OkBrush,
            _                                                  => NeutralBrush
        };

        // -- Session details ---------------------------------------------------
        SessionIdText.Text = snap.SessionId.HasValue
            ? snap.SessionId.Value.ToString("D")
            : "—";

        SessionStartedText.Text = snap.SessionStartedAt.HasValue
            ? snap.SessionStartedAt.Value.ToLocalTime().ToString("HH:mm:ss")
            : "—";

        SessionEventCountText.Text = snap.AppEventCount.ToString("N0");

        TranscriptSegmentCountText.Text = snap.TranscriptSegmentCount > 0
            ? $"({snap.TranscriptSegmentCount} segment{(snap.TranscriptSegmentCount == 1 ? "" : "s")})"
            : "(0 segments)";

        // Refresh duration now; timer keeps it ticking while active.
        RefreshDurationLabel();

        // Start or stop the 1-second timer based on whether a session is active.
        if (snap.IsActive)
            _durationTimer.Start();
        else
            _durationTimer.Stop();

        // -- Active window -----------------------------------------------------
        ActiveAppText.Text = string.IsNullOrWhiteSpace(snap.ActiveProcessName)
            ? "—"
            : snap.ActiveProcessName;

        ActiveProcessIdText.Text = snap.ActiveProcessId > 0
            ? snap.ActiveProcessId.ToString()
            : "—";

        ActiveWindowTitleText.Text = string.IsNullOrWhiteSpace(snap.ActiveWindowTitle)
            ? "—"
            : snap.ActiveWindowTitle;

        // -- Button states -----------------------------------------------------
        var isIdle      = snap.LifecycleState == Core.Domain.Enums.SessionLifecycleState.Idle;
        var isListening = snap.LifecycleState == Core.Domain.Enums.SessionLifecycleState.Listening;
        var isPaused    = snap.LifecycleState == Core.Domain.Enums.SessionLifecycleState.Paused;
        var isActive    = snap.IsActive;

        StartSessionButton.IsEnabled  = isIdle;
        PauseSessionButton.IsEnabled  = isListening;
        ResumeSessionButton.IsEnabled = isPaused;
        StopSessionButton.IsEnabled   = isActive;
    }

    private void RefreshDurationLabel()
    {
        SessionDurationText.Text = _currentSnapshot.GetDurationDisplay(DateTimeOffset.UtcNow);
    }

    private void UpdateRawMicDiagnosticUiState(bool isRecording)
    {
        _rawMicRecordingInProgress = isRecording;
        RecordRawWaveInButton.IsEnabled = !isRecording;
        RecordRawWasapiButton.IsEnabled = !isRecording;
    }

    private void ShowRawMicDiagnosticResult(string prefix, RawMicDiagnosticRecorder.Result result)
    {
        RawMicDiagnosticStatusText.Text =
            $"{prefix}: {result.FilePath}\n" +
            $"Device={result.DeviceName}  Format={result.Format}  Callbacks={result.CallbackCount}  " +
            $"Bytes={result.TotalBytesWritten}  AvgRMS={result.AverageRms:F4}  MaxPeak={result.MaxPeak:F4}  " +
            $"NonZero={result.AnyNonZeroSignalSeen}";
    }

    // -- Button handlers -------------------------------------------------------

    private async void StartSessionButton_Click(object sender, RoutedEventArgs e)
    {
        StartSessionButton.IsEnabled = false;
        try
        {
            await _coordinator.StartSessionAsync(
                $"Session {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start session.");
            StartSessionButton.IsEnabled = true;
        }
    }

    private async void PauseSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try   { await _coordinator.PauseSessionAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to pause session."); }
    }

    private async void ResumeSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try   { await _coordinator.ResumeSessionAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to resume session."); }
    }

    private async void StopSessionButton_Click(object sender, RoutedEventArgs e)
    {
        try   { await _coordinator.StopSessionAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to stop session."); }
    }

    private async void RecordRawWaveInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMicRecordingInProgress)
            return;

        UpdateRawMicDiagnosticUiState(isRecording: true);
        RawMicDiagnosticStatusText.Text = "Recording raw WaveIn microphone audio for 5 seconds...";

        try
        {
            var result = await _rawMicDiagnosticRecorder
                .RecordWaveInAsync(_micSettings.WaveInDeviceNumber)
                .ConfigureAwait(true);
            ShowRawMicDiagnosticResult("Raw WaveIn saved", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raw WaveIn microphone diagnostic recording failed.");
            RawMicDiagnosticStatusText.Text = $"Raw WaveIn recording failed: {ex.Message}";
        }
        finally
        {
            UpdateRawMicDiagnosticUiState(isRecording: false);
        }
    }

    private async void RecordRawWasapiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMicRecordingInProgress)
            return;

        UpdateRawMicDiagnosticUiState(isRecording: true);
        RawMicDiagnosticStatusText.Text = "Recording raw WASAPI microphone audio for 5 seconds...";

        try
        {
            var result = await _rawMicDiagnosticRecorder
                .RecordWasapiAsync()
                .ConfigureAwait(true);
            ShowRawMicDiagnosticResult("Raw WASAPI saved", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raw WASAPI microphone diagnostic recording failed.");
            RawMicDiagnosticStatusText.Text = $"Raw WASAPI recording failed: {ex.Message}";
        }
        finally
        {
            UpdateRawMicDiagnosticUiState(isRecording: false);
        }
    }

    // -- Diagnostics -----------------------------------------------------------

    private void OnDiagnosticsReady(object? sender, StartupDiagnosticsResult result)
    {
        Dispatcher.InvokeAsync(() => ApplyDiagnostics(result));
    }

    private void ApplyDiagnostics(StartupDiagnosticsResult r)
    {
        DiagnosticsTimestamp.Text = $"Last checked: {r.CapturedAt.ToLocalTime():HH:mm:ss}";

        // ?? System Status card ????????????????????????????????????????????????
        StorageDot.Fill    = r.StorageAvailable ? OkBrush : ErrorBrush;
        StorageStatus.Text = r.StorageAvailable
            ? $"OK  ({r.DataFolderPath})"
            : $"Error: {r.StorageError}";

        DatabaseDot.Fill    = r.DatabaseAvailable ? OkBrush : ErrorBrush;
        DatabaseStatus.Text = r.DatabaseAvailable
            ? $"OK  ({r.DatabasePath})"
            : $"Error: {r.DatabaseError}";

        // ?? Storage Paths card ????????????????????????????????????????????????
        if (r.StoragePaths is { } sp)
        {
            StorageModeDot.Fill      = r.StorageAvailable ? OkBrush : WarnBrush;
            StorageModeText.Text     = sp.ModeDisplay;
            StorageDataPathText.Text = sp.DataFolder;
            StorageCachePathText.Text    = sp.CacheFolder;
            StorageArtifactPathText.Text = sp.ArtifactsFolder;
            StorageDatabasePathText.Text = sp.DatabasePath;

            // Show the selection reason banner only in non-Default modes
            if (sp.Mode != StorageMode.Default
                && !string.IsNullOrWhiteSpace(sp.DataRootReason))
            {
                StorageDataReasonText.Text   = sp.DataRootReason;
                StorageReasonBanner.Visibility = Visibility.Visible;
            }
        }

        // ?? Provider / routing cards ??????????????????????????????????????????
        if (r.ProviderDiscovery is { } disc)
            ApplyProviderStatus(disc);

        if (r.EffectiveRouting is { } routing)
        {
            RoutingModeText.Text = routing.Mode.ToString();
            ApplyWorkflowRow(AiWorkflow.RealtimeAssist, routing, RealtimeAssistModel, RealtimeAssistState);
            ApplyWorkflowRow(AiWorkflow.MemoryQuery,    routing, MemoryQueryModel,    MemoryQueryState);
            ApplyWorkflowRow(AiWorkflow.MeetingSummary, routing, MeetingSummaryModel, MeetingSummaryState);
            ApplyWorkflowRow(AiWorkflow.ScreenExplain,  routing, ScreenExplainModel,  ScreenExplainState);
        }

        if (!r.AnyProviderAvailable)
            NoProvidersBanner.Visibility = Visibility.Visible;
    }

    private void ApplyProviderStatus(ProviderDiscoveryResult disc)
    {
        (OllamaDot.Fill, OllamaStatus.Text) = disc.OllamaAvailability switch
        {
            ProviderAvailability.Available   => (OkBrush,      $"Running  ({disc.OllamaModels.Count} model(s))  @ {disc.OllamaEndpoint}"),
            ProviderAvailability.NoModels    => (WarnBrush,    $"Running but no models installed  @ {disc.OllamaEndpoint}"),
            ProviderAvailability.Unreachable => (ErrorBrush,   $"Not reachable  @ {disc.OllamaEndpoint}"),
            ProviderAvailability.Error       => (ErrorBrush,   $"Error: {disc.OllamaError}"),
            _                                => (NeutralBrush, "Not configured")
        };

        if (disc.OllamaAvailability is ProviderAvailability.Unreachable or ProviderAvailability.NoModels)
        {
            OllamaWarningText.Text = disc.OllamaAvailability == ProviderAvailability.NoModels
                ? "? Ollama is running but has no models. Run: ollama pull llama3"
                : "? Ollama is not running. Start it with: ollama serve";
            OllamaWarningBanner.Visibility = Visibility.Visible;
        }

        (OpenAiDot.Fill, OpenAiStatus.Text) = disc.OpenAiAvailability switch
        {
            ProviderAvailability.Available     => (OkBrush,      "API key configured"),
            ProviderAvailability.NotConfigured => (NeutralBrush, "Not configured (set OPENAI_API_KEY to enable)"),
            ProviderAvailability.Error         => (ErrorBrush,   "Configuration error"),
            _                                  => (NeutralBrush, "Unknown")
        };
    }

    private static void ApplyWorkflowRow(
        AiWorkflow workflow,
        global::Argus.AI.Selection.EffectiveRoutingResult routing,
        System.Windows.Controls.TextBlock modelText,
        System.Windows.Controls.TextBlock stateText)
    {
        var wf = routing.ForWorkflow(workflow);
        if (wf is null)
        {
            modelText.Text       = "—";
            stateText.Text       = "—";
            stateText.Foreground = NeutralBrush;
            return;
        }

        modelText.Text = wf.PrimaryModelDisplay;

        if (wf.IsFullyAvailable)
        {
            stateText.Text       = "? Ready";
            stateText.Foreground = OkBrush;
        }
        else
        {
            stateText.Text       = "? Partial";
            stateText.Foreground = WarnBrush;
        }
    }

    // -- Window lifecycle ------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        _logger.LogDebug("MainWindow hidden (close intercepted for tray-first mode).");
        base.OnClosing(e);
    }

    // Called by App.xaml.cs when the host is shutting down for real.
    internal void OnHostShuttingDown()
    {
        _statePublisher.SnapshotChanged             -= OnSnapshotChanged;
        _audioPublisher.AudioStatusChanged           -= OnAudioStatusChanged;
        _audioPublisher.TranscriptSegmentsReceived   -= OnTranscriptSegmentsReceived;
        _diagnostics.DiagnosticsReady                -= OnDiagnosticsReady;
    }
}
