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
    private bool _onlineManagedByEnvironment;
    private IReadOnlyList<EditorAudioOutputDevice> _audioOutputDevices = [];

    public SettingsWindow() : this(EditorConnectionSettings.ResolveServerAddress()) { }

    public SettingsWindow(Uri serverAddress)
    {
        InitializeComponent();
        _serverAddress = serverAddress;
        _http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromSeconds(30) };
        ServerUrlBox.Text = serverAddress.AbsoluteUri.TrimEnd('/');
        AudioTab.IsVisible = OperatingSystem.IsWindows();
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
        RefreshAudioDevices();
        try
        {
            var libraryTask = _http.GetFromJsonAsync<LibrarySettingsDto>("/api/settings/library");
            var usdbTask = _http.GetFromJsonAsync<UsdbProviderSettingsDto>("/api/admin/settings/usdb");
            var geniusTask = _http.GetFromJsonAsync<GeniusProviderSettingsDto>("/api/admin/settings/genius");
            var onlineTask = _http.GetFromJsonAsync<OnlineServerSettingsDto>("/api/admin/settings/online");
            var nvencTask = EditorExportSettings.DetectNvencAsync();
            await Task.WhenAll(libraryTask, usdbTask, geniusTask, onlineTask);
            var library = await libraryTask ?? throw new InvalidDataException("Missing library settings.");
            var usdb = await usdbTask ?? throw new InvalidDataException("Missing USDB settings.");
            var genius = await geniusTask ?? throw new InvalidDataException("Missing Genius settings.");
            var online = await onlineTask ?? throw new InvalidDataException("Missing online settings.");
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
            OnlineEnabledBox.IsChecked = online.Enabled;
            LiveKitServerUrlBox.Text = online.ServerUrl;
            LiveKitApiKeyBox.Text = online.ApiKey;
            LiveKitApiSecretBox.Text = string.Empty;
            LiveKitApiSecretBox.Watermark = online.HasApiSecret
                ? Text("Gespeichertes Secret vorhanden · leer lassen zum Behalten",
                    "Stored secret present · leave blank to keep")
                : Text("LiveKit API-Secret eingeben", "Enter LiveKit API secret");
            LiveKitRoomPrefixBox.Text = online.RoomPrefix;
            LiveKitStatusText.Text = online.Status;
            LiveKitStatusText.Foreground = Avalonia.Media.Brushes.MediumTurquoise;
            SetOnlineManagedState(online.ManagedByEnvironment);
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
        var audioSaved = false;
        var connectionSaved = false;
        var connectionChanged = false;
        try
        {
            if (!Uri.TryCreate(ServerUrlBox.Text?.Trim(), UriKind.Absolute, out var editorServer) ||
                editorServer.Scheme is not ("http" or "https"))
                throw new ArgumentException(Text(
                    "Die Serveradresse muss eine vollständige HTTP- oder HTTPS-Adresse sein.",
                    "The server address must be a complete HTTP or HTTPS URL."));
            var normalizedEditorServer = editorServer.AbsoluteUri.TrimEnd('/');
            connectionChanged = !string.Equals(normalizedEditorServer,
                _serverAddress.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            new EditorConnectionSettings(normalizedEditorServer).Save();
            connectionSaved = true;

            if (OperatingSystem.IsWindows() && AudioOutputDeviceBox.SelectedItem is EditorAudioOutputDevice device)
            {
                new EditorAudioSettings(device.Id, device.Label).Save();
                audioSaved = true;
            }
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
            if (!_onlineManagedByEnvironment)
            {
                using var onlineResponse = await _http.PutAsJsonAsync("/api/admin/settings/online",
                    CreateOnlineRequest());
                await EnsureSuccessAsync(onlineResponse);
            }
            new EditorExportSettings(VideoEncoderBox.SelectedIndex switch
            {
                1 => EditorVideoEncoderMode.Software,
                2 => EditorVideoEncoderMode.NvidiaNvenc,
                _ => EditorVideoEncoderMode.Auto
            }).Save();
            ClearAnimuxCredentialsBox.IsChecked = false;
            ClearGeniusAccessTokenBox.IsChecked = false;
            ClearLiveKitApiSecretBox.IsChecked = false;
            await LoadAsync();
            StatusText.Text = connectionChanged
                ? Text("Einstellungen gespeichert. Die neue Serveradresse gilt nach einem Editor-Neustart.",
                    "Settings saved. The new server address applies after restarting the editor.")
                : Text("Einstellungen gespeichert. Eine geänderte Audioausgabe gilt nach einem Editor-Neustart.",
                    "Settings saved. A changed audio output takes effect after restarting the editor.");
        }
        catch (Exception exception)
        {
            StatusText.Text = (connectionSaved
                ? Text("Serveradresse wurde lokal gespeichert und gilt nach einem Neustart; weitere Einstellungen fehlgeschlagen: ",
                    "Server address was saved locally and applies after restart; other settings failed: ")
                : audioSaved
                    ? Text("Audioausgabe wurde lokal gespeichert; Server-Einstellungen fehlgeschlagen: ",
                        "Audio output was saved locally; server settings failed: ")
                : Text("Speichern fehlgeschlagen: ", "Save failed: ")) + exception.Message;
        }
        finally { SetBusy(false); }
    }

    private async void OpenQobuzClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await new QobuzPluginSettingsWindow(_serverAddress).ShowDialog(this);

    private void RefreshAudioDevicesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        RefreshAudioDevices();

    private void RefreshAudioDevices()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var selected = EditorAudioSettings.Load();
            _audioOutputDevices = LibVlcAudioPlaybackService.GetWindowsAudioOutputDevices();
            AudioOutputDeviceBox.ItemsSource = _audioOutputDevices;
            AudioOutputDeviceBox.SelectedItem = _audioOutputDevices.FirstOrDefault(device =>
                device.Id == selected.OutputDeviceId) ?? _audioOutputDevices[0];
            AudioDeviceStatusText.Text = Text(
                $"{_audioOutputDevices.Count - 1} Windows-Audiogerät(e) erkannt.",
                $"Detected {_audioOutputDevices.Count - 1} Windows audio device(s).");
        }
        catch (Exception exception)
        {
            _audioOutputDevices = [new(null, Text("Windows-Standardgerät", "Windows default device"))];
            AudioOutputDeviceBox.ItemsSource = _audioOutputDevices;
            AudioOutputDeviceBox.SelectedIndex = 0;
            AudioDeviceStatusText.Text = Text("Audiogeräte konnten nicht gelesen werden: ",
                "Could not enumerate audio devices: ") + exception.Message;
        }
    }

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

    private async void TestLiveKitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        SetBusy(true);
        LiveKitStatusText.Text = Text("LiveKit-Verbindung wird geprüft …",
            "Testing LiveKit connection …");
        LiveKitStatusText.Foreground = Avalonia.Media.Brushes.MediumTurquoise;
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/admin/settings/online/test",
                CreateOnlineRequest());
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<OnlineServerTestResultDto>()
                ?? throw new InvalidDataException("Missing LiveKit test result.");
            LiveKitStatusText.Text = result.Success
                ? $"{result.Message} ({result.ElapsedMilliseconds} ms)"
                : result.Message;
            LiveKitStatusText.Foreground = result.Success
                ? Avalonia.Media.Brushes.MediumTurquoise
                : Avalonia.Media.Brushes.Orange;
        }
        catch (Exception exception)
        {
            LiveKitStatusText.Text = Text("LiveKit-Test fehlgeschlagen: ",
                "LiveKit test failed: ") + exception.Message;
            LiveKitStatusText.Foreground = Avalonia.Media.Brushes.Orange;
        }
        finally { SetBusy(false); }
    }

    private UpdateOnlineServerSettingsRequest CreateOnlineRequest() => new(
        OnlineEnabledBox.IsChecked == true,
        LiveKitServerUrlBox.Text?.Trim() ?? string.Empty,
        LiveKitApiKeyBox.Text?.Trim() ?? string.Empty,
        NullIfEmpty(LiveKitApiSecretBox.Text),
        LiveKitRoomPrefixBox.Text?.Trim() ?? string.Empty,
        ClearLiveKitApiSecretBox.IsChecked == true);

    private void SetManagedState(bool managed)
    {
        _managedByEnvironment = false;
        UsdbEnabledBox.IsEnabled = UsdbBaseUrlBox.IsEnabled = AnimuxEnabledBox.IsEnabled =
            AnimuxBaseUrlBox.IsEnabled = AnimuxUsernameBox.IsEnabled = AnimuxPasswordBox.IsEnabled =
                ClearAnimuxCredentialsBox.IsEnabled = true;
    }

    private void SetGeniusManagedState(bool managed)
    {
        _geniusManagedByEnvironment = false;
        GeniusEnabledBox.IsEnabled = GeniusBaseUrlBox.IsEnabled = GeniusAccessTokenBox.IsEnabled =
            ClearGeniusAccessTokenBox.IsEnabled = true;
    }

    private void SetOnlineManagedState(bool managed)
    {
        _onlineManagedByEnvironment = false;
        OnlineEnabledBox.IsEnabled = LiveKitServerUrlBox.IsEnabled = LiveKitApiKeyBox.IsEnabled =
            LiveKitApiSecretBox.IsEnabled = LiveKitRoomPrefixBox.IsEnabled =
                ClearLiveKitApiSecretBox.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        TestLiveKitButton.IsEnabled = !busy;
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException((await response.Content.ReadAsStringAsync()).Trim('"'));
    }
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Text(string german, string english) => EditorLocale.German ? german : english;
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => Close();
}
