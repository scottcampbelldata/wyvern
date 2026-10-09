using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Beacon.Core.Commands;
using Screen = System.Windows.Forms.Screen;
using WinForms = System.Windows.Forms;

namespace Beacon.App;

/// <summary>
/// Confetti over the screen the pointer is on: a window that covers it, shows nothing but the
/// pieces, never comes to the front, and goes away when the last piece has fallen. Thrown
/// again while some is in the air, there is simply more of it. Nothing of the screen is read:
/// the window is drawn on, and that is all.
/// </summary>
/// <remarks>
/// The window is one of two kinds. The fast kind is an ordinary window whose empty parts
/// Windows itself shows through (its frame is spread over the whole of it, and what is drawn
/// carries how solid each pixel is): the graphics card draws it and Windows lays it over the
/// screen, so it runs at the screen's own pace whatever the size of the screen. It is a real
/// window to the mouse, though, so a click or a turn of the wheel while confetti falls lands
/// on it: that ends the confetti at once, and the next click goes where it was meant.
/// The other kind lets every click through to what is underneath, which only a layered
/// window can do, and a layered window that covers a large screen has to be copied out of
/// the graphics card for every frame, which is what makes it stutter there.
/// </remarks>
public sealed class ConfettiOverlay
{
    // The longest time one frame lets pass. When a frame is late the confetti slows for a
    // moment in place of jumping ahead, which is what the eye takes for a stutter.
    private const double LongestStep = 1.0 / 20;

    private static readonly Color[] Colours =
    [
        Color.FromRgb(0xFF, 0x63, 0x63),
        Color.FromRgb(0xFF, 0xC5, 0x31),
        Color.FromRgb(0x59, 0xD4, 0x99),
        Color.FromRgb(0x56, 0xC2, 0xFF),
        Color.FromRgb(0xCF, 0x2F, 0x98),
        Color.FromRgb(0xFF, 0x92, 0x17),
        Color.FromRgb(0x9E, 0x6B, 0xFF),
    ];

    private static readonly Brush[] Solid = Colours.Select(colour => Fill(colour, 1)).ToArray();

    private readonly List<Confetti> _throws = [];
    private readonly Stopwatch _clock = new();
    private Window? _window;
    private Sheet? _sheet;
    private TimeSpan _last;
    private bool _running;
    private bool _drawnOnce;
    private bool _clickThrough;

    /// <summary>Throws confetti over the screen the pointer is on.</summary>
    /// <param name="clickThrough">
    /// True for the window that lets clicks through to what is underneath, which stutters on a
    /// large screen. False for the fast one, which a click ends.
    /// </param>
    public void Throw(bool clickThrough = false)
    {
        // Windows shows through the empty parts of an ordinary window from Windows 8 on.
        bool wanted = clickThrough || !OperatingSystem.IsWindowsVersionAtLeast(6, 2);
        if (_window is not null && wanted != _clickThrough)
        {
            Close();
        }

        var window = Overlay(wanted);
        var area = Screen.FromPoint(WinForms.Cursor.Position).Bounds;
        if (_throws.Count == 0)
        {
            // Shown once out of sight, so that the scale windows are placed in is known, and
            // then laid over the whole of the screen.
            window.Left = -10000;
            window.Top = -10000;
            window.Show();
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            window.Left = area.Left / scale;
            window.Top = area.Top / scale;
            window.Width = area.Width / scale;
            window.Height = area.Height / scale;
        }

        _throws.Add(new Confetti(window.Width, window.Height, Environment.TickCount));
        if (!_running)
        {
            _running = true;
            _drawnOnce = false;
            _clock.Restart();
            _last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>Takes the confetti away now. For when Beacon closes.</summary>
    public void Close()
    {
        Stop();
        _window?.Close();
        _window = null;
        _sheet = null;
    }

    // Once for each frame Windows draws: time passes for every throw, and what is left is
    // drawn. The first frame lets no time pass, so that the throw starts when it is first
    // seen and not while the window was still being made.
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double seconds = _drawnOnce ? Math.Min((now - _last).TotalSeconds, LongestStep) : 0;
        _drawnOnce = true;
        _last = now;
        _throws.RemoveAll(one => !one.Step(seconds));
        if (_throws.Count == 0)
        {
            Stop();
            return;
        }

        _sheet?.Draw(_throws);
    }

    private void Stop()
    {
        if (_running)
        {
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
            _clock.Stop();
        }

        _throws.Clear();
        _sheet?.Draw(_throws);
        _window?.Hide();
    }

    private Window Overlay(bool clickThrough)
    {
        if (_window is not null)
        {
            return _window;
        }

        if (!clickThrough)
        {
            try
            {
                if (Fast() is { } fast)
                {
                    _clickThrough = false;
                    return _window = fast;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                // The other kind it is.
            }
        }

        _clickThrough = true;
        return _window = Layered();
    }

    // The fast kind: an ordinary window without a frame of its own. Windows is asked to
    // spread the window's frame over all of it, and from then on shows the desktop through
    // every pixel that is drawn clear. Null when Windows will not.
    private Window? Fast()
    {
        var sheet = new Sheet();
        var window = Blank(sheet);
        window.AllowsTransparency = false;
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is not { CompositionTarget: { } target })
        {
            window.Close();
            return null;
        }

        target.BackgroundColor = Colors.Transparent;
        var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(handle, ref all) != 0)
        {
            window.Close();
            return null;
        }

        // The same is asked for a second way, which other programs rely on for windows that
        // have no frame: "blur what is behind" an area that holds nothing, which blurs
        // nothing and has Windows go by how solid each pixel is. If Windows will not, the
        // first way stands.
        IntPtr nowhere = CreateRectRgn(0, 0, -1, -1);
        try
        {
            var behind = new BlurBehind { Flags = BlurEnable | BlurRegion, Enable = true, Region = nowhere };
            _ = DwmEnableBlurBehindWindow(handle, ref behind);
        }
        finally
        {
            if (nowhere != IntPtr.Zero)
            {
                _ = DeleteObject(nowhere);
            }
        }

        // It never comes to the front and Alt+Tab does not list it. It cannot let clicks
        // through, so the first click or turn of the wheel on it ends the confetti.
        _ = SetWindowLong(handle, ExtendedStyleIndex, GetWindowLong(handle, ExtendedStyleIndex) | NoActivate | ToolWindow);
        window.PreviewMouseDown += (_, _) => Stop();
        window.PreviewMouseWheel += (_, _) => Stop();
        _sheet = sheet;
        return window;
    }

