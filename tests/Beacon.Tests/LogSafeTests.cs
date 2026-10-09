using Beacon.Core.Diagnostics;

namespace Beacon.Tests;

public class LogSafeTests
{
    [Theory]
    [InlineData("snippet:sig.md", "snippet:(path withheld)")]
    [InlineData(@"snippet:work\Email signature.md", "snippet:(path withheld)")]
    [InlineData("SNIPPET:sig.md", "snippet:(path withheld)")]
    [InlineData("Snippet:", "snippet:(path withheld)")]
    public void Snippet_ids_are_withheld_because_they_hold_a_file_name(string itemId, string expected)
    {
        Assert.Equal(expected, LogSafe.ItemId(itemId));
    }

    [Theory]
    [InlineData("script:backup.ps1", "script:(path withheld)")]
    [InlineData(@"script:docker\Restart Docker.cmd", "script:(path withheld)")]
    [InlineData("SCRIPT:backup.ps1", "script:(path withheld)")]
    [InlineData("Script:", "script:(path withheld)")]
    public void Script_ids_are_withheld_because_they_hold_a_file_name(string itemId, string expected)
    {
        Assert.Equal(expected, LogSafe.ItemId(itemId));
    }

    [Theory]
    [InlineData("quicklink:Search GitHub", "quicklink:(name withheld)")]
    [InlineData("quicklink:My bank", "quicklink:(name withheld)")]
    [InlineData("QuickLink:My bank", "quicklink:(name withheld)")]
    [InlineData("quicklink:", "quicklink:(name withheld)")]
    public void Quicklink_ids_are_withheld_because_they_hold_the_owners_own_name_for_the_link(string itemId, string expected)
    {
        Assert.Equal(expected, LogSafe.ItemId(itemId));
    }

    [Theory]
    [InlineData("clipboard:0b7f3c1e-5a2d-4e8b-9c41-7d2a6f0e1b93", "clipboard:(id withheld)")]
    [InlineData("Clipboard:0b7f3c1e-5a2d-4e8b-9c41-7d2a6f0e1b93", "clipboard:(id withheld)")]
    [InlineData("clipboard:", "clipboard:(id withheld)")]
    public void Clipboard_entry_ids_are_withheld_because_they_name_one_thing_the_owner_copied(string itemId, string expected)
    {
        Assert.Equal(expected, LogSafe.ItemId(itemId));
    }

    [Theory]
    [InlineData("command:new-snippet")]
    [InlineData("command:edit-quicklinks")]
    [InlineData("app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void Fixed_identifiers_are_still_logged_as_they_are(string itemId)
    {
        Assert.Equal(itemId, LogSafe.ItemId(itemId));
    }
}
