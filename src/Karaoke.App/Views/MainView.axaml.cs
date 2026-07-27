using Avalonia.Controls;

namespace Karaoke.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 1450 || Bounds.Height < 820;
            if (compact && !Classes.Contains("compact")) Classes.Add("compact");
            else if (!compact) Classes.Remove("compact");
        };
    }
}
