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
    private StageLyricsPreview? _preview;

    static StageLyricsPreviewControl()
    {
        AffectsRender<StageLyricsPreviewControl>(DocumentProperty, PositionProperty);
        DocumentProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, change) =>
        {
            control._preview = change.NewValue is LyricsEditorDocument document ? new StageLyricsPreview(document) : null;
            control.InvalidateVisual();
        });
        RevisionProperty.Changed.AddClassHandler<StageLyricsPreviewControl>((control, _) =>
        {
            control._preview = control.Document is { } document ? new StageLyricsPreview(document) : null;
            control.InvalidateVisual();
        });
    }

    public LyricsEditorDocument? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public TimeSpan Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#08040F"), 0),
                new GradientStop(Color.Parse("#1B0827"), .55),
                new GradientStop(Color.Parse("#07131A"), 1)
            }
        }, Bounds);
        if (_preview is null) return;
        var frame = _preview.Evaluate(Position);
        if (frame.Lines.Count == 0) return;
        var rowHeight = Bounds.Height / frame.Lines.Count;
        for (var index = 0; index < frame.Lines.Count; index++)
            DrawLine(context, frame.Lines[index], new Rect(42, index * rowHeight, Math.Max(1, Bounds.Width - 84), rowHeight), frame.Alpha);
        if (frame.ShowEntryCue) DrawCue(context, rowHeight, frame.EntryCueProgress, frame.Alpha);
    }

    private static void DrawLine(DrawingContext context, StagePreviewLine line, Rect area, double alpha)
    {
        var size = FitFont(line.Text, area);
        var typeface = new Typeface("Inter", FontStyle.Normal, FontWeight.Bold);
        var baseBrush = new SolidColorBrush(Color.FromArgb((byte)(235 * alpha), 240, 224, 247));
        var text = new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size, baseBrush);
        var origin = new Point(area.Center.X - text.Width / 2, area.Center.Y - text.Height / 2);
        context.DrawText(text, origin);
        if (line.Progress <= 0) return;

        var neon = new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, size,
            new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 223, 255, 40)));
        var clipWidth = text.Width * Math.Clamp(line.Progress, 0, 1);
        using (context.PushClip(new Rect(origin.X, area.Y, clipWidth, area.Height)))
        {
            var glowAlpha = (byte)(Math.Clamp(.24 + (1 - line.Pace) * .28, 0, 1) * 255 * alpha);
            var glow = new SolidColorBrush(Color.FromArgb(glowAlpha, 255, 80, 8));
            foreach (var offset in new[] { new Vector(-2, 0), new Vector(2, 0), new Vector(0, -2), new Vector(0, 2) })
            {
                var glowText = new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, size, glow);
                context.DrawText(glowText, origin + offset);
            }
            context.DrawText(neon, origin);
        }
    }

    private void DrawCue(DrawingContext context, double rowHeight, double progress, double alpha)
    {
        var width = 34d;
        var x = 18d;
        var height = Math.Clamp(rowHeight * .52, 24, 54);
        var y = (rowHeight - height) / 2;
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
        for (var size = Math.Min(40, area.Height * .42); size >= 18; size -= 2)
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), size, Brushes.White);
            if (formatted.Width <= area.Width && formatted.Height <= area.Height - 4) return size;
        }
        return 18;
    }
}
