using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmCancelAlignmentWindow : Window
{
    public ConfirmCancelAlignmentWindow()
    {
        Title = EditorLocale.German ? "Alignments abbrechen" : "Cancel alignments";
        Width = 540;
        Height = 245;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0D1016");

        var keepRunning = new Button { Content = EditorLocale.German ? "Weiterlaufen lassen" : "Keep running" };
        var cancel = new Button
        {
            Content = EditorLocale.German ? "Alle abbrechen" : "Cancel all",
            Background = Brush.Parse("#D94762"), Foreground = Brushes.White
        };
        keepRunning.Click += (_, _) => Close(false);
        cancel.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 15,
            Children =
            {
                new TextBlock
                {
                    Text = EditorLocale.German ? "ALIGNMENTS ABBRECHEN?" : "CANCEL ALIGNMENTS?",
                    FontSize = 21, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#FF6D88")
                },
                new TextBlock
                {
                    Text = EditorLocale.German
                        ? "Der aktive Aligner-Job und alle noch nicht gestarteten Songs und Varianten dieser Server-Warteschlange werden abgebrochen."
                        : "The active aligner job and every song or variant not yet started in this server queue will be cancelled.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#C0C6D2")
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { keepRunning, cancel }
                }
            }
        };
    }
}
