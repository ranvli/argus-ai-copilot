using Argus.AI.Configuration;
using Argus.AI.Discovery;
using Argus.App.Diagnostics;
using Argus.App.Services;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Argus.App;

public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly IAppBootstrapper _bootstrapper;
    private readonly IStartupDiagnosticsService _diagnostics;

    private static readonly SolidColorBrush OkBrush      = new(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush WarnBrush    = new(System.Windows.Media.Color.FromRgb(0xF5, 0x7C, 0x00));
    private static readonly SolidColorBrush ErrorBrush   = new(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush NeutralBrush = new(System.Windows.Media.Color.FromRgb(0x75, 0x75, 0x75));

    public MainWindow(
        ILogger<MainWindow> logger,
        IAppBootstrapper bootstrapper,
        IStartupDiagnosticsService diagnostics)
    {
        _logger       = logger;
        _bootstrapper = bootstrapper;
        _diagnostics  = diagnostics;

        InitializeComponent();

        _logger.LogInformation(
            "MainWindow initialized. Bootstrap complete: {IsBootstrapped}",
            _bootstrapper.IsInitialized);

        // If diagnostics already finished before the window was constructed, apply immediately.
        if (_diagnostics.Result is not null)
            ApplyDiagnostics(_diagnostics.Result);
        else
            _diagnostics.DiagnosticsReady += OnDiagnosticsReady;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private void OnDiagnosticsReady(object? sender, StartupDiagnosticsResult result)
    {
        // DiagnosticsReady fires on the hosted-service thread — marshal to UI.
        Dispatcher.InvokeAsync(() => ApplyDiagnostics(result));
    }

    private void ApplyDiagnostics(StartupDiagnosticsResult r)
    {
        DiagnosticsTimestamp.Text = $"Last checked: {r.CapturedAt.ToLocalTime():HH:mm:ss}";

        // ── Storage ───────────────────────────────────────────────────────────
        StorageDot.Fill     = r.StorageAvailable ? OkBrush : ErrorBrush;
        StorageStatus.Text  = r.StorageAvailable
            ? $"OK  ({r.DataFolderPath})"
            : $"Error: {r.StorageError}";

        // ── Database ──────────────────────────────────────────────────────────
        DatabaseDot.Fill    = r.DatabaseAvailable ? OkBrush : ErrorBrush;
        DatabaseStatus.Text = r.DatabaseAvailable
            ? $"OK  ({r.DatabasePath})"
            : $"Error: {r.DatabaseError}";

        // ── Providers ─────────────────────────────────────────────────────────
        if (r.ProviderDiscovery is { } disc)
            ApplyProviderStatus(disc);

        // ── Routing ───────────────────────────────────────────────────────────
        if (r.EffectiveRouting is { } routing)
        {
            RoutingModeText.Text = routing.Mode.ToString();
            ApplyWorkflowRow(AiWorkflow.RealtimeAssist, routing, RealtimeAssistModel, RealtimeAssistState);
            ApplyWorkflowRow(AiWorkflow.MemoryQuery,    routing, MemoryQueryModel,    MemoryQueryState);
            ApplyWorkflowRow(AiWorkflow.MeetingSummary, routing, MeetingSummaryModel, MeetingSummaryState);
            ApplyWorkflowRow(AiWorkflow.ScreenExplain,  routing, ScreenExplainModel,  ScreenExplainState);
        }

        // ── Global warnings ───────────────────────────────────────────────────
        if (!r.AnyProviderAvailable)
        {
            NoProvidersBanner.Visibility = Visibility.Visible;
        }
    }

    private void ApplyProviderStatus(ProviderDiscoveryResult disc)
    {
        // Ollama
        (OllamaDot.Fill, OllamaStatus.Text) = disc.OllamaAvailability switch
        {
            ProviderAvailability.Available    => (OkBrush,      $"Running  ({disc.OllamaModels.Count} model(s))  @ {disc.OllamaEndpoint}"),
            ProviderAvailability.NoModels     => (WarnBrush,    $"Running but no models installed  @ {disc.OllamaEndpoint}"),
            ProviderAvailability.Unreachable  => (ErrorBrush,   $"Not reachable  @ {disc.OllamaEndpoint}"),
            ProviderAvailability.Error        => (ErrorBrush,   $"Error: {disc.OllamaError}"),
            _                                 => (NeutralBrush, "Not configured")
        };

        if (disc.OllamaAvailability is ProviderAvailability.Unreachable or ProviderAvailability.NoModels)
        {
            OllamaWarningText.Text = disc.OllamaAvailability == ProviderAvailability.NoModels
                ? "⚠ Ollama is running but has no models. Run: ollama pull llama3"
                : "⚠ Ollama is not running. Start it with: ollama serve";
            OllamaWarningBanner.Visibility = Visibility.Visible;
        }

        // OpenAI
        (OpenAiDot.Fill, OpenAiStatus.Text) = disc.OpenAiAvailability switch
        {
            ProviderAvailability.Available    => (OkBrush,      "API key configured"),
            ProviderAvailability.NotConfigured => (NeutralBrush, "Not configured (set OPENAI_API_KEY to enable)"),
            ProviderAvailability.Error        => (ErrorBrush,   "Configuration error"),
            _                                 => (NeutralBrush, "Unknown")
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
            modelText.Text      = "—";
            stateText.Text      = "—";
            stateText.Foreground = NeutralBrush;
            return;
        }

        modelText.Text = wf.PrimaryModelDisplay;

        if (wf.IsFullyAvailable)
        {
            stateText.Text       = "✓ Ready";
            stateText.Foreground = OkBrush;
        }
        else
        {
            stateText.Text       = "⚠ Partial";
            stateText.Foreground = WarnBrush;
        }
    }

    // ── Window lifecycle ──────────────────────────────────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        // Tray-first: intercept the close gesture and hide the window instead.
        e.Cancel = true;
        Hide();
        _logger.LogDebug("MainWindow hidden (close intercepted for tray-first mode).");
        base.OnClosing(e);
    }
}
