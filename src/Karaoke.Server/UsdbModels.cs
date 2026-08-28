using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record LyricsSourceAudio(
    string AudioPath,
    string Title,
    string Artist,
    string? Album,
    TimeSpan Duration,
    int? Year = null);

public sealed record UsdbSearchCandidate(
    string Label,
    string Title,
    string Artist,
    int? Year,
    Uri DetailUri,
    string Provider = UsdbProviders.Eu,
    string? Language = null,
    string? Edition = null);

public sealed record UsdbVersionCandidate(
    long VersionId,
    string Title,
    string Artist,
    int? Year,
    string? Language,
    string? Edition,
    Uri DetailUri,
    string Provider = UsdbProviders.Eu);

public static class UsdbProviders
{
    public const string Eu = "usdb.eu";
    public const string Animux = "usdb.animux.de";
}

public sealed record UsdbDownloadedLyrics(
    UsdbVersionCandidate Version,
    string Content,
    UltraStarLyricsImport Parsed,
    bool FromCache);

public enum UsdbMatchConfidence
{
    Rejected,
    Uncertain,
    High,
    Exact
}

public sealed record UsdbMatchAssessment(
    UsdbMatchConfidence Confidence,
    double Score,
    double TitleSimilarity,
    double ArtistSimilarity,
    double DurationDifferenceSeconds,
    string Reason)
{
    public bool Accepted => Confidence is UsdbMatchConfidence.High or UsdbMatchConfidence.Exact;
}

public sealed record UsdbLyricsResolution(
    bool Success,
    string Reason,
    long? VersionId = null,
    UsdbMatchAssessment? Match = null,
    string? EnhancedLrc = null,
    bool TrustedDirectCandidate = false,
    UsdbRecordingTimingDiagnostic? RecordingTiming = null);

public sealed record ImportedLyricsResolution(
    bool Success,
    string Source,
    UsdbLyricsResolution Usdb);

public interface IUsdbClient
{
    Task<IReadOnlyList<UsdbSearchCandidate>> SearchAsync(
        string title, string artist, CancellationToken cancellationToken);
    Task<IReadOnlyList<UsdbVersionCandidate>> ResolveVersionsAsync(
        UsdbSearchCandidate candidate, CancellationToken cancellationToken);
    Task<UsdbDownloadedLyrics> DownloadAsync(
        UsdbVersionCandidate version, CancellationToken cancellationToken);
}
