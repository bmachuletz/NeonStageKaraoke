using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmRealignSongWindow : Window
{
    public ConfirmRealignSongWindow(string? title, string? artist)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        var all = string.IsNullOrWhiteSpace(title);
        Title = EditorLocale.German ? "Song neu ausrichten" : "Realign song";
        Width = 590; Height = 355; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));
        var cancel = new Button { Content = "Abbrechen" };
        var start = new Button
        {
            Content = all
                ? (EditorLocale.German ? "Gesamte Bibliothek neu alignen" : "Realign entire library")
                : (EditorLocale.German ? "Zwei Varianten erstellen" : "Create two variants"),
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        cancel.Click += (_, _) => Close(false);
        start.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = EditorLocale.German ? "GPU-ALIGNMENT NEU STARTEN?" : "START GPU ALIGNMENT?",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28")) },
                new TextBlock { Text = all ? "Alle geeigneten Songs der Bibliothek" : $"„{title}“ von {artist}", TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")) },
                new TextBlock
                {
                    Text = all
                        ? (EditorLocale.German
                            ? "Dieser GPU-Auftrag kann lange dauern und verarbeitet die Songs nacheinander. Bereits freigegebene Stage-Versionen werden nicht automatisch neu freigegeben."
                            : "This GPU job can take a long time and processes songs sequentially. Published Stage versions are never republished automatically.")
                        : (EditorLocale.German
                            ? "Der aktuelle Arbeitsstand wird zuerst gespeichert. Danach entstehen zwei getrennte Review-Versionen:\n\n1. Akustisches Re-Alignment auf Basis des letzten Editor-Stands\n2. Originale LRCLIB-Lyrics auf dem Timing einer neuen Volltranskription\n\nDie veröffentlichte Stage-Version und die Bibliotheksdateien bleiben unverändert."
                            : "The current working version is saved first. Two separate review versions are then created:\n\n1. Acoustic realignment based on the latest editor version\n2. Original LRCLIB lyrics on a new full-transcript timing scaffold\n\nThe published Stage version and library files remain unchanged."),
                    TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF3CBD"))
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, start } }
            }
        };
    }
}
