using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>settings.json as the settings window reads and writes it.</summary>
public sealed class SettingsDocumentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string FileWith(string json)
    {
        string path = _dir.File("settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_file_that_does_not_exist_is_an_empty_document_and_every_setting_gives_its_fallback()
    {
        var document = SettingsDocument.Read(_dir.File("settings.json"));

        Assert.NotNull(document);
        Assert.Equal("Alt+Space", document.Text("hotkey", "Alt+Space"));
        Assert.True(document.Flag("startWithWindows", true));
        Assert.Equal(90, document.Number("clipboardRetentionDays", 90));
        Assert.Null(document.List("indexedFolders"));
    }

    [Fact]
    public void Settings_are_read_whatever_the_case_of_their_names_and_with_comments_in_the_file()
    {
        string path = FileWith("""
            {
              // the launcher
              "Hotkey": "Ctrl+Shift+K",
              "STARTWITHWINDOWS": false,
              "clipboardRetentionDays": 30,
              "indexedFolders": ["C:\\made-up\\one", 7, "C:\\made-up\\two"],
            }
            """);

        var document = SettingsDocument.Read(path);

        Assert.NotNull(document);
        Assert.Equal("Ctrl+Shift+K", document.Text("hotkey", "Alt+Space"));
        Assert.False(document.Flag("startWithWindows", true));
        Assert.Equal(30, document.Number("clipboardRetentionDays", 90));
        Assert.Equal([@"C:\made-up\one", @"C:\made-up\two"], document.List("indexedFolders"));
    }

    [Fact]
    public void A_value_of_the_wrong_kind_gives_the_fallback()
    {
        var document = SettingsDocument.Read(FileWith("""{ "hotkey": 5, "startWithWindows": "yes", "clipboardRetentionDays": 1.5, "indexedFolders": "C:\\made-up" }"""));

        Assert.NotNull(document);
        Assert.Equal("Alt+Space", document.Text("hotkey", "Alt+Space"));
        Assert.True(document.Flag("startWithWindows", true));
        Assert.Equal(90, document.Number("clipboardRetentionDays", 90));
        Assert.Null(document.List("indexedFolders"));
    }

    [Fact]
    public void A_file_that_is_not_json_or_not_an_object_is_not_a_document()
    {
        Assert.Null(SettingsDocument.Read(FileWith("not json")));
        Assert.Null(SettingsDocument.Read(FileWith("[1, 2]")));
    }

    [Fact]
    public void What_is_set_is_written_and_everything_else_in_the_file_is_kept()
    {
        string path = FileWith("""{ "Hotkey": "Alt+Space", "somethingNew": { "kept": [1, 2] }, "snippetAutoExpand": false }""");
        var document = SettingsDocument.Read(path)!;

        document.Set("hotkey", "Ctrl+Alt+B");
        document.Set("snippetAutoExpand", true);
        document.Set("clipboardRetentionDays", 30);
        document.Set("indexedFolders", [@"C:\made-up\one"]);
        document.Write(path);

        var again = SettingsDocument.Read(path)!;
        Assert.Equal("Ctrl+Alt+B", again.Text("hotkey"));
        Assert.True(again.Flag("snippetAutoExpand", false));
        Assert.Equal(30, again.Number("clipboardRetentionDays", 90));
        Assert.Equal([@"C:\made-up\one"], again.List("indexedFolders"));

        string written = File.ReadAllText(path);

        // The name keeps the case the file had it in, so that it is not there twice.
        Assert.Contains("\"Hotkey\": \"Ctrl+Alt+B\"", written);
        Assert.DoesNotContain("\"hotkey\"", written);
        Assert.Contains("\"somethingNew\"", written);
        Assert.Contains("\"kept\"", written);
    }

    [Fact]
    public void A_text_with_nothing_in_it_and_a_list_of_null_take_the_setting_out_and_an_empty_list_is_written()
    {
        string path = FileWith("""{ "clipboardHotkey": "Ctrl+Shift+V", "dataDir": "D:\\made-up", "indexedFolders": ["C:\\made-up"], "excludedFolderNames": ["bin"] }""");
        var document = SettingsDocument.Read(path)!;

        document.Set("clipboardHotkey", "   ");
        document.Set("dataDir", (string?)null);
        document.Set("indexedFolders", (IReadOnlyList<string>?)null);
        document.Set("excludedFolderNames", []);
        document.Write(path);

        string written = File.ReadAllText(path);
        Assert.DoesNotContain("clipboardHotkey", written);
        Assert.DoesNotContain("dataDir", written);
        Assert.DoesNotContain("indexedFolders", written);
        Assert.Empty(SettingsDocument.Read(path)!.List("excludedFolderNames")!);
    }

    [Fact]
    public void What_is_written_is_what_the_settings_reader_reads()
    {
        string path = _dir.File("settings.json");
        var document = SettingsDocument.Empty();
        document.Set("hotkey", "Ctrl+Alt+B");
        document.Set("startWithWindows", false);
        document.Set("snippetAutoExpand", true);
        document.Write(path);

        var loaded = SettingsStore.Load(path);

        Assert.Null(loaded.Error);
        Assert.Equal("Ctrl+Alt+B", loaded.Settings.Hotkey);
        Assert.False(loaded.Settings.StartWithWindows);
        Assert.True(loaded.Settings.SnippetAutoExpand?.GetBoolean());
    }

    [Fact]
    public void Writing_leaves_no_other_file_beside_the_settings()
    {
        string path = _dir.File("settings.json");
        var document = SettingsDocument.Empty();
        document.Set("hotkey", "Alt+Space");

        document.Write(path);
        document.Write(path);

        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }
}
