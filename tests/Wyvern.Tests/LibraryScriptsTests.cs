using System.Runtime.Versioning;
using System.Text;
using Wyvern.Core.Library;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>What the library loader does with the scripts folder. Nothing here runs a script.</summary>
public sealed class LibraryScriptsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;

    public LibraryScriptsTests()
    {
        _paths = new DataPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private string WriteScript(string relativePath, string text, Encoding? encoding = null)
    {
        string file = Path.Combine(_paths.ScriptsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return file;
    }

    [Fact]
    public void A_snapshot_made_without_scripts_has_none()
    {
        Assert.Empty(LibrarySnapshot.Empty.Scripts);
        Assert.Empty(new LibrarySnapshot([], [], []).Scripts);
    }

    [Fact]
    public void A_scripts_folder_that_does_not_exist_holds_nothing_and_is_not_a_problem()
    {
        var snapshot = LibraryLoader.Load(_paths);

        Assert.Empty(snapshot.Scripts);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void A_script_is_loaded_with_what_its_header_says()
    {
        string file = WriteScript(
            "restart.ps1",
            "# @wyvern.title Restart Docker\r\n# @wyvern.mode output\r\n# @wyvern.argument Container name\r\nparam($name)\r\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal([new Script("restart.ps1", file, "Restart Docker", ScriptMode.Output, "Container name")], snapshot.Scripts);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void Scripts_of_all_five_kinds_are_found_at_any_depth_and_listed_in_path_order()
    {
        WriteScript(@"work\deploy\ship.cmd", ":: @wyvern.title Ship it\r\n");
        WriteScript("zebra.js", "// @wyvern.title Zebra\n");
        WriteScript(@"Alpha\one.PY", "print('one')\n");
        WriteScript("build.bat", "REM @wyvern.title Build\r\n");
        WriteScript("backup.ps1", "");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(
            [@"Alpha\one.PY", "backup.ps1", "build.bat", @"work\deploy\ship.cmd", "zebra.js"],
            snapshot.Scripts.Select(s => s.RelativePath));
        Assert.Equal(["one", "backup", "Build", "Ship it", "Zebra"], snapshot.Scripts.Select(s => s.Title));
        Assert.Equal(@"script:work\deploy\ship.cmd", snapshot.Scripts[3].Id);
        Assert.Equal(".py", snapshot.Scripts[0].Extension);
    }

    [Fact]
    public void Files_of_other_kinds_are_not_scripts_and_not_a_problem()
    {
        WriteScript("notes.txt", "# @wyvern.title Not a script");
        WriteScript("tool.exe", "x");
        WriteScript("backup.ps1.bak", "x");
        WriteScript("README", "x");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Empty(snapshot.Scripts);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void A_script_with_a_mode_that_is_not_understood_is_skipped_and_named_and_the_rest_load()
    {
        string bad = WriteScript("loud.py", "# @wyvern.title Loud\n# @wyvern.mode loud\n");
        WriteScript("fine.py", "# @wyvern.title Fine\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Fine"], snapshot.Scripts.Select(s => s.Title));
        Assert.Equal([bad], snapshot.BadFiles);
        Assert.False(snapshot.HasUnreadableFiles);
    }

    [Fact]
    public void A_header_line_below_the_first_twenty_is_not_seen()
    {
        string filler = string.Concat(Enumerable.Repeat("echo filler\r\n", 20));
        WriteScript("late.cmd", filler + "REM @wyvern.title Too late\r\nREM @wyvern.mode loud\r\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal("late", Assert.Single(snapshot.Scripts).Title);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void A_script_larger_than_the_limit_for_quicklinks_and_snippets_is_loaded_because_scripts_have_no_size_limit()
    {
        WriteScript("huge.js", "// @wyvern.title Huge\n" + new string('x', (int)LibraryLoader.MaxFileBytes * 2));

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal("Huge", Assert.Single(snapshot.Scripts).Title);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Only_the_top_of_a_script_is_read_so_a_part_of_it_that_cannot_be_read_does_not_matter()
    {
        // The size test above cannot tell a bounded read from a whole one: both succeed on a big
        // file. A byte-range lock can. Another handle holds everything from 64 KB on, so reading
        // the file to its end fails with an IOException, which the loader would turn into a bad
        // file that is also unreadable. Reading only the top never goes near the locked part.
        // Windows enforces such a lock against other handles, which is why this is a Windows test.
        string file = WriteScript("long.js", "// @wyvern.title Long\n" + new string('x', 1024 * 1024));
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long locked = 64 * 1024;
        holder.Lock(locked, holder.Length - locked);
        try
        {
            var snapshot = LibraryLoader.Load(_paths);

            Assert.Equal("Long", Assert.Single(snapshot.Scripts).Title);
            Assert.Empty(snapshot.BadFiles);
            Assert.False(snapshot.HasUnreadableFiles);
        }
        finally
        {
            holder.Unlock(locked, holder.Length - locked);
        }
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        string file = WriteScript("marked.ps1", "# @wyvern.title Marked\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(file)[0]);

        Assert.Equal("Marked", Assert.Single(LibraryLoader.Load(_paths).Scripts).Title);
    }

    [Fact]
    public void A_script_saved_as_utf16_with_its_mark_is_read_too()
    {
        // Windows PowerShell's own editor saves scripts this way.
        WriteScript("wide.ps1", "# @wyvern.title Größe\r\n", Encoding.Unicode);

        Assert.Equal("Größe", Assert.Single(LibraryLoader.Load(_paths).Scripts).Title);
    }

    [Fact]
    public void Hidden_scripts_are_left_out()
    {
        string hidden = WriteScript("hidden.cmd", "");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.Empty(LibraryLoader.Load(_paths).Scripts);
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("NODE_MODULES")]
    [InlineData("Node_Modules")]
    [InlineData(".git")]
    [InlineData("__pycache__")]
    [InlineData(".venv")]
    [InlineData(".VENV")]
    [InlineData("venv")]
    [InlineData("Venv")]
    [InlineData("site-packages")]
    [InlineData("Site-Packages")]
    public void Folders_that_hold_what_a_tool_downloaded_or_generated_are_not_looked_into(string folder)
    {
        WriteScript(Path.Combine("tool", folder, "left-pad", "index.js"), "");
        WriteScript(Path.Combine("tool", "run.js"), "// @wyvern.title Run tool\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Run tool"], snapshot.Scripts.Select(s => s.Title));
    }

    [Fact]
    public void A_python_virtual_environment_adds_none_of_its_packages_or_its_activate_scripts_as_commands()
    {
        // The usual layout of one: activate scripts under Scripts, packages under Lib\site-packages.
        string[] environments = [".venv", "venv"];
        foreach (string environment in environments)
        {
            WriteScript(Path.Combine("tool", environment, "Scripts", "activate.bat"), "");
            WriteScript(Path.Combine("tool", environment, "Scripts", "Activate.ps1"), "");
            WriteScript(Path.Combine("tool", environment, "Lib", "site-packages", "pip", "__main__.py"), "");
        }

        WriteScript(Path.Combine("tool", "run.py"), "# @wyvern.title Run tool\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Run tool"], snapshot.Scripts.Select(s => s.Title));
    }

    [Fact]
    public void A_virtual_environment_with_another_name_is_still_kept_out_by_its_site_packages_folder()
    {
        WriteScript(Path.Combine("tool", "env37", "Lib", "site-packages", "pip", "__main__.py"), "");
        WriteScript(Path.Combine("tool", "run.py"), "# @wyvern.title Run tool\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Run tool"], snapshot.Scripts.Select(s => s.Title));
    }

    [Theory]
    [InlineData("node_modules")]
    [InlineData("Node_Modules")]
    [InlineData(".git")]
    [InlineData("__pycache__")]
    [InlineData(".venv")]
    [InlineData("venv")]
    [InlineData("site-packages")]
    public void A_skipped_folder_directly_in_the_scripts_folder_is_not_looked_into_either(string folder)
    {
        WriteScript(Path.Combine(folder, "x.js"), "// @wyvern.title Hidden in a tool folder\n");
        WriteScript("run.js", "// @wyvern.title Run\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Run"], snapshot.Scripts.Select(s => s.Title));
    }

    [Fact]
    public void A_folder_is_skipped_by_its_whole_name_not_by_how_its_name_starts()
    {
        // The match is on a whole folder name, so neither a file nor a folder whose name merely
        // begins with a skipped name is left out.
        WriteScript(Path.Combine("tools", "node_modules_notes.js"), "// @wyvern.title Notes\n");
        WriteScript(Path.Combine("tools", "node_modules_old", "x.js"), "// @wyvern.title Old\n");
        WriteScript(Path.Combine("tools", "venv_notes.py"), "# @wyvern.title Venv notes\n");
        WriteScript(Path.Combine("tools", "venvs", "x.py"), "# @wyvern.title Venvs\n");
        WriteScript(Path.Combine("tools", ".venv-old", "x.py"), "# @wyvern.title Old venv\n");
        WriteScript(Path.Combine("tools", "my-site-packages", "x.py"), "# @wyvern.title My packages\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(
            ["My packages", "Notes", "Old", "Old venv", "Venv notes", "Venvs"],
            snapshot.Scripts.Select(s => s.Title).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_script_another_program_has_locked_is_skipped_named_and_marked_to_be_tried_again()
    {
        string locked = WriteScript("locked.cmd", "REM @wyvern.title Locked\r\n");
        WriteScript("free.cmd", "REM @wyvern.title Free\r\n");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Free"], snapshot.Scripts.Select(s => s.Title));
        Assert.Equal([locked], snapshot.BadFiles);
        Assert.True(snapshot.HasUnreadableFiles);
    }

    [Fact]
    public void A_script_an_editor_still_has_open_for_writing_is_read()
    {
        string open = WriteScript("open.py", "# @wyvern.title Still open\n");
        using var editor = new FileStream(open, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("Still open", Assert.Single(LibraryLoader.Load(_paths).Scripts).Title);
    }

    [Fact]
    public void Scripts_load_beside_quicklinks_and_snippets_and_bad_files_of_every_kind_are_listed_in_path_order()
    {
        Directory.CreateDirectory(_paths.SnippetsDir);
        File.WriteAllText(_paths.QuicklinksFile, """[{ "name": "Wiki", "link": "https://wiki.example.org" }]""");
        File.WriteAllText(Path.Combine(_paths.SnippetsDir, "sig.md"), "Regards");
        string badSnippet = Path.Combine(_paths.SnippetsDir, "broken.md");
        File.WriteAllText(badSnippet, "---\n");
        WriteScript("fine.cmd", "");
        string badScript = WriteScript("loud.cmd", "REM @wyvern.mode loud\r\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Single(snapshot.Quicklinks);
        Assert.Single(snapshot.Snippets);
        Assert.Equal(["fine"], snapshot.Scripts.Select(s => s.Title));
        Assert.Equal([badScript, badSnippet], snapshot.BadFiles);
    }
}
