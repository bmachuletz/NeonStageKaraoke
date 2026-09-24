using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Karaoke.App.Services;
using Karaoke.Contracts;
using Karaoke.Editor.Core;
using NeonStage.Testing;

namespace Karaoke.App.Desktop;

public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly HttpClient _http;
    private readonly IAudioPlaybackService _audio;
    private readonly bool _loadVisualAssets;
    private SongDto? _selectedSong;
    private LyricsEditorDocument? _document;
    private LyricsEditorDocument? _timingPreviewDocument;
    private IReadOnlyList<double> _visualBeatTimes = [];
    private readonly Dictionary<Guid, (TimeSpan Start, TimeSpan End)> _timingProjectionBaseline = [];
    private TimeSpan _playhead;
    private string _status = "Verbinde mit Neon Stage …";
    private bool _busy;
    private WaveformPyramid? _waveform;
    private Bitmap? _cover;
    private SongVideoInfoDto? _songVideo;
    private readonly FfmpegWaveformService _waveforms = new();
    private readonly EditorAudioCache _audioCache;
    private readonly EditorDraftRecovery _draftRecovery = new();
    private readonly EditorStageTestSession _stageTest = new();
    private Guid? _serverVersionId;
    private long _serverRevision;
    private LyricsVersionStatus? _serverVersionStatus;
    private string? _alignmentReportJson;
    private IReadOnlyList<PitchNoteEvidence> _pitchEvidence = [];
    private string _songFilter = string.Empty;
    private string _songStatusFilter = "In Review";
    private LyricSegment? _selectedSegment;
    private CancellationTokenSource? _songLoadCancellation;
    private Guid? _loadedAudioSongId;
    private Uri? _selectedPlaybackUri;
    private Uri? _localVocalUri;
    private Uri? _localInstrumentalUri;
    private Uri? _localOriginalUri;
    private bool _hasVocalStem;
    private bool _hasInstrumentalStem;
    private bool _instrumentalEnabled;
    private int _instrumentalVolume = 65;
    private int _vocalVolume = 90;
    private int _playbackSpeedPercent = 100;
    private long _previewRevision;
    private bool _perceptualLeadEnabled = true;
    private bool _karaokeTimingEnabled = true;
    private bool _showTechnicalLyrics;
    private bool _musicalHighlightEnabled = StageLyricsPreview.MusicalHighlightEnvironmentEnabled();
    private long _timelineRevision;
    private TimeSpan? _loopStart;
    private TimeSpan? _loopEnd;
    private bool _loopEnabled;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _jobTimer;
    private readonly DispatcherTimer _stageLyricsUpdateTimer;
    private bool _stageLyricsUpdatePending;
    private bool _consoleVisible;
    private string _consoleMode = "wishlist";
    private bool _versionsVisible;
    private KaraokeEventDto? _selectedWishEvent;
    private string _jobState = "Hintergrundverarbeitung bereit";
    private int _wishProgress;
    private string _wishProgressLabel = "Kein Auftrag aktiv";
    private Guid? _handledImportJob;
    private Guid? _handledRealignmentJob;
    private Guid? _handledLyricsRecognitionJob;
    private readonly HashSet<Guid> _realignedSongsPendingReview = [];
    private bool _wishWorkerRunning;
    private string _adminWishQuery = string.Empty;
    private string _adminWishSearchStatus = "Spotify oder Qobuz durchsuchen und einen Treffer direkt importieren.";
    private bool _adminWishSearching;
    private string _folderImportPath = string.Empty;
    private bool _folderImportRecursive = true;
    private int _folderImportProgress;
    private string _folderImportSummary = "Noch kein Ordnerimport gestartet.";
    private Guid? _handledFolderImportJob;
    private readonly HashSet<string> _seenConsoleOutput = [];
    private readonly CancellationTokenSource _changeFeedCancellation = new();
    private TimeSpan _positionAnchor;
    private long _positionAnchorTimestamp = Stopwatch.GetTimestamp();
    private Task _pendingSeek = Task.CompletedTask;
    private readonly SemaphoreSlim _playbackCommandLock = new(1, 1);
    private readonly SemaphoreSlim _draftSaveLock = new(1, 1);
    private CancellationTokenSource? _seekCancellation;
    private int _loopSeekInProgress;
    private bool _visualClockSuspended;
    private long _lastStageClockTimestamp;
    private string _stageTestStatus = "Stage-Test nicht gestartet";
    private string _mp4ExportStatus = "Kein MP4-Export aktiv";
    private bool _mp4ExportRunning;
    private CancellationTokenSource? _mp4ExportCancellation;
    private bool _stageTestAudioMuted;
    private int _stageTestPreviousMasterVolume;
    private int _stageTestPreviousVocalVolume;
    private string? _loadedSourceFingerprint;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions StageIpcJsonOptions = new(JsonSerializerDefaults.Web);

    public EditorViewModel(IAudioPlaybackService audio, bool loadVisualAssets = true)
    {
        _audio = audio;
        _loadVisualAssets = loadVisualAssets;
        ServerAddress = EditorConnectionSettings.ResolveServerAddress();
        _http = new HttpClient { BaseAddress = ServerAddress, Timeout = TimeSpan.FromMinutes(5) };
        _audioCache = new EditorAudioCache(_http);
        _audio.PositionChanged += (_, position) => Dispatcher.UIThread.Post(() => ObserveDecoderPosition(position));
        _audio.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => Status = state);
        _audio.PlaybackFailed += (_, error) => Dispatcher.UIThread.Post(() => Status = error);
        _audio.Volume = _vocalVolume;
        _audio.VocalVolume = _vocalVolume;
        _stageTest.StatusChanged += value => Dispatcher.UIThread.Post(() =>
        {
            StageTestStatus = value;
            OnPropertyChanged(nameof(IsStageTestRunning));
            OnPropertyChanged(nameof(StageTestActionLabel));
            if (!_stageTest.IsRunning) RestoreEditorAudioAfterStageTest();
        });
        _stageTest.ExportProgress += (frame, total) => Dispatcher.UIThread.Post(() =>
        {
            var percent = total <= 0 ? 0 : frame * 100d / total;
            Mp4ExportStatus = $"MP4: {frame:N0} / {total:N0} Frames · {percent:0.0} %";
        });
        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) =>
            {
                if (!_audio.IsPlaying || _visualClockSuspended) return;
                var elapsed = Stopwatch.GetElapsedTime(_positionAnchorTimestamp);
                var interpolated = _positionAnchor + TimeSpan.FromTicks(
                    (long)(elapsed.Ticks * (_playbackSpeedPercent / 100d)));
                if (LoopEnabled && LoopStart is { } loopStart && LoopEnd is { } loopEnd && interpolated >= loopEnd)
                {
                    if (Interlocked.Exchange(ref _loopSeekInProgress, 1) == 0)
                        _ = SeekLoopAsync(loopStart, loopEnd, interpolated);
                    return;
                }
                Playhead = interpolated < Playhead ? Playhead : interpolated;
                if (_stageTest.IsRunning && Stopwatch.GetElapsedTime(_lastStageClockTimestamp) >= TimeSpan.FromMilliseconds(500))
                {
                    _lastStageClockTimestamp = Stopwatch.GetTimestamp();
                    _ = _stageTest.SendClockAsync(Playhead, _audio.IsPlaying);
                }
            });
        _positionTimer.Start();
        _stageLyricsUpdateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background, (_, _) => FlushStageLyricsUpdate());
        _jobTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            async (_, _) => await RefreshJobStatusAsync());
        _jobTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SongDto> Songs { get; } = [];
    public ObservableCollection<SongDto> FilteredSongs { get; } = [];
    public ObservableCollection<KaraokeEventDto> WishEvents { get; } = [];
    public ObservableCollection<EditorWishItem> Wishes { get; } = [];
    public ObservableCollection<SpotifyTrackDto> AdminWishSearchResults { get; } = [];
    public ObservableCollection<string> ConsoleLines { get; } = [];
    public ObservableCollection<EditorLyricsVersionItem> LyricsVersions { get; } = [];
    public IReadOnlyList<string> SongStatusFilters { get; } =
    [
        Localized("Alle", "All"),
        "In Review",
        Localized("Freigegeben", "Released"),
        Localized("In Review · synchronisierte Lyrics", "In review · synchronized lyrics"),
        Localized("In Review · keine synchronisierten Lyrics", "In review · no synchronized lyrics"),
        Localized("Ohne Lyrics", "Without lyrics")
    ];
    public Uri ServerAddress { get; }
    public CommandHistory History { get; } = new();
    public SongDto? SelectedSong
    {
        get => _selectedSong;
        set
        {
            if (!Set(ref _selectedSong, value)) return;
            OnPropertyChanged(nameof(SongReleaseActionLabel));
        }
    }
    public string SongReleaseActionLabel => SelectedSong?.ReviewStatus == SongReviewStatus.Approved
        ? Localized("↩ Zurück in Review", "↩ Return to review")
        : Localized("✓ Song freigeben", "✓ Release song");
    public LyricsEditorDocument? Document
    {
        get => _document;
        private set
        {
            if (!Set(ref _document, value)) return;
            _showTechnicalLyrics = false;
            RefreshTimingProjection();
            OnPropertyChanged(nameof(KaraokeTimingAvailable));
            OnPropertyChanged(nameof(KaraokeTimingActive));
            OnPropertyChanged(nameof(KaraokeTimingDescription));
            OnPropertyChanged(nameof(ShowTechnicalLyrics));
            OnPropertyChanged(nameof(HasTechnicalLyrics));
            OnPropertyChanged(nameof(CanEditDisplayedSegmentText));
        }
    }
    public LyricsEditorDocument? TimingPreviewDocument
    {
        get => _timingPreviewDocument;
        private set => Set(ref _timingPreviewDocument, value);
    }
    public WaveformPyramid? Waveform { get => _waveform; private set => Set(ref _waveform, value); }
    public IReadOnlyList<PitchNoteEvidence> PitchEvidence
    {
        get => _pitchEvidence;
        private set => Set(ref _pitchEvidence, value);
    }
    public Bitmap? Cover
    {
        get => _cover;
        private set
        {
            if (ReferenceEquals(_cover, value)) return;
            var previous = _cover;
            _cover = value;
            OnPropertyChanged();
            previous?.Dispose();
        }
    }
    public TimeSpan Playhead
    {
        get => _playhead;
        set { if (Set(ref _playhead, value)) OnPropertyChanged(nameof(PlayheadLabel)); }
    }
    public string PlayheadLabel => Playhead.ToString(@"mm\:ss\.fff");
    public bool IsAudioPlaying => _audio.IsPlaying;
    public bool IsStageTestRunning => _stageTest.IsRunning;
    public string StageTestActionLabel => IsStageTestRunning ? "■ Stage-Test beenden" : "▣ Song auf Stage testen";
    public string StageTestStatus { get => _stageTestStatus; private set => Set(ref _stageTestStatus, value); }
    public string Mp4ExportStatus { get => _mp4ExportStatus; private set => Set(ref _mp4ExportStatus, value); }
    public bool Mp4ExportRunning
    {
        get => _mp4ExportRunning;
        private set
        {
            if (!Set(ref _mp4ExportRunning, value)) return;
            OnPropertyChanged(nameof(Mp4ExportActionLabel));
        }
    }
    public string Mp4ExportActionLabel => Mp4ExportRunning ? "■ MP4-Export abbrechen" : "MP4 exportieren …";
    public bool HasSongVideo => _songVideo is not null;
    public int SongVideoOffsetMilliseconds => _songVideo?.OffsetMilliseconds ?? 0;
    public string Status { get => _status; private set => Set(ref _status, EditorLocale.Text(value)); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public bool ConsoleVisible
    {
        get => _consoleVisible;
        set
        {
            if (!Set(ref _consoleVisible, value)) return;
            OnPropertyChanged(nameof(WishlistConsoleVisible));
            OnPropertyChanged(nameof(FolderImportConsoleVisible));
        }
    }
    public bool WishlistConsoleVisible => ConsoleVisible && _consoleMode == "wishlist";
    public bool FolderImportConsoleVisible => ConsoleVisible && _consoleMode == "folder";
    public bool VersionsVisible { get => _versionsVisible; set => Set(ref _versionsVisible, value); }
    public string LyricsVersionsHeading => EditorLocale.German
        ? $"LYRICS-VERSIONEN · {LyricsVersions.Count}"
        : $"LYRICS VERSIONS · {LyricsVersions.Count}";
    public KaraokeEventDto? SelectedWishEvent
    {
        get => _selectedWishEvent;
        set => Set(ref _selectedWishEvent, value);
    }
    public string JobState { get => _jobState; private set => Set(ref _jobState, value); }
    public int WishProgress { get => _wishProgress; private set => Set(ref _wishProgress, value); }
    public string WishProgressLabel { get => _wishProgressLabel; private set => Set(ref _wishProgressLabel, value); }
    public string AdminWishQuery { get => _adminWishQuery; set => Set(ref _adminWishQuery, value); }
    public string AdminWishSearchStatus { get => _adminWishSearchStatus; private set => Set(ref _adminWishSearchStatus, value); }
    public bool AdminWishSearching { get => _adminWishSearching; private set => Set(ref _adminWishSearching, value); }
    public string FolderImportPath { get => _folderImportPath; set => Set(ref _folderImportPath, value); }
    public bool FolderImportRecursive { get => _folderImportRecursive; set => Set(ref _folderImportRecursive, value); }
    public int FolderImportProgress { get => _folderImportProgress; private set => Set(ref _folderImportProgress, value); }
    public string FolderImportSummary { get => _folderImportSummary; private set => Set(ref _folderImportSummary, value); }
    public bool HasInstrumentalStem { get => _hasInstrumentalStem; private set => Set(ref _hasInstrumentalStem, value); }
    public bool InstrumentalEnabled
    {
        get => _instrumentalEnabled;
        set
        {
            if (!Set(ref _instrumentalEnabled, value)) return;
            _audio.Stop();
            _loadedAudioSongId = null;
            _audio.Volume = value ? InstrumentalVolume : VocalVolume;
            _audio.VocalVolume = VocalVolume;
            if (_stageTestAudioMuted) MuteEditorAudioForStageTest();
            var start = value ? TimeSpan.Zero : FirstVocalPosition();
            AnchorPosition(start);
            Status = value ? "Instrumental wird bei der nächsten Wiedergabe zugemischt." : "Vocal-Solowiedergabe aktiviert.";
        }
    }
    public int InstrumentalVolume
    {
        get => _instrumentalVolume;
        set
        {
            if (!Set(ref _instrumentalVolume, value) || !InstrumentalEnabled) return;
            // Der aktuelle vorberechnete Mix darf beim Ziehen am Regler nicht
            // plötzlich abbrechen. Der neue Pegel gilt beim nächsten Start.
            _loadedAudioSongId = null;
            Status = "Instrumentalpegel geändert – wird beim nächsten Start angewendet.";
        }
    }
    public int VocalVolume
    {
        get => _vocalVolume;
        set
        {
            if (!Set(ref _vocalVolume, value)) return;
            if (InstrumentalEnabled)
            {
                _loadedAudioSongId = null;
                Status = "Vocalpegel geändert – wird beim nächsten Start angewendet.";
            }
            else if (!_stageTestAudioMuted) _audio.Volume = value;
        }
    }
    public int PlaybackSpeedPercent
    {
        get => _playbackSpeedPercent;
        set
        {
            var normalized = Math.Clamp((int)Math.Round(value / 10d) * 10, 10, 100);
            if (normalized == _playbackSpeedPercent) return;
            var decoderPosition = _audio.IsSeekable ? _audio.Position : Playhead;
            _playbackSpeedPercent = normalized;
            _audio.PlaybackRate = normalized / 100d;
            AnchorPosition(decoderPosition);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackSpeedLabel));
            Status = EditorLocale.German
                ? $"Wiedergabegeschwindigkeit: {normalized} %"
                : $"Playback speed: {normalized}%";
        }
    }
    public string PlaybackSpeedLabel => $"{PlaybackSpeedPercent} %";
    public long PreviewRevision { get => _previewRevision; private set => Set(ref _previewRevision, value); }
    public bool PerceptualLeadEnabled
    {
        get => _perceptualLeadEnabled;
        set => Set(ref _perceptualLeadEnabled, value);
    }
    public bool KaraokeTimingEnabled
    {
        get => _karaokeTimingEnabled;
        set
        {
            if (!Set(ref _karaokeTimingEnabled, value)) return;
            RefreshTimingProjection();
            OnPropertyChanged(nameof(KaraokeTimingActive));
            OnPropertyChanged(nameof(KaraokeTimingDescription));
            OnPropertyChanged(nameof(CanEditWord));
            OnPropertyChanged(nameof(CanEditSyllable));
            OnPropertyChanged(nameof(CanEditLine));
            OnPropertyChanged(nameof(CanDeleteSegment));
        }
    }
    public bool MusicalHighlightEnabled
    {
        get => _musicalHighlightEnabled;
        set
        {
            if (!Set(ref _musicalHighlightEnabled, value)) return;
            PreviewRevision++;
            QueueStageLyricsUpdate();
        }
    }
    public bool KaraokeTimingAvailable => Document is not null && !Document.UsesUltraStarTiming;
    public bool KaraokeTimingActive => KaraokeTimingEnabled && KaraokeTimingAvailable;
    public string KaraokeTimingDescription => Document?.UsesUltraStarTiming == true
        ? Localized("UltraStar-Timing geschützt · keine Umrechnung",
            "UltraStar timing protected · no conversion")
        : Localized("Visuelles Beat-Raster · gespeicherte Zeiten bleiben unverändert",
            "Visual beat grid · stored timing remains unchanged");
    public bool HasTechnicalLyrics => Document?.Segments.Any(segment =>
        !string.IsNullOrWhiteSpace(segment.TechnicalText)) == true;
    public bool ShowTechnicalLyrics
    {
        get => _showTechnicalLyrics;
        set
        {
            var normalized = value && HasTechnicalLyrics;
            if (!Set(ref _showTechnicalLyrics, normalized)) return;
            TimelineRevision++;
            OnPropertyChanged(nameof(SelectedSegmentHeading));
            OnPropertyChanged(nameof(SelectedSegmentText));
            OnPropertyChanged(nameof(CanEditDisplayedSegmentText));
        }
    }
    public long TimelineRevision { get => _timelineRevision; private set => Set(ref _timelineRevision, value); }
    public TimeSpan? LoopStart { get => _loopStart; private set => Set(ref _loopStart, value); }
    public TimeSpan? LoopEnd { get => _loopEnd; private set => Set(ref _loopEnd, value); }
    public bool LoopEnabled
    {
        get => _loopEnabled;
        private set { if (Set(ref _loopEnabled, value)) OnPropertyChanged(nameof(LoopLabel)); }
    }
    public string LoopLabel => LoopEnabled ? "↻ Loop aktiv" : "↻ Loop";
    public string SongFilter
    {
        get => _songFilter;
        set { if (Set(ref _songFilter, value)) ApplySongFilter(); }
    }
    public string SongStatusFilter
    {
        get => _songStatusFilter;
        set { if (Set(ref _songStatusFilter, value)) ApplySongFilter(); }
    }
    public LyricSegment? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!Set(ref _selectedSegment, value)) return;
            OnPropertyChanged(nameof(SelectedSegmentHeading));
            OnPropertyChanged(nameof(SelectedSegmentTiming));
            OnPropertyChanged(nameof(SelectedSegmentDetails));
            OnPropertyChanged(nameof(SelectedSegmentText));
            OnPropertyChanged(nameof(CanEditWord));
            OnPropertyChanged(nameof(CanEditSyllable));
            OnPropertyChanged(nameof(CanEditLine));
            OnPropertyChanged(nameof(CanDeleteSegment));
            OnPropertyChanged(nameof(CanEditDisplayedSegmentText));
            OnPropertyChanged(nameof(SelectedStageEffect));
            OnPropertyChanged(nameof(SelectedVoiceLane));
        }
    }
    public string SelectedSegmentHeading => SelectedSegment is null
        ? "Segment auswählen"
        : $"{SelectedSegment.Type}: {DisplayedText(SelectedSegment)}";
    public string SelectedSegmentTiming => SelectedSegment is null ? "" :
        $"{SelectedSegment.Start:mm\\:ss\\.fff}  →  {SelectedSegment.End:mm\\:ss\\.fff}  ·  {(SelectedSegment.End - SelectedSegment.Start).TotalMilliseconds:0} ms";
    public string SelectedSegmentDetails => SelectedSegment is null ?
        "Klicke ein Segment oder ziehe eine gemeinsame Silbengrenze in der Timeline."
        : $"Quelle: {SelectedSegment.Origin} · Konfidenz: {(SelectedSegment.Confidence is null ? "–" : SelectedSegment.Confidence.Value.ToString("P0"))}";
    public string SelectedSegmentText => SelectedSegment is null ? string.Empty : DisplayedText(SelectedSegment);
    public bool CanEditWord => SelectedSegment?.Type == LyricSegmentType.Word;
    public bool CanEditSyllable => SelectedSegment?.Type == LyricSegmentType.Syllable;
    public bool CanEditLine => SelectedSegment?.Type == LyricSegmentType.Line;
    public bool CanDeleteSegment => CanEditLine || CanEditWord || CanEditSyllable;
    public bool CanEditDisplayedSegmentText => CanDeleteSegment && !ShowTechnicalLyrics;
    public Array StageEffects { get; } = Enum.GetValues<StageLineEffect>();
    public StageLineEffect SelectedStageEffect => SelectedSegment?.Type == LyricSegmentType.Line
        ? SelectedSegment.StageEffect : StageLineEffect.Automatic;
    public int SelectedVoiceLane => SelectedSegment?.Type == LyricSegmentType.Line
        ? Math.Clamp(SelectedSegment.VoiceLane, 0, 3) : 0;
    public string SongHeading => SelectedSong is null ? "Kein Song ausgewählt" : $"{SelectedSong.Title}  ·  {SelectedSong.Artist}";
    public string ReviewSummary => Document is null ? "Kein Alignment geladen" :
        $"{Document.Segments.Count()} Segmente · {Document.Segments.Count(s => s.RequiresReview)} ungeprüft";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Songs.Count > 0 || Busy) return;
        Busy = true;
        try
        {
            var songs = await _http.GetFromJsonAsync<IReadOnlyList<SongDto>>("/api/songs?take=500&includeUnreleased=true", cancellationToken) ?? [];
            foreach (var song in songs.OrderBy(song => song.Artist).ThenBy(song => song.Title))
                Songs.Add(song);
            ApplySongFilter();
            Status = $"{Songs.Count} Songprojekte geladen";
            await LoadWishEventsAsync(cancellationToken);
            _ = RunChangeFeedAsync(_changeFeedCancellation.Token);
        }
        catch (Exception exception) { Status = "Server nicht erreichbar: " + exception.Message; }
        finally { Busy = false; }
    }

    public async Task<bool> ExportSongPackageAsync(IReadOnlyCollection<Guid> songIds, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (songIds.Count == 0 || string.IsNullOrWhiteSpace(destinationPath)) return false;
        var partialPath = destinationPath + ".partial";
        Status = EditorLocale.German
            ? songIds.Count == 1
                ? "Songpaket wird exportiert …"
                : $"Songpaket mit {songIds.Count} Songs wird exportiert …"
            : songIds.Count == 1
                ? "Exporting song package …"
                : $"Exporting package with {songIds.Count} songs …";
        try
        {
            using var transfer = new HttpClient { BaseAddress = ServerAddress, Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/song-packages/export")
            {
                Content = JsonContent.Create(new SongPackageExportRequest(songIds.ToArray()))
            };
            using var response = await transfer.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ReadTransferErrorAsync(response, cancellationToken));
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(destination, cancellationToken);
            File.Move(partialPath, destinationPath, overwrite: true);
            Status = EditorLocale.German
                ? songIds.Count == 1
                    ? "Songpaket vollständig exportiert."
                    : $"{songIds.Count} Songs vollständig exportiert."
                : songIds.Count == 1
                    ? "Song package exported successfully."
                    : $"{songIds.Count} songs exported successfully.";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = (EditorLocale.German ? "Export fehlgeschlagen: " : "Export failed: ") + exception.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); }
            catch (IOException) { }
        }
    }

    public async Task<bool> ImportSongPackagesAsync(IReadOnlyList<string> packagePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = packagePaths.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).ToArray();
        if (paths.Length == 0) return false;
        var importedSongs = 0;
        try
        {
            using var transfer = new HttpClient { BaseAddress = ServerAddress, Timeout = Timeout.InfiniteTimeSpan };
            for (var index = 0; index < paths.Length; index++)
            {
                Status = EditorLocale.German
                    ? paths.Length == 1
                        ? "Songpaket wird geprüft und importiert …"
                        : $"Songpaket {index + 1} von {paths.Length} wird geprüft und importiert …"
                    : paths.Length == 1
                        ? "Validating and importing song package …"
                        : $"Validating and importing package {index + 1} of {paths.Length} …";
                await using var stream = new FileStream(paths[index], FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var content = new StreamContent(stream, 1024 * 1024);
                content.Headers.ContentType = new("application/vnd.neonstage.song-package+zip");
                using var response = await transfer.PostAsync("/api/admin/song-packages/import", content,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(await ReadTransferErrorAsync(response, cancellationToken));
                var result = await response.Content.ReadFromJsonAsync<SongPackageImportResultDto>(cancellationToken)
                             ?? throw new InvalidOperationException(EditorLocale.German
                                 ? "Der Server lieferte kein Importergebnis."
                                 : "The server returned no import result.");
                importedSongs += result.ImportedSongs;
            }
            await MergeNewSongsAsync(cancellationToken);
            Status = EditorLocale.German
                ? $"Import abgeschlossen: {importedSongs} Song(s) vollständig übernommen."
                : $"Import complete: {importedSongs} song(s) imported successfully.";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = EditorLocale.German
                ? $"Import nach {importedSongs} Song(s) abgebrochen: {exception.Message}"
                : $"Import stopped after {importedSongs} song(s): {exception.Message}";
            return false;
        }
    }

    private static async Task<string> ReadTransferErrorAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return $"Serverfehler {(int)response.StatusCode}";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out var detail) &&
                !string.IsNullOrWhiteSpace(detail.GetString())) return detail.GetString()!;
        }
        catch (JsonException) { }
        return body.Length <= 1000 ? body : body[..1000];
    }

    public void ToggleConsole()
    {
        ConsoleVisible = !ConsoleVisible;
        NotifyConsoleMode();
    }

    public void ShowWishlistConsole()
    {
        _consoleMode = "wishlist";
        ConsoleVisible = true;
        NotifyConsoleMode();
    }

    public void ShowFolderImportConsole()
    {
        _consoleMode = "folder";
        ConsoleVisible = true;
        NotifyConsoleMode();
    }

    private void NotifyConsoleMode()
    {
        OnPropertyChanged(nameof(WishlistConsoleVisible));
        OnPropertyChanged(nameof(FolderImportConsoleVisible));
    }

    public void ToggleVersions() => VersionsVisible = !VersionsVisible;

    public async Task RefreshLyricsVersionsAsync(CancellationToken cancellationToken = default,
        bool reportErrors = true)
    {
        if (SelectedSong is not { } song)
        {
            LyricsVersions.Clear();
            OnPropertyChanged(nameof(LyricsVersionsHeading));
            return;
        }
        try
        {
            var versions = await _http.GetFromJsonAsync<IReadOnlyList<LyricsVersionSummaryDto>>(
                $"/api/songs/{song.Id}/lyrics/versions", cancellationToken) ?? [];
            if (SelectedSong?.Id != song.Id) return;
            LyricsVersions.Clear();
            foreach (var version in versions.OrderByDescending(item => item.Revision))
                LyricsVersions.Add(new EditorLyricsVersionItem(version, version.Id == _serverVersionId));
            OnPropertyChanged(nameof(LyricsVersionsHeading));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (reportErrors) Status = "Lyrics-Versionen konnten nicht geladen werden: " + exception.Message;
        }
    }

    public async Task LoadLyricsVersionAsync(EditorLyricsVersionItem item,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song || item.Version.SongId != song.Id) return;
        try
        {
            var version = await _http.GetFromJsonAsync<LyricsVersionDto>(
                $"/api/songs/{song.Id}/lyrics/versions/{item.Version.Id}", cancellationToken);
            if (version is null) throw new InvalidOperationException("Der Lyrics-Stand wurde nicht gefunden.");
            var document = JsonSerializer.Deserialize<LyricsEditorDocument>(version.DocumentJson, JsonOptions);
            if (document is null) throw new InvalidOperationException("Der Lyrics-Stand enthält kein lesbares Editor-Dokument.");
            LyricsDocumentImporter.IgnoreStructureMarkers(document);

            _audio.Stop();
            _loadedAudioSongId = null;
            History.Clear();
            SelectedSegment = null;
            Document = document;
            // UpdateAsync überschreibt niemals eine Revision, sondern archiviert
            // sie und legt beim Speichern einen neuen Stand an. Deshalb darf eine
            // noch bearbeitbare generierte/reviewte Version als echte Basis des
            // Arbeitsstands verbunden bleiben. Nur abgeschlossene historische
            // oder veröffentlichte Stände werden als ungespeicherte Kopie geladen.
            var connectedWorkingVersion = CanContinueAsWorkingVersion(version.Status);
            _serverVersionId = connectedWorkingVersion ? version.Id : null;
            _serverRevision = connectedWorkingVersion ? version.Revision : 0;
            _serverVersionStatus = connectedWorkingVersion ? version.Status : null;
            SetAlignmentReport(version.AlignmentReportJson);
            AnchorPosition(FirstVocalPosition());
            PreviewRevision++;
            TimelineRevision++;
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(ReviewSummary));
            SaveLocalRecoverySnapshot();
            QueueStageLyricsUpdate();
            await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
            Status = connectedWorkingVersion
                ? Localized(
                    $"Revision {version.Revision} vom {EditorLyricsVersionItem.FormatTimestamp(version.CreatedAt)} ist jetzt der Arbeitsstand · Speichern erzeugt eine neue Revision",
                    $"Revision {version.Revision} from {EditorLyricsVersionItem.FormatTimestamp(version.CreatedAt)} is now the working version · saving creates a new revision")
                : Localized(
                    $"Revision {version.Revision} vom {EditorLyricsVersionItem.FormatTimestamp(version.CreatedAt)} als neue ungespeicherte Arbeitskopie geladen",
                    $"Revision {version.Revision} from {EditorLyricsVersionItem.FormatTimestamp(version.CreatedAt)} loaded as a new unsaved working copy");
        }
        catch (Exception exception)
        {
            Status = "Lyrics-Version konnte nicht geladen werden: " + exception.Message;
        }
    }

    public async Task<LyricsVersionReportDto?> GetLyricsVersionReportAsync(EditorLyricsVersionItem item,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song || item.Version.SongId != song.Id) return null;
        try
        {
            var language = EditorLocale.German ? "de" : "en";
            return await _http.GetFromJsonAsync<LyricsVersionReportDto>(
                $"/api/songs/{song.Id}/lyrics/versions/{item.Version.Id}/report?language={language}",
                cancellationToken);
        }
        catch (Exception exception)
        {
            Status = "Alignment-Bericht konnte nicht geladen werden: " + exception.Message;
            return null;
        }
    }

    public async Task<bool> DeleteLyricsVersionAsync(EditorLyricsVersionItem item,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song || item.Version.SongId != song.Id || !item.CanDelete) return false;
        try
        {
            using var response = await _http.DeleteAsync(
                $"/api/songs/{song.Id}/lyrics/versions/{item.Version.Id}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Status = "Lyrics-Version konnte nicht gelöscht werden: " +
                         await response.Content.ReadAsStringAsync(cancellationToken);
                return false;
            }
            if (_serverVersionId == item.Version.Id)
            {
                _serverVersionId = null;
                _serverRevision = 0;
                _serverVersionStatus = null;
                SaveLocalRecoverySnapshot();
            }
            await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
            Status = $"Revision {item.Version.Revision} wurde gelöscht.";
            return true;
        }
        catch (Exception exception)
        {
            Status = "Lyrics-Version konnte nicht gelöscht werden: " + exception.Message;
            return false;
        }
    }

    public async Task LoadWishEventsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var events = await _http.GetFromJsonAsync<IReadOnlyList<KaraokeEventDto>>("/api/events", cancellationToken) ?? [];
            var selectedId = SelectedWishEvent?.Id;
            WishEvents.Clear();
            foreach (var item in events.OrderByDescending(item => item.StartsAt)) WishEvents.Add(item);
            SelectedWishEvent = WishEvents.FirstOrDefault(item => item.Id == selectedId) ?? WishEvents.FirstOrDefault();
            await LoadWishesAsync(cancellationToken);
        }
        catch (Exception exception) { AppendConsole("Events konnten nicht geladen werden: " + exception.Message); }
    }

    public async Task LoadWishesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshots = await Task.WhenAll(WishEvents.Select(async karaokeEvent =>
            {
                var token = Uri.EscapeDataString(karaokeEvent.InviteToken);
                var items = await _http.GetFromJsonAsync<IReadOnlyList<WishDto>>($"/api/wishlist?eventToken={token}", cancellationToken) ?? [];
                return items.Select(wish => new EditorWishItem(karaokeEvent, wish));
            }));
            var incoming = snapshots.SelectMany(items => items).OrderByDescending(item => item.Wish.RequestedAt).ToList();
            var incomingIds = incoming.Select(item => item.Wish.Id).ToHashSet();
            for (var index = Wishes.Count - 1; index >= 0; index--)
                if (!incomingIds.Contains(Wishes[index].Wish.Id)) Wishes.RemoveAt(index);
            foreach (var wish in incoming)
            {
                var existingIndex = Wishes.ToList().FindIndex(existing => existing.Wish.Id == wish.Wish.Id);
                if (existingIndex >= 0)
                {
                    if (Wishes[existingIndex] != wish) Wishes[existingIndex] = wish;
                    continue;
                }
                var index = 0;
                while (index < Wishes.Count && Wishes[index].Wish.RequestedAt > wish.Wish.RequestedAt) index++;
                Wishes.Insert(index, wish);
            }
            JobState = $"{Wishes.Count} Wünsche aus {WishEvents.Count} Sessions";
        }
        catch (Exception exception) { AppendConsole("Wünsche konnten nicht geladen werden: " + exception.Message); }
    }

    public async Task DeleteSelectedEventAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedWishEvent is null) return;
        try
        {
            using var response = await _http.DeleteAsync($"/api/events/{SelectedWishEvent.Id}?includeOpenWishes=true", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            AppendConsole($"Session '{SelectedWishEvent.Name}' inklusive Wünsche und Queue gelöscht.");
            SelectedWishEvent = null;
            await LoadWishEventsAsync(cancellationToken);
        }
        catch (Exception exception) { AppendConsole("Session konnte nicht gelöscht werden: " + exception.Message); }
    }

    public async Task StartWishProcessingAsync(CancellationToken cancellationToken = default)
    {
        ShowWishlistConsole();
        AppendConsole("> process-wishlist --all-events");
        try
        {
            using var response = await _http.PostAsync("/api/admin/wishlist/process", null, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                AppendConsole("Ein Worker läuft bereits; Status wird übernommen.");
            else response.EnsureSuccessStatusCode();
            await RefreshJobStatusAsync(cancellationToken);
        }
        catch (Exception exception) { AppendConsole("Start fehlgeschlagen: " + exception.Message); }
    }

    public async Task StartWishProcessingAsync(EditorWishItem item, CancellationToken cancellationToken = default)
    {
        ShowWishlistConsole();
        AppendConsole($"> Einzelwunsch: {item.Title} · {item.Event.Name}");
        try
        {
            using var response = await _http.PostAsync(
                $"/api/events/{item.Event.Id}/wishlist/{item.Wish.Id}/process", null, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                AppendConsole("Ein Worker läuft bereits; bitte nach dessen Abschluss erneut starten.");
            else response.EnsureSuccessStatusCode();
            await RefreshJobStatusAsync(cancellationToken);
        }
        catch (Exception exception) { AppendConsole("Einzelwunsch konnte nicht gestartet werden: " + exception.Message); }
    }

    public async Task<bool> RemoveWishAsync(EditorWishItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            var token = Uri.EscapeDataString(item.Event.InviteToken);
            using var response = await _http.DeleteAsync($"/api/wishlist/{item.Wish.Id}?eventToken={token}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            Wishes.Remove(item);
            JobState = $"{Wishes.Count} Wünsche aus {WishEvents.Count} Sessions";
            AppendConsole(Localized($"Wunsch entfernt: {item.Title}", $"Request removed: {item.Title}"));
            return true;
        }
        catch (Exception exception)
        {
            AppendConsole(Localized("Wunsch konnte nicht entfernt werden: ", "Could not remove request: ") + exception.Message);
            return false;
        }
    }

    public async Task<bool> AdoptWishAudioAsync(EditorWishItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync(
                $"/api/admin/events/{item.Event.Id}/wishlist/{item.Wish.Id}/adopt-audio", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            var song = await response.Content.ReadFromJsonAsync<SongDto>(cancellationToken: cancellationToken)
                       ?? throw new InvalidOperationException(Localized("Der Server hat kein Songprojekt bestätigt.",
                           "The server did not confirm a song project."));
            Wishes.Remove(item);
            if (Songs.All(existing => existing.Id != song.Id)) Songs.Add(song);
            else ReplaceSong(song);
            ApplySongFilter();
            Status = Localized(
                $"{song.Title} wurde als ‚Ohne Lyrics‘ übernommen. Lyrics importieren und anschließend neu alignen.",
                $"{song.Title} was adopted as ‘Without Lyrics’. Import lyrics, then run alignment.");
            AppendConsole(Status);
            return true;
        }
        catch (Exception exception)
        {
            AppendConsole(Localized("Audiofund konnte nicht übernommen werden: ",
                "Could not adopt audio candidate: ") + exception.Message);
            return false;
        }
    }

    public async Task SearchAdminWishesAsync(CancellationToken cancellationToken = default)
    {
        var query = AdminWishQuery.Trim();
        if (query.Length < 2) { AdminWishSearchStatus = Localized("Bitte mindestens zwei Zeichen eingeben.", "Enter at least two characters."); return; }
        AdminWishSearching = true;
        AdminWishSearchStatus = Localized("Spotify, Qobuz und LRCLIB werden durchsucht …",
            "Searching Spotify, Qobuz, and LRCLIB …");
        try
        {
            var results = await _http.GetFromJsonAsync<IReadOnlyList<SpotifyTrackDto>>(
                $"/api/wishlist/search?q={Uri.EscapeDataString(query)}", cancellationToken) ?? [];
            AdminWishSearchResults.Clear();
            foreach (var track in results) AdminWishSearchResults.Add(track);
            AdminWishSearchStatus = Localized(
                $"{results.Count} Treffer · ohne passende Lyrics wird automatisch ein Volltranskript erzeugt.",
                $"{results.Count} results · a full transcript is created automatically when lyrics are unavailable.");
        }
        catch (Exception exception) { AdminWishSearchStatus = Localized("Suche fehlgeschlagen: ", "Search failed: ") + exception.Message; }
        finally { AdminWishSearching = false; }
    }

    public async Task ImportAdminWishAsync(SpotifyTrackDto track, CancellationToken cancellationToken = default)
    {
        ShowWishlistConsole();
        AppendConsole($"> Admin-Import: {track.Title} · {track.Artist}");
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/admin/wishlist/import",
                new AddWishRequest(track, "Admin"), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                AppendConsole("Ein Worker läuft bereits. Der Titel bleibt in der Wunschliste und kann danach verarbeitet werden.");
            else response.EnsureSuccessStatusCode();
            AdminWishSearchStatus = Localized($"{track.Title} wurde an die Import-Pipeline übergeben.",
                $"{track.Title} was submitted to the import pipeline.");
            await LoadWishEventsAsync(cancellationToken);
            await RefreshJobStatusAsync(cancellationToken);
        }
        catch (Exception exception) { AdminWishSearchStatus = Localized("Import konnte nicht gestartet werden: ", "Could not start import: ") + exception.Message; }
    }

    public async Task ImportSongAsync(NewSongProjectRequest request, CancellationToken cancellationToken = default)
    {
        ShowWishlistConsole();
        AppendConsole($"> import-song \"{Path.GetFileName(request.AudioPath)}\"");
        try
        {
            await using var stream = File.OpenRead(request.AudioPath);
            using var form = new MultipartFormDataContent();
            using var audio = new StreamContent(stream);
            audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            form.Add(audio, "audio", Path.GetFileName(request.AudioPath));
            form.Add(new StringContent(request.Title), "title");
            form.Add(new StringContent(request.Artist), "artist");
            form.Add(new StringContent(request.Lyrics), "lyrics");
            form.Add(new StringContent(request.UseLrclib ? "true" : "false"), "useLrclib");
            using var response = await _http.PostAsync("/api/admin/song-import", form, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                throw new InvalidOperationException("Es wird bereits ein Songprojekt verarbeitet.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledImportJob = null;
            AppendConsole("Upload abgeschlossen. GPU-Auftrag läuft im Hintergrund.");
            await RefreshImportStatusAsync(cancellationToken);
        }
        catch (Exception exception) { AppendConsole("Import fehlgeschlagen: " + exception.Message); }
    }

    public async Task StartFolderImportAsync(CancellationToken cancellationToken = default)
    {
        var path = FolderImportPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            FolderImportSummary = Localized(
                "Bitte zuerst einen Audio-Ordner mit MP3- oder FLAC-Dateien auswählen.",
                "Please select an audio folder containing MP3 or FLAC files first.");
            return;
        }
        ShowFolderImportConsole();
        AppendConsole($"> import-audio-folder \"{path}\"" + (FolderImportRecursive ? " --recursive" : string.Empty));
        try
        {
            using var response = Directory.Exists(path)
                ? await UploadFolderImportAsync(path, FolderImportRecursive, cancellationToken)
                : await _http.PostAsJsonAsync("/api/admin/folder-import",
                    new FolderImportRequest(path, FolderImportRecursive), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                AppendConsole(Localized(
                    "Ein Audio-Ordnerimport läuft bereits; dessen Status wird angezeigt.",
                    "An audio-folder import is already running; its status is shown."));
            else if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledFolderImportJob = null;
            await RefreshFolderImportStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            FolderImportSummary = "Ordnerimport konnte nicht gestartet werden: " + exception.Message.Trim('"');
            AppendConsole(FolderImportSummary);
        }
    }

    private async Task<HttpResponseMessage> UploadFolderImportAsync(string sourcePath, bool recursive,
        CancellationToken cancellationToken)
    {
        FolderImportSummary = Localized(
            "Lokaler Audio-Ordner wird für den Server vorbereitet …",
            "Preparing the local audio folder for the server …");
        var archivePath = await Task.Run(() => CreateFolderImportArchive(sourcePath, recursive), cancellationToken);
        try
        {
            var archiveSize = new FileInfo(archivePath).Length;
            FolderImportSummary = Localized(
                $"Audio-Ordner wird hochgeladen ({archiveSize / 1024d / 1024d:0.0} MB) …",
                $"Uploading audio folder ({archiveSize / 1024d / 1024d:0.0} MB) …");
            using var transfer = new HttpClient { BaseAddress = ServerAddress, Timeout = Timeout.InfiniteTimeSpan };
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(stream, 1024 * 1024);
            content.Headers.ContentType = new("application/vnd.neonstage.folder-import+zip");
            return await transfer.PostAsync(
                $"/api/admin/folder-import/upload?recursive={recursive.ToString().ToLowerInvariant()}",
                content, cancellationToken);
        }
        finally
        {
            try { File.Delete(archivePath); }
            catch (IOException) { }
        }
    }

    internal static string CreateFolderImportArchive(string sourcePath, bool recursive)
    {
        var root = Path.GetFullPath(sourcePath);
        var audioFiles = Directory.EnumerateFiles(root, "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (audioFiles.Length == 0)
            throw new InvalidOperationException("Der gewählte Ordner enthält keine MP3- oder FLAC-Dateien.");

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var audioPath in audioFiles)
        {
            included.Add(audioPath);
            var directory = Path.GetDirectoryName(audioPath)!;
            var baseName = Path.GetFileNameWithoutExtension(audioPath);
            foreach (var candidate in Directory.EnumerateFiles(directory))
            {
                var candidateName = Path.GetFileName(candidate);
                if (candidateName.Equals(baseName + ".lrc", StringComparison.OrdinalIgnoreCase) ||
                    candidateName.Equals(baseName + ".txt", StringComparison.OrdinalIgnoreCase) ||
                    candidateName.Equals(baseName + ".cover.jpg", StringComparison.OrdinalIgnoreCase))
                    included.Add(candidate);
            }
        }

        var archivePath = Path.Combine(Path.GetTempPath(), $"neonstage-folder-import-{Guid.NewGuid():N}.zip");
        try
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var filePath in included.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(root, filePath).Replace(Path.DirectorySeparatorChar, '/');
                var extension = Path.GetExtension(filePath);
                var compression = extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase) ||
                                  extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.Fastest
                    : CompressionLevel.NoCompression;
                archive.CreateEntryFromFile(filePath, relativePath, compression);
            }
            return archivePath;
        }
        catch
        {
            File.Delete(archivePath);
            throw;
        }
    }

    public async Task StartSelectedSongRealignmentAsync(AlignmentVariantChoice choice,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song)
        {
            Status = "Bitte zuerst einen Song auswählen.";
            return;
        }
        if (Document is not null && !await SaveDraftAsync(cancellationToken, allowTimingConflicts: true))
        {
            AppendConsole("Der aktuelle Editor-Stand konnte nicht als Alignment-Basis gespeichert werden.");
            return;
        }
        ConsoleVisible = true;
        _audio.Stop();
        AppendConsole($"> GPU-Alignment: {song.Title} · {song.Artist}");
        AppendConsole(Localized(
            "  Automatisch bester Lyrics-Treffer · EasyAligner: globales DE/EN-CTC auf dem reinen Vocal-Stem",
            "  Automatic best lyrics match · EasyAligner: global DE/EN CTC on the clean vocal stem"));
        try
        {
            using var response = await _http.PostAsJsonAsync($"/api/admin/songs/{song.Id}/realign",
                new SongRealignmentRequest(_serverVersionId),
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                AppendConsole("Es läuft bereits eine GPU-Neuausrichtung. Es wird kein zweiter Song gestartet.");
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledRealignmentJob = null;
            Status = $"EasyAligner für {song.Title} läuft im Hintergrund …";
            await RefreshRealignmentStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = "Neuausrichtung konnte nicht gestartet werden: " + exception.Message;
            AppendConsole(Status);
        }
    }

    public async Task StartSelectedSongsRealignmentAsync(IReadOnlyList<SongDto> songs,
        AlignmentVariantChoice choice, CancellationToken cancellationToken = default)
    {
        var selectedSongs = songs.DistinctBy(song => song.Id).ToArray();
        if (selectedSongs.Length == 0)
        {
            Status = Localized("Bitte zuerst mindestens einen Song auswählen.",
                "Select at least one song first.");
            return;
        }
        if (selectedSongs.Length == 1)
        {
            if (SelectedSong?.Id != selectedSongs[0].Id) SelectedSong = selectedSongs[0];
            await StartSelectedSongRealignmentAsync(choice, cancellationToken);
            return;
        }
        var alignment = new SongRealignmentRequest();
        ConsoleVisible = true;
        _audio.Stop();
        AppendConsole(Localized(
            $"> GPU-Neuausrichtung: {selectedSongs.Length} ausgewählte Songs",
            $"> GPU realignment: {selectedSongs.Length} selected songs"));
        foreach (var song in selectedSongs)
            AppendConsole($"  • {song.Title} · {song.Artist}");
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/admin/songs/realign-selection",
                new SongSelectionRealignmentRequest(
                    selectedSongs.Select(song => song.Id).ToArray(), alignment),
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                AppendConsole(Localized("Es läuft bereits eine GPU-Neuausrichtung.",
                    "A GPU realignment is already running."));
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    await response.Content.ReadAsStringAsync(cancellationToken));
            _handledRealignmentJob = null;
            Status = Localized(
                $"{selectedSongs.Length} ausgewählte Songs laufen nacheinander durch das GPU-Alignment …",
                $"{selectedSongs.Length} selected songs are being processed by GPU alignment …");
            await RefreshRealignmentStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = Localized("Auswahl-Alignment konnte nicht gestartet werden: ",
                "Could not start selection alignment: ") + exception.Message;
            AppendConsole(Status);
        }
    }

    public async Task StartAllSongsRealignmentAsync(AlignmentVariantChoice choice,
        CancellationToken cancellationToken = default)
    {
        var alignment = new SongRealignmentRequest();
        ConsoleVisible = true;
        _audio.Stop();
        AppendConsole("> GPU-Neuausrichtung: gesamte Bibliothek");
        AppendConsole(Localized(
            "  EasyAligner: globales DE/EN-CTC auf dem reinen Vocal-Stem",
            "  EasyAligner: global DE/EN CTC on the clean vocal stem"));
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/admin/songs/realign-all",
                alignment, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                AppendConsole("Es läuft bereits eine GPU-Neuausrichtung.");
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledRealignmentJob = null;
            Status = "EasyAligner für die gesamte Bibliothek läuft im Hintergrund …";
            await RefreshRealignmentStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = "Bibliotheks-Alignment konnte nicht gestartet werden: " + exception.Message;
            AppendConsole(Status);
        }
    }

    public async Task CancelRealignmentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.DeleteAsync(
                "/api/admin/song-realignment", cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                Status = Localized("Es läuft derzeit kein Alignment.",
                    "No alignment is currently running.");
                AppendConsole(Status);
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    await response.Content.ReadAsStringAsync(cancellationToken));
            Status = Localized(
                "Abbruch angefordert; aktive und wartende Alignments werden beendet …",
                "Cancellation requested; active and queued alignments are being stopped …");
            AppendConsole(Status);
            await RefreshRealignmentStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = Localized("Alignment-Abbruch fehlgeschlagen: ",
                "Could not cancel alignment: ") + exception.Message.Trim('"');
            AppendConsole(Status);
        }
    }

    public async Task ObserveReplacementLyricsAlignmentAsync(SongDto song,
        RetrieveReplacementLyricsResultDto result, CancellationToken cancellationToken = default)
    {
        ConsoleVisible = true;
        _audio.Stop();
        if (result.IsFullTranscript)
        {
            AppendConsole(Localized(
                $"> Volltranskript: {song.Title} · {song.Artist}",
                $"> Full transcript: {song.Title} · {song.Artist}"));
            AppendConsole(Localized(
                "  Audio → vollständige Lyrics-Erkennung → EasyAligner → neuer Review-Stand",
                "  Audio → complete lyrics recognition → EasyAligner → new review version"));
            _handledLyricsRecognitionJob = null;
            Status = Localized(
                $"Vollständige Lyrics für {song.Title} werden aus dem Audio erkannt …",
                $"Complete lyrics for {song.Title} are being recognized from audio …");
            await RefreshLyricsRecognitionStatusAsync(cancellationToken);
            return;
        }
        AppendConsole(Localized(
            $"> Neue Lyrics: {result.Source} #{result.SourceId} · {song.Title} · {song.Artist}",
            $"> New lyrics: {result.Source} #{result.SourceId} · {song.Title} · {song.Artist}"));
        AppendConsole(Localized(
            "  Ausgewählte Provider-Fassung → EasyAligner Direct → neuer Review-Stand",
            "  Selected provider version → EasyAligner Direct → new review version"));
        _handledRealignmentJob = null;
        Status = Localized(
            $"Neue Lyrics von {result.Source} werden mit EasyAligner ausgerichtet …",
            $"New lyrics from {result.Source} are being aligned with EasyAligner …");
        await RefreshRealignmentStatusAsync(cancellationToken);
    }

    public async Task<bool> WaitForReplacementLyricsAlignmentAsync(bool fullTranscript = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid? jobId = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fullTranscript)
                {
                    var recognition = await _http.GetFromJsonAsync<EditorLyricsRecognitionStatus>(
                        "/api/admin/song-lyrics-recognition", cancellationToken);
                    if (recognition?.JobId is null)
                    {
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }
                    jobId ??= recognition.JobId;
                    await RefreshLyricsRecognitionStatusAsync(cancellationToken);
                    if (recognition.JobId == jobId && !recognition.IsRunning)
                        return recognition.ExitCode == 0;
                }
                else
                {
                    var alignment = await _http.GetFromJsonAsync<EditorRealignmentStatus>(
                        "/api/admin/song-realignment", cancellationToken);
                    if (alignment?.JobId is null)
                    {
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }
                    jobId ??= alignment.JobId;
                    await RefreshRealignmentStatusAsync(cancellationToken);
                    if (alignment.JobId == jobId && !alignment.IsRunning)
                        return alignment.ExitCode == 0;
                }
                await Task.Delay(750, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            Status = Localized("Alignment-Status konnte nicht gelesen werden: ",
                "Could not read alignment status: ") + exception.Message;
            AppendConsole(Status);
            return false;
        }
    }

    public async Task StartCompleteLyricsRecognitionAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song)
        {
            Status = Localized("Bitte zuerst einen Song auswählen.", "Select a song first.");
            return;
        }
        // Preserve the current manual state as an immutable version before the
        // generated transcript becomes a separate review candidate.
        if (Document is not null && !await SaveDraftAsync(cancellationToken, allowTimingConflicts: true))
            return;
        ShowWishlistConsole();
        _audio.Stop();
        AppendConsole(Localized($"> Vollständige Lyrics aus Audio erkennen: {song.Title} · {song.Artist}",
            $"> Recognize complete lyrics from audio: {song.Title} · {song.Artist}"));
        try
        {
            using var response = await _http.PostAsync(
                $"/api/admin/songs/{song.Id}/recognize-lyrics", null, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                Status = Localized("Es läuft bereits eine vollständige Lyrics-Erkennung.",
                    "A complete lyrics recognition job is already running.");
                AppendConsole(Status);
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledLyricsRecognitionJob = null;
            Status = Localized(
                $"Volltext-Erkennung für {song.Title} wurde in die GPU-Queue gestellt …",
                $"Complete transcription for {song.Title} was queued for the GPU …");
            await RefreshLyricsRecognitionStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = Localized("Vollständige Lyrics-Erkennung konnte nicht gestartet werden: ",
                "Complete lyrics recognition could not be started: ") + exception.Message;
            AppendConsole(Status);
        }
    }

    private async Task RefreshJobStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<EditorJobStatus>("/api/admin/wishlist-processing", cancellationToken);
            if (status is null) return;
            _wishWorkerRunning = status.IsRunning;
            JobState = status.IsRunning ? $"GPU-WORKER · {status.Message}" : status.Message;
            WishProgress = status.Percent;
            WishProgressLabel = status.Total == 0 ? status.Message :
                $"{status.Percent}% · Wunsch {Math.Min(status.Current, status.Total)} von {status.Total}";
            foreach (var line in status.RecentOutput)
                if (_seenConsoleOutput.Add("wish:" + line)) AppendConsole(line);
            // Wunsch- und Bibliotheksänderungen kommen über den Change-Stream.
        }
        catch { /* Statuspolling darf den Editor niemals blockieren. */ }
        await RefreshImportStatusAsync(cancellationToken);
        await RefreshRealignmentStatusAsync(cancellationToken);
        await RefreshLyricsRecognitionStatusAsync(cancellationToken);
        await RefreshFolderImportStatusAsync(cancellationToken);
    }

    private async Task RefreshLyricsRecognitionStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<EditorLyricsRecognitionStatus>(
                "/api/admin/song-lyrics-recognition", cancellationToken);
            if (status?.JobId is null) return;
            if (status.IsRunning)
            {
                JobState = $"FULL LYRICS · {status.Percent}% · {status.Message}";
                Status = status.Message;
            }
            foreach (var line in status.RecentOutput)
                if (_seenConsoleOutput.Add("recognition:" + status.JobId + ":" + line)) AppendConsole(line);
            if (status.IsRunning || _handledLyricsRecognitionJob == status.JobId) return;
            _handledLyricsRecognitionJob = status.JobId;
            if (status.ExitCode != 0)
            {
                Status = Localized(
                    "Vollständige Lyrics-Erkennung fehlgeschlagen. Details stehen in der Konsole.",
                    "Complete lyrics recognition failed. See the console for details.");
                return;
            }
            await ReloadSongsAsync(cancellationToken);
            if (status.SongId is { } songId && SelectedSong?.Id == songId)
                await LoadRealignmentForReviewAsync(songId, cancellationToken);
            else
                Status = Localized(
                    "Vollständig erkannte Lyrics stehen als neuer Review-Stand bereit.",
                    "The completely recognized lyrics are available as a new review version.");
        }
        catch { /* Hintergrundstatus darf die Editorbedienung nicht blockieren. */ }
    }

    private async Task RefreshFolderImportStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<FolderImportStatus>("/api/admin/folder-import", cancellationToken);
            if (status?.JobId is null) return;
            FolderImportProgress = status.Percent;
            FolderImportSummary = status.IsRunning
                ? $"{status.Percent}% · Datei {Math.Min(status.Current, status.Total)} von {status.Total} · " +
                  $"{status.Succeeded} fertig, {status.Review} Review, {status.Failed} Fehler"
                : $"{status.Message} · {status.Succeeded} fertig, {status.Review} Review, " +
                  $"{status.Failed} Fehler, {status.Skipped} übersprungen";
            if (status.IsRunning) JobState = $"AUDIO-ORDNERIMPORT · {status.Percent}% · {status.Message}";
            foreach (var line in status.RecentOutput)
                if (_seenConsoleOutput.Add("folder:" + status.JobId + ":" + line)) AppendConsole(line);
            if (status.IsRunning || _handledFolderImportJob == status.JobId) return;
            _handledFolderImportJob = status.JobId;
            await ReloadSongsAsync(cancellationToken);
        }
        catch { /* Der Hintergrundstatus darf die Editorbedienung nicht blockieren. */ }
    }

    private async Task RefreshRealignmentStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<EditorRealignmentStatus>(
                "/api/admin/song-realignment", cancellationToken);
            if (status?.JobId is null) return;
            if (status.IsRunning)
            {
                JobState = $"AUDIO ANALYSE · {status.Percent}% · {status.Message}";
                Status = status.Message;
            }
            foreach (var line in status.RecentOutput)
                if (_seenConsoleOutput.Add("realign:" + status.JobId + ":" + line)) AppendConsole(line);
            if (status.IsRunning || _handledRealignmentJob == status.JobId) return;
            _handledRealignmentJob = status.JobId;
            if (status.ExitCode == -2)
            {
                Status = Localized(
                    "GPU-Analyse wurde abgebrochen; es werden keine weiteren Jobs gestartet.",
                    "GPU analysis was cancelled; no further jobs will be started.");
                AppendConsole(Status);
                return;
            }
            if (status.ExitCode != 0)
            {
                Status = Localized(
                    "GPU-Analyse fehlgeschlagen. Details stehen in der Konsole.",
                    "GPU analysis failed. See the console for details.");
                return;
            }
            await ReloadSongsAsync(cancellationToken);
            if (status.SongIds is { Count: > 0 } selectedSongIds)
            {
                foreach (var selectedSongId in selectedSongIds)
                    _realignedSongsPendingReview.Add(selectedSongId);
                if (SelectedSong is { } selectedSong && selectedSongIds.Contains(selectedSong.Id))
                    await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
                Status = status.Message;
                AppendConsole(Status);
            }
            else if (status.SongId is { } songId)
            {
                if (SelectedSong?.Id == songId)
                {
                    _realignedSongsPendingReview.Add(songId);
                    await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
                    Status = Localized(
                        "GPU-Analyse abgeschlossen. Das Ergebnis steht unter Lyrics-Versionen bereit; der aktuelle Arbeitsstand blieb geladen.",
                        "GPU analysis complete. The result is available under lyrics versions; the current working version remains loaded.");
                    AppendConsole(Status);
                }
                else
                    _realignedSongsPendingReview.Add(songId);
            }
            else
            {
                await ReloadSongsAsync(cancellationToken);
                Status = Localized(
                    "GPU-Analyse der Bibliothek abgeschlossen. Neue Ergebnisse stehen im Review bereit.",
                    "GPU library analysis complete. New results are ready for review.");
                AppendConsole(Status);
            }
        }
        catch { /* Hintergrundstatus darf die Editorbedienung nicht blockieren. */ }
    }

    private async Task LoadRealignmentForReviewAsync(Guid songId, CancellationToken cancellationToken,
        LyricsDto? suppliedLyrics = null)
    {
        var lyrics = suppliedLyrics ?? await _http.GetFromJsonAsync<LyricsDto>(
            $"/api/admin/songs/{songId}/lyrics/source", cancellationToken);
        if (lyrics is null) return;
        var serverVersion = await _http.GetFromJsonAsync<LyricsVersionDto>(
            $"/api/songs/{songId}/lyrics/editor", cancellationToken);
        Document = serverVersion is null
            ? LyricsDocumentImporter.Import(lyrics)
            : JsonSerializer.Deserialize<LyricsEditorDocument>(serverVersion.DocumentJson, JsonOptions);
        if (Document is null) throw new InvalidOperationException("Die neue Alignment-Version ist nicht lesbar.");
        LyricsDocumentImporter.IgnoreStructureMarkers(Document);
        _loadedSourceFingerprint = JsonSerializer.Serialize(lyrics, JsonOptions);
        History.Clear();
        _serverVersionId = serverVersion?.Id;
        _serverRevision = serverVersion?.Revision ?? 0;
        _serverVersionStatus = serverVersion?.Status;
        SetAlignmentReport(serverVersion?.AlignmentReportJson);
        SelectedSegment = null;
        LoopEnabled = false;
        LoopStart = null;
        LoopEnd = null;
        if (Document.Lines.Count > 0)
            AnchorPosition(Document.Lines.SelectMany(line => line.Children.Count > 0 ? line.Children : [line])
                .Select(segment => segment.Start).DefaultIfEmpty(TimeSpan.Zero).Min());
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
        Status = serverVersion is null
            ? "Neues GPU-Alignment geladen, aber noch nicht serverseitig versioniert."
            : $"Neues GPU-Alignment als Revision {_serverRevision} geladen – jetzt prüfen und anschließend freigeben.";
        AppendConsole(Status);
    }

    private async Task RefreshImportStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<EditorImportStatus>("/api/admin/song-import", cancellationToken);
            if (status?.JobId is null) return;
            if (status.IsRunning || !_wishWorkerRunning)
                JobState = status.IsRunning ? $"SONG IMPORT · {status.Percent}% · {status.Message}" : status.Message;
            foreach (var line in status.RecentOutput)
                if (_seenConsoleOutput.Add("import:" + status.JobId + ":" + line)) AppendConsole(line);
            if (!status.IsRunning && _handledImportJob != status.JobId)
            {
                _handledImportJob = status.JobId;
                await ReloadSongsAsync(cancellationToken);
            }
        }
        catch { }
    }

    private async Task ReloadSongsAsync(CancellationToken cancellationToken)
    {
        var songs = await _http.GetFromJsonAsync<IReadOnlyList<SongDto>>("/api/songs?take=500&includeUnreleased=true", cancellationToken) ?? [];
        var selectedId = SelectedSong?.Id;
        Songs.Clear();
        foreach (var song in songs.OrderBy(song => song.Artist).ThenBy(song => song.Title)) Songs.Add(song);
        ApplySongFilter();
        SelectedSong = Songs.FirstOrDefault(song => song.Id == selectedId);
    }

    private async Task MergeNewSongsAsync(CancellationToken cancellationToken)
    {
        var songs = await _http.GetFromJsonAsync<IReadOnlyList<SongDto>>("/api/songs?take=500&includeUnreleased=true", cancellationToken) ?? [];
        foreach (var song in songs.Where(song => Songs.All(existing => existing.Id != song.Id)))
        {
            var songIndex = 0;
            while (songIndex < Songs.Count && CompareSongs(Songs[songIndex], song) <= 0) songIndex++;
            Songs.Insert(songIndex, song);
            if (MatchesSongFilter(song))
            {
                var filteredIndex = 0;
                while (filteredIndex < FilteredSongs.Count && CompareSongs(FilteredSongs[filteredIndex], song) <= 0) filteredIndex++;
                FilteredSongs.Insert(filteredIndex, song);
            }
        }
    }

    private async Task RunChangeFeedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/changes/stream");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    if (line == "event: library-changed")
                        await Dispatcher.UIThread.InvokeAsync(async () => await HandleLibraryChangedAsync(cancellationToken));
                    else if (line == "event: lyrics-version-changed")
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                            await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false));
                    else if (line == "event: wishlist-changed")
                        await Dispatcher.UIThread.InvokeAsync(async () => await LoadWishEventsAsync(cancellationToken));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { await Task.Delay(1500, cancellationToken); }
        }
    }

    private async Task HandleLibraryChangedAsync(CancellationToken cancellationToken)
    {
        await MergeNewSongsAsync(cancellationToken);
        await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
        if (SelectedSong is not { } song || Document is null || _serverVersionId is not null ||
            History.CanUndo || string.IsNullOrEmpty(_loadedSourceFingerprint)) return;
        try
        {
            var lyrics = await _http.GetFromJsonAsync<LyricsDto>(
                $"/api/admin/songs/{song.Id}/lyrics/source", cancellationToken);
            if (lyrics is null) return;
            var fingerprint = JsonSerializer.Serialize(lyrics, JsonOptions);
            if (fingerprint == _loadedSourceFingerprint) return;
            await LoadRealignmentForReviewAsync(song.Id, cancellationToken, lyrics);
            Status = "Extern aktualisiertes Alignment automatisch geladen.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppendConsole("Alignment-Aktualisierung konnte nicht geladen werden: " + exception.Message);
        }
    }

    private static int CompareSongs(SongDto left, SongDto right)
    {
        var artist = string.Compare(left.Artist, right.Artist, StringComparison.CurrentCultureIgnoreCase);
        return artist != 0 ? artist : string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool MatchesSongFilter(SongDto song)
    {
        var needle = SongFilter.Trim();
        var matchesStatus = SongStatusFilter == "In Review"
            ? song.LibraryCategory == SongLibraryCategory.KaraokeReady &&
              song.ReviewStatus == SongReviewStatus.InReview
            : SongStatusFilter == Localized("Freigegeben", "Released")
                ? song.LibraryCategory == SongLibraryCategory.KaraokeReady &&
                  song.ReviewStatus == SongReviewStatus.Approved
                : SongStatusFilter == Localized(
                    "In Review · synchronisierte Lyrics", "In review · synchronized lyrics")
                    ? song.LibraryCategory == SongLibraryCategory.KaraokeReady &&
                      song.ReviewStatus == SongReviewStatus.InReview &&
                      song.HasSynchronizedLyrics
                    : SongStatusFilter == Localized(
                        "In Review · keine synchronisierten Lyrics",
                        "In review · no synchronized lyrics")
                        ? song.LibraryCategory == SongLibraryCategory.KaraokeReady &&
                          song.ReviewStatus == SongReviewStatus.InReview &&
                          !song.HasSynchronizedLyrics
                        : SongStatusFilter == Localized("Ohne Lyrics", "Without lyrics")
                            ? song.LibraryCategory == SongLibraryCategory.WithoutLyrics
                            : true;
        return matchesStatus && (needle.Length == 0 || song.Title.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
               song.Artist.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
               song.Album.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
    }

    public async Task ApproveSelectedSongAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSong is null) return;
        if (SelectedSong.ReviewStatus == SongReviewStatus.Approved)
        {
            using var reviewResponse = await _http.PutAsJsonAsync(
                $"/api/admin/songs/{SelectedSong.Id}/review-status",
                new ChangeSongReviewStatusRequest(SongReviewStatus.InReview), cancellationToken);
            if (!reviewResponse.IsSuccessStatusCode)
            {
                Status = Localized("Der Song konnte nicht zurück in Review gesetzt werden: ",
                    "The song could not be returned to review: ") +
                    await reviewResponse.Content.ReadAsStringAsync(cancellationToken);
                return;
            }
            var reviewed = await reviewResponse.Content.ReadFromJsonAsync<SongDto>(cancellationToken: cancellationToken);
            if (reviewed is null) return;
            ReplaceSong(reviewed);
            SelectedSong = reviewed;
            ApplySongFilter();
            Status = Localized("Song zurück in Review gesetzt – er ist nicht mehr auf der Stage verfügbar.",
                "Song returned to review – it is no longer available on the stage.");
            return;
        }
        if (SelectedSong.LibraryCategory == SongLibraryCategory.WithoutLyrics ||
            !SelectedSong.HasLyrics || !SelectedSong.HasInstrumental || !SelectedSong.HasVocals)
        {
            Status = Localized(
                "Freigabe noch nicht möglich: Lyrics importieren und den Song anschließend neu alignen, damit beide Stems vorliegen.",
                "Release is not available yet: import lyrics, then realign the song so both stems are available.");
            return;
        }
        if (Document is null) { Status = "Freigabe nicht möglich: Lyrics konnten nicht geladen werden."; return; }
        var openSegments = Document.Segments.Count(segment => segment.RequiresReview);
        var timingConflicts = TimelineEditing.ValidateHierarchy(Document).Count +
                              TimelineEditing.ValidateLineSequence(Document).Count;
        if (!await SaveDraftAsync(cancellationToken, allowTimingConflicts: true)) return;
        if (_serverVersionId is null) { Status = "Freigabe nicht möglich: Review-Entwurf wurde nicht gespeichert."; return; }
        using (var publishResponse = await _http.PostAsJsonAsync(
                   $"/api/songs/{SelectedSong.Id}/lyrics/versions/{_serverVersionId}/publish",
                   new ChangeLyricsVersionStatusRequest(_serverRevision, AllowTimingConflicts: true), cancellationToken))
        {
            if (!publishResponse.IsSuccessStatusCode)
            {
                Status = "Freigabe nicht möglich: Die gespeicherte Lyrics-Version konnte nicht veröffentlicht werden. " +
                         await publishResponse.Content.ReadAsStringAsync(cancellationToken);
                return;
            }
            var published = await publishResponse.Content.ReadFromJsonAsync<LyricsVersionDto>(cancellationToken: cancellationToken);
            if (published is null) { Status = "Freigabe nicht möglich: Der Server hat die veröffentlichte Lyrics-Version nicht bestätigt."; return; }
            _serverRevision = published.Revision;
            _serverVersionStatus = published.Status;
            Document.Revision = published.Revision;
            Document.Status = LyricsReviewStatus.Published;
        }
        using var response = await _http.PutAsJsonAsync($"/api/admin/songs/{SelectedSong.Id}/review-status",
            new ChangeSongReviewStatusRequest(SongReviewStatus.Approved), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Status = "Freigabe abgelehnt: " + await response.Content.ReadAsStringAsync(cancellationToken);
            return;
        }
        var updated = await response.Content.ReadFromJsonAsync<SongDto>(cancellationToken: cancellationToken);
        if (updated is null) return;
        ReplaceSong(updated);
        SelectedSong = updated;
        await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
        var acceptedWarnings = new List<string>();
        if (openSegments > 0) acceptedWarnings.Add($"{openSegments} automatische Prüfhinweise");
        if (timingConflicts > 0) acceptedWarnings.Add($"{timingConflicts} Timing-Konflikte");
        Status = acceptedWarnings.Count > 0
            ? $"Song freigegeben – er ist jetzt auf der Stage verfügbar ({string.Join(" und ", acceptedWarnings)} wurden bewusst übernommen)."
            : "Song freigegeben – er ist jetzt auf der Stage verfügbar.";
    }

    public async Task<bool> DeleteSelectedSongAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSong is null) return false;
        var deleting = SelectedSong;
        _audio.Stop();
        using var response = await _http.DeleteAsync($"/api/admin/songs/{deleting.Id}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Status = "Song konnte nicht gelöscht werden: " + await response.Content.ReadAsStringAsync(cancellationToken);
            return false;
        }
        Songs.Remove(Songs.First(song => song.Id == deleting.Id));
        var filtered = FilteredSongs.FirstOrDefault(song => song.Id == deleting.Id);
        if (filtered is not null) FilteredSongs.Remove(filtered);
        SelectedSong = null; Document = null; Waveform = null;
        Status = "Song und alle zugehörigen Bibliotheksdateien wurden gelöscht.";
        return true;
    }

    public async Task RefreshSongVideoAsync(CancellationToken cancellationToken = default)
    {
        _songVideo = null;
        if (SelectedSong is { } song)
        {
            try
            {
                using var response = await _http.GetAsync($"/api/songs/{song.Id}/video/info", cancellationToken);
                if (response.IsSuccessStatusCode)
                    _songVideo = await response.Content.ReadFromJsonAsync<SongVideoInfoDto>(
                        cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                // Eine optionale Video-Vorschau darf das Lyrics-Editing nicht blockieren.
            }
        }
        OnPropertyChanged(nameof(HasSongVideo));
        OnPropertyChanged(nameof(SongVideoOffsetMilliseconds));
        if (_stageTest.IsRunning && SelectedSong is not null && Document is not null)
            await _stageTest.ReplaceStateAsync(CreateStageSongState(), cancellationToken);
    }

    public async Task<bool> RemoveSongVideoAsync(SongDto song, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.DeleteAsync($"/api/admin/songs/{song.Id}/video", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Status = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? Localized("Für diesen Song ist kein Video hinterlegt.",
                        "No video is assigned to this song.")
                    : Localized("Das Video konnte nicht entfernt werden: ",
                        "Could not remove the video: ") +
                      (await response.Content.ReadAsStringAsync(cancellationToken)).Trim('"');
                AppendConsole(Status);
                return false;
            }

            if (SelectedSong?.Id == song.Id) await RefreshSongVideoAsync(cancellationToken);
            Status = Localized($"Video von „{song.Title}“ wurde entfernt.",
                $"Video for “{song.Title}” was removed.");
            AppendConsole(Status);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            Status = Localized("Das Video konnte nicht entfernt werden: ",
                "Could not remove the video: ") + exception.Message.Trim('"');
            AppendConsole(Status);
            return false;
        }
    }

    public async Task RemoveAllSongsFromStageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync(
                "/api/admin/songs/remove-all-from-stage", null, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    await response.Content.ReadAsStringAsync(cancellationToken));
            var result = await response.Content.ReadFromJsonAsync<RemoveSongsFromStageResultDto>(
                cancellationToken: cancellationToken);
            await ReloadSongsAsync(cancellationToken);
            Status = Localized(
                $"Stage geleert: {result?.SongsRemoved ?? 0} Songs wurden auf „In Review“ gesetzt.",
                $"Stage cleared: {result?.SongsRemoved ?? 0} songs were moved to “In Review”.");
            AppendConsole(Status);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            Status = Localized("Die Stage konnte nicht geleert werden: ",
                "Could not clear the stage: ") + exception.Message.Trim('"');
            AppendConsole(Status);
        }
    }

    public async Task DeleteAllLyricsVersionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _audio.Stop();
            using var response = await _http.DeleteAsync(
                "/api/admin/lyrics/versions", cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    await response.Content.ReadAsStringAsync(cancellationToken));
            var result = await response.Content.ReadFromJsonAsync<DeleteAllLyricsVersionsResultDto>(
                cancellationToken: cancellationToken);
            var localRecoveries = await _draftRecovery.DeleteAllAsync(cancellationToken);
            _handledRealignmentJob = null;
            _realignedSongsPendingReview.Clear();
            await ReloadSongsAsync(cancellationToken);
            if (SelectedSong is not null) await LoadSelectedSongAsync(cancellationToken);
            Status = Localized(
                $"Versionsspeicher aufgeräumt: {result?.VersionsDeleted ?? 0} Serverversionen und {localRecoveries} lokale Recoverys gelöscht; {result?.SongsRemoved ?? 0} Songs von der Stage entfernt.",
                $"Version store cleaned: {result?.VersionsDeleted ?? 0} server versions and {localRecoveries} local recoveries deleted; {result?.SongsRemoved ?? 0} songs removed from the stage.");
            AppendConsole(Status);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or
                                          JsonException or IOException or UnauthorizedAccessException)
        {
            Status = Localized("Die Lyrics-Versionen konnten nicht vollständig gelöscht werden: ",
                "Could not completely delete the lyrics versions: ") + exception.Message.Trim('"');
            AppendConsole(Status);
        }
    }

    private void ReplaceSong(SongDto updated)
    {
        var index = Songs.ToList().FindIndex(song => song.Id == updated.Id);
        if (index >= 0) Songs[index] = updated;
        ApplySongFilter();
    }

    private void AppendConsole(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        ConsoleLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (ConsoleLines.Count > 250) ConsoleLines.RemoveAt(0);
    }

    public async Task LoadSelectedSongAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSong is null) return;
        _seekCancellation?.Cancel();
        _songLoadCancellation?.Cancel();
        _songLoadCancellation?.Dispose();
        _songLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loadCancellation = _songLoadCancellation;
        var ct = loadCancellation.Token;
        var song = SelectedSong;
        var loadFreshAlignment = _realignedSongsPendingReview.Remove(song.Id);
        _audio.Stop();
        _loadedAudioSongId = null;
        _selectedPlaybackUri = null;
        _localVocalUri = null;
        _localInstrumentalUri = null;
        _localOriginalUri = null;
        LoopEnabled = false;
        LoopStart = null;
        LoopEnd = null;
        _hasVocalStem = false;
        HasInstrumentalStem = false;
        _serverVersionId = null;
        _serverRevision = 0;
        _serverVersionStatus = null;
        SetAlignmentReport(null);
        _loadedSourceFingerprint = null;
        History.Clear();
        _visualBeatTimes = [];
        Document = null;
        Waveform = null;
        Cover = null;
        _songVideo = null;
        OnPropertyChanged(nameof(HasSongVideo));
        OnPropertyChanged(nameof(SongVideoOffsetMilliseconds));
        LyricsVersions.Clear();
        OnPropertyChanged(nameof(LyricsVersionsHeading));
        Playhead = TimeSpan.Zero;
        SelectedSegment = null;
        Busy = true;
        OnPropertyChanged(nameof(SongHeading));
        try
        {
            if (_loadVisualAssets) await LoadCoverAsync(song.Id, ct);
            try
            {
                var visualization = await _http.GetFromJsonAsync<SongVisualizationDto>(
                    $"/api/songs/{song.Id}/visualization", ct);
                _visualBeatTimes = visualization?.Frames
                    .Where(frame => frame.Beat)
                    .Select(frame => frame.TimeSeconds)
                    .OrderBy(value => value)
                    .ToArray() ?? [];
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                // Waveform editing remains available when no beat analysis exists.
                _visualBeatTimes = [];
            }
            await RefreshSongVideoAsync(ct);
            Status = "KI-Alignment wird geladen …";
            var stems = await _http.GetFromJsonAsync<StemAvailabilityDto>($"/api/songs/{song.Id}/stems", ct);
            _hasVocalStem = stems?.HasVocals == true;
            HasInstrumentalStem = stems?.HasInstrumental == true;
            if (!HasInstrumentalStem) InstrumentalEnabled = false;
            LyricsDto? currentSource = null;
            using (var sourceResponse = await _http.GetAsync($"/api/admin/songs/{song.Id}/lyrics/source", ct))
                if (sourceResponse.IsSuccessStatusCode)
                    currentSource = await sourceResponse.Content.ReadFromJsonAsync<LyricsDto>(cancellationToken: ct);
            var currentSourceFingerprint = currentSource is null
                ? null
                : JsonSerializer.Serialize(currentSource, JsonOptions);
            var recovery = loadFreshAlignment ? null : await _draftRecovery.LoadAsync(song.Id, ct);
            if (recovery?.SourceFingerprint is { Length: > 0 } recoverySource &&
                currentSourceFingerprint is not null && recoverySource != currentSourceFingerprint)
            {
                // Keep the recovery file on disk, but do not let a document
                // based on an older alignment hide newly generated timings.
                recovery = null;
                loadFreshAlignment = true;
            }
            using var draftResponse = loadFreshAlignment
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : await _http.GetAsync($"/api/songs/{song.Id}/lyrics/editor", ct);
            LyricsVersionDto? serverDraft = null;
            if (draftResponse.IsSuccessStatusCode)
            {
                serverDraft = await draftResponse.Content.ReadFromJsonAsync<LyricsVersionDto>(cancellationToken: ct);
            }
            var useRecovery = recovery is not null &&
                              (serverDraft is null || recovery.SavedAt > serverDraft.UpdatedAt.AddSeconds(1));
            if (useRecovery)
            {
                Document = JsonSerializer.Deserialize<LyricsEditorDocument>(recovery!.DocumentJson, JsonOptions);
                LyricsVersionDto? recoveryVersion = null;
                if (recovery.ServerVersionId is { } recoveryVersionId)
                {
                    using var recoveryVersionResponse = await _http.GetAsync(
                        $"/api/songs/{song.Id}/lyrics/versions/{recoveryVersionId}", ct);
                    if (recoveryVersionResponse.IsSuccessStatusCode)
                        recoveryVersion = await recoveryVersionResponse.Content
                            .ReadFromJsonAsync<LyricsVersionDto>(cancellationToken: ct);
                }
                // Eine lokale Recovery darf niemals an einen anderen, nur zufällig
                // neuesten Serverentwurf gebunden werden. Entweder gehört ihre
                // exakte Version noch zu einem bearbeitbaren Stand, oder Speichern
                // legt bewusst eine neue Revision an.
                var connectedRecovery = recoveryVersion is not null &&
                                        recoveryVersion.Revision == recovery.ServerRevision &&
                                        CanContinueAsWorkingVersion(recoveryVersion.Status);
                _serverVersionId = connectedRecovery ? recoveryVersion!.Id : null;
                _serverRevision = connectedRecovery ? recoveryVersion!.Revision : 0;
                _serverVersionStatus = connectedRecovery ? recoveryVersion!.Status : null;
                SetAlignmentReport(connectedRecovery
                    ? recoveryVersion!.AlignmentReportJson
                    : serverDraft?.AlignmentReportJson);
                _loadedSourceFingerprint = currentSourceFingerprint;
                Status = connectedRecovery
                    ? Localized("Lokalen Recovery-Arbeitsstand mit seiner Serverrevision geladen.",
                        "Loaded the local recovery working version with its server revision.")
                    : Localized("Lokale Recovery als ungespeicherte Arbeitskopie geladen – bitte speichern.",
                        "Loaded local recovery as an unsaved working copy — please save it.");
            }
            else if (serverDraft is not null)
            {
                Document = JsonSerializer.Deserialize<LyricsEditorDocument>(serverDraft.DocumentJson, JsonOptions);
                _serverVersionId = serverDraft.Id;
                _serverRevision = serverDraft.Revision;
                _serverVersionStatus = serverDraft.Status;
                SetAlignmentReport(serverDraft.AlignmentReportJson);
                _loadedSourceFingerprint = currentSourceFingerprint;
            }
            else
            {
                LyricsDto? lyrics = currentSource;
                if (!loadFreshAlignment)
                {
                    using var lyricsResponse = await _http.GetAsync($"/api/songs/{song.Id}/lyrics", ct);
                    lyrics = lyricsResponse.IsSuccessStatusCode
                        ? await lyricsResponse.Content.ReadFromJsonAsync<LyricsDto>(cancellationToken: ct)
                        : null;
                }
                Document = lyrics is null ? null : LyricsDocumentImporter.Import(lyrics);
                _loadedSourceFingerprint = currentSourceFingerprint;
                _serverVersionId = null;
                _serverRevision = 0;
                _serverVersionStatus = null;
                SetAlignmentReport(null);
            }
            var ignoredStructureMarkers = Document is null
                ? 0
                : LyricsDocumentImporter.IgnoreStructureMarkers(Document);
            History.Clear();
            if (Document is { Lines.Count: > 0 })
                AnchorPosition(Document.Lines.SelectMany(line => line.Children.Count > 0 ? line.Children : [line])
                    .Select(segment => segment.Start).DefaultIfEmpty(TimeSpan.Zero).Min());
            OnPropertyChanged(nameof(ReviewSummary));
            Status = ignoredStructureMarkers > 0
                ? Localized($"Alignment bereit – {ignoredStructureMarkers} Strukturmarker ignoriert.",
                    $"Alignment ready — ignored {ignoredStructureMarkers} structure marker(s).")
                : Document is null ? "Keine Lyrics verfügbar" : loadFreshAlignment
                ? "Neues GPU-Alignment als Review-Stand geladen"
                : "Alignment bereit zur Prüfung";
            await RefreshLyricsVersionsAsync(ct, reportErrors: false);
            // Auch ein ausdrücklich übernommener Song ohne Lyrics braucht im
            // Editor sofort eine Waveform. Bis Stems existieren, ist der
            // Originalsong die stabile Bearbeitungsspur.
            {
                Status = Document is null ? "Original-Waveform wird vorbereitet …" : "Vocal-Waveform wird vorbereitet …";
                var vocals = new Uri(ServerAddress, $"/api/songs/{song.Id}/stems/vocals?format=flac");
                var instrumental = new Uri(ServerAddress, $"/api/songs/{song.Id}/stems/instrumental?format=flac");
                var original = new Uri(ServerAddress, $"/api/songs/{song.Id}/audio");
                try
                {
                    if (Document is null || !_hasVocalStem) throw new InvalidOperationException("Keine Vocalspur vorhanden.");
                    Status = "Vocalspur wird lokal für sample-stabiles Editing vorbereitet …";
                    _localVocalUri = await _audioCache.GetAsync(song.Id, "vocals", vocals, ct,
                        stems?.Revision);
                    if (HasInstrumentalStem)
                        _localInstrumentalUri = await _audioCache.GetAsync(song.Id, "instrumental", instrumental, ct,
                            stems?.Revision);
                    Waveform = await _waveforms.LoadAsync(song.Id, _localVocalUri, ct);
                    _selectedPlaybackUri = _localVocalUri;
                }
                catch (InvalidOperationException) when (!ct.IsCancellationRequested)
                {
                    Status = "Vocalspur fehlt – Waveform wird aus dem Originalsong erzeugt …";
                    _localOriginalUri = await _audioCache.GetAsync(song.Id, "original", original, ct);
                    Waveform = await _waveforms.LoadAsync(song.Id, _localOriginalUri, ct, "original");
                    _selectedPlaybackUri = _localOriginalUri;
                }
                Status = Document is null
                    ? "Song ohne Lyrics geladen · Original-Waveform bereit · Lyrics importieren und danach neu alignen"
                    : "Alignment und Vocal-Waveform bereit zur Prüfung";
            }
            if (_stageTest.IsRunning && Document is not null)
                await _stageTest.ReplaceStateAsync(CreateStageSongState(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) { Status = "Song konnte nicht geladen werden: " + exception.Message; }
        finally { if (ReferenceEquals(_songLoadCancellation, loadCancellation)) Busy = false; }
    }

    private async Task LoadCoverAsync(Guid songId, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync($"/api/songs/{songId}/cover", cancellationToken);
            if (SelectedSong?.Id != songId) return;
            using var stream = new MemoryStream(bytes, writable: false);
            Cover = new Bitmap(stream);
        }
        catch (HttpRequestException) { if (SelectedSong?.Id == songId) Cover = null; }
    }

    public async Task UploadCoverAsync(string path, CancellationToken cancellationToken = default)
    {
        if (SelectedSong is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (new FileInfo(path).Length > 20 * 1024 * 1024)
        {
            Status = "Das Cover darf höchstens 20 MiB groß sein.";
            return;
        }
        var song = SelectedSong;
        Status = "Cover wird zugeschnitten und konvertiert …";
        await using var input = File.OpenRead(path);
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(input);
        form.Add(content, "file", Path.GetFileName(path));
        using var response = await _http.PostAsync($"/api/admin/songs/{song.Id}/cover", form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Status = "Cover konnte nicht gespeichert werden: " + await response.Content.ReadAsStringAsync(cancellationToken);
            return;
        }
        var updated = song with { HasCover = true };
        ReplaceSong(updated);
        SelectedSong = updated;
        await LoadCoverAsync(song.Id, cancellationToken);
        if (_stageTest.IsRunning && Document is not null)
            await _stageTest.ReplaceStateAsync(CreateStageSongState(), cancellationToken);
        Status = "Cover gespeichert · quadratisch, 1024 × 1024 JPEG";
    }

    public async Task<bool> SaveDraftAsync(CancellationToken cancellationToken = default, bool allowTimingConflicts = false)
    {
        if (SelectedSong is null || Document is null)
        {
            Status = "Speichern noch nicht möglich: Kein Songdokument geladen.";
            return false;
        }
        await _draftSaveLock.WaitAsync(cancellationToken);
        var songId = SelectedSong.Id;
        try
        {
            var validationErrors = TimelineEditing.ValidateHierarchy(Document);
            var lineErrors = TimelineEditing.ValidateLineSequence(Document);
            if (lineErrors.Count > 0 && !allowTimingConflicts)
            {
                Status = $"Nicht gespeichert: {lineErrors[0]} Zeilen dürfen sich niemals überschneiden.";
                return false;
            }
            // Wort-/Silbenkonflikte sind gerade der Grund, warum ein Stand im
            // Editor überprüft wird. Sie werden angezeigt, dürfen aber weder
            // Recovery noch eine nachvollziehbare Serverversion verhindern.
            // Zeilenüberschneidungen bleiben dagegen ein harter Fehler, weil die
            // Stage Zeilenblöcke eindeutig fortlaufend benötigt.
            var timingWarnings = validationErrors.Count + lineErrors.Count;
            var timingWarning = timingWarnings > 0
                ? $" · {timingWarnings} Timing-Hinweis(e) bleiben zur Prüfung markiert"
                : string.Empty;
            var createNewVersion = _serverVersionId is null || _serverVersionStatus == LyricsVersionStatus.Published;
            Document.Status = LyricsReviewStatus.InReview;
            Document.ModifiedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(Document, JsonOptions);
            await _draftRecovery.SaveAsync(songId,
                new RecoveryDraft(DateTimeOffset.UtcNow, json, _serverVersionId, _serverRevision,
                    _loadedSourceFingerprint), cancellationToken);
            Status = "Lokale Sicherung erstellt · Server speichert …";
            HttpResponseMessage response;
            if (createNewVersion)
                response = await _http.PostAsJsonAsync($"/api/songs/{songId}/lyrics/versions",
                    new CreateLyricsVersionRequest(json, Document.AnalysisRunId,
                        AllowTimingConflicts: allowTimingConflicts,
                        AlignmentReportJson: _alignmentReportJson), cancellationToken);
            else
                response = await _http.PutAsJsonAsync($"/api/songs/{songId}/lyrics/versions/{_serverVersionId}",
                    new UpdateLyricsVersionRequest(_serverRevision, json,
                        AllowTimingConflicts: allowTimingConflicts,
                        AlignmentReportJson: _alignmentReportJson), cancellationToken);
            using (response)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    Status = "Versionskonflikt: Der lokale Arbeitsstand bleibt gesichert; Serverversion bitte neu laden.";
                    return false;
                }
                response.EnsureSuccessStatusCode();
                var saved = await response.Content.ReadFromJsonAsync<LyricsVersionDto>(cancellationToken: cancellationToken);
                _serverVersionId = saved?.Id;
                _serverRevision = saved?.Revision ?? _serverRevision;
                _serverVersionStatus = saved?.Status;
                SetAlignmentReport(saved?.AlignmentReportJson);
                Document.Revision = _serverRevision;
                if (saved is null) throw new InvalidOperationException("Server hat keine gespeicherte Revision zurückgegeben.");
                var verified = await _http.GetFromJsonAsync<LyricsVersionDto>(
                    $"/api/songs/{songId}/lyrics/versions/{saved.Id}", cancellationToken);
                if (verified?.Revision != saved.Revision)
                    throw new InvalidOperationException("Gespeicherte Serverrevision konnte nicht kontrollgelesen werden.");
                await _draftRecovery.SaveAsync(songId,
                    new RecoveryDraft(DateTimeOffset.UtcNow, json, saved.Id, saved.Revision,
                        _loadedSourceFingerprint), cancellationToken);
                await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
                Status = $"Dauerhaft gespeichert · Server bestätigt Revision {_serverRevision}{timingWarning}";
                return true;
            }
        }
        catch (Exception exception)
        {
            Status = "Server-Speicherung fehlgeschlagen; lokale Recovery bleibt erhalten: " + exception.Message;
            return false;
        }
        finally { _draftSaveLock.Release(); }
    }

    public async Task PlayPauseAsync()
    {
        if (SelectedSong is null) return;
        if (!await _playbackCommandLock.WaitAsync(0)) { Status = "Playback wird bereits vorbereitet …"; return; }
        try
        {
            await _pendingSeek;
            if (_audio.IsPlaying)
            {
                _audio.Pause();
                await _stageTest.SendTransportAsync(StageTestProtocol.Pause, Playhead);
                OnPropertyChanged(nameof(IsAudioPlaying));
                return;
            }
            if (_loadedAudioSongId == SelectedSong.Id)
            {
                _audio.Resume();
                await _stageTest.SendTransportAsync(StageTestProtocol.Play, Playhead, true);
                OnPropertyChanged(nameof(IsAudioPlaying));
                return;
            }
            var requestedPosition = Playhead;
            _visualClockSuspended = true;
            var vocals = _hasVocalStem ? _localVocalUri : _selectedPlaybackUri;
            if (_stageTestAudioMuted)
            {
                // Im Stage-Test erzeugt ausschließlich Unity den hörbaren Stem-Mix.
                // Der stumme Originalstream bleibt hier nur als Editor-Masterclock aktiv.
                var clockSource = _localOriginalUri ?? new Uri(ServerAddress,
                    $"/api/songs/{SelectedSong.Id}/audio");
                await _audio.PlayAsync(clockSource, startPosition: requestedPosition);
            }
            else if (InstrumentalEnabled && HasInstrumentalStem)
            {
                var instrumental = _localInstrumentalUri ?? throw new InvalidOperationException("Instrumentalspur ist noch nicht lokal vorbereitet.");
                var vocal = vocals ?? throw new InvalidOperationException("Vocalspur ist noch nicht lokal vorbereitet.");
                Status = "Einspuriger Preview-Mix wird vorbereitet …";
                var mix = await _audioCache.GetMixAsync(SelectedSong.Id, instrumental, vocal,
                    InstrumentalVolume, VocalVolume, _songLoadCancellation?.Token ?? CancellationToken.None);
                _audio.Volume = _stageTestAudioMuted ? 0 : 100;
                await _audio.PlayAsync(mix, startPosition: requestedPosition);
            }
            else
            {
                var source = vocals ?? new Uri(ServerAddress, $"/api/songs/{SelectedSong.Id}/audio");
                await _audio.PlayAsync(source, startPosition: requestedPosition);
            }
            _loadedAudioSongId = SelectedSong.Id;
            AnchorPosition(_audio.Position);
            await _stageTest.SendTransportAsync(StageTestProtocol.Play, requestedPosition, true);
            OnPropertyChanged(nameof(IsAudioPlaying));
        }
        catch (Exception exception) { Status = "Playback konnte nicht gestartet werden: " + exception.Message; }
        finally { _visualClockSuspended = false; _playbackCommandLock.Release(); }
    }

    public async Task ToggleStageTestAsync()
    {
        if (_stageTest.IsRunning)
        {
            await _stageTest.StopAsync();
            OnPropertyChanged(nameof(IsStageTestRunning));
            OnPropertyChanged(nameof(StageTestActionLabel));
            return;
        }
        if (SelectedSong is null || Document is null)
        {
            Status = "Für den Stage-Test bitte zuerst einen Song mit Lyrics laden.";
            return;
        }
        try
        {
            StageTestStatus = "Stage-Test wird gestartet …";
            var continuePlaying = _audio.IsPlaying;
            var editorPosition = Playhead;
            MuteEditorAudioForStageTest();
            // Bereits geladene Vocal-/Instrumental-Stems werden aus dem Editor
            // entfernt. Für die Masterclock genügt während des Tests der stumme
            // Originalstream; den hörbaren Stem-Mix erzeugt allein Unity.
            _audio.Stop();
            _loadedAudioSongId = null;
            await _stageTest.StartAsync(CreateStageSongState(continuePlaying, editorPosition));
            MuteEditorAudioForStageTest();
            if (continuePlaying)
            {
                var clockSource = _localOriginalUri ?? new Uri(ServerAddress,
                    $"/api/songs/{SelectedSong.Id}/audio");
                await _audio.PlayAsync(clockSource, startPosition: editorPosition);
                _loadedAudioSongId = SelectedSong.Id;
                AnchorPosition(_audio.Position);
            }
            OnPropertyChanged(nameof(IsStageTestRunning));
            OnPropertyChanged(nameof(StageTestActionLabel));
        }
        catch (Exception exception)
        {
            await _stageTest.StopAsync();
            RestoreEditorAudioAfterStageTest();
            StageTestStatus = "Stage-Test konnte nicht gestartet werden: " + exception.Message;
        }
    }

    public async Task ExportMp4Async(string outputPath, CancellationToken cancellationToken = default)
    {
        if (SelectedSong is null || Document is null)
        {
            Status = "Für den MP4-Export bitte zuerst einen Song mit Lyrics laden.";
            return;
        }
        if (Mp4ExportRunning) return;
        Mp4ExportRunning = true;
        Mp4ExportStatus = "Unity-Renderer wird für den MP4-Export gestartet …";
        _mp4ExportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Mp4ExportStatus = "Originalaudio wird lokal für den Export vorbereitet …";
            _localOriginalUri ??= await _audioCache.GetAsync(SelectedSong.Id, "original",
                new Uri(ServerAddress, $"/api/songs/{SelectedSong.Id}/audio"),
                _mp4ExportCancellation.Token);
            var snapshot = CreateStageSongState(false, TimeSpan.Zero);
            snapshot.exportAudioPath = _localOriginalUri.LocalPath;
            Mp4ExportStatus = "Unity-Renderer wird für den MP4-Export gestartet …";
            var completedPath = await _stageTest.ExportAsync(snapshot, outputPath,
                cancellationToken: _mp4ExportCancellation.Token);
            Mp4ExportStatus = "MP4 fertig: " + completedPath;
            Status = "MP4-Export abgeschlossen.";
        }
        catch (OperationCanceledException) { Mp4ExportStatus = "MP4-Export abgebrochen."; }
        catch (Exception exception)
        {
            Mp4ExportStatus = "MP4-Export fehlgeschlagen: " + exception.Message;
            Status = Mp4ExportStatus;
        }
        finally
        {
            await _stageTest.StopAsync();
            _mp4ExportCancellation.Dispose();
            _mp4ExportCancellation = null;
            Mp4ExportRunning = false;
        }
    }

    public void CancelMp4Export()
    {
        if (!Mp4ExportRunning) return;
        Mp4ExportStatus = "MP4-Export wird abgebrochen …";
        _mp4ExportCancellation?.Cancel();
    }

    private void MuteEditorAudioForStageTest()
    {
        if (!_stageTestAudioMuted)
        {
            _stageTestPreviousMasterVolume = _audio.Volume;
            _stageTestPreviousVocalVolume = _audio.VocalVolume;
            _stageTestAudioMuted = true;
        }
        _audio.Volume = 0;
        _audio.VocalVolume = 0;
    }

    private void RestoreEditorAudioAfterStageTest()
    {
        if (!_stageTestAudioMuted) return;
        _audio.Stop();
        _loadedAudioSongId = null;
        _stageTestAudioMuted = false;
        _audio.Volume = _stageTestPreviousMasterVolume;
        _audio.VocalVolume = _stageTestPreviousVocalVolume;
    }

    public async Task StopPlaybackAsync()
    {
        _audio.Stop();
        _loadedAudioSongId = null;
        AnchorPosition(TimeSpan.Zero);
        await _stageTest.SendTransportAsync(StageTestProtocol.Stop, TimeSpan.Zero);
        OnPropertyChanged(nameof(IsAudioPlaying));
        Status = "Wiedergabe gestoppt";
    }

    public void Seek(TimeSpan position)
    {
        Playhead = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        AnchorPosition(Playhead);
        _ = _stageTest.SendTransportAsync(StageTestProtocol.Seek, Playhead, _audio.IsPlaying);
        if (_audio.IsSeekable)
        {
            _visualClockSuspended = true;
            var previous = Interlocked.Exchange(ref _seekCancellation, new CancellationTokenSource());
            previous?.Cancel();
            previous?.Dispose();
            _pendingSeek = SeekLoadedAudioAsync(Playhead, _seekCancellation.Token);
        }
    }

    private async Task SeekLoadedAudioAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        try
        {
            Status = "Audio wird an der gewählten Position vorbereitet …";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                _songLoadCancellation?.Token ?? CancellationToken.None);
            await _audio.SeekAsync(position, linked.Token);
            if (cancellationToken.IsCancellationRequested) return;
            AnchorPosition(_audio.Position);
            Status = _audio.IsPlaying ? "Wiedergabe läuft" : "Position bereit";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = "Seek fehlgeschlagen: " + exception.Message; }
        finally { if (!cancellationToken.IsCancellationRequested) _visualClockSuspended = false; }
    }

    private async Task SeekLoopAsync(TimeSpan loopStart, TimeSpan loopEnd, TimeSpan observedPosition)
    {
        try
        {
            _visualClockSuspended = true;
            // Renderframes können die Grenze geringfügig überschreiten. Den
            // Überhang übernehmen, damit der Loop langfristig nicht driftet.
            var overshoot = observedPosition - loopEnd;
            var target = loopStart + TimeSpan.FromMilliseconds(Math.Clamp(overshoot.TotalMilliseconds, 0, 40));
            await _audio.SeekAsync(target, _songLoadCancellation?.Token ?? CancellationToken.None);
            AnchorPosition(_audio.Position);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Status = "Loop-Sprung fehlgeschlagen: " + exception.Message; }
        finally { _visualClockSuspended = false; Interlocked.Exchange(ref _loopSeekInProgress, 0); }
    }

    public async Task PlaySelectedBoundaryAsync()
    {
        if (SelectedSegment is null) { Status = "Bitte zuerst ein Segment auswählen."; return; }
        var start = SelectedSegment.Start > TimeSpan.FromMilliseconds(300)
            ? SelectedSegment.Start - TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;
        var end = SelectedSegment.Start + TimeSpan.FromMilliseconds(300);
        SetLoopRange(start, end);
        Seek(start);
        await _pendingSeek;
        if (!_audio.IsPlaying) await PlayPauseAsync();
    }

    private TimeSpan FirstVocalPosition() => Document is { Lines.Count: > 0 }
        ? Document.Lines.SelectMany(line => line.Children.Count > 0 ? line.Children : [line])
            .Select(segment => segment.Start).DefaultIfEmpty(TimeSpan.Zero).Min()
        : TimeSpan.Zero;

    private void AnchorPosition(TimeSpan position)
    {
        _positionAnchor = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        _positionAnchorTimestamp = Stopwatch.GetTimestamp();
        Playhead = _positionAnchor;
    }

    private void ObserveDecoderPosition(TimeSpan position)
    {
        if (_visualClockSuspended) return;
        // Jede Decoderzeit verankert die geglättete Darstellungsuhr neu. Zwischen
        // zwei VLC-Meldungen interpoliert der Render-Timer; er läuft daher niemals
        // dauerhaft auf einer von Audio unabhängigen Uhr.
        AnchorPosition(position);
    }

    public void Undo()
    {
        if (!History.Undo()) { Status = "Nichts zum Rückgängigmachen."; return; }
        RefreshAfterHistory("Änderung rückgängig gemacht.");
    }
    public void Redo()
    {
        if (!History.Redo()) { Status = "Nichts zum Wiederholen."; return; }
        RefreshAfterHistory("Änderung wiederholt.");
    }

    private void RefreshAfterHistory(string status)
    {
        CommitBeatPreviewEdits();
        if (SelectedSegment is not null && Document is not null &&
            !Document.Segments.Any(segment => segment.Id == SelectedSegment.Id))
            SelectedSegment = null;
        PreviewRevision++;
        TimelineRevision++;
        Status = status;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(SelectedSegmentTiming));
        OnPropertyChanged(nameof(SelectedSegmentDetails));
        OnPropertyChanged(nameof(SelectedSegmentText));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        QueueStageLyricsUpdate();
    }

    public void SelectSegment(LyricSegment? segment)
    {
        SelectedSegment = segment is null || Document is null
            ? null
            : Document.Segments.FirstOrDefault(candidate => candidate.Id == segment.Id);
    }

    private void RefreshTimingProjection()
    {
        TimingPreviewDocument = KaraokeTimingEnabled && Document is { UsesUltraStarTiming: false } document
            ? KaraokeTimingProjection.Create(document, _visualBeatTimes)
            : Document;
        CaptureTimingProjectionBaseline();
        PreviewRevision++;
        TimelineRevision++;
    }

    private void CaptureTimingProjectionBaseline()
    {
        _timingProjectionBaseline.Clear();
        if (!KaraokeTimingActive || TimingPreviewDocument is null ||
            ReferenceEquals(TimingPreviewDocument, Document)) return;
        foreach (var segment in TimingPreviewDocument.Segments)
            _timingProjectionBaseline[segment.Id] = (segment.Start, segment.End);
    }

    private int CommitBeatPreviewEdits()
    {
        if (!KaraokeTimingActive || Document is null || TimingPreviewDocument is null ||
            ReferenceEquals(Document, TimingPreviewDocument)) return 0;
        var canonical = Document.Segments.ToDictionary(segment => segment.Id);
        var changed = 0;
        foreach (var preview in TimingPreviewDocument.Segments)
        {
            if (!_timingProjectionBaseline.TryGetValue(preview.Id, out var baseline) ||
                (preview.Start == baseline.Start && preview.End == baseline.End) ||
                !canonical.TryGetValue(preview.Id, out var target)) continue;
            target.Start = preview.Start;
            target.End = preview.End;
            target.KaraokeTimingLocked = true;
            target.IsManuallyAdjusted = true;
            target.Origin = SegmentOrigin.ManuallyAdjusted;
            target.RequiresReview = true;
            changed++;
        }
        CaptureTimingProjectionBaseline();
        return changed;
    }

    public void SelectNextReviewSegment()
    {
        if (Document is null) return;
        var candidates = Document.Segments.Where(segment => segment.RequiresReview)
            .OrderBy(segment => segment.Start).ThenBy(segment => segment.Type).ToList();
        if (candidates.Count == 0) { Status = "Keine ungeprüften Segmente mehr."; return; }
        var current = SelectedSegment is null ? -1 : candidates.FindIndex(segment => segment.Id == SelectedSegment.Id);
        SelectedSegment = candidates[(current + 1) % candidates.Count];
        Seek(SelectedSegment.Start > TimeSpan.FromMilliseconds(300)
            ? SelectedSegment.Start - TimeSpan.FromMilliseconds(300) : TimeSpan.Zero);
    }

    public void MarkSelectedReviewed()
    {
        if (SelectedSegment is null) return;
        History.Execute(new SetReviewStateCommand(SelectedSegment, true));
        OnPropertyChanged(nameof(SelectedSegmentDetails));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
    }

    public void MoveSelected(int milliseconds)
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable } segment) return;
        var line = FindLine(segment);
        if (line is null) return;
        var delta = TimeSpan.FromMilliseconds(milliseconds);
        History.Execute(new EditSegmentTreeCommand(line, "Segment verschieben", () =>
        {
            if (segment.Type == LyricSegmentType.Line)
            {
                var (previousEnd, nextStart) = LineNeighborBounds(segment);
                TimelineEditing.MoveLineWithinNeighbors(segment, delta, previousEnd, nextStart);
            }
            else if (segment.Type == LyricSegmentType.Word)
            {
                var index = line.Children.IndexOf(segment);
                var minimumStart = index > 0 ? line.Children[index - 1].End : line.Start;
                var maximumEnd = index + 1 < line.Children.Count ? line.Children[index + 1].Start : line.End;
                if (segment.Start + delta < minimumStart) delta = minimumStart - segment.Start;
                if (segment.End + delta > maximumEnd) delta = maximumEnd - segment.End;
                TimelineEditing.MoveWithChildren(segment, delta);
            }
            else if (FindParentWord(segment) is { } word) TimelineEditing.MoveSyllable(segment, word, delta);
        }));
        RefreshEditor();
    }

    public void ShiftAllLyrics(int milliseconds)
    {
        if (Document is null || SelectedSong is null)
        {
            Status = Localized("Bitte zuerst einen Song mit Lyrics laden.",
                "Load a song with lyrics first.");
            return;
        }
        if (milliseconds == 0)
        {
            Status = Localized("Der globale Versatz beträgt 0 ms; es wurde nichts verändert.",
                "The global shift is 0 ms; nothing was changed.");
            return;
        }

        var delta = TimeSpan.FromMilliseconds(milliseconds);
        try
        {
            History.Execute(new EditSegmentForestCommand(Document.Lines,
                Localized("Alle Lyrics zeitlich verschieben", "Shift all lyrics"),
                () => TimelineEditing.ShiftDocument(Document, delta,
                    TimeSpan.FromSeconds(SelectedSong.DurationSeconds))));
        }
        catch (InvalidOperationException exception)
        {
            Status = Localized(exception.Message, exception.Message switch
            {
                "Das Lyrics-Dokument enthält keine Zeilen." => "The lyrics document contains no lines.",
                "Der globale Versatz würde Lyrics vor den Songanfang verschieben." =>
                    "The global shift would move lyrics before the start of the song.",
                "Der globale Versatz würde Lyrics hinter das Songende verschieben." =>
                    "The global shift would move lyrics beyond the end of the song.",
                _ => exception.Message
            });
            return;
        }

        // The audio/playhead remains on the same physical sample. Only the
        // lyrics move relative to it, which makes the correction observable
        // immediately in the live Stage preview.
        RefreshEditor();
        Status = Localized(
            $"Alle Lyrics wurden um {milliseconds:+#;-#;0} ms verschoben. Rückgängig ist verfügbar.",
            $"All lyrics were shifted by {milliseconds:+#;-#;0} ms. Undo is available.");
    }

    public void SetKaraokeColors(KaraokeColorSettings colors)
    {
        if (Document is null) return;
        Document.KaraokeColors = colors.Normalized();
        RefreshTimingProjection();
        RefreshEditor();
        Status = Localized("Lyrics-Farben gespeichert.", "Lyrics colors saved.");
    }

    public void ResizeSelected(bool startEdge, int milliseconds)
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable } segment) return;
        var line = FindLine(segment);
        if (line is null) return;
        var delta = TimeSpan.FromMilliseconds(milliseconds);
        try
        {
            History.Execute(new EditSegmentTreeCommand(line, startEdge ? "Segmentanfang ändern" : "Segmentende ändern", () =>
            {
                var start = startEdge ? segment.Start + delta : segment.Start;
                var end = startEdge ? segment.End : segment.End + delta;
                if (segment.Type == LyricSegmentType.Line)
                {
                    var (previousEnd, nextStart) = LineNeighborBounds(segment);
                    TimelineEditing.ResizeLineWithinNeighbors(segment, start, end, previousEnd, nextStart,
                        TimeSpan.FromMilliseconds(100));
                }
                else if (segment.Type == LyricSegmentType.Word)
                {
                    var index = line.Children.IndexOf(segment);
                    var minimumStart = index > 0 ? line.Children[index - 1].End : line.Start;
                    var maximumEnd = index + 1 < line.Children.Count ? line.Children[index + 1].Start : line.End;
                    start = start < minimumStart ? minimumStart : start;
                    end = end > maximumEnd ? maximumEnd : end;
                    TimelineEditing.ResizeWord(segment, start, end, TimeSpan.FromMilliseconds(35));
                }
                else if (FindParentWord(segment) is { } word)
                    TimelineEditing.ResizeSyllable(segment, word, start, end, TimeSpan.FromMilliseconds(25));
            }));
        }
        catch (InvalidOperationException exception) { Status = exception.Message; return; }
        RefreshEditor();
    }

    public void AddSyllable()
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Syllable } syllable ||
            FindParentWord(syllable) is not { } word || FindLine(word) is not { } line) return;
        if ((syllable.End - syllable.Start).TotalMilliseconds < 70) { Status = "Die Silbe ist zum Teilen zu kurz."; return; }
        var split = syllable.Start + TimeSpan.FromTicks((syllable.End - syllable.Start).Ticks / 2);
        var created = NewSegment(word.Id, LyricSegmentType.Syllable, split, syllable.End, "…");
        var index = word.Children.IndexOf(syllable);
        History.Execute(new EditSegmentTreeCommand(line, "Silbe einfügen", () =>
        {
            syllable.End = split;
            TimelineEditing.MarkAdjusted(syllable);
            if (!word.Children.Contains(created)) word.Children.Insert(index + 1, created);
            TimelineEditing.FitParentToChildren(word);
            TimelineEditing.SynchronizeWordText(word);
            TimelineEditing.SynchronizeLineText(line);
        }));
        SelectedSegment = created;
        RefreshEditor();
    }

    public void AddWord()
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Word } word || FindLine(word) is not { } line) return;
        if ((word.End - word.Start).TotalMilliseconds < 100) { Status = "Das Wort ist zum Teilen zu kurz."; return; }
        var originalEnd = word.End;
        var split = word.Start + TimeSpan.FromTicks((word.End - word.Start).Ticks / 2);
        var created = NewSegment(line.Id, LyricSegmentType.Word, split, originalEnd, "Neues Wort");
        created.Children.Add(NewSegment(created.Id, LyricSegmentType.Syllable, split, originalEnd, "…"));
        var index = line.Children.IndexOf(word);
        History.Execute(new EditSegmentTreeCommand(line, "Wort einfügen", () =>
        {
            TimelineEditing.ResizeWord(word, word.Start, split, TimeSpan.FromMilliseconds(35));
            if (!line.Children.Contains(created)) line.Children.Insert(index + 1, created);
            TimelineEditing.MarkAdjusted(created);
            TimelineEditing.SynchronizeLineText(line);
        }));
        SelectedSegment = created;
        RefreshEditor();
    }

    public void AddLine(bool duplicate)
    {
        if (Document is null) return;
        var source = SelectedSegment is null ? Document.Lines.LastOrDefault() : FindLine(SelectedSegment);
        var index = source is null ? Document.Lines.Count : Document.Lines.IndexOf(source) + 1;
        var duration = source is null ? TimeSpan.FromSeconds(2) : source.End - source.Start;
        if (duration < TimeSpan.FromMilliseconds(200)) duration = TimeSpan.FromSeconds(2);
        var start = source?.End ?? Playhead;
        var nextStart = index < Document.Lines.Count ? Document.Lines[index].Start : (TimeSpan?)null;
        if (nextStart is { } boundary)
        {
            var available = boundary - start;
            if (available < TimeSpan.FromMilliseconds(100))
            {
                Status = "Zwischen diesen Zeilen ist kein Platz für einen weiteren Zeilenblock.";
                return;
            }
            if (duration > available) duration = available;
        }
        var created = duplicate && source is not null
            ? CloneSegmentTree(source, null, start - source.Start, duration)
            : CreateBlankLine(start, duration);
        History.Execute(new EditLineCollectionCommand(Document.Lines,
            duplicate ? "Zeilenblock duplizieren" : "Zeilenblock einfügen", () =>
            {
                if (!Document.Lines.Contains(created)) Document.Lines.Insert(Math.Min(index, Document.Lines.Count), created);
                TimelineEditing.MarkAdjusted(created);
            }));
        SelectedSegment = created;
        RefreshEditor();
    }

    public string CopyLyricsSegments(IEnumerable<LyricSegment> selection)
    {
        var payload = LyricsSegmentClipboard.Create(selection);
        var count = payload.Segments.Count;
        Status = EditorLocale.German
            ? count == 1 ? "Lyrics-Segment kopiert." : $"{count} Lyrics-Segmente kopiert."
            : count == 1 ? "Lyrics segment copied." : $"{count} lyrics segments copied.";
        return LyricsSegmentClipboard.Serialize(payload);
    }

    public void CutLyricsSegments(IEnumerable<LyricSegment> selection)
    {
        if (Document is null) throw new InvalidOperationException("Bitte zuerst einen Song laden.");
        var payload = LyricsSegmentClipboard.Create(selection);
        var roots = LyricsSegmentClipboard.NormalizeSelection(selection).ToList();
        var documentSegments = Document.Segments.ToHashSet();
        if (roots.Any(segment => !documentSegments.Contains(segment)))
            throw new InvalidOperationException("Die Auswahl gehört nicht zum aktuellen Song.");

        LyricSegment? fallback;
        if (payload.SegmentType == LyricSegmentType.Line)
        {
            if (Document.Lines.Count - roots.Count < 1)
                throw new InvalidOperationException("Der letzte Zeilenblock kann nicht ausgeschnitten werden.");
            History.Execute(new EditLineCollectionCommand(Document.Lines, "Zeilen ausschneiden", () =>
            {
                foreach (var line in roots) Document.Lines.Remove(line);
            }));
            fallback = Document.Lines.OrderBy(line => line.Start)
                .FirstOrDefault(line => line.Start >= Playhead) ?? Document.Lines.OrderBy(line => line.Start).LastOrDefault();
        }
        else
        {
            var affectedLines = roots.Select(segment => FindLine(segment) ??
                    throw new InvalidOperationException("Ein ausgewähltes Segment besitzt keine Zeile."))
                .Distinct().ToList();
            if (payload.SegmentType == LyricSegmentType.Word &&
                roots.GroupBy(FindLine).Any(group => group.Key is null || group.Key.Children.Count <= group.Count()))
                throw new InvalidOperationException("Das letzte Wort einer Zeile kann nicht ausgeschnitten werden.");
            if (payload.SegmentType == LyricSegmentType.Syllable &&
                roots.GroupBy(FindParentWord).Any(group => group.Key is null || group.Key.Children.Count <= group.Count()))
                throw new InvalidOperationException("Die letzte Silbe eines Wortes kann nicht ausgeschnitten werden.");

            History.Execute(new EditSegmentForestCommand(affectedLines, "Lyrics-Segmente ausschneiden", () =>
            {
                if (payload.SegmentType == LyricSegmentType.Word)
                {
                    foreach (var group in roots.GroupBy(segment => FindLine(segment)!))
                    {
                        foreach (var word in group) group.Key.Children.Remove(word);
                        TimelineEditing.SynchronizeLineText(group.Key);
                    }
                }
                else
                {
                    foreach (var group in roots.GroupBy(segment => FindParentWord(segment)!))
                    {
                        foreach (var syllable in group) group.Key.Children.Remove(syllable);
                        TimelineEditing.FitParentToChildren(group.Key);
                        TimelineEditing.SynchronizeWordText(group.Key);
                        if (FindLine(group.Key) is { } line) TimelineEditing.SynchronizeLineText(line);
                    }
                }
            }));
            fallback = payload.SegmentType == LyricSegmentType.Syllable
                ? FindParentWord(roots[0])
                : affectedLines[0];
        }

        SelectedSegment = fallback;
        Status = EditorLocale.German
            ? roots.Count == 1 ? "Lyrics-Segment ausgeschnitten." : $"{roots.Count} Lyrics-Segmente ausgeschnitten."
            : roots.Count == 1 ? "Lyrics segment cut." : $"{roots.Count} lyrics segments cut.";
        RefreshEditor();
    }

    public IReadOnlyList<LyricSegment> PasteLyricsSegments(string clipboardText)
    {
        if (Document is null) throw new InvalidOperationException("Bitte zuerst einen Song laden.");
        var payload = LyricsSegmentClipboard.Deserialize(clipboardText);
        var inserted = payload.SegmentType switch
        {
            LyricSegmentType.Line => PasteLines(payload),
            LyricSegmentType.Word => PasteWords(payload),
            LyricSegmentType.Syllable => PasteSyllables(payload),
            _ => throw new InvalidOperationException("Dieser Lyrics-Segmenttyp kann nicht eingefügt werden.")
        };
        SelectedSegment = inserted[0];
        Status = EditorLocale.German
            ? inserted.Count == 1 ? "Lyrics-Segment am Abspielcursor eingefügt." : $"{inserted.Count} Lyrics-Segmente am Abspielcursor eingefügt."
            : inserted.Count == 1 ? "Lyrics segment pasted at the playhead." : $"{inserted.Count} lyrics segments pasted at the playhead.";
        RefreshEditor();
        return inserted;
    }

    private IReadOnlyList<LyricSegment> PasteLines(LyricsSegmentClipboardPayload payload)
    {
        var created = LyricsSegmentClipboard.Instantiate(payload, Playhead, null).ToList();
        EnsureNoOverlap(created, Document!.Lines, useEffectiveBounds: true);
        History.Execute(new EditLineCollectionCommand(Document.Lines, "Zeilen einfügen", () =>
        {
            foreach (var line in created)
                if (!Document.Lines.Contains(line)) Document.Lines.Add(line);
            SortSegments(Document.Lines);
        }));
        return created;
    }

    private IReadOnlyList<LyricSegment> PasteWords(LyricsSegmentClipboardPayload payload)
    {
        var line = ResolvePasteLine();
        var created = LyricsSegmentClipboard.Instantiate(payload, Playhead, line.Id).ToList();
        if (created.Any(word => word.Start < line.Start || word.End > line.End))
            throw new InvalidOperationException("Die eingefügten Wörter würden außerhalb der Zielzeile liegen.");
        EnsureNoOverlap(created, line.Children, useEffectiveBounds: false);
        History.Execute(new EditSegmentTreeCommand(line, "Wörter einfügen", () =>
        {
            foreach (var word in created)
                if (!line.Children.Contains(word)) line.Children.Add(word);
            SortSegments(line.Children);
            TimelineEditing.SynchronizeLineText(line);
        }));
        return created;
    }

    private IReadOnlyList<LyricSegment> PasteSyllables(LyricsSegmentClipboardPayload payload)
    {
        var word = ResolvePasteWord();
        var line = FindLine(word) ?? throw new InvalidOperationException("Das Zielwort besitzt keine Zeile.");
        var created = LyricsSegmentClipboard.Instantiate(payload, Playhead, word.Id).ToList();
        if (created.Any(syllable => syllable.Start < line.Start || syllable.End > line.End))
            throw new InvalidOperationException("Die eingefügten Silben würden außerhalb der Zielzeile liegen.");
        EnsureNoOverlap(created, word.Children, useEffectiveBounds: false);
        var newWordStart = word.Children.Concat(created).Min(segment => segment.Start);
        var newWordEnd = word.Children.Concat(created).Max(segment => segment.End);
        var otherWords = line.Children.Where(candidate => candidate.Id != word.Id).ToList();
        if (otherWords.Any(candidate => Overlaps(newWordStart, newWordEnd, candidate.Start, candidate.End)))
            throw new InvalidOperationException("Die eingefügten Silben würden das Zielwort mit einem Nachbarwort überlappen lassen.");
        History.Execute(new EditSegmentTreeCommand(line, "Silben einfügen", () =>
        {
            foreach (var syllable in created)
                if (!word.Children.Contains(syllable)) word.Children.Add(syllable);
            SortSegments(word.Children);
            TimelineEditing.FitParentToChildren(word);
            TimelineEditing.SynchronizeWordText(word);
            TimelineEditing.SynchronizeLineText(line);
        }));
        return created;
    }

    private LyricSegment ResolvePasteLine() => SelectedSegment is not null && FindLine(SelectedSegment) is { } selectedLine
        ? selectedLine
        : Document!.Lines.FirstOrDefault(line => line.Start <= Playhead && line.End >= Playhead)
          ?? throw new InvalidOperationException("Bitte eine Zielzeile auswählen oder den Abspielcursor in eine Zeile setzen.");

    private LyricSegment ResolvePasteWord()
    {
        if (SelectedSegment?.Type == LyricSegmentType.Word) return SelectedSegment;
        if (SelectedSegment is { Type: LyricSegmentType.Syllable } syllable && FindParentWord(syllable) is { } parent)
            return parent;
        var line = ResolvePasteLine();
        return line.Children.FirstOrDefault(word => word.Start <= Playhead && word.End >= Playhead)
               ?? throw new InvalidOperationException("Bitte ein Zielwort auswählen oder den Abspielcursor in ein Wort setzen.");
    }

    private static void EnsureNoOverlap(IReadOnlyList<LyricSegment> inserted,
        IEnumerable<LyricSegment> existing, bool useEffectiveBounds)
    {
        var insertedBounds = inserted.Select(segment => useEffectiveBounds
            ? (Start: TimelineEditing.EffectiveStart(segment), End: TimelineEditing.EffectiveEnd(segment))
            : (segment.Start, segment.End)).OrderBy(item => item.Start).ToList();
        for (var index = 1; index < insertedBounds.Count; index++)
            if (Overlaps(insertedBounds[index - 1].Start, insertedBounds[index - 1].End,
                    insertedBounds[index].Start, insertedBounds[index].End))
                throw new InvalidOperationException("Die kopierten Segmente überlappen sich bereits untereinander.");
        foreach (var candidate in existing)
        {
            var candidateStart = useEffectiveBounds ? TimelineEditing.EffectiveStart(candidate) : candidate.Start;
            var candidateEnd = useEffectiveBounds ? TimelineEditing.EffectiveEnd(candidate) : candidate.End;
            if (insertedBounds.Any(item => Overlaps(item.Start, item.End, candidateStart, candidateEnd)))
                throw new InvalidOperationException("Am Abspielcursor ist nicht genügend freier Platz zum Einfügen.");
        }
    }

    private static bool Overlaps(TimeSpan leftStart, TimeSpan leftEnd, TimeSpan rightStart, TimeSpan rightEnd) =>
        leftStart < rightEnd && leftEnd > rightStart;

    private static void SortSegments(List<LyricSegment> segments)
    {
        var ordered = segments.OrderBy(segment => segment.Start).ThenBy(segment => segment.End).ToList();
        segments.Clear();
        segments.AddRange(ordered);
    }

    public void DeleteSelected(IEnumerable<LyricSegment>? selection = null)
    {
        if (Document is null) return;
        var roots = LyricsSegmentClipboard.NormalizeSelection(selection ??
            (SelectedSegment is null ? [] : [SelectedSegment])).ToList();
        if (roots.Count == 0) return;
        if (roots.Any(segment => segment.Type != roots[0].Type))
            throw new InvalidOperationException("Bitte nur Segmente derselben Ebene gemeinsam löschen.");
        var documentSegments = Document.Segments.ToHashSet();
        if (roots.Any(segment => !documentSegments.Contains(segment)))
            throw new InvalidOperationException("Die Auswahl gehört nicht zum aktuellen Song.");

        if (roots[0].Type == LyricSegmentType.Line)
        {
            if (Document.Lines.Count - roots.Count < 1)
                throw new InvalidOperationException("Der letzte Zeilenblock kann nicht gelöscht werden.");
            History.Execute(new EditLineCollectionCommand(Document.Lines, "Zeilenblöcke löschen", () =>
            {
                foreach (var line in roots) Document.Lines.Remove(line);
            }));
            SelectedSegment = Document.Lines.OrderBy(line => line.Start).FirstOrDefault();
        }
        else
        {
            var lines = roots.Select(segment => FindLine(segment) ??
                throw new InvalidOperationException("Ein ausgewähltes Segment besitzt keine Zeile."))
                .Distinct().ToList();
            if (roots[0].Type == LyricSegmentType.Word &&
                roots.GroupBy(FindLine).Any(group => group.Key is null || group.Key.Children.Count <= group.Count()))
                throw new InvalidOperationException("Das letzte Wort einer Zeile kann nicht gelöscht werden.");
            if (roots[0].Type == LyricSegmentType.Syllable &&
                roots.GroupBy(FindParentWord).Any(group => group.Key is null || group.Key.Children.Count <= group.Count()))
                throw new InvalidOperationException("Die letzte Silbe eines Wortes kann nicht gelöscht werden.");
            History.Execute(new EditSegmentForestCommand(lines, "Lyrics-Segmente löschen", () =>
            {
                if (roots[0].Type == LyricSegmentType.Word)
                    foreach (var group in roots.GroupBy(segment => FindLine(segment)!))
                    {
                        foreach (var word in group) group.Key.Children.Remove(word);
                        TimelineEditing.SynchronizeLineText(group.Key);
                        TimelineEditing.MarkAdjusted(group.Key);
                    }
                else
                    foreach (var group in roots.GroupBy(segment => FindParentWord(segment)!))
                    {
                        foreach (var syllable in group) group.Key.Children.Remove(syllable);
                        TimelineEditing.FitParentToChildren(group.Key);
                        TimelineEditing.SynchronizeWordText(group.Key);
                        if (FindLine(group.Key) is { } line) TimelineEditing.SynchronizeLineText(line);
                        TimelineEditing.MarkAdjusted(group.Key);
                    }
            }));
            SelectedSegment = lines[0];
        }
        Status = roots.Count == 1 ? "Lyrics-Segment gelöscht." : $"{roots.Count} Lyrics-Segmente gelöscht.";
        RefreshEditor();
    }

    public void ChangeSelectedText(string text)
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable } segment ||
            segment.Text == text || FindLine(segment) is not { } line || string.IsNullOrWhiteSpace(text)) return;
        History.Execute(new EditSegmentTreeCommand(line, "Segmenttext ändern", () =>
        {
            segment.Text = text.Trim();
            TimelineEditing.MarkAdjusted(segment);
            if (segment.Type == LyricSegmentType.Syllable && FindParentWord(segment) is { } word)
                TimelineEditing.SynchronizeWordText(word);
            if (segment.Type is LyricSegmentType.Word or LyricSegmentType.Syllable)
                TimelineEditing.SynchronizeLineText(line);
        }));
        RefreshEditor();
    }

    public void ChangeLinePresentation(StageLineEffect effect, int voiceLane)
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Line } line) return;
        voiceLane = Math.Clamp(voiceLane, 0, 3);
        if (line.HoldAfterMilliseconds is null && line.StageEffect == effect && line.VoiceLane == voiceLane) return;
        History.Execute(new EditSegmentTreeCommand(line, "Stage-Darstellung ändern", () =>
        {
            // Numeric holds are legacy data now. The freely editable line
            // container is the single presentation window.
            line.HoldAfterMilliseconds = null;
            line.StageEffect = effect;
            line.VoiceLane = voiceLane;
            TimelineEditing.MarkAdjusted(line);
        }));
        RefreshEditor();
        OnPropertyChanged(nameof(SelectedStageEffect));
        OnPropertyChanged(nameof(SelectedVoiceLane));
    }

    public IReadOnlyList<LyricSegment> MoveToOtherVoice(IEnumerable<LyricSegment> selection)
    {
        if (Document is null)
            throw new InvalidOperationException("Bitte zuerst einen Song laden.");
        var normalized = LyricsSegmentClipboard.NormalizeSelection(selection);
        var moved = Array.Empty<LyricSegment>();
        var command = new EditLyricsStructureCommand(Document,
            "Lyrics zur anderen Stimme verschieben", () =>
                moved = TimelineEditing.MoveToOtherVoice(Document, normalized).ToArray());
        try { History.Execute(command); }
        catch
        {
            command.Undo();
            throw;
        }
        SelectedSegment = moved.FirstOrDefault();
        RefreshEditor();
        Status = EditorLocale.German
            ? $"{moved.Length} Segment(e) zur anderen Stimme verschoben."
            : $"Moved {moved.Length} segment(s) to the other voice.";
        return moved;
    }

    public void SetLoopRange(TimeSpan start, TimeSpan end)
    {
        if (end < start) (start, end) = (end, start);
        if (end <= start) return;
        LoopStart = start;
        LoopEnd = end;
        LoopEnabled = true;
        Status = $"Loop {start:mm\\:ss\\.fff} – {end:mm\\:ss\\.fff}";
    }

    public void UpdateTrackedWordLoopRange(TimeSpan start, TimeSpan end)
    {
        if (end <= start) return;
        LoopStart = start;
        LoopEnd = end;
        LoopEnabled = true;
    }

    public void ClearLoopRange()
    {
        LoopEnabled = false;
        LoopStart = null;
        LoopEnd = null;
    }

    public async Task PlayLoopRangeAsync(TimeSpan start, TimeSpan end)
    {
        SetLoopRange(start, end);
        Seek(start);
        await _pendingSeek;
        if (!_audio.IsPlaying) await PlayPauseAsync();
    }

    public void ToggleLoop()
    {
        if (LoopStart is null || LoopEnd is null) { Status = "Loopbereich zuerst mit Shift + Ziehen markieren."; return; }
        LoopEnabled = !LoopEnabled;
    }

    public void NotifyTimelineEdit()
    {
        var beatChanges = CommitBeatPreviewEdits();
        if (Document is not null) Document.ModifiedAt = DateTimeOffset.UtcNow;
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(SelectedSegmentTiming));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        QueueStageLyricsUpdate();
        if (beatChanges > 0)
            Status = Localized(
                $"{beatChanges} Beat-Grenze(n) in den Arbeitsstand übernommen.",
                $"Committed {beatChanges} beat boundary/boundaries to the working version.");
    }

    public void ReportTimelineStatus(string status) => Status = status;

    public async Task<BaseLyricsSourceDto?> LoadBaseLyricsSourceAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song) return null;
        Status = Localized("Base-Lyrics werden geladen …", "Loading base lyrics …");
        using var response = await _http.GetAsync(
            $"/api/admin/songs/{song.Id}/lyrics/base-source", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Status = Localized("Für diesen Song sind keine Base-Lyrics vorhanden.",
                "No base lyrics are available for this song.");
            return null;
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        var source = await response.Content.ReadFromJsonAsync<BaseLyricsSourceDto>(cancellationToken: cancellationToken);
        if (source is null)
            throw new InvalidOperationException(Localized(
                "Der Server hat keine lesbaren Base-Lyrics geliefert.",
                "The server returned no readable base lyrics."));
        return source;
    }

    public async Task SaveBaseLyricsSourceAsync(string lyrics,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song) return;
        using var response = await _http.PutAsJsonAsync(
            $"/api/admin/songs/{song.Id}/lyrics/base-source",
            new UpdateBaseLyricsSourceRequest(lyrics), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        Status = Localized(
            "Base-Lyrics gespeichert. Sie werden bei der nächsten Neuausrichtung verwendet; die aktuelle Timeline blieb unverändert.",
            "Base lyrics saved. They will be used by the next realignment; the current timeline was not changed.");
    }

    public void PauseEditorPlaybackForCatalogPreview()
    {
        if (_audio.IsPlaying)
        {
            _audio.Pause();
            _ = _stageTest.SendTransportAsync(StageTestProtocol.Pause, Playhead);
        }
    }

    public async Task ImportUltraStarLyricsAsync(UltraStarLyricsImport imported,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song)
            throw new InvalidOperationException(EditorLocale.German
                ? "Bitte zuerst einen Song auswählen."
                : "Select a song first.");
        var replacement = imported.ToEditorDocument(song.Id);
        var previous = Document;
        using (var response = await _http.PutAsJsonAsync($"/api/admin/songs/{song.Id}/lyrics/import-source",
                   new ImportLyricsSourceRequest(imported.ToEnhancedLrc()), cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        _audio.Stop();
        _visualClockSuspended = false;
        LoopEnabled = false;
        LoopStart = null;
        LoopEnd = null;
        SelectedSegment = null;
        History.Execute(new ReplaceLyricsDocumentCommand(previous, replacement,
            value => Document = value, EditorLocale.German ? "UltraStar-Lyrics importieren" : "Import UltraStar lyrics"));

        // Ein Import darf weder eine veröffentlichte noch eine vorhandene
        // Entwurfsversion überschreiben. Der nächste Speichervorgang legt stets
        // einen neuen, verwaltbaren Lyrics-Stand an.
        _serverVersionId = null;
        _serverRevision = 0;
        _serverVersionStatus = null;
        SetAlignmentReport(null);
        _loadedSourceFingerprint = null;
        if (replacement.Lines.Count > 0) AnchorPosition(replacement.Lines.Min(line => line.Start));
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        QueueStageLyricsUpdate();
        Status = EditorLocale.German
            ? $"UltraStar-Lyrics als neuer ungespeicherter Arbeitsstand importiert · {replacement.Lines.Count} Zeilen · Rückgängig möglich · bei fehlenden Stems anschließend neu alignen"
            : $"UltraStar lyrics imported as a new unsaved working state · {replacement.Lines.Count} lines · Undo is available · run alignment next if stems are missing";
    }

    public async Task ImportEnhancedLrcLyricsAsync(string source, LyricsDto imported,
        CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song || imported.SongId != song.Id)
            throw new InvalidOperationException(EditorLocale.German
                ? "Bitte zuerst den zugehörigen Song auswählen."
                : "Select the matching song first.");
        var replacement = LyricsDocumentImporter.Import(imported,
            modelVersion: "Enhanced LRC", detailedOrigin: SegmentOrigin.ImportedLineLyrics);
        var previous = Document;
        using (var response = await _http.PutAsJsonAsync($"/api/admin/songs/{song.Id}/lyrics/import-source",
                   new ImportLyricsSourceRequest(source.Trim()), cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        _audio.Stop();
        _visualClockSuspended = false;
        LoopEnabled = false;
        LoopStart = null;
        LoopEnd = null;
        SelectedSegment = null;
        History.Execute(new ReplaceLyricsDocumentCommand(previous, replacement,
            value => Document = value, EditorLocale.German
                ? "Enhanced-LRC importieren" : "Import Enhanced LRC"));
        _serverVersionId = null;
        _serverRevision = 0;
        _serverVersionStatus = null;
        SetAlignmentReport(null);
        _loadedSourceFingerprint = null;
        if (replacement.Lines.Count > 0) AnchorPosition(replacement.Lines.Min(line => line.Start));
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        QueueStageLyricsUpdate();
        Status = EditorLocale.German
            ? $"Enhanced-LRC als neuer ungespeicherter Arbeitsstand importiert · {replacement.Lines.Count} Zeilen · Rückgängig möglich"
            : $"Enhanced LRC imported as a new unsaved working state · {replacement.Lines.Count} lines · Undo is available";
    }

    private LyricSegment? FindLine(LyricSegment segment) => Document?.Lines.FirstOrDefault(line =>
        line.Id == segment.Id || line.DescendantsAndSelf().Any(candidate => candidate.Id == segment.Id));
    private (TimeSpan PreviousEnd, TimeSpan? NextStart) LineNeighborBounds(LyricSegment line)
    {
        var ordered = Document?.Lines.OrderBy(candidate => candidate.Start).ThenBy(candidate => candidate.End).ToList() ?? [];
        var index = ordered.IndexOf(line);
        return (index > 0 ? ordered[index - 1].End : TimeSpan.Zero,
            index >= 0 && index + 1 < ordered.Count ? ordered[index + 1].Start : null);
    }
    private LyricSegment? FindParentWord(LyricSegment segment) => Document?.Lines.SelectMany(line => line.Children)
        .FirstOrDefault(word => word.Id == segment.ParentId);
    private static LyricSegment NewSegment(Guid parentId, LyricSegmentType type, TimeSpan start, TimeSpan end, string text) => new()
    {
        Id = Guid.CreateVersion7(), ParentId = parentId, Type = type, Start = start, End = end, Text = text,
        OriginalStart = start, OriginalEnd = end, OriginalText = text, Origin = SegmentOrigin.ManuallyCreated,
        IsManuallyAdjusted = true, RequiresReview = true
    };
    private static LyricSegment CreateBlankLine(TimeSpan start, TimeSpan duration)
    {
        var line = NewSegment(Guid.Empty, LyricSegmentType.Line, start, start + duration, "Neue Zeile");
        var word = NewSegment(line.Id, LyricSegmentType.Word, line.Start, line.End, "Neue Zeile");
        word.Children.Add(NewSegment(word.Id, LyricSegmentType.Syllable, word.Start, word.End, "Neue Zeile"));
        line.Children.Add(word);
        return line;
    }
    private static LyricSegment CloneSegmentTree(LyricSegment source, Guid? parentId, TimeSpan delta,
        TimeSpan? maximumDuration = null)
    {
        var clone = new LyricSegment
        {
            Id = Guid.CreateVersion7(), ParentId = parentId, Type = source.Type, Text = source.Text,
            Start = source.Start + delta, End = source.End + delta, OriginalStart = source.Start + delta,
            OriginalEnd = source.End + delta, OriginalText = source.Text, Origin = SegmentOrigin.ManuallyCreated,
            Confidence = source.Confidence, IsManuallyAdjusted = true, RequiresReview = true
            , HoldAfterMilliseconds = source.HoldAfterMilliseconds, StageEffect = source.StageEffect
        };
        foreach (var child in source.Children) clone.Children.Add(CloneSegmentTree(child, clone.Id, delta));
        if (maximumDuration is { } duration && clone.End - clone.Start > duration)
            TimelineEditing.ResizeWithDescendants(clone, clone.Start, clone.Start + duration,
                TimeSpan.FromMilliseconds(100));
        return clone;
    }
    private void RefreshEditor()
    {
        Document!.ModifiedAt = DateTimeOffset.UtcNow;
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(SelectedSegmentHeading));
        OnPropertyChanged(nameof(SelectedSegmentTiming));
        OnPropertyChanged(nameof(SelectedSegmentDetails));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        QueueStageLyricsUpdate();
    }

    private StageTestSongState CreateStageSongState(bool? playing = null, TimeSpan? position = null)
    {
        var song = SelectedSong ?? throw new InvalidOperationException("Kein Song ausgewählt.");
        var document = Document ?? throw new InvalidOperationException("Keine Lyrics geladen.");
        var lyrics = StageTestLyricsMapper.ToLyricsDto(document, song, MusicalHighlightEnabled);
        return new StageTestSongState
        {
            songId = song.Id.ToString(),
            title = song.Title,
            artist = song.Artist,
            serverUrl = ServerAddress.ToString().TrimEnd('/'),
            lyricsJson = JsonSerializer.Serialize(lyrics, StageIpcJsonOptions),
            positionSeconds = Math.Max(0, (position ?? Playhead).TotalSeconds),
            playing = playing ?? _audio.IsPlaying,
            durationSeconds = song.DurationSeconds
        };
    }

    private void QueueStageLyricsUpdate()
    {
        if (!_stageTest.IsRunning || SelectedSong is null || Document is null) return;
        _stageLyricsUpdatePending = true;
        if (!_stageLyricsUpdateTimer.IsEnabled) _stageLyricsUpdateTimer.Start();
    }

    private void FlushStageLyricsUpdate()
    {
        if (!_stageTest.IsRunning || SelectedSong is null || Document is null)
        {
            _stageLyricsUpdatePending = false;
            _stageLyricsUpdateTimer.Stop();
            return;
        }
        if (!_stageLyricsUpdatePending)
        {
            _stageLyricsUpdateTimer.Stop();
            return;
        }
        _stageLyricsUpdatePending = false;
        var lyrics = StageTestLyricsMapper.ToLyricsDto(Document, SelectedSong, MusicalHighlightEnabled);
        _stageTest.QueueLyricsUpdate(JsonSerializer.Serialize(lyrics, StageIpcJsonOptions));
    }

    private string DisplayedText(LyricSegment segment) =>
        ShowTechnicalLyrics && !string.IsNullOrWhiteSpace(segment.TechnicalText)
            ? segment.TechnicalText
            : segment.Text;

    private void SaveLocalRecoverySnapshot()
    {
        if (SelectedSong is null || Document is null) return;
        try
        {
            Document.ModifiedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(Document, JsonOptions);
            _draftRecovery.SaveAsync(SelectedSong.Id,
                new RecoveryDraft(DateTimeOffset.UtcNow, json, _serverVersionId, _serverRevision,
                    _loadedSourceFingerprint),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Status = "Lokale automatische Sicherung fehlgeschlagen: " + exception.Message;
        }
    }

    private static bool CanContinueAsWorkingVersion(LyricsVersionStatus status) => status is
        LyricsVersionStatus.Generated or LyricsVersionStatus.NeedsReview or
        LyricsVersionStatus.InReview or LyricsVersionStatus.ReviewOverlaps or LyricsVersionStatus.Reviewed or
        LyricsVersionStatus.Approved;

    private void ApplySongFilter()
    {
        FilteredSongs.Clear();
        foreach (var song in Songs.Where(MatchesSongFilter))
            FilteredSongs.Add(song);
    }

    private static string Localized(string german, string english) => EditorLocale.German ? german : english;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }

    private void SetAlignmentReport(string? json)
    {
        _alignmentReportJson = json;
        PitchEvidence = AlignmentPitchEvidence.Parse(json);
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        _seekCancellation?.Cancel();
        _seekCancellation?.Dispose();
        _songLoadCancellation?.Cancel();
        _songLoadCancellation?.Dispose();
        _positionTimer.Stop();
        _jobTimer.Stop();
        _stageLyricsUpdateTimer.Stop();
        _changeFeedCancellation.Cancel();
        _changeFeedCancellation.Dispose();
        _audio.Dispose();
        Task.Run(async () => await _stageTest.DisposeAsync()).GetAwaiter().GetResult();
        Cover = null;
        _draftSaveLock.Dispose();
        _http.Dispose();
    }
}

