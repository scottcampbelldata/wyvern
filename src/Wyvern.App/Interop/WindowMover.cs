using System.Runtime.InteropServices;
using Wyvern.Core.Commands;

namespace Wyvern.App.Interop;

/// <summary>
/// Puts the window the owner was working in when the launcher opened somewhere on its screen:
/// a half, a quarter, a third, the whole of it, the middle, back where it was, or onto another
/// display. Use on the UI thread.
/// </summary>
/// <param name="window">Gives the window to move, or zero when there is none.</param>
public sealed class WindowMover(Func<IntPtr> window) : IWindowMover
{
    // How many windows' earlier places are kept for Restore before those of windows that
    // have closed are thrown out.
    private const int MostRemembered = 64;

    // Where each window was before it was first moved from here, for Restore.
    private readonly Dictionary<IntPtr, ScreenRect> _before = [];

    public WindowMove Place(WindowLayout layout)
    {
        IntPtr target = window();
        if (target == IntPtr.Zero || !IsWindow(target))
        {
            return WindowMove.NoWindow;
        }

        // Every number below is in pixels of the screens themselves. Without this a program
        // that does not follow each screen's scaling is given numbers Windows has made up
        // for it, and those do not agree with the edge Windows draws.
        IntPtr scaling = UsePixels();
        try
        {
            return Place(target, layout);
        }
        finally
        {
            Restore(scaling);
        }
    }

    private WindowMove Place(IntPtr target, WindowLayout layout)
    {
        // A minimised window has no place on a screen: it is put back first.
        if (IsIconic(target))
        {
            _ = ShowWindow(target, ShowRestored);
        }

        WindowMove done;
        switch (layout)
        {
            case WindowLayout.Maximize:
                if (!IsZoomed(target))
                {
                    Remember(target);
                }

                _ = ShowWindow(target, ShowMaximized);
                done = Went(IsZoomed(target));
                break;

            case WindowLayout.Restore:
                if (IsZoomed(target))
                {
                    _ = ShowWindow(target, ShowRestored);
                }

                done = Went(!_before.Remove(target, out var before) || Put(target, before));
                break;

            case WindowLayout.NextDisplay:
            case WindowLayout.PreviousDisplay:
                done = ToOtherDisplay(target, layout == WindowLayout.NextDisplay);
                break;

            default:
                done = Went(ToPlace(target, layout));
                break;
        }

        // The launcher has just hidden itself, and the window that was moved is the one to
        // go on working in.
        _ = SetForegroundWindow(target);
        return done;
    }

    private bool ToPlace(IntPtr target, WindowLayout layout)
    {
        // A maximised window does not move: it is made an ordinary one first.
        if (IsZoomed(target))
        {
            _ = ShowWindow(target, ShowRestored);
        }

        if (Seen(target) is not { } now || WorkArea(MonitorFromWindow(target, Nearest)) is not { } work || WindowCommands.Place(layout, work, now) is not { } place)
        {
            return false;
        }

        Remember(target);
        return Put(target, CanResize(target) ? place : WindowCommands.Fitted(place, work, now));
    }

    private static WindowMove Went(bool done) => done ? WindowMove.Done : WindowMove.Refused;

    private WindowMove ToOtherDisplay(IntPtr target, bool next)
    {
        var monitors = Monitors();
        int current = monitors.FindIndex(monitor => monitor.Handle == MonitorFromWindow(target, Nearest));
        if (WindowCommands.OtherDisplay(monitors.Select(monitor => monitor.Area).ToList(), current, next) is not { } other)
        {
            return WindowMove.OneDisplay;
        }

        // A maximised window goes over as an ordinary one and is maximised again there.
        bool maximised = IsZoomed(target);
        if (maximised)
        {
            _ = ShowWindow(target, ShowRestored);
        }

        if (Seen(target) is not { } now)
        {
            return WindowMove.Refused;
        }

        Remember(target);
        var from = monitors[current].Work;
        var to = monitors[other].Work;
        var place = WindowCommands.OnDisplay(now, from, to);
        place = CanResize(target) ? place : WindowCommands.Fitted(place, to, now);

        // Twice: a display that is scaled differently makes the window change its own size
        // as it arrives, and the second time puts that right.
        bool done = Put(target, place) && Put(target, place);
        if (maximised)
        {
            _ = ShowWindow(target, ShowMaximized);
        }

        return Went(done);
    }

