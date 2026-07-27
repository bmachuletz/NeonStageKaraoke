namespace Karaoke.Editor.Core;

public sealed class TimelineViewport
{
    public TimelineViewport(double pixelsPerSecond = 120, TimeSpan? offset = null)
    {
        PixelsPerSecond = Math.Clamp(pixelsPerSecond, 5, 4000);
        Offset = offset ?? TimeSpan.Zero;
    }

    public double PixelsPerSecond { get; private set; }
    public TimeSpan Offset { get; private set; }

    public double TimeToPixel(TimeSpan time) => (time - Offset).TotalSeconds * PixelsPerSecond;

    public TimeSpan PixelToTime(double pixel) =>
        Offset + TimeSpan.FromSeconds(pixel / PixelsPerSecond);

    public void ScrollPixels(double pixels) => SetOffset(Offset + TimeSpan.FromSeconds(pixels / PixelsPerSecond));

    public void ZoomAt(double factor, double anchorPixel)
    {
        var anchorTime = PixelToTime(anchorPixel);
        PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, 5, 4000);
        SetOffset(anchorTime - TimeSpan.FromSeconds(anchorPixel / PixelsPerSecond));
    }

    public (TimeSpan Start, TimeSpan End) VisibleRange(double width) =>
        (Offset, PixelToTime(Math.Max(0, width)));

    public void Reset(double pixelsPerSecond = 115)
    {
        PixelsPerSecond = Math.Clamp(pixelsPerSecond, 5, 4000);
        Offset = TimeSpan.Zero;
    }

    public void ShowWindow(TimeSpan start, TimeSpan duration, double pixelWidth)
    {
        if (duration <= TimeSpan.Zero || pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        PixelsPerSecond = Math.Clamp(pixelWidth / duration.TotalSeconds, 5, 4000);
        SetOffset(start);
    }

    private void SetOffset(TimeSpan value) => Offset = value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
