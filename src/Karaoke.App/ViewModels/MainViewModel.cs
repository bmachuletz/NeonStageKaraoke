using System.Collections.ObjectModel;
using System.Net.Http.Json;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karaoke.Contracts;
using Karaoke.App.Services;
using Microsoft.AspNetCore.SignalR.Client;
using QRCoder;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Karaoke.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HttpClient _http;
    private readonly HubConnection _hubConnection;
    private readonly IAudioPlaybackService _audioPlayback = AudioPlaybackServiceFactory.Create();
    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private readonly SemaphoreSlim _completionLock = new(1, 1);
    private readonly Guid _controllerId;
    private readonly CancellationTokenSource _lifetime = new();
    private AppPreferences _preferences;
    private IReadOnlyList<LyricsLineDto> _lyrics = [];
    private Guid? _playingSongId;
    private Guid? _nextSongId;
    private Guid? _currentQueueEntryId;
    private TimeSpan? _pendingSeek;
    private bool _isRestoringPosition;
    private long _lastPositionReportTicks;
    private long _lastPlaybackRevision;
    private Guid? _completedQueueEntryId;
    private long _ignoreEndEventsUntilTicks;
    private bool _disposed;
    private bool _controlsAudioPlayback;
    private bool _lastServerPaused;
    private readonly Dictionary<Guid, Task<Bitmap?>> _coverCache = [];
    private readonly Uri _guestBaseAddress;

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string guestName = "Gast";
    [ObservableProperty] private string status = "Backend wird verbunden …";
    [ObservableProperty] private SongItemViewModel? selectedSong;
    [ObservableProperty] private bool stageMode;
    [ObservableProperty] private string currentTitle = "Noch kein Titel";
    [ObservableProperty] private string currentSinger = string.Empty;
    [ObservableProperty] private string lyricMinusTwo = string.Empty;
    [ObservableProperty] private string lyricMinusOne = string.Empty;
    [ObservableProperty] private string currentLyric = "GLEICH GEHT'S LOS";
    [ObservableProperty] private string currentLyricSung = string.Empty;
    [ObservableProperty] private string currentLyricRemaining = "GLEICH GEHT'S LOS";
    [ObservableProperty] private string lyricCue = string.Empty;
    [ObservableProperty] private string lyricPlusOne = string.Empty;
    [ObservableProperty] private string lyricPlusTwo = string.Empty;
    [ObservableProperty] private double lyricProgress;
    [ObservableProperty] private IReadOnlyList<LyricsWordDto> currentLyricWords = [];
    [ObservableProperty] private TimeSpan lyricsPosition;
    [ObservableProperty] private bool lyricsRunning;
    [ObservableProperty] private IReadOnlyList<VisualizationFrameDto> visualizationFrames = [];
    [ObservableProperty] private int volume = 85;
    [ObservableProperty] private int vocalVolume;
    [ObservableProperty] private Bitmap? currentCover;
    [ObservableProperty] private string audioStatus = "Wiedergabe bereit";
    [ObservableProperty] private bool qrCodeVisible;
    [ObservableProperty] private Bitmap? qrCode;
    [ObservableProperty] private string webAddress = string.Empty;
    [ObservableProperty] private string activeEventName = "Neon Stage";
    [ObservableProperty] private bool hasNextSong;
    [ObservableProperty] private string nextTitle = "Noch offen";
    [ObservableProperty] private string nextSinger = "Wünsch dir einen Song";
    [ObservableProperty] private Bitmap? nextCover;
    [ObservableProperty] private bool settingsVisible;
    [ObservableProperty] private string settingsServerAddress = string.Empty;
    [ObservableProperty] private string settingsLibraryPath = string.Empty;
    [ObservableProperty] private string settingsMessage = string.Empty;
    [ObservableProperty] private string newEventName = string.Empty;
    [ObservableProperty] private string newEventStartsAt = DateTimeOffset.Now.AddDays(7).ToString("yyyy-MM-dd HH:mm");
    [ObservableProperty] private KaraokeEventDto? selectedEvent;
    [ObservableProperty] private StageThemeDto? selectedNewEventStageTheme;

    public ObservableCollection<SongItemViewModel> Songs { get; } = [];
    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];
    public ObservableCollection<KaraokeEventDto> Events { get; } = [];
    public ObservableCollection<StageThemeDto> StageThemes { get; } = [];
    public string[] PreviewLines { get; } = ["GLEICH GEHT'S LOS", "Such dir deinen Song aus", "Die Bühne wartet auf dich", "", ""];

    public MainViewModel()
    {
        _preferences = AppPreferences.Load();
        if (_preferences.ControllerId == Guid.Empty)
        {
            _preferences = _preferences with { ControllerId = Guid.NewGuid() };
            try { _preferences.Save(); } catch { }
        }
        _controllerId = _preferences.ControllerId;
        var configuredServer = Environment.GetEnvironmentVariable("KARAOKE_SERVER") ?? _preferences.ServerAddress;
        var serverAddress = Uri.TryCreate(configuredServer, UriKind.Absolute, out var parsedServer)
            ? parsedServer : new Uri(AppPreferences.DefaultServerAddress);
        if (serverAddress.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            serverAddress = new UriBuilder(serverAddress) { Host = "127.0.0.1" }.Uri;
        Volume = Math.Clamp(_preferences.Volume, 0, 100);
        VocalVolume = Math.Clamp(_preferences.VocalVolume, 0, 100);
        SettingsServerAddress = serverAddress.ToString().TrimEnd('/');
        _http = new() { BaseAddress = serverAddress, Timeout = TimeSpan.FromSeconds(5) };
        _guestBaseAddress = ResolveGuestAddress(serverAddress);
        WebAddress = _guestBaseAddress.ToString().TrimEnd('/');
        QrCode = CreateQrCode(WebAddress);
        _http.DefaultRequestHeaders.Add("X-Karaoke-Controller", _controllerId.ToString());
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(serverAddress, "/hubs/karaoke"))
            .WithAutomaticReconnect()
            .Build();
        _audioPlayback.Volume = Volume;
        _audioPlayback.VocalVolume = VocalVolume;
        _audioPlayback.PositionChanged += (_, position) =>
        {
            Dispatcher.UIThread.Post(() => UpdateLyrics(position));
            ReportPosition(position);
        };
        _audioPlayback.PlaybackStarted += (_, _) =>
        {
            var seek = _pendingSeek;
            _pendingSeek = null;
            if (seek is { } position && position > TimeSpan.FromMilliseconds(500))
                _ = RestorePositionAsync(position, _playingSongId);
            else
                _isRestoringPosition = false;
        };
        _audioPlayback.PlaybackEnded += (_, _) =>
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _ignoreEndEventsUntilTicks)) return;
            if (_controlsAudioPlayback && _currentQueueEntryId is { } entryId)
                _ = CompleteCurrentEntryAsync(entryId, false);
        };
        _audioPlayback.PlaybackFailed += (_, message) =>
        {
            Dispatcher.UIThread.Post(() => AudioStatus = "Audiofehler: " + message);
            if (_controlsAudioPlayback && _currentQueueEntryId is { } entryId)
                _ = CompleteCurrentEntryAsync(entryId, true);
        };
        _audioPlayback.StateChanged += (_, message) =>
            Dispatcher.UIThread.Post(() => AudioStatus = message);

        _hubConnection.On<PlaybackStateDto>("QueueChanged", state =>
            Dispatcher.UIThread.InvokeAsync(() => ApplyQueueState(state)));
        _hubConnection.On<PlaybackPositionDto>("PlaybackPositionChanged", update =>
        {
            if (update.Revision <= Interlocked.Read(ref _lastPlaybackRevision)) return;
            Interlocked.Exchange(ref _lastPlaybackRevision, update.Revision);
            if (!_controlsAudioPlayback && update.QueueEntryId == _currentQueueEntryId)
                Dispatcher.UIThread.Post(() => UpdateLyrics(update.Position));
        });
        _hubConnection.On<LibraryScanStatusDto>("ScanStatusChanged", scan =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (scan.IsRunning)
                    Status = $"Bibliothek wird geprüft: {scan.CheckedFiles} – {scan.CurrentFile}";
            }));
        _hubConnection.Reconnecting += async _ =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = "Live-Verbindung wird wiederhergestellt …");
        };
        _hubConnection.Reconnected += async _ =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = "Live-Verbindung wiederhergestellt");
            await RefreshAsync();
        };
        _hubConnection.Closed += async _ =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = "Live-Verbindung getrennt");
        };

        _ = InitializeAsync();
        _ = MaintainControllerLeaseAsync(_lifetime.Token);
    }

    private async Task InitializeAsync()
    {
        await LoadActiveEventAsync();
        await RefreshAsync();
        try
        {
            await _hubConnection.StartAsync();
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"{Songs.Count} Titel geladen · Live verbunden");
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = "Live-Verbindung fehlgeschlagen: " + exception.Message);
        }
    }

    private async Task LoadActiveEventAsync()
    {
        try
        {
            var item = await _http.GetFromJsonAsync<KaraokeEventDto>("/api/events/active");
            if (item is null) return;
            ActiveEventName = item.Name;
            WebAddress = new Uri(_guestBaseAddress, "/e/" + Uri.EscapeDataString(item.InviteToken)).ToString().TrimEnd('/');
            QrCode = CreateQrCode(WebAddress);
        }
        catch { }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var result = await _http.GetFromJsonAsync<PagedResultDto<SongDto>>(
                "/api/library/search?q=" + Uri.EscapeDataString(SearchText) + "&page=1&pageSize=50");
            var state = await _http.GetFromJsonAsync<PlaybackStateDto>("/api/queue");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Songs.Clear();
                foreach (var song in result?.Items ?? [])
                {
                    var item = new SongItemViewModel(song);
                    Songs.Add(item);
                    _ = PopulateCoverAsync(item);
                }
                if (state is not null) ApplyQueueState(state);
                Status = $"{result?.TotalCount ?? 0} Titel gefunden";
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = "Backend nicht erreichbar: " + exception.Message);
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (SelectedSong is null) return;
        using var response = await _http.PostAsJsonAsync("/api/queue", new AddQueueRequest(SelectedSong.Song.Id, GuestName));
        if (response.IsSuccessStatusCode) await RefreshAsync();
        else Status = "Der Titel konnte nicht zur Warteliste hinzugefügt werden.";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var startsNewSession = !_controlsAudioPlayback;
        AudioStatus = startsNewSession ? "Wiedergabe wird gestartet …" : "Wiedergabe wird fortgesetzt …";
        if (!await ClaimControllerAsync()) return;
        _controlsAudioPlayback = true;
        if (!startsNewSession) _audioPlayback.Resume();
        if (startsNewSession)
        {
            _playingSongId = null;
            _pendingSeek = null;
            _isRestoringPosition = false;
        }
        var state = await SendPlaybackCommandAsync(startsNewSession
            ? "/api/playback/start-fresh"
            : "/api/playback/start");
        if (state?.Current is null)
        {
            _controlsAudioPlayback = false;
            await ReleaseControllerAsync();
            Status = "Die Warteliste ist leer – bitte zuerst einen Titel hinzufügen.";
            return;
        }
        ApplyQueueState(state);
        StageMode = true;
    }

    [RelayCommand]
    private Task PauseAsync()
    {
        AudioStatus = "Wiedergabe wird pausiert …";
        if (_controlsAudioPlayback) _audioPlayback.Pause();
        return ApplyPlaybackCommandAsync("/api/playback/pause");
    }
    [RelayCommand]
    private Task ResumeAsync()
    {
        AudioStatus = "Wiedergabe wird fortgesetzt …";
        if (_controlsAudioPlayback) _audioPlayback.Resume();
        return ApplyPlaybackCommandAsync("/api/playback/resume");
    }
    [RelayCommand] private Task PreviousAsync() => ApplyPlaybackCommandAsync("/api/playback/previous");
    [RelayCommand] private Task NextAsync() => ApplyPlaybackCommandAsync("/api/playback/next");
    [RelayCommand]
    private async Task StopAsync()
    {
        await ApplyPlaybackCommandAsync("/api/playback/stop");
        _controlsAudioPlayback = false;
        _audioPlayback.Stop();
        AudioStatus = "Wiedergabe gestoppt";
        await ReleaseControllerAsync();
    }

    [RelayCommand]
    private void ToggleStage() => StageMode = !StageMode;

    [RelayCommand]
    private void ToggleQrCode() => QrCodeVisible = !QrCodeVisible;

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        SettingsMessage = string.Empty;
        try
        {
            var library = await _http.GetFromJsonAsync<LibrarySettingsDto>("/api/settings/library");
            SettingsLibraryPath = library?.LibraryPath ?? string.Empty;
            await LoadEventsAsync();
        }
        catch { SettingsMessage = "Servereinstellungen konnten nicht geladen werden."; }
        SettingsVisible = true;
    }

    [RelayCommand]
    private void CloseSettings() => SettingsVisible = false;

    private async Task LoadEventsAsync()
    {
        var eventsTask = _http.GetFromJsonAsync<IReadOnlyList<KaraokeEventDto>>("/api/events");
        var themesTask = _http.GetFromJsonAsync<IReadOnlyList<StageThemeDto>>("/api/stage-themes");
        await Task.WhenAll(eventsTask, themesTask);
        var items = await eventsTask;
        Events.Clear();
        foreach (var item in items ?? []) Events.Add(item);
        SelectedEvent = Events.FirstOrDefault(item => item.IsActive) ?? Events.FirstOrDefault();
        StageThemes.Clear();
        foreach (var theme in await themesTask ?? []) StageThemes.Add(theme);
        SelectedNewEventStageTheme = StageThemes.FirstOrDefault(theme => theme.IsDefault) ?? StageThemes.FirstOrDefault();
    }

    [RelayCommand]
    private async Task CreateEventAsync()
    {
        if (string.IsNullOrWhiteSpace(NewEventName) || !DateTimeOffset.TryParse(NewEventStartsAt, out var startsAt))
        {
            SettingsMessage = "Eventname und Startzeit (z. B. 2026-08-01 19:00) prüfen.";
            return;
        }
        using var response = await _http.PostAsJsonAsync("/api/events", new CreateKaraokeEventRequest(
            NewEventName, startsAt, StageThemeId: SelectedNewEventStageTheme?.Id ?? "standard"));
        if (!response.IsSuccessStatusCode) { SettingsMessage = "Event konnte nicht erstellt werden."; return; }
        NewEventName = string.Empty;
        await LoadEventsAsync();
        SettingsMessage = "Event und Einladungslink wurden erstellt.";
    }

    [RelayCommand]
    private async Task ActivateEventAsync()
    {
        if (SelectedEvent is null) return;
        using var response = await _http.PostAsync($"/api/events/{SelectedEvent.Id}/activate", null);
        if (!response.IsSuccessStatusCode) { SettingsMessage = "Event konnte nicht aktiviert werden."; return; }
        await LoadEventsAsync();
        await LoadActiveEventAsync();
        await RefreshAsync();
        SettingsMessage = "Die Bühne verwendet jetzt dieses Event.";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!Uri.TryCreate(SettingsServerAddress.Trim(), UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https"))
        {
            SettingsMessage = "Bitte eine vollständige HTTP- oder HTTPS-Adresse eingeben.";
            return;
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(SettingsLibraryPath))
            {
                using var response = await _http.PutAsJsonAsync("/api/settings/library", new LibrarySettingsDto(SettingsLibraryPath.Trim()));
                response.EnsureSuccessStatusCode();
            }
            _preferences = new AppPreferences(address.ToString().TrimEnd('/'), Volume, _controllerId);
            _preferences.Save();
            SettingsMessage = address.GetLeftPart(UriPartial.Authority).TrimEnd('/') == _http.BaseAddress!.GetLeftPart(UriPartial.Authority).TrimEnd('/')
                ? "Einstellungen gespeichert. Die Bibliothek wird neu eingelesen."
                : "Gespeichert. Die neue Serveradresse gilt nach einem Neustart der App.";
        }
        catch (Exception exception) { SettingsMessage = "Speichern fehlgeschlagen: " + exception.Message; }
    }

    private static Uri ResolveGuestAddress(Uri configuredAddress)
    {
        if (configuredAddress.Host is not ("localhost" or "127.0.0.1")) return configuredAddress;
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(info => info.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));
        return address is null ? configuredAddress : new UriBuilder(configuredAddress) { Host = address.ToString() }.Uri;
    }

    private static Bitmap? CreateQrCode(string content)
    {
        try
        {
            var bytes = PngByteQRCodeHelper.GetQRCode(content, QRCodeGenerator.ECCLevel.Q, 12, false);
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private void ApplyQueueState(PlaybackStateDto state)
    {
        _currentQueueEntryId = state.Current?.Id;
        if (_completedQueueEntryId != _currentQueueEntryId) _completedQueueEntryId = null;
        Interlocked.Exchange(ref _lastPlaybackRevision, state.Revision);
        Queue.Clear();
        foreach (var entry in state.Queue)
        {
            var item = new QueueItemViewModel(entry);
            Queue.Add(item);
            _ = PopulateCoverAsync(item);
        }
        var next = state.Queue.FirstOrDefault();
        HasNextSong = next is not null;
        NextTitle = next?.Song.Title ?? "Noch offen";
        NextSinger = next is null ? "Wünsch dir einen Song" : $"Danach singt {next.RequestedBy}";
        NextCover = null;
        _nextSongId = next?.Song.Id;
        if (next is not null) _ = LoadNextCoverAsync(next.Song);
        Status = state.Current is null
            ? $"{Queue.Count} Titel in der Warteliste"
            : state.IsPaused
                ? $"Pausiert: {state.Current.Song.Title}"
                : $"Jetzt: {state.Current.Song.Title} – {state.Current.RequestedBy}";
        _ = SynchronizePlaybackAsync(state);
    }

    private async Task ApplyPlaybackCommandAsync(string endpoint)
    {
        var state = await SendPlaybackCommandAsync(endpoint);
        if (state is not null) ApplyQueueState(state);
    }

    private async Task<PlaybackStateDto?> SendPlaybackCommandAsync(string endpoint)
    {
        try
        {
            using var response = await _http.PostAsync(endpoint, null);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PlaybackStateDto>();
        }
        catch (Exception exception)
        {
            Status = "Wiedergabebefehl fehlgeschlagen: " + exception.Message;
            return null;
        }
    }

    private async Task CompleteCurrentEntryAsync(Guid entryId, bool afterFailure)
    {
        await _completionLock.WaitAsync();
        try
        {
            if (!_controlsAudioPlayback || _currentQueueEntryId != entryId || _completedQueueEntryId == entryId) return;
            if (afterFailure) await Task.Delay(750);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var response = await _http.PostAsJsonAsync("/api/playback/complete", new CompletePlaybackRequest(entryId));
                    response.EnsureSuccessStatusCode();
                    var state = await response.Content.ReadFromJsonAsync<PlaybackStateDto>();
                    _completedQueueEntryId = entryId;
                    Interlocked.Exchange(ref _ignoreEndEventsUntilTicks, DateTime.UtcNow.AddSeconds(2).Ticks);
                    if (state is not null) await Dispatcher.UIThread.InvokeAsync(() => ApplyQueueState(state));
                    return;
                }
                catch (Exception) when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
                }
                catch (Exception exception)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => AudioStatus = "Titelwechsel fehlgeschlagen: " + exception.Message);
                }
            }
        }
        finally { _completionLock.Release(); }
    }

    private async Task<bool> ClaimControllerAsync()
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/playback/controller/claim",
                new PlaybackControllerRequest(_controllerId, Environment.MachineName));
            response.EnsureSuccessStatusCode();
            var lease = await response.Content.ReadFromJsonAsync<PlaybackControllerDto>();
            if (lease?.OwnsControl == true) return true;
            Status = string.IsNullOrWhiteSpace(lease?.ControllerName)
                ? "Die Bühne wird bereits von einer anderen App gesteuert."
                : $"Die Bühne wird bereits von {lease.ControllerName} gesteuert.";
        }
        catch (Exception exception)
        {
            Status = "Bühnensteuerung konnte nicht übernommen werden: " + exception.Message;
        }
        return false;
    }

    private async Task MaintainControllerLeaseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_controlsAudioPlayback) continue;
                if (await ClaimControllerAsync()) continue;
                _controlsAudioPlayback = false;
                _audioPlayback.Stop();
                await Dispatcher.UIThread.InvokeAsync(() => AudioStatus = "Bühnensteuerung verloren");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReleaseControllerAsync()
    {
        try { using var response = await _http.DeleteAsync("/api/playback/controller"); }
        catch (Exception) { }
    }

    partial void OnVolumeChanged(int value)
    {
        _audioPlayback.Volume = value;
        if (_preferences is not null)
        {
            _preferences = _preferences with { Volume = value };
            try { _preferences.Save(); } catch { }
        }
    }

    partial void OnVocalVolumeChanged(int value)
    {
        _audioPlayback.VocalVolume = value;
        if (_preferences is not null)
        {
            _preferences = _preferences with { VocalVolume = value };
            try { _preferences.Save(); } catch { }
        }
    }

    private async Task SynchronizePlaybackAsync(PlaybackStateDto state)
    {
        await _playbackLock.WaitAsync();
        try
        {
            LyricsRunning = state.IsRunning && !state.IsPaused;
            if (!_controlsAudioPlayback)
            {
                if (_audioPlayback.IsPlaying) _audioPlayback.Stop();
                _lastServerPaused = state.IsPaused;
                AudioStatus = state.Current is null
                    ? "Wiedergabe bereit"
                    : "Bereit – Play startet den Titel von vorn";
                return;
            }

            if (state.Current is null || !state.IsRunning)
            {
                _audioPlayback.Stop();
                AudioStatus = "Wiedergabe gestoppt";
                _playingSongId = null;
                _controlsAudioPlayback = false;
                _lyrics = [];
                return;
            }

            var startedNewTrack = false;
            if (_playingSongId != state.Current.Song.Id)
            {
                AudioStatus = "Audiostream wird geladen …";
                _playingSongId = state.Current.Song.Id;
                CurrentTitle = $"{state.Current.Song.Title} · {state.Current.Song.Artist}";
                CurrentSinger = state.Current.RequestedBy;
                CurrentCover = null;
                _lyrics = [];
                _pendingSeek = CalculateCurrentPosition(state);
                _isRestoringPosition = _pendingSeek > TimeSpan.FromMilliseconds(500);
                var stems = await GetStemAvailabilityAsync(state.Current.Song.Id);
                var source = stems is { HasInstrumental: true }
                    ? new Uri(_http.BaseAddress!, $"/api/songs/{state.Current.Song.Id}/stems/instrumental")
                    : new Uri(_http.BaseAddress!, $"/api/songs/{state.Current.Song.Id}/audio");
                var vocals = stems is { HasVocals: true }
                    ? new Uri(_http.BaseAddress!, $"/api/songs/{state.Current.Song.Id}/stems/vocals")
                    : null;
                await _audioPlayback.PlayAsync(source, vocals);
                AudioStatus = "Warte auf Audio-Decoder …";
                _ = LoadPlaybackAssetsAsync(state.Current.Song);
                startedNewTrack = true;
            }

            if (state.IsPaused)
            {
                _audioPlayback.Pause();
                AudioStatus = "Wiedergabe pausiert";
            }
            else if (!startedNewTrack && _lastServerPaused)
            {
                _audioPlayback.Resume();
            }
            _lastServerPaused = state.IsPaused;
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => AudioStatus = "Audiofehler: " + exception.Message);
        }
        finally { _playbackLock.Release(); }
    }

    private TimeSpan CalculateCurrentPosition(PlaybackStateDto state)
    {
        var position = state.Position;
        if (!state.IsPaused && state.PositionUpdatedAt is { } updatedAt)
        {
            var elapsed = DateTimeOffset.UtcNow - updatedAt;
            // Nur frische Positionsimpulse stammen sicher von einer noch laufenden Wiedergabe.
            // Nach App-/Serverpausen wird exakt an der zuletzt bestätigten Position fortgesetzt.
            if (elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(5))
                position += TimeSpan.FromTicks((long)(Math.Min(elapsed.Ticks, TimeSpan.FromSeconds(2).Ticks) * state.Speed));
        }

        var duration = TimeSpan.FromSeconds(state.Current?.Song.DurationSeconds ?? 0);
        if (duration > TimeSpan.FromSeconds(1))
            position = TimeSpan.FromMilliseconds(Math.Clamp(position.TotalMilliseconds, 0, (duration - TimeSpan.FromSeconds(1)).TotalMilliseconds));
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private void ReportPosition(TimeSpan position)
    {
        if (!_controlsAudioPlayback || _currentQueueEntryId is null || _lastServerPaused || _isRestoringPosition) return;
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastPositionReportTicks);
        if (now - previous < TimeSpan.FromMilliseconds(500).Ticks ||
            Interlocked.CompareExchange(ref _lastPositionReportTicks, now, previous) != previous) return;
        _ = SendPositionAsync(_currentQueueEntryId.Value, position);
    }

    private async Task RestorePositionAsync(TimeSpan target, Guid? songId)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            while (!_audioPlayback.IsSeekable)
            {
                if (_playingSongId != songId || !_controlsAudioPlayback) return;
                await Task.Delay(100, timeout.Token);
            }

            foreach (var delay in new[] { 0, 200, 450, 800 })
            {
                if (delay > 0) await Task.Delay(delay, timeout.Token);
                if (_playingSongId != songId || !_controlsAudioPlayback || !_audioPlayback.IsSeekable) return;
                _audioPlayback.Seek(target);
                await Task.Delay(150, timeout.Token);
                if (Math.Abs((_audioPlayback.Position - target).TotalSeconds) <= 2)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => AudioStatus = $"Wiedergabe fortgesetzt bei {target:mm\\:ss}");
                    return;
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() => AudioStatus = "Wiedergabe läuft – gespeicherte Position konnte nicht gesetzt werden");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => AudioStatus = "Wiedergabe läuft – Stream ist nicht seekbar");
        }
        finally
        {
            if (_playingSongId == songId) _isRestoringPosition = false;
        }
    }

    private async Task SendPositionAsync(Guid queueEntryId, TimeSpan position)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/playback/position", new PlaybackPositionUpdateRequest(queueEntryId, position));
            if (response.IsSuccessStatusCode)
            {
                var update = await response.Content.ReadFromJsonAsync<PlaybackPositionDto>();
                if (update is not null) Interlocked.Exchange(ref _lastPlaybackRevision, update.Revision);
            }
        }
        catch (Exception)
        {
            // Der nächste Positionsimpuls versucht die Synchronisation erneut.
        }
    }

    private void UpdateLyrics(TimeSpan position)
    {
        LyricsPosition = position;
        if (_lyrics.Count == 0)
        {
            CurrentLyric = "Keine synchronisierten Lyrics vorhanden";
            CurrentLyricSung = string.Empty;
            CurrentLyricRemaining = CurrentLyric;
            LyricCue = string.Empty;
            LyricMinusTwo = LyricMinusOne = LyricPlusOne = LyricPlusTwo = string.Empty;
            LyricProgress = 0;
            CurrentLyricWords = [];
            return;
        }

        var index = -1;
        for (var candidate = 0; candidate < _lyrics.Count; candidate++)
        {
            if (_lyrics[candidate].Start > position) break;
            index = candidate;
        }
        while (index > 0)
        {
            var previous = _lyrics[index - 1];
            var previousEnd = EffectiveSungEnd(previous);
            var overlap = previousEnd - _lyrics[index].Start;
            if (overlap <= TimeSpan.Zero || overlap > TimeSpan.FromSeconds(3) || position >= previousEnd)
                break;
            index--;
        }
        if (index < 0)
        {
            var seconds = Math.Max(0, (int)Math.Ceiling((_lyrics[0].Start - position).TotalSeconds));
            CurrentLyric = seconds <= 5 ? $"EINSATZ IN {seconds}" : "GLEICH GEHT'S LOS";
            CurrentLyricSung = string.Empty;
            CurrentLyricRemaining = CurrentLyric;
            LyricCue = _lyrics[0].Text;
            LyricMinusTwo = LyricMinusOne = string.Empty;
            LyricPlusOne = _lyrics[0].Text;
            LyricPlusTwo = LyricAt(1);
            LyricProgress = 0;
            CurrentLyricWords = [];
            return;
        }
        LyricMinusTwo = LyricAt(index - 2);
        LyricMinusOne = LyricAt(index - 1);
        LyricPlusOne = LyricAt(index + 1);
        LyricPlusTwo = LyricAt(index + 2);
        var start = _lyrics[index].Start;
        var naturalEnd = _lyrics[index].End ?? (index + 1 < _lyrics.Count ? _lyrics[index + 1].Start : _audioPlayback.Duration);
        var text = LyricAt(index);
        if (_lyrics[index].Words is { Count: > 0 } timedWords)
        {
            CurrentLyricWords = timedWords;
            var activeCount = 0;
            while (activeCount < timedWords.Count && timedWords[activeCount].Start <= position)
                activeCount++;
            CurrentLyric = text;
            CurrentLyricSung = string.Join(' ', timedWords.Take(activeCount).Select(word => word.Text));
            if (activeCount > 0 && activeCount < timedWords.Count) CurrentLyricSung += " ";
            CurrentLyricRemaining = string.Join(' ', timedWords.Skip(activeCount).Select(word => word.Text));
            var wordStart = timedWords[0].Start;
            var wordEnd = timedWords[^1].End ?? naturalEnd;
            LyricProgress = Math.Clamp((position - wordStart).TotalMilliseconds / Math.Max((wordEnd - wordStart).TotalMilliseconds, 1), 0, 1);
            LyricCue = activeCount == 0 ? "Einsatz …" : string.Empty;
            return;
        }
        CurrentLyricWords = [];
        var estimatedSingingEnd = start + TimeSpan.FromSeconds(Math.Clamp(text.Length / 7d, 1.8, 9));
        var end = naturalEnd <= start ? estimatedSingingEnd : naturalEnd < estimatedSingingEnd ? naturalEnd : estimatedSingingEnd;
        const double highlightSpeed = 1.4;
        LyricProgress = Math.Clamp(
            (position - start).TotalMilliseconds * highlightSpeed /
            Math.Max((end - start).TotalMilliseconds, 1), 0, 1);
        var inPause = string.IsNullOrWhiteSpace(text) || position > end + TimeSpan.FromMilliseconds(350);
        if (inPause)
        {
            CurrentLyric = "♪  INSTRUMENTAL  ♪";
            CurrentLyricSung = string.Empty;
            CurrentLyricRemaining = CurrentLyric;
            var nextStart = index + 1 < _lyrics.Count ? _lyrics[index + 1].Start : naturalEnd;
            var cueSeconds = (int)Math.Ceiling((nextStart - position).TotalSeconds);
            LyricCue = cueSeconds is > 0 and <= 5 ? $"Nächster Einsatz in {cueSeconds}" : string.Empty;
            LyricProgress = 0;
            return;
        }

        CurrentLyric = text;
        var filledCharacters = Math.Clamp((int)Math.Round(text.Length * LyricProgress), 0, text.Length);
        while (filledCharacters < text.Length && filledCharacters > 0 && text[filledCharacters - 1] != ' ')
            filledCharacters++;
        filledCharacters = Math.Min(filledCharacters, text.Length);
        CurrentLyricSung = text[..filledCharacters];
        CurrentLyricRemaining = text[filledCharacters..];
        LyricCue = string.Empty;
    }

    private string LyricAt(int index) => index >= 0 && index < _lyrics.Count ? _lyrics[index].Text : string.Empty;

    private static TimeSpan EffectiveSungEnd(LyricsLineDto line)
    {
        if (line.Words is not { Count: > 0 } words)
            return line.End ?? line.Start;
        var last = words[^1];
        var estimatedDuration = TimeSpan.FromSeconds(Math.Clamp(last.Text.Length / 7d, 0.35, 1.8));
        var estimatedEnd = last.Start + estimatedDuration + TimeSpan.FromMilliseconds(80);
        return last.End is { } supplied && supplied > estimatedEnd ? supplied : estimatedEnd;
    }

    private async Task LoadPlaybackAssetsAsync(SongDto song)
    {
        IReadOnlyList<LyricsLineDto> lyrics = [];
        IReadOnlyList<VisualizationFrameDto> visualization = [];
        try
        {
            lyrics = (await _http.GetFromJsonAsync<LyricsDto>($"/api/songs/{song.Id}/lyrics"))?.Lines ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fehlende oder ungültige Lyrics dürfen die Audiowiedergabe nicht beeinflussen.
        }
        try
        {
            visualization = (await _http.GetFromJsonAsync<SongVisualizationDto>($"/api/songs/{song.Id}/visualization"))?.Frames ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Der Song bleibt auch ohne vorberechnete Hintergrundeffekte abspielbar.
        }
        var cover = await LoadCoverAsync(song);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_playingSongId != song.Id) return;
            _lyrics = lyrics;
            VisualizationFrames = visualization;
            CurrentCover = cover;
            UpdateLyrics(_audioPlayback.Position);
        });
    }

    private async Task<StemAvailabilityDto?> GetStemAvailabilityAsync(Guid songId)
    {
        try
        {
            return await _http.GetFromJsonAsync<StemAvailabilityDto>($"/api/songs/{songId}/stems")
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Die normale Audiospur bleibt auch bei einer langsamen Stem-Abfrage spielbar.
            return null;
        }
    }

    private async Task PopulateCoverAsync(ICoverItem item)
    {
        var cover = await LoadCoverAsync(item.Song);
        await Dispatcher.UIThread.InvokeAsync(() => item.Cover = cover);
    }

    private async Task LoadNextCoverAsync(SongDto song)
    {
        var cover = await LoadCoverAsync(song);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_nextSongId == song.Id) NextCover = cover;
        });
    }

    private Task<Bitmap?> LoadCoverAsync(SongDto song)
    {
        if (!song.HasCover) return Task.FromResult<Bitmap?>(null);
        lock (_coverCache)
        {
            if (_coverCache.TryGetValue(song.Id, out var cached)) return cached;
            var load = LoadCoverCoreAsync(song.Id);
            _coverCache[song.Id] = load;
            return load;
        }
    }

    private async Task<Bitmap?> LoadCoverCoreAsync(Guid songId)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync($"/api/songs/{songId}/cover");
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return new Bitmap(stream);
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // Native VLC-Aufrufe können beim Fensterschließen noch aus einem Event zurückkehren.
        // Nicht den UI-Thread auf deren Abschluss warten lassen.
        _ = Task.Run(() =>
        {
            try { _audioPlayback.Dispose(); } catch { }
        });
        _ = ReleaseControllerAtShutdownAsync(_http.BaseAddress!, _controllerId);
        _ = _hubConnection.DisposeAsync();
        _lifetime.Dispose();
        _http.Dispose();
    }

    private static async Task ReleaseControllerAtShutdownAsync(Uri serverAddress, Guid controllerId)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromSeconds(1) };
            client.DefaultRequestHeaders.Add("X-Karaoke-Controller", controllerId.ToString());
            using var response = await client.DeleteAsync("/api/playback/controller").ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}

public interface ICoverItem
{
    SongDto Song { get; }
    Bitmap? Cover { get; set; }
}

public partial class SongItemViewModel(SongDto song) : ObservableObject, ICoverItem
{
    public SongDto Song { get; } = song;
    public Guid Id => Song.Id;
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    public string Album => Song.Album;
    public bool HasLyrics => Song.HasLyrics;
    [ObservableProperty] private Bitmap? cover;
}

public partial class QueueItemViewModel(QueueEntryDto entry) : ObservableObject, ICoverItem
{
    public QueueEntryDto Entry { get; } = entry;
    public SongDto Song => Entry.Song;
    public string RequestedBy => Entry.RequestedBy;
    public int Position => Entry.Position;
    [ObservableProperty] private Bitmap? cover;
}
