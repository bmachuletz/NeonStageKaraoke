using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public sealed class EventManagementViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly Guid ProtectedDefaultEventId = new("00000000-0000-0000-0000-000000000001");
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private EditorEventItem? _selectedEvent;
    private Bitmap? _qrCode;
    private byte[]? _qrBytes;
    private int _wishCount;
    private string _status = "Events werden geladen …";
    private bool _busy;

    public EventManagementViewModel(Uri serverAddress)
    {
        ServerAddress = serverAddress;
        _http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromMinutes(2) };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<EditorEventItem> Events { get; } = [];
    public Uri ServerAddress { get; }
    public EditorEventItem? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!Set(ref _selectedEvent, value)) return;
            NotifySelection();
            _ = LoadSelectedDetailsAsync(value, _lifetime.Token);
        }
    }
    public Bitmap? QrCode
    {
        get => _qrCode;
        private set
        {
            if (ReferenceEquals(_qrCode, value)) return;
            var previous = _qrCode;
            _qrCode = value;
            OnPropertyChanged();
            previous?.Dispose();
        }
    }
    public bool HasSelection => SelectedEvent is not null;
    public bool CanActivate => SelectedEvent is { IsActive: false } && !Busy;
    public bool CanDeactivate => SelectedEvent is { IsActive: true } && !Busy;
    public bool CanDelete => SelectedEvent is { IsActive: false } selected && selected.Id != ProtectedDefaultEventId && !Busy;
    public int WishCount { get => _wishCount; private set { if (Set(ref _wishCount, value)) OnPropertyChanged(nameof(WishCountLabel)); } }
    public string WishCountLabel => EditorLocale.German
        ? $"{WishCount} Wunsch/Wünsche in dieser Session"
        : $"{WishCount} request(s) in this session";
    public string InvitationUrl => SelectedEvent is null ? string.Empty :
        new Uri(ServerAddress, $"/e/{Uri.EscapeDataString(SelectedEvent.InviteToken)}").AbsoluteUri;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanActivate));
            OnPropertyChanged(nameof(CanDeactivate));
            OnPropertyChanged(nameof(CanDelete));
        }
    }

    public async Task LoadAsync(Guid? preferredId = null, CancellationToken cancellationToken = default)
    {
        preferredId ??= SelectedEvent?.Id;
        Busy = true;
        try
        {
            var events = await _http.GetFromJsonAsync<IReadOnlyList<KaraokeEventDto>>(
                "/api/events", cancellationToken) ?? [];
            Events.Clear();
            foreach (var item in events.OrderByDescending(item => item.IsActive)
                         .ThenByDescending(item => item.StartsAt))
                Events.Add(new EditorEventItem(item));
            SelectedEvent = Events.FirstOrDefault(item => item.Id == preferredId)
                            ?? Events.FirstOrDefault(item => item.IsActive)
                            ?? Events.FirstOrDefault();
            Status = EditorLocale.German
                ? $"{Events.Count} Session(s) geladen."
                : $"Loaded {Events.Count} session(s).";
        }
        catch (Exception exception)
        {
            Status = (EditorLocale.German ? "Events konnten nicht geladen werden: " : "Could not load events: ") + exception.Message;
        }
        finally { Busy = false; }
    }

    public async Task CreateAsync(CreateKaraokeEventRequest request, CancellationToken cancellationToken = default)
    {
        await MutateAsync(async () =>
        {
            using var response = await _http.PostAsJsonAsync("/api/events", request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var created = await response.Content.ReadFromJsonAsync<KaraokeEventDto>(cancellationToken: cancellationToken)
                          ?? throw new InvalidOperationException("Server returned no event.");
            await LoadAsync(created.Id, cancellationToken);
            Report(EditorLocale.German ? "Event wurde angelegt." : "Event created.");
        });
    }

    public Task CreateInstantSessionAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var name = EditorLocale.German
            ? $"Sofort-Session · {now:dd.MM.yyyy HH:mm}"
            : $"Instant session · {now:g}";
        var description = EditorLocale.German
            ? "Spontane Karaoke-Session (Lyrics Editor)"
            : "Instant karaoke session (Lyrics Editor)";
        return MutateAsync(async () =>
        {
            using var createResponse = await _http.PostAsJsonAsync("/api/events",
                new CreateKaraokeEventRequest(name, now, Description: description), cancellationToken);
            await EnsureSuccessAsync(createResponse, cancellationToken);
            var created = await createResponse.Content.ReadFromJsonAsync<KaraokeEventDto>(cancellationToken: cancellationToken)
                          ?? throw new InvalidOperationException("Server returned no event.");
            using var activateResponse = await _http.PostAsync($"/api/events/{created.Id}/activate", null, cancellationToken);
            await EnsureSuccessAsync(activateResponse, cancellationToken);
            await LoadAsync(created.Id, cancellationToken);
            Report(EditorLocale.German ? "Sofort-Session ist aktiv." : "Instant session is active.");
        });
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default) => WithSelectedAsync(async selected =>
    {
        using var response = await _http.PostAsync($"/api/events/{selected.Id}/activate", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await LoadAsync(selected.Id, cancellationToken);
        Report(EditorLocale.German ? "Bühne wurde auf dieses Event umgeschaltet." : "Stage switched to this event.");
    });

    public Task DeactivateAsync(CancellationToken cancellationToken = default) => WithSelectedAsync(async selected =>
    {
        using var response = await _http.PostAsync($"/api/events/{selected.Id}/deactivate", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await LoadAsync(selected.Id, cancellationToken);
        Report(EditorLocale.German ? "Aktive Bühne wurde beendet." : "Active stage session ended.");
    });

    public Task ProcessWishesAsync(CancellationToken cancellationToken = default) => WithSelectedAsync(async selected =>
    {
        using var response = await _http.PostAsync($"/api/events/{selected.Id}/wishlist/process", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        Report(EditorLocale.German
            ? $"Wünsche für „{selected.Name}“ wurden an die Pipeline übergeben."
            : $"Requests for “{selected.Name}” were queued for processing.");
    });

    public Task DeleteAsync(CancellationToken cancellationToken = default) => WithSelectedAsync(async selected =>
    {
        using var response = await _http.DeleteAsync(
            $"/api/events/{selected.Id}?includeOpenWishes=true", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await LoadAsync(null, cancellationToken);
        Report(EditorLocale.German ? "Session einschließlich Queue und Wünschen gelöscht." :
            "Session, queue and requests deleted.");
    });

    public async Task<byte[]> GetQrBytesAsync(CancellationToken cancellationToken = default)
    {
        if (_qrBytes is not null) return _qrBytes;
        if (SelectedEvent is null) return [];
        _qrBytes = await _http.GetByteArrayAsync(
            $"/api/events/{Uri.EscapeDataString(SelectedEvent.InviteToken)}/qr", cancellationToken);
        return _qrBytes;
    }

    public void Report(string message) => Status = message;

    private Task WithSelectedAsync(Func<EditorEventItem, Task> operation) =>
        SelectedEvent is { } selected ? MutateAsync(() => operation(selected)) : Task.CompletedTask;

    private async Task MutateAsync(Func<Task> operation)
    {
        if (Busy) return;
        Busy = true;
        try { await operation(); }
        catch (Exception exception)
        {
            Status = (EditorLocale.German ? "Aktion fehlgeschlagen: " : "Action failed: ") + exception.Message;
        }
        finally { Busy = false; }
    }

    private async Task LoadSelectedDetailsAsync(EditorEventItem? selected, CancellationToken cancellationToken)
    {
        _qrBytes = null;
        QrCode = null;
        WishCount = 0;
        OnPropertyChanged(nameof(InvitationUrl));
        if (selected is null) return;
        try
        {
            var token = Uri.EscapeDataString(selected.InviteToken);
            var qrTask = _http.GetByteArrayAsync($"/api/events/{token}/qr", cancellationToken);
            var wishesTask = _http.GetFromJsonAsync<IReadOnlyList<WishDto>>(
                $"/api/wishlist?eventToken={token}", cancellationToken);
            await Task.WhenAll(qrTask, wishesTask);
            if (SelectedEvent?.Id != selected.Id) return;
            _qrBytes = await qrTask;
            QrCode = new Bitmap(new MemoryStream(_qrBytes));
            WishCount = (await wishesTask)?.Count ?? 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (SelectedEvent?.Id == selected.Id)
                Status = (EditorLocale.German ? "Eventdetails konnten nicht geladen werden: " :
                    "Could not load event details: ") + exception.Message;
        }
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanDeactivate));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(InvitationUrl));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"HTTP {(int)response.StatusCode}" : detail.Trim('"'));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        QrCode = null;
        _http.Dispose();
    }
}

public sealed class EditorEventItem(KaraokeEventDto source)
{
    public Guid Id => source.Id;
    public string Name => source.Name;
    public string InviteToken => source.InviteToken;
    public bool IsActive => source.IsActive;
    public string Description => source.Description ?? string.Empty;
    public string StatusLabel => IsActive ? (EditorLocale.German ? "● AKTIV" : "● ACTIVE") :
        source.EndsAt < DateTimeOffset.Now ? (EditorLocale.German ? "BEENDET" : "ENDED") :
        (EditorLocale.German ? "GEPLANT" : "PLANNED");
    public string StatusColor => IsActive ? "#DFFF28" : "#FF3CBD";
    public string ScheduleLabel
    {
        get
        {
            var start = source.StartsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            return source.EndsAt is { } end
                ? $"{start} – {end.ToLocalTime():g}"
                : start;
        }
    }
}
