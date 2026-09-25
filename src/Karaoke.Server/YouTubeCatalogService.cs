using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class YouTubeCatalogService(YtDlpService ytDlp)
{
    public async Task<IReadOnlyList<SpotifyTrackDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var output = await ytDlp.RunForOutputAsync(
        [
            "--dump-single-json", "--flat-playlist", "--no-warnings", "--playlist-end", "10",
            $"ytsearch10:{query.Trim()}"
        ], cancellationToken);
        using var document = JsonDocument.Parse(output);
        return ParseSearch(document.RootElement);
    }

    internal static IReadOnlyList<SpotifyTrackDto> ParseSearch(JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];
        var tracks = new List<SpotifyTrackDto>();
        foreach (var entry in entries.EnumerateArray())
        {
            var id = Text(entry, "id");
            var title = Text(entry, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            if (id.Length != 11 || id.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')) continue;
            var durationSeconds = Number(entry, "duration");
            if (durationSeconds is <= 0 or > 60 * 60 * 2) continue;
            var url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
            var artist = Text(entry, "channel") ?? Text(entry, "uploader") ?? "YouTube";
            var durationMilliseconds = durationSeconds is null
                ? 0
                : (int)Math.Min(int.MaxValue, Math.Round(durationSeconds.Value * 1000));
            tracks.Add(new SpotifyTrackDto(
                "youtube:" + id, "youtube:" + id, title, artist, "YouTube",
                Text(entry, "thumbnail"), durationMilliseconds, false,
                Source: AudioCatalogSource.YouTube, SourceUrl: url,
                DownloadSource: AudioDownloadSource.YouTube));
        }
        return tracks;
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static double? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var number) ? number : null;
}
