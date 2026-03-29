using Argus.AI.Configuration;
using Argus.AI.Models;
using Argus.AI.Providers;
using Argus.Transcription.Intent;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Argus.App.Services;

/// <summary>
/// Listens for intent detection results and, when a wake or help phrase is detected,
/// builds a context prompt from the recent transcript and calls the configured chat model.
///
/// Only one reaction runs at a time — if a new intent fires while a request is in
/// flight the previous call is cancelled and replaced.
/// </summary>
public sealed class AssistantReactionService : IAssistantReactionPublisher, IAsyncDisposable
{
    private readonly IModelResolver _modelResolver;
    private readonly ILogger<AssistantReactionService> _logger;

    private CancellationTokenSource? _currentCts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastQuestionReactionAt = DateTimeOffset.MinValue;
    private string _lastQuestionFingerprint = string.Empty;
    private static readonly TimeSpan QuestionCooldown = TimeSpan.FromSeconds(12);
    private static readonly Regex CollapseSpaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex StripQuestionFingerprintNoise = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    public AssistantReactionSnapshot Current { get; private set; } =
        AssistantReactionSnapshot.Empty;

    public event EventHandler<AssistantReactionSnapshot>? ReactionChanged;

    public AssistantReactionService(
        IModelResolver modelResolver,
        ILogger<AssistantReactionService> logger)
    {
        _modelResolver = modelResolver;
        _logger        = logger;
    }

    /// <summary>
    /// Fires when the intent detector finds an actionable intent.
    /// Call from the segment-received handler in <see cref="SessionCoordinatorService"/>.
    /// </summary>
    public void OnIntentDetected(IntentDetectionResult result, string recentTranscriptText)
    {
        if (!result.HasIntent) return;

        if (result.Intent == DetectedIntent.QuestionForAssistant)
        {
            var fingerprint = BuildQuestionFingerprint(result.TriggerText);
            var now = DateTimeOffset.UtcNow;
            if (fingerprint.Length > 0 &&
                string.Equals(fingerprint, _lastQuestionFingerprint, StringComparison.Ordinal) &&
                now - _lastQuestionReactionAt < QuestionCooldown)
            {
                _logger.LogInformation(
                    "[ReactionCooldown] skipped={Reason}",
                    "same_question_within_cooldown");
                return;
            }

            _lastQuestionFingerprint = fingerprint;
            _lastQuestionReactionAt = now;
        }

        // Cancel any in-flight request and start a new one
        var nextCts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _currentCts, nextCts);
        if (prev is not null)
            _logger.LogDebug("[Assistant] Cancelling in-flight reaction because a newer intent arrived.");
        try { prev?.Cancel(); prev?.Dispose(); } catch { /* best-effort */ }

