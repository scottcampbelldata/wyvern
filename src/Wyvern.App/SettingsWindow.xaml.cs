using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wyvern.Core.Files;
using Wyvern.Core.Settings;
using Microsoft.Win32;

namespace Wyvern.App;

/// <summary>What the settings window asks the rest of Wyvern to do. Each runs on the UI thread and does not throw.</summary>
public sealed record SettingsActions(
    Action Reload,
    Action OpenClipboard,
    Action NewSnippet,
    Action OpenLibraryFolder,
    Action OpenSyncSetup,
    Action SyncNow,
    Action OpenSettingsFile,
    Action OpenLogFolder);

/// <summary>
/// The settings, as a window: the hotkeys, starting with Windows, the folders, the clipboard
/// history, snippets and sync. It reads settings.json when it opens and, on Save, writes back
/// only what was changed and has Wyvern read the file again, as the Reload command does. What
/// a value means, and what is said when one cannot be used, stays where it always was.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string DefaultHotkey = "Alt+Space";
    private const int DefaultRetentionDays = 90;
    private const int DefaultSyncMinutes = 5;

    private readonly string _path;
    private readonly SettingsActions _actions;
    private readonly SettingsDocument? _document;

    // What the fields held when the window opened, so that only what was changed is written.
    private readonly string _hotkey = "";
    private readonly string _clipHotkey = "";
    private readonly bool _start;
    private readonly bool _glass;
    private readonly bool _confettiClickThrough;
    private readonly Appearance _appearance;
    private readonly string _device = "";
    private readonly string _dataDir = "";
    private readonly int _retention;
    private readonly bool _autoExpand;
    private readonly int _syncMinutes;
    private bool _foldersChanged;

    // The hotkeys the commands had when the window opened, by the command's short name, and
    // the box of each command that is shown.
    private readonly Dictionary<string, string> _commandHotkeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(HotkeyChoice Command, TextBox Box)> _hotkeyBoxes = [];

    /// <param name="hotkeyCommands">The commands that can be given a hotkey of their own. Left out by code that has none to show.</param>
    public SettingsWindow(string settingsFile, SettingsActions actions, string version, IReadOnlyList<HotkeyChoice>? hotkeyCommands = null)
    {
        InitializeComponent();
        LauncherLook.Apply(this, LauncherLook.IsLight());
        _path = settingsFile;
        _actions = actions;
        VersionText.Text = "Version " + version;

        _document = SettingsDocument.Read(settingsFile);
        if (_document is null)
        {
            // Writing the file would throw away what is in it, so nothing can be saved.
            Status.Text = "settings.json cannot be read, so nothing can be changed here. Open it under About, put it right, and open Settings again.";
            SaveButton.IsEnabled = false;
            return;
        }

        _hotkey = _document.Text("hotkey", DefaultHotkey);
        _clipHotkey = _document.Text("clipboardHotkey");
        _start = _document.Flag("startWithWindows", true);
        _glass = _document.Flag("launcherGlass", true);
        _confettiClickThrough = _document.Flag("confettiClickThrough", false);
        _device = _document.Text("deviceName");
        _dataDir = _document.Text("dataDir");
        _retention = _document.Number("clipboardRetentionDays", DefaultRetentionDays);
        _autoExpand = _document.Flag("snippetAutoExpand", false);
        _syncMinutes = _document.Number("syncIntervalMinutes", DefaultSyncMinutes);

        HotkeyBox.Text = _hotkey;
        ClipHotkeyBox.Text = _clipHotkey;
        StartBox.IsChecked = _start;
        GlassBox.IsChecked = _glass;
        ConfettiBox.IsChecked = _confettiClickThrough;

        // Light or dark: a value the file has that is none of the three is shown as following
        // Windows, which is what Wyvern does with it.
        _appearance = AppearanceOptions.Parse(_document.Text(AppearanceOptions.SettingName)) ?? Appearance.System;
        (_appearance switch
        {
            Appearance.Light => LookLight,
            Appearance.Dark => LookDark,
            _ => LookSystem,
        }).IsChecked = true;
        DeviceBox.Text = _device;
        DataDirBox.Text = _dataDir;
        RetentionBox.Text = _retention.ToString(CultureInfo.InvariantCulture);
        AutoExpandBox.IsChecked = _autoExpand;
        SyncIntervalBox.Text = _syncMinutes.ToString(CultureInfo.InvariantCulture);
        foreach (string folder in _document.List("indexedFolders") ?? FileIndexOptions.DefaultFolders())
        {
            FoldersList.Items.Add(folder);
        }

        foreach (var (command, keys) in _document.Map(CommandHotkeyOptions.SettingName) ?? new Dictionary<string, string>())
        {
            _commandHotkeys[command] = keys.Trim();
        }

        ShowHotkeyRows(hotkeyCommands ?? []);
    }

    // One row for each command: its name, the part of Wyvern it belongs to in grey, and a box
    // that takes a hotkey as the two boxes on the other pages do. The commands stand by their
    // part and then by their name.
    private void ShowHotkeyRows(IReadOnlyList<HotkeyChoice> commands)
    {
        var rows = commands
            .Select(command => (Command: command, Part: LauncherLook.PartOfCommand(command.Id)))
            .OrderBy(row => row.Part, StringComparer.Ordinal)
            .ThenBy(row => row.Command.Name, StringComparer.Ordinal);
        foreach (var (command, part) in rows)
        {
            var name = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Text = command.Name };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var group = new TextBlock { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0), Text = part };
            group.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var words = new StackPanel { Orientation = Orientation.Horizontal };
            words.Children.Add(name);
            words.Children.Add(group);

            var box = new TextBox
            {
                Style = (Style)FindResource("Field"),
                Width = 190,
                IsReadOnly = true,
                Text = _commandHotkeys.GetValueOrDefault(command.Id, ""),
            };
            box.PreviewKeyDown += OnHotkeyKeyDown;
            DockPanel.SetDock(box, Dock.Right);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            row.Children.Add(box);
            row.Children.Add(words);
            HotkeyRows.Children.Add(row);
            _hotkeyBoxes.Add((command, box));
        }
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        // Raised for the first tab while the window is still being made.
        if (PageAbout is null)
        {
            return;
        }

        PageGeneral.Visibility = Shown(TabGeneral);
        PageAppearance.Visibility = Shown(TabAppearance);
        PageClipboard.Visibility = Shown(TabClipboard);
        PageHotkeys.Visibility = Shown(TabHotkeys);
        PageSnippets.Visibility = Shown(TabSnippets);
        PageFiles.Visibility = Shown(TabFiles);
        PageSync.Visibility = Shown(TabSync);
        PageAbout.Visibility = Shown(TabAbout);
    }

    private static Visibility Shown(RadioButton tab) => tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // Which of the three is chosen.
    private Appearance ChosenLook() =>
        LookLight.IsChecked == true ? Appearance.Light
        : LookDark.IsChecked == true ? Appearance.Dark
        : Appearance.System;

    // A choice of light or dark is shown at once in this window, so that it is seen before
    // it is saved. The rest of Wyvern follows at Save.
    private void OnLookChecked(object sender, RoutedEventArgs e)
    {
        // Raised for the one that is chosen while the window is still being made.
        if (LookDark is null || LookLight is null || LookSystem is null)
        {
            return;
        }

        LauncherLook.Apply(this, ChosenLook() switch
        {
            Appearance.Light => true,
            Appearance.Dark => false,
            _ => LauncherLook.WindowsIsLight(),
        });
    }

    // A hotkey is set by pressing it: see HotkeyRecorder, which every window that asks for one uses.
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e) => HotkeyRecorder.OnKeyDown(sender, e, words => Status.Text = words);

    private void OnBrowseDataDir(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is { } folder)
        {
            DataDirBox.Text = folder;
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is { } folder && !FoldersList.Items.Cast<string>().Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            FoldersList.Items.Add(folder);
            _foldersChanged = true;
        }
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedIndex >= 0)
        {
            FoldersList.Items.RemoveAt(FoldersList.SelectedIndex);
            _foldersChanged = true;
        }
    }

    private string? PickFolder()
    {
        var dialog = new OpenFolderDialog();
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void OnOpenClipboard(object sender, RoutedEventArgs e) => _actions.OpenClipboard();

    private void OnNewSnippet(object sender, RoutedEventArgs e) => _actions.NewSnippet();

    private void OnOpenLibrary(object sender, RoutedEventArgs e) => _actions.OpenLibraryFolder();

    private void OnSyncSetup(object sender, RoutedEventArgs e) => _actions.OpenSyncSetup();

    private void OnSyncNow(object sender, RoutedEventArgs e) => _actions.SyncNow();

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e) => _actions.OpenSettingsFile();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => _actions.OpenLogFolder();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // Writes what was changed and has Wyvern read the file again. What cannot be used is said
    // here and nothing is written.
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        string hotkey = HotkeyBox.Text.Trim();
        string clipHotkey = ClipHotkeyBox.Text.Trim();
        if (!Hotkey.TryParse(hotkey, out _))
        {
            Say(TabGeneral, "Wyvern needs a hotkey. Click the box and press one.");
            return;
        }

        if (clipHotkey.Length > 0 && string.Equals(clipHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
        {
            Say(TabClipboard, "The clipboard history needs a hotkey of its own, not the launcher's.");
            return;
        }

        if (!int.TryParse(RetentionBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int retention) || retention is < 1 or > 3650)
        {
            Say(TabClipboard, "The history is kept for a whole number of days, from 1 to 3650.");
            return;
        }

        if (!int.TryParse(SyncIntervalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes is < 1 or > 1440)
        {
            Say(TabSync, "Sync runs every whole number of minutes, from 1 to 1440.");
            return;
        }

        // Every hotkey has a key of its own: the launcher's, the clipboard history's and each
        // command's. What settings.json gives to a command that is not shown here is kept.
        var commandHotkeys = new Dictionary<string, string>(_commandHotkeys, StringComparer.OrdinalIgnoreCase);
        var keysInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hotkey };
        if (clipHotkey.Length > 0)
        {
            keysInUse.Add(clipHotkey);
        }

        foreach (var (command, box) in _hotkeyBoxes)
        {
            string keys = box.Text.Trim();
            if (keys.Length == 0)
            {
                commandHotkeys.Remove(command.Id);
                continue;
            }

            if (!keysInUse.Add(keys))
            {
                Say(TabHotkeys, $"{keys} is set for two things. Each needs a hotkey of its own.");
                return;
            }

            commandHotkeys[command.Id] = keys;
        }

        if (hotkey != _hotkey)
        {
            _document.Set("hotkey", hotkey);
        }

        if (commandHotkeys.Count != _commandHotkeys.Count || commandHotkeys.Any(one => !_commandHotkeys.TryGetValue(one.Key, out string? before) || before != one.Value))
        {
            _document.SetMap(CommandHotkeyOptions.SettingName, commandHotkeys);
        }

        if (clipHotkey != _clipHotkey)
        {
            _document.Set("clipboardHotkey", clipHotkey);
        }

        if (StartBox.IsChecked == true != _start)
        {
            _document.Set("startWithWindows", StartBox.IsChecked == true);
        }

        if (GlassBox.IsChecked == true != _glass)
        {
            _document.Set("launcherGlass", GlassBox.IsChecked == true);
        }

        if (ConfettiBox.IsChecked == true != _confettiClickThrough)
        {
            _document.Set("confettiClickThrough", ConfettiBox.IsChecked == true);
        }

        if (ChosenLook() != _appearance)
        {
            // Following Windows is what Wyvern does without the setting, so it is taken out.
            _document.Set(AppearanceOptions.SettingName, ChosenLook() == Appearance.System ? null : AppearanceOptions.Text(ChosenLook()));
        }

        if (DeviceBox.Text.Trim() != _device)
        {
            _document.Set("deviceName", DeviceBox.Text);
        }

        if (DataDirBox.Text.Trim() != _dataDir)
        {
            _document.Set("dataDir", DataDirBox.Text);
        }

        if (retention != _retention)
        {
            _document.Set("clipboardRetentionDays", retention);
        }

        if (AutoExpandBox.IsChecked == true != _autoExpand)
        {
            _document.Set("snippetAutoExpand", AutoExpandBox.IsChecked == true);
        }

        if (minutes != _syncMinutes)
        {
            _document.Set("syncIntervalMinutes", minutes);
        }

        if (_foldersChanged)
        {
            _document.Set("indexedFolders", [.. FoldersList.Items.Cast<string>()]);
        }

        try
        {
            _document.Write(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "settings.json could not be written, so nothing was changed.";
            return;
        }

        // The same as the Reload command: the file is read again and whatever cannot be used
        // is said in a notification, as it always was.
        _actions.Reload();
        Close();
    }

    private void Say(RadioButton tab, string words)
    {
        tab.IsChecked = true;
        Status.Text = words;
    }
}
