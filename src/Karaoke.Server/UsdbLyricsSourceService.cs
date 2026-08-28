using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class UsdbLyricsSourceService(
    IUsdbClient client,
    UsdbSongMatcher matcher,
    IOptions<UsdbOptions> options,
    ILogger<UsdbLyricsSourceService> logger)
{
    private readonly UsdbOptions _options = options.Value;

    public async Task<ImportedLyricsResolution> ResolveWithFallbackAsync(
        LyricsSourceAudio audio,
        string targetLrcPath,
        string fallbackName,
        Func<CancellationToken, Task<bool>> fallback,
        CancellationToken cancellationToken)
    {
        var usdb = await TryResolveAsync(audio, targetLrcPath, cancellationToken);
        if (usdb.Success) return new(true, "USDB", usdb);
        logger.LogInformation("USDB did not produce lyrics for {Title}; starting {Fallback}",
            audio.Title, fallbackName);
        var fallbackSuccess = await fallback(cancellationToken);
        logger.LogInformation("Lyrics fallback {Fallback} for {Title}: {Result}", fallbackName,
            audio.Title, fallbackSuccess ? "success" : "no usable lyrics");
        return new(fallbackSuccess, fallbackName, usdb);
    }

    public async Task<UsdbLyricsResolution> TryResolveAsync(LyricsSourceAudio audio,
        string targetLrcPath, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return new(false, "USDB is disabled by configuration.");
        try
        {
            var searches = await client.SearchAsync(audio.Title, audio.Artist, cancellationToken);
            var edgeTiming = await UsdbRecordingTimingAnalyzer.MeasureAudioAsync(
                audio.AudioPath, audio.Duration, cancellationToken);
            if (edgeTiming.Measured)
                logger.LogInformation(
                    "Imported audio edge timing for {Title}: leading silence {Leading:F3}s, trailing silence {Trailing:F3}s, audible duration {Audible:F3}s",
                    audio.Title, edgeTiming.LeadingSilence.TotalSeconds,
                    edgeTiming.TrailingSilence.TotalSeconds, edgeTiming.AudibleDuration.TotalSeconds);
            logger.LogInformation("USDB search for {Title} by {Artist} returned {Count} candidate(s)",
                audio.Title, audio.Artist, searches.Count);
            UsdbMatchAssessment? bestRejected = null;
            foreach (var search in searches)
            {
                if (UsdbSongMatcher.Similarity(audio.Title, search.Title) < .70 ||
                    UsdbSongMatcher.Similarity(audio.Artist, search.Artist) < .60)
                {
                    logger.LogDebug("Skipping unrelated USDB search candidate {Label}", search.Label);
                    continue;
                }
                var versions = await client.ResolveVersionsAsync(search, cancellationToken);
                foreach (var version in versions)
                {
                    UsdbDownloadedLyrics download;
                    try { download = await client.DownloadAsync(version, cancellationToken); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception,
                            "USDB version {VersionId} could not be downloaded; trying the regular lyrics fallback",
                            version.VersionId);
                        continue;
                    }
                    var assessment = matcher.Assess(audio, version, download.Parsed, edgeTiming);
                    var timeline = UsdbRecordingTimingAnalyzer.Compare(
                        edgeTiming, download.Parsed,
                        Math.Min(3, _options.MaximumDurationDifferenceSeconds));
                    logger.LogInformation(
                        "USDB recording timing {VersionId} ({Provider}): hypothesis {Hypothesis}, declared {Declared}, full delta {FullDelta:F3}s, edge-trimmed delta {TrimmedDelta:F3}s, suggested offset {Offset:F3}s, compatible {Compatible} ({Reason})",
                        version.VersionId, version.Provider, timeline.Hypothesis,
                        timeline.DeclaredUsdbDuration?.TotalSeconds,
                        timeline.FullDurationDifferenceSeconds,
                        timeline.TrimmedDurationDifferenceSeconds,
                        timeline.SuggestedGlobalOffset.TotalSeconds,
                        timeline.DurationCompatible, timeline.Reason);
                    logger.LogInformation(
                        "USDB version {VersionId}: {Confidence}, score {Score}, title {Title:P0}, artist {Artist:P0}, duration delta {Duration:F1}s ({Reason})",
                        version.VersionId, assessment.Confidence, assessment.Score,
                        assessment.TitleSimilarity, assessment.ArtistSimilarity,
                        assessment.DurationDifferenceSeconds, assessment.Reason);
                    if (!assessment.Accepted)
                    {
                        if (bestRejected is null || assessment.Score > bestRejected.Score) bestRejected = assessment;
                        continue;
                    }
                    // Legacy UltraStar charts often omit #END. They can still
                    // use the direct chart path after the normal matcher has
                    // accepted title, artist, version markers and the final-
                    // note duration. #END adds a stronger recording check but
                    // is not a prerequisite for preserving a trusted chart.
                    var trustedDirectCandidate = timeline.Hypothesis ==
                                                 UsdbTimelineHypothesis.Unavailable ||
                                                 timeline.DurationCompatible;
                    var enhanced = download.Parsed.ToEnhancedLrc();
                    if (trustedDirectCandidate && timeline.SuggestedGlobalOffset != TimeSpan.Zero)
                        enhanced = "[neon-usdb-recording-offset:" +
                                   timeline.SuggestedGlobalOffset.TotalSeconds.ToString(
                                       "F3", CultureInfo.InvariantCulture) + "]" +
                                   Environment.NewLine + enhanced;
                    await WriteAtomicAsync(targetLrcPath, enhanced, cancellationToken);
                    logger.LogInformation(
                        "Lyrics source selected: USDB version {VersionId} ({Confidence}); {Words} words and {Syllables} syllables are passed to the aligner",
                        version.VersionId, assessment.Confidence, download.Parsed.WordCount,
                        download.Parsed.SyllableCount);
                    return new(true, trustedDirectCandidate
                            ? "A compatible USDB recording was selected for direct timing import."
                            : "A compatible USDB recording was selected for regular alignment.",
                        version.VersionId, assessment, enhanced, trustedDirectCandidate, timeline);
                }
            }
            var reason = bestRejected is null ? "USDB returned no downloadable version." :
                $"The best USDB version was {bestRejected.Confidence}: {bestRejected.Reason}.";
            logger.LogInformation("Lyrics source fallback for {Title}: {Reason}", audio.Title, reason);
            return new(false, reason, Match: bestRejected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "USDB lookup failed for {Title}; the existing LRCLIB/full-transcript pipeline remains active",
                audio.Title);
            return new(false, "USDB lookup failed; continuing with the regular lyrics fallback.");
        }
    }

    private static async Task WriteAtomicAsync(string target, string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, target, overwrite: true);
    }
}
