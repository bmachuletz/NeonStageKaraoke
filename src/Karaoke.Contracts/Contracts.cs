namespace Karaoke.Contracts;

public enum SongReviewStatus { InReview, Approved }
public enum SongLibraryCategory { KaraokeReady, WithoutLyrics }
public sealed record SongDto(Guid Id, string Title, string Artist, string Album, double DurationSeconds,
    bool HasLyrics, bool IsQueued = false, bool HasCover = false,
    SongReviewStatus ReviewStatus = SongReviewStatus.InReview,
    bool HasInstrumental = false, bool HasVocals = false,
    SongLibraryCategory LibraryCategory = SongLibraryCategory.KaraokeReady,
    bool HasSynchronizedLyrics = false)
{
    public string ReviewStatusLabel => LibraryCategory == SongLibraryCategory.WithoutLyrics
        ? "Ohne Lyrics / Without Lyrics"
        : ReviewStatus == SongReviewStatus.Approved ? "Freigegeben" : "In Review";
}
public sealed record ChangeSongReviewStatusRequest(SongReviewStatus Status);
public sealed record ImportLyricsSourceRequest(string Lyrics);
public sealed record BaseLyricsSourceDto(string Lyrics, string Source, bool IsPreAlignmentSource);
public sealed record UpdateBaseLyricsSourceRequest(string Lyrics);
public enum UsdbLyricsCandidateConfidence { Possible, Strong, Exact }
public sealed record UsdbLyricsCandidateDto(
    Guid SelectionToken,
    long VersionId,
    string Title,
    string Artist,
    int? Year,
    string? Language,
    string? Edition,
    double Score,
    UsdbLyricsCandidateConfidence Confidence,
    bool IsRecommended,
    string Source = "usdb.eu");
public sealed record UsdbLyricsSearchDto(
    string Query,
    IReadOnlyList<UsdbLyricsCandidateDto> Items);
public sealed record ImportUsdbLyricsRequest(Guid SelectionToken, bool StartLocalAlignment = true);
public sealed record ImportUsdbLyricsResultDto(
    LyricsVersionDto Version,
    long UsdbVersionId,
    int LineCount,
    int WordCount,
    int SyllableCount,
    string MatchConfidence,
    double DurationDifferenceSeconds,
    bool AlignmentStarted = false,
    string Source = "usdb.eu");
public sealed record DeleteSongResultDto(Guid SongId, int DeletedFiles);
public sealed record PagedResultDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}
public enum QueueEntryStatus { Waiting, Playing, Completed, Skipped, Removed }
public sealed record QueueEntryDto(
    Guid Id,
    SongDto Song,
    string RequestedBy,
    DateTimeOffset AddedAt,
    int Position = 0,
    QueueEntryStatus Status = QueueEntryStatus.Waiting,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null);
public sealed record AddQueueRequest(Guid SongId, string RequestedBy);
public sealed record ReorderQueueRequest(Guid EntryId, int Position);
public sealed record PlaybackPositionUpdateRequest(Guid QueueEntryId, TimeSpan Position);
public sealed record CompletePlaybackRequest(Guid QueueEntryId);
public sealed record PlaybackPositionDto(Guid QueueEntryId, TimeSpan Position, DateTimeOffset UpdatedAt, long Revision);
public sealed record PlaybackControllerRequest(Guid ClientId, string ClientName, bool Force = false);
public sealed record PlaybackControllerDto(bool OwnsControl, Guid? ControllerId, string? ControllerName, DateTimeOffset LeaseExpiresAt);
public sealed record LibrarySettingsDto(string LibraryPath);
public sealed record UsdbProviderSettingsDto(
    bool Enabled,
    string BaseUrl,
    bool AnimuxEnabled,
    string AnimuxBaseUrl,
    string AnimuxUsername,
    bool HasAnimuxPassword,
    bool ManagedByEnvironment,
    string Status);
public sealed record UpdateUsdbProviderSettingsRequest(
    bool Enabled,
    string BaseUrl,
    bool AnimuxEnabled,
    string AnimuxBaseUrl,
    string AnimuxUsername,
    string? AnimuxPassword = null,
    bool ClearAnimuxCredentials = false);
public enum QobuzDownloadQuality
{
    Mp3_320 = 5,
    FlacCd = 6,
    FlacHiRes96 = 7,
    FlacHiRes192 = 27
}
public sealed record QobuzPluginSettingsDto(
    bool Enabled,
    bool Configured,
    string AppId,
    bool HasAppSecret,
    bool HasUserAuthToken,
    QobuzDownloadQuality Quality,
    string ApiBaseUrl,
    bool ManagedByEnvironment,
    string Status);
