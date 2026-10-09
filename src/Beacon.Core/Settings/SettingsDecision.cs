using Beacon.Core.AutoExpand;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Files;
using Beacon.Core.Scripts;
using Beacon.Core.Sync;

namespace Beacon.Core.Settings;

/// <summary>
/// What the app should do after reading settings.json. The rules live here, free of Windows
/// calls, so they can be unit tested.
/// </summary>
/// <param name="Hotkey">The hotkey to register, or null to leave the current registration alone.</param>
/// <param name="HotkeyText">How <paramref name="Hotkey"/> is written, for messages. Null when it is null.</param>
/// <param name="StartWithWindows">The value to apply to the start-with-Windows entry, or null to leave it alone.</param>
/// <param name="HotkeyInvalid">True when settings.json held hotkey text that could not be parsed.</param>
/// <param name="Notes">Sentences to show the owner. They can quote the file, so they must never be logged.</param>
/// <param name="Files">The folders to index, or null to leave the file index as it is.</param>
/// <param name="DataDir">Where the data folder is, or null to leave the library where it is.</param>
public sealed record SettingsDecision(
    Hotkey? Hotkey,
    string? HotkeyText,
    bool? StartWithWindows,
    bool HotkeyInvalid,
    IReadOnlyList<string> Notes,
    FileIndexOptions? Files,
    DataDirSetting? DataDir)
{
    /// <summary>
    /// How scripts are to be run, or null to go on running them as before. It is null whenever
    /// settings.json could not be read.
    /// </summary>
    public ScriptOptions? Scripts { get; init; }

    /// <summary>
    /// The settings for clipboard history, or null to leave them as they are. It is null
    /// whenever settings.json could not be read.
    /// </summary>
    public ClipboardOptions? Clipboard { get; init; }

    /// <summary>
    /// How often sync runs, or null to leave that as it is. It is null whenever settings.json
    /// could not be read.
    /// </summary>
    public SyncOptions? Sync { get; init; }

    /// <summary>
    /// Whether snippet auto-expand is on, or null to leave it as it is. It is null whenever
    /// settings.json could not be read: at startup that leaves it off, and on Reload it stays
    /// as the owner last had it.
    /// </summary>
    public AutoExpandOptions? AutoExpand { get; init; }

    /// <param name="loaded">The result of reading settings.json.</param>
    /// <param name="firstLoad">True at startup, when there are no earlier settings to keep.</param>
    public static SettingsDecision From(SettingsLoadResult loaded, bool firstLoad)
    {
        if (loaded.Error is not null)
        {
            // loaded.Settings holds defaults standing in for a file that could not be read.
            // Applying them would undo choices the owner made, such as turning off
            // start-with-Windows, choosing which folders are indexed, moving the data folder
            // or naming the program that runs a script, so only what startup cannot do without
            // is taken from them.
            return firstLoad
                ? new SettingsDecision(
                    Settings.Hotkey.Default,
                    BeaconSettings.DefaultHotkey,
                    StartWithWindows: null,
                    HotkeyInvalid: false,
                    [
                        loaded.Error + ".",
                        $"Using {BeaconSettings.DefaultHotkey} until it is fixed. Files are not indexed, the library is not loaded and nothing you copy is recorded until then.",
                    ],
                    Files: null,
                    DataDir: null)
                : new SettingsDecision(
                    Hotkey: null,
                    HotkeyText: null,
                    StartWithWindows: null,
                    HotkeyInvalid: false,
                    [loaded.Error + ".", "Keeping the settings already in use."],
                    Files: null,
                    DataDir: null);
        }

        var files = FileIndexOptions.From(loaded.Settings);
        var dataDir = DataDirSetting.From(loaded.Settings);
        var scripts = ScriptOptions.From(loaded.Settings);
        var clipboard = ClipboardOptions.From(loaded.Settings);
        var sync = SyncOptions.From(loaded.Settings);
        var autoExpand = AutoExpandOptions.From(loaded.Settings);
        string text = loaded.Settings.Hotkey;
        if (Settings.Hotkey.TryParse(text, out var parsed))
        {
            return new SettingsDecision(parsed, text, loaded.Settings.StartWithWindows, HotkeyInvalid: false, [], files, dataDir)
            {
                Scripts = scripts,
                Clipboard = clipboard,
                Sync = sync,
                AutoExpand = autoExpand,
            };
        }

        string problem = $"\"{text}\" is not a hotkey Beacon understands.";
        return firstLoad
            ? new SettingsDecision(
                Settings.Hotkey.Default,
                BeaconSettings.DefaultHotkey,
                loaded.Settings.StartWithWindows,
                HotkeyInvalid: true,
                [problem, $"Using {BeaconSettings.DefaultHotkey}."],
                files,
                dataDir)
            {
                Scripts = scripts,
                Clipboard = clipboard,
                Sync = sync,
                AutoExpand = autoExpand,
            }
            : new SettingsDecision(
                Hotkey: null,
                HotkeyText: null,
                loaded.Settings.StartWithWindows,
                HotkeyInvalid: true,
                [problem, "Keeping the current hotkey."],
                files,
                dataDir)
            {
                Scripts = scripts,
                Clipboard = clipboard,
                Sync = sync,
                AutoExpand = autoExpand,
            };
    }
}
