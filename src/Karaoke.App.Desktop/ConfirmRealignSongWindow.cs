using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public enum AlignmentVariantChoice { EasyAligner }

public sealed class ConfirmRealignSongWindow : Window
{
    public ConfirmRealignSongWindow(string? title, string? artist, int? songCount = null)
    {
        Title = EditorLocale.German ? "EasyAligner starten" : "Start EasyAligner";
        Width = 620;
        Height = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0D1016");
        var scope = songCount is { } count
            ? EditorLocale.German ? $"{count} ausgewählte Songs" : $"{count} selected songs"
            : string.IsNullOrWhiteSpace(title) ? EditorLocale.German ? "Gesamte Bibliothek" : "Entire library"
            : $"{title} · {artist}";
        var cancel = new Button { Content = EditorLocale.German ? "Abbrechen" : "Cancel" };
        var start = new Button
        {
            Content = EditorLocale.German ? "EasyAligner starten" : "Start EasyAligner",
            Background = Brush.Parse("#DFFF28"), Foreground = Brush.Parse("#11151C")
        };
        cancel.Click += (_, _) => Close(null);
        start.Click += (_, _) => Close(AlignmentVariantChoice.EasyAligner);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = "EASYALIGNER", Foreground = Brush.Parse("#DFFF28"),
                    FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock { Text = scope, Foreground = Brush.Parse("#C0C6D2") },
                new TextBlock
                {
                    Text = EditorLocale.German
                        ? "Verwendet die vorhandene Lyrics-Quelle und berechnet den globalen deutschen oder englischen CTC-Pfad auf dem Vocal-Stem."
                        : "Uses the existing lyrics source and computes the global German or English CTC path on the vocal stem.",
                    Foreground = Brush.Parse("#8993A6"), TextWrapping = TextWrapping.Wrap
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, start } }
            }
        };
    }
}
