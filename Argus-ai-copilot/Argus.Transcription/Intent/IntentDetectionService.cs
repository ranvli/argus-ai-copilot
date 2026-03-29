using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Argus.Transcription.Intent;

/// <summary>
/// Rule-based intent detector that inspects recent transcript text for
/// wake phrases and help-request patterns.
///
/// Normalisation:
///   - Converts to lower-case
///   - Strips punctuation
///   - Collapses whitespace
///   - Handles common Whisper mis-transcriptions for "argus" (argas, argos, arkus)
///
/// All phrase lists are matched as whole-word substrings so partial matches
/// (e.g. "margareth") don't fire.
/// </summary>
public sealed class IntentDetectionService
{
    private readonly ILogger<IntentDetectionService> _logger;

    // ── Wake / address phrases ────────────────────────────────────────────────
    // Sorted longest-first so the most specific match wins.
    private static readonly string[] WakePhrases =
    [
        "oye argus",
        "hey argus",
        "hola argus",
        "ok argus",
        "okay argus",
        "argus",
        "argas",     // common Whisper mis-transcription (Spanish accent)
        "argos",
        "arkus",
    ];

    // ── Help / advice phrases ─────────────────────────────────────────────────
    private static readonly (string Phrase, DetectedIntent Intent)[] HelpPhrases =
    [
        ("que le digo",       DetectedIntent.SuggestReply),
        ("qué le digo",       DetectedIntent.SuggestReply),
        ("que respondo",      DetectedIntent.HowToRespond),
        ("qué respondo",      DetectedIntent.HowToRespond),
        ("como respondo",     DetectedIntent.HowToRespond),
        ("cómo respondo",     DetectedIntent.HowToRespond),
        ("que sugieres",      DetectedIntent.SuggestReply),
        ("qué sugieres",      DetectedIntent.SuggestReply),
        ("mira esto",         DetectedIntent.ExplainContext),
        ("what should i say", DetectedIntent.SuggestReply),
        ("what do i say",     DetectedIntent.SuggestReply),
        ("what do i respond", DetectedIntent.HowToRespond),
        ("help me respond",   DetectedIntent.HowToRespond),
        ("suggest a reply",   DetectedIntent.SuggestReply),
        ("look at this",      DetectedIntent.ExplainContext),
        ("what do you think", DetectedIntent.GeneralHelp),
    ];

    private static readonly string[] QuestionPhrases =
    [
        "argus que le respondo",
        "argus qué le respondo",
        "que hago ahora",
        "qué hago ahora",
        "que le respondo",
        "qué le respondo",
        "que digo",
        "qué digo",
        "que hago",
        "qué hago",
        "que respondo",
        "qué respondo",
        "como respondo",
        "cómo respondo",
        "por que",
        "por qué",
        "cuando",
        "cuándo",
        "donde",
        "dónde",
        "cual",
        "cuál",
        "deberia",
        "debería",
        "puedo",
        "me recomiendas",
        "what should i say",
        "what should i do",
        "what do i say",
        "how should i respond",
        "how do i respond",
        "can you help me",
        "could i",
        "should i",
        "can i",
        "do i",
        "what",
        "why",
        "how",
        "when",
        "where",
        "who",
    ];

    // Strip punctuation but keep spaces and alphanumeric (including accented chars)
    private static readonly Regex StripPunctuation =
        new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    private static readonly Regex CollapseSpaces =
        new(@"\s+", RegexOptions.Compiled);

    public IntentDetectionService(ILogger<IntentDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks <paramref name="recentText"/> for wake phrases and help patterns.
    /// Returns <see cref="IntentDetectionResult.None"/> if nothing actionable is found.
    /// </summary>
    public IntentDetectionResult Detect(string recentText)
    {
        if (string.IsNullOrWhiteSpace(recentText))
            return IntentDetectionResult.None;

        var normalised = Normalise(recentText);
        var folded = FoldText(normalised);
        if (IsTooShortOrNoisy(folded))
            return IntentDetectionResult.None;

        var questionMatch = FindQuestionPhrase(recentText, normalised, folded);
        if (questionMatch is not null)
        {
            _logger.LogInformation(
                "[QuestionIntent] detected={Detected} text={Preview}",
                questionMatch,
                Truncate(recentText, 120));

            return new IntentDetectionResult(DetectedIntent.QuestionForAssistant, questionMatch, recentText);
        }

        // 1. Check for help/advice phrases first (more specific → higher priority)
        foreach (var (phrase, intent) in HelpPhrases)
        {
            if (ContainsPhrase(normalised, phrase))
            {
                _logger.LogInformation(
                    "[Intent] Help phrase detected. Phrase='{Phrase}' Intent={Intent} Text='{Text}'",
                    phrase, intent, Truncate(recentText, 120));

                return new IntentDetectionResult(intent, phrase, recentText);
            }
        }

        // 2. Check for wake / address phrases
        foreach (var wake in WakePhrases)
        {
            if (ContainsPhrase(normalised, wake))
            {
                _logger.LogInformation(
                    "[Intent] Wake phrase detected. Phrase='{Phrase}' Text='{Text}'",
                    wake, Truncate(recentText, 120));

                return new IntentDetectionResult(DetectedIntent.WakeWord, wake, recentText);
            }
        }

        return IntentDetectionResult.None;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Normalise(string text)
    {
        var lower   = text.ToLowerInvariant();
        var stripped = StripPunctuation.Replace(lower, " ");
        return CollapseSpaces.Replace(stripped, " ").Trim();
    }

    private static string FoldText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Whole-word / whole-phrase match: the phrase must appear as a complete
    /// token sequence (preceded and followed by a word boundary or string edge).
    /// </summary>
    private static bool ContainsPhrase(string normalisedText, string normalisedPhrase)
    {
        // For single tokens the built-in word boundary is enough.
        // For multi-word phrases we just check substring presence — since both
        // sides are already normalised the chance of false positives is very low.
        var idx = normalisedText.IndexOf(normalisedPhrase, StringComparison.Ordinal);
        if (idx < 0) return false;

        // Verify character boundaries (not mid-word)
        var before = idx == 0                                  || normalisedText[idx - 1] == ' ';
        var after  = idx + normalisedPhrase.Length >= normalisedText.Length
                     || normalisedText[idx + normalisedPhrase.Length] == ' ';

        return before && after;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? FindQuestionPhrase(string originalText, string normalised, string folded)
    {
        var hasQuestionPunctuation = originalText.Contains('?') || originalText.Contains('¿');

        foreach (var phrase in QuestionPhrases)
        {
            var foldedPhrase = FoldText(phrase);
            if (ContainsPhrase(folded, foldedPhrase))
                return phrase;
        }

        return hasQuestionPunctuation && folded.Length >= 8 && ContainsAnyLetter(folded)
            ? "question_punctuation"
            : null;
    }

    private static bool IsTooShortOrNoisy(string text)
    {
        if (text.Length < 8)
            return true;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
            return true;

        if (!ContainsAnyLetter(text))
            return true;

        var distinctWords = words.Distinct(StringComparer.Ordinal).Count();
        return distinctWords == 1 && words.Length <= 3;
    }

    private static bool ContainsAnyLetter(string text)
        => text.Any(char.IsLetter);
}
