using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public sealed class StageLyricsPreviewControl : Control
{
    public static readonly StyledProperty<LyricsEditorDocument?> DocumentProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, LyricsEditorDocument?>(nameof(Document));
    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, TimeSpan>(nameof(Position));
    public static readonly StyledProperty<long> RevisionProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, long>(nameof(Revision));
    public static readonly StyledProperty<bool> PerceptualLeadEnabledProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, bool>(nameof(PerceptualLeadEnabled), true);
    public static readonly StyledProperty<bool> KaraokeTimingEnabledProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, bool>(nameof(KaraokeTimingEnabled), true);
    public static readonly StyledProperty<bool> MusicalHighlightEnabledProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, bool>(nameof(MusicalHighlightEnabled),
            StageLyricsPreview.MusicalHighlightEnvironmentEnabled());
    public static readonly StyledProperty<bool> HasVideoBackgroundProperty =
        AvaloniaProperty.Register<StageLyricsPreviewControl, bool>(nameof(HasVideoBackground));
    private StageLyricsPreview? _preview;

    static StageLyricsPreviewControl()
    {
        AffectsRender<StageLyricsPreviewControl>(DocumentProperty, PositionProperty, HasVideoBackgroundProperty);
        DocumentProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, change) =>
        {
            control.RebuildPreview(change.NewValue as LyricsEditorDocument);
            control.InvalidateVisual();
        });
        RevisionProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, _) =>
        {
            control.RebuildPreview(control.Document);
            control.InvalidateVisual();
        });
        PerceptualLeadEnabledProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, _) =>
        {
            control.RebuildPreview(control.Document);
            control.InvalidateVisual();
        });
        KaraokeTimingEnabledProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, _) =>
        {
            control.RebuildPreview(control.Document);
            control.InvalidateVisual();
        });
        MusicalHighlightEnabledProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, _) =>
        {
            control.RebuildPreview(control.Document);
            control.InvalidateVisual();
        });
    }

    public LyricsEditorDocument? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public TimeSpan Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public bool PerceptualLeadEnabled
    {
        get => GetValue(PerceptualLeadEnabledProperty);
        set => SetValue(PerceptualLeadEnabledProperty, value);
    }
    public bool KaraokeTimingEnabled
    {
        get => GetValue(KaraokeTimingEnabledProperty);
        set => SetValue(KaraokeTimingEnabledProperty, value);
    }

    public bool MusicalHighlightEnabled
    {
        get => GetValue(MusicalHighlightEnabledProperty);
        set => SetValue(MusicalHighlightEnabledProperty, value);
    }
    public bool HasVideoBackground
    {
        get => GetValue(HasVideoBackgroundProperty);
        set => SetValue(HasVideoBackgroundProperty, value);
    }

    private void RebuildPreview(LyricsEditorDocument? document) =>
        _preview = document is null ? null : new StageLyricsPreview(
            document, PerceptualLeadEnabled, KaraokeTimingEnabled, MusicalHighlightEnabled);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var presentationBounds = HasVideoBackground
            ? StagePreviewLayout.VideoFrame(Bounds)
            : Bounds;
        using var frameClip = context.PushClip(presentationBounds);
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(HasVideoBackground ? Color.Parse("#7008040F") : Color.Parse("#08040F"), 0),
                new GradientStop(HasVideoBackground ? Color.Parse("#781B0827") : Color.Parse("#1B0827"), .55),
                new GradientStop(HasVideoBackground ? Color.Parse("#7007131A") : Color.Parse("#07131A"), 1)
            }
        }, presentationBounds);
        if (_preview is null) return;
        var frame = _preview.Evaluate(Position);
        if (frame.Lines.Count == 0) return;
        var rowHeight = presentationBounds.Height / frame.Lines.Count;
        var horizontalPadding = Math.Clamp(presentationBounds.Width * .045, 24, 56);
        var palette = PreviewPalette.From(Document?.KaraokeColors);
        for (var index = 0; index < frame.Lines.Count; index++)
            DrawLine(context, frame.Lines[index], new Rect(
                presentationBounds.X + horizontalPadding,
                presentationBounds.Y + index * rowHeight,
                Math.Max(1, presentationBounds.Width - horizontalPadding * 2), rowHeight), frame.Alpha,
                palette, HasVideoBackground);
        if (frame.ShowEntryCue)
            DrawCue(context, presentationBounds, rowHeight, frame.EntryCueProgress, frame.Alpha);
        if (HasVideoBackground)
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#526070")), 1), presentationBounds);
    }

    private static void DrawLine(DrawingContext context, StagePreviewLine line, Rect area, double alpha,
        PreviewPalette palette, bool highContrast)
    {
        var size = FitFont(line.Text, area);
        var typeface = new Typeface("Inter", FontStyle.Normal, FontWeight.Bold);
        var baseBrush = new SolidColorBrush(WithAlpha(palette.Unsung, .95 * alpha));
        var text = new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size, baseBrush);
        var origin = new Point(area.Center.X - text.Width / 2, area.Center.Y - text.Height / 2);
        DrawContrastEdge(context, line.Text, typeface, size, origin, alpha, highContrast,
            palette.OutlineStrength);
        context.DrawText(text, origin);
        if (line.Progress <= 0) return;

        var neon = new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size,
            new SolidColorBrush(WithAlpha(palette.Sung, alpha)));
        var clipWidth = text.Width * Math.Clamp(line.Progress, 0, 1);
        using (context.PushClip(new Rect(origin.X, area.Y, clipWidth, area.Height)))
        {
            var glowAlpha = (byte)(Math.Clamp(.24 + (1 - line.Pace) * .28, 0, 1) * 255 * alpha);
            var glow = new SolidColorBrush(Color.FromArgb(glowAlpha, palette.Glow.R, palette.Glow.G, palette.Glow.B));
            foreach (var offset in new[] { new Vector(-2, 0), new Vector(2, 0), new Vector(0, -2), new Vector(0, 2) })
            {
                var glowText = new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, size, glow);
                context.DrawText(glowText, origin + offset);
            }
            context.DrawText(neon, origin);
        }
    }

    private static void DrawContrastEdge(DrawingContext context, string value, Typeface typeface, double size,
        Point origin, double alpha, bool highContrast, int outlineStrength)
    {
        if (outlineStrength <= 0) return;
        var edge = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size,
            new SolidColorBrush(Color.FromArgb((byte)((highContrast ? 230 : 205) * alpha), 0, 0, 0)));
        var radius = .5 + Math.Clamp(outlineStrength, 0, 100) / 100d * 3.5;
        foreach (var offset in new[]
                 {
                     new Vector(-radius, 0), new Vector(radius, 0), new Vector(0, -radius), new Vector(0, radius),
                     new Vector(-radius, -radius), new Vector(radius, -radius),
                     new Vector(-radius, radius), new Vector(radius, radius)
                 })
            context.DrawText(edge, origin + offset);
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);

    private readonly record struct PreviewPalette(Color Unsung, Color Sung, Color Glow, int OutlineStrength)
    {
        public static PreviewPalette From(KaraokeColorSettings? colors)
        {
            var normalized = (colors ?? new KaraokeColorSettings()).Normalized();
            return new(Color.Parse(normalized.UnsungColor), Color.Parse(normalized.SungColor),
                Color.Parse(normalized.GlowColor), normalized.OutlineStrength);
        }
    }

    private static void DrawCue(DrawingContext context, Rect presentationBounds, double rowHeight,
        double progress, double alpha)
    {
        var width = 34d;
        var x = presentationBounds.X + Math.Clamp(presentationBounds.Width * .018, 10, 24);
        var height = Math.Clamp(rowHeight * .52, 24, 54);
        var y = presentationBounds.Y + (rowHeight - height) / 2;
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(80 * alpha), 255, 35, 120)), null,
            new Rect(x, y, width, height), 5, 5);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(245 * alpha), 223, 255, 40)), null,
            new Rect(x, y, width * Math.Clamp(progress, 0, 1), height), 5, 5);
        var scanX = x + width * Math.Clamp(progress, 0, 1);
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 110, 15)), 3),
            new Point(scanX, y - 4), new Point(scanX, y + height + 4));
    }

    private static double FitFont(string text, Rect area)
    {
        for (var size = Math.Min(54, area.Height * .46); size >= 12; size -= 2)
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), size, Brushes.White);
            if (formatted.Width <= area.Width && formatted.Height <= area.Height - 4) return size;
        }
        return 12;
    }
}
