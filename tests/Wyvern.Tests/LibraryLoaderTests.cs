using System.Text;
using Wyvern.Core.Library;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public sealed class LibraryLoaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;

    public LibraryLoaderTests()
    {
        _paths = new DataPaths(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private string WriteSnippet(string relativePath, string text)
    {
        string file = Path.Combine(_paths.SnippetsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    private void WriteQuicklinks(string json)
    {
        Directory.CreateDirectory(_paths.LibraryDir);
        File.WriteAllText(_paths.QuicklinksFile, json);
    }

    [Fact]
    public void A_data_folder_that_does_not_exist_yet_holds_nothing_and_is_not_a_problem()
    {
        var snapshot = LibraryLoader.Load(new DataPaths(_dir.File("not-created")));

        Assert.Empty(snapshot.Quicklinks);
        Assert.Empty(snapshot.Snippets);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void Loads_quicklinks_and_snippets()
    {
        WriteQuicklinks("""[{ "name": "Wiki", "keyword": "w", "link": "https://wiki.example.org" }]""");
        string file = WriteSnippet("sig.md", "---\nname: Signature\nkeyword: ;sig\n---\nRegards\n");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal([new Quicklink("Wiki", "https://wiki.example.org", "w")], snapshot.Quicklinks);
        Assert.Equal([new Snippet("sig.md", file, "Signature", ";sig", "Regards")], snapshot.Snippets);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void Snippets_are_found_at_any_depth_and_listed_in_path_order()
    {
        WriteSnippet(@"work\replies\thanks.txt", "Thanks");
        WriteSnippet("zebra.md", "Z");
        WriteSnippet(@"Alpha\one.MD", "1");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal([@"Alpha\one.MD", @"work\replies\thanks.txt", "zebra.md"], snapshot.Snippets.Select(s => s.RelativePath));
        Assert.Equal(["one", "thanks", "zebra"], snapshot.Snippets.Select(s => s.Name));
        Assert.Equal(@"snippet:work\replies\thanks.txt", snapshot.Snippets[1].Id);
    }

    [Fact]
    public void Files_that_are_not_md_or_txt_are_not_snippets_and_not_a_problem()
    {
        WriteSnippet("notes.docx", "x");
        WriteSnippet("sig.md.bak", "x");
        WriteSnippet("README", "x");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Empty(snapshot.Snippets);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void Hidden_files_are_left_out()
    {
        string hidden = WriteSnippet("hidden.md", "x");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.Empty(LibraryLoader.Load(_paths).Snippets);
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        string file = Path.Combine(_paths.SnippetsDir, "bom.md");
        Directory.CreateDirectory(_paths.SnippetsDir);
        File.WriteAllText(file, "---\nname: Marked\n---\nBody\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(file)[0]);

        var snippet = Assert.Single(LibraryLoader.Load(_paths).Snippets);

        Assert.Equal("Marked", snippet.Name);
        Assert.Equal("Body", snippet.Body);
    }

    [Fact]
    public void Text_outside_ascii_is_read_as_utf8()
    {
        WriteSnippet("accents.txt", "Très bien … 日本語");

        Assert.Equal("Très bien … 日本語", Assert.Single(LibraryLoader.Load(_paths).Snippets).Body);
    }

    [Fact]
    public void A_snippet_with_unclosed_front_matter_is_skipped_and_named_and_the_rest_load()
    {
        string bad = WriteSnippet("broken.md", "---\nname: Never closed\n");
        WriteSnippet("fine.md", "Fine");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["fine"], snapshot.Snippets.Select(s => s.Name));
        Assert.Equal([bad], snapshot.BadFiles);
    }

    [Fact]
    public void A_snippet_over_the_size_limit_is_skipped_and_named()
    {
        string big = WriteSnippet("big.txt", new string('x', (int)LibraryLoader.MaxFileBytes + 1));
        WriteSnippet("at-the-limit.txt", new string('x', (int)LibraryLoader.MaxFileBytes));

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["at-the-limit"], snapshot.Snippets.Select(s => s.Name));
        Assert.Equal([big], snapshot.BadFiles);
    }

    [Fact]
    public void A_snippet_another_program_has_locked_is_skipped_and_named()
    {
        string locked = WriteSnippet("locked.md", "x");
        WriteSnippet("free.md", "Free");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["free"], snapshot.Snippets.Select(s => s.Name));
        Assert.Equal([locked], snapshot.BadFiles);
    }

    [Fact]
    public void A_file_that_could_not_be_opened_is_marked_so_that_it_can_be_tried_again()
    {
        string locked = WriteSnippet("locked.md", "x");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var snapshot = LibraryLoader.Load(_paths);

        Assert.True(snapshot.HasUnreadableFiles);
        Assert.Equal([locked], snapshot.BadFiles);
    }

    [Fact]
    public void A_quicklinks_file_that_could_not_be_opened_is_marked_too()
    {
        WriteQuicklinks("[]");
        using var hold = new FileStream(_paths.QuicklinksFile, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.True(LibraryLoader.Load(_paths).HasUnreadableFiles);
    }

    [Fact]
    public void A_file_that_is_malformed_or_too_large_is_not_marked_because_reading_it_again_changes_nothing()
    {
        WriteSnippet("broken.md", "---\nname: Never closed\n");
        WriteSnippet("big.txt", new string('x', (int)LibraryLoader.MaxFileBytes + 1));
        WriteQuicklinks("[ { \"name\": ");
        WriteSnippet("fine.md", "Fine");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(3, snapshot.BadFiles.Count);
        Assert.False(snapshot.HasUnreadableFiles);
    }

    [Fact]
    public void A_snippet_an_editor_still_has_open_for_writing_is_read()
    {
        string open = WriteSnippet("open.md", "Still open");
        using var editor = new FileStream(open, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("Still open", Assert.Single(LibraryLoader.Load(_paths).Snippets).Body);
    }

    [Fact]
    public void A_snippet_held_open_with_delete_on_close_is_read()
    {
        // A handle that will delete the file when it closes lets other readers in only when they
        // allow deletion too. Some editors and sync tools hold a file this way.
        string held = WriteSnippet("held.md", "Held");
        using var holder = new FileStream(
            held, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal("Held", Assert.Single(snapshot.Snippets).Body);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void Quicklinks_that_are_not_valid_json_are_named_and_snippets_still_load()
    {
        WriteQuicklinks("[ { \"name\": ");
        WriteSnippet("fine.md", "Fine");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Empty(snapshot.Quicklinks);
        Assert.Single(snapshot.Snippets);
        Assert.Equal([_paths.QuicklinksFile], snapshot.BadFiles);
    }

    [Fact]
    public void Quicklinks_with_one_bad_entry_keep_the_rest_and_the_file_is_named()
    {
        WriteQuicklinks("""[{ "name": "No link" }, { "name": "Wiki", "link": "https://wiki.example.org" }]""");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Wiki"], snapshot.Quicklinks.Select(q => q.Name));
        Assert.Equal([_paths.QuicklinksFile], snapshot.BadFiles);
    }

    [Fact]
    public void A_quicklink_whose_text_cannot_be_read_does_not_stop_the_rest_of_the_library_loading()
    {
        // "\ud800" is half of a surrogate pair written as an escape: valid JSON, but the text it
        // makes cannot be read. It is six characters in the file, not one in this source.
        WriteQuicklinks("""[{ "name": "\ud800", "link": "https://bad.example.org" }, { "name": "Wiki", "link": "https://wiki.example.org" }]""");
        WriteSnippet("fine.md", "Fine");

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Equal(["Wiki"], snapshot.Quicklinks.Select(q => q.Name));
        Assert.Equal(["fine"], snapshot.Snippets.Select(s => s.Name));
        Assert.Equal([_paths.QuicklinksFile], snapshot.BadFiles);
    }

    [Fact]
    public void An_empty_quicklinks_file_is_not_a_problem()
    {
        WriteQuicklinks(LibraryFiles.QuicklinksTemplate);

        var snapshot = LibraryLoader.Load(_paths);

        Assert.Empty(snapshot.Quicklinks);
        Assert.Empty(snapshot.BadFiles);
    }

    [Fact]
    public void Bad_files_are_listed_in_path_order()
    {
        WriteQuicklinks("nonsense");
        string second = WriteSnippet("b-bad.md", "---\n");
        string first = WriteSnippet("A-bad.md", "---\n");

        Assert.Equal([_paths.QuicklinksFile, first, second], LibraryLoader.Load(_paths).BadFiles);
    }
}
