using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class GlobalLyricsOffsetWindow : Window
{
    public GlobalLyricsOffsetWindow()
    {
        var german = EditorLocale.German;
        Title = german ? "Globaler Lyrics-Versatz" : "Global lyrics shift";
        Width = 560;
        Height = 340;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var input = new TextBox
        {
            Text = "0", Watermark = german ? "z. B. -200 oder 150" : "e.g. -200 or 150",
            FontFamily = new FontFamily("monospace"), FontSize = 18
        };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF6D88")), TextWrapping = TextWrapping.Wrap
        };
        var cancel = new Button { Content = german ? "Abbrechen" : "Cancel" };
        var apply = new Button
        {
            Content = german ? "Alle Timings verschieben" : "Shift all timings",
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        cancel.Click += (_, _) => Close(null);
        apply.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var milliseconds) || milliseconds is < -120000 or > 120000)
            {
                error.Text = german
                    ? "Bitte einen ganzzahligen Wert zwischen -120000 und +120000 Millisekunden eingeben."
                    : "Enter an integer between -120000 and +120000 milliseconds.";
                return;
            }
            Close((int?)milliseconds);
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 13,
            Children =
            {
                new TextBlock
                {
                    Text = german ? "ALLE LYRICS ZEITLICH VERSCHIEBEN" : "SHIFT ALL LYRICS IN TIME",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                new TextBlock
                {
                    Text = german
                        ? "Positiv = später, negativ = früher. Zeilen, Wörter und Silben werden gemeinsam um exakt denselben Betrag verschoben; es wird kein LRC-Offset-Tag gesetzt."
                        : "Positive = later, negative = earlier. Lines, words, and syllables move together by exactly the same amount; no LRC offset tag is written.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new TextBlock
                {
                    Text = german ? "VERSATZ IN MILLISEKUNDEN" : "SHIFT IN MILLISECONDS",
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")), FontSize = 11
                },
                input,
                error,
                new TextBlock
                {
                    Text = german
                        ? "Die Änderung wird als ein einzelner Undo-Schritt behandelt. Timings vor 00:00 oder hinter dem Songende werden abgewiesen."
                        : "The change is one atomic undo step. Timings before 00:00 or beyond the song end are rejected.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#8993A6")), FontSize = 11
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, apply }
                }
            }
        };
        Opened += (_, _) => input.Focus();
    }
}
