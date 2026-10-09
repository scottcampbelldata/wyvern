using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>When Wyvern shows its welcome window.</summary>
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
    public void The_hotkey_it_suggests_is_one_wyvern_understands_and_not_the_launcher_s()
    {
        Assert.True(Hotkey.TryParse(Welcome.SuggestedClipboardHotkey, out var suggested));
        Assert.NotEqual(Hotkey.Default, suggested);
        Assert.NotEqual(WyvernSettings.DefaultHotkey, Welcome.SuggestedClipboardHotkey);
    }

    [Fact]
    public void The_file_that_remembers_it_is_beside_the_settings_and_says_what_it_is()
    {
        var paths = new WyvernPaths(@"C:\made-up\Wyvern");

        Assert.Equal(@"C:\made-up\Wyvern\welcomed", paths.WelcomedFile);
        Assert.EndsWith("\n", Welcome.MarkerText);
        Assert.Contains("welcome", Welcome.MarkerText);
    }
}
