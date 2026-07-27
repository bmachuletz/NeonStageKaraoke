using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Karaoke.Contracts;

namespace Karaoke.App.Desktop;

public partial class SongPackageExportWindow : Window, INotifyPropertyChanged
{
    private readonly List<SongPackageSelectionItem> _songs;

    public SongPackageExportWindow() : this(Array.Empty<SongDto>()) { }

    public SongPackageExportWindow(IEnumerable<SongDto> songs)
    {
        InitializeComponent();
        _songs = songs.OrderBy(song => song.Artist).ThenBy(song => song.Title)
            .Select(song => new SongPackageSelectionItem(song)).ToList();
        foreach (var song in _songs) song.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectionLabel));
        DataContext = this;
        ApplyFilter();
        EditorLocale.Apply(this);
    }

    public ObservableCollection<SongPackageSelectionItem> VisibleSongs { get; } = [];
    public string SelectionLabel => EditorLocale.German
        ? $"{_songs.Count(song => song.IsSelected)} von {_songs.Count} Songs ausgewählt"
        : $"{_songs.Count(song => song.IsSelected)} of {_songs.Count} songs selected";
    public new event PropertyChangedEventHandler? PropertyChanged;

    private void SearchTextChanged(object? sender, TextChangedEventArgs eventArgs) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        VisibleSongs.Clear();
        foreach (var song in _songs.Where(song => string.IsNullOrWhiteSpace(query) ||
                     song.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     song.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)))
            VisibleSongs.Add(song);
        OnPropertyChanged(nameof(SelectionLabel));
    }

    private void SelectVisibleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        foreach (var song in VisibleSongs) song.IsSelected = true;
    }

    private void ClearSelectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        foreach (var song in _songs) song.IsSelected = false;
    }

    private void ExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        var selected = _songs.Where(song => song.IsSelected).Select(song => song.Id).ToArray();
        if (selected.Length > 0) Close(selected);
    }

    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) => Close(null);
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SongPackageSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public SongPackageSelectionItem(SongDto song) => (Id, Title, Artist) = (song.Id, song.Title, song.Artist);
    public Guid Id { get; }
    public string Title { get; }
    public string Artist { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
