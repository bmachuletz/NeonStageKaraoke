using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Karaoke.Editor.Core;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed partial class UsdbSongMatcher(IOptions<UsdbOptions> options)
{
    private readonly UsdbOptions _options = options.Value;

    public UsdbMatchAssessment Assess(LyricsSourceAudio audio, UsdbVersionCandidate version,
        UltraStarLyricsImport lyrics, AudioEdgeTiming? edgeTiming = null)
    {
        var candidateTitle = lyrics.Metadata.Title ?? version.Title;
        var candidateArtist = lyrics.Metadata.Artist ?? version.Artist;
        var titleSimilarity = Similarity(audio.Title, candidateTitle);
        var artistSimilarity = ArtistSimilarity(audio.Artist, candidateArtist);
        // #END describes the recording timeline. The final sung note does not:
        // songs commonly have a long instrumental outro. Fall back to the old
        // conservative comparison only for legacy charts without #END.
        var chartDuration = lyrics.Metadata.DeclaredEndMilliseconds is > 0
            ? TimeSpan.FromMilliseconds(lyrics.Metadata.DeclaredEndMilliseconds.Value)
            : lyrics.End;
        var fullDurationDelta = Math.Abs(audio.Duration.TotalSeconds - chartDuration.TotalSeconds);
        var audibleDurationDelta = edgeTiming is { Measured: true }
            ? Math.Abs(edgeTiming.AudibleDuration.TotalSeconds - chartDuration.TotalSeconds)
            : double.PositiveInfinity;
        var durationDelta = Math.Min(fullDurationDelta, audibleDurationDelta);
        var versionCompatible = VersionCompatible(audio.Title, candidateTitle);
        var yearCompatible = audio.Year is null || version.Year is null ||
                             Math.Abs(audio.Year.Value - version.Year.Value) <= 1;
        var durationCompatible = durationDelta <= _options.MaximumDurationDifferenceSeconds;
        var metadataCompatible = titleSimilarity >= _options.MinimumTitleSimilarity &&
                                 artistSimilarity >= _options.MinimumArtistSimilarity &&
                                 versionCompatible && yearCompatible;

        var durationScore = Math.Max(0, 1 - durationDelta /
            Math.Max(1, _options.MaximumDurationDifferenceSeconds));
        var score = .45 * titleSimilarity + .30 * artistSimilarity + .20 * durationScore +
                    .05 * (yearCompatible ? 1 : 0);
        var exact = titleSimilarity >= .98 && artistSimilarity >= .95 && durationDelta <= 2 &&
                    versionCompatible && yearCompatible;
        var confidence = exact ? UsdbMatchConfidence.Exact :
            metadataCompatible && durationCompatible && score >= .82 ? UsdbMatchConfidence.High :
            titleSimilarity >= .75 && artistSimilarity >= .70 ? UsdbMatchConfidence.Uncertain :
            UsdbMatchConfidence.Rejected;
        var reason = confidence switch
        {
            UsdbMatchConfidence.Exact => "metadata and duration identify the recording exactly",
            UsdbMatchConfidence.High => "metadata, version and duration identify a compatible recording",
            _ when !versionCompatible => "version markers (live/remix/edit/acoustic) are incompatible",
            _ when !yearCompatible => "release years are incompatible",
            _ when !durationCompatible => $"duration differs by {durationDelta:F1}s",
            _ => "title or artist similarity is too low"
        };
        return new(confidence, Math.Round(score * 100, 1), titleSimilarity, artistSimilarity,
            durationDelta, reason);
    }

    internal static double Similarity(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1d - previous[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    internal static double ArtistSimilarity(string left, string right)
    {
        var direct = Similarity(left, right);
        var leftParts = ArtistSplitRegex().Split(left).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var rightParts = ArtistSplitRegex().Split(right).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (leftParts.Length == 0 || rightParts.Length == 0) return direct;
        var component = leftParts.Select(a => rightParts.Max(b => Similarity(a, b))).Average();
        return Math.Max(direct, component);
    }

    internal static bool VersionCompatible(string left, string right)
    {
        var leftMarkers = VersionMarkerRegex().Matches(Normalize(left)).Select(match => match.Value).ToHashSet();
        var rightMarkers = VersionMarkerRegex().Matches(Normalize(right)).Select(match => match.Value).ToHashSet();
        return leftMarkers.SetEquals(rightMarkers) || leftMarkers.Count == 0 && rightMarkers.Count == 0;
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            output.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return WhitespaceRegex().Replace(output.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+(?:feat(?:uring)?\.?|ft\.?|x|&|and|und)\s+|[,;/]", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplitRegex();
    [GeneratedRegex(@"\b(?:live|acoustic|remix|radio edit|extended|instrumental|karaoke|clean|explicit|remaster(?:ed)?|album version|single version)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarkerRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
