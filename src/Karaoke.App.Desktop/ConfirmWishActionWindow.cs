using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmWishActionWindow : Window
{
    public ConfirmWishActionWindow(EditorWishItem item, bool adopt)
    {
        var german = EditorLocale.German;
        Title = german
            ? adopt ? "Audiofund übernehmen" : "Wunsch entfernen"
            : adopt ? "Adopt audio candidate" : "Remove request";
        Width = 570;
        Height = adopt ? 330 : 280;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var cancel = new Button { Content = german ? "Abbrechen" : "Cancel" };
        var confirm = new Button
        {
            Content = german
                ? adopt ? "Als ‚Ohne Lyrics‘ übernehmen" : "Wunsch endgültig entfernen"
                : adopt ? "Adopt as ‘Without Lyrics’" : "Remove request permanently",
            Background = new SolidColorBrush(Color.Parse(adopt ? "#DFFF28" : "#D94762")),
            Foreground = new SolidColorBrush(Color.Parse(adopt ? "#11151C" : "#FFFFFF"))
        };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = german
                        ? adopt ? "AUDIO OHNE LYRICS ÜBERNEHMEN?" : "WUNSCH ENTFERNEN?"
                        : adopt ? "ADOPT AUDIO WITHOUT LYRICS?" : "REMOVE REQUEST?",
                    FontSize = 21, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse(adopt ? "#DFFF28" : "#FF6D88"))
                },
                new TextBlock
                {
                    Text = $"{item.Title} · {item.Wish.Track.Artist}\n{item.Event.Name}",
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2")), TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = german
                        ? adopt
                            ? "Die gefundene Audiodatei wird als nicht freigegebenes Songprojekt der Kategorie ‚Ohne Lyrics‘ in den Editor übernommen. Sie bleibt von der Stage ausgeschlossen, bis Lyrics, Stems und Review vollständig sind. Der Wunsch wird danach entfernt."
                            : "Nur der Wunsch wird entfernt. Ein bereits heruntergeladener, aber nicht übernommener Audiofund bleibt unveröffentlicht und erscheint nicht auf der Stage."
                        : adopt
                            ? "The downloaded audio is added to the editor as an unreleased ‘Without Lyrics’ song project. It remains unavailable to the stage until lyrics, stems, and review are complete. The request is then removed."
                            : "Only the request is removed. Any downloaded but unadopted audio remains unpublished and does not appear on the stage.",
                    Foreground = new SolidColorBrush(Color.Parse("#AAB2C1")), TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, confirm }
                }
            }
        };
    }
}
