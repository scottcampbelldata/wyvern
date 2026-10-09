using System.Text;
using Beacon.Core.Library;

namespace Beacon.Tests;

/// <summary>A new snippet or quicklink written into the library, as the forms that make them write it.</summary>
public sealed class LibraryWriterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _snippets;
    private readonly string _quicklinks;

    public LibraryWriterTests()
    {
        _snippets = Path.Combine(_dir.Path, "library", "snippets");
        _quicklinks = Path.Combine(_dir.Path, "library", "quicklinks.json");
    }

    public void Dispose() => _dir.Dispose();

    private Snippet Read(string fileName)
    {
        string file = Path.Combine(_snippets, fileName);
        return SnippetFile.Parse(File.ReadAllText(file, Encoding.UTF8), fileName, file)!;
    }

    private string[] SnippetFiles() => Directory.Exists(_snippets)
        ? Directory.GetFiles(_snippets).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray()
        : [];

    [Fact]
    public void A_snippet_is_written_as_a_file_of_its_name_that_reads_back_as_it_was_typed()
    {
        var saved = LibraryWriter.SaveSnippet(_snippets, "  Signature  ", " ;sig ", "Kind regards,\r\nScott");

        Assert.True(saved.Saved);
        Assert.Equal("", saved.Note);
        Assert.Equal(["Signature.md"], SnippetFiles());
        var snippet = Read("Signature.md");
        Assert.Equal("Signature", snippet.Name);
        Assert.Equal(";sig", snippet.Keyword);
        Assert.Equal("Kind regards,\r\nScott", snippet.Body);
    }

    [Fact]
    public void A_snippet_without_a_keyword_has_none_and_a_line_break_at_its_end_is_kept()
    {
        Assert.True(LibraryWriter.SaveSnippet(_snippets, "Line", null, "one line\n").Saved);

        var snippet = Read("Line.md");
        Assert.Null(snippet.Keyword);
        Assert.Equal("one line\n", snippet.Body);
        Assert.DoesNotContain("keyword", File.ReadAllText(Path.Combine(_snippets, "Line.md")));
    }

    [Fact]
    public void Text_that_looks_like_the_top_of_a_snippet_file_is_still_only_text()
    {
        string text = "---\nname: not a name\n---\nbody";
        Assert.True(LibraryWriter.SaveSnippet(_snippets, "Tricky: one", null, text).Saved);

        var snippet = Read("Tricky- one.md");
        Assert.Equal("Tricky: one", snippet.Name);
        Assert.Equal(text, snippet.Body);
    }

    [Fact]
    public void The_file_has_no_byte_order_mark_and_no_file_is_left_beside_it()
    {
        LibraryWriter.SaveSnippet(_snippets, "Plain", null, "text");

        byte[] bytes = File.ReadAllBytes(Path.Combine(_snippets, "Plain.md"));
        Assert.Equal((byte)'-', bytes[0]);
        Assert.Equal(["Plain.md"], SnippetFiles());
    }

    [Theory]
    [InlineData("", LibraryWriter.NameNeeded)]
    [InlineData("   ", LibraryWriter.NameNeeded)]
    [InlineData("\r\n", LibraryWriter.NameNeeded)]
    public void A_snippet_needs_a_name(string name, string note)
    {
        var saved = LibraryWriter.SaveSnippet(_snippets, name, null, "text");

        Assert.False(saved.Saved);
        Assert.Equal(note, saved.Note);
        Assert.Empty(SnippetFiles());
    }

    [Fact]
    public void What_cannot_be_saved_is_refused_in_fixed_words_and_nothing_is_written()
    {
        Assert.Equal(LibraryWriter.NameTooLong, LibraryWriter.SaveSnippet(_snippets, new string('n', LibraryWriter.MaxNameLength + 1), null, "text").Note);
        Assert.Equal(LibraryWriter.KeywordTooLong, LibraryWriter.SaveSnippet(_snippets, "Name", new string('k', LibraryWriter.MaxKeywordLength + 1), "text").Note);
        Assert.Equal(LibraryWriter.KeywordHasSpace, LibraryWriter.SaveSnippet(_snippets, "Name", "two words", "text").Note);
        Assert.Equal(LibraryWriter.TextNeeded, LibraryWriter.SaveSnippet(_snippets, "Name", null, "").Note);
        Assert.Equal(LibraryWriter.TextNeeded, LibraryWriter.SaveSnippet(_snippets, "Name", null, " \r\n ").Note);
        Assert.Empty(SnippetFiles());

        // The longest name and keyword are taken.
        Assert.True(LibraryWriter.SaveSnippet(_snippets, new string('n', LibraryWriter.MaxNameLength), new string('k', LibraryWriter.MaxKeywordLength), "text").Saved);
    }

    [Fact]
    public void A_name_that_is_taken_is_refused_whatever_file_has_it_and_whatever_its_case()
    {
        Assert.True(LibraryWriter.SaveSnippet(_snippets, "Signature", null, "first").Saved);

        // The same file name.
        Assert.Equal(LibraryWriter.SnippetExists, LibraryWriter.SaveSnippet(_snippets, "signature", null, "second").Note);

        // Another file that gives the name at its top, in a folder below.
        Directory.CreateDirectory(Path.Combine(_snippets, "work"));
        File.WriteAllText(Path.Combine(_snippets, "work", "other.txt"), "---\nname: Out Of Office\n---\nAway.");
        Assert.Equal(LibraryWriter.SnippetExists, LibraryWriter.SaveSnippet(_snippets, "out of office", null, "third").Note);

        Assert.Equal("first", Read("Signature.md").Body);
        Assert.Equal(["Signature.md"], SnippetFiles());
    }

    [Theory]
    [InlineData("Signature", "Signature")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    [InlineData("ends with dots...", "ends with dots")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("...", "snippet")]
    [InlineData("", "snippet")]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("com1.notes", "_com1.notes")]
    [InlineData("console", "console")]
    [InlineData("tab\there", "tab-here")]
    public void A_snippet_s_file_is_named_after_it_with_nothing_a_file_name_cannot_have(string name, string file) =>
        Assert.Equal(file, LibraryWriter.FileName(name));

    [Fact]
    public void A_quicklink_is_added_to_a_file_that_is_not_there_yet()
    {
        var saved = LibraryWriter.AddQuicklink(_quicklinks, " Search ", " https://example.com/?q={query}&lang=en ", " s ");

        Assert.True(saved.Saved);
        var quicklink = Assert.Single(QuicklinkFile.Parse(File.ReadAllText(_quicklinks)).Quicklinks);
        Assert.Equal("Search", quicklink.Name);
        Assert.Equal("https://example.com/?q={query}&lang=en", quicklink.Link);
        Assert.Equal("s", quicklink.Keyword);

        // The link is in the file as it reads, not as escapes.
        Assert.Contains("https://example.com/?q={query}&lang=en", File.ReadAllText(_quicklinks));
        Assert.Equal(["quicklinks.json"], Directory.GetFiles(Path.GetDirectoryName(_quicklinks)!).Select(file => Path.GetFileName(file)));
    }

    [Fact]
    public void A_quicklink_is_added_after_the_ones_that_are_there_which_stay_as_they_are_written()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_quicklinks)!);
        File.WriteAllText(_quicklinks, """
            [
              // work
              { "name": "Mail", "link": "https://mail.example.com", "extra": [1, 2] },
              { "name": 7, "link": "https://unusable.example.com" },
            ]
            """);

        Assert.True(LibraryWriter.AddQuicklink(_quicklinks, "Docs", @"C:\made-up\docs", null).Saved);

        string written = File.ReadAllText(_quicklinks);
        var parsed = QuicklinkFile.Parse(written);
        Assert.Equal(["Mail", "Docs"], parsed.Quicklinks.Select(quicklink => quicklink.Name));
        Assert.Equal(@"C:\made-up\docs", parsed.Quicklinks[1].Link);
        Assert.Null(parsed.Quicklinks[1].Keyword);
        Assert.Equal(1, parsed.Skipped);

        // The entry Beacon cannot use, and the key it does not know, are still in the file.
        Assert.Contains("unusable.example.com", written);
        Assert.Contains("\"extra\": [1, 2]", written);
    }

    [Fact]
    public void A_file_that_is_there_and_empty_gets_its_first_quicklink()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_quicklinks)!);
        File.WriteAllText(_quicklinks, "");

        Assert.True(LibraryWriter.AddQuicklink(_quicklinks, "Mail", "https://mail.example.com", null).Saved);

        Assert.Single(QuicklinkFile.Parse(File.ReadAllText(_quicklinks)).Quicklinks);
    }

    [Fact]
    public void A_name_that_a_quicklink_already_has_is_refused_and_the_file_is_left_as_it_was()
    {
        LibraryWriter.AddQuicklink(_quicklinks, "Mail", "https://mail.example.com", null);
        string before = File.ReadAllText(_quicklinks);

        var saved = LibraryWriter.AddQuicklink(_quicklinks, "MAIL", "https://other.example.com", null);

        Assert.False(saved.Saved);
        Assert.Equal(LibraryWriter.QuicklinkExists, saved.Note);
        Assert.Equal(before, File.ReadAllText(_quicklinks));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"name\": \"Mail\" }")]
    [InlineData("[ { \"name\": \"Mail\", ")]
    public void A_file_that_cannot_be_read_is_never_written_over(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_quicklinks)!);
        File.WriteAllText(_quicklinks, content);

        var saved = LibraryWriter.AddQuicklink(_quicklinks, "Docs", "https://docs.example.com", null);

        Assert.False(saved.Saved);
        Assert.Equal(LibraryWriter.QuicklinksUnreadable, saved.Note);
        Assert.Equal(content, File.ReadAllText(_quicklinks));
    }

    [Fact]
    public void A_quicklink_needs_a_name_and_a_link_and_a_keyword_of_one_word()
    {
        Assert.Equal(LibraryWriter.NameNeeded, LibraryWriter.AddQuicklink(_quicklinks, " ", "https://example.com", null).Note);
        Assert.Equal(LibraryWriter.LinkNeeded, LibraryWriter.AddQuicklink(_quicklinks, "Name", "  ", null).Note);
        Assert.Equal(LibraryWriter.KeywordHasSpace, LibraryWriter.AddQuicklink(_quicklinks, "Name", "https://example.com", "two words").Note);
        Assert.Equal(LibraryWriter.NameTooLong, LibraryWriter.AddQuicklink(_quicklinks, new string('n', 101), "https://example.com", null).Note);
        Assert.False(File.Exists(_quicklinks));
    }

    [Fact]
    public void A_line_break_typed_into_a_name_or_a_link_does_not_break_the_file()
    {
        Assert.True(LibraryWriter.AddQuicklink(_quicklinks, "Two\r\nlines", "https://example.com/\npath", null).Saved);
        Assert.True(LibraryWriter.SaveSnippet(_snippets, "Two\nlines", null, "text").Saved);

        var quicklink = Assert.Single(QuicklinkFile.Parse(File.ReadAllText(_quicklinks)).Quicklinks);
        Assert.Equal("Two  lines", quicklink.Name);
        Assert.Equal("https://example.com/ path", quicklink.Link);
        Assert.Equal("Two lines", Read("Two lines.md").Name);
    }
}
