using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public enum AlignmentVariantChoice { Phoneme, FullTranscript, Both }

public sealed class ConfirmRealignSongWindow : Window
{
    public ConfirmRealignSongWindow(string? title, string? artist)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        var all = string.IsNullOrWhiteSpace(title);
        Title = EditorLocale.German ? "Alignment auswählen" : "Choose alignment";
        Width = 720; Height = 570; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var phoneme = Variant(
            EditorLocale.German ? "Variante 1.2 · IPA-Mikro-Alignment" : "Variant 1.2 · IPA micro alignment",
            EditorLocale.German
                ? (all
                    ? "Richtet den jeweils letzten Editor-Stand mit IPA, phonemabhängiger 2,5-ms-Audioanalyse, lokalem Dauerpfad und Pitch-/Voicing-Ausklängen neu aus. Keine Volltranskription."
                    : "Richtet den letzten Editor-Stand mit IPA, phonemabhängiger 2,5-ms-Audioanalyse, lokalem Dauerpfad und Pitch-/Voicing-Ausklängen neu aus. Keine Volltranskription.")
                : "Realigns the latest editor version using IPA, class-aware 2.5 ms audio analysis, a local duration path, and pitch/voicing releases. No full transcript.",
            selected: true);
        var transcript = Variant(
            EditorLocale.German ? "Variante 2 · LRCLIB + Volltranskript" : "Variant 2 · LRCLIB + full transcript",
            EditorLocale.German
                ? "Überträgt den originalen LRCLIB-Text auf das Timing einer neuen vollständigen Audio-Transkription. Sinnvoll bei fehlenden Wiederholungen, falscher Zeilenstruktur oder stark verschobenem Ausgangstext."
                : "Transfers the original LRCLIB text onto a newly generated full-audio transcript. Useful for missing repetitions, incorrect line structure, or heavily shifted source lyrics.");
        var both = Variant(
            EditorLocale.German ? "Beide Varianten" : "Both variants",
            EditorLocale.German
                ? "Führt Variante 1 und anschließend Variante 2 aus. Beide Ergebnisse werden getrennt als Review-Versionen gespeichert und können im Editor verglichen werden."
                : "Runs variant 1 followed by variant 2. Both results are stored as separate review versions for comparison in the editor.");

        var cancel = new Button { Content = EditorLocale.German ? "Abbrechen" : "Cancel" };
        var start = new Button
        {
            Content = EditorLocale.German ? "Auswahl starten" : "Start selection",
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        cancel.Click += (_, _) => Close(null);
        start.Click += (_, _) => Close(
            both.IsChecked == true ? AlignmentVariantChoice.Both :
            transcript.IsChecked == true ? AlignmentVariantChoice.FullTranscript :
            AlignmentVariantChoice.Phoneme);

        Content = new Grid
        {
            Margin = new Avalonia.Thickness(24), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = EditorLocale.German ? "GPU-ALIGNMENT AUSWÄHLEN" : "CHOOSE GPU ALIGNMENT",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                At(new TextBlock
                {
                    Text = all
                        ? (EditorLocale.German ? "Alle geeigneten Songs der Bibliothek werden nacheinander verarbeitet."
                                                : "All eligible library songs are processed sequentially.")
                        : $"„{title}“ · {artist}",
                    Margin = new Avalonia.Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                }, 1),
                At(new StackPanel { Spacing = 10, Children = { phoneme, transcript, both } }, 2),
                At(new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new Avalonia.Thickness(0, 16, 0, 0),
                    Children = { cancel, start }
                }, 3)
            }
        };
    }

    private static RadioButton Variant(string title, string description, bool selected = false) => new()
    {
        GroupName = "alignment-variant", IsChecked = selected,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Padding = new Avalonia.Thickness(14, 11),
        Background = new SolidColorBrush(Color.Parse("#171D28")),
        Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")) },
                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#AAB2C1")) }
            }
        }
    };

    private static T At<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
