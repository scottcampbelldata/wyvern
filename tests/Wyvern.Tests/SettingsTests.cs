using Wyvern.Core.Files;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.File("settings.json");

    [Fact]
    public void Missing_file_is_created_with_defaults()
    {
        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Equal("Alt+Space", result.Settings.Hotkey);
        Assert.True(result.Settings.StartWithWindows);

        string written = File.ReadAllText(SettingsPath);
        Assert.Contains("\"hotkey\": \"Alt+Space\"", written);
        Assert.Contains("\"startWithWindows\": true", written);
    }

    [Fact]
    public void The_defaults_file_leaves_the_data_folder_out_so_the_default_follows_the_root()
    {
        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Settings.DataDir);
        Assert.DoesNotContain("dataDir", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Reads_the_data_folder()
    {
        File.WriteAllText(SettingsPath, """{ "dataDir": "D:\\Wyvern data" }""");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Equal(@"D:\Wyvern data", result.Settings.DataDir);
    }

    [Fact]
    public void The_defaults_file_lists_the_indexed_folders_and_excluded_names_in_full()
    {
        var result = SettingsStore.Load(SettingsPath);

        Assert.Equal(FileIndexOptions.DefaultFolders(), result.Settings.IndexedFolders);
        Assert.Equal(FileIndexOptions.DefaultExcludedNames, result.Settings.ExcludedFolderNames);

        // Reading the file back gives the same lists, so what was written is what is used.
        var reread = SettingsStore.Load(SettingsPath);
        Assert.Equal(FileIndexOptions.DefaultFolders(), reread.Settings.IndexedFolders);
        Assert.Contains("\"node_modules\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_file_without_the_folder_keys_leaves_them_unset()
    {
        File.WriteAllText(SettingsPath, """{ "hotkey": "Win+1" }""");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Settings.IndexedFolders);
        Assert.Null(result.Settings.ExcludedFolderNames);
    }

    [Fact]
    public void Reads_indexed_folders_and_excluded_names()
    {
        File.WriteAllText(SettingsPath, """
            {
              "indexedFolders": ["C:\\Work", "D:\\Photos"],
              "excludedFolderNames": ["target", ".cache"]
            }
            """);

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Equal([@"C:\Work", @"D:\Photos"], result.Settings.IndexedFolders);
        Assert.Equal(["target", ".cache"], result.Settings.ExcludedFolderNames);
    }

    [Fact]
    public void Creates_the_folder_when_it_does_not_exist()
    {
        string nested = Path.Combine(_dir.Path, "Wyvern", "settings.json");

        SettingsStore.Load(nested);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Reads_values_from_an_existing_file()
    {
        File.WriteAllText(SettingsPath, """{ "hotkey": "Ctrl+Shift+K", "startWithWindows": false }""");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Null(result.Cause);
        Assert.Equal("Ctrl+Shift+K", result.Settings.Hotkey);
        Assert.False(result.Settings.StartWithWindows);
    }

    [Fact]
    public void Key_names_are_matched_without_regard_to_case()
    {
        File.WriteAllText(SettingsPath, """{ "Hotkey": "Win+1", "STARTWITHWINDOWS": false }""");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Equal("Win+1", result.Settings.Hotkey);
        Assert.False(result.Settings.StartWithWindows);
    }

    [Fact]
    public void Missing_or_null_keys_fall_back_to_defaults()
    {
        File.WriteAllText(SettingsPath, """{ "hotkey": null }""");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Equal("Alt+Space", result.Settings.Hotkey);
        Assert.True(result.Settings.StartWithWindows);
    }

    [Fact]
    public void Comments_trailing_commas_and_unknown_keys_are_tolerated()
    {
        File.WriteAllText(SettingsPath, """
            {
              // my launcher key
              "hotkey": "Win+1",
              "aKeyWyvernWillNeverHave": ["C:\\dev"],
            }
            """);

        var result = SettingsStore.Load(SettingsPath);

        Assert.Null(result.Error);
        Assert.Equal("Win+1", result.Settings.Hotkey);
    }

    [Fact]
    public void Invalid_json_returns_defaults_with_an_error_and_leaves_the_file_alone()
    {
        File.WriteAllText(SettingsPath, "{ \"hotkey\": ");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Equal("settings.json is not valid JSON (line 1)", result.Error);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(result.Cause);
        Assert.Equal("Alt+Space", result.Settings.Hotkey);
        Assert.Equal("{ \"hotkey\": ", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void The_error_gives_the_line_and_nothing_the_parser_said()
    {
        // The mistake is on the third line. The parser's own message would quote the file.
        File.WriteAllText(SettingsPath, "{\n  // my launcher key\n  \"hotkey\": \"Win+1\" \"private words\"\n}");

        var result = SettingsStore.Load(SettingsPath);

        Assert.Equal("settings.json is not valid JSON (line 3)", result.Error);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(result.Cause);
    }

    [Fact]
    public void File_locked_by_another_program_returns_defaults_with_an_error()
    {
        File.WriteAllText(SettingsPath, """{ "hotkey": "Win+1" }""");
        using var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = SettingsStore.Load(SettingsPath);

        Assert.Equal("settings.json could not be read or created", result.Error);
        Assert.IsAssignableFrom<IOException>(result.Cause);
        Assert.Equal("Alt+Space", result.Settings.Hotkey);
    }

    [Fact]
    public void Defaults_that_cannot_be_written_are_still_returned_with_an_error()
    {
        // The settings folder cannot be created because a file already has its name.
        File.WriteAllText(_dir.File("blocker"), "");

        var result = SettingsStore.Load(Path.Combine(_dir.File("blocker"), "settings.json"));

        Assert.Equal("settings.json could not be read or created", result.Error);
        Assert.Equal("Alt+Space", result.Settings.Hotkey);
    }

    [Fact]
    public void Paths_are_laid_out_under_the_root()
    {
        var paths = new WyvernPaths(@"C:\Data\Wyvern");

        Assert.Equal(@"C:\Data\Wyvern\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\Data\Wyvern\usage.json", paths.UsageFile);
        Assert.Equal(@"C:\Data\Wyvern\logs\wyvern.log", paths.LogFile);
        Assert.Equal(@"C:\Data\Wyvern\data", paths.DefaultDataDir);
    }

    [Fact]
    public void Default_root_is_local_app_data_unless_WYVERN_HOME_is_set()
    {
        string? original = Environment.GetEnvironmentVariable(WyvernPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(WyvernPaths.HomeVariable, null);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(localAppData, "Wyvern"), WyvernPaths.Default().Root);

            Environment.SetEnvironmentVariable(WyvernPaths.HomeVariable, @"C:\Scratch\Wyvern");
            Assert.Equal(@"C:\Scratch\Wyvern", WyvernPaths.Default().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WyvernPaths.HomeVariable, original);
        }
    }

    [Theory]
    [InlineData(@"Scratch\Wyvern")]
    [InlineData(@".\Scratch\Wyvern")]
    [InlineData(@"..\Scratch\Wyvern")]
    [InlineData("Scratch")]
    public void A_relative_WYVERN_HOME_is_made_a_full_path_so_that_every_path_under_it_is_one(string home)
    {
        // A relative root makes every script path relative, and a script is run with its own
        // folder as the working folder, where a relative path means something else.
        string? original = Environment.GetEnvironmentVariable(WyvernPaths.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(WyvernPaths.HomeVariable, home);

            var paths = WyvernPaths.Default();

            Assert.Equal(Path.GetFullPath(home), paths.Root);
            Assert.True(Path.IsPathFullyQualified(paths.Root));
            Assert.True(Path.IsPathFullyQualified(paths.SettingsFile));
            Assert.True(Path.IsPathFullyQualified(paths.DefaultDataDir));
            Assert.True(Path.IsPathFullyQualified(paths.Data(new DataDirSetting(null, null)).ScriptsDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WyvernPaths.HomeVariable, original);
        }
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20u)]
    [InlineData(" alt + space ", HotkeyModifiers.Alt, 0x20u)]
    [InlineData("Ctrl+Shift+K", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4Bu)]
    [InlineData("control+k", HotkeyModifiers.Control, 0x4Bu)]
    [InlineData("Win+1", HotkeyModifiers.Win, 0x31u)]
    [InlineData("F12", HotkeyModifiers.None, 0x7Bu)]
    [InlineData("Ctrl+F1", HotkeyModifiers.Control, 0x70u)]
    [InlineData("Shift+F24", HotkeyModifiers.Shift, 0x87u)]
    public void Parses_valid_hotkeys(string text, HotkeyModifiers modifiers, uint virtualKey)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, virtualKey), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Space")]
    [InlineData("K")]
    [InlineData("Alt+")]
    [InlineData("Alt+Alt+K")]
    [InlineData("Hyper+K")]
    [InlineData("Alt+Banana")]
    [InlineData("Alt+F25")]
    [InlineData("Alt+F0")]
    [InlineData("Alt+é")]
    public void Rejects_invalid_hotkeys(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Default_is_alt_space()
    {
        Assert.True(Hotkey.TryParse(WyvernSettings.DefaultHotkey, out var parsed));
        Assert.Equal(Hotkey.Default, parsed);
    }
}
