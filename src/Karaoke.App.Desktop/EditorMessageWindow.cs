using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class EditorMessageWindow : Window
{
    public EditorMessageWindow(string title, string message)
    {
        Title = title;
        Width = 560;
        Height = 240;
        MinWidth = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#10151F");

        var close = new Button
        {
            Content = EditorLocale.German ? "Schließen" : "Close",
            Background = Brush.Parse("#DFFF28"),
            Foreground = Brush.Parse("#11151C"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = title, FontSize = 20, FontWeight = FontWeight.Bold,
                    Foreground = Brush.Parse("#FF6D88")
                },
                new TextBlock
                {
                    [Grid.RowProperty] = 1,
                    Text = message, Margin = new Thickness(0, 14),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush.Parse("#C0C6D2")
                },
                new Border { [Grid.RowProperty] = 2, Child = close }
            }
        };
    }
}
