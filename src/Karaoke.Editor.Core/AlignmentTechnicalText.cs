using System.Text.Json;

namespace Karaoke.Editor.Core;

/// <summary>
/// Attaches the reversible text representation used by the aligner to an
/// editor document. It is diagnostic only: Stage and LRC export continue to
/// use the human-facing segment text.
/// </summary>
public static class AlignmentTechnicalText
{
    public static int Attach(LyricsEditorDocument document, string? alignmentReportJson)
    {
        if (string.IsNullOrWhiteSpace(alignmentReportJson)) return 0;
        try
        {
            using var report = JsonDocument.Parse(alignmentReportJson);
            if (!report.RootElement.TryGetProperty("details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
                return 0;
            var attached = 0;
            var lineCount = Math.Min(document.Lines.Count, details.GetArrayLength());
            for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                var detail = details[lineIndex];
                var line = document.Lines[lineIndex];
                if (Read(detail, "technical_text") is { } lineText)
                {
                    line.TechnicalText = lineText;
                    attached++;
                }
                if (!detail.TryGetProperty("words", out var words) ||
                    words.ValueKind != JsonValueKind.Array)
                    continue;
                var wordCount = Math.Min(line.Children.Count, words.GetArrayLength());
                for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
                {
                    if (Read(words[wordIndex], "technical_text") is not { } wordText) continue;
                    line.Children[wordIndex].TechnicalText = wordText;
                    attached++;
                }
            }
            return attached;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? Read(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
