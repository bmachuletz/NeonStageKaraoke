using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Karaoke.App.Desktop;

public sealed class ConfirmLibraryCleanupWindow : Window
{
    public ConfirmLibraryCleanupWindow(bool deleteAllVersions)
    {
        Opened += (_, _) => EditorLocale.Apply(this);
        Title = deleteAllVersions ? "Alle Lyrics-Versionen löschen" : "Stage vollständig leeren";
        Width = 620;
        Height = deleteAllVersions ? 340 : 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        var cancel = new Button { Content = "Abbrechen" };
        var confirm = new Button
        {
            Content = deleteAllVersions
                ? "Alle Versionen endgültig löschen"
                : "Alle Songs von der Stage entfernen",
            Background = new SolidColorBrush(Color.Parse("#D94762")),
            Foreground = Brushes.White
        };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);

        var details = deleteAllVersions
            ? "Sämtliche gespeicherten Lyrics-Versionen aller Songs werden unwiderruflich aus der Datenbank gelöscht – einschließlich Entwürfen, Review-Ständen, veröffentlichten Fassungen und Alignment-Berichten. Lokale Editor-Recoverys werden ebenfalls entfernt. Die Audio-, Stem-, LRC- und Quelldateien bleiben erhalten."
            : "Alle derzeit freigegebenen Songs werden auf „In Review“ gesetzt und verschwinden sofort von der Stage. Songs, Audiodateien und Lyrics-Versionen bleiben erhalten.";
        var consequence = deleteAllVersions
            ? "Zusätzlich werden alle Songs von der Stage entfernt. Der freigewordene Datenbankplatz wird anschließend bereinigt."
            : "Diese Aktion lässt sich später songweise durch eine erneute Freigabe rückgängig machen.";

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = deleteAllVersions ? "ALLE LYRICS-VERSIONEN LÖSCHEN?" : "STAGE VOLLSTÄNDIG LEEREN?",
                    FontSize = 21,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#FF6D88"))
                },
                new TextBlock
                {
                    Text = details, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
                },
                new TextBlock
                {
                    Text = consequence, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };
    }
}
