using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class EventManagementWindow : Window
{
    private readonly EventManagementViewModel _viewModel;

    public EventManagementWindow() : this(new Uri(
        (Environment.GetEnvironmentVariable("NEONSTAGE_SERVER_URL")
         ?? Environment.GetEnvironmentVariable("KARAOKE_SERVER"))?.Trim()
        ?? "http://192.168.178.91:5274")) { }

    public EventManagementWindow(Uri serverAddress)
    {
        InitializeComponent();
        _viewModel = new EventManagementViewModel(serverAddress);
        DataContext = _viewModel;
        Opened += async (_, _) =>
        {
            EditorLocale.Apply(this);
            await _viewModel.LoadAsync();
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void RefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await _viewModel.LoadAsync();

    private async void QuickSessionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await _viewModel.CreateInstantSessionAsync();

    private async void NewEventClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var request = await new NewEventWindow().ShowDialog<CreateKaraokeEventRequest?>(this);
        if (request is not null) await _viewModel.CreateAsync(request);
    }

    private async void ActivateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await _viewModel.ActivateAsync();

    private async void DeactivateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await _viewModel.DeactivateAsync();

    private async void ProcessWishesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await _viewModel.ProcessWishesAsync();

    private async void DeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedEvent is not { } selected) return;
        if (await new ConfirmDeleteEventWindow(selected.Name, _viewModel.WishCount).ShowDialog<bool>(this))
            await _viewModel.DeleteAsync();
    }

    private async void CopyLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_viewModel.InvitationUrl);
        _viewModel.Report(EditorLocale.German ? "Einladungslink kopiert." : "Invitation link copied.");
    }

    private void WhatsAppClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedEvent is not { } selected) return;
        var text = Uri.EscapeDataString(EditorLocale.German
            ? $"Du bist zu {selected.Name} eingeladen! {_viewModel.InvitationUrl}"
            : $"You are invited to {selected.Name}! {_viewModel.InvitationUrl}");
        OpenExternal($"https://wa.me/?text={text}");
    }

    private void EmailClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedEvent is not { } selected) return;
        var subject = Uri.EscapeDataString(EditorLocale.German
            ? $"Einladung: {selected.Name}" : $"Invitation: {selected.Name}");
        var body = Uri.EscapeDataString(EditorLocale.German
            ? $"Du bist eingeladen!\n\n{_viewModel.InvitationUrl}"
            : $"You are invited!\n\n{_viewModel.InvitationUrl}");
        OpenExternal($"mailto:?subject={subject}&body={body}");
    }

    private async void SaveQrClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedEvent is not { } selected) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = EditorLocale.German ? "Event-QR-Code speichern" : "Save event QR code",
            SuggestedFileName = $"NeonStage-{selected.InviteToken}.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        if (file is null) return;
        var bytes = await _viewModel.GetQrBytesAsync();
        await using var target = await file.OpenWriteAsync();
        target.SetLength(0);
        await target.WriteAsync(bytes);
        _viewModel.Report(EditorLocale.German ? "QR-Code gespeichert." : "QR code saved.");
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _viewModel.Report((EditorLocale.German ? "Öffnen fehlgeschlagen: " : "Could not open: ") + exception.Message);
        }
    }

    private void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
