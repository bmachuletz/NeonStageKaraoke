using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Karaoke.App.Desktop;

/// <summary>
/// Zeichnet VLC-Frames in die normale Avalonia-Szene. Anders als VideoView
/// erzeugt dieser Host kein natives Kind-/Overlayfenster; dadurch können die
/// Lyrics zuverlässig im selben Grid über dem Video gerendert werden.
/// </summary>
public sealed class VlcBitmapVideoView : Control, IDisposable
{
    private const uint FrameWidth = 768;
    private const uint FrameHeight = 432;
    private const uint FramePitch = FrameWidth * 4;
    private static readonly long MinimumFrameInterval = Stopwatch.Frequency / 30;
    private readonly object _frameLock = new();
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;
    private IntPtr _videoBuffer;
    private byte[]? _latestFrame;
    private WriteableBitmap? _bitmap;
    private int _frameWidth;
    private int _frameHeight;
    private int _framePitch;
    private bool _updateQueued;
    private long _lastQueuedFrame;
    private bool _disposed;

    public VlcBitmapVideoView()
    {
        ClipToBounds = true;
        _lockCallback = LockVideo;
        _displayCallback = DisplayVideo;
    }

    public void Attach(MediaPlayer player)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_frameLock)
        {
            ReleaseBufferLocked();
            var byteCount = checked((int)(FramePitch * FrameHeight));
            _videoBuffer = Marshal.AllocHGlobal(byteCount);
            _latestFrame = new byte[byteCount];
            _frameWidth = (int)FrameWidth;
            _frameHeight = (int)FrameHeight;
            _framePitch = (int)FramePitch;
        }
        player.SetVideoFormat("RV32", FrameWidth, FrameHeight, FramePitch);
        player.SetVideoCallbacks(_lockCallback, null!, _displayCallback);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Black, Bounds);
        var bitmap = _bitmap;
        if (bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var destination = StagePreviewLayout.VideoFrame(Bounds);
        context.DrawImage(bitmap,
            new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), destination);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#526070")), 1), destination);
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        lock (_frameLock)
        {
            Marshal.WriteIntPtr(planes, _videoBuffer);
            return _videoBuffer;
        }
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        lock (_frameLock)
        {
            if (_disposed || _videoBuffer == IntPtr.Zero || _latestFrame is null) return;
            if (_updateQueued) return;
            var now = Stopwatch.GetTimestamp();
            if (_lastQueuedFrame != 0 && now - _lastQueuedFrame < MinimumFrameInterval) return;
            Marshal.Copy(_videoBuffer, _latestFrame, 0, _latestFrame.Length);
            _updateQueued = true;
            _lastQueuedFrame = now;
        }
        Dispatcher.UIThread.Post(PublishLatestFrame, DispatcherPriority.Render);
    }

    private void PublishLatestFrame()
    {
        lock (_frameLock)
        {
            _updateQueued = false;
            if (_disposed || _latestFrame is null || _frameWidth <= 0 || _frameHeight <= 0) return;
            if (_bitmap?.PixelSize != new PixelSize(_frameWidth, _frameHeight))
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(_frameWidth, _frameHeight),
                    new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            using var framebuffer = _bitmap.Lock();
            var rowBytes = checked(_frameWidth * 4);
            for (var row = 0; row < _frameHeight; row++)
                Marshal.Copy(_latestFrame, row * _framePitch,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
        }
        InvalidateVisual();
    }

    private void ReleaseBufferLocked()
    {
        if (_videoBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_videoBuffer);
        _videoBuffer = IntPtr.Zero;
        _latestFrame = null;
        _frameWidth = 0;
        _frameHeight = 0;
        _framePitch = 0;
    }

    public void Dispose()
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseBufferLocked();
        }
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

internal static class StagePreviewLayout
{
    private const double StageAspectRatio = 16d / 9d;

    public static Rect VideoFrame(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return bounds;
        var width = Math.Min(bounds.Width, bounds.Height * StageAspectRatio);
        var height = width / StageAspectRatio;
        return new Rect(bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2, width, height);
    }
}
