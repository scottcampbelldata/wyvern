using System.Text;
using Wyvern.Core.Library;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>A snippet or a quicklink changed or deleted from the launcher: what is written into the library, and the actions its row offers.</summary>
public sealed class LibraryEditTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _snippets;
    private readonly string _quicklinks;

    public LibraryEditTests()
    {
        _snippets = Path.Combine(_dir.Path, "library", "snippets");
        _quicklinks = Path.Combine(_dir.Path, "library", "quicklinks.json");
        Directory.CreateDirectory(_snippets);
    }

    public void Dispose() => _dir.Dispose();

    private Snippet Put(string relativePath, string content)
    {
        string file = Path.Combine(_snippets, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, new UTF8Encoding(false));
        return Read(relativePath);
    }

    private Snippet Read(string relativePath)
    {
        string file = Path.Combine(_snippets, relativePath);
        return SnippetFile.Parse(File.ReadAllText(file, Encoding.UTF8), relativePath, file)!;
    }

    private IReadOnlyList<Quicklink> Quicklinks() => QuicklinkFile.Parse(File.ReadAllText(_quicklinks)).Quicklinks;

    [Fact]
    public void A_changed_snippet_is_written_into_its_own_file_and_stays_the_same_item()
    {
        var snippet = Put(@"work\sig.md", "---\nname: Signature\nkeyword: ;sig\n---\nKind regards,\nScott\n");

        var saved = LibraryWriter.UpdateSnippet(_snippets, snippet, " Sign-off ", ";so", "All the best,\r\nScott");

        Assert.True(saved.Saved);
        Assert.Equal([Path.Combine(_snippets, @"work\sig.md")], Directory.GetFiles(_snippets, "*", SearchOption.AllDirectories));
        var changed = Read(@"work\sig.md");
        Assert.Equal(snippet.Id, changed.Id);
        Assert.Equal("Sign-off", changed.Name);
        Assert.Equal(";so", changed.Keyword);
        Assert.Equal("All the best,\r\nScott", changed.Body);
    }

    [Fact]
    public void A_snippet_file_without_a_top_gets_one_and_a_keyword_that_is_emptied_goes()
    {
        var plain = Put("plain.txt", "just text");
        var keyed = Put("keyed.md", "---\nkeyword: ;k\n---\nbody");

        Assert.True(LibraryWriter.UpdateSnippet(_snippets, plain, "Plain Text", null, "just text, changed").Saved);
        Assert.True(LibraryWriter.UpdateSnippet(_snippets, keyed, "keyed", "  ", "body").Saved);

        Assert.Equal("Plain Text", Read("plain.txt").Name);
        Assert.Equal("just text, changed", Read("plain.txt").Body);
        Assert.Null(Read("keyed.md").Keyword);
        Assert.DoesNotContain("keyword", File.ReadAllText(Path.Combine(_snippets, "keyed.md")));
    }

    [Fact]
    public void Lines_at_the_top_that_wyvern_does_not_read_are_kept_and_text_that_looks_like_a_top_is_only_text()
    {
        var snippet = Put("tagged.md", "---\nname: Tagged\ntags: work, letters\nKeyword: ;t\n# a note to self\n---\nbody\n");

        Assert.True(LibraryWriter.UpdateSnippet(_snippets, snippet, "Tagged", ";t", "---\nname: not a name\n---\nbody").Saved);

        string written = File.ReadAllText(Path.Combine(_snippets, "tagged.md"));
        Assert.Equal("---\nname: Tagged\nkeyword: ;t\ntags: work, letters\n# a note to self\n---\n---\nname: not a name\n---\nbody\n", written);
        Assert.Equal("Tagged", Read("tagged.md").Name);
        Assert.Equal("---\nname: not a name\n---\nbody", Read("tagged.md").Body);
    }

    [Fact]
    public void A_snippet_may_keep_its_own_name_and_may_not_take_another_s()
    {
        var first = Put("first.md", "---\nname: First\n---\none");
        Put("second.md", "---\nname: Second\n---\ntwo");

        Assert.True(LibraryWriter.UpdateSnippet(_snippets, first, "first", null, "one, changed").Saved);
        var refused = LibraryWriter.UpdateSnippet(_snippets, first, "SECOND", null, "one");

        Assert.False(refused.Saved);
        Assert.Equal(LibraryWriter.SnippetExists, refused.Note);
        Assert.Equal("one, changed", Read("first.md").Body);
    }

    [Fact]
    public void A_snippet_is_changed_only_with_a_name_some_text_and_a_keyword_of_one_word()
    {
        var snippet = Put("one.md", "---\nname: One\n---\ntext");
        string before = File.ReadAllText(Path.Combine(_snippets, "one.md"));

        Assert.Equal(LibraryWriter.NameNeeded, LibraryWriter.UpdateSnippet(_snippets, snippet, "  ", null, "text").Note);
        Assert.Equal(LibraryWriter.TextNeeded, LibraryWriter.UpdateSnippet(_snippets, snippet, "One", null, " \r\n ").Note);
        Assert.Equal(LibraryWriter.KeywordHasSpace, LibraryWriter.UpdateSnippet(_snippets, snippet, "One", "two words", "text").Note);
        Assert.Equal(LibraryWriter.NameTooLong, LibraryWriter.UpdateSnippet(_snippets, snippet, new string('n', 101), null, "text").Note);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_snippets, "one.md")));
    }

    [Fact]
    public void A_snippet_whose_file_has_gone_or_is_not_in_the_snippets_folder_is_not_written()
    {
        var gone = Put("gone.md", "text");
        File.Delete(gone.FilePath);
        string outside = _dir.File("outside.md");
        File.WriteAllText(outside, "text");
        var stranger = new Snippet("outside.md", outside, "outside", null, "text");
        var climbing = new Snippet(@"..\outside.md", Path.Combine(_snippets, @"..\outside.md"), "outside", null, "text");

        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.UpdateSnippet(_snippets, gone, "Gone", null, "text").Note);
        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.UpdateSnippet(_snippets, stranger, "Outside", null, "changed").Note);
        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.UpdateSnippet(_snippets, climbing, "Outside", null, "changed").Note);
        Assert.Equal("text", File.ReadAllText(outside));
        Assert.False(File.Exists(gone.FilePath));
    }

    [Fact]
    public void Deleting_a_snippet_hands_its_file_and_nothing_else_to_what_removes_it()
    {
        var snippet = Put(@"work\sig.md", "text");
        var handed = new List<string>();

        var removed = LibraryWriter.RemoveSnippet(_snippets, snippet, file =>
        {
            handed.Add(file);
            File.Delete(file);
        });

        Assert.True(removed.Saved);
        Assert.Equal([Path.Combine(_snippets, @"work\sig.md")], handed);
    }

    [Fact]
    public void A_file_that_is_not_a_snippet_s_or_not_in_the_folder_is_never_handed_over()
    {
        string outside = _dir.File("outside.md");
        File.WriteAllText(outside, "text");
        string script = Path.Combine(_snippets, "run.ps1");
        File.WriteAllText(script, "text");
        var handed = new List<string>();

        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.RemoveSnippet(_snippets, new Snippet("outside.md", outside, "o", null, ""), handed.Add).Note);
        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.RemoveSnippet(_snippets, new Snippet(@"..\outside.md", Path.Combine(_snippets, @"..\outside.md"), "o", null, ""), handed.Add).Note);
        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.RemoveSnippet(_snippets, new Snippet("run.ps1", script, "r", null, ""), handed.Add).Note);
        Assert.Equal(LibraryWriter.SnippetGone, LibraryWriter.RemoveSnippet(_snippets, new Snippet("none.md", Path.Combine(_snippets, "none.md"), "n", null, ""), handed.Add).Note);

        Assert.Empty(handed);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(script));
    }

    [Fact]
    public void A_snippet_that_is_still_there_afterwards_or_could_not_be_removed_is_said_not_to_be_deleted()
    {
        var snippet = Put("stays.md", "text");

        Assert.Equal(LibraryWriter.NotDeleted, LibraryWriter.RemoveSnippet(_snippets, snippet, _ => { }).Note);
        Assert.Equal(LibraryWriter.NotDeleted, LibraryWriter.RemoveSnippet(_snippets, snippet, _ => throw new IOException("in use")).Note);
        Assert.Equal(LibraryWriter.NotDeleted, LibraryWriter.RemoveSnippet(_snippets, snippet, _ => throw new OperationCanceledException()).Note);
        Assert.True(File.Exists(snippet.FilePath));
    }

    private const string ThreeQuicklinks = """
        [
          // the search engine
          { "name": "Search", "link": "https://duckduckgo.com/?q={query}&ia=web", "keyword": "s" },
          { "name": "Docs", "link": "https://docs.example.com", "color": "blue", "order": 2 },
          { "name": 5, "link": "not usable" },
          { "name": "Folder", "link": "C:\\made-up\\folder" },
        ]
        """;

    [Fact]
    public void A_changed_quicklink_keeps_its_place_and_whatever_else_its_entry_holds()
    {
        File.WriteAllText(_quicklinks, ThreeQuicklinks);

        var saved = LibraryWriter.UpdateQuicklink(_quicklinks, "docs", " Manuals ", "https://manuals.example.com/a?b=1&c=2", "m");

        Assert.True(saved.Saved);
        Assert.Equal(["Search", "Manuals", "Folder"], Quicklinks().Select(quicklink => quicklink.Name));
        Assert.Equal(new Quicklink("Manuals", "https://manuals.example.com/a?b=1&c=2", "m"), Quicklinks()[1]);
        string written = File.ReadAllText(_quicklinks);
        Assert.Contains("""{"name":"Manuals","link":"https://manuals.example.com/a?b=1&c=2","keyword":"m","color":"blue","order":2}""", written);

        // The others stand as they were written, the one Wyvern cannot use as well.
        Assert.Contains("""{ "name": "Search", "link": "https://duckduckgo.com/?q={query}&ia=web", "keyword": "s" }""", written);
        Assert.Contains("""{ "name": 5, "link": "not usable" }""", written);
        Assert.Contains("""{ "name": "Folder", "link": "C:\\made-up\\folder" }""", written);
    }

    [Fact]
    public void A_quicklink_may_keep_its_name_lose_its_keyword_and_not_take_another_s_name()
    {
        File.WriteAllText(_quicklinks, ThreeQuicklinks);

        Assert.True(LibraryWriter.UpdateQuicklink(_quicklinks, "Search", "search", "https://example.com/?q={query}", null).Saved);
        var refused = LibraryWriter.UpdateQuicklink(_quicklinks, "Docs", "FOLDER", "https://docs.example.com", null);

        Assert.Equal(new Quicklink("search", "https://example.com/?q={query}", null), Quicklinks()[0]);
        Assert.False(refused.Saved);
        Assert.Equal(LibraryWriter.QuicklinkExists, refused.Note);
        Assert.Equal("Docs", Quicklinks()[1].Name);
    }

    [Fact]
    public void A_quicklink_is_changed_only_when_it_is_there_and_the_file_can_be_read()
    {
        Assert.Equal(LibraryWriter.QuicklinkGone, LibraryWriter.UpdateQuicklink(_quicklinks, "Docs", "Docs", "https://example.com", null).Note);
        Assert.False(File.Exists(_quicklinks));

        File.WriteAllText(_quicklinks, ThreeQuicklinks);
        Assert.Equal(LibraryWriter.QuicklinkGone, LibraryWriter.UpdateQuicklink(_quicklinks, "Heron", "Heron", "https://example.com", null).Note);
        Assert.Equal(LibraryWriter.NameNeeded, LibraryWriter.UpdateQuicklink(_quicklinks, "Docs", " ", "https://example.com", null).Note);
        Assert.Equal(LibraryWriter.LinkNeeded, LibraryWriter.UpdateQuicklink(_quicklinks, "Docs", "Docs", " ", null).Note);
        Assert.Equal(LibraryWriter.KeywordHasSpace, LibraryWriter.UpdateQuicklink(_quicklinks, "Docs", "Docs", "https://example.com", "two words").Note);
        Assert.Equal(ThreeQuicklinks, File.ReadAllText(_quicklinks));

        File.WriteAllText(_quicklinks, "{ not a list");
        Assert.Equal(LibraryWriter.QuicklinksUnreadable, LibraryWriter.UpdateQuicklink(_quicklinks, "Docs", "Docs", "https://example.com", null).Note);
        Assert.Equal(LibraryWriter.QuicklinksUnreadable, LibraryWriter.RemoveQuicklink(_quicklinks, "Docs").Note);
        Assert.Equal("{ not a list", File.ReadAllText(_quicklinks));
    }

    [Fact]
    public void A_deleted_quicklink_leaves_the_others_as_they_were_written()
    {
        File.WriteAllText(_quicklinks, ThreeQuicklinks);

        Assert.True(LibraryWriter.RemoveQuicklink(_quicklinks, "DOCS").Saved);

        Assert.Equal(["Search", "Folder"], Quicklinks().Select(quicklink => quicklink.Name));
        string written = File.ReadAllText(_quicklinks);
        Assert.Contains("""{ "name": 5, "link": "not usable" }""", written);
        Assert.DoesNotContain("docs.example.com", written);
        Assert.Equal(LibraryWriter.QuicklinkGone, LibraryWriter.RemoveQuicklink(_quicklinks, "Docs").Note);
    }

    [Fact]
    public void Deleting_takes_every_entry_of_the_name_and_the_last_one_leaves_an_empty_list()
    {
        File.WriteAllText(_quicklinks, """[ { "name": "Docs", "link": "https://one.example.com" }, { "name": "docs", "link": "https://two.example.com" } ]""");

        Assert.True(LibraryWriter.RemoveQuicklink(_quicklinks, "Docs").Saved);

        Assert.Equal("[]\n", File.ReadAllText(_quicklinks));
        Assert.Empty(Quicklinks());
        Assert.True(LibraryWriter.AddQuicklink(_quicklinks, "Docs", "https://docs.example.com", null).Saved);
        Assert.Equal("Docs", Assert.Single(Quicklinks()).Name);
    }

    [Fact]
    public void A_snippet_s_row_offers_to_edit_it_and_to_delete_it_and_deleting_asks_first()
    {
        var snippet = new Snippet("sig.md", Path.Combine(_snippets, "sig.md"), "Signature", ";sig", "Kind regards");
        var library = new LibrarySnapshot([], [snippet], []);
        var done = new List<string>();
        var provider = new SnippetProvider(
            () => library,
            new NoPaste(),
            new FakeClipboard(),
            new FakeProcessLauncher(),
            new FakeClock(),
            TimeZoneInfo.Utc,
            edit: edited => done.Add("edit " + edited.Id),
            delete: deleted => done.Add("delete " + deleted.Id));

        var item = Assert.Single(provider.Browse());

        Assert.Equal([SnippetProvider.EditSnippet, SnippetProvider.DeleteSnippet], item.Actions.Select(action => action.Name));
        Assert.Null(item.Actions[0].Question);
        Assert.Equal(SnippetProvider.DeleteQuestion, item.Actions[1].Question);
        item.Actions[0].Run();
        item.Actions[1].Run();
        Assert.Equal(["edit snippet:sig.md", "delete snippet:sig.md"], done);

        // Without a way to change or delete one, a snippet has only its own two actions.
        var plain = new SnippetProvider(() => library, new NoPaste(), new FakeClipboard(), new FakeProcessLauncher(), new FakeClock());
        Assert.Empty(Assert.Single(plain.Browse()).Actions);
    }

    [Fact]
    public void A_quicklink_s_row_offers_the_same_whether_or_not_it_takes_something_typed()
    {
        var library = new LibrarySnapshot(
            [new Quicklink("Docs", "https://docs.example.com", null), new Quicklink("Search", "https://example.com/?q={query}", "s")],
            [],
            []);
        var done = new List<string>();
        var provider = new QuicklinkProvider(() => library, new FakeProcessLauncher(), edited => done.Add("edit " + edited.Name), deleted => done.Add("delete " + deleted.Name));

        var items = provider.Browse();

        Assert.All(items, item =>
        {
            Assert.Equal([QuicklinkProvider.EditQuicklink, QuicklinkProvider.DeleteQuicklink], item.Actions.Select(action => action.Name));
            Assert.Null(item.Actions[0].Question);
            Assert.Equal(QuicklinkProvider.DeleteQuestion, item.Actions[1].Question);
        });
        items[1].Actions[0].Run();
        items[0].Actions[1].Run();
        Assert.Equal(["edit Search", "delete Docs"], done);
        Assert.All(new QuicklinkProvider(() => library, new FakeProcessLauncher()).Browse(), item => Assert.Empty(item.Actions));
    }

    private sealed class NoPaste : Wyvern.Core.Platform.IPasteService
    {
        public void Paste(string text)
        {
        }

        public void PasteAndKeep(IReadOnlyList<Wyvern.Core.Platform.ClipboardBlock> blocks)
        {
        }
    }
}
