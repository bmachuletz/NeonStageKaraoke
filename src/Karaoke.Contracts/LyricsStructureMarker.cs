using System.Text.RegularExpressions;

namespace Karaoke.Contracts;

/// <summary>Recognizes section headings that describe a lyric structure but are not sung text.</summary>
public static partial class LyricsStructureMarker
{
    public static bool IsMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var candidate = text.Trim().Trim('♪', '♫').Trim();
        var wasWrapped = false;
        if (candidate.Length >= 2 &&
            ((candidate[0] == '[' && candidate[^1] == ']') ||
             (candidate[0] == '(' && candidate[^1] == ')') ||
             (candidate[0] == '{' && candidate[^1] == '}')))
        {
            wasWrapped = true;
            candidate = candidate[1..^1].Trim();
        }
        var separator = candidate.IndexOf(':');
        if (wasWrapped && separator > 0)
            candidate = candidate[..separator].Trim();
        candidate = candidate.TrimEnd(':').Trim();
        return MarkerRegex().IsMatch(candidate);
    }

    [GeneratedRegex(
        @"^(?:(?:pre|post)[\s_-]*chorus|(?:vor|nach)[\s_-]*refrain|chorus|refrain|verse|strophe|couplet|part|teil|bridge|intro|outro|instrumental|interlude|zwischenspiel|hook|solo|breakdown|spoken|rap)(?:\s+(?:\d+|x\s*\d+|\d+\s*x|[ivxlcdm]+|one|two|three|four|five|eins|zwei|drei|vier|fünf))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();
}
