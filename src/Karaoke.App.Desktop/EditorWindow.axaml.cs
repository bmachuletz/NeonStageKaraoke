using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Karaoke.App.Services;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public partial class EditorWindow : Window
{
    private string? _lyricsClipboardFallback;
    private bool _selectionOriginatesFromTimeline;
    private INotifyCollectionChanged? _consoleLogSource;
    private IAudioPlaybackService? _catalogPreviewAudio;
    private CancellationTokenSource? _catalogPreviewCancellation;
    private string? _catalogPreviewTrackId;

    public EditorWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            EditorLocale.Apply(this);
            if (DataContext is not EditorViewModel viewModel) return;
            Timeline.History = viewModel.History;
            _consoleLogSource = viewModel.ConsoleLines;
            _consoleLogSource.CollectionChanged += ConsoleLinesCollectionChanged;
            Timeline.PositionRequested += (_, position) => viewModel.Seek(position);
            Timeline.SegmentSelected += (_, segment) =>
            {
                _selectionOriginatesFromTimeline = true;
                try { viewModel.SelectSegment(segment); }
                finally { _selectionOriginatesFromTimeline = false; }
            };
            Timeline.RangeSelected += (_, range) => viewModel.SetLoopRange(range.Start, range.End);
            Timeline.TrackedWordLoopRangeChanged += (_, range) =>
                viewModel.UpdateTrackedWordLoopRange(range.Start, range.End);
            Timeline.TrackedWordLoopCleared += (_, _) => viewModel.ClearLoopRange();
            Timeline.SegmentEdited += (_, _) => viewModel.NotifyTimelineEdit();
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EditorViewModel.SelectedSegment))
                {
                    if (_selectionOriginatesFromTimeline) Timeline.SelectedSegment = viewModel.SelectedSegment;
                    else Timeline.SelectOnly(viewModel.SelectedSegment);
                }
                if (args.PropertyName is nameof(EditorViewModel.LoopStart) or nameof(EditorViewModel.LoopEnd))
                    Timeline.SetLoopRange(viewModel.LoopStart, viewModel.LoopEnd);
            };
            await viewModel.InitializeAsync();
            ScrollConsoleToEnd();
        };
        Closed += (_, _) =>
        {
            if (_consoleLogSource is not null)
                _consoleLogSource.CollectionChanged -= ConsoleLinesCollectionChanged;
            StopCatalogPreview();
            _catalogPreviewAudio?.Dispose();
            _catalogPreviewAudio = null;
        };
        KeyDown += OnEditorKeyDown;
    }

    private void ConsoleLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(ScrollConsoleToEnd, DispatcherPriority.Background);

    private void ScrollConsoleToEnd()
    {
        var lastIndex = ConsoleLogList.ItemCount - 1;
        if (lastIndex >= 0) ConsoleLogList.ScrollIntoView(lastIndex);
    }

    private async void SongSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel || sender is not ListBox list) return;
        var activeSong = eventArgs.AddedItems.OfType<SongDto>().LastOrDefault()
                         ?? list.SelectedItem as SongDto;
        if (activeSong?.Id == viewModel.SelectedSong?.Id) return;
        viewModel.SelectedSong = activeSong;
        await viewModel.LoadSelectedSongAsync();
    }

    private async void PlayPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.PlayPauseAsync();
    }

    private async void PlayBoundaryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        Timeline.ClearTrackedWordLoop();
        if (DataContext is EditorViewModel viewModel) await viewModel.PlaySelectedBoundaryAsync();
    }

    private async void PlayWordInLoopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel ||
            !Timeline.TryTrackContextWordLoop(out var range)) return;
        await viewModel.PlayLoopRangeAsync(range.Start, range.End);
    }

    private void MoveToOtherVoiceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        if (!Timeline.EditingEnabled)
        {
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? "Zum Bearbeiten zuerst die Beat-Vorschau ausschalten."
                : "Turn off the beat preview before editing.");
            return;
        }
        try
        {
            var moved = viewModel.MoveToOtherVoice(Timeline.GetSelectedSegments());
            Timeline.SelectSegments(moved);
        }
        catch (InvalidOperationException exception)
        {
            viewModel.ReportTimelineStatus(exception.Message);
        }
    }

    private void SelectAllLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        ReportDirectionalSelection(Timeline.SelectContextRange(toRight: false));

    private void SelectAllRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        ReportDirectionalSelection(Timeline.SelectContextRange(toRight: true));

    private void ReportDirectionalSelection(int count)
    {
        if (count > 0 && DataContext is EditorViewModel viewModel)
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? $"{count} Segmente markiert."
                : $"Selected {count} segments.");
    }

    private async void PreviewCatalogTrackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: SpotifyTrackDto track } ||
            !Uri.TryCreate(track.PreviewUrl, UriKind.Absolute, out var previewUri) ||
            previewUri.Scheme is not ("http" or "https")) return;

        if (_catalogPreviewTrackId == track.Id)
        {
            StopCatalogPreview();
            if (DataContext is EditorViewModel stoppedViewModel)
                stoppedViewModel.ReportTimelineStatus(EditorLocale.German
                    ? "Audiovorschau beendet."
                    : "Audio preview stopped.");
            return;
        }

        StopCatalogPreview();
        var cancellation = new CancellationTokenSource();
        _catalogPreviewCancellation = cancellation;
        _catalogPreviewTrackId = track.Id;
        if (DataContext is EditorViewModel editorViewModel)
            editorViewModel.PauseEditorPlaybackForCatalogPreview();
        var player = GetCatalogPreviewAudio();
        try
        {
            await player.PlayAsync(previewUri, cancellationToken: cancellation.Token);
            if (DataContext is EditorViewModel viewModel)
                viewModel.ReportTimelineStatus(EditorLocale.German
                    ? $"30-Sekunden-Vorschau: {track.Title} · {track.Artist}"
                    : $"30-second preview: {track.Title} · {track.Artist}");
            _ = StopCatalogPreviewAfterDelayAsync(track.Id, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StopCatalogPreview();
            if (DataContext is EditorViewModel viewModel)
                viewModel.ReportTimelineStatus((EditorLocale.German
                    ? "Audiovorschau konnte nicht gestartet werden: "
                    : "Audio preview could not be started: ") + exception.Message);
        }
    }

    private IAudioPlaybackService GetCatalogPreviewAudio()
    {
        if (_catalogPreviewAudio is not null) return _catalogPreviewAudio;
        var player = new LibVlcAudioPlaybackService { Volume = 75 };
        player.PlaybackFailed += (_, error) => Dispatcher.UIThread.Post(() =>
        {
            StopCatalogPreview();
            if (DataContext is EditorViewModel viewModel)
                viewModel.ReportTimelineStatus((EditorLocale.German
                    ? "Audiovorschau fehlgeschlagen: "
                    : "Audio preview failed: ") + error);
        });
        player.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(StopCatalogPreview);
        _catalogPreviewAudio = player;
        return player;
    }

    private async Task StopCatalogPreviewAfterDelayAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_catalogPreviewTrackId == trackId) StopCatalogPreview();
            });
        }
        catch (OperationCanceledException) { }
    }

    private void StopCatalogPreview()
    {
        var cancellation = _catalogPreviewCancellation;
        _catalogPreviewCancellation = null;
        _catalogPreviewTrackId = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        _catalogPreviewAudio?.Stop();
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

    private async void ShowVersionReportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel ||
            sender is not Button { Tag: EditorLyricsVersionItem item }) return;
        var report = await viewModel.GetLyricsVersionReportAsync(item);
        if (report is not null) await new LyricsVersionReportWindow(report).ShowDialog(this);
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
        if (DataContext is not EditorViewModel viewModel) return;
        var songs = (SongList.SelectedItems?.OfType<SongDto>() ?? [])
            .DistinctBy(song => song.Id).ToArray();
        if (songs.Length == 0 && viewModel.SelectedSong is { } activeSong)
            songs = [activeSong];
        if (songs.Length == 0) return;
        var choice = await new ConfirmRealignSongWindow(
                songs.Length == 1 ? songs[0].Title : null,
                songs.Length == 1 ? songs[0].Artist : null,
                songs.Length > 1 ? songs.Length : null)
            .ShowDialog<AlignmentVariantChoice?>(this);
        if (choice is { } selected)
            await viewModel.StartSelectedSongsRealignmentAsync(songs, selected);
    }

    private async void RealignAllSongsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var choice = await new ConfirmRealignSongWindow(null, null)
            .ShowDialog<AlignmentVariantChoice?>(this);
        if (choice is { } selected) await viewModel.StartAllSongsRealignmentAsync(selected);
    }

    private async void GlobalLyricsOffsetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { Document: not null } viewModel)
        {
            (DataContext as EditorViewModel)?.ReportTimelineStatus(EditorLocale.German
                ? "Bitte zuerst einen Song mit Lyrics laden."
                : "Load a song with lyrics first.");
            return;
        }
        var offset = await new GlobalLyricsOffsetWindow().ShowDialog<int?>(this);
        if (offset is { } milliseconds) viewModel.ShiftAllLyrics(milliseconds);
    }

    private async void RecognizeLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: { } song } viewModel)
        {
            (DataContext as EditorViewModel)?.ReportTimelineStatus(EditorLocale.German
                ? "Bitte zuerst einen Song auswählen."
                : "Select a song first.");
            return;
        }
        if (await new ConfirmLyricsRecognitionWindow(song.Title, song.Artist,
                viewModel.Document is not null).ShowDialog<bool>(this))
            await viewModel.StartCompleteLyricsRecognitionAsync();
    }

    private async void EditBaseLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: { } song } viewModel)
        {
            (DataContext as EditorViewModel)?.ReportTimelineStatus(EditorLocale.German
                ? "Bitte zuerst einen Song auswählen."
                : "Select a song first.");
            return;
        }
        if (song.ReviewStatus != SongReviewStatus.InReview)
        {
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? "Base-Lyrics lassen sich nur bearbeiten, solange der Song in Review ist."
                : "Base lyrics can only be edited while the song is in review.");
            return;
        }
        try
        {
            var source = await viewModel.LoadBaseLyricsSourceAsync();
            if (source is null)
            {
                await new EditorMessageWindow(
                    EditorLocale.German ? "Keine Base-Lyrics" : "No base lyrics",
                    EditorLocale.German
                        ? "Für diesen Song wurden keine erkannten oder heruntergeladenen Base-Lyrics gefunden."
                        : "No recognized or downloaded base lyrics were found for this song.")
                    .ShowDialog(this);
                return;
            }
            var edited = await new BaseLyricsWindow(song.Title, song.Artist, source)
                .ShowDialog<string?>(this);
            if (edited is not null)
                await viewModel.SaveBaseLyricsSourceAsync(edited);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or
                                          System.Text.Json.JsonException or TaskCanceledException)
        {
            var message = EditorLocale.German
                ? "Base-Lyrics konnten nicht geöffnet werden: " + exception.Message
                : "Base lyrics could not be opened: " + exception.Message;
            viewModel.ReportTimelineStatus(message);
            await new EditorMessageWindow(
                EditorLocale.German ? "Base-Lyrics nicht verfügbar" : "Base lyrics unavailable",
                message).ShowDialog(this);
        }
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

    private void MoveBackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => ShiftTimelineSelection(-10);
    private void MoveForwardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => ShiftTimelineSelection(10);
    private void StartEarlierClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(true, -10);
    private void StartLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(true, 10);
    private void EndEarlierClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(false, -10);
    private void EndLaterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.ResizeSelected(false, 10);
    private void AddWordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddWord();
    private void AddSyllableClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddSyllable();
    private void AddLineClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddLine(false);
    private void DuplicateLineClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => (DataContext as EditorViewModel)?.AddLine(true);
    private void DeleteSegmentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        try
        {
            viewModel.DeleteSelected(Timeline.GetSelectedSegments());
            Timeline.SelectOnly(viewModel.SelectedSegment);
        }
        catch (InvalidOperationException exception) { viewModel.ReportTimelineStatus(exception.Message); }
    }
    private void SegmentTextLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (sender is TextBox textBox) (DataContext as EditorViewModel)?.ChangeSelectedText(textBox.Text ?? string.Empty);
    }
    private void ApplyLinePresentationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel && StageEffectBox.SelectedItem is StageLineEffect effect)
            viewModel.ChangeLinePresentation(effect, Math.Max(0, VoiceLaneBox.SelectedIndex));
    }
    private void ToggleLoopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        (DataContext as EditorViewModel)?.ToggleLoop();
    private void SynchronizeSelectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        try
        {
            var result = Timeline.SynchronizeSelectionToRange();
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? result.UsedWaveform
                    ? $"{result.SegmentCount} Segment(e) synchronisiert · {result.AcousticBoundaries} Wort-/Silbengrenze(n) akustisch angepasst."
                    : $"{result.SegmentCount} Segment(e) exakt eingepasst · Waveform ohne eindeutige Übergänge."
                : result.UsedWaveform
                    ? $"{result.SegmentCount} segment(s) synchronized · {result.AcousticBoundaries} word/syllable boundary adjustment(s) guided by the waveform."
                    : $"{result.SegmentCount} segment(s) fitted exactly · no unambiguous waveform transitions found.");
        }
        catch (InvalidOperationException exception) { viewModel.ReportTimelineStatus(exception.Message); }
    }

    private void ShiftTimelineSelection(int milliseconds)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        try
        {
            TimeSpan? duration = viewModel.SelectedSong is { } song
                ? TimeSpan.FromSeconds(song.DurationSeconds) : null;
            var changed = Timeline.ShiftSelection(TimeSpan.FromMilliseconds(milliseconds), duration);
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? $"{changed} ausgewählte(s) Segment(e) um {milliseconds:+#;-#;0} ms verschoben."
                : $"Shifted {changed} selected segment(s) by {milliseconds:+#;-#;0} ms.");
        }
        catch (InvalidOperationException exception) { viewModel.ReportTimelineStatus(exception.Message); }
    }

    private async void ScaleSelectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        var request = await new ScaleLyricsSelectionWindow().ShowDialog<ScaleLyricsSelectionRequest?>(this);
        if (request is null) return;
        try
        {
            TimeSpan? duration = viewModel.SelectedSong is { } song
                ? TimeSpan.FromSeconds(song.DurationSeconds) : null;
            var changed = Timeline.ScaleSelection(request.Factor, request.Anchor, duration);
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? $"{changed} ausgewählte(s) Segment(e) mit Faktor {request.Factor:0.####} skaliert."
                : $"Scaled {changed} selected segment(s) by factor {request.Factor:0.####}.");
        }
        catch (InvalidOperationException exception) { viewModel.ReportTimelineStatus(exception.Message); }
    }
    private async void CopyLyricsSegmentsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await CopyLyricsSegmentsAsync(cut: false);
    private async void CutLyricsSegmentsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await CopyLyricsSegmentsAsync(cut: true);
    private async void PasteLyricsSegmentsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
        await PasteLyricsSegmentsAsync();

    private async Task CopyLyricsSegmentsAsync(bool cut)
    {
        if (DataContext is not EditorViewModel viewModel) return;
        if (cut && !Timeline.EditingEnabled)
        {
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? "Zum Ausschneiden zuerst die Beat-Vorschau ausschalten."
                : "Turn off the beat preview before cutting.");
            return;
        }
        try
        {
            var selection = Timeline.GetSelectedSegments();
            var text = viewModel.CopyLyricsSegments(selection);
            _lyricsClipboardFallback = text;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
            if (cut) viewModel.CutLyricsSegments(selection);
        }
        catch (Exception exception)
        {
            viewModel.ReportTimelineStatus(exception.Message);
        }
    }

    private async Task PasteLyricsSegmentsAsync()
    {
        if (DataContext is not EditorViewModel viewModel) return;
        if (!Timeline.EditingEnabled)
        {
            viewModel.ReportTimelineStatus(EditorLocale.German
                ? "Zum Einfügen zuerst die Beat-Vorschau ausschalten."
                : "Turn off the beat preview before pasting.");
            return;
        }
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            var inserted = viewModel.PasteLyricsSegments(text ?? _lyricsClipboardFallback ?? string.Empty);
            Timeline.SelectSegments(inserted);
        }
        catch (Exception exception)
        {
            viewModel.ReportTimelineStatus(exception.Message);
        }
    }
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
            Title = EditorLocale.Text("Audio-Ordner mit MP3- oder FLAC-Dateien auswählen"),
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) viewModel.FolderImportPath = path;
    }
    private async void StartFolderImportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel) await viewModel.StartFolderImportAsync();
    }
    private async void ImportUltraStarLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: not null } viewModel)
        {
            (DataContext as EditorViewModel)?.ReportTimelineStatus(EditorLocale.German
                ? "Bitte zuerst einen Song auswählen."
                : "Select a song first.");
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = EditorLocale.Text("UltraStar-Lyrics importieren"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("UltraStar Deluxe TXT") { Patterns = ["*.txt"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var imported = UltraStarLyricsImporter.Parse(await File.ReadAllTextAsync(path));
            var replacing = viewModel.Document is { Lines.Count: > 0 };
            if (!await new ConfirmUltraStarImportWindow(imported, viewModel.SelectedSong, replacing)
                    .ShowDialog<bool>(this)) return;
            await viewModel.ImportUltraStarLyricsAsync(imported);
        }
        catch (UltraStarFormatException exception)
        {
            viewModel.ReportTimelineStatus(EditorLocale.German ? exception.Message : exception.EnglishMessage);
        }
        catch (Exception exception)
        {
            viewModel.ReportTimelineStatus((EditorLocale.German
                ? "UltraStar-Import fehlgeschlagen: "
                : "UltraStar import failed: ") + exception.Message);
        }
    }
    private async void SearchUsdbLyricsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel { SelectedSong: { } song } viewModel)
        {
            (DataContext as EditorViewModel)?.ReportTimelineStatus(EditorLocale.German
                ? "Bitte zuerst einen Song auswählen."
                : "Select a song first.");
            return;
        }
        var imported = await new UsdbLyricsSearchWindow(viewModel.ServerAddress, song)
            .ShowDialog<ImportUsdbLyricsResultDto?>(this);
        if (imported is null) return;
        var saved = imported.Version;
        await viewModel.LoadLyricsVersionAsync(new EditorLyricsVersionItem(
            new LyricsVersionSummaryDto(saved.Id, saved.SongId, saved.Revision, saved.Status,
                saved.AnalysisRunId, saved.CreatedAt, saved.UpdatedAt,
                saved.AlignmentReportJson is not null), true));
        viewModel.ReportTimelineStatus(EditorLocale.German
            ? $"USDB #{imported.UsdbVersionId} als Revision {imported.Version.Revision} gespeichert · {imported.LineCount} Zeilen · {imported.SyllableCount} Silben" +
              (imported.AlignmentStarted ? " · lokale GPU-Ausrichtung läuft" : "")
            : $"USDB #{imported.UsdbVersionId} saved as revision {imported.Version.Revision} · {imported.LineCount} lines · {imported.SyllableCount} syllables" +
              (imported.AlignmentStarted ? " · local GPU alignment is running" : ""));
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
    private async void RemoveWishClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel || sender is not Button { Tag: EditorWishItem item }) return;
        if (await new ConfirmWishActionWindow(item, adopt: false).ShowDialog<bool>(this))
            await viewModel.RemoveWishAsync(item);
    }
    private async void AdoptWishAudioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is not EditorViewModel viewModel || sender is not Button { Tag: EditorWishItem item }) return;
        if (await new ConfirmWishActionWindow(item, adopt: true).ShowDialog<bool>(this))
            await viewModel.AdoptWishAudioAsync(item);
    }
    private async void SearchAdminWishesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        StopCatalogPreview();
        if (DataContext is EditorViewModel viewModel) await viewModel.SearchAdminWishesAsync();
    }
    private async void AdminWishQueryKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && DataContext is EditorViewModel viewModel)
        {
            StopCatalogPreview();
            await viewModel.SearchAdminWishesAsync();
        }
    }
    private async void ImportAdminWishClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        StopCatalogPreview();
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
    private async void OpenDownloadProviderSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel)
            await new QobuzPluginSettingsWindow(viewModel.ServerAddress).ShowDialog(this);
    }
    private async void OpenSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (DataContext is EditorViewModel viewModel)
            await new SettingsWindow(viewModel.ServerAddress).ShowDialog(this);
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
        if (editsText && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            eventArgs.Key is Key.A or Key.C or Key.X or Key.V) return;
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
        else if (eventArgs.Key == Key.A && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (Timeline.SelectAllAtCurrentLevel() is { } selection)
                viewModel.ReportTimelineStatus(EditorLocale.German
                    ? $"{selection.Count} {SelectionTypeName(selection.Type, true)} ausgewählt."
                    : $"Selected all {selection.Count} {SelectionTypeName(selection.Type, false)}.");
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.C && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await CopyLyricsSegmentsAsync(cut: false);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.X && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await CopyLyricsSegmentsAsync(cut: true);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.V && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await PasteLyricsSegmentsAsync();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.N) { viewModel.SelectNextReviewSegment(); eventArgs.Handled = true; }
        else if (eventArgs.Key == Key.R) { viewModel.MarkSelectedReviewed(); eventArgs.Handled = true; }
    }

    private static string SelectionTypeName(LyricSegmentType type, bool german) => (type, german) switch
    {
        (LyricSegmentType.Line, true) => "Zeilen",
        (LyricSegmentType.Word, true) => "Wörter",
        (LyricSegmentType.Syllable, true) => "Silben",
        (LyricSegmentType.Line, false) => "lines",
        (LyricSegmentType.Word, false) => "words",
        (LyricSegmentType.Syllable, false) => "syllables",
        _ => german ? "Segmente" : "segments"
    };
}