public sealed record UpdateQobuzPluginSettingsRequest(
    bool Enabled,
    string AppId,
    string? AppSecret,
    string? UserAuthToken,
    QobuzDownloadQuality Quality = QobuzDownloadQuality.FlacCd,
    string? ApiBaseUrl = null,
    bool ClearStoredCredentials = false);
public enum AudioCatalogSource { Spotify, Qobuz }
public enum AudioDownloadSource { YouTube, Qobuz }
public sealed record SpotifyStatusDto(bool Configured, bool Connected, string? PlaylistUrl = null, string? Message = null);
public sealed record SpotifyTrackDto(
    string Id,
    string Uri,
    string Title,
    string Artist,
    string Album,
    string? ImageUrl,
    int DurationMilliseconds,
    bool HasSyncedLyrics,
    string? SpotifyUrl = null,
    AudioCatalogSource Source = AudioCatalogSource.Spotify,
    string? SourceUrl = null,
    decimal? Price = null,
    string? Currency = null,
    string? QobuzId = null,
    string? AudioQuality = null,
    AudioDownloadSource DownloadSource = AudioDownloadSource.YouTube,
    string? PreviewUrl = null)
{
    public string SourceLabel => Source == AudioCatalogSource.Qobuz ? "QOBUZ" : "SPOTIFY";
    public string DownloadSourceLabel => DownloadSource == AudioDownloadSource.Qobuz ? "QOBUZ" : "YOUTUBE / SUNNIFY";
    public string DurationLabel
    {
        get
        {
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, DurationMilliseconds));
            return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        }
    }
    public string PriceLabel => Price is null ? string.Empty : $"{Price:0.00} {Currency}".Trim();
    public bool HasPrice => Price is not null;
    public bool HasPreview => System.Uri.TryCreate(PreviewUrl, UriKind.Absolute, out var uri) &&
                              uri.Scheme is "http" or "https";
}
public sealed record AddWishRequest(SpotifyTrackDto Track, string RequestedBy);
public sealed record WishDto(Guid Id, SpotifyTrackDto Track, string RequestedBy, DateTimeOffset RequestedAt,
    string Status, bool HasAudioCandidate = false);
public sealed record WishAudioCandidateRequest(string AudioPath, string Status);
public sealed record ResolveImportedLyricsRequest(
    string AudioPath, string? Title = null, string? Artist = null, string? Album = null,
    double? DurationSeconds = null);
public sealed record FolderImportRequest(string SourcePath, bool Recursive = true);
public sealed record FolderImportStatus(
    bool IsRunning,
    Guid? JobId,
    string? SourcePath,
    int Current,
    int Total,
    int Succeeded,
    int Review,
    int Failed,
    int Skipped,
    int Percent,
    string Message,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> RecentOutput);
public sealed record KaraokeEventDto(Guid Id, string Name, string InviteToken, DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt, bool IsActive, DateTimeOffset CreatedAt, string? Description = null,
    string StageThemeId = "standard");
public sealed record CreateKaraokeEventRequest(string Name, DateTimeOffset StartsAt, DateTimeOffset? EndsAt = null,
    string? Description = null, string StageThemeId = "standard");
public sealed record StageThemeDto(string Id, string Name, string Description, bool IsDefault = false);
public sealed record PublicServerInfoDto(string BaseUrl);
public sealed record PlaybackStateDto(
    bool IsRunning,
    QueueEntryDto? Current,
    IReadOnlyList<QueueEntryDto> Queue,
    bool IsPaused = false,
    TimeSpan Position = default,
    DateTimeOffset? PositionUpdatedAt = null,
    double Speed = 1,
    long Revision = 0);
public sealed record LyricsDto(
    Guid SongId,
    IReadOnlyList<LyricsLineDto> Lines,
    string? Artist = null,
    string? Title = null,
    string? Album = null,
    string? Author = null,
    int OffsetMilliseconds = 0,
    bool HasUltraStarTimingHeritage = false,
    MusicalHighlightSettingsDto? MusicalHighlight = null);
public sealed record MusicalHighlightSettingsDto(
    bool Enabled,
    int TimelineVersion = 1);
