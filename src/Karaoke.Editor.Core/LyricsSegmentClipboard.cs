using System.Text.Json;
using System.Text.Json.Serialization;

namespace Karaoke.Editor.Core;

public sealed record LyricsSegmentClipboardPayload(
    string Format,
    int Version,
    LyricSegmentType SegmentType,
    IReadOnlyList<LyricsClipboardSegment> Segments);

public sealed record LyricsClipboardSegment(
    LyricSegmentType Type,
    long StartOffsetTicks,
    long EndOffsetTicks,
    string Text,
    double? Confidence,
    int? HoldAfterMilliseconds,
    StageLineEffect StageEffect,
    IReadOnlyList<LyricsClipboardSegment> Children);

public static class LyricsSegmentClipboard
{
    public const string ClipboardFormat = "neon-stage/lyrics-segments";
    private const string TextPrefix = "NEON_STAGE_LYRICS_CLIPBOARD_V1\n";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<LyricSegment> NormalizeSelection(IEnumerable<LyricSegment> selection)
    {
        var selected = selection.Distinct().ToList();
        return selected.Where(segment => !selected.Any(parent => !ReferenceEquals(parent, segment) &&
                parent.DescendantsAndSelf().Contains(segment)))
            .OrderBy(segment => segment.Start).ThenBy(segment => segment.End).ToList();
    }

    public static LyricsSegmentClipboardPayload Create(IEnumerable<LyricSegment> selection)
    {
        var roots = NormalizeSelection(selection);
        if (roots.Count == 0)
            throw new InvalidOperationException("Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen.");
        if (roots.Any(segment => segment.Type is not
                (LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable)))
            throw new InvalidOperationException("Die Auswahl enthält kein kopierbares Lyrics-Segment.");
        if (roots.Any(segment => segment.Type != roots[0].Type))
            throw new InvalidOperationException("Bitte nur Segmente derselben Ebene gemeinsam kopieren.");

        var origin = roots.Min(segment => segment.Start);
        return new LyricsSegmentClipboardPayload(ClipboardFormat, 1, roots[0].Type,
            roots.Select(segment => Capture(segment, origin)).ToList());
    }

    public static string Serialize(LyricsSegmentClipboardPayload payload) =>
        TextPrefix + JsonSerializer.Serialize(payload, JsonOptions);

    public static LyricsSegmentClipboardPayload Deserialize(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(TextPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Die Zwischenablage enthält keine Neon-Stage-Lyrics-Segmente.");
        LyricsSegmentClipboardPayload? payload;
        try { payload = JsonSerializer.Deserialize<LyricsSegmentClipboardPayload>(text[TextPrefix.Length..], JsonOptions); }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Der Lyrics-Inhalt der Zwischenablage ist beschädigt.", exception);
        }
        if (payload is null || payload.Format != ClipboardFormat || payload.Version != 1 || payload.Segments.Count == 0 ||
            payload.Segments.Any(segment => segment.Type != payload.SegmentType))
            throw new InvalidOperationException("Der Lyrics-Inhalt der Zwischenablage wird nicht unterstützt.");
        Validate(payload.Segments, payload.SegmentType, null);
        return payload;
    }

    public static IReadOnlyList<LyricSegment> Instantiate(LyricsSegmentClipboardPayload payload,
        TimeSpan anchor, Guid? parentId)
    {
        if (anchor < TimeSpan.Zero)
            throw new InvalidOperationException("Der Einfügecursor darf nicht vor dem Song liegen.");
        return payload.Segments.Select(segment => CreateSegment(segment, anchor, parentId)).ToList();
    }

    private static LyricsClipboardSegment Capture(LyricSegment segment, TimeSpan origin) => new(
        segment.Type,
        (segment.Start - origin).Ticks,
        (segment.End - origin).Ticks,
        segment.Text,
        segment.Confidence,
        segment.HoldAfterMilliseconds,
        segment.StageEffect,
        segment.Children.Select(child => Capture(child, origin)).ToList());

    private static LyricSegment CreateSegment(LyricsClipboardSegment source, TimeSpan anchor, Guid? parentId)
    {
        var id = Guid.CreateVersion7();
        var start = anchor + TimeSpan.FromTicks(source.StartOffsetTicks);
        var end = anchor + TimeSpan.FromTicks(source.EndOffsetTicks);
        var created = new LyricSegment
        {
            Id = id,
            ParentId = parentId,
            Type = source.Type,
            Start = start,
            End = end,
            Text = source.Text,
            Origin = SegmentOrigin.ManuallyCreated,
            Confidence = source.Confidence,
            IsManuallyAdjusted = true,
            RequiresReview = true,
            OriginalStart = start,
            OriginalEnd = end,
            OriginalText = source.Text,
            HoldAfterMilliseconds = source.HoldAfterMilliseconds,
            StageEffect = source.StageEffect
        };
        foreach (var child in source.Children)
            created.Children.Add(CreateSegment(child, anchor, id));
        return created;
    }

    private static void Validate(IEnumerable<LyricsClipboardSegment> segments, LyricSegmentType expectedType,
        (long Start, long End)? parentBounds)
    {
        var ordered = segments.OrderBy(segment => segment.StartOffsetTicks)
            .ThenBy(segment => segment.EndOffsetTicks).ToList();
        LyricsClipboardSegment? previous = null;
        foreach (var segment in ordered)
        {
            if (segment.Type != expectedType || segment.EndOffsetTicks <= segment.StartOffsetTicks ||
                segment.StartOffsetTicks < 0 || string.IsNullOrWhiteSpace(segment.Text))
                throw new InvalidOperationException("Der Lyrics-Inhalt der Zwischenablage ist ungültig.");
            if (parentBounds is { } bounds &&
                (segment.StartOffsetTicks < bounds.Start || segment.EndOffsetTicks > bounds.End))
                throw new InvalidOperationException("Der Lyrics-Inhalt der Zwischenablage ist ungültig.");
            if (previous is not null && segment.StartOffsetTicks < previous.EndOffsetTicks)
                throw new InvalidOperationException("Der Lyrics-Inhalt der Zwischenablage enthält überlappende Segmente.");
            var childType = expectedType switch
            {
                LyricSegmentType.Line => LyricSegmentType.Word,
                LyricSegmentType.Word => LyricSegmentType.Syllable,
                LyricSegmentType.Syllable => LyricSegmentType.Phoneme,
                _ => LyricSegmentType.Phoneme
            };
            if (expectedType == LyricSegmentType.Phoneme && segment.Children.Count > 0)
                throw new InvalidOperationException("Der Lyrics-Inhalt der Zwischenablage ist ungültig.");
            if (segment.Children.Count > 0)
                Validate(segment.Children, childType, (segment.StartOffsetTicks, segment.EndOffsetTicks));
            previous = segment;
        }
    }
}
