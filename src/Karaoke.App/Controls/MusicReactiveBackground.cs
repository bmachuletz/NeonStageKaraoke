using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Karaoke.Contracts;

namespace Karaoke.App.Controls;

public sealed class MusicReactiveBackground : Control
{
    public static readonly StyledProperty<IReadOnlyList<VisualizationFrameDto>?> FramesProperty =
        AvaloniaProperty.Register<MusicReactiveBackground, IReadOnlyList<VisualizationFrameDto>?>(nameof(Frames));
    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<MusicReactiveBackground, TimeSpan>(nameof(Position));
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<MusicReactiveBackground, bool>(nameof(IsRunning));

    private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#12091E"));
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private TimeSpan _anchorPosition;
    private long _anchorMilliseconds;

    static MusicReactiveBackground() =>
        AffectsRender<MusicReactiveBackground>(FramesProperty, PositionProperty, IsRunningProperty);

    public MusicReactiveBackground()
    {
        PositionProperty.Changed.AddClassHandler<MusicReactiveBackground>((control, change) =>
            control.SetAnchor(change.NewValue is TimeSpan position ? position : TimeSpan.Zero));
        IsRunningProperty.Changed.AddClassHandler<MusicReactiveBackground>((control, _) => control.SetAnchor(control.Position));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => { if (IsRunning && IsVisible) InvalidateVisual(); });
        _timer.Start();
    }

    public IReadOnlyList<VisualizationFrameDto>? Frames { get => GetValue(FramesProperty); set => SetValue(FramesProperty, value); }
    public TimeSpan Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    private void SetAnchor(TimeSpan position)
    {
        _anchorPosition = position;
        _anchorMilliseconds = _clock.ElapsedMilliseconds;
        InvalidateVisual();
    }

    private double RenderSeconds => (IsRunning
        ? _anchorPosition + TimeSpan.FromMilliseconds(Math.Min(_clock.ElapsedMilliseconds - _anchorMilliseconds, 1000))
        : _anchorPosition).TotalSeconds;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Dark, Bounds);
        var frame = CurrentFrame();
        if (frame is null) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height * 0.53);
        var pulse = frame.Beat ? 1.18 : 1.0;
        DrawGlow(context, center, Math.Max(Bounds.Width, Bounds.Height) * (0.35 + frame.Bass * 0.18) * pulse,
            Color.FromArgb((byte)(25 + frame.Bass * 50), 255, 31, 216));
        DrawGlow(context, center, Math.Max(Bounds.Width, Bounds.Height) * (0.18 + frame.Mid * 0.12),
            Color.FromArgb((byte)(18 + frame.Mid * 42), 80, 220, 255));

        var horizon = Bounds.Height * 0.72;
        var spacing = 42 + frame.Bass * 20;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(25 + frame.High * 55), 232, 255, 50)), 1.2);
        for (var x = -Bounds.Height; x < Bounds.Width + Bounds.Height; x += spacing)
            context.DrawLine(pen, new Point(center.X, horizon), new Point(x, Bounds.Height));
        for (var row = 0; row < 7; row++)
        {
            var ratio = row / 7d;
            var y = horizon + (Bounds.Height - horizon) * ratio * ratio;
            context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }

        // Feste Ruhezone hinter den Lyrics.
        var safeTop = Bounds.Height * 0.25;
        var safeHeight = Bounds.Height * 0.5;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(105, 8, 4, 16)), new Rect(0, safeTop, Bounds.Width, safeHeight));
    }

    private VisualizationFrameDto? CurrentFrame()
    {
        var frames = Frames;
        if (frames is not { Count: > 0 }) return null;
        var exactIndex = Math.Clamp(RenderSeconds * 10, 0, frames.Count - 1);
        var leftIndex = (int)Math.Floor(exactIndex);
        var rightIndex = Math.Min(leftIndex + 1, frames.Count - 1);
        var amount = exactIndex - leftIndex;
        var left = frames[leftIndex];
        var right = frames[rightIndex];
        return new VisualizationFrameDto(
            RenderSeconds,
            Mix(left.Energy, right.Energy, amount),
            Mix(left.Bass, right.Bass, amount),
            Mix(left.Mid, right.Mid, amount),
            Mix(left.High, right.High, amount),
            left.Beat && amount < 0.65);
    }

    private static double Mix(double left, double right, double amount) => left + (right - left) * amount;

    private static void DrawGlow(DrawingContext context, Point center, double radius, Color color)
    {
        var brush = new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };
        context.DrawEllipse(brush, null, center, radius, radius);
    }
}
