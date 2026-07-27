using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmDeleteEventWindow : Window
{
    public ConfirmDeleteEventWindow(string eventName, int wishes)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = "Session endgültig löschen"; Width = 520; Height = 275; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.Parse("#0D1016"));
        var cancel = new Button { Content = "Abbrechen" };
        var delete = new Button { Content = "Session endgültig löschen", Background = new SolidColorBrush(Color.Parse("#D94762")), Foreground = Brushes.White };
        cancel.Click += (_, _) => Close(false); delete.Click += (_, _) => Close(true);
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = "SESSION LÖSCHEN?", FontSize = 21, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#FF6D88")) },
                new TextBlock { Text = $"„{eventName}“ wird zusammen mit {wishes} offenen/gespeicherten Wunsch/Wünschen und allen Queue-Einträgen unwiderruflich gelöscht.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")) },
                new TextBlock { Text = "Eine aktive Session muss zuerst beendet werden.", Foreground = new SolidColorBrush(Color.Parse("#DFFF28")) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, delete } }
            }
        };
    }
}
