using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Karaoke.App.Desktop;

public partial class EditorApplication : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new EditorViewModel(new LibVlcAudioPlaybackService());
            desktop.MainWindow = new EditorWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => viewModel.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
