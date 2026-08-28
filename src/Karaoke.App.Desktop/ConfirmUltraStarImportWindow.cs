using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public sealed class ConfirmUltraStarImportWindow : Window
{
    public ConfirmUltraStarImportWindow(UltraStarLyricsImport imported, SongDto song, bool replacing)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = EditorLocale.German ? "UltraStar-Lyrics importieren" : "Import UltraStar lyrics";
        Width = 610;
        var declaredDuration = imported.Metadata.DeclaredEndMilliseconds is > 0
            ? imported.Metadata.DeclaredEndMilliseconds.Value / 1000d : (double?)null;
        var durationDifference = declaredDuration is { } sourceDuration
            ? song.DurationSeconds - sourceDuration : 0;
        var recordingMismatch = declaredDuration is not null && Math.Abs(durationDifference) >= .15;
        Height = imported.Warnings.Count > 0 || recordingMismatch ? 430 : 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

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
        var warningText = imported.Warnings.Count == 0
            ? null
            : EditorLocale.German
                ? string.Join(Environment.NewLine, imported.Warnings.Take(3).Select(warning => "• " + warning))
                : $"• {imported.Warnings.Count} timing normalization warning(s); review the imported regions in the timeline.";

        var content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = EditorLocale.German
                ? replacing ? "LYRICS VOLLSTÄNDIG ERSETZEN?" : "ULTRASTAR-LYRICS IMPORTIEREN?"
                : replacing ? "REPLACE ALL LYRICS?" : "IMPORT ULTRASTAR LYRICS?",
            FontSize = 21, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
        });
        content.Children.Add(new TextBlock
        {
            Text = EditorLocale.German
                ? $"{song.Title} · {song.Artist}\n{imported.Lines.Count} Zeilen · {imported.WordCount} Wörter · {imported.SyllableCount} Silben · BPM {imported.Metadata.Bpm:0.###} · GAP {imported.Metadata.GapMilliseconds:0.###} ms"
                : $"{song.Title} · {song.Artist}\n{imported.Lines.Count} lines · {imported.WordCount} words · {imported.SyllableCount} syllables · BPM {imported.Metadata.Bpm:0.###} · GAP {imported.Metadata.GapMilliseconds:0.###} ms",
            Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")), TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = EditorLocale.German
                ? replacing
                    ? "Der aktuelle Editorinhalt wird durch den Import ersetzt. Audio, Cover und gespeicherte Lyrics-Versionen bleiben erhalten. Rückgängig ist bis zum Schließen möglich; beim Speichern entsteht eine neue Version."
                    : "Die Lyrics werden in das leere Projekt eingesetzt. Audio und Cover bleiben unverändert; beim Speichern entsteht eine neue Lyrics-Version."
                : replacing
                    ? "The current editor content will be replaced by the import. Audio, cover art, and saved lyrics versions remain intact. Undo is available until the editor closes; saving creates a new version."
                    : "The lyrics will be added to the empty project. Audio and cover art remain unchanged; saving creates a new lyrics version.",
            Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")), TextWrapping = TextWrapping.Wrap
        });
        if (warningText is not null)
            content.Children.Add(new TextBlock { Text = warningText, Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap });
        if (recordingMismatch)
            content.Children.Add(new TextBlock
            {
                Text = EditorLocale.German
                    ? $"⚠ Die UltraStar-Datei nennt eine Aufnahme von {declaredDuration:0.000} s, der gewählte Song hat {song.DurationSeconds:0.000} s ({durationDifference:+0.000;-0.000} s). Das ist keine Rundungsdrift des Imports: Wahrscheinlich sind es verschiedene Aufnahmen. Dann wird die Abweichung zum Songende zunehmend sichtbar."
                    : $"⚠ The UltraStar file declares a {declaredDuration:0.000} s recording, while the selected song is {song.DurationSeconds:0.000} s ({durationDifference:+0.000;-0.000} s). This is not importer rounding drift: the recordings probably differ, so the offset becomes increasingly visible near the end.",
                Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap
            });
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { cancel, import }
        });
        Content = content;
    }
}
