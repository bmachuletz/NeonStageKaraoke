using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmLyricsRecognitionWindow : Window
{
    public ConfirmLyricsRecognitionWindow(string title, string artist, bool hasCurrentLyrics)
    {
        var german = EditorLocale.German;
        Title = german ? "Lyrics vollständig erkennen" : "Recognize complete lyrics";
        Width = 610;
        Height = 390;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));
        var cancel = new Button { Content = german ? "Abbrechen" : "Cancel" };
        var start = new Button
        {
            Content = german ? "GPU-Erkennung starten" : "Start GPU recognition",
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
                new TextBlock
                {
                    Text = german ? "VOLLSTÄNDIGE LYRICS AUS AUDIO ERKENNEN?" : "RECOGNIZE COMPLETE LYRICS FROM AUDIO?",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                new TextBlock
                {
                    Text = $"{title} · {artist}", FontSize = 16, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new TextBlock
                {
                    Text = german
                        ? "Der Aligner isoliert zuerst die Vocals, erkennt den vollständigen Text mit Qwen3-ASR, prüft ihn mit Stable-TS large-v3 und erzeugt mit dem Qwen Forced Aligner Wortzeiten. Danach läuft automatisch die reguläre Wort-/Silbenpipeline."
                        : "The aligner first isolates the vocals, recognizes the complete text with Qwen3-ASR, verifies it with Stable-TS large-v3, and creates word timings with Qwen Forced Aligner. The regular word/syllable pipeline then runs automatically.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#FF3CBD"))
                },
                new TextBlock
                {
                    Text = german
                        ? "Die Modelle laufen nacheinander in einem isolierten Queue-Worker. Beim Prozessende werden RAM und GPU-Speicher vollständig freigegeben. Andere Aligner-Jobs warten auf den freien Slot."
                        : "Models run sequentially in an isolated queued worker. Process exit fully releases RAM and GPU memory. Other alignment jobs wait for the free slot.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#8993A6"))
                },
                new TextBlock
                {
                    Text = hasCurrentLyrics
                        ? german
                            ? "Der aktuelle Editorstand wird vor dem Start als eigene Version gespeichert. Das erkannte Ergebnis wird anschließend als neuer Review-Stand geladen und nicht automatisch freigegeben."
                            : "The current editor state is saved as its own version before processing. The recognized result is loaded as a new review version and is never released automatically."
                        : german
                            ? "Das Ergebnis wird als neuer Review-Stand angelegt und nicht automatisch freigegeben."
                            : "The result is created as a new review version and is never released automatically.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, start }
                }
            }
        };
    }
}
