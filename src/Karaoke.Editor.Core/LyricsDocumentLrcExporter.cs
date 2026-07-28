using System.Globalization;
using System.Text;

namespace Karaoke.Editor.Core;

/// <summary>
/// Serializes an editor revision as enhanced LRC so alignment jobs can use the
/// reviewed text and timing windows instead of an old matcher file. Syllables
/// are intentionally rebuilt by the downstream aligner.
/// </summary>
public static class LyricsDocumentLrcExporter
{
    public static string ToEnhancedLrc(LyricsEditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var output = new StringBuilder();
        foreach (var line in document.Lines.OrderBy(item => item.Start))
        {
            output.Append('[').Append(Format(line.Start)).Append(']');
            var words = line.Children
                .Where(item => item.Type == LyricSegmentType.Word)
                .OrderBy(item => item.Start)
                .ToArray();
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

    private static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var minutes = (int)value.TotalMinutes;
        var seconds = value.Seconds + value.Milliseconds / 1000d;
        return $"{minutes:00}:{seconds.ToString("00.000", CultureInfo.InvariantCulture)}";
    }
}
