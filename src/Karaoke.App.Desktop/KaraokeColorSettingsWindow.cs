using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public sealed class KaraokeColorSettingsWindow : Window
{
    private readonly TextBox _unsung;
    private readonly TextBox _sung;
    private readonly TextBox _glow;
    private readonly Border _unsungSwatch;
    private readonly Border _sungSwatch;
    private readonly Border _glowSwatch;
    private readonly Slider _outlineStrength;
    private readonly TextBlock _outlineValue;
    private readonly Slider _burnIntensity;
    private readonly TextBlock _burnValue;
    private readonly TextBlock _error;

    public KaraokeColorSettingsWindow(KaraokeColorSettings? source)
    {
        var german = EditorLocale.German;
        var colors = (source ?? new KaraokeColorSettings()).Normalized();
        Title = german ? "Lyrics-Darstellung" : "Lyrics appearance";
        Width = 520;
        Height = 555;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1016"));

        _unsung = Input(colors.UnsungColor);
        _sung = Input(colors.SungColor);
        _glow = Input(colors.GlowColor);
        _unsungSwatch = Swatch(colors.UnsungColor);
        _sungSwatch = Swatch(colors.SungColor);
        _glowSwatch = Swatch(colors.GlowColor);
        _outlineStrength = new Slider
        {
            Minimum = 0, Maximum = 100, Value = colors.OutlineStrength,
            TickFrequency = 5, Width = 220, VerticalAlignment = VerticalAlignment.Center
        };
        _outlineValue = new TextBlock
        {
            Text = $"{colors.OutlineStrength}%", Width = 48,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF"))
        };
        _outlineStrength.ValueChanged += (_, _) =>
            _outlineValue.Text = $"{Math.Round(_outlineStrength.Value):0}%";
        _burnIntensity = new Slider
        {
            Minimum = 0, Maximum = 100, Value = colors.BurnIntensity,
            TickFrequency = 5, Width = 220, VerticalAlignment = VerticalAlignment.Center
        };
        _burnValue = new TextBlock
        {
            Text = $"{colors.BurnIntensity}%", Width = 48,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF"))
        };
        _burnIntensity.ValueChanged += (_, _) =>
            _burnValue.Text = $"{Math.Round(_burnIntensity.Value):0}%";
        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF6D88")),
            TextWrapping = TextWrapping.Wrap
        };
        _unsung.TextChanged += (_, _) => UpdateSwatch(_unsung, _unsungSwatch);
        _sung.TextChanged += (_, _) => UpdateSwatch(_sung, _sungSwatch);
        _glow.TextChanged += (_, _) => UpdateSwatch(_glow, _glowSwatch);

        var reset = new Button { Content = german ? "Defaults" : "Defaults" };
        var cancel = new Button { Content = german ? "Abbrechen" : "Cancel" };
        var apply = new Button
        {
            Content = german ? "Übernehmen" : "Apply",
            Background = new SolidColorBrush(Color.Parse("#DFFF28")),
            Foreground = new SolidColorBrush(Color.Parse("#11151C"))
        };
        reset.Click += (_, _) =>
        {
            _unsung.Text = KaraokeColorSettings.DefaultUnsungColor;
            _sung.Text = KaraokeColorSettings.DefaultSungColor;
            _glow.Text = KaraokeColorSettings.DefaultGlowColor;
            _outlineStrength.Value = KaraokeColorSettings.DefaultOutlineStrength;
            _burnIntensity.Value = KaraokeColorSettings.DefaultBurnIntensity;
        };
        cancel.Click += (_, _) => Close(null);
        apply.Click += (_, _) => Apply(german);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 13 };
        panel.Children.Add(new TextBlock
        {
            Text = german ? "KARAOKE-DARSTELLUNG" : "KARAOKE APPEARANCE", FontSize = 21,
            FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#DFFF28"))
        });
        panel.Children.Add(new TextBlock
        {
            Text = german
                ? "Farben und Burn-Intensität gelten für diesen Song. Auf Video legt die Stage automatisch eine dunkle Kontrastkante unter die Schrift."
                : "Colors and burn intensity apply to this song. On video, the Stage automatically adds a dark contrast edge behind the text.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C0C6D2"))
        });
        panel.Children.Add(Row(german ? "UNGESUNGENE SCHRIFT" : "UNSUNG TEXT", _unsung, _unsungSwatch));
        panel.Children.Add(Row(german ? "GESUNGENE SCHRIFT" : "SUNG TEXT", _sung, _sungSwatch));
        panel.Children.Add(Row("GLOW", _glow, _glowSwatch));
        panel.Children.Add(StrengthRow(german ? "SCHWARZE KONTUR" : "BLACK OUTLINE",
            _outlineStrength, _outlineValue));
        panel.Children.Add(StrengthRow(german ? "BURN-/FLAMMEN-INTENSITÄT" : "BURN / FLAME INTENSITY",
            _burnIntensity, _burnValue));
        panel.Children.Add(_error);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { reset, cancel, apply }
        });
        Content = panel;
        Opened += (_, _) => _unsung.Focus();
    }

    private static TextBox Input(string value) => new()
    {
        Text = value, Width = 150, FontFamily = new FontFamily("monospace"), FontSize = 16
    };

    private static Border Swatch(string value) => new()
    {
        Width = 54, Height = 32, CornerRadius = new CornerRadius(5),
        BorderBrush = new SolidColorBrush(Color.Parse("#596273")), BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.Parse(value))
    };

    private static Control Row(string label, TextBox input, Border swatch)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")), FontSize = 11
        });
        Grid.SetColumn(input, 1);
        Grid.SetColumn(swatch, 2);
        grid.Children.Add(input);
        grid.Children.Add(swatch);
        return grid;
    }

    private static Control StrengthRow(string label, Slider slider, TextBlock value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#FF3CBD")), FontSize = 11
        });
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    private static void UpdateSwatch(TextBox input, Border swatch)
    {
        if (KaraokeColorSettings.TryNormalize(input.Text, out var value))
            swatch.Background = new SolidColorBrush(Color.Parse(value));
    }

    private void Apply(bool german)
    {
        if (!KaraokeColorSettings.TryNormalize(_unsung.Text, out var unsung) ||
            !KaraokeColorSettings.TryNormalize(_sung.Text, out var sung) ||
            !KaraokeColorSettings.TryNormalize(_glow.Text, out var glow))
        {
            _error.Text = german
                ? "Bitte Farben als #RRGGBB oder #RGB eingeben."
                : "Enter colors as #RRGGBB or #RGB.";
            return;
        }
        Close(new KaraokeColorSettings
        {
            UnsungColor = unsung, SungColor = sung, GlowColor = glow,
            OutlineStrength = (int)Math.Round(_outlineStrength.Value),
            BurnIntensity = (int)Math.Round(_burnIntensity.Value)
        });
    }
}
