using Beacon.Core.Settings;

namespace Beacon.Tests;

public class SettingsDecisionTests
{
    private static SettingsLoadResult Loaded(string hotkey, bool startWithWindows) =>
        new(new BeaconSettings { Hotkey = hotkey, StartWithWindows = startWithWindows }, null);

    private static SettingsLoadResult Failed() =>
        new(new BeaconSettings(), "settings.json is not valid JSON (line 3)", new FormatException("line 3"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Valid_settings_are_applied_as_written(bool firstLoad)
    {
        var decision = SettingsDecision.From(Loaded("Ctrl+Shift+K", startWithWindows: false), firstLoad);

        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4B), decision.Hotkey);
        Assert.Equal("Ctrl+Shift+K", decision.HotkeyText);
        Assert.False(decision.StartWithWindows);
        Assert.False(decision.HotkeyInvalid);
        Assert.Empty(decision.Notes);
        Assert.NotNull(decision.Files);
        Assert.Equal(new DataDirSetting(null, null), decision.DataDir);
    }

    [Fact]
    public void Indexed_folders_come_from_the_settings()
    {
        var loaded = new SettingsLoadResult(new BeaconSettings { IndexedFolders = [@"C:\Work"], ExcludedFolderNames = ["target"] }, null);

        var decision = SettingsDecision.From(loaded, firstLoad: false);

        Assert.NotNull(decision.Files);
        Assert.Equal([@"C:\Work"], decision.Files.Folders);
        Assert.Equal(["target"], decision.Files.ExcludedNames);
    }

    [Fact]
    public void The_data_folder_comes_from_the_settings()
    {
        var loaded = new SettingsLoadResult(new BeaconSettings { DataDir = @"D:\Beacon data" }, null);

        var decision = SettingsDecision.From(loaded, firstLoad: false);

        Assert.Equal(new DataDirSetting(@"D:\Beacon data", null), decision.DataDir);
    }

    [Fact]
    public void A_data_folder_that_is_the_root_of_a_drive_is_handed_on_as_refused_like_any_other_that_cannot_be_used()
    {
        // The shell says so to the owner from Rejected and uses the default folder: the same
        // way as for a path that is not a full path. Path text only, nothing on any drive is read.
        var loaded = new SettingsLoadResult(new BeaconSettings { DataDir = @"D:\" }, null);

        var decision = SettingsDecision.From(loaded, firstLoad: false);

        Assert.Equal(new DataDirSetting(null, @"D:\"), decision.DataDir);
    }

    [Fact]
    public void Unreadable_file_at_startup_uses_the_default_hotkey_and_leaves_startup_files_and_the_library_alone()
    {
        var decision = SettingsDecision.From(Failed(), firstLoad: true);

        Assert.Equal(Hotkey.Default, decision.Hotkey);
        Assert.Equal("Alt+Space", decision.HotkeyText);
        Assert.Null(decision.StartWithWindows);
        Assert.Null(decision.Files);
        Assert.Null(decision.DataDir);
        Assert.False(decision.HotkeyInvalid);
        Assert.Equal(
            [
                "settings.json is not valid JSON (line 3).",
                "Using Alt+Space until it is fixed. Files are not indexed, the library is not loaded and nothing you copy is recorded until then.",
            ],
            decision.Notes);
    }

    [Fact]
    public void Unreadable_file_on_reload_changes_nothing()
    {
        var decision = SettingsDecision.From(Failed(), firstLoad: false);

        Assert.Null(decision.Hotkey);
        Assert.Null(decision.HotkeyText);
        Assert.Null(decision.StartWithWindows);
        Assert.Null(decision.Files);
        Assert.Null(decision.DataDir);
        Assert.Equal(["settings.json is not valid JSON (line 3).", "Keeping the settings already in use."], decision.Notes);
    }

    [Fact]
    public void Invalid_hotkey_at_startup_falls_back_to_the_default_but_still_applies_the_rest()
    {
        var decision = SettingsDecision.From(Loaded("Banana", startWithWindows: false), firstLoad: true);

        Assert.Equal(Hotkey.Default, decision.Hotkey);
        Assert.Equal("Alt+Space", decision.HotkeyText);
        Assert.False(decision.StartWithWindows);
        Assert.True(decision.HotkeyInvalid);
        Assert.NotNull(decision.Files);
        Assert.NotNull(decision.DataDir);
        Assert.Equal(["\"Banana\" is not a hotkey Beacon understands.", "Using Alt+Space."], decision.Notes);
    }

    [Fact]
    public void Invalid_hotkey_on_reload_keeps_the_current_hotkey_but_still_applies_the_rest()
    {
        var decision = SettingsDecision.From(Loaded("Banana", startWithWindows: true), firstLoad: false);

        Assert.Null(decision.Hotkey);
        Assert.Null(decision.HotkeyText);
        Assert.True(decision.StartWithWindows);
        Assert.True(decision.HotkeyInvalid);
        Assert.NotNull(decision.Files);
        Assert.NotNull(decision.DataDir);
        Assert.Equal(["\"Banana\" is not a hotkey Beacon understands.", "Keeping the current hotkey."], decision.Notes);
    }
}
