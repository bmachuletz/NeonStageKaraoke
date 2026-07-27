using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class AudioCatalogSearchService(SpotifyService spotify, QobuzCatalogService qobuz)
{
    public async Task<IReadOnlyList<SpotifyTrackDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var providers = new List<(string Name, Func<Task<IReadOnlyList<SpotifyTrackDto>>> Search)>();
        if (spotify.Configured) providers.Add(("Spotify", () => spotify.SearchAsync(query, cancellationToken)));
        if (await qobuz.IsAvailableAsync(cancellationToken))
            providers.Add(("Qobuz", () => qobuz.SearchAsync(query, cancellationToken)));
        if (providers.Count == 0)
            throw new InvalidOperationException("Weder Spotify noch Qobuz ist für die Katalogsuche konfiguriert.");

        var results = new List<SpotifyTrackDto>();
        var errors = new List<string>();
        foreach (var provider in providers)
        {
            try { results.AddRange(await provider.Search()); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{provider.Name}: {exception.Message}");
            }
        }
        if (results.Count == 0 && errors.Count == providers.Count)
            throw new InvalidOperationException(string.Join(" · ", errors));
        return results.OrderBy(track => track.Source).ThenBy(track => track.Artist).ThenBy(track => track.Title).ToArray();
    }
}
