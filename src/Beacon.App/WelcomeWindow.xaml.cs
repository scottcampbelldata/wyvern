using System.IO;
using System.Windows;
using System.Windows.Input;
using Beacon.Core.Settings;

namespace Beacon.App;

/// <summary>
/// The window Beacon shows the first time it runs on a PC: the launcher's hotkey and the
/// clipboard history's, each set by pressing it. Get Started writes the two to settings.json
/// and has Beacon read the file again, as Save in the settings window does. Not Now changes
/// nothing: the launcher keeps its hotkey, and the clipboard history has none until one is set
/// under Settings.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly string _path;
    private readonly Action _reload;
    private readonly SettingsDocument? _document;

    /// <param name="settingsFile">The full path of settings.json.</param>
    /// <param name="reload">Has Beacon read settings.json again. It runs on the UI thread and does not throw.</param>
    public WelcomeWindow(string settingsFile, Action reload)
    {
        InitializeComponent();
        LauncherLook.Apply(this, LauncherLook.IsLight());
        _path = settingsFile;
        _reload = reload;
        _document = SettingsDocument.Read(settingsFile);

        // What the file has, and for the clipboard history, which has none yet, a suggestion.
        HotkeyBox.Text = _document?.Text("hotkey", BeaconSettings.DefaultHotkey) ?? BeaconSettings.DefaultHotkey;
        string history = _document?.Text("clipboardHotkey") ?? "";
        ClipHotkeyBox.Text = history.Length > 0 ? history : Welcome.SuggestedClipboardHotkey;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e) => HotkeyRecorder.OnKeyDown(sender, e, words => Status.Text = words);

    private void OnSkip(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            // Writing the file would throw away what is in it.
            Status.Text = "settings.json cannot be read, so nothing was changed. Set the hotkeys under Settings once it is put right.";
            return;
        }

        string hotkey = HotkeyBox.Text.Trim();
        string history = ClipHotkeyBox.Text.Trim();
        if (!Hotkey.TryParse(hotkey, out _))
        {
            Status.Text = "Beacon needs a hotkey. Click the first box and press one.";
            return;
        }

        if (history.Length > 0 && string.Equals(history, hotkey, StringComparison.OrdinalIgnoreCase))
        {
            Status.Text = "The clipboard history needs a hotkey of its own, not the launcher's.";
            return;
        }

        _document.Set("hotkey", hotkey);
        _document.Set("clipboardHotkey", history);
        try
        {
            _document.Write(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "settings.json could not be written, so nothing was changed.";
            return;
        }

        // The same as the Reload command: whatever cannot be used is said in a notification.
        _reload();
        Close();
    }
}
