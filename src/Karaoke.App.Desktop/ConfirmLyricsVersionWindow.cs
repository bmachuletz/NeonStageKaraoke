using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmLyricsVersionWindow : Window
{
    public ConfirmLyricsVersionWindow(EditorLyricsVersionItem item, bool delete)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = delete ? "Lyrics-Version löschen" : "Lyrics-Version laden";
        Width = 520;
        Height = delete ? 260 : 280;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var cancel = new Button { Content = "Abbrechen" };
        var confirm = new Button
        {
            Content = delete ? "Version endgültig löschen" : "Als Arbeitsstand laden",
            Background = new SolidColorBrush(Color.Parse(delete ? "#D94762" : "#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse(delete ? "#FFFFFF" : "#11151C"))
        };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);

        var explanation = delete
            ? "Nur dieser gespeicherte Lyrics-Stand wird gelöscht. Audio, Song und andere Versionen bleiben erhalten."
            : "Der aktuelle Editorinhalt wird durch diesen Stand ersetzt. Die gewählte Version bleibt unverändert; mit „Speichern“ entsteht anschließend eine neue Revision.";
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = delete ? "LYRICS-STAND LÖSCHEN?" : "LYRICS-STAND LADEN?",
                    FontSize = 21,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(delete ? "#FF6D88" : "#DFFF28"))
                },
                new TextBlock
                {
                    Text = $"Revision {item.Revision} · {item.Timestamp} · {item.StatusLabel}",
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")),
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = explanation,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")),
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };
    }
}
