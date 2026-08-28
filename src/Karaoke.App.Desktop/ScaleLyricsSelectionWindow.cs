using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public sealed record ScaleLyricsSelectionRequest(double Factor, SelectionScaleAnchor Anchor);

public sealed class ScaleLyricsSelectionWindow : Window
{
    public ScaleLyricsSelectionWindow()
    {
        var german = EditorLocale.German;
        Title = german ? "Lyrics-Auswahl skalieren" : "Scale lyrics selection";
        Width = 570;
        Height = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var factor = new TextBox
        {
            Text = "1.000", Watermark = german ? "z. B. 0,98 oder 1,02" : "e.g. 0.98 or 1.02",
            FontFamily = new FontFamily("monospace"), FontSize = 18
        };
        var anchor = new ComboBox
        {
            ItemsSource = german
                ? new[] { "Anfang festhalten", "Mitte festhalten", "Ende festhalten" }
                : new[] { "Keep start fixed", "Keep center fixed", "Keep end fixed" },
            SelectedIndex = 0
        };
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF6D88")), TextWrapping = TextWrapping.Wrap
        };
        var cancel = new Button { Content = german ? "Abbrechen" : "Cancel" };
        var apply = new Button
        {
            Content = german ? "Auswahl skalieren" : "Scale selection",
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        cancel.Click += (_, _) => Close(null);
        apply.Click += (_, _) =>
        {
            var raw = factor.Text?.Trim().Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value is < 0.05 or > 20)
            {
                error.Text = german
                    ? "Bitte einen Faktor zwischen 0,05 und 20 eingeben. 0,98 verkleinert um 2 %, 1,02 vergrößert um 2 %."
                    : "Enter a factor between 0.05 and 20. 0.98 shrinks by 2%; 1.02 expands by 2%.";
                return;
            }
            Close((ScaleLyricsSelectionRequest?)new(value, (SelectionScaleAnchor)Math.Clamp(anchor.SelectedIndex, 0, 2)));
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 13,
            Children =
            {
                new TextBlock
                {
                    Text = german ? "AUSWAHL PROPORTIONAL SKALIEREN" : "SCALE SELECTION PROPORTIONALLY",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                new TextBlock
                {
                    Text = german
                        ? "Alle markierten Zeilen, Wörter oder Silben werden mit demselben Zeitfaktor transformiert. Abstände, Dauern und Unterelemente bleiben proportional."
                        : "All selected lines, words, or syllables are transformed by the same time factor. Relative gaps, durations, and child elements remain proportional.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new TextBlock
                {
                    Text = german ? "FAKTOR" : "FACTOR",
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")), FontSize = 11
                },
                factor,
                new TextBlock
                {
                    Text = german ? "ZEITANKER" : "TIME ANCHOR",
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")), FontSize = 11
                },
                anchor,
                error,
                new TextBlock
                {
                    Text = german
                        ? "Die Änderung ist ein einzelner Undo-Schritt. Nicht markierte Segmente werden nicht verschoben."
                        : "The change is one atomic undo step. Unselected segments are not moved.",
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
        Opened += (_, _) => factor.Focus();
    }
}