        var ct = nextCts.Token;
        _ = Task.Run(() => ReactAsync(result, recentTranscriptText, ct), ct);
    }

    private async Task ReactAsync(
        IntentDetectionResult result,
        string recentText,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation(
                "[Assistant] Reaction triggered. Intent={Intent} WakePhrase='{Wake}'",
                result.Intent, result.WakePhrase);

            IChatModel chat;
            try
            {
                chat = _modelResolver.ResolveChatModel(AiWorkflow.RealtimeAssist);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Assistant] No chat model available for RealtimeAssist. Detail: {Msg}", ex.Message);
                Publish(new AssistantReactionSnapshot
                {
                    WakePhrase   = result.WakePhrase,
                    Intent       = result.Intent,
                    IsError      = true,
                    ErrorMessage = "No chat provider configured for RealtimeAssist.",
                    At           = DateTimeOffset.UtcNow
                });
                return;
            }

            var systemPrompt = BuildSystemPrompt(result.Intent);
            var userMessage  = BuildUserMessage(result.Intent, recentText);

            _logger.LogDebug(
                "[Assistant] Sending request. Provider={Provider} Model={Model} Intent={Intent}",
                chat.ProviderId, chat.ModelId, result.Intent);

            var request = new ChatRequest
            {
                SystemPrompt = systemPrompt,
                Messages     = [new ChatMessage { Role = ChatRole.User, Content = userMessage }],
                Temperature  = 0.4f,
                MaxTokens    = 200
            };

            ChatResponse response;
            try
            {
                response = await chat.CompleteAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("[Assistant] Reaction cancelled (superseded by newer intent).");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Assistant] Chat model error.");
                Publish(new AssistantReactionSnapshot
                {
                    WakePhrase   = result.WakePhrase,
                    Intent       = result.Intent,
                    IsError      = true,
                    ErrorMessage = ex.Message,
                    At           = DateTimeOffset.UtcNow
                });
                return;
            }

            if (response.IsError)
            {
                if (IsExpectedCancellation(response.ErrorMessage, ct))
                {
                    _logger.LogDebug("[Assistant] Reaction cancelled by provider response (superseded request).");
                    return;
                }

                _logger.LogError(
                    "[Assistant] Chat provider returned error. Detail: {Msg}", response.ErrorMessage);
                Publish(new AssistantReactionSnapshot
                {
                    WakePhrase   = result.WakePhrase,
                    Intent       = result.Intent,
                    IsError      = true,
                    ErrorMessage = response.ErrorMessage,
                    At           = DateTimeOffset.UtcNow
                });
                return;
            }

            var suggestion = response.Content.Trim();
            _logger.LogInformation(
                "[Assistant] Reaction complete. Intent={Intent} WakePhrase='{Wake}' " +
                "Provider={Provider} Model={Model} Length={Len} Preview='{Preview}'",
                result.Intent, result.WakePhrase,
                chat.ProviderId, chat.ModelId,
                suggestion.Length,
                suggestion.Length > 100 ? suggestion[..100] + "…" : suggestion);

            Publish(new AssistantReactionSnapshot
            {
                WakePhrase  = result.WakePhrase,
                Intent      = result.Intent,
                Suggestion  = suggestion,
                At          = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(AssistantReactionSnapshot snap)
    {
        Current = snap;
        ReactionChanged?.Invoke(this, snap);
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    private static string BuildSystemPrompt(DetectedIntent intent) => intent switch
    {
        DetectedIntent.SuggestReply =>
            "You are a real-time conversation assistant. " +
            "The user is in a live conversation and needs a suggested reply. " +
            "Give a concise, natural reply in the same language as the conversation. " +
            "Keep it to 1-3 sentences. Do not explain yourself — just give the reply.",

        DetectedIntent.HowToRespond =>
            "You are a real-time conversation assistant. " +
            "The user wants guidance on how to respond in their current conversation. " +
            "Give a brief, practical suggestion in the same language as the conversation.",

        DetectedIntent.ExplainContext =>
            "You are a real-time context assistant. " +
            "The user wants you to explain or summarise what is currently being discussed. " +
            "Give a short, clear summary in the same language as the conversation.",

        DetectedIntent.QuestionForAssistant =>
            "You are Argus, a real-time desktop assistant. " +
            "The user asked a spoken question in the provided transcript. " +
            "Answer briefly, practically, and only from the provided transcript text. " +
            "If the transcript is unclear, say briefly that you are not sure.",

        _ =>
            "You are Argus, a helpful real-time AI assistant. " +
            "The user has addressed you in the provided transcript. " +
            "Give a brief, helpful response using only that transcript context. " +
            "Keep it to 1-3 sentences."
    };

    private static string BuildUserMessage(DetectedIntent intent, string recentText)
    {
        var context = recentText.Trim();
        if (context.Length == 0) context = "(no recent transcript available)";

        return intent switch
        {
            DetectedIntent.SuggestReply =>
                $"Recent conversation:\n\"{context}\"\n\nWhat should I say in reply?",

            DetectedIntent.HowToRespond =>
                $"Recent conversation:\n\"{context}\"\n\nHow should I respond?",

            DetectedIntent.ExplainContext =>
                $"Recent conversation:\n\"{context}\"\n\nWhat is being discussed here?",

            DetectedIntent.QuestionForAssistant =>
                $"Recent conversation:\n\"{context}\"\n\nAnswer the user's question directly and briefly. Do not assume facts not present in the transcript.",

            _ =>
                $"Recent conversation:\n\"{context}\"\n\nHow can you help me right now?"
        };
    }

    private static string BuildQuestionFingerprint(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lowered = text.ToLowerInvariant();
        var stripped = StripQuestionFingerprintNoise.Replace(lowered, " ");
        var collapsed = CollapseSpaces.Replace(stripped, " ").Trim();
        return collapsed.Length <= 160 ? collapsed : collapsed[..160];
    }

    private static bool IsExpectedCancellation(string? message, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return true;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("operation was cancelled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("task was cancelled", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        var cts = Interlocked.Exchange(ref _currentCts, null);
        if (cts is not null)
        {
            await Task.Run(() => { try { cts.Cancel(); cts.Dispose(); } catch { } })
                .ConfigureAwait(false);
        }
    }
}
