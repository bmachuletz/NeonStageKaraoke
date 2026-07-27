using System.Globalization;
using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

internal static class EditorLyricsRuntimeMapper
{
    public static LyricsDto Map(LyricsDto fallback, string documentJson)
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
                        syllableIndex, Number(syllable, "confidence"))).ToArray();
                return new LyricsWordDto(Time(word, "start"), Text(word), TimeOrNull(word, "end"), wordIndex,
                    syllables.Length == 0 ? null : syllables, Number(word, "confidence"));
            }).ToArray();

            var runtimeText = words.Length == 0 ? Text(line) : string.Join(" ", words.Select(word => word.Text));
            return new LyricsLineDto(Time(line, "start"), runtimeText, TimeOrNull(line, "end"), lineIndex,
                words.Length == 0 ? null : words, IntegerOrNull(line, "holdAfterMilliseconds"),
                StringOrNull(line, "stageEffect"));
        }).ToArray();

        return lines.Length == 0 ? fallback : fallback with { Lines = lines };
    }

    private static IEnumerable<JsonElement> Children(JsonElement parent, string type) =>
        parent.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array
            ? children.EnumerateArray().Where(child => StringOrNull(child, "type") == type)
            : [];

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
}
