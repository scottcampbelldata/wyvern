using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;
using WinForms = System.Windows.Forms;

namespace Wyvern.App;

/// <summary>
/// A few words shown for a moment near the bottom of the screen, such as "Copied to
/// Clipboard": the answer to something that was done with the launcher already hidden. It
/// takes no focus and no click, and goes away by itself. The words are always fixed ones.
/// </summary>
public sealed class Hud
{
    private static readonly TimeSpan Shown = TimeSpan.FromSeconds(1.6);

    private readonly Window _window;
    private readonly TextBlock _words;
    private readonly DispatcherTimer _timer;

    public Hud()
    {
        _words = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13.5,
            Foreground = Brushes.White,
        };
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border
            {
                Margin = new Thickness(10),
                Padding = new Thickness(18, 10, 18, 11),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x20, 0x20, 0x24)),
                Child = _words,
            },
        };
        _timer = new DispatcherTimer { Interval = Shown };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _window.Hide();
        };
    }

    /// <summary>Shows the words for a moment, in the middle near the bottom of the screen the pointer is on. Words shown again start the moment afresh.</summary>
    public void Show(string words)
    {
        _timer.Stop();
        _words.Text = words;

        // Shown once out of sight to be measured, in the units windows are placed in.
        _window.Left = -10000;
        _window.Top = -10000;
        _window.Show();
        _window.UpdateLayout();

        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        var area = Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        _window.Left = area.Left / scale + (area.Width / scale - _window.ActualWidth) / 2;
        _window.Top = area.Bottom / scale - _window.ActualHeight - 70;
        _timer.Start();
    }

    /// <summary>Takes the words away now. For when Wyvern closes.</summary>
    public void Hide()
    {
        _timer.Stop();
        _window.Hide();
    }
}
