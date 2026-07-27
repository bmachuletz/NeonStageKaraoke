using System.Globalization;
using Avalonia.Controls;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class NewEventWindow : Window
{
    private const string DateFormat = "yyyy-MM-dd HH:mm";

    public NewEventWindow()
    {
        InitializeComponent();
        var start = DateTime.Now.AddHours(1);
        var interval = TimeSpan.FromMinutes(15).Ticks;
        start = new DateTime(((start.Ticks + interval - 1) / interval) * interval,
            DateTimeKind.Local);
        StartBox.Text = start.ToString(DateFormat, CultureInfo.InvariantCulture);
        Opened += (_, _) => EditorLocale.Apply(this);
    }

    private void CreateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = EditorLocale.German ? "Bitte einen Eventnamen eingeben." : "Please enter an event name.";
            return;
        }
        if (!TryParseLocal(StartBox.Text, out var startsAt))
        {
            ErrorText.Text = EditorLocale.German ? "Der Startzeitpunkt ist ungültig." : "The start date is invalid.";
            return;
        }
        DateTimeOffset? endsAt = null;
        if (!string.IsNullOrWhiteSpace(EndBox.Text))
        {
            if (!TryParseLocal(EndBox.Text, out var parsedEnd))
            {
                ErrorText.Text = EditorLocale.German ? "Der Endzeitpunkt ist ungültig." : "The end date is invalid.";
                return;
            }
            if (parsedEnd <= startsAt)
            {
                ErrorText.Text = EditorLocale.German ? "Das Ende muss nach dem Start liegen." : "The end must be after the start.";
                return;
            }
            endsAt = parsedEnd;
        }
        Close(new CreateKaraokeEventRequest(NameBox.Text.Trim(), startsAt, endsAt,
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim()));
    }

    private static bool TryParseLocal(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var local)) return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        value = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return true;
    }

    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
}
