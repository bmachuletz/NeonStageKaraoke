using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class ReplacementLyricsWindow : Window
{
    private readonly HttpClient _http;
    private readonly SongDto _song;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;

    public ObservableCollection<ReplacementLyricsCandidateViewItem> Results { get; } = [];

    public ReplacementLyricsWindow() : this(new Uri("http://127.0.0.1:5274"),
        new SongDto(Guid.Empty, string.Empty, string.Empty, string.Empty, 0, false)) { }

    public ReplacementLyricsWindow(Uri serverAddress, SongDto song)
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
        SetBusy(true, EditorLocale.German ? "Lyrics-Quellen werden durchsucht …" : "Searching lyrics sources …");
        try
        {
            var query = Uri.EscapeDataString(QueryBox.Text?.Trim() ?? string.Empty);
            using var response = await _http.GetAsync(
                $"/api/admin/songs/{_song.Id}/lyrics/replacements/search?query={query}", _cancellation.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ReadErrorAsync(response, _cancellation.Token));
            var result = await response.Content.ReadFromJsonAsync<ReplacementLyricsSearchDto>(
                cancellationToken: _cancellation.Token) ?? throw new InvalidDataException("Empty lyrics response.");
            Results.Clear();
            foreach (var item in result.Items) Results.Add(new(item));
            ResultsList.SelectedItem = Results.FirstOrDefault(item => item.IsRecommended);
            SourcesLabel.Text = string.Join("   ·   ", result.Sources.Select(SourceStatus));
            var providerMatches = Results.Count(item => !item.Candidate.IsFullTranscript);
            StatusLabel.Text = providerMatches == 0
                ? EditorLocale.German
                    ? "Keine passende Provider-Fassung gefunden. Das Volltranskript aus dem Audio ist verfügbar."
                    : "No matching provider version was found. Full transcription from audio is available."
                : EditorLocale.German
                    ? $"{providerMatches} Provider-Fassung(en) gefunden. Das Volltranskript steht als Alternative bereit."
                    : $"Found {providerMatches} provider version(s). Full transcription is available as an alternative.";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Results.Clear();
            SourcesLabel.Text = string.Empty;
            StatusLabel.Text = (EditorLocale.German ? "Lyrics-Suche fehlgeschlagen: " : "Lyrics search failed: ") +
                               exception.Message;
        }
        finally { SetBusy(false); }
    }

    private static string SourceStatus(ReplacementLyricsSourceStatusDto source)
    {
        if (source.Source.Equals("Volltranskript", StringComparison.OrdinalIgnoreCase))
            return EditorLocale.German ? "Volltranskript: immer verfügbar" : "Full transcript: always available";
        if (!string.IsNullOrWhiteSpace(source.Error))
            return $"{source.Source}: {source.MatchCount} Treffer · {source.Error}";
        return $"{source.Source}: {source.MatchCount} Treffer";
    }

    private void ResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = ResultsList.SelectedItem as ReplacementLyricsCandidateViewItem;
        SelectButton.Content = selected?.Candidate.IsFullTranscript == true
            ? EditorLocale.German ? "Volltranskript starten" : "Start full transcription"
            : EditorLocale.German ? "Auswählen und mit EasyAligner ausrichten" : "Select and align with EasyAligner";
        SelectButton.IsEnabled = !_busy && selected?.CanRetrieve == true;
        OpenSourceButton.IsVisible = selected?.ExternalUrl is not null;
        OpenSourceButton.IsEnabled = !_busy && selected?.ExternalUrl is not null;
    }

    private void OpenSourceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ReplacementLyricsCandidateViewItem { ExternalUrl: { } url }) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void SelectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || ResultsList.SelectedItem is not ReplacementLyricsCandidateViewItem selected) return;
        SetBusy(true, EditorLocale.German
            ? selected.Candidate.IsFullTranscript
                ? "Vollständige Lyrics werden direkt aus der Audiodatei erkannt …"
                : $"{selected.SourceLabel} wird geladen; EasyAligner wird gestartet …"
            : selected.Candidate.IsFullTranscript
                ? "Recognizing complete lyrics directly from the audio …"
                : $"Downloading {selected.SourceLabel}; starting EasyAligner …");
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"/api/admin/songs/{_song.Id}/lyrics/replacements/apply",
                new RetrieveReplacementLyricsRequest(selected.Candidate.SelectionToken), _cancellation.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ReadErrorAsync(response, _cancellation.Token));
            var result = await response.Content.ReadFromJsonAsync<RetrieveReplacementLyricsResultDto>(
                cancellationToken: _cancellation.Token) ?? throw new InvalidDataException("Empty import response.");
            Close(result);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusLabel.Text = (EditorLocale.German ? "Lyrics konnten nicht übernommen werden: " :
                "Could not apply lyrics: ") + exception.Message;
            SetBusy(false);
        }
    }

    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        Progress.IsVisible = busy;
        QueryBox.IsEnabled = !busy;
        SelectButton.IsEnabled = !busy && ResultsList.SelectedItem is ReplacementLyricsCandidateViewItem
            { CanRetrieve: true };
        OpenSourceButton.IsEnabled = !busy && ResultsList.SelectedItem is ReplacementLyricsCandidateViewItem
            { ExternalUrl: not null };
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

public sealed record ReplacementLyricsCandidateViewItem(ReplacementLyricsCandidateDto Candidate)
{
    public bool IsRecommended => Candidate.IsRecommended;
    public bool CanRetrieve => Candidate.CanRetrieve;
    public string? ExternalUrl => Candidate.ExternalUrl;
    public string SourceLabel => Candidate.IsFullTranscript
        ? EditorLocale.German ? "VOLLTEXT" : "FULL TRANSCRIPT"
        : Candidate.Source + " #" + Candidate.SourceId;
    public string Heading => Candidate.IsFullTranscript
        ? EditorLocale.German ? "Lyrics vollständig aus Audio erkennen" : "Recognize complete lyrics from audio"
        : $"{Candidate.Title} · {Candidate.Artist}";
    public string Details => string.Join(" · ", new[]
    {
        Candidate.Album,
        Candidate.DurationSeconds is { } duration ? TimeSpan.FromSeconds(duration).ToString(@"m\:ss") : null,
        Candidate.Language,
        Candidate.Edition
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string ScoreLabel => Candidate.IsFullTranscript ? "ALTERNATIVE" : $"{Candidate.Score:0.0}%";
    public string ScoreCaption => Candidate.IsFullTranscript ? string.Empty : "Match";
    public string TimingLabel => Candidate.HasSyncedLyrics
        ? EditorLocale.German ? "ZEILENSTRUKTUR + QUELLTIMING" : "LINES + SOURCE TIMING"
        : Candidate.IsFullTranscript
            ? EditorLocale.German ? "AUDIO → VOLLTEXT" : "AUDIO → FULL TEXT"
        : EditorLocale.German ? "REINER TEXT" : "PLAIN TEXT";
}
