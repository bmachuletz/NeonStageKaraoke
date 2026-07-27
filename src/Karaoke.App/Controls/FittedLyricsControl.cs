using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Karaoke.App.Controls;

public sealed class FittedLyricsControl : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<FittedLyricsControl, string>(nameof(Text), string.Empty);
    public static readonly StyledProperty<double> MaximumFontSizeProperty =
        AvaloniaProperty.Register<FittedLyricsControl, double>(nameof(MaximumFontSize), 48);
    public static readonly StyledProperty<double> MinimumFontSizeProperty =
        AvaloniaProperty.Register<FittedLyricsControl, double>(nameof(MinimumFontSize), 26);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FittedLyricsControl, IBrush?>(nameof(Foreground), Brushes.White);

    static FittedLyricsControl() =>
        AffectsRender<FittedLyricsControl>(TextProperty, MaximumFontSizeProperty, MinimumFontSizeProperty,
            ForegroundProperty, BoundsProperty);

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double MaximumFontSize { get => GetValue(MaximumFontSizeProperty); set => SetValue(MaximumFontSizeProperty, value); }
    public double MinimumFontSize { get => GetValue(MinimumFontSizeProperty); set => SetValue(MinimumFontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrWhiteSpace(Text) || Bounds.Width < 10 || Bounds.Height < 10) return;
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Black);
        var formatted = Fit(Text, typeface, Foreground ?? Brushes.White, Bounds.Width - 24, Bounds.Height - 10,
            MinimumFontSize, MaximumFontSize);
        var origin = new Point(12, Math.Max(0, (Bounds.Height - formatted.Height) / 2));
        context.DrawText(formatted, origin);
    }

    internal static FormattedText Fit(string text, Typeface typeface, IBrush brush, double width, double height,
        double minimumSize, double maximumSize)
    {
        FormattedText? selected = null;
        for (var size = maximumSize; size >= minimumSize; size -= 2)
        {
            var candidate = Create(text, typeface, size, brush, width);
            selected = candidate;
            if (candidate.Height <= height) break;
        }
        return selected ?? Create(text, typeface, minimumSize, brush, width);
    }

    internal static FormattedText Create(string text, Typeface typeface, double size, IBrush brush, double width) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(1, width),
            TextAlignment = TextAlignment.Center,
            LineHeight = size * 1.12
        };
}
