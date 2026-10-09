using Beacon.Core.Library;
using Beacon.Core.Settings;

namespace Beacon.Tests;

public sealed class LibraryFilesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;

    public LibraryFilesTests()
    {
        // The data folder itself does not exist yet, as on a first run.
        _paths = new DataPaths(_dir.File("data"));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Creates_the_library_snippets_and_scripts_folders_and_nothing_else()
    {
        LibraryFiles.EnsureFolders(_paths);
        LibraryFiles.EnsureFolders(_paths);

        Assert.True(Directory.Exists(_paths.SnippetsDir));
        Assert.True(Directory.Exists(_paths.ScriptsDir));
        Assert.Equal(
            [_paths.ScriptsDir, _paths.SnippetsDir],
            Directory.GetFileSystemEntries(_paths.LibraryDir).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(_paths.QuicklinksFile));
    }

    [Fact]
    public void The_quicklinks_file_is_created_with_one_commented_out_example()
    {
        string file = LibraryFiles.EnsureQuicklinksFile(_paths);

        Assert.Equal(_paths.QuicklinksFile, file);
        string[] lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.Equal("[", lines[0]);
        Assert.StartsWith("  // {", lines[1]);
        Assert.Contains("{query}", lines[1]);
        Assert.Equal("]", lines[2]);
    }

    [Fact]
    public void A_new_quicklinks_file_holds_no_quicklinks_until_the_owner_writes_one()
    {
        LibraryFiles.EnsureQuicklinksFile(_paths);

        var parsed = QuicklinkFile.Parse(File.ReadAllText(_paths.QuicklinksFile));

        Assert.Empty(parsed.Quicklinks);
        Assert.Equal(0, parsed.Skipped);
    }

    [Fact]
    public void The_example_works_once_its_comment_marks_are_removed()
    {
        var parsed = QuicklinkFile.Parse(LibraryFiles.QuicklinksTemplate.Replace("// ", ""));

        Assert.Equal([new Quicklink("Search GitHub", "https://github.com/search?q={query}", "gh")], parsed.Quicklinks);
    }

    [Fact]
    public void An_existing_quicklinks_file_is_left_exactly_as_it_is()
    {
        Directory.CreateDirectory(_paths.LibraryDir);
        File.WriteAllText(_paths.QuicklinksFile, "[ /* mine */ ]");

        string file = LibraryFiles.EnsureQuicklinksFile(_paths);

        Assert.Equal("[ /* mine */ ]", File.ReadAllText(file));
    }

    [Fact]
    public void A_new_snippet_is_created_from_the_template_and_parses()
    {
        string file = LibraryFiles.CreateSnippet(_paths);

        Assert.Equal(Path.Combine(_paths.SnippetsDir, "new-snippet.md"), file);
        var snippet = SnippetFile.Parse(File.ReadAllText(file), "new-snippet.md", file);
        Assert.NotNull(snippet);
        Assert.Equal("New snippet", snippet.Name);
        Assert.Null(snippet.Keyword);
        Assert.Equal("Replace this line with the text to paste.", snippet.Body);
    }

    [Fact]
    public void A_new_snippet_never_overwrites_and_takes_the_next_free_number()
    {
        string first = LibraryFiles.CreateSnippet(_paths);
        File.WriteAllText(first, "mine now");

        string second = LibraryFiles.CreateSnippet(_paths);
        string third = LibraryFiles.CreateSnippet(_paths);

        Assert.Equal("new-snippet-2.md", Path.GetFileName(second));
        Assert.Equal("new-snippet-3.md", Path.GetFileName(third));
        Assert.Equal("mine now", File.ReadAllText(first));
        Assert.Equal(LibraryFiles.SnippetTemplate, File.ReadAllText(second));
    }

    [Fact]
    public void A_gap_left_by_a_deleted_snippet_is_reused()
    {
        string first = LibraryFiles.CreateSnippet(_paths);
        LibraryFiles.CreateSnippet(_paths);
        File.Delete(first);

        Assert.Equal(first, LibraryFiles.CreateSnippet(_paths));
    }
}