    // The kind that lets clicks through: a layered window, which Windows hands every click
    // on to whatever is underneath when it is told to.
    private Window Layered()
    {
        var sheet = new Sheet();
        var window = Blank(sheet);
        window.AllowsTransparency = true;
        window.IsHitTestVisible = false;
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        _ = SetWindowLong(handle, ExtendedStyleIndex, GetWindowLong(handle, ExtendedStyleIndex) | ClickThrough | NoActivate | ToolWindow);
        _sheet = sheet;
        return window;
    }

    private static Window Blank(Sheet sheet) => new()
    {
        WindowStyle = WindowStyle.None,
        Background = Brushes.Transparent,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = false,
        Focusable = false,
        Topmost = true,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Content = sheet,
    };

    private static Brush Fill(Color colour, double opacity)
    {
        var brush = new SolidColorBrush(colour) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    // What the pieces are drawn on. A frame is one picture, written afresh: all the pieces of
    // one colour are one shape, so a frame is a handful of shapes whatever the number of
    // pieces, and nothing is laid out again.
    private sealed class Sheet : FrameworkElement
    {
        private readonly DrawingVisual _picture = new();

        public Sheet()
        {
            AddVisualChild(_picture);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _picture;

        public void Draw(List<Confetti> throws)
        {
            using var drawing = _picture.RenderOpen();
            var shapes = new StreamGeometry?[Colours.Length];
            var paths = new StreamGeometryContext?[Colours.Length];
            var corners = new Point[3];
            foreach (var one in throws)
            {
                double opacity = one.Opacity;
                if (opacity <= 0)
                {
                    continue;
                }

                foreach (var piece in one.Pieces)
                {
                    int colour = piece.Colour % Colours.Length;
                    if (paths[colour] is not { } path)
                    {
                        shapes[colour] = new StreamGeometry { FillRule = FillRule.Nonzero };
                        paths[colour] = path = shapes[colour]!.Open();
                    }

                    // The four corners of the piece, turned about its middle.
                    double turn = piece.Angle * Math.PI / 180;
                    double cos = Math.Cos(turn);
                    double sin = Math.Sin(turn);
                    double halfWidth = piece.Width / 2;
                    double halfHeight = piece.Height / 2;
                    double acrossX = halfWidth * cos;
                    double acrossY = halfWidth * sin;
                    double downX = -halfHeight * sin;
                    double downY = halfHeight * cos;
                    path.BeginFigure(new Point(piece.X - acrossX - downX, piece.Y - acrossY - downY), isFilled: true, isClosed: true);
                    corners[0] = new Point(piece.X + acrossX - downX, piece.Y + acrossY - downY);
                    corners[1] = new Point(piece.X + acrossX + downX, piece.Y + acrossY + downY);
                    corners[2] = new Point(piece.X - acrossX + downX, piece.Y - acrossY + downY);
                    path.PolyLineTo(corners, isStroked: false, isSmoothJoin: false);
                }

                // The pieces fade through their brushes: a layer of the whole screen made
                // see-through would cost far more than the pieces do.
                for (int colour = 0; colour < shapes.Length; colour++)
                {
                    if (shapes[colour] is { } shape)
                    {
                        paths[colour]!.Close();
                        shape.Freeze();
                        drawing.DrawGeometry(opacity >= 1 ? Solid[colour] : Fill(Colours[colour], opacity), null, shape);
                        shapes[colour] = null;
                        paths[colour] = null;
                    }
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    // GetWindowLong and SetWindowLong with GWL_EXSTYLE (-20): WS_EX_TRANSPARENT (0x20) lets
    // clicks through a layered window, WS_EX_TOOLWINDOW (0x80) keeps the window out of
    // Alt+Tab, and WS_EX_NOACTIVATE (0x08000000) keeps it from ever being the window in front.
    private const int ExtendedStyleIndex = -20;
    private const int ClickThrough = 0x00000020;
    private const int ToolWindow = 0x00000080;
    private const int NoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    // DWM_BLURBEHIND: dwFlags, fEnable, hRgnBlur, fTransitionOnMaximized. DWM_BB_ENABLE is 1
    // and DWM_BB_BLURREGION is 2.
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;

        public IntPtr Region;

        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    private const uint BlurEnable = 0x1;
    private const uint BlurRegion = 0x2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr window, ref BlurBehind blur);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr item);

    // DwmExtendFrameIntoClientArea(hwnd, MARGINS*), in Dwmapi.dll: margins of -1 spread the
    // frame over the whole window. It answers S_OK (zero) or an error.
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
