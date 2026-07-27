using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public partial class EditorWindow : Window
{
    public EditorWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            EditorLocale.Apply(this);
            if (DataContext is not EditorViewModel viewModel) return;
            Timeline.History = viewModel.History;
            Timeline.PositionRequested += (_, position) => viewModel.Seek(position);
            Timeline.SegmentSelected += (_, segment) => viewModel.SelectSegment(segment);
            Timeline.RangeSelected += (_, range) => viewModel.SetLoopRange(range.Start, range.End);
            Timeline.SegmentEdited += (_, _) => viewModel.NotifyTimelineEdit();
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EditorViewModel.SelectedSegment))
                    Timeline.SelectedSegment = viewModel.SelectedSegment;
                if (args.PropertyName is nameof(EditorViewModel.LoopStart) or nameof(EditorViewModel.LoopEnd))
                    Timeline.SetLoopRange(viewModel.LoopStart, viewModel.LoopEnd);
            };
            await viewModel.InitializeAsync();
        };
        KeyDown += OnEditorKeyDown;
    }

    private async void SongSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel || sender is not ListBox list) return;
        viewModel.SelectedSong = list.SelectedItem as SongDto;
        await viewModel.LoadSelectedSongAsync();
    }

    private async void PlayPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.PlayPauseAsync();
    }

    private async void PlayBoundaryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.PlaySelectedBoundaryAsync();
    }

    private async void CoverPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Cover auswählen", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Bilder")
                { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (DataContext is EditorViewModel viewModel && !string.IsNullOrWhiteSpace(path))
            await viewModel.UploadCoverAsync(path);
    }

    private void CoverDragOver(object? sender, DragEventArgs eventArgs)
    {
        var path = eventArgs.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        eventArgs.DragEffects = IsSupportedCover(path) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void CoverDrop(object? sender, DragEventArgs eventArgs)
    {
        var path = eventArgs.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (DataContext is EditorViewModel viewModel && IsSupportedCover(path))
            await viewModel.UploadCoverAsync(path!);
    }

    private static bool IsSupportedCover(string? path) => path is not null &&
        new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void UndoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.Undo();
    private void RedoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.Redo();

    private async void SaveDraftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.SaveDraftAsync();
    }

    private async void ToggleVersionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        viewModel.ToggleVersions();
        if (viewModel.VersionsVisible) await viewModel.RefreshLyricsVersionsAsync();
    }

    private async void RefreshVersionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.RefreshLyricsVersionsAsync();
    }

    private async void LoadVersionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel ||
            sender is not Button { Tag: EditorLyricsVersionItem item }) return;
        if (await new ConfirmLyricsVersionWindow(item, delete: false).ShowDialog<bool>(this))
            await viewModel.LoadLyricsVersionAsync(item);
    }

    private async void DeleteVersionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel ||
            sender is not Button { Tag: EditorLyricsVersionItem item }) return;
        if (await new ConfirmLyricsVersionWindow(item, delete: true).ShowDialog<bool>(this))
            await viewModel.DeleteLyricsVersionAsync(item);
    }

    private async void RealignSongClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: { } song } viewModel) return;
        if (await new ConfirmRealignSongWindow(song.Title, song.Artist).ShowDialog<bool>(this))
            await viewModel.StartSelectedSongRealignmentAsync();
    }

    private async void RealignAllSongsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        if (await new ConfirmRealignSongWindow(null, null).ShowDialog<bool>(this))
            await viewModel.StartAllSongsRealignmentAsync();
    }

    private async void ApproveSongClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.ApproveSelectedSongAsync();
    }

    private async void DeleteSongClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: { } song } viewModel) return;
        if (await new ConfirmDeleteSongWindow(song.Title, song.Artist).ShowDialog<bool>(this))
            await viewModel.DeleteSelectedSongAsync();
    }

    private void NextReviewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.SelectNextReviewSegment();

    private void MarkReviewedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.MarkSelectedReviewed();

    private void MoveBackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.MoveSelected(-10);
    private void MoveForwardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.MoveSelected(10);
    private void StartEarlierClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(true, -10);
    private void StartLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(true, 10);
    private void EndEarlierClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(false, -10);
    private void EndLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(false, 10);
    private void AddWordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddWord();
    private void AddSyllableClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddSyllable();
    private void AddLineClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddLine(false);
    private void DuplicateLineClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddLine(true);
    private void DeleteSegmentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.DeleteSelected();
    private void SegmentTextLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (sender is TextBox textBox) (DataContext as EditorViewModel)?.ChangeSelectedText(textBox.Text ?? string.Empty);
    }
    private void ApplyLinePresentationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel && StageEffectBox.SelectedItem is StageLineEffect effect)
            viewModel.ChangeLinePresentation(HoldAfterBox.Text ?? string.Empty, effect);
    }
    private void ToggleLoopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.ToggleLoop();
    private void ToggleConsoleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.ToggleConsole();
    private void OpenWishlistConsoleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.ShowWishlistConsole();
    private void OpenFolderImportConsoleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.ShowFolderImportConsole();
    private async void PickImportFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "MP3-Ordner für die Import-Pipeline auswählen",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) viewModel.FolderImportPath = path;
    }
    private async void StartFolderImportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.StartFolderImportAsync();
    }
    private async void ExportCurrentSongPackageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: { } song } viewModel) return;
        await ExportSongPackageAsync(viewModel, [song.Id], song.Title);
    }
    private async void ExportMultipleSongPackagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var selected = await new SongPackageExportWindow(viewModel.Songs).ShowDialog<Guid[]?>(this);
        if (selected is not { Length: > 0 }) return;
        await ExportSongPackageAsync(viewModel, selected, $"neon-stage-{selected.Length}-songs");
    }
    private async void ImportSingleSongPackageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await ImportSongPackagesAsync(allowMultiple: false);
    private async void ImportMultipleSongPackagesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await ImportSongPackagesAsync(allowMultiple: true);

    private async Task ExportSongPackageAsync(EditorViewModel viewModel, IReadOnlyCollection<Guid> songIds,
        string suggestedName)
    {
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = EditorLocale.Text(songIds.Count == 1 ? "Songpaket exportieren" : "Mehrere Songs exportieren"),
            SuggestedFileName = SafePackageName(suggestedName) + ".neonstage.zip",
            FileTypeChoices = [SongPackageFileType()]
        });
        var path = target?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) await viewModel.ExportSongPackageAsync(songIds, path);
    }

    private async Task ImportSongPackagesAsync(bool allowMultiple)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorLocale.Text(allowMultiple
                ? "Mehrere Neon-Stage-Songpakete importieren"
                : "Neon-Stage-Songpaket importieren"),
            AllowMultiple = allowMultiple,
            FileTypeFilter = [SongPackageFileType()]
        });
        var paths = selected.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>().ToArray();
        if (paths.Length > 0) await viewModel.ImportSongPackagesAsync(paths);
    }

    private static FilePickerFileType SongPackageFileType() => new("Neon Stage song package")
    {
        Patterns = ["*.neonstage.zip", "*.zip"],
        MimeTypes = ["application/vnd.neonstage.song-package+zip", "application/zip"]
    };

    private static string SafePackageName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "neon-stage-song" : safe;
    }
    private async void StartWishProcessingClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.StartWishProcessingAsync();
    }
    private async void ProcessSingleWishClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel && sender is Button { Tag: EditorWishItem item })
            await viewModel.StartWishProcessingAsync(item);
    }
    private async void SearchAdminWishesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.SearchAdminWishesAsync();
    }
    private async void AdminWishQueryKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && DataContext is EditorViewModel viewModel)
            await viewModel.SearchAdminWishesAsync();
    }
    private async void ImportAdminWishClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel && sender is Button { Tag: SpotifyTrackDto track })
            await viewModel.ImportAdminWishAsync(track);
    }
    private async void NewSongClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var request = await new NewSongWindow().ShowDialog<NewSongProjectRequest?>(this);
        if (request is not null) await viewModel.ImportSongAsync(request);
    }
    private async void OpenEventManagementClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        await new EventManagementWindow(viewModel.ServerAddress).ShowDialog(this);
        await viewModel.LoadWishEventsAsync();
    }
    private void OpenAdminWebsiteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var url = new Uri(viewModel.ServerAddress, "/admin").AbsoluteUri;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }
    private async void DeleteEventClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel || viewModel.SelectedWishEvent is null) return;
        var count = viewModel.Wishes.Count(item => item.Event.Id == viewModel.SelectedWishEvent.Id);
        if (await new ConfirmDeleteEventWindow(viewModel.SelectedWishEvent.Name, count).ShowDialog<bool>(this))
            await viewModel.DeleteSelectedEventAsync();
    }
    private void ConsoleResizeDragDelta(object? sender, VectorEventArgs eventArgs)
    {
        var maximum = Math.Max(300, Bounds.Height - 80);
        ConsolePanel.Height = Math.Clamp(ConsolePanel.Height - eventArgs.Vector.Y, 260, maximum);
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        // Routed key events also reach the window while a TextBox is editing.
        // Plain editor shortcuts must never swallow characters from any input field.
        var sourceControl = eventArgs.Source as Control;
        var editsText = sourceControl is TextBox ||
                        sourceControl?.GetVisualAncestors().OfType<TextBox>().Any() == true;
        if (editsText && !eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (eventArgs.Key == Key.Space) { await viewModel.PlayPauseAsync(); eventArgs.Handled = true; }
        else if (eventArgs.Key == Key.Z && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift)) viewModel.Redo(); else viewModel.Undo();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.S && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await viewModel.SaveDraftAsync();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.N) { viewModel.SelectNextReviewSegment(); eventArgs.Handled = true; }
        else if (eventArgs.Key == Key.R) { viewModel.MarkSelectedReviewed(); eventArgs.Handled = true; }
    }
}
