using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public partial class NewSongWindow : Window
{
    public NewSongWindow()
    {
        InitializeComponent();
        Opened += (_, _) => EditorLocale.Apply(this);
    }

    private async void PickAudioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorLocale.Text("MP3 für das neue Songprojekt auswählen"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("MP3 Audio") { Patterns = ["*.mp3"] }]
        });
        if (files.Count == 0) return;
        AudioPathBox.Text = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Text = Path.GetFileNameWithoutExtension(AudioPathBox.Text);
    }

    private async void PickLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorLocale.Text("Lyrics laden"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Lyrics") { Patterns = ["*.lrc", "*.txt"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            ApplyLyricsText(await File.ReadAllTextAsync(path), path);
    }

    private async void PasteLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) ApplyLyricsText(await clipboard.TryGetTextAsync() ?? string.Empty, null);
    }

    private void CreateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AudioPathBox.Text) || !File.Exists(AudioPathBox.Text)) return;
        if (UltraStarLyricsImporter.LooksLikeUltraStar(LyricsBox.Text))
        {
            try { UltraStarLyricsImporter.Parse(LyricsBox.Text!); }
            catch (UltraStarFormatException exception)
            {
                LyricsStatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
                LyricsStatusText.Text = EditorLocale.German ? exception.Message : exception.EnglishMessage;
                return;
            }
        }
        Close(new NewSongProjectRequest(AudioPathBox.Text, TitleBox.Text?.Trim() ?? string.Empty,
            ArtistBox.Text?.Trim() ?? string.Empty, LyricsBox.Text ?? string.Empty, UseLrclibBox.IsChecked != false));
    }

    private void ApplyLyricsText(string value, string? sourcePath)
    {
        LyricsBox.Text = value;
        LyricsStatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#45E6D0"));
        if (string.IsNullOrWhiteSpace(value))
        {
            LyricsStatusText.Text = string.Empty;
            return;
        }
        if (!UltraStarLyricsImporter.LooksLikeUltraStar(value))
        {
            LyricsStatusText.Text = EditorLocale.German
                ? "Lyrics geladen · das Timing wird von der Import-Pipeline geprüft."
                : "Lyrics loaded · timing will be checked by the import pipeline.";
            return;
        }
        try
        {
            var imported = UltraStarLyricsImporter.Parse(value);
            var titleWasDerivedFromAudio = !string.IsNullOrWhiteSpace(AudioPathBox.Text) &&
                string.Equals(TitleBox.Text?.Trim(), Path.GetFileNameWithoutExtension(AudioPathBox.Text),
                    StringComparison.CurrentCultureIgnoreCase);
            if (string.IsNullOrWhiteSpace(TitleBox.Text) || titleWasDerivedFromAudio)
                TitleBox.Text = imported.Metadata.Title;
            if (string.IsNullOrWhiteSpace(ArtistBox.Text)) ArtistBox.Text = imported.Metadata.Artist;
            UseLrclibBox.IsChecked = false;
            TryResolveUltraStarAudio(imported, sourcePath);
            LyricsStatusText.Text = EditorLocale.German
                ? $"UltraStar erkannt · {imported.Lines.Count} Zeilen · {imported.WordCount} Wörter · {imported.SyllableCount} Silben"
                : $"UltraStar detected · {imported.Lines.Count} lines · {imported.WordCount} words · {imported.SyllableCount} syllables";
        }
        catch (UltraStarFormatException exception)
        {
            LyricsStatusText.Foreground = Avalonia.Media.Brushes.OrangeRed;
            LyricsStatusText.Text = EditorLocale.German ? exception.Message : exception.EnglishMessage;
        }
    }

    private void TryResolveUltraStarAudio(UltraStarLyricsImport imported, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(AudioPathBox.Text) || string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(imported.Metadata.AudioFile)) return;
        var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, imported.Metadata.AudioFile));
        if (!Path.GetExtension(candidate).Equals(".mp3", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return;
        AudioPathBox.Text = candidate;
    }
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}

public sealed record NewSongProjectRequest(string AudioPath, string Title, string Artist, string Lyrics, bool UseLrclib);
