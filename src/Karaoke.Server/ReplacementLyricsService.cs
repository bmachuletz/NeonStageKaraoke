using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

/// <summary>
/// Searches the configured online lyrics providers without changing a song.
/// Opaque, short-lived tokens keep provider payloads and download URLs on the server.
/// </summary>
internal sealed class ReplacementLyricsService(
    IUsdbClient usdb,
    IHttpClientFactory httpClientFactory,
    IOptions<GeniusOptions> geniusOptions,
    TimeProvider timeProvider,
    ILogger<ReplacementLyricsService> logger)
{
    private readonly ConcurrentDictionary<Guid, PendingSelection> _selections = new();

    public async Task<ReplacementLyricsSearchDto> SearchAsync(SongDto song, string? query,
        CancellationToken cancellationToken)
    {
        var effectiveQuery = string.IsNullOrWhiteSpace(query) ? song.Title : query.Trim();
        RemoveExpiredSelections();
        foreach (var item in _selections)
            if (item.Value.SongId == song.Id) _selections.TryRemove(item.Key, out _);

        var searches = await Task.WhenAll(
            SearchUsdbAsync(song, effectiveQuery, cancellationToken),
            SearchLrclibAsync(song, effectiveQuery, cancellationToken),
            SearchGeniusAsync(song, effectiveQuery, cancellationToken));
        var candidates = searches.SelectMany(item => item.Candidates).ToList();
        var statuses = searches.SelectMany(item => item.Statuses).ToList();

        var ordered = candidates
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
        var result = new List<ReplacementLyricsCandidateDto>(ordered.Length + 1);
        // EasyAligner's default text source is LRCLIB. USDB remains selectable
        // when its authored chart is intentionally preferred, but it must not
        // silently win merely because it sorts a fraction higher.
        var recommendedIndex = Array.FindIndex(ordered, candidate =>
            candidate.CanRetrieve && candidate.Source.Equals("LRCLIB", StringComparison.OrdinalIgnoreCase));
        if (recommendedIndex < 0)
            recommendedIndex = Array.FindIndex(ordered, candidate => candidate.CanRetrieve);
        for (var index = 0; index < ordered.Length; index++)
        {
            var candidate = ordered[index];
            var token = Guid.NewGuid();
            if (candidate.CanRetrieve)
                _selections[token] = new(song.Id, candidate.Source, candidate.SourceId,
                    candidate.Lyrics, candidate.UsdbVersion, candidate.HasSyncedLyrics,
                    $"{candidate.Title} · {candidate.Artist}", timeProvider.GetUtcNow().AddMinutes(15), false);
            result.Add(new(token, candidate.Source, candidate.SourceId, candidate.Title,
                candidate.Artist, candidate.Album, candidate.DurationSeconds, candidate.Language,
                candidate.Edition, candidate.Score, index == recommendedIndex,
                candidate.HasSyncedLyrics, candidate.CanRetrieve, candidate.ExternalUrl));
        }
        var transcriptToken = Guid.NewGuid();
        _selections[transcriptToken] = new(song.Id, "Volltranskript", "audio",
            null, null, false, $"{song.Title} · {song.Artist}",
            timeProvider.GetUtcNow().AddMinutes(15), true);
        result.Add(new(transcriptToken, "Volltranskript", "audio", song.Title, song.Artist,
            song.Album, song.DurationSeconds, null, "Direkt aus der Audiodatei", 0,
            recommendedIndex < 0, false, true, null, true));
        statuses.Add(new("Volltranskript", 1));
        logger.LogInformation("Replacement lyrics search for {SongId} returned {Count} candidate(s)",
            song.Id, result.Count);
        return new(effectiveQuery, result, statuses);
    }

    public async Task<RetrievedLyricsSelection?> RetrieveAsync(Guid songId, Guid selectionToken,
        CancellationToken cancellationToken)
    {
        if (!_selections.TryRemove(selectionToken, out var pending) || pending.SongId != songId ||
            pending.ExpiresAt <= timeProvider.GetUtcNow()) return null;
        if (pending.IsFullTranscript)
            return new(string.Empty, string.Empty, string.Empty, pending.Source,
                pending.SourceId, pending.Label, true);
        var lyrics = pending.Lyrics;
        var rawLyrics = lyrics;
        var rawExtension = pending.HasSyncedLyrics ? ".lrc" : ".txt";
        if (pending.UsdbVersion is not null)
        {
            var downloaded = await usdb.DownloadAsync(pending.UsdbVersion, cancellationToken);
            rawLyrics = downloaded.Content;
            rawExtension = ".txt";
            lyrics = downloaded.Parsed.ToEnhancedLrc();
        }
        if (string.IsNullOrWhiteSpace(lyrics))
            throw new InvalidDataException("Die ausgewählte Quelle enthält keinen verwendbaren Text.");
        return new(lyrics.Trim() + Environment.NewLine,
            (rawLyrics ?? lyrics).Trim() + Environment.NewLine, rawExtension,
            pending.Source, pending.SourceId, pending.Label, false);
    }

    /// <summary>
    /// Resolves the highest-scoring retrievable provider result without an
    /// interactive selection. Full transcription is deliberately excluded: a
    /// library re-alignment must keep running through EasyAligner and can fall
    /// back to the song's existing stable lyrics when no provider matches.
    /// </summary>
    public async Task<AutomaticLyricsSelection?> RetrieveBestMatchAsync(SongDto song,
        CancellationToken cancellationToken)
    {
        var search = await SearchAsync(song, null, cancellationToken);
        var best = search.Items
            .Where(item => item.CanRetrieve && !item.IsFullTranscript)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (best is null) return null;
        var retrieved = await RetrieveAsync(song.Id, best.SelectionToken, cancellationToken);
        return retrieved is null ? null : new(retrieved, best.Score);
    }

    private async Task<SourceSearchResult> SearchUsdbAsync(SongDto song, string query,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var statuses = new List<ReplacementLyricsSourceStatusDto>();
        try
        {
            var found = await usdb.SearchAsync(query, song.Artist, cancellationToken);
            foreach (var search in found)
            foreach (var version in await usdb.ResolveVersionsAsync(search, cancellationToken))
            {
                var title = UsdbSongMatcher.Similarity(song.Title, version.Title);
                var artist = UsdbSongMatcher.ArtistSimilarity(song.Artist, version.Artist);
                var compatible = UsdbSongMatcher.VersionCompatible(song.Title, version.Title);
                var score = Math.Round(100 * (title * .6 + artist * .4) * (compatible ? 1 : .65), 1);
                if (title < .55 || artist < .45) continue;
                candidates.Add(new(version.Provider, version.VersionId.ToString(CultureInfo.InvariantCulture),
                    version.Title, version.Artist, null, null, version.Language, version.Edition,
                    score, true, null, version));
            }
            foreach (var group in candidates.Where(item => item.UsdbVersion is not null)
                         .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase))
                statuses.Add(new(group.Key, group.Count()));
            if (!statuses.Any(item => item.Source.Contains("usdb", StringComparison.OrdinalIgnoreCase)))
                statuses.Add(new("USDB", 0));
            return new(candidates, statuses);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "USDB replacement lyrics search failed for {SongId}", song.Id);
            return new([], [new("USDB", 0, exception.Message)]);
        }
    }

    private async Task<SourceSearchResult> SearchLrclibAsync(SongDto song, string query,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        try
        {
            var client = httpClientFactory.CreateClient("Lrclib");
            var uri = "api/search?track_name=" + Uri.EscapeDataString(query) +
                      "&artist_name=" + Uri.EscapeDataString(song.Artist);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new([], [new("LRCLIB", 0)]);
            response.EnsureSuccessStatusCode();
            var tracks = await response.Content.ReadFromJsonAsync<List<LrclibCandidate>>(
                             cancellationToken: cancellationToken) ?? [];
            var count = 0;
            foreach (var track in tracks.Where(item => !item.Instrumental &&
                         (!string.IsNullOrWhiteSpace(item.SyncedLyrics) ||
                          !string.IsNullOrWhiteSpace(item.PlainLyrics))))
            {
                var score = ScoreLrclib(song, track);
                if (score < 45) continue;
                var duration = track.DurationSeconds;
                candidates.Add(new("LRCLIB", track.Id.ToString(CultureInfo.InvariantCulture),
                    track.TrackName, track.ArtistName, track.AlbumName, duration > 0 ? duration : null,
                    null, null, score, !string.IsNullOrWhiteSpace(track.SyncedLyrics),
                    track.SyncedLyrics ?? track.PlainLyrics, null));
                count++;
            }
            return new(candidates, [new("LRCLIB", count)]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "LRCLIB replacement lyrics search failed for {SongId}", song.Id);
            return new([], [new("LRCLIB", 0, exception.Message)]);
        }
    }

    private async Task<SourceSearchResult> SearchGeniusAsync(SongDto song, string query,
        CancellationToken cancellationToken)
    {
        var options = geniusOptions.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.AccessToken))
            return new([], [new("Genius", 0,
                "Nicht konfiguriert: Genius__AccessToken auf dem Server setzen.")]);
        try
        {
            var client = httpClientFactory.CreateClient("Genius");
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "search?q=" + Uri.EscapeDataString(song.Artist + " " + query));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", options.AccessToken.Trim());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var candidates = new List<Candidate>();
            if (payload.RootElement.TryGetProperty("response", out var body) &&
                body.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var hit in hits.EnumerateArray())
                {
                    if (!hit.TryGetProperty("result", out var result)) continue;
                    var id = result.TryGetProperty("id", out var idValue) ? idValue.ToString() : string.Empty;
                    var title = result.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
                    var url = result.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
                    var artist = result.TryGetProperty("primary_artist", out var primary) &&
                                 primary.TryGetProperty("name", out var artistValue)
                        ? artistValue.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) ||
                        string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(url)) continue;
                    var titleScore = UsdbSongMatcher.Similarity(song.Title, title);
                    var artistScore = UsdbSongMatcher.ArtistSimilarity(song.Artist, artist);
                    var score = Math.Round(100 * (titleScore * .6 + artistScore * .4), 1);
                    if (titleScore < .5 || artistScore < .4) continue;
                    candidates.Add(new("Genius", id, title, artist, null, null, null,
                        "Discovery · offizieller Link", score, false, null, null, false, url));
                }
            }
            return new(candidates, [new("Genius", candidates.Count,
                "Discovery only: Die offizielle API liefert keinen Lyrics-Text.")]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Genius discovery failed for {SongId}", song.Id);
            return new([], [new("Genius", 0, exception.Message)]);
        }
    }

    internal static double ScoreLrclib(SongDto song, LrclibCandidate track)
    {
        var title = UsdbSongMatcher.Similarity(song.Title, track.TrackName);
        var artist = UsdbSongMatcher.ArtistSimilarity(song.Artist, track.ArtistName);
        var album = string.IsNullOrWhiteSpace(song.Album) || string.IsNullOrWhiteSpace(track.AlbumName)
            ? .5 : UsdbSongMatcher.Similarity(song.Album, track.AlbumName);
        var duration = track.DurationSeconds;
        var durationScore = duration <= 0 ? .35 : Math.Max(0, 1 - Math.Abs(song.DurationSeconds - duration) / 15);
        var compatible = UsdbSongMatcher.VersionCompatible(song.Title, track.TrackName);
        return Math.Round(100 * (title * .42 + artist * .3 + album * .08 + durationScore * .2) *
                          (compatible ? 1 : .65), 1);
    }

    private void RemoveExpiredSelections()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _selections)
            if (item.Value.ExpiresAt <= now) _selections.TryRemove(item.Key, out _);
    }

    private sealed record PendingSelection(Guid SongId, string Source, string SourceId,
        string? Lyrics, UsdbVersionCandidate? UsdbVersion, bool HasSyncedLyrics,
        string Label, DateTimeOffset ExpiresAt, bool IsFullTranscript);
    private sealed record Candidate(string Source, string SourceId, string Title, string Artist,
        string? Album, double? DurationSeconds, string? Language, string? Edition, double Score,
        bool HasSyncedLyrics, string? Lyrics, UsdbVersionCandidate? UsdbVersion,
        bool CanRetrieve = true, string? ExternalUrl = null);
    private sealed record SourceSearchResult(
        IReadOnlyList<Candidate> Candidates,
        IReadOnlyList<ReplacementLyricsSourceStatusDto> Statuses);
}

internal sealed record RetrievedLyricsSelection(
    string Lyrics, string RawLyrics, string RawExtension,
    string Source, string SourceId, string Label, bool IsFullTranscript);

internal sealed record AutomaticLyricsSelection(RetrievedLyricsSelection Lyrics, double Score);

internal sealed record LrclibCandidate(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("trackName")] string TrackName,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("albumName")] string? AlbumName,
    [property: JsonPropertyName("duration")] JsonElement Duration,
    [property: JsonPropertyName("instrumental")] bool Instrumental,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics)
{
    public double DurationSeconds => Duration.ValueKind switch
    {
        JsonValueKind.Number when Duration.TryGetDouble(out var value) => value,
        JsonValueKind.String when double.TryParse(Duration.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) => value,
        _ => 0
    };
}
