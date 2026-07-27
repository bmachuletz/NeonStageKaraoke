using System.Globalization;
using System.Text.RegularExpressions;
using Karaoke.Contracts;

namespace Karaoke.Server;

internal static partial class LrcParser
{
    public static LyricsDto Parse(Guid songId, IEnumerable<string> sourceLines, TimeSpan duration)
    {
        var source = sourceLines.ToArray();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offsetMilliseconds = 0;
        foreach (var sourceLine in source)
        {
            var match = MetadataRegex().Match(sourceLine.Trim());
            if (!match.Success) continue;
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();
            metadata[key] = value;
            if (key.Equals("offset", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out offsetMilliseconds);
        }

        var parsed = new List<(TimeSpan Start, string Text, IReadOnlyList<(TimeSpan Start, TimeSpan? End, string Text)> Words)>();
        foreach (var sourceLine in source)
        {
            var matches = TimestampRegex().Matches(sourceLine);
            if (matches.Count == 0) continue;
            var withoutLineTimestamps = TimestampRegex().Replace(sourceLine, string.Empty);
            var words = EnhancedWordRegex().Matches(withoutLineTimestamps)
                .Select(match => (
                    Start: ParseTimestamp(match.Groups["minutes"].Value, match.Groups["seconds"].Value, offsetMilliseconds),
                    End: match.Groups["endMinutes"].Success
                        ? ParseTimestamp(match.Groups["endMinutes"].Value, match.Groups["endSeconds"].Value, offsetMilliseconds)
                        : (TimeSpan?)null,
                    Text: match.Groups["text"].Value.Trim()))
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .ToArray();
            var text = words.Length > 0
                ? string.Join(' ', words.Select(word => word.Text))
                : EnhancedTimestampRegex().Replace(withoutLineTimestamps, string.Empty).Trim();
            foreach (Match match in matches)
            {
                var lineStart = ParseTimestamp(match.Groups["minutes"].Value, match.Groups["seconds"].Value, offsetMilliseconds);
                // Bei wortgenauen Lyrics ist die Erkennung des ersten Wortes genauer
                // als der ursprüngliche, nur satzweise LRC-Zeitstempel.
                var start = words.Length > 0 ? words[0].Start : lineStart;
                parsed.Add((start, text, words));
            }
        }

        var ordered = parsed
            .GroupBy(line => line.Start)
            .Select(group => (
                Start: group.Key,
                Text: string.Join("  ·  ", group.Select(line => line.Text).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal)),
                Words: group.SelectMany(line => line.Words).GroupBy(word => (word.Start, word.Text)).Select(word => word.First()).OrderBy(word => word.Start).ToArray()))
            .OrderBy(line => line.Start)
            .ToArray();
        var lines = new LyricsLineDto[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var end = index + 1 < ordered.Length ? ordered[index + 1].Start : duration;
            if (end <= ordered[index].Start)
                end = ordered[index].Start + TimeSpan.FromSeconds(2);
            var words = new LyricsWordDto[ordered[index].Words.Length];
            for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                var alignedEnd = ordered[index].Words[wordIndex].End;
                var wordEnd = alignedEnd is { } exact && exact >= ordered[index].Words[wordIndex].Start
                    ? exact
                    : wordIndex + 1 < words.Length
                        ? ordered[index].Words[wordIndex + 1].Start
                        : ordered[index].Words[wordIndex].Start + TimeSpan.FromSeconds(
                            Math.Clamp(ordered[index].Words[wordIndex].Text.Length / 7d, 0.35, 1.8));
                words[wordIndex] = new(ordered[index].Words[wordIndex].Start, ordered[index].Words[wordIndex].Text, wordEnd, wordIndex);
            }
            lines[index] = new(ordered[index].Start, ordered[index].Text, end, index, words);
        }

        return new(songId, lines, Get("ar"), Get("ti"), Get("al"), Get("by"), offsetMilliseconds);

        string? Get(string key) => metadata.TryGetValue(key, out var value) ? value : null;

        static TimeSpan ParseTimestamp(string minutesValue, string secondsValue, int offset)
        {
            var minutes = int.Parse(minutesValue, CultureInfo.InvariantCulture);
            var seconds = double.Parse(secondsValue.Replace(':', '.'), CultureInfo.InvariantCulture);
            var value = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(offset);
            return value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

    }

    [GeneratedRegex(@"\[(?<minutes>\d{1,3}):(?<seconds>\d{1,2}(?:[\.:]\d{1,3})?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[\.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex EnhancedTimestampRegex();

    [GeneratedRegex(@"<(?<minutes>\d{1,3}):(?<seconds>\d{1,2}(?:[\.:]\d{1,3})?)(?:,(?<endMinutes>\d{1,3}):(?<endSeconds>\d{1,2}(?:[\.:]\d{1,3})?))?>(?<text>[^<]*)", RegexOptions.CultureInvariant)]
    private static partial Regex EnhancedWordRegex();

    [GeneratedRegex(@"^\[(?<key>ar|ti|al|by|offset):(?<value>.*)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetadataRegex();
}
