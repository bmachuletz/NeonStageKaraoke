using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public sealed class BaseLyricsWindow : Window
{
    private readonly TextBox _lyrics;

    public BaseLyricsWindow(string title, string artist, BaseLyricsSourceDto source)
    {
        Title = EditorLocale.German ? "Base-Lyrics bearbeiten" : "Edit base lyrics";
        Width = 840;
        Height = 760;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#10151F");

        _lyrics = new TextBox
        {
            Text = source.Lyrics,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("monospace"),
            FontSize = 13,
            Background = Brush.Parse("#0A0E15"),
            Foreground = Brush.Parse("#E8E1F0"),
            BorderBrush = Brush.Parse("#303848"),
            Padding = new Thickness(14),
            [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto
        };

        var cancel = new Button { Content = EditorLocale.German ? "Abbrechen" : "Cancel" };
        cancel.Click += (_, _) => Close(null);
        var save = new Button
        {
            Content = EditorLocale.German ? "Base-Lyrics speichern" : "Save base lyrics",
            Background = Brush.Parse("#DFFF28"),
            Foreground = Brush.Parse("#11151C")
        };
        save.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_lyrics.Text)) Close(_lyrics.Text);
        };

        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{title}  ·  {artist}", FontSize = 20,
                            FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#DFFF28")
                        },
                        new TextBlock
                        {
                            Text = source.Source, Margin = new Thickness(0, 4, 0, 0),
                            Foreground = Brush.Parse("#8993A6")
                        }
                    }
                },
                new Border
                {
                    [Grid.RowProperty] = 1,
                    Margin = new Thickness(0, 12, 0, 12),
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(6),
                    Background = Brush.Parse("#263044"),
                    Child = new TextBlock
                    {
                        Text = EditorLocale.German
                            ? "Zeitstempel dürfen bearbeitet werden. Strukturzeilen wie [Chorus], Refrain oder Verse 1 werden beim Alignment automatisch ignoriert. Speichern ändert nur die Basis der nächsten Neuausrichtung."
                            : "Timestamps may be edited. Structure lines such as [Chorus], Refrain, or Verse 1 are ignored automatically. Saving only changes the basis of the next realignment.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush.Parse("#B8C0D0")
                    }
                },
                new Border { [Grid.RowProperty] = 2, Child = _lyrics },
                new StackPanel
                {
                    [Grid.RowProperty] = 3,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { cancel, save }
                }
            }
        };
    }
}
