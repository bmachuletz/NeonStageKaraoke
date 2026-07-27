using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmDeleteSongWindow : Window
{
    public ConfirmDeleteSongWindow(string title, string artist)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = "Song endgültig löschen"; Width = 540; Height = 290; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));
        var cancel = new Button { Content = "Abbrechen" };
        var delete = new Button
        {
            Content = "Song und Dateien endgültig löschen",
            Background = new SolidColorBrush(Color.Parse("#D94762")), Foreground = Brushes.White
        };
        cancel.Click += (_, _) => Close(false);
        delete.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = "SONG RÜCKSTANDSLOS LÖSCHEN?", FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#FF6D88")) },
                new TextBlock { Text = $"„{title}“ von {artist} wird aus dem Index entfernt.", TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")) },
                new TextBlock { Text = "Audio, LRC, Instrumental, Vocals, Alignment und weitere Sidecars werden unwiderruflich gelöscht.",
                    TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#DFFF28")) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, delete } }
            }
        };
    }
}
