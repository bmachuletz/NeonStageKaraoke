namespace Karaoke.Server;

/// <summary>Prefers authenticated usdb.animux.de and fails open to usdb.eu.</summary>
public sealed class PreferredUsdbClient(
    AnimuxUsdbClient animux,
    UsdbClient eu,
    ILogger<PreferredUsdbClient> logger) : IUsdbClient
{
    public async Task<IReadOnlyList<UsdbSearchCandidate>> SearchAsync(string title, string artist,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UsdbSearchCandidate> preferred = [];
        if (animux.IsConfigured)
        {
            try
            {
                preferred = await animux.SearchAsync(title, artist, cancellationToken);
                if (preferred.Count == 0)
                    logger.LogInformation("USDB Animux returned no candidates; using usdb.eu results");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                                              InvalidOperationException)
            {
                logger.LogWarning(exception, "USDB Animux is unavailable; falling back to usdb.eu");
            }
        }
        try
        {
            var fallback = await eu.SearchAsync(title, artist, cancellationToken);
            return preferred.Concat(fallback)
                .DistinctBy(item => (item.Provider.ToLowerInvariant(), item.DetailUri.AbsoluteUri))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (preferred.Count > 0 &&
                                          exception is HttpRequestException or InvalidDataException or
                                              InvalidOperationException)
        {
            logger.LogWarning(exception, "usdb.eu is unavailable; keeping the USDB Animux results");
            return preferred;
        }
    }

    public Task<IReadOnlyList<UsdbVersionCandidate>> ResolveVersionsAsync(UsdbSearchCandidate candidate,
        CancellationToken cancellationToken) =>
        candidate.Provider.Equals(UsdbProviders.Animux, StringComparison.OrdinalIgnoreCase)
            ? animux.ResolveVersionsAsync(candidate, cancellationToken)
            : eu.ResolveVersionsAsync(candidate, cancellationToken);

    public Task<UsdbDownloadedLyrics> DownloadAsync(UsdbVersionCandidate version,
        CancellationToken cancellationToken) =>
        version.Provider.Equals(UsdbProviders.Animux, StringComparison.OrdinalIgnoreCase)
            ? animux.DownloadAsync(version, cancellationToken)
            : eu.DownloadAsync(version, cancellationToken);
}
