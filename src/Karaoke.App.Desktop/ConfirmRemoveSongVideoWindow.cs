using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmRemoveSongVideoWindow : Window
{
    public ConfirmRemoveSongVideoWindow(string title, string artist)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = "Video entfernen";
        Width = 520;
        Height = 255;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var cancel = new Button { Content = "Abbrechen" };
        var remove = new Button
        {
            Content = "Video endgültig entfernen",
            Background = new SolidColorBrush(Color.Parse("#D94762")),
            Foreground = Brushes.White
        };
        cancel.Click += (_, _) => Close(false);
        remove.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "VIDEO VOM SONG ENTFERNEN?",
                    FontSize = 21,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#FF6D88"))
                },
                new TextBlock
                {
                    Text = $"„{title}“ von {artist}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new TextBlock
                {
                    Text = "Das lokal gespeicherte Stage-Video wird gelöscht und nicht mehr mit Songpaketen exportiert.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, remove }
                }
            }
        };
    }
}
