using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class AudioCatalogSearchService(SpotifyService spotify, QobuzCatalogService qobuz,
    YouTubeCatalogService youtube)
{
    public async Task<IReadOnlyList<SpotifyTrackDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var qobuzAvailable = await qobuz.IsAvailableAsync(cancellationToken);
        var results = new List<SpotifyTrackDto>();
        var errors = new List<string>();
        var needsYouTubeFallback = NeedsYouTubeFallback(spotify.Configured, null, false);

        if (spotify.Configured)
        {
            try
            {
                var spotifyResults = await spotify.SearchAsync(query, cancellationToken);
                results.AddRange(spotifyResults.Select(track => track with
                    { DownloadSource = AudioDownloadSource.YouTube }));
                needsYouTubeFallback = NeedsYouTubeFallback(true, spotifyResults.Count, false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"Spotify: {exception.Message}");
                needsYouTubeFallback = NeedsYouTubeFallback(true, null, true);
            }
        }

        if (qobuzAvailable)
        {
            try { results.AddRange(await qobuz.SearchAsync(query, cancellationToken)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"Qobuz: {exception.Message}");
            }
        }

        // YouTube ist der echte Fallback, wenn Spotify nicht eingerichtet ist,
        // ausfällt oder für die Suchanfrage keinen Treffer liefert.
        if (needsYouTubeFallback)
        {
            try { results.AddRange(await youtube.SearchAsync(query, cancellationToken)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"YouTube: {exception.Message}");
            }
        }
        if (results.Count == 0 && errors.Count > 0)
            throw new InvalidOperationException(string.Join(" · ", errors));
        return Deduplicate(results)
            .OrderBy(track => track.Source)
            .ThenBy(track => track.Artist)
            .ThenBy(track => track.Title)
            .ToArray();
    }

    internal static IEnumerable<SpotifyTrackDto> Deduplicate(IEnumerable<SpotifyTrackDto> tracks) =>
        tracks.DistinctBy(track => (track.Source, Normalize(track.Artist), Normalize(track.Title)));

    internal static bool NeedsYouTubeFallback(bool spotifyConfigured, int? spotifyResultCount,
        bool spotifyFailed) => !spotifyConfigured || spotifyFailed || spotifyResultCount == 0;

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant));
}
