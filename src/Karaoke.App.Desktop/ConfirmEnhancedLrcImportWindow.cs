using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public sealed class ConfirmEnhancedLrcImportWindow : Window
{
    public ConfirmEnhancedLrcImportWindow(LyricsDto imported, SongDto song, bool replacing)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = EditorLocale.German ? "Enhanced-LRC importieren" : "Import Enhanced LRC";
        Width = 610;
        Height = 350;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var wordCount = imported.Lines.Sum(line => line.Words?.Count ?? 0);
        var first = imported.Lines.Select(line => line.Start).DefaultIfEmpty().Min();
        var last = imported.Lines.Select(line => line.End ?? line.Start).DefaultIfEmpty().Max();
        var cancel = new Button { Content = EditorLocale.German ? "Abbrechen" : "Cancel" };
        var import = new Button
        {
            Content = EditorLocale.German
                ? replacing ? "Bestehende Lyrics ersetzen" : "Lyrics importieren"
                : replacing ? "Replace existing lyrics" : "Import lyrics",
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        cancel.Click += (_, _) => Close(false);
        import.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = EditorLocale.German
                        ? replacing ? "LYRICS VOLLSTÄNDIG ERSETZEN?" : "ENHANCED-LRC IMPORTIEREN?"
                        : replacing ? "REPLACE ALL LYRICS?" : "IMPORT ENHANCED LRC?",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                new TextBlock
                {
                    Text = $"{song.Title} · {song.Artist}\n{imported.Lines.Count} " +
                           (EditorLocale.German ? "Zeilen" : "lines") + $" · {wordCount} " +
                           (EditorLocale.German ? "Wörter" : "words") +
                           $" · {first:mm\\:ss\\.fff}–{last:mm\\:ss\\.fff}",
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new TextBlock
                {
                    Text = EditorLocale.German
                        ? "Die importierten Wort-Startzeiten werden unverändert übernommen. Fehlende Wort-Endzeiten werden aus dem nächsten Wort abgeleitet; das letzte Wort bleibt innerhalb seiner Zeile. Beim Speichern entsteht eine neue Version."
                        : "Imported word start times are preserved exactly. Missing word end times are derived from the next word; the final word remains inside its line. Saving creates a new version.",
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")),
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, import }
                }
            }
        };
    }
}
