using Argus.Core.Domain.Entities;

namespace Argus.Transcription.Text;

public static class TranscriptTextFilter
{
    public static bool IsMeaningfulText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("(speaking in foreign language)", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[inaudible]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Length < 4)
            return false;

        var letters = trimmed.Count(char.IsLetter);
        if (letters < 3)
            return false;

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1 && trimmed.Length < 6)
            return false;

        return true;
    }

    public static List<TranscriptSegment> FilterMeaningfulSegments(IReadOnlyList<TranscriptSegment> segments)
        => segments.Where(segment => IsMeaningfulText(segment.Text)).ToList();
}
