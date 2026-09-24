using System.Net.Http.Json;
using Avalonia.Controls;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class QobuzPluginSettingsWindow : Window
{
    private readonly HttpClient _http;
    private bool _managedByEnvironment;
    private readonly IReadOnlyList<QualityItem> _qualities =
    [
        new(QobuzDownloadQuality.FlacCd, EditorLocale.German
            ? "FLAC · CD 16 Bit / 44,1 kHz (empfohlen)" : "FLAC · CD 16 bit / 44.1 kHz (recommended)"),
        new(QobuzDownloadQuality.FlacHiRes96, "FLAC · Hi-Res bis 24 Bit / 96 kHz"),
        new(QobuzDownloadQuality.FlacHiRes192, "FLAC · Hi-Res bis 24 Bit / 192 kHz"),
        new(QobuzDownloadQuality.Mp3_320, "MP3 · 320 kbit/s")
    ];

    public QobuzPluginSettingsWindow() : this(EditorConnectionSettings.ResolveServerAddress()) { }

    public QobuzPluginSettingsWindow(Uri serverAddress)
    {
        InitializeComponent();
        _http = new HttpClient { BaseAddress = serverAddress, Timeout = TimeSpan.FromSeconds(30) };
        QualityBox.ItemsSource = _qualities;
        QualityBox.SelectedIndex = 0;
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
            var settings = await _http.GetFromJsonAsync<QobuzPluginSettingsDto>(
                "/api/admin/download-providers/qobuz");
            if (settings is null) throw new InvalidOperationException("Der Server hat keine Qobuz-Konfiguration geliefert.");
            EnabledBox.IsChecked = settings.Enabled;
            AppIdBox.Text = settings.AppId;
            ApiBaseUrlBox.Text = settings.ApiBaseUrl;
            QualityBox.SelectedItem = _qualities.First(item => item.Value == settings.Quality);
            AppSecretBox.Watermark = settings.HasAppSecret
                ? Text("Gespeichertes Secret vorhanden · leer lassen zum Behalten", "Stored secret present · leave blank to keep")
                : Text("App-Secret eingeben", "Enter app secret");
            UserTokenBox.Watermark = settings.HasUserAuthToken
                ? Text("Gespeicherter Token vorhanden · leer lassen zum Behalten", "Stored token present · leave blank to keep")
                : Text("Autorisierten User-Auth-Token eingeben", "Enter authorized user auth token");
            StatusText.Text = Text(settings.Status,
                settings.Enabled && settings.Configured
                    ? "Qobuz is active. Authorized purchase downloads replace YouTube."
                    : settings.Configured
                        ? "Qobuz is configured but disabled. YouTube remains active."
                        : "Qobuz is not fully configured. YouTube remains active.");
            SetManagedState(settings.ManagedByEnvironment);
        }
        catch (Exception exception)
        {
            StatusText.Text = Text("Konfiguration konnte nicht geladen werden: ", "Could not load configuration: ") + exception.Message;
        }
        finally { SetBusy(false); }
    }

    private async void SaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (QualityBox.SelectedItem is not QualityItem quality) return;
        SetBusy(true);
        try
        {
            var request = new UpdateQobuzPluginSettingsRequest(
                EnabledBox.IsChecked == true,
                AppIdBox.Text?.Trim() ?? string.Empty,
                NullIfEmpty(AppSecretBox.Text),
                NullIfEmpty(UserTokenBox.Text),
                quality.Value,
                NullIfEmpty(ApiBaseUrlBox.Text),
                ClearCredentialsBox.IsChecked == true);
            using var response = await _http.PutAsJsonAsync("/api/admin/download-providers/qobuz", request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException((await response.Content.ReadAsStringAsync()).Trim('"'));
            AppSecretBox.Text = string.Empty;
            UserTokenBox.Text = string.Empty;
            ClearCredentialsBox.IsChecked = false;
            await LoadAsync();
            StatusText.Text = Text("Qobuz-Konfiguration wurde sicher auf dem Server gespeichert.",
                "Qobuz configuration was stored securely on the server.");
        }
        catch (Exception exception)
        {
            StatusText.Text = Text("Speichern fehlgeschlagen: ", "Save failed: ") + exception.Message;
        }
        finally { SetBusy(false); }
    }

    private void SetManagedState(bool managed)
    {
        _managedByEnvironment = false;
        EnabledBox.IsEnabled = AppIdBox.IsEnabled = AppSecretBox.IsEnabled = UserTokenBox.IsEnabled =
            QualityBox.IsEnabled = ApiBaseUrlBox.IsEnabled = ClearCredentialsBox.IsEnabled = SaveButton.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy && !_managedByEnvironment;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Text(string german, string english) => EditorLocale.German ? german : english;
    private void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => Close();

    private sealed record QualityItem(QobuzDownloadQuality Value, string Label)
    {
        public override string ToString() => Label;
    }
}
