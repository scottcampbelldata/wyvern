using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>When Beacon shows its welcome window.</summary>
public class WelcomeTests
{
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(false, "   ", true)]
    [InlineData(false, "Ctrl+Shift+V", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "Ctrl+Shift+V", false)]
    public void It_is_shown_once_and_only_while_the_clipboard_history_has_no_hotkey(bool shownBefore, string? clipboardHotkey, bool due) =>
        Assert.Equal(due, Welcome.IsDue(shownBefore, clipboardHotkey));

    [Fact]
    public void The_hotkey_it_suggests_is_one_beacon_understands_and_not_the_launcher_s()
    {
        Assert.True(Hotkey.TryParse(Welcome.SuggestedClipboardHotkey, out var suggested));
        Assert.NotEqual(Hotkey.Default, suggested);
        Assert.NotEqual(BeaconSettings.DefaultHotkey, Welcome.SuggestedClipboardHotkey);
    }

    [Fact]
    public void The_file_that_remembers_it_is_beside_the_settings_and_says_what_it_is()
    {
        var paths = new BeaconPaths(@"C:\made-up\Beacon");

        Assert.Equal(@"C:\made-up\Beacon\welcomed", paths.WelcomedFile);
        Assert.EndsWith("\n", Welcome.MarkerText);
        Assert.Contains("welcome", Welcome.MarkerText);
    }
}
