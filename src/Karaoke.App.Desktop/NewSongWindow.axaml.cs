using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

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
            Title = "MP3 für das neue Songprojekt auswählen", AllowMultiple = false,
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
            Title = "Lyrics laden", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Lyrics") { Patterns = ["*.lrc", "*.txt"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) LyricsBox.Text = await File.ReadAllTextAsync(path);
    }

    private async void PasteLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) LyricsBox.Text = await clipboard.TryGetTextAsync() ?? string.Empty;
    }

    private void CreateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AudioPathBox.Text) || !File.Exists(AudioPathBox.Text)) return;
        Close(new NewSongProjectRequest(AudioPathBox.Text, TitleBox.Text?.Trim() ?? string.Empty,
            ArtistBox.Text?.Trim() ?? string.Empty, LyricsBox.Text ?? string.Empty, UseLrclibBox.IsChecked != false));
    }
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}

public sealed record NewSongProjectRequest(string AudioPath, string Title, string Artist, string Lyrics, bool UseLrclib);
