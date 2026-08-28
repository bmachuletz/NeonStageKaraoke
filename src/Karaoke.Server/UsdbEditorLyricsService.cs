using System.Collections.Concurrent;
using System.Text.Json;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

/// <summary>
/// Provides an explicit, non-destructive USDB picker for the lyrics editor.
/// Search tokens keep resolved server-side version URLs out of the import API.
/// </summary>
internal sealed class UsdbEditorLyricsService(
    IUsdbClient client,
    UsdbSongMatcher matcher,
    LyricsVersionRepository versions,
    TimeProvider timeProvider,
    ILogger<UsdbEditorLyricsService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly ConcurrentDictionary<Guid, PendingSelection> _selections = new();

    public async Task<UsdbLyricsSearchDto> SearchAsync(SongDto song, string? query,
        CancellationToken cancellationToken)
    {
        var effectiveQuery = string.IsNullOrWhiteSpace(query) ? song.Title : query.Trim();
        RemoveExpiredSelections();
        foreach (var selection in _selections)
            if (selection.Value.SongId == song.Id) _selections.TryRemove(selection.Key, out _);
        var found = await client.SearchAsync(effectiveQuery, song.Artist, cancellationToken);
        var resolved = new List<(UsdbVersionCandidate Version, double Score,
            UsdbLyricsCandidateConfidence Confidence)>();
        foreach (var candidate in found)
        {
            foreach (var version in await client.ResolveVersionsAsync(candidate, cancellationToken))
            {
                var title = UsdbSongMatcher.Similarity(song.Title, version.Title);
                var artist = UsdbSongMatcher.ArtistSimilarity(song.Artist, version.Artist);
                var year = version.Year is null ? .5 : 1;
                var versionCompatible = UsdbSongMatcher.VersionCompatible(song.Title, version.Title);
                var score = (title * .58 + artist * .37 + year * .05) *
                            (versionCompatible ? 1 : .65);
                var confidence = score >= .96 ? UsdbLyricsCandidateConfidence.Exact :
                    score >= .82 ? UsdbLyricsCandidateConfidence.Strong :
                    UsdbLyricsCandidateConfidence.Possible;
                resolved.Add((version, Math.Round(score * 100, 1), confidence));
            }
        }

        var unique = resolved
            .GroupBy(item => (item.Version.Provider, item.Version.VersionId))
            .Select(group => group.OrderByDescending(item => item.Score).First())
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Version.Provider == UsdbProviders.Animux ? 0 : 1)
            .ThenBy(item => item.Version.VersionId)
            .ToArray();
        var items = new List<UsdbLyricsCandidateDto>(unique.Length);
        for (var index = 0; index < unique.Length; index++)
        {
            var item = unique[index];
            var token = Guid.NewGuid();
            _selections[token] = new(song.Id, item.Version,
                timeProvider.GetUtcNow().AddMinutes(15));
            items.Add(new(token, item.Version.VersionId, item.Version.Title, item.Version.Artist,
                item.Version.Year, item.Version.Language, item.Version.Edition, item.Score,
                item.Confidence, index == 0, item.Version.Provider));
        }
        logger.LogInformation("Editor USDB search for {SongId} returned {Count} resolved version(s)",
            song.Id, items.Count);
        return new(effectiveQuery, items);
    }

    public async Task<ImportUsdbLyricsResultDto?> ImportAsync(SongDto song, Guid selectionToken,
        CancellationToken cancellationToken)
    {
        if (!_selections.TryRemove(selectionToken, out var pending) || pending.SongId != song.Id ||
            pending.ExpiresAt <= timeProvider.GetUtcNow()) return null;

        var downloaded = await client.DownloadAsync(pending.Version, cancellationToken);
        var audio = new LyricsSourceAudio(string.Empty, song.Title, song.Artist, song.Album,
            TimeSpan.FromSeconds(song.DurationSeconds));
        var assessment = matcher.Assess(audio, pending.Version, downloaded.Parsed);
        var analysisRunId = $"usdb:{pending.Version.Provider}:{pending.Version.VersionId}:{assessment.Confidence}";
        var document = LyricsDocumentImporter.Import(downloaded.Parsed.ToLyrics(song.Id), analysisRunId,
            $"UltraStar/{pending.Version.Provider} {pending.Version.VersionId}", SegmentOrigin.ImportedFromUltraStar);
        document.Status = LyricsReviewStatus.InReview;
        var version = await versions.CreateAsync(song.Id, new(
            JsonSerializer.Serialize(document, JsonOptions), analysisRunId,
            LyricsVersionStatus.InReview, PreserveExistingDrafts: true), cancellationToken);
        logger.LogInformation(
            "USDB version {UsdbVersionId} was imported for song {SongId} as lyrics revision {Revision}; acoustic match {Confidence}, duration delta {Duration:F1}s",
            pending.Version.VersionId, song.Id, version.Revision, assessment.Confidence,
            assessment.DurationDifferenceSeconds);
        return new(version, pending.Version.VersionId, downloaded.Parsed.Lines.Count,
            downloaded.Parsed.WordCount, downloaded.Parsed.SyllableCount,
            assessment.Confidence.ToString(), assessment.DurationDifferenceSeconds,
            Source: pending.Version.Provider);
    }

    private void RemoveExpiredSelections()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _selections)
            if (item.Value.ExpiresAt <= now) _selections.TryRemove(item.Key, out _);
    }

    private sealed record PendingSelection(Guid SongId, UsdbVersionCandidate Version,
        DateTimeOffset ExpiresAt);
}
