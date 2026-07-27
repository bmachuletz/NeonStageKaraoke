using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class LyricsAvailabilityService(IHttpClientFactory clients)
{
    public async Task<SpotifyTrackDto> CheckAsync(SpotifyTrackDto track, CancellationToken cancellationToken)
    {
        try
        {
            var client = clients.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NeonStageKaraoke/1.0");
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return track;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return track with
            {
                HasSyncedLyrics = document.RootElement.EnumerateArray().Any(item =>
                    item.TryGetProperty("syncedLyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(lyrics.GetString()))
            };
        }
        catch { return track; }
    }
}
