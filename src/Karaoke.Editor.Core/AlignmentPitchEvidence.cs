using System.Text.Json;

namespace Karaoke.Editor.Core;

public sealed record PitchContourPoint(TimeSpan Time, double Midi, double Confidence);

public sealed record PitchNoteEvidence(
    TimeSpan Start,
    TimeSpan End,
    int Midi,
    double Amplitude,
    int? Line,
    string? NoteTrackId = null,
    string? SingerId = null,
    IReadOnlyList<PitchContourPoint>? Contour = null);

/// <summary>Reads the immutable Basic-Pitch note track from an alignment report.</summary>
public static class AlignmentPitchEvidence
{
    public static IReadOnlyList<PitchNoteEvidence> Parse(string? alignmentReportJson)
    {
        if (string.IsNullOrWhiteSpace(alignmentReportJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(alignmentReportJson);
            JsonElement events = default;
            var rawNotes = TryObject(document.RootElement, "basic_pitch_analysis", out var analysis) &&
                           analysis.TryGetProperty("notes", out events) &&
                           events.ValueKind == JsonValueKind.Array;
            if (!rawNotes && (!TryObject(document.RootElement, "basic_pitch_evidence", out var evidence) ||
                !TryObject(evidence, "pitch_timeline", out var timeline) ||
                !timeline.TryGetProperty("events", out events) ||
                events.ValueKind != JsonValueKind.Array))
                return [];
            var result = new List<PitchNoteEvidence>();
            foreach (var item in events.EnumerateArray())
            {
                if (!TryNumber(item, "start", out var start) ||
                    !TryNumber(item, "end", out var end) ||
                    !(TryInteger(item, "midi", out var midi) ||
                      TryInteger(item, "pitch", out midi)) || end <= start ||
                    start < 0 || midi is < 0 or > 127)
                    continue;
                var amplitude = (TryNumber(item, "confidence", out var measuredAmplitude) ||
                                 TryNumber(item, "amplitude", out measuredAmplitude))
                    ? Math.Clamp(measuredAmplitude, 0, 1) : 0;
                int? line = TryInteger(item, "line", out var lineNumber) && lineNumber > 0
                    ? lineNumber : null;
                var track = TryString(item, "track_id") ?? (rawNotes ? "basic-pitch" : null);
                var singer = TryString(item, "singer_id");
                var contour = ParseContour(item);
                result.Add(new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end),
                    midi, amplitude, line, track, singer, contour));
            }
            return result.OrderBy(item => item.Start).ThenBy(item => item.Midi).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static int AttachToSyllables(LyricsEditorDocument document,
        IReadOnlyList<PitchNoteEvidence> notes)
    {
        var attached = 0;
        foreach (var syllable in document.Segments.Where(segment =>
                     segment.Type == LyricSegmentType.Syllable))
        foreach (var note in notes)
        {
            if (note.End <= syllable.Start || note.Start >= syllable.End) continue;
            syllable.Notes.Add(note);
            attached++;
        }
        return attached;
    }

    private static IReadOnlyList<PitchContourPoint> ParseContour(JsonElement note)
    {
        if (!note.TryGetProperty("contour", out var contour) ||
            contour.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PitchContourPoint>();
        foreach (var point in contour.EnumerateArray())
            if (TryNumber(point, "time", out var time) &&
                TryNumber(point, "midi", out var midi) &&
                TryNumber(point, "confidence", out var confidence))
                result.Add(new(TimeSpan.FromSeconds(time), midi,
                    Math.Clamp(confidence, 0, 1)));
        return result;
    }

    private static bool TryObject(JsonElement source, string name, out JsonElement value)
    {
        value = default;
        return source.ValueKind == JsonValueKind.Object &&
               source.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryNumber(JsonElement source, string name, out double value)
    {
        value = 0;
        return source.ValueKind == JsonValueKind.Object &&
               source.TryGetProperty(name, out var item) && item.TryGetDouble(out value) &&
               double.IsFinite(value);
    }

    private static bool TryInteger(JsonElement source, string name, out int value)
    {
        value = 0;
        return source.ValueKind == JsonValueKind.Object &&
               source.TryGetProperty(name, out var item) && item.TryGetInt32(out value);
    }

    private static string? TryString(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;
}
