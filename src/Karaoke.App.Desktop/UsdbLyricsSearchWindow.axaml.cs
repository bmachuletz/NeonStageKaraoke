using System.Collections.ObjectModel;
using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class UsdbLyricsSearchWindow : Window
{
    private readonly HttpClient _http;
    private readonly SongDto _song;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;

    public ObservableCollection<UsdbCandidateViewItem> Results { get; } = [];

    public UsdbLyricsSearchWindow() : this(new Uri("http://127.0.0.1:5274"),
        new SongDto(Guid.Empty, string.Empty, string.Empty, string.Empty, 0, false))
    {
    }

    public UsdbLyricsSearchWindow(Uri serverAddress, SongDto song)
    {
        InitializeComponent();
        _song = song;
        _http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromMinutes(5) };
        ResultsList.ItemsSource = Results;
        SongLabel.Text = $"{song.Title} · {song.Artist}";
        QueryBox.Text = song.Title;
        Opened += async (_, _) =>
        {
            EditorLocale.Apply(this);
            await SearchAsync();
        };
        Closed += (_, _) =>
        {
            _cancellation.Cancel();
            _http.Dispose();
            _cancellation.Dispose();
        };
    }

    private async void SearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await SearchAsync();

    private async void QueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_busy) return;
        SetBusy(true, EditorLocale.German ? "USDB wird durchsucht …" : "Searching USDB …");
        try
        {
            var query = Uri.EscapeDataString(QueryBox.Text?.Trim() ?? string.Empty);
            var response = await _http.GetAsync(
                $"/api/admin/songs/{_song.Id}/lyrics/usdb/search?query={query}", _cancellation.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ReadErrorAsync(response, _cancellation.Token));
            var result = await response.Content.ReadFromJsonAsync<UsdbLyricsSearchDto>(
                cancellationToken: _cancellation.Token) ?? throw new InvalidDataException("Empty USDB response.");
            Results.Clear();
            foreach (var item in result.Items) Results.Add(new(item));
            ResultsList.SelectedItem = Results.FirstOrDefault(item => item.IsRecommended);
            StatusLabel.Text = Results.Count == 0
                ? EditorLocale.German ? "Keine USDB-Version gefunden." : "No USDB version found."
                : EditorLocale.German
                    ? $"{Results.Count} Version(en) gefunden. Die Audiodauer wird bei der Auswahl abschließend geprüft."
                    : $"Found {Results.Count} version(s). Audio duration is checked when you select one.";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Results.Clear();
            StatusLabel.Text = (EditorLocale.German ? "USDB-Suche fehlgeschlagen: " : "USDB search failed: ") +
                               exception.Message;
        }
        finally { SetBusy(false); }
    }

    private void ResultSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = !_busy && ResultsList.SelectedItem is UsdbCandidateViewItem;

    private async void SelectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || ResultsList.SelectedItem is not UsdbCandidateViewItem selected) return;
        SetBusy(true, EditorLocale.German
            ? "UltraStar-Datei wird geladen und als neue Lyrics-Version gespeichert …"
            : "Downloading UltraStar file and saving a new lyrics version …");
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"/api/admin/songs/{_song.Id}/lyrics/usdb/import",
                new ImportUsdbLyricsRequest(selected.Candidate.SelectionToken,
                    LocalAlignmentCheck.IsChecked == true), _cancellation.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ReadErrorAsync(response, _cancellation.Token));
            var result = await response.Content.ReadFromJsonAsync<ImportUsdbLyricsResultDto>(
                cancellationToken: _cancellation.Token) ?? throw new InvalidDataException("Empty import response.");
            Close(result);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusLabel.Text = (EditorLocale.German ? "USDB-Import fehlgeschlagen: " : "USDB import failed: ") +
                               exception.Message;
            SetBusy(false);
        }
    }

    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        Progress.IsVisible = busy;
        QueryBox.IsEnabled = !busy;
        LocalAlignmentCheck.IsEnabled = !busy;
        SelectButton.IsEnabled = !busy && ResultsList.SelectedItem is UsdbCandidateViewItem;
        if (status is not null) StatusLabel.Text = status;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return $"HTTP {(int)response.StatusCode}";
        try
        {
            var problem = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(text);
            if (problem.TryGetProperty("detail", out var detail)) return detail.GetString() ?? text;
        }
        catch (System.Text.Json.JsonException) { }
        return text;
    }
}

public sealed record UsdbCandidateViewItem(UsdbLyricsCandidateDto Candidate)
{
    public bool IsRecommended => Candidate.IsRecommended;
    public string Heading => $"{Candidate.Title} · {Candidate.Artist}";
    public string Details => string.Join(" · ", new[]
    {
        Candidate.Year?.ToString(), Candidate.Language, Candidate.Edition,
        $"{Candidate.Source} #{Candidate.VersionId}"
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string ScoreLabel => $"{Candidate.Score:0.0}%";
    public string ConfidenceLabel => Candidate.Confidence switch
    {
        UsdbLyricsCandidateConfidence.Exact => EditorLocale.German ? "SEHR PASSEND" : "VERY CLOSE",
        UsdbLyricsCandidateConfidence.Strong => EditorLocale.German ? "PASSEND" : "CLOSE",
        _ => EditorLocale.German ? "MÖGLICH" : "POSSIBLE"
    };
}