    // Where the window was before it was first moved from here. Kept once: a second move
    // does not write over it, so Restore goes back to where the owner had the window.
    private void Remember(IntPtr target)
    {
        if (_before.ContainsKey(target) || Seen(target) is not { } now)
        {
            return;
        }

        if (_before.Count >= MostRemembered)
        {
            foreach (IntPtr gone in _before.Keys.Where(known => !IsWindow(known)).ToList())
            {
                _before.Remove(gone);
            }

            if (_before.Count >= MostRemembered)
            {
                _before.Clear();
            }
        }

        _before[target] = now;
    }

    // Puts the edge of the window that is seen at the place. Windows 10 and 11 give a window
    // a border that is not drawn, to take hold of it by, and count it as part of the window:
    // the place asked for is made larger by that much on each side.
    private static bool Put(IntPtr target, ScreenRect place)
    {
        if (!GetWindowRect(target, out Rect whole))
        {
            return false;
        }

        Rect seen = SeenEdge(target) ?? whole;
        int left = seen.Left - whole.Left;
        int top = seen.Top - whole.Top;
        int right = whole.Right - seen.Right;
        int bottom = whole.Bottom - seen.Bottom;
        return SetWindowPos(
            target,
            IntPtr.Zero,
            place.Left - left,
            place.Top - top,
            place.Width + left + right,
            place.Height + top + bottom,
            NoZOrder | NoActivate);
    }

    // The edge of the window that is seen, or null when the window has gone.
    private static ScreenRect? Seen(IntPtr target)
    {
        if (!GetWindowRect(target, out Rect whole))
        {
            return null;
        }

        Rect seen = SeenEdge(target) ?? whole;
        return new ScreenRect(seen.Left, seen.Top, seen.Right - seen.Left, seen.Bottom - seen.Top);
    }

    private static Rect? SeenEdge(IntPtr target)
    {
        try
        {
            return DwmGetWindowAttribute(target, ExtendedFrameBounds, out Rect seen, Marshal.SizeOf<Rect>()) == 0
                && seen.Right > seen.Left
                && seen.Bottom > seen.Top
                ? seen
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // A window with a border to drag can be given any size. One without keeps its size.
    private static bool CanResize(IntPtr target) => (GetWindowLong(target, StyleIndex) & SizingBorder) != 0;

    private static ScreenRect? WorkArea(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? Area(info.Work) : null;
    }

    private static List<(IntPtr Handle, ScreenRect Area, ScreenRect Work)> Monitors()
    {
        var found = new List<(IntPtr, ScreenRect, ScreenRect)>();
        MonitorCallback each = (IntPtr monitor, IntPtr context, ref Rect area, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                found.Add((monitor, Area(info.Monitor), Area(info.Work)));
            }

            return true;
        };
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, each, IntPtr.Zero);
        GC.KeepAlive(each);
        return found;
    }

    private static ScreenRect Area(Rect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static IntPtr UsePixels()
    {
        try
        {
            return SetThreadDpiAwarenessContext(PerMonitorV2);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    private static void Restore(IntPtr scaling)
    {
        if (scaling != IntPtr.Zero)
        {
            _ = SetThreadDpiAwarenessContext(scaling);
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // MONITORINFO: cbSize, rcMonitor (the whole display), rcWork (without the taskbar), dwFlags.
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr context, ref Rect area, IntPtr data);

    private const int ShowMaximized = 3;          // SW_MAXIMIZE
    private const int ShowRestored = 9;           // SW_RESTORE
    private const uint Nearest = 2;               // MONITOR_DEFAULTTONEAREST
    private const uint NoZOrder = 0x0004;         // SWP_NOZORDER
    private const uint NoActivate = 0x0010;       // SWP_NOACTIVATE
    private const int StyleIndex = -16;           // GWL_STYLE
    private const int SizingBorder = 0x00040000;  // WS_THICKFRAME
    private const int ExtendedFrameBounds = 9;    // DWMWA_EXTENDED_FRAME_BOUNDS
    private static readonly IntPtr PerMonitorV2 = new(-4);  // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    // ShowWindow answers whether the window was visible before, which is of no use here.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int how);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    // SetWindowPos answers false when Windows would not move the window, as for one that
    // belongs to a program running with more rights.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr context, IntPtr clip, MonitorCallback each, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    // DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, RECT*, sizeof(RECT)), in
    // Dwmapi.dll: the edge of the window as it is drawn. It answers S_OK (zero) or an error.
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
}
