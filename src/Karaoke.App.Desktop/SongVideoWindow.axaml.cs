using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class SongVideoWindow : Window
{
    private readonly HttpClient _http;
    private readonly SongDto _song;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;
    public ObservableCollection<SongVideoCandidateItem> Results { get; } = [];

    public SongVideoWindow() : this(new Uri("http://127.0.0.1:5274"),
        new SongDto(Guid.Empty, string.Empty, string.Empty, string.Empty, 0, false)) { }

    public SongVideoWindow(Uri serverAddress, SongDto song)
    {
        InitializeComponent();
        _song = song;
        _http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromHours(2) };
        ResultsList.ItemsSource = Results;
        SongLabel.Text = $"{song.Title} · {song.Artist}";
        QueryBox.Text = $"{song.Artist} {song.Title} official music video";
        Opened += async (_, _) =>
        {
            EditorLocale.Apply(this);
            await LoadCurrentAsync();
            await SearchAsync();
        };
        Closed += (_, _) => { _cancellation.Cancel(); _http.Dispose(); _cancellation.Dispose(); };
    }

    private async Task LoadCurrentAsync()
    {
        using var response = await _http.GetAsync($"/api/songs/{_song.Id}/video/info", _cancellation.Token);
        if (!response.IsSuccessStatusCode) return;
        var info = await response.Content.ReadFromJsonAsync<SongVideoInfoDto>(cancellationToken: _cancellation.Token);
        if (info is null) return;
        OffsetBox.Value = info.OffsetMilliseconds;
        StatusLabel.Text = EditorLocale.German
            ? $"Aktuell: {info.Title} · stumm · Versatz {info.OffsetMilliseconds:+#;-#;0} ms"
            : $"Current: {info.Title} · muted · offset {info.OffsetMilliseconds:+#;-#;0} ms";
    }

    private async void SearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SearchAsync();
    private async void QueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_busy) return;
        SetBusy(true, EditorLocale.German ? "YouTube-Vorschläge werden geladen …" : "Loading YouTube suggestions …");
        try
        {
            var query = Uri.EscapeDataString(QueryBox.Text?.Trim() ?? string.Empty);
            var result = await _http.GetFromJsonAsync<SongVideoSearchDto>(
                $"/api/admin/songs/{_song.Id}/video/search?query={query}", _cancellation.Token)
                         ?? throw new InvalidDataException("Empty video response.");
            Results.Clear();
            foreach (var candidate in result.Items) Results.Add(new(candidate));
            ResultsList.SelectedItem = Results.FirstOrDefault();
            StatusLabel.Text = result.ProviderStatus;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception) { StatusLabel.Text = exception.Message; }
        finally { SetBusy(false); }
    }

    private void ResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = ResultsList.SelectedItem is SongVideoCandidateItem;
        PreviewButton.IsEnabled = !_busy && selected;
        SelectButton.IsEnabled = !_busy && selected;
    }

    private void PreviewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not SongVideoCandidateItem selected) return;
        Process.Start(new ProcessStartInfo(selected.Candidate.WebUrl) { UseShellExecute = true });
    }

    private async void SelectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || ResultsList.SelectedItem is not SongVideoCandidateItem selected) return;
        if (AuthorizationBox.IsChecked != true)
        {
            StatusLabel.Text = EditorLocale.German
                ? "Bitte bestätige zuerst, dass du dieses Video herunterladen und anzeigen darfst."
                : "Confirm that you are allowed to download and display this video.";
            return;
        }
        SetBusy(true, EditorLocale.German ? "Video wird geladen und stumm normalisiert …" : "Downloading and muting video …");
        try
        {
            using var response = await _http.PostAsJsonAsync($"/api/admin/songs/{_song.Id}/video/select",
                new SelectSongVideoRequest(selected.Candidate.SelectionToken, true), _cancellation.Token);
            await EnsureSuccessAsync(response, _cancellation.Token);
            var info = await response.Content.ReadFromJsonAsync<SongVideoInfoDto>(cancellationToken: _cancellation.Token)
                       ?? throw new InvalidDataException("Empty video response.");
            info = await SaveOffsetAsync(info);
            StatusLabel.Text = EditorLocale.German ? "Video wurde stumm gespeichert." : "Muted video saved.";
            Close(info);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { StatusLabel.Text = exception.Message; SetBusy(false); }
    }

    private async void UploadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorLocale.Text("Eigene Videodatei auswählen"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video") { Patterns = ["*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.m4v"] }]
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        SetBusy(true, EditorLocale.German ? "Video wird hochgeladen und stumm normalisiert …" : "Uploading and muting video …");
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var form = new MultipartFormDataContent();
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", file.Name);
            using var response = await _http.PostAsync($"/api/admin/songs/{_song.Id}/video/upload", form, _cancellation.Token);
            await EnsureSuccessAsync(response, _cancellation.Token);
            var info = await response.Content.ReadFromJsonAsync<SongVideoInfoDto>(cancellationToken: _cancellation.Token)
                       ?? throw new InvalidDataException("Empty video response.");
            Close(await SaveOffsetAsync(info));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { StatusLabel.Text = exception.Message; SetBusy(false); }
    }

    private async void SaveOffsetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            using var currentResponse = await _http.GetAsync($"/api/songs/{_song.Id}/video/info", _cancellation.Token);
            await EnsureSuccessAsync(currentResponse, _cancellation.Token);
            var current = await currentResponse.Content.ReadFromJsonAsync<SongVideoInfoDto>(
                cancellationToken: _cancellation.Token) ?? throw new InvalidDataException("Empty video response.");
            Close(await SaveOffsetAsync(current));
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { StatusLabel.Text = exception.Message; }
    }

    private async Task<SongVideoInfoDto> SaveOffsetAsync(SongVideoInfoDto fallback)
    {
        using var response = await _http.PutAsJsonAsync($"/api/admin/songs/{_song.Id}/video/offset",
            new UpdateSongVideoOffsetRequest((int)(OffsetBox.Value ?? 0)), _cancellation.Token);
        await EnsureSuccessAsync(response, _cancellation.Token);
        return await response.Content.ReadFromJsonAsync<SongVideoInfoDto>(cancellationToken: _cancellation.Token)
               ?? fallback;
    }

    private async void DeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        using var response = await _http.DeleteAsync($"/api/admin/songs/{_song.Id}/video", _cancellation.Token);
        if (response.IsSuccessStatusCode) Close(new SongVideoInfoDto("Removed", null, null, "", null, null, 0, DateTimeOffset.UtcNow));
        else StatusLabel.Text = EditorLocale.German ? "Für diesen Song ist kein Video hinterlegt." : "No video is assigned to this song.";
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy; Progress.IsVisible = busy; QueryBox.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && ResultsList.SelectedItem is SongVideoCandidateItem;
        SelectButton.IsEnabled = !busy && ResultsList.SelectedItem is SongVideoCandidateItem;
        if (text is not null) StatusLabel.Text = text;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("detail", out var detail) &&
                    !string.IsNullOrWhiteSpace(detail.GetString()))
                    throw new InvalidOperationException(detail.GetString());
                if (json.RootElement.TryGetProperty("title", out var title) &&
                    !string.IsNullOrWhiteSpace(title.GetString()))
                    throw new InvalidOperationException(title.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            throw new InvalidOperationException(body.Trim('"'));
        }
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }
}

public sealed record SongVideoCandidateItem(SongVideoCandidateDto Candidate)
{
    public string Title => Candidate.Title;
    public string Details => $"{Candidate.Channel} · " +
        (Candidate.DurationSeconds is { } duration ? TimeSpan.FromSeconds(duration).ToString(@"m\:ss") : "–");
}
