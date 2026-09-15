using System.Net.Http.Json;
using Avalonia.Controls;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class SettingsWindow : Window
{
    private readonly HttpClient _http;
    private readonly Uri _serverAddress;
    private bool _managedByEnvironment;
    private bool _geniusManagedByEnvironment;

    public SettingsWindow() : this(new Uri(
        (Environment.GetEnvironmentVariable("NEONSTAGE_SERVER_URL") ??
         Environment.GetEnvironmentVariable("KARAOKE_SERVER"))?.Trim()
        ?? "http://192.168.178.91:5274")) { }

    public SettingsWindow(Uri serverAddress)
    {
        InitializeComponent();
        _serverAddress = serverAddress;
        _http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromSeconds(30) };
        ServerUrlBox.Text = serverAddress.AbsoluteUri.TrimEnd('/');
        TransportWarning.IsVisible = serverAddress.Scheme != Uri.UriSchemeHttps && !serverAddress.IsLoopback;
        Opened += async (_, _) =>
        {
            EditorLocale.Apply(this);
            await LoadAsync();
        };
        Closed += (_, _) => _http.Dispose();
    }

    private async Task LoadAsync()
    {
        SetBusy(true);
        try
        {
            var libraryTask = _http.GetFromJsonAsync<LibrarySettingsDto>("/api/settings/library");
            var usdbTask = _http.GetFromJsonAsync<UsdbProviderSettingsDto>("/api/admin/settings/usdb");
            var geniusTask = _http.GetFromJsonAsync<GeniusProviderSettingsDto>("/api/admin/settings/genius");
            var nvencTask = EditorExportSettings.DetectNvencAsync();
            await Task.WhenAll(libraryTask, usdbTask, geniusTask);
            var library = await libraryTask ?? throw new InvalidDataException("Missing library settings.");
            var usdb = await usdbTask ?? throw new InvalidDataException("Missing USDB settings.");
            var genius = await geniusTask ?? throw new InvalidDataException("Missing Genius settings.");
            LibraryPathBox.Text = library.LibraryPath;
            UsdbEnabledBox.IsChecked = usdb.Enabled;
            UsdbBaseUrlBox.Text = usdb.BaseUrl;
            AnimuxEnabledBox.IsChecked = usdb.AnimuxEnabled;
            AnimuxBaseUrlBox.Text = usdb.AnimuxBaseUrl;
            AnimuxUsernameBox.Text = usdb.AnimuxUsername;
            AnimuxPasswordBox.Text = string.Empty;
            AnimuxPasswordBox.Watermark = usdb.HasAnimuxPassword
                ? Text("Gespeichertes Passwort vorhanden · leer lassen zum Behalten",
                    "Stored password present · leave blank to keep")
                : Text("Animux-Passwort eingeben", "Enter Animux password");
            UsdbStatusText.Text = usdb.Status;
            SetManagedState(usdb.ManagedByEnvironment);
            GeniusEnabledBox.IsChecked = genius.Enabled;
            GeniusBaseUrlBox.Text = genius.BaseUrl;
            GeniusAccessTokenBox.Text = string.Empty;
            GeniusAccessTokenBox.Watermark = genius.HasAccessToken
                ? Text("Gespeicherter Token vorhanden · leer lassen zum Behalten",
                    "Stored token present · leave blank to keep")
                : Text("Client Access Token eingeben", "Enter client access token");
            GeniusStatusText.Text = genius.Status;
            SetGeniusManagedState(genius.ManagedByEnvironment);
            var exportSettings = EditorExportSettings.Load();
            VideoEncoderBox.SelectedIndex = exportSettings.VideoEncoder switch
            {
                EditorVideoEncoderMode.Software => 1,
                EditorVideoEncoderMode.NvidiaNvenc => 2,
                _ => 0
            };
            var nvencAvailable = await nvencTask;
            VideoEncoderStatusText.Text = nvencAvailable
                ? Text("NVENC erkannt · automatische Auswahl verwendet die NVIDIA-GPU.",
                    "NVENC detected · automatic selection uses the NVIDIA GPU.")
                : Text("NVENC nicht verfügbar · automatische Auswahl verwendet libx264.",
                    "NVENC unavailable · automatic selection uses libx264.");
            StatusText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            StatusText.Text = Text("Einstellungen konnten nicht geladen werden: ",
                "Could not load settings: ") + exception.Message;
        }
        finally { SetBusy(false); }
    }

    private async void SaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            using var libraryResponse = await _http.PutAsJsonAsync("/api/settings/library",
                new LibrarySettingsDto(LibraryPathBox.Text?.Trim() ?? string.Empty));
            await EnsureSuccessAsync(libraryResponse);

            if (!_managedByEnvironment)
            {
                var request = new UpdateUsdbProviderSettingsRequest(
                    UsdbEnabledBox.IsChecked == true,
                    UsdbBaseUrlBox.Text?.Trim() ?? string.Empty,
                    AnimuxEnabledBox.IsChecked == true,
                    AnimuxBaseUrlBox.Text?.Trim() ?? string.Empty,
                    AnimuxUsernameBox.Text?.Trim() ?? string.Empty,
                    NullIfEmpty(AnimuxPasswordBox.Text),
                    ClearAnimuxCredentialsBox.IsChecked == true);
                using var usdbResponse = await _http.PutAsJsonAsync("/api/admin/settings/usdb", request);
                await EnsureSuccessAsync(usdbResponse);
            }
            if (!_geniusManagedByEnvironment)
            {
                using var geniusResponse = await _http.PutAsJsonAsync("/api/admin/settings/genius",
                    new UpdateGeniusProviderSettingsRequest(
                        GeniusEnabledBox.IsChecked == true,
                        GeniusBaseUrlBox.Text?.Trim() ?? string.Empty,
                        NullIfEmpty(GeniusAccessTokenBox.Text),
                        ClearGeniusAccessTokenBox.IsChecked == true));
                await EnsureSuccessAsync(geniusResponse);
            }
            new EditorExportSettings(VideoEncoderBox.SelectedIndex switch
            {
                1 => EditorVideoEncoderMode.Software,
                2 => EditorVideoEncoderMode.NvidiaNvenc,
                _ => EditorVideoEncoderMode.Auto
            }).Save();
            ClearAnimuxCredentialsBox.IsChecked = false;
            ClearGeniusAccessTokenBox.IsChecked = false;
            await LoadAsync();
            StatusText.Text = Text("Einstellungen wurden auf dem Server gespeichert.",
                "Settings were saved on the server.");
        }
        catch (Exception exception)
        {
            StatusText.Text = Text("Speichern fehlgeschlagen: ", "Save failed: ") + exception.Message;
        }
        finally { SetBusy(false); }
    }

    private async void OpenQobuzClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await new QobuzPluginSettingsWindow(_serverAddress).ShowDialog(this);

    private async void TestAnimuxClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            using var response = await _http.PostAsync("/api/admin/settings/usdb/test", null);
            await EnsureSuccessAsync(response);
            UsdbStatusText.Text = Text("Animux-Anmeldung erfolgreich.", "Animux login succeeded.");
        }
        catch (Exception exception)
        {
            UsdbStatusText.Text = Text("Animux-Verbindung fehlgeschlagen: ",
                "Animux connection failed: ") + exception.Message;
        }
        finally { SetBusy(false); }
    }

    private void SetManagedState(bool managed)
    {
        _managedByEnvironment = managed;
        UsdbEnabledBox.IsEnabled = UsdbBaseUrlBox.IsEnabled = AnimuxEnabledBox.IsEnabled =
            AnimuxBaseUrlBox.IsEnabled = AnimuxUsernameBox.IsEnabled = AnimuxPasswordBox.IsEnabled =
                ClearAnimuxCredentialsBox.IsEnabled = !managed;
        if (managed)
            UsdbStatusText.Text = Text("USDB wird durch Server-Umgebungsvariablen verwaltet.",
                "USDB is managed by server environment variables.");
    }

    private void SetGeniusManagedState(bool managed)
    {
        _geniusManagedByEnvironment = managed;
        GeniusEnabledBox.IsEnabled = GeniusBaseUrlBox.IsEnabled = GeniusAccessTokenBox.IsEnabled =
            ClearGeniusAccessTokenBox.IsEnabled = !managed;
        if (managed)
            GeniusStatusText.Text = Text("Genius wird durch Server-Umgebungsvariablen verwaltet.",
                "Genius is managed by server environment variables.");
    }

    private void SetBusy(bool busy) => SaveButton.IsEnabled = !busy;
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException((await response.Content.ReadAsStringAsync()).Trim('"'));
    }
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Text(string german, string english) => EditorLocale.German ? german : english;
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => Close();
}