public sealed record LyricsLineDto(TimeSpan Start, string Text, TimeSpan? End = null, int Index = 0,
    IReadOnlyList<LyricsWordDto>? Words = null, int? HoldAfterMilliseconds = null, string? StageEffect = null,
    int VoiceLane = 0, string? VoiceLabel = null, bool KaraokeTimingLocked = false);
public sealed record LyricsWordDto(TimeSpan Start, string Text, TimeSpan? End = null, int Index = 0,
    IReadOnlyList<LyricsSyllableDto>? Syllables = null, double SyllableConfidence = 0,
    bool KaraokeTimingLocked = false);
public sealed record LyricsSyllableDto(TimeSpan Start, string Text, TimeSpan? End = null, int Index = 0,
    double Confidence = 0, bool KaraokeTimingLocked = false,
    IReadOnlyList<LyricsNoteEvidenceDto>? Notes = null);
public sealed record LyricsNoteEvidenceDto(TimeSpan Start, TimeSpan End, int Midi, double Confidence);
public sealed record StemAvailabilityDto(bool HasInstrumental, bool HasVocals, string? Revision = null);
public sealed record StageTimingSampleDto(
    DateTimeOffset CapturedAt,
    string DeviceId,
    Guid? SongId,
    double LyricsPositionSeconds,
    double DspPositionSeconds,
    double MasterSamplePositionSeconds,
    double VocalSamplePositionSeconds,
    double SampleClockCorrectionSeconds,
    double StemDifferenceSeconds,
    int DspBufferLength,
    int DspBufferCount,
    int OutputSampleRate,
    bool Playing);
public sealed record VisualizationFrameDto(double TimeSeconds, double Energy, double Bass, double Mid, double High, bool Beat);
public sealed record SongVisualizationDto(Guid SongId, double FrameRate, IReadOnlyList<VisualizationFrameDto> Frames);
public sealed record LibraryScanStatusDto(
    bool IsRunning,
    int CheckedFiles,
    int NewFiles,
    int UpdatedFiles,
    int RemovedFiles,
    int Errors,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CurrentFile);

public enum LyricsVersionStatus
{
    Generated, NeedsReview, InReview, ReviewOverlaps, Reviewed, Approved, Published,
    Rejected, Superseded
}
public sealed record LyricsVersionSummaryDto(Guid Id, Guid SongId, long Revision, LyricsVersionStatus Status,
    string? AnalysisRunId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool HasAlignmentReport = false);
public sealed record LyricsVersionDto(Guid Id, Guid SongId, long Revision, LyricsVersionStatus Status,
    string DocumentJson, string? AnalysisRunId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? AlignmentReportJson = null);
public sealed record CreateLyricsVersionRequest(string DocumentJson, string? AnalysisRunId = null,
    LyricsVersionStatus Status = LyricsVersionStatus.InReview, bool AllowTimingConflicts = false,
    bool PreserveExistingDrafts = false, string? AlignmentReportJson = null);
public sealed record UpdateLyricsVersionRequest(long ExpectedRevision, string DocumentJson,
    LyricsVersionStatus Status = LyricsVersionStatus.InReview, bool AllowTimingConflicts = false);
public sealed record ChangeLyricsVersionStatusRequest(long ExpectedRevision, bool AllowTimingConflicts = false);
public sealed record LyricsVersionReportDto(Guid VersionId, Guid SongId, long Revision,
    string Title, string Outcome, string Content, bool HasTechnicalAlignmentReport,
    DateTimeOffset CreatedAt);
public sealed record SongRealignmentRequest(Guid? SourceVersionId = null,
    bool IncludeEditorBasis = true, bool IncludeOriginalLyrics = true,
    bool IncludeResearchShadow = false, bool IncludeEditorGuidance = false,
    bool IncludeBasicPitchAb = false, int? MaximumSongs = null);
public sealed record SongBasicPitchRequest(Guid? SourceVersionId = null);
public sealed record SongSelectionRealignmentRequest(IReadOnlyList<Guid> SongIds,
    SongRealignmentRequest Alignment);
public sealed record SongPackageExportRequest(IReadOnlyList<Guid> SongIds);
public sealed record ImportedSongPackageDto(Guid SongId, string Title, string Artist,
    SongReviewStatus ReviewStatus, int ImportedFiles, int ImportedLyricsVersions);
public sealed record SongPackageImportResultDto(int ImportedSongs,
    IReadOnlyList<ImportedSongPackageDto> Songs);
