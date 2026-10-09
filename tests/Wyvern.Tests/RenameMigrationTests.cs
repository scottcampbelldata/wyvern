using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public sealed class RenameMigrationTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string Old => _dir.File("Beacon");

    private string New => _dir.File("Wyvern");

    public void Dispose() => _dir.Dispose();

    private void Put(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void The_old_folder_is_copied_once_and_left_in_place()
    {
        Put(Old, "settings.json", "{ \"hotkey\": \"Alt+Space\" }");
        Put(Old, Path.Combine("data", "library", "snippets", "sig.txt"), "Regards");
        Put(Old, Path.Combine("data", ClipStore.LockFileName), "");
        Put(Old, Path.Combine("updates", "BeaconSetup-1.8.1.exe"), "setup");

        Assert.True(RenameMigration.Run(Old, New));

        Assert.Equal("{ \"hotkey\": \"Alt+Space\" }", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("Regards", File.ReadAllText(Path.Combine(New, "data", "library", "snippets", "sig.txt")));
        Assert.False(File.Exists(Path.Combine(New, "data", ClipStore.LockFileName)));
        Assert.False(Directory.Exists(Path.Combine(New, "updates")));
        Assert.False(Directory.Exists(New + ".copying"));
        Assert.True(File.Exists(Path.Combine(Old, "settings.json")));

        // Once the new folder is there, nothing is copied again.
        Put(Old, "settings.json", "changed");
        Assert.False(RenameMigration.Run(Old, New));
        Assert.Equal("{ \"hotkey\": \"Alt+Space\" }", File.ReadAllText(Path.Combine(New, "settings.json")));
    }

    [Fact]
    public void Nothing_happens_without_an_old_folder()
    {
        Assert.False(RenameMigration.Run(Old, New));
        Assert.False(Directory.Exists(New));
    }
}
