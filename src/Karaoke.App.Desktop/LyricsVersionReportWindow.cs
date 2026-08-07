using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public sealed class LyricsVersionReportWindow : Window
{
    private readonly TextBox _content;

    public LyricsVersionReportWindow(LyricsVersionReportDto report)
    {
        Title = report.Title;
        Width = 860;
        Height = 760;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#10151F");

        _content = new TextBox
        {
            Text = report.Content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = FontFamily.Parse("monospace"),
            FontSize = 13,
            Background = Brush.Parse("#0A0E15"),
            Foreground = Brush.Parse("#E8E1F0"),
            BorderBrush = Brush.Parse("#303848"),
            Padding = new Thickness(16),
            [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var copy = new Button { Content = "Bericht kopieren" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(report.Content);
        };
        var close = new Button
        {
            Content = "Schließen",
            Background = Brush.Parse("#DFFF28"),
            Foreground = Brush.Parse("#11151C")
        };
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = report.Title,
                    FontSize = 21,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush.Parse("#DFFF28")
                },
                new Border
                {
                    [Grid.RowProperty] = 1,
                    Margin = new Thickness(0, 8, 0, 14),
                    Padding = new Thickness(10, 7),
                    CornerRadius = new CornerRadius(6),
                    Background = Brush.Parse(report.HasTechnicalAlignmentReport ? "#193A3540" : "#193A2A40"),
                    Child = new TextBlock { Text = report.Outcome, Foreground = Brush.Parse("#45E6D0") }
                },
                new Border
                {
                    [Grid.RowProperty] = 2,
                    Child = _content
                },
                new StackPanel
                {
                    [Grid.RowProperty] = 3,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { copy, close }
                }
            }
        };
        EditorLocale.Apply(this);
    }
}
