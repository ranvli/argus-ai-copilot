using Argus.Core.Domain.Entities;
using Argus.Transcription.Text;
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
    private sealed record QuestionMatch(string Detected, string TriggerText);

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
        "argus que hago",
        "argus qué hago",
        "que hago ahora",
        "qué hago ahora",
        "que le respondo",
        "qué le respondo",
        "que le respondo ahora",
        "qué le respondo ahora",
        "que digo",
        "qué digo",
        "que hago",
        "qué hago",
        "que respondo",
        "qué respondo",
        "como respondo",
        "cómo respondo",
        "me recomiendas",
        "que deberia hacer",
        "qué debería hacer",
        "por que pasa",
        "por qué pasa",
        "what should i say",
        "what should i do",
        "what do i do",
        "what do i say",
        "how should i respond",
        "how do i respond",
        "can you help me",
        "could you help me",
        "should i respond",
        "can i respond",
    ];

    // Strip punctuation but keep spaces and alphanumeric (including accented chars)
    private static readonly Regex StripPunctuation =
        new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    private static readonly Regex CollapseSpaces =
        new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex SentencePattern =
        new(@"[^.!?\r\n]+(?:[.!?]|$)", RegexOptions.Compiled);

    public IntentDetectionService(ILogger<IntentDetectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks the newest transcript segment(s) for wake phrases and help patterns.
    /// Returns <see cref="IntentDetectionResult.None"/> if nothing actionable is found.
    /// </summary>
    public IntentDetectionResult Detect(IReadOnlyList<TranscriptSegment> recentSegments)
    {
        var candidates = BuildCandidates(recentSegments);
        if (candidates.Count == 0)
            return IntentDetectionResult.None;

        foreach (var candidate in candidates)
        {
            var normalised = Normalise(candidate);
            var folded = FoldText(normalised);
            if (IsTooShortOrNoisy(folded))
                continue;

            var questionMatch = FindQuestionPhrase(candidate, normalised, folded);
            if (questionMatch is not null)
            {
                _logger.LogInformation(
                    "[QuestionIntent] detected={Detected} text={Preview}",
                    questionMatch.Detected,
                    Truncate(questionMatch.TriggerText, 120));

                return new IntentDetectionResult(
                    DetectedIntent.QuestionForAssistant,
                    questionMatch.Detected,
                    candidate,
                    questionMatch.TriggerText);
            }

            // 1. Check for help/advice phrases first (more specific → higher priority)
            foreach (var (phrase, intent) in HelpPhrases)
            {
                if (ContainsPhrase(normalised, phrase))
                {
                    _logger.LogInformation(
                        "[Intent] Help phrase detected. Phrase='{Phrase}' Intent={Intent} Text='{Text}'",
                        phrase, intent, Truncate(candidate, 120));

                    return new IntentDetectionResult(intent, phrase, candidate, candidate);
                }
            }

            // 2. Check for wake / address phrases
            foreach (var wake in WakePhrases)
            {
                if (ContainsPhrase(normalised, wake))
                {
                    _logger.LogInformation(
                        "[Intent] Wake phrase detected. Phrase='{Phrase}' Text='{Text}'",
                        wake, Truncate(candidate, 120));

                    return new IntentDetectionResult(DetectedIntent.WakeWord, wake, candidate, candidate);
                }
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

    private static IReadOnlyList<string> BuildCandidates(IReadOnlyList<TranscriptSegment> recentSegments)
    {
        if (recentSegments.Count == 0)
            return [];

        var texts = recentSegments
            .Select(segment => segment.Text?.Trim())
            .Where(static text => TranscriptTextFilter.IsMeaningfulText(text))
            .TakeLast(2)
            .Cast<string>()
            .ToList();

        if (texts.Count == 0)
            return [];

        var candidates = new List<string>(3) { texts[^1] };

        if (texts.Count > 1)
        {
            candidates.Add(string.Join(" ", texts));
            candidates.Add(texts[^2]);
        }

        return candidates;
    }

    private static QuestionMatch? FindQuestionPhrase(string originalText, string normalised, string folded)
    {
        foreach (var phrase in QuestionPhrases)
        {
            var foldedPhrase = FoldText(Normalise(phrase));
            if (ContainsPhrase(folded, foldedPhrase))
            {
                var triggerText = FindMatchingSentence(
                    originalText,
                    sentence => ContainsPhrase(FoldText(Normalise(sentence)), foldedPhrase))
                    ?? CollapseWhitespace(originalText);

                return new QuestionMatch(phrase, triggerText);
            }
        }

        return null;
    }

    private static string? FindMatchingSentence(string originalText, Func<string, bool> predicate)
    {
        var matches = SentencePattern.Matches(originalText);

        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var sentence = CollapseWhitespace(matches[index].Value.Trim());
            if (sentence.Length > 0 && predicate(sentence))
                return sentence;
        }

        var collapsed = CollapseWhitespace(originalText);
        return collapsed.Length > 0 && predicate(collapsed)
            ? collapsed
            : null;
    }

    private static string CollapseWhitespace(string text)
        => CollapseSpaces.Replace(text, " ").Trim();

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