public sealed record EditorWishItem(KaraokeEventDto Event, WishDto Wish)
{
    public string Title => Wish.Track.Title;
    public string Details => $"{Wish.Track.SourceLabel} · {Wish.Track.Artist} · {Wish.RequestedBy} · {Wish.Status}";
    public bool IsProcessing =>
        Wish.Status.Contains("werden verarbeitet", StringComparison.OrdinalIgnoreCase) ||
        Wish.Status.Contains("processing", StringComparison.OrdinalIgnoreCase);
    public bool CanAdopt => Wish.HasAudioCandidate && !IsProcessing;
    public bool CanRemove => !IsProcessing;
}

public sealed record EditorLyricsVersionItem(LyricsVersionSummaryDto Version, bool IsCurrent)
{
    public long Revision => Version.Revision;
    public string Timestamp => FormatTimestamp(Version.CreatedAt);
    public string StatusLabel => Version.Status switch
    {
        LyricsVersionStatus.Generated => EditorLocale.German ? "Generiert" : "Generated",
        LyricsVersionStatus.NeedsReview => EditorLocale.German ? "Prüfung nötig" : "Needs review",
        LyricsVersionStatus.InReview => EditorLocale.German ? "In Prüfung" : "In review",
        LyricsVersionStatus.ReviewOverlaps => EditorLocale.German ? "In Prüfung – Überlappungen" : "In review – overlaps",
        LyricsVersionStatus.Reviewed => EditorLocale.German ? "Geprüft" : "Reviewed",
        LyricsVersionStatus.Approved => EditorLocale.German ? "Freigegeben" : "Approved",
        LyricsVersionStatus.Published => EditorLocale.German ? "Veröffentlicht" : "Published",
        LyricsVersionStatus.Rejected => EditorLocale.German ? "Abgelehnt" : "Rejected",
        LyricsVersionStatus.Superseded => EditorLocale.German ? "Archiviert" : "Archived",
        _ => Version.Status.ToString()
    };
    public string CurrentLabel => IsCurrent
        ? (EditorLocale.German ? "AKTUELLER ARBEITSSTAND" : "CURRENT WORKING VERSION")
        : string.Empty;
    public string VariantLabel => Version.AnalysisRunId switch
    {
        { } value when value.Contains(":easyaligner-global:", StringComparison.Ordinal) =>
            EditorLocale.German
                ? "EASYALIGNER DIRECT · GLOBALES CTC"
                : "EASYALIGNER DIRECT · GLOBAL CTC",
        { } value when value.Contains("full-transcription-", StringComparison.Ordinal) =>
            EditorLocale.German ? "VOLLTRANSKRIPT · EASYALIGNER" : "FULL TRANSCRIPT · EASYALIGNER",
        _ => string.Empty
    };
    public bool CanDelete => Version.Status != LyricsVersionStatus.Published;
    public string DeleteHint => CanDelete
        ? (EditorLocale.German ? "Diesen archivierten Stand löschen" : "Delete this archived version")
        : (EditorLocale.German ? "Veröffentlichte Versionen sind geschützt" : "Published versions are protected");

    public static string FormatTimestamp(DateTimeOffset timestamp)
    {
        var local = timestamp.ToLocalTime();
        return EditorLocale.German
            ? local.ToString("dd.MM.yyyy · HH:mm:ss")
            : local.ToString("yyyy-MM-dd · HH:mm:ss");
    }
}

public sealed record EditorJobStatus(bool IsRunning, Guid? EventId, string? EventName, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, int? ExitCode, string Message, IReadOnlyList<string> RecentOutput,
    int Current, int Total, int Percent);
public sealed record EditorImportStatus(bool IsRunning, Guid? JobId, string? Title, string? Artist, int Percent,
    string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode,
    IReadOnlyList<string> RecentOutput);
public sealed record EditorRealignmentStatus(bool IsRunning, Guid? JobId, Guid? SongId, string? SongTitle,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode,
    IReadOnlyList<string> RecentOutput, IReadOnlyList<Guid>? SongIds = null);
public sealed record EditorLyricsRecognitionStatus(bool IsRunning, Guid? JobId, Guid? SongId,
    string? SongTitle, int Percent, string Message, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, int? ExitCode, IReadOnlyList<string> RecentOutput);
