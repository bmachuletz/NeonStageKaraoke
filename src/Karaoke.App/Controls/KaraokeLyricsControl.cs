using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Karaoke.Contracts;

namespace Karaoke.App.Controls;

public sealed class KaraokeLyricsControl : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<KaraokeLyricsControl, string>(nameof(Text), string.Empty);
    public static readonly StyledProperty<IReadOnlyList<LyricsWordDto>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeLyricsControl, IReadOnlyList<LyricsWordDto>?>(nameof(Words));
    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<KaraokeLyricsControl, TimeSpan>(nameof(Position));
    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<KaraokeLyricsControl, bool>(nameof(IsRunning));

    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#FFF8FF"));
    private static readonly IBrush SungBrush = new SolidColorBrush(Color.Parse("#E8FF32"));
    private static readonly IBrush GlowBrush = new SolidColorBrush(Color.Parse("#66E8FF32"));
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _anchorPosition;
    private long _anchorMilliseconds;

    public KaraokeLyricsControl()
    {
        MinHeight = 120;
        AffectsRender<KaraokeLyricsControl>(TextProperty, WordsProperty, PositionProperty, IsRunningProperty);
        PositionProperty.Changed.AddClassHandler<KaraokeLyricsControl>((control, change) =>
            control.SetAnchor(change.NewValue is TimeSpan position ? position : TimeSpan.Zero));
        IsRunningProperty.Changed.AddClassHandler<KaraokeLyricsControl>((control, _) =>
            control.SetAnchor(control.Position));
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) =>
            {
                if (IsRunning && IsVisible) InvalidateVisual();
            });
        _renderTimer.Start();
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IReadOnlyList<LyricsWordDto>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public TimeSpan Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    private void SetAnchor(TimeSpan position)
    {
        _anchorPosition = position;
        _anchorMilliseconds = _clock.ElapsedMilliseconds;
        InvalidateVisual();
    }

    private TimeSpan RenderPosition => IsRunning
        ? _anchorPosition + TimeSpan.FromMilliseconds(Math.Min(_clock.ElapsedMilliseconds - _anchorMilliseconds, 1000))
        : _anchorPosition;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrWhiteSpace(Text) || Bounds.Width <= 0) return;

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Black);
        var textWidth = Math.Max(1, Bounds.Width - 24);
        var formatted = FittedLyricsControl.Fit(Text, typeface, PendingBrush, textWidth,
            Math.Max(1, Bounds.Height - 8), 30, 54);
        var origin = new Point(12, Math.Max(0, (Bounds.Height - formatted.Height) / 2));
        context.DrawText(formatted, origin);

        var words = Words;
        if (words is not { Count: > 0 }) return;
        var now = RenderPosition;
        var sungText = FittedLyricsControl.Fit(Text, typeface, Brushes.Transparent, textWidth,
            Math.Max(1, Bounds.Height - 8), 30, 54);
        var glowText = FittedLyricsControl.Fit(Text, typeface, Brushes.Transparent, textWidth,
            Math.Max(1, Bounds.Height - 8), 30, 54);
        var partialWords = new List<(int Index, int Length, double Progress)>();
        var searchIndex = 0;
        foreach (var word in words)
        {
            var wordText = word.Text;
            var characterIndex = Text.IndexOf(wordText, searchIndex, StringComparison.Ordinal);
            if (characterIndex < 0) continue;
            searchIndex = characterIndex + wordText.Length;
            var end = word.End ?? word.Start + TimeSpan.FromMilliseconds(500);
            var progress = now >= end
                ? 1
                : Math.Clamp((now - word.Start).TotalMilliseconds / Math.Max((end - word.Start).TotalMilliseconds, 1), 0, 1);
            if (progress >= 1)
            {
                sungText.SetForegroundBrush(SungBrush, characterIndex, wordText.Length);
                glowText.SetForegroundBrush(GlowBrush, characterIndex, wordText.Length);
            }
            else if (progress > 0)
            {
                partialWords.Add((characterIndex, wordText.Length, progress));
            }
        }

        // Alle abgeschlossenen Wörter in jeweils einem Layout zeichnen. So bleiben
        // beim Zeilenumbruch keine schmalen ungefärbten Clip-Kanten zurück.
        context.DrawText(glowText, origin + new Vector(0, 2));
        context.DrawText(sungText, origin);

        if (partialWords.Count == 0) return;
        var fullGlow = FittedLyricsControl.Fit(Text, typeface, GlowBrush, textWidth,
            Math.Max(1, Bounds.Height - 8), 30, 54);
        var fullSung = FittedLyricsControl.Fit(Text, typeface, SungBrush, textWidth,
            Math.Max(1, Bounds.Height - 8), 30, 54);
        foreach (var partial in partialWords)
        {
            var wordGeometry = formatted.BuildHighlightGeometry(origin, partial.Index, partial.Length);
            if (wordGeometry is not null)
            {
                var bounds = wordGeometry.Bounds;
                var progressClip = new Rect(bounds.X - 2, bounds.Y - 3,
                    (bounds.Width + 4) * partial.Progress, bounds.Height + 6);
                using (context.PushGeometryClip(wordGeometry))
                using (context.PushClip(progressClip))
                {
                    context.DrawText(fullGlow, origin + new Vector(0, 2));
                    context.DrawText(fullSung, origin);
                }
            }
        }
    }

}
