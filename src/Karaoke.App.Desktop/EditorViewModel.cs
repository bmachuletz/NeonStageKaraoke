using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Karaoke.App.Services;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public sealed class EditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly HttpClient _http;
    private readonly IAudioPlaybackService _audio;
    private readonly bool _loadVisualAssets;
    private SongDto? _selectedSong;
    private LyricsEditorDocument? _document;
    private TimeSpan _playhead;
    private string _status = "Verbinde mit Neon Stage …";
    private bool _busy;
    private WaveformPyramid? _waveform;
    private Bitmap? _cover;
    private readonly FfmpegWaveformService _waveforms = new();
    private readonly EditorAudioCache _audioCache;
    private readonly EditorDraftRecovery _draftRecovery = new();
    private Guid? _serverVersionId;
    private long _serverRevision;
    private LyricsVersionStatus? _serverVersionStatus;
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
    private long _previewRevision;
    private long _timelineRevision;
    private TimeSpan? _loopStart;
    private TimeSpan? _loopEnd;
    private bool _loopEnabled;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _jobTimer;
    private bool _consoleVisible;
    private string _consoleMode = "wishlist";
    private bool _versionsVisible;
    private KaraokeEventDto? _selectedWishEvent;
    private string _jobState = "Hintergrundverarbeitung bereit";
    private int _wishProgress;
    private string _wishProgressLabel = "Kein Auftrag aktiv";
    private Guid? _handledImportJob;
    private Guid? _handledRealignmentJob;
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
    private string? _loadedSourceFingerprint;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public EditorViewModel(IAudioPlaybackService audio, bool loadVisualAssets = true)
    {
        _audio = audio;
        _loadVisualAssets = loadVisualAssets;
        var configured = (Environment.GetEnvironmentVariable("NEONSTAGE_SERVER_URL")
                          ?? Environment.GetEnvironmentVariable("KARAOKE_SERVER"))?.Trim();
        ServerAddress = new Uri(string.IsNullOrWhiteSpace(configured) ? "http://192.168.178.91:5274" : configured);
        _http = new HttpClient { BaseAddress = ServerAddress, Timeout = TimeSpan.FromMinutes(5) };
        _audioCache = new EditorAudioCache(_http);
        _audio.PositionChanged += (_, position) => Dispatcher.UIThread.Post(() => ObserveDecoderPosition(position));
        _audio.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => Status = state);
        _audio.PlaybackFailed += (_, error) => Dispatcher.UIThread.Post(() => Status = error);
        _audio.Volume = _vocalVolume;
        _audio.VocalVolume = _vocalVolume;
        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) =>
            {
                if (!_audio.IsPlaying || _visualClockSuspended) return;
                var interpolated = _positionAnchor + Stopwatch.GetElapsedTime(_positionAnchorTimestamp);
                if (LoopEnabled && LoopStart is { } loopStart && LoopEnd is { } loopEnd && interpolated >= loopEnd)
                {
                    if (Interlocked.Exchange(ref _loopSeekInProgress, 1) == 0)
                        _ = SeekLoopAsync(loopStart, loopEnd, interpolated);
                    return;
                }
                Playhead = interpolated < Playhead ? Playhead : interpolated;
            });
        _positionTimer.Start();
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
    public IReadOnlyList<string> SongStatusFilters { get; } = ["Alle", "In Review", "Freigegeben"];
    public Uri ServerAddress { get; }
    public CommandHistory History { get; } = new();
    public SongDto? SelectedSong { get => _selectedSong; set => Set(ref _selectedSong, value); }
    public LyricsEditorDocument? Document { get => _document; private set => Set(ref _document, value); }
    public WaveformPyramid? Waveform { get => _waveform; private set => Set(ref _waveform, value); }
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
            else _audio.Volume = value;
        }
    }
    public long PreviewRevision { get => _previewRevision; private set => Set(ref _previewRevision, value); }
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
            OnPropertyChanged(nameof(SelectedHoldAfterText));
            OnPropertyChanged(nameof(SelectedStageEffect));
        }
    }
    public string SelectedSegmentHeading => SelectedSegment is null
        ? "Segment auswählen"
        : $"{SelectedSegment.Type}: {SelectedSegment.Text}";
    public string SelectedSegmentTiming => SelectedSegment is null ? "" :
        $"{SelectedSegment.Start:mm\\:ss\\.fff}  →  {SelectedSegment.End:mm\\:ss\\.fff}  ·  {(SelectedSegment.End - SelectedSegment.Start).TotalMilliseconds:0} ms";
    public string SelectedSegmentDetails => SelectedSegment is null ?
        "Klicke ein Segment oder ziehe eine gemeinsame Silbengrenze in der Timeline."
        : $"Quelle: {SelectedSegment.Origin} · Konfidenz: {(SelectedSegment.Confidence is null ? "–" : SelectedSegment.Confidence.Value.ToString("P0"))}";
    public string SelectedSegmentText => SelectedSegment?.Text ?? string.Empty;
    public bool CanEditWord => SelectedSegment?.Type == LyricSegmentType.Word;
    public bool CanEditSyllable => SelectedSegment?.Type == LyricSegmentType.Syllable;
    public bool CanEditLine => SelectedSegment?.Type == LyricSegmentType.Line;
    public bool CanDeleteSegment => CanEditLine || CanEditWord || CanEditSyllable;
    public string SelectedHoldAfterText => SelectedSegment?.Type == LyricSegmentType.Line && SelectedSegment.HoldAfterMilliseconds is { } value
        ? (value / 1000d).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
    public Array StageEffects { get; } = Enum.GetValues<StageLineEffect>();
    public StageLineEffect SelectedStageEffect => SelectedSegment?.Type == LyricSegmentType.Line
        ? SelectedSegment.StageEffect : StageLineEffect.Automatic;
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
            foreach (var song in songs.Where(song => song.HasLyrics).OrderBy(song => song.Artist).ThenBy(song => song.Title))
                Songs.Add(song);
            ApplySongFilter();
            Status = $"{Songs.Count} Songs mit Lyrics geladen";
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

            _audio.Stop();
            _loadedAudioSongId = null;
            History.Clear();
            SelectedSegment = null;
            Document = document;
            // Historische Stände bleiben unveränderlich. Der geladene Inhalt ist
            // ein neuer Arbeitsstand und erhält erst beim Speichern eine neue ID.
            _serverVersionId = null;
            _serverRevision = 0;
            _serverVersionStatus = null;
            AnchorPosition(FirstVocalPosition());
            PreviewRevision++;
            TimelineRevision++;
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(ReviewSummary));
            SaveLocalRecoverySnapshot();
            await RefreshLyricsVersionsAsync(cancellationToken, reportErrors: false);
            Status = $"Revision {version.Revision} vom {EditorLyricsVersionItem.FormatTimestamp(version.CreatedAt)} als neuer Arbeitsstand geladen · noch nicht gespeichert";
        }
        catch (Exception exception)
        {
            Status = "Lyrics-Version konnte nicht geladen werden: " + exception.Message;
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
                if (Wishes.Any(existing => existing.Wish.Id == wish.Wish.Id)) continue;
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
                $"{results.Count} Treffer · Titel mit Lyrics können direkt importiert werden.",
                $"{results.Count} results · tracks with lyrics can be imported directly.");
        }
        catch (Exception exception) { AdminWishSearchStatus = Localized("Suche fehlgeschlagen: ", "Search failed: ") + exception.Message; }
        finally { AdminWishSearching = false; }
    }

    public async Task ImportAdminWishAsync(SpotifyTrackDto track, CancellationToken cancellationToken = default)
    {
        if (!track.HasSyncedLyrics) { AdminWishSearchStatus = Localized("Für diesen Treffer wurden keine geeigneten Lyrics gefunden.", "No suitable lyrics were found for this result."); return; }
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
            FolderImportSummary = "Bitte zuerst einen MP3-Ordner auswählen.";
            return;
        }
        ShowFolderImportConsole();
        AppendConsole($"> import-mp3-folder \"{path}\"" + (FolderImportRecursive ? " --recursive" : string.Empty));
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/admin/folder-import",
                new FolderImportRequest(path, FolderImportRecursive), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                AppendConsole("Ein MP3-Ordnerimport läuft bereits; dessen Status wird angezeigt.");
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

    public async Task StartSelectedSongRealignmentAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSong is not { } song)
        {
            Status = "Bitte zuerst einen Song auswählen.";
            return;
        }
        ConsoleVisible = true;
        _audio.Stop();
        AppendConsole($"> GPU-Neuausrichtung: {song.Title} · {song.Artist}");
        try
        {
            using var response = await _http.PostAsync($"/api/admin/songs/{song.Id}/realign", null, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                AppendConsole("Es läuft bereits eine GPU-Neuausrichtung. Es wird kein zweiter Song gestartet.");
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledRealignmentJob = null;
            Status = $"GPU-Alignment für {song.Title} läuft im Hintergrund …";
            await RefreshRealignmentStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = "Neuausrichtung konnte nicht gestartet werden: " + exception.Message;
            AppendConsole(Status);
        }
    }

    public async Task StartAllSongsRealignmentAsync(CancellationToken cancellationToken = default)
    {
        ConsoleVisible = true;
        _audio.Stop();
        AppendConsole("> GPU-Neuausrichtung: gesamte Bibliothek");
        try
        {
            using var response = await _http.PostAsync("/api/admin/songs/realign-all", null, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                AppendConsole("Es läuft bereits eine GPU-Neuausrichtung.");
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            _handledRealignmentJob = null;
            Status = "GPU-Alignment der gesamten Bibliothek läuft im Hintergrund …";
            await RefreshRealignmentStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Status = "Bibliotheks-Alignment konnte nicht gestartet werden: " + exception.Message;
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
        await RefreshFolderImportStatusAsync(cancellationToken);
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
            if (status.IsRunning) JobState = $"MP3-ORDNERIMPORT · {status.Percent}% · {status.Message}";
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
                JobState = $"SONG ALIGNMENT · {status.Percent}% · {status.Message}";
                Status = status.Message;
            }
            foreach (var line in status.RecentOutput)
                if (_seenConsoleOutput.Add("realign:" + status.JobId + ":" + line)) AppendConsole(line);
            if (status.IsRunning || _handledRealignmentJob == status.JobId) return;
            _handledRealignmentJob = status.JobId;
            if (status.ExitCode != 0)
            {
                Status = "GPU-Neuausrichtung fehlgeschlagen. Details stehen in der Konsole.";
                return;
            }
            await ReloadSongsAsync(cancellationToken);
            if (status.SongId is { } songId)
            {
                if (SelectedSong?.Id == songId)
                    await LoadRealignmentForReviewAsync(songId, cancellationToken);
                else
                    _realignedSongsPendingReview.Add(songId);
            }
            else
            {
                await ReloadSongsAsync(cancellationToken);
                Status = "GPU-Neuausrichtung der Bibliothek abgeschlossen. Neue Ergebnisse stehen im Review bereit.";
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
        _loadedSourceFingerprint = JsonSerializer.Serialize(lyrics, JsonOptions);
        History.Clear();
        _serverVersionId = serverVersion?.Id;
        _serverRevision = serverVersion?.Revision ?? 0;
        _serverVersionStatus = serverVersion?.Status;
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
        foreach (var song in songs.Where(song => song.HasLyrics).OrderBy(song => song.Artist).ThenBy(song => song.Title)) Songs.Add(song);
        ApplySongFilter();
        SelectedSong = Songs.FirstOrDefault(song => song.Id == selectedId);
    }

    private async Task MergeNewSongsAsync(CancellationToken cancellationToken)
    {
        var songs = await _http.GetFromJsonAsync<IReadOnlyList<SongDto>>("/api/songs?take=500&includeUnreleased=true", cancellationToken) ?? [];
        foreach (var song in songs.Where(song => song.HasLyrics && Songs.All(existing => existing.Id != song.Id)))
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
        var matchesStatus = SongStatusFilter switch
        {
            "In Review" => song.ReviewStatus == SongReviewStatus.InReview,
            "Freigegeben" => song.ReviewStatus == SongReviewStatus.Approved,
            _ => true
        };
        return matchesStatus && (needle.Length == 0 || song.Title.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
               song.Artist.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
               song.Album.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
    }

    public async Task ApproveSelectedSongAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSong is null) return;
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
        _loadedSourceFingerprint = null;
        History.Clear();
        Document = null;
        Waveform = null;
        Cover = null;
        LyricsVersions.Clear();
        OnPropertyChanged(nameof(LyricsVersionsHeading));
        Playhead = TimeSpan.Zero;
        SelectedSegment = null;
        Busy = true;
        OnPropertyChanged(nameof(SongHeading));
        try
        {
            if (_loadVisualAssets) await LoadCoverAsync(song.Id, ct);
            Status = "KI-Alignment wird geladen …";
            var stems = await _http.GetFromJsonAsync<StemAvailabilityDto>($"/api/songs/{song.Id}/stems", ct);
            _hasVocalStem = stems?.HasVocals == true;
            HasInstrumentalStem = stems?.HasInstrumental == true;
            if (!HasInstrumentalStem) InstrumentalEnabled = false;
            var currentSource = await _http.GetFromJsonAsync<LyricsDto>(
                $"/api/admin/songs/{song.Id}/lyrics/source", ct);
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
                // Fehlt die Version in der aktuell verbundenen Serverdatenbank,
                // muss der Recovery-Entwurf dort neu angelegt statt gegen eine
                // alte ID aktualisiert werden.
                _serverVersionId = serverDraft?.Id;
                _serverRevision = serverDraft?.Revision ?? 0;
                _serverVersionStatus = serverDraft?.Status;
                _loadedSourceFingerprint = currentSourceFingerprint;
                Status = serverDraft is null
                    ? "Lokaler Recovery-Entwurf geladen – bitte erneut speichern."
                    : "Neueren lokalen Recovery-Entwurf geladen – bitte erneut speichern.";
            }
            else if (serverDraft is not null)
            {
                Document = JsonSerializer.Deserialize<LyricsEditorDocument>(serverDraft.DocumentJson, JsonOptions);
                _serverVersionId = serverDraft.Id;
                _serverRevision = serverDraft.Revision;
                _serverVersionStatus = serverDraft.Status;
                _loadedSourceFingerprint = currentSourceFingerprint;
            }
            else
            {
                var lyrics = loadFreshAlignment
                    ? currentSource
                    : await _http.GetFromJsonAsync<LyricsDto>($"/api/songs/{song.Id}/lyrics", ct);
                Document = lyrics is null ? null : LyricsDocumentImporter.Import(lyrics);
                _loadedSourceFingerprint = currentSourceFingerprint;
                _serverVersionId = null;
                _serverRevision = 0;
                _serverVersionStatus = null;
            }
            History.Clear();
            if (Document is { Lines.Count: > 0 })
                AnchorPosition(Document.Lines.SelectMany(line => line.Children.Count > 0 ? line.Children : [line])
                    .Select(segment => segment.Start).DefaultIfEmpty(TimeSpan.Zero).Min());
            OnPropertyChanged(nameof(ReviewSummary));
            Status = Document is null ? "Keine Lyrics verfügbar" : loadFreshAlignment
                ? "Neues GPU-Alignment als Review-Stand geladen"
                : "Alignment bereit zur Prüfung";
            await RefreshLyricsVersionsAsync(ct, reportErrors: false);
            if (Document is not null)
            {
                Status = "Vocal-Waveform wird vorbereitet …";
                var vocals = new Uri(ServerAddress, $"/api/songs/{song.Id}/stems/vocals?format=flac");
                var instrumental = new Uri(ServerAddress, $"/api/songs/{song.Id}/stems/instrumental?format=flac");
                var original = new Uri(ServerAddress, $"/api/songs/{song.Id}/audio");
                try
                {
                    if (!_hasVocalStem) throw new InvalidOperationException("Keine Vocalspur vorhanden.");
                    Status = "Vocalspur wird lokal für sample-stabiles Editing vorbereitet …";
                    _localVocalUri = await _audioCache.GetAsync(song.Id, "vocals", vocals, ct);
                    if (HasInstrumentalStem)
                        _localInstrumentalUri = await _audioCache.GetAsync(song.Id, "instrumental", instrumental, ct);
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
                Status = "Alignment und Vocal-Waveform bereit zur Prüfung";
            }
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
                        AllowTimingConflicts: allowTimingConflicts), cancellationToken);
            else
                response = await _http.PutAsJsonAsync($"/api/songs/{songId}/lyrics/versions/{_serverVersionId}",
                    new UpdateLyricsVersionRequest(_serverRevision, json,
                        AllowTimingConflicts: allowTimingConflicts), cancellationToken);
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
            if (_audio.IsPlaying) { _audio.Pause(); return; }
            if (_loadedAudioSongId == SelectedSong.Id) { _audio.Resume(); return; }
            var requestedPosition = Playhead;
            _visualClockSuspended = true;
            var vocals = _hasVocalStem ? _localVocalUri : _selectedPlaybackUri;
            if (InstrumentalEnabled && HasInstrumentalStem)
            {
                var instrumental = _localInstrumentalUri ?? throw new InvalidOperationException("Instrumentalspur ist noch nicht lokal vorbereitet.");
                var vocal = vocals ?? throw new InvalidOperationException("Vocalspur ist noch nicht lokal vorbereitet.");
                Status = "Einspuriger Preview-Mix wird vorbereitet …";
                var mix = await _audioCache.GetMixAsync(SelectedSong.Id, instrumental, vocal,
                    InstrumentalVolume, VocalVolume, _songLoadCancellation?.Token ?? CancellationToken.None);
                _audio.Volume = 100;
                await _audio.PlayAsync(mix, startPosition: requestedPosition);
            }
            else
            {
                var source = vocals ?? new Uri(ServerAddress, $"/api/songs/{SelectedSong.Id}/audio");
                await _audio.PlayAsync(source, startPosition: requestedPosition);
            }
            _loadedAudioSongId = SelectedSong.Id;
            AnchorPosition(_audio.Position);
        }
        catch (Exception exception) { Status = "Playback konnte nicht gestartet werden: " + exception.Message; }
        finally { _visualClockSuspended = false; _playbackCommandLock.Release(); }
    }

    public void Seek(TimeSpan position)
    {
        Playhead = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        AnchorPosition(Playhead);
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
    }

    public void SelectSegment(LyricSegment? segment) => SelectedSegment = segment;

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

    public void DeleteSelected()
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable } segment ||
            FindLine(segment) is not { } line) return;
        if (segment.Type == LyricSegmentType.Line)
        {
            if (Document!.Lines.Count <= 1) { Status = "Der letzte Zeilenblock kann nicht gelöscht werden."; return; }
            var next = Document.Lines.ElementAtOrDefault(Document.Lines.IndexOf(line) + 1) ??
                       Document.Lines.ElementAtOrDefault(Document.Lines.IndexOf(line) - 1);
            History.Execute(new EditLineCollectionCommand(Document.Lines, "Zeilenblock löschen", () => Document.Lines.Remove(line)));
            SelectedSegment = next;
            RefreshEditor();
            return;
        }
        var parent = segment.Type == LyricSegmentType.Word ? line : FindParentWord(segment);
        if (parent is null || parent.Children.Count <= 1) { Status = "Das letzte Segment kann nicht gelöscht werden."; return; }
        History.Execute(new EditSegmentTreeCommand(line, "Segment löschen", () =>
        {
            parent.Children.Remove(segment);
            if (parent.Type == LyricSegmentType.Word)
            {
                TimelineEditing.FitParentToChildren(parent);
                TimelineEditing.SynchronizeWordText(parent);
            }
            TimelineEditing.SynchronizeLineText(line);
            TimelineEditing.MarkAdjusted(parent);
        }));
        SelectedSegment = parent.Type == LyricSegmentType.Word ? parent : line;
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

    public void ChangeLinePresentation(string holdText, StageLineEffect effect)
    {
        if (SelectedSegment is not { Type: LyricSegmentType.Line } line) return;
        int? hold = null;
        if (!string.IsNullOrWhiteSpace(holdText))
        {
            if (!double.TryParse(holdText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out var seconds) || seconds < 0 || seconds > 10)
            {
                Status = "Haltezeit bitte zwischen 0 und 10 Sekunden eingeben.";
                return;
            }
            hold = (int)Math.Round(seconds * 1000);
        }
        if (line.HoldAfterMilliseconds == hold && line.StageEffect == effect) return;
        History.Execute(new EditSegmentTreeCommand(line, "Stage-Darstellung ändern", () =>
        {
            line.HoldAfterMilliseconds = hold;
            line.StageEffect = effect;
            TimelineEditing.MarkAdjusted(line);
        }));
        RefreshEditor();
        OnPropertyChanged(nameof(SelectedHoldAfterText));
        OnPropertyChanged(nameof(SelectedStageEffect));
    }

    public void SetLoopRange(TimeSpan start, TimeSpan end)
    {
        if (end < start) (start, end) = (end, start);
        if (end - start < TimeSpan.FromMilliseconds(100)) return;
        LoopStart = start;
        LoopEnd = end;
        LoopEnabled = true;
        Status = $"Loop {start:mm\\:ss\\.fff} – {end:mm\\:ss\\.fff}";
    }

    public void ToggleLoop()
    {
        if (LoopStart is null || LoopEnd is null) { Status = "Loopbereich zuerst mit Shift + Ziehen markieren."; return; }
        LoopEnabled = !LoopEnabled;
    }

    public void NotifyTimelineEdit()
    {
        if (Document is not null) Document.ModifiedAt = DateTimeOffset.UtcNow;
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(SelectedSegmentTiming));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
    }

    public void ReportTimelineStatus(string status) => Status = status;

    public void ImportUltraStarLyrics(UltraStarLyricsImport imported)
    {
        if (SelectedSong is not { } song)
            throw new InvalidOperationException(EditorLocale.German
                ? "Bitte zuerst einen Song auswählen."
                : "Select a song first.");
        var replacement = imported.ToEditorDocument(song.Id);
        var previous = Document;
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
        _loadedSourceFingerprint = null;
        if (replacement.Lines.Count > 0) AnchorPosition(replacement.Lines.Min(line => line.Start));
        PreviewRevision++;
        TimelineRevision++;
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(ReviewSummary));
        SaveLocalRecoverySnapshot();
        Status = EditorLocale.German
            ? $"UltraStar-Lyrics als neuer ungespeicherter Arbeitsstand importiert · {replacement.Lines.Count} Zeilen · Rückgängig möglich"
            : $"UltraStar lyrics imported as a new unsaved working state · {replacement.Lines.Count} lines · Undo is available";
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
    }

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
        _changeFeedCancellation.Cancel();
        _changeFeedCancellation.Dispose();
        _audio.Dispose();
        Cover = null;
        _draftSaveLock.Dispose();
        _http.Dispose();
    }
}

public sealed record EditorWishItem(KaraokeEventDto Event, WishDto Wish)
{
    public string Title => Wish.Track.Title;
    public string Details => $"{Wish.Track.SourceLabel} · {Wish.Track.Artist} · {Wish.RequestedBy} · {Wish.Status}";
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
    IReadOnlyList<string> RecentOutput);
