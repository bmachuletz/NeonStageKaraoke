using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>
/// Provides window-local pointer coordinates for the runtime Stage.
///
/// Unity's legacy Linux mouse position can contain XWayland root-desktop
/// coordinates when a borderless player is fullscreen on a monitor whose
/// desktop origin is not (0,0). XQueryPointer against the actual player
/// window removes that desktop offset. XWayland may additionally upscale a
/// lower-resolution Unity framebuffer to the physical window, so the local
/// pointer is mapped back into Unity's Screen coordinate space.
/// Other platforms retain Unity's normal input path.
/// </summary>
internal sealed class StagePointerInput : IDisposable
{
    private const uint Button1Mask = 1u << 8;
    private IntPtr _display;
    private UIntPtr _playerWindow;
    private bool _wasDown;
    private bool _clickConsumed;
    private string? _activeDrag;

    public Vector2 Position { get; private set; }
    public bool IsDown { get; private set; }
    public bool Pressed { get; private set; }
    public bool Released { get; private set; }
    public bool UsesWindowRelativeLinuxInput { get; private set; }

    public void Update()
    {
        var down = false;
        if (!TryReadLinuxWindowPointer(out var position, out down))
        {
            var unity = Input.mousePosition;
            position = new Vector2(unity.x, Screen.height - unity.y);
            down = Input.GetMouseButton(0);
            UsesWindowRelativeLinuxInput = false;
        }

        Position = position;
        IsDown = down;
        Pressed = down && !_wasDown;
        Released = !down && _wasDown;
        _wasDown = down;
        _clickConsumed = false;
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        if (Pressed || Released)
            UnityEngine.Debug.Log(
                $"Neon Stage pointer {(Pressed ? "down" : "up")}: " +
                $"{Position.x:0},{Position.y:0}; " +
                $"source={(UsesWindowRelativeLinuxInput ? "x11-window" : "unity")}");
#endif
    }

    public bool ConsumeClick(Rect rect)
    {
        if (_clickConsumed || !Released || !rect.Contains(Position)) return false;
        _clickConsumed = true;
        return true;
    }

    public bool UpdateDrag(string id, Rect rect, out float normalized, out bool released)
    {
        normalized = 0f;
        released = false;
        if (Pressed && rect.Contains(Position)) _activeDrag = id;
        if (!string.Equals(_activeDrag, id, StringComparison.Ordinal)) return false;
        if (!IsDown && !Released)
        {
            _activeDrag = null;
            return false;
        }

        normalized = Mathf.Clamp01((Position.x - rect.x) / Mathf.Max(1f, rect.width));
        if (Released)
        {
            released = true;
            _activeDrag = null;
        }
        return true;
    }

    private bool TryReadLinuxWindowPointer(out Vector2 position, out bool down)
    {
        position = default;
        down = false;
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        try
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return false;
            if (_playerWindow == UIntPtr.Zero) _playerWindow = FindPlayerWindow();
            if (_playerWindow == UIntPtr.Zero) return false;
            if (XQueryPointer(_display, _playerWindow, out _, out _,
                    out _, out _, out var windowX, out var windowY, out var mask) == 0)
            {
                _playerWindow = UIntPtr.Zero;
                return false;
            }
            if (XGetGeometry(_display, _playerWindow, out _, out _, out _,
                    out var windowWidth, out var windowHeight, out _, out _) == 0 ||
                windowWidth == 0 || windowHeight == 0)
            {
                _playerWindow = UIntPtr.Zero;
                return false;
            }
            position = new Vector2(
                windowX * Screen.width / (float)windowWidth,
                windowY * Screen.height / (float)windowHeight);
            // XWayland can expose an accurate window-local pointer through
            // XQueryPointer while its state mask omits compositor-forwarded
            // or synthetic button events. Unity does receive that button
            // state, so combine the reliable coordinate source with the
            // reliable button source.
            down = (mask & Button1Mask) != 0 || Input.GetMouseButton(0);
            UsesWindowRelativeLinuxInput = true;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
#else
        return false;
#endif
    }

#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
    private UIntPtr FindPlayerWindow()
    {
        var root = XDefaultRootWindow(_display);
        if (root == UIntPtr.Zero || XQueryTree(_display, root, out _, out _,
                out var children, out var childCount) == 0 || children == IntPtr.Zero)
            return UIntPtr.Zero;
        try
        {
            var pidAtom = XInternAtom(_display, "_NET_WM_PID", false);
            if (pidAtom == UIntPtr.Zero) return UIntPtr.Zero;
            var processId = Process.GetCurrentProcess().Id;
            var bestWindow = UIntPtr.Zero;
            ulong bestArea = 0;
            for (var index = 0; index < childCount; index++)
            {
                var window = new UIntPtr(unchecked((ulong)Marshal.ReadIntPtr(
                    children, checked((int)index * IntPtr.Size)).ToInt64()));
                if (ReadWindowProcessId(window, pidAtom) != processId ||
                    XGetGeometry(_display, window, out _, out _, out _,
                        out var width, out var height, out _, out _) == 0)
                    continue;
                var area = (ulong)width * height;
                if (width < 320 || height < 200 || area <= bestArea) continue;
                bestWindow = window;
                bestArea = area;
            }
            return bestWindow;
        }
        finally { XFree(children); }
    }

    private int ReadWindowProcessId(UIntPtr window, UIntPtr pidAtom)
    {
        var result = XGetWindowProperty(_display, window, pidAtom,
            IntPtr.Zero, (IntPtr)1, false, UIntPtr.Zero,
            out _, out var format, out var count, out _, out var property);
        if (result != 0 || property == IntPtr.Zero || format != 32 || count == UIntPtr.Zero)
            return -1;
        try { return Marshal.ReadInt32(property); }
        finally { XFree(property); }
    }
#endif

    public void Dispose()
    {
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
#endif
    }

#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(IntPtr display, UIntPtr window,
        out UIntPtr rootReturn, out UIntPtr childReturn,
        out int rootXReturn, out int rootYReturn,
        out int windowXReturn, out int windowYReturn, out uint maskReturn);

    [DllImport("libX11.so.6")]
    private static extern UIntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryTree(IntPtr display, UIntPtr window,
        out UIntPtr rootReturn, out UIntPtr parentReturn,
        out IntPtr childrenReturn, out uint childCountReturn);

    [DllImport("libX11.so.6")]
    private static extern int XGetGeometry(IntPtr display, UIntPtr drawable,
        out UIntPtr rootReturn, out int xReturn, out int yReturn,
        out uint widthReturn, out uint heightReturn,
        out uint borderWidthReturn, out uint depthReturn);

    [DllImport("libX11.so.6")]
    private static extern UIntPtr XInternAtom(IntPtr display, string atomName,
        [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(IntPtr display, UIntPtr window,
        UIntPtr property, IntPtr longOffset, IntPtr longLength,
        [MarshalAs(UnmanagedType.Bool)] bool delete, UIntPtr requestedType,
        out UIntPtr actualTypeReturn, out int actualFormatReturn,
        out UIntPtr itemCountReturn, out UIntPtr bytesAfterReturn,
        out IntPtr propertyReturn);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);
#endif
}

}
