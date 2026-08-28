using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Karaoke.Editor.Core;

/// <summary>
/// Serializes an editor revision as enhanced LRC so alignment jobs can use the
/// reviewed text and timing windows instead of an old matcher file. Existing
/// syllable geometry is carried as Neon Stage metadata as a non-authoritative
/// reference; manual flags still decide which syllables are immutable.
/// </summary>
public static class LyricsDocumentLrcExporter
{
    public static string ToEnhancedLrc(LyricsEditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var output = new StringBuilder();
        var lines = document.Lines.OrderBy(item => item.Start).ToArray();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.VoiceLane > 0 || !string.IsNullOrWhiteSpace(line.VoiceLabel))
            {
                output.Append("[neon-voice:").Append(Math.Max(0, line.VoiceLane));
                if (!string.IsNullOrWhiteSpace(line.VoiceLabel))
                    output.Append(':').Append(Base64UrlEncode(
                        Encoding.UTF8.GetBytes(line.VoiceLabel.Trim())));
                output.AppendLine("]");
            }
            if (line.DescendantsAndSelf().Any(item => item.IsManuallyAdjusted))
            {
                output.Append("[neon-manual:")
                    .Append(line.Start.TotalSeconds.ToString("F7", CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(line.End.TotalSeconds.ToString("F7", CultureInfo.InvariantCulture))
                    .AppendLine("]");
            }
            var words = line.Children
                .Where(item => item.Type == LyricSegmentType.Word)
                .OrderBy(item => item.Start)
                .ToArray();
            for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                var syllables = words[wordIndex].Children
                    .Where(item => item.Type == LyricSegmentType.Syllable)
                    .OrderBy(item => item.Start)
                    .ToArray();
                if (syllables.Length == 0 && !words[wordIndex].IsManuallyAdjusted) continue;
                var payload = new EditorSyllablePayload(
                    lineIndex,
                    wordIndex,
                    words[wordIndex].Text,
                    words[wordIndex].Start.TotalSeconds,
                    words[wordIndex].End.TotalSeconds,
                    words[wordIndex].IsManuallyAdjusted,
                    syllables.Select(item => new EditorSyllable(
                        item.Text,
                        item.Start.TotalSeconds,
                        item.End.TotalSeconds,
                        item.IsManuallyAdjusted)).ToArray());
                output.Append("[neon-editor-syllables:")
                    .Append(Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload)))
                    .AppendLine("]");
            }
            // Metadata must occupy complete header lines. Writing it after the
            // LRC timestamp would turn the encoded payload into visible lyric
            // text and leave the real enhanced words without a line timestamp.
            output.Append('[').Append(Format(line.Start)).Append(']');
            if (words.Length == 0)
            {
                output.Append(line.Text.Trim());
            }
            else
            {
                for (var index = 0; index < words.Length; index++)
                {
                    if (index > 0) output.Append(' ');
                    output.Append('<').Append(Format(words[index].Start)).Append(',')
                        .Append(Format(words[index].End)).Append('>')
                        .Append(words[index].Text.Trim());
                }
            }
            output.AppendLine();
        }
        return output.ToString();
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record EditorSyllablePayload(
        int Line, int Word, string Text, double Start, double End,
        bool WordManuallyAdjusted,
        IReadOnlyList<EditorSyllable> Syllables);

    private sealed record EditorSyllable(
        string Text, double Start, double End, bool ManuallyAdjusted);

    private static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var minutes = (int)value.TotalMinutes;
        var seconds = value.Seconds + value.Milliseconds / 1000d;
        return $"{minutes:00}:{seconds.ToString("00.000", CultureInfo.InvariantCulture)}";
    }
}
