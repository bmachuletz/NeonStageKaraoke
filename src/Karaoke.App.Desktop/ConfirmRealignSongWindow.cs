using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public enum AlignmentVariantChoice { Phoneme, EditorGuided, FullTranscript, ResearchShadow, BasicPitchAb, All }

public sealed class ConfirmRealignSongWindow : Window
{
    public ConfirmRealignSongWindow(string? title, string? artist, int? songCount = null)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        var all = string.IsNullOrWhiteSpace(title);
        var multiple = songCount is > 1;
        all &= !multiple;
        var collection = all || multiple;
        Title = EditorLocale.German ? "Alignment auswählen" : "Choose alignment";
        Width = 720; Height = 890; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var phoneme = Variant(
            EditorLocale.German ? "Variante 1.2 · IPA-Mikro-Alignment" : "Variant 1.2 · IPA micro alignment",
            EditorLocale.German
                ? (collection
                    ? "Richtet den jeweils letzten Editor-Stand mit IPA, phonemabhängiger 2,5-ms-Audioanalyse, lokalem Dauerpfad und Pitch-/Voicing-Ausklängen neu aus. Keine Volltranskription."
                    : "Richtet den letzten Editor-Stand mit IPA, phonemabhängiger 2,5-ms-Audioanalyse, lokalem Dauerpfad und Pitch-/Voicing-Ausklängen neu aus. Keine Volltranskription.")
                : "Realigns the latest editor version using IPA, class-aware 2.5 ms audio analysis, a local duration path, and pitch/voicing releases. No full transcript.",
            selected: true);
        var transcript = Variant(
            EditorLocale.German ? "Variante 2 · LRCLIB + Volltranskript" : "Variant 2 · LRCLIB + full transcript",
            EditorLocale.German
                ? "Überträgt den originalen LRCLIB-Text auf das Timing einer neuen vollständigen Audio-Transkription. Sinnvoll bei fehlenden Wiederholungen, falscher Zeilenstruktur oder stark verschobenem Ausgangstext."
                : "Transfers the original LRCLIB text onto a newly generated full-audio transcript. Useful for missing repetitions, incorrect line structure, or heavily shifted source lyrics.");
        var guided = Variant(
            EditorLocale.German
                ? "Referenzgestütztes Realignment · manuelle Anker"
                : "Reference-guided realignment · manual anchors",
            EditorLocale.German
                ? "Bewahrt manuell korrigierte Bereiche unverändert und lernt aus deren Abweichung zur automatischen Analyse einen lokalen Timing-Verlauf. Nur akustisch bessere Korrekturen an unbearbeiteten Nachbarbereichen werden übernommen."
                : "Keeps manually corrected regions immutable and derives a local timing calibration from their residuals against the automatic analysis. Only acoustically better corrections to untouched neighbouring regions are selected.");
        var shadow = Variant(
            EditorLocale.German
                ? "Forschungs-Schattenlauf · vollständig neu"
                : "Research shadow run · clean-room",
            EditorLocale.German
                ? "Beginnt immer bei den aktuell gespeicherten Base-Lyrics (pre-align/LRCLIB). Gespeicherte Editor-, Wort-, Silben- und Haltezeitkorrekturen werden ausdrücklich nicht übernommen. Das Ergebnis wird als eigene, im Editor ladbare Review-Version gespeichert."
                : "Always starts from the currently saved base lyrics (pre-align/LRCLIB). Saved editor, word, syllable, and hold-time edits are explicitly ignored. The result is stored as a separate review version that can be loaded in the editor.");
        var basicPitch = Variant(
            EditorLocale.German
                ? "A/B-Prototyp · Spotify Basic Pitch"
                : "A/B prototype · Spotify Basic Pitch",
            EditorLocale.German
                ? (collection
                    ? "Erzeugt für maximal fünf Songs ohne UltraStar-Herkunft zwei getrennte Review-Versionen: A bleibt unverändert, B ergänzt konservative Basic-Pitch-Phrasenonsets aus einem separaten Container."
                    : "Erzeugt zwei getrennte Review-Versionen dieses Songs: A bleibt unverändert, B ergänzt konservative Basic-Pitch-Phrasenonsets. Songs mit UltraStar-Herkunft werden abgewiesen.")
                : (all
                    ? "Creates two separate review versions for at most five non-UltraStar songs: unchanged A and B with conservative Basic Pitch phrase-onset evidence from an isolated container."
                    : "Creates two separate review versions: unchanged A and B with conservative Basic Pitch phrase-onset evidence. UltraStar-derived songs are rejected."));
        var both = Variant(
            EditorLocale.German ? "Alle Varianten" : "All variants",
            EditorLocale.German
                ? "Führt Variante 1.2, das referenzgestützte Realignment, Variante 2 und den Forschungs-Schattenlauf nacheinander aus. Alle Ergebnisse werden getrennt gespeichert."
                : "Runs Variant 1.2, reference-guided realignment, Variant 2, and the research shadow run sequentially. Every result is stored separately.");

        var cancel = new Button { Content = EditorLocale.German ? "Abbrechen" : "Cancel" };
        var start = new Button
        {
            Content = EditorLocale.German ? "Auswahl starten" : "Start selection",
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        cancel.Click += (_, _) => Close(null);
        start.Click += (_, _) => Close(
            both.IsChecked == true ? AlignmentVariantChoice.All :
            basicPitch.IsChecked == true ? AlignmentVariantChoice.BasicPitchAb :
            shadow.IsChecked == true ? AlignmentVariantChoice.ResearchShadow :
            transcript.IsChecked == true ? AlignmentVariantChoice.FullTranscript :
            guided.IsChecked == true ? AlignmentVariantChoice.EditorGuided :
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
                        : multiple
                        ? (EditorLocale.German
                            ? $"{songCount} ausgewählte Songs werden nacheinander verarbeitet."
                            : $"{songCount} selected songs are processed sequentially.")
                        : $"„{title}“ · {artist}",
                    Margin = new Avalonia.Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                }, 1),
                At(new StackPanel { Spacing = 10,
                    Children = { phoneme, guided, transcript, shadow, basicPitch, both } }, 2),
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
