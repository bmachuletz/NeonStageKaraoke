using System.Globalization;
using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

internal static class EditorLyricsRuntimeMapper
{
    public static LyricsDto Map(LyricsDto fallback, string documentJson,
        MusicalHighlightSettingsDto? musicalHighlight = null)
    {
        using var document = JsonDocument.Parse(documentJson);
        if (!document.RootElement.TryGetProperty("lines", out var sourceLines) || sourceLines.ValueKind != JsonValueKind.Array)
            return fallback;

        var lines = sourceLines.EnumerateArray().Select((line, lineIndex) =>
        {
            var words = Children(line, "Word").Select((word, wordIndex) =>
            {
                var syllables = Children(word, "Syllable").Select((syllable, syllableIndex) =>
                    new LyricsSyllableDto(Time(syllable, "start"), Text(syllable), TimeOrNull(syllable, "end"),
                        syllableIndex, Number(syllable, "confidence"),
                        Boolean(syllable, "karaokeTimingLocked"), Notes(syllable))).ToArray();
                return new LyricsWordDto(Time(word, "start"), Text(word), TimeOrNull(word, "end"), wordIndex,
                    syllables.Length == 0 ? null : syllables, Number(word, "confidence"),
                    Boolean(word, "karaokeTimingLocked"));
            }).ToArray();

            var runtimeText = words.Length == 0 ? Text(line) : string.Join(" ", words.Select(word => word.Text));
            return new LyricsLineDto(Time(line, "start"), runtimeText, TimeOrNull(line, "end"), lineIndex,
                words.Length == 0 ? null : words, IntegerOrNull(line, "holdAfterMilliseconds"),
                StringOrNull(line, "stageEffect"), IntegerOrNull(line, "voiceLane") ?? 0,
                StringOrNull(line, "voiceLabel"), Boolean(line, "karaokeTimingLocked"));
        }).ToArray();

        var ultraStarHeritage = Boolean(document.RootElement, "hasUltraStarTimingHeritage") ||
            StringOrNull(document.RootElement, "analysisRunId")?.StartsWith("usdb:",
                StringComparison.OrdinalIgnoreCase) == true ||
            StringOrNull(document.RootElement, "modelVersion")?.Contains("UltraStar",
                StringComparison.OrdinalIgnoreCase) == true ||
            sourceLines.EnumerateArray().SelectMany(line => DescendantsAndSelf(line))
                .Any(segment => StringOrNull(segment, "origin") == "ImportedFromUltraStar");
        var karaokeColors = KaraokeColors(document.RootElement, fallback.KaraokeColors);

        return lines.Length == 0 ? fallback : fallback with
        {
            Lines = lines,
            KaraokeColors = karaokeColors,
            HasUltraStarTimingHeritage = fallback.HasUltraStarTimingHeritage || ultraStarHeritage,
            MusicalHighlight = musicalHighlight is { Enabled: true } && !ultraStarHeritage
                ? new(true, musicalHighlight.TimelineVersion)
                : new(false, musicalHighlight?.TimelineVersion ?? 1)
        };
    }

    private static KaraokeColorSettingsDto KaraokeColors(JsonElement root, KaraokeColorSettingsDto? fallback)
    {
        var defaults = fallback ?? new KaraokeColorSettingsDto();
        if (!root.TryGetProperty("karaokeColors", out var colors) || colors.ValueKind != JsonValueKind.Object)
            return defaults;
        return new KaraokeColorSettingsDto(
            HtmlColorOrDefault(StringOrNull(colors, "unsungColor"), defaults.UnsungColor),
            HtmlColorOrDefault(StringOrNull(colors, "sungColor"), defaults.SungColor),
            HtmlColorOrDefault(StringOrNull(colors, "glowColor"), defaults.GlowColor));
    }

    private static string HtmlColorOrDefault(string? value, string fallback)
    {
        var candidate = value?.Trim();
        if (candidate is null || candidate.Length != 7 || candidate[0] != '#' ||
            !candidate.Skip(1).All(Uri.IsHexDigit)) return fallback;
        return candidate.ToUpperInvariant();
    }

    private static IEnumerable<JsonElement> Children(JsonElement parent, string type) =>
        parent.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            ? children.EnumerateArray().Where(child => StringOrNull(child, "type") == type)
            : [];

    private static IEnumerable<JsonElement> DescendantsAndSelf(JsonElement element)
    {
        yield return element;
        if (!element.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
    }

    private static string Text(JsonElement element) => StringOrNull(element, "text") ?? string.Empty;

    private static TimeSpan Time(JsonElement element, string property) =>
        TimeOrNull(element, property) ?? TimeSpan.Zero;

    private static TimeSpan? TimeOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntegerOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed : null;

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed : 0;

    private static IReadOnlyList<LyricsNoteEvidenceDto>? Notes(JsonElement syllable)
    {
        if (!syllable.TryGetProperty("notes", out var source) || source.ValueKind != JsonValueKind.Array ||
            source.GetArrayLength() == 0) return null;
        var result = new List<LyricsNoteEvidenceDto>();
        foreach (var note in source.EnumerateArray())
        {
            var start = TimeOrNull(note, "start");
            var end = TimeOrNull(note, "end");
            if (start is null || end is null || end <= start) continue;
            var confidence = Number(note, "confidence");
            if (confidence <= 0) confidence = Number(note, "amplitude");
            result.Add(new(start.Value, end.Value, (int)Number(note, "midi"), confidence));
        }
        return result.Count == 0 ? null : result;
    }

    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;
}
