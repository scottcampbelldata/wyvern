using WinForms = System.Windows.Forms;

namespace Beacon.App;

/// <summary>
/// The notification-area icon: a menu, a click to open, and balloon notifications.
/// The callbacks run on the UI thread; the caller makes sure they do not throw.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private TrayMenu _menu;
    private readonly TrayMenuItem[] _items;
    private readonly WinForms.NotifyIcon _icon;

    // Beacon's own icon, or null when it could not be read: then Windows' plain one is shown.
    private readonly System.Drawing.Icon? _own = OwnIcon();

    /// <param name="menuOpening">
    /// Called each time the menu is about to open, so that the text of the icon can be made
    /// again from how things stand at that moment (<see cref="SetText"/>). It is the only
    /// thing that does so apart from a change that is told: nothing here runs on a schedule.
    /// </param>
    public TrayIcon(Action open, Action openClipboard, Action openSettings, Action reload, Action syncNow, Action quit, Action menuOpening)
    {
        // The menu is Beacon's own window, in the launcher's look, and not the system's.
        _items =
        [
            new TrayMenuItem("\uE8A7", "Open Beacon", open),
            new TrayMenuItem("\uE77F", "Clipboard History", openClipboard),
            TrayMenuItem.Rule,
            new TrayMenuItem("\uE72C", "Reload", reload),
            new TrayMenuItem("\uE895", "Sync Now", syncNow),
            TrayMenuItem.Rule,
            new TrayMenuItem("\uE946", "Version: " + Version()),
            new TrayMenuItem("\uE713", "Settings...", openSettings),
            TrayMenuItem.Rule,
            new TrayMenuItem("\uE7E8", "Quit", quit),
        ];
        _menu = new TrayMenu(_items);

        _icon = new WinForms.NotifyIcon
        {
            Icon = _own ?? System.Drawing.SystemIcons.Application,
            Text = "Beacon",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                open();
            }
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                // The words on the icon are made again each time the menu is asked for.
                menuOpening();
                _menu.ShowAt(WinForms.Cursor.Position);
            }
        };
    }

    /// <summary>
    /// Puts "Update to" and the version at the top of the menu, for an update that has been
    /// found. Call on the UI thread. The words are fixed but for the version number.
    /// </summary>
    public void ShowUpdate(string version, Action update)
    {
        var old = _menu;
        _menu = new TrayMenu([new TrayMenuItem("\uE896", "Update to " + version, update), TrayMenuItem.Rule, .. _items]);
        old.Close();
    }

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.Info);

    /// <summary>
    /// Changes the words shown when the pointer rests on the icon: what Beacon is doing about
    /// the clipboard and, when sync is set up, how sync stands. Windows shows at most 127
    /// characters there and the icon refuses more, so the caller keeps them short and fixed.
    /// </summary>
    public void SetText(string text) => _icon.Text = text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Hide();
        _own?.Dispose();
    }

    // The version of this build, as three numbers.
    private static string Version() => typeof(TrayIcon).Assembly.GetName().Version?.ToString(3) ?? "";

    // The icon is packed into the program and read from memory, in the size the notification
    // area shows at this screen's scaling: no file is opened for it. An icon that cannot be
    // read must not keep Beacon from starting.
    private static System.Drawing.Icon? OwnIcon()
    {
        try
        {
            using var packed = typeof(TrayIcon).Assembly.GetManifestResourceStream("Beacon.ico");
            return packed is null ? null : new System.Drawing.Icon(packed, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
