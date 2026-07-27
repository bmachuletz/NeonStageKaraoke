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
        Title = "Song neu ausrichten"; Width = 560; Height = 300; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));
        var cancel = new Button { Content = "Abbrechen" };
        var start = new Button
        {
            Content = all ? "Gesamte Bibliothek neu alignen" : "Diesen Song neu alignen",
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
                new TextBlock { Text = "GPU-ALIGNMENT NEU STARTEN?", FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28")) },
                new TextBlock { Text = all ? "Alle geeigneten Songs der Bibliothek" : $"„{title}“ von {artist}", TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")) },
                new TextBlock
                {
                    Text = all
                        ? "Dieser GPU-Auftrag kann lange dauern und verarbeitet die Songs nacheinander. Bereits freigegebene Stage-Versionen werden nicht automatisch neu freigegeben."
                        : "Nur dieser Song wird neu verarbeitet. Der aktuelle Editor- und Stage-Stand bleibt erhalten. Nach Abschluss wird das neue Ergebnis als ungespeicherter Review-Stand in den Editor geladen.",
                    TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF3CBD"))
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, start } }
            }
        };
    }
}
