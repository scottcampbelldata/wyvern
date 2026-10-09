using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Screen = System.Windows.Forms.Screen;

namespace Beacon.App;

/// <summary>
/// One line of the tray menu. A line with no action is only words, shown grey, such as the
/// version. A line with no words is a rule between two groups of lines.
/// </summary>
public sealed record TrayMenuItem(string Glyph, string Text, Action? Run = null)
{
    public static TrayMenuItem Rule { get; } = new("", "");
}

/// <summary>
/// The menu of the tray icon, drawn by Beacon in the launcher's own look: rounded, light or
/// dark as Windows is set, a sign in front of each line. It is one window, shown beside the
/// pointer and hidden again when a line is chosen, Esc is pressed or anything else is clicked.
/// </summary>
public partial class TrayMenu : Window
{
    public TrayMenu(IReadOnlyList<TrayMenuItem> items)
    {
        InitializeComponent();
        foreach (var item in items)
        {
            if (item.Text.Length == 0)
            {
                Rows.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 5, 0, 5) }.WithBrush("Line"));
                continue;
            }

            var row = new Button { DataContext = item, Style = (Style)Resources["MenuRow"], IsEnabled = item.Run is not null };
            row.Click += (_, _) =>
            {
                // Hidden first, so that whatever the line opens takes the focus from nothing.
                Hide();
                item.Run?.Invoke();
            };
            Rows.Children.Add(row);
        }

        Deactivated += (_, _) => Hide();
    }

    /// <summary>Shows the menu with its lower left corner at a point of the screen, given in pixels, and inside that screen.</summary>
    public void ShowAt(System.Drawing.Point pixel)
    {
        LauncherLook.Apply(this, LauncherLook.IsLight());

        // Shown once out of sight to be measured, in the units windows are placed in.
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();

        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = Screen.FromPoint(pixel).WorkingArea;
        double left = pixel.X / scale - 14;
        double top = pixel.Y / scale - ActualHeight + 14;
        Left = Math.Clamp(left, area.Left / scale, Math.Max(area.Left / scale, area.Right / scale - ActualWidth));
        Top = Math.Clamp(top, area.Top / scale, Math.Max(area.Top / scale, area.Bottom / scale - ActualHeight));
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides the menu: the one window is used again for as long as Beacon runs.
        // Quitting still works, because application shutdown ignores the cancel.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}

internal static class TrayMenuParts
{
    // Gives a rule its colour by the name the look knows it by, so that it follows light and dark.
    public static Border WithBrush(this Border border, string key)
    {
        border.SetResourceReference(Border.BackgroundProperty, key);
        return border;
    }
}
