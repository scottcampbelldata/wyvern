using Wyvern.Core.Library;

namespace Wyvern.Tests;

/// <summary>
/// The texts here are written with explicit line breaks, never as multi-line literals, so that
/// what is tested does not depend on how this source file's own line endings are stored.
/// </summary>
public class SnippetFileTests
{
    private static Snippet Parse(string text, string relativePath = "sig.md")
    {
        var snippet = SnippetFile.Parse(text, relativePath, @"C:\lib\snippets\" + relativePath);
        Assert.NotNull(snippet);
        return snippet;
    }

    [Fact]
    public void Front_matter_gives_the_name_and_keyword_and_the_rest_is_the_body()
    {
        var snippet = Parse("---\nname: Email signature\nkeyword: ;sig\n---\nRegards,\nScott\n");

        Assert.Equal("Email signature", snippet.Name);
        Assert.Equal(";sig", snippet.Keyword);
        Assert.Equal("Regards,\nScott", snippet.Body);
        Assert.Equal("sig.md", snippet.RelativePath);
        Assert.Equal(@"C:\lib\snippets\sig.md", snippet.FilePath);
        Assert.Equal("snippet:sig.md", snippet.Id);
    }

    [Fact]
    public void Windows_line_endings_work_and_are_kept_inside_the_body()
    {
        var snippet = Parse("---\r\nname: Email signature\r\nkeyword: ;sig\r\n---\r\nRegards,\r\nScott\r\n");

        Assert.Equal("Email signature", snippet.Name);
        Assert.Equal(";sig", snippet.Keyword);
        Assert.Equal("Regards,\r\nScott", snippet.Body);
    }

    [Fact]
    public void Without_front_matter_the_whole_file_is_the_body_and_the_file_gives_the_name()
    {
        var snippet = Parse("Kind regards,\nScott\n", @"work\Sign off.txt");

        Assert.Equal("Sign off", snippet.Name);
        Assert.Null(snippet.Keyword);
        Assert.Equal("Kind regards,\nScott", snippet.Body);
        Assert.Equal(@"snippet:work\Sign off.txt", snippet.Id);
    }

    [Theory]
    [InlineData("Body\n", "Body")]
    [InlineData("Body\r\n", "Body")]
    [InlineData("Body", "Body")]
    [InlineData("Body\n\n", "Body\n")]
    [InlineData("Body\r\n\r\n", "Body\r\n")]
    [InlineData("\n", "")]
    [InlineData("", "")]
    public void Only_one_final_line_break_is_dropped(string text, string expectedBody)
    {
        Assert.Equal(expectedBody, Parse(text).Body);
        Assert.Equal(expectedBody, Parse("---\n---\n" + text).Body);
    }

    [Fact]
    public void A_name_left_out_or_left_empty_falls_back_to_the_file_name()
    {
        Assert.Equal("sig", Parse("---\nkeyword: ;s\n---\nBody").Name);
        Assert.Equal("sig", Parse("---\nname:\n---\nBody").Name);
        Assert.Equal("sig", Parse("---\nname:    \n---\nBody").Name);
    }

    // A file called just ".md" has an extension and nothing before it. Its name would be empty,
    // and a row with nothing to read is no use, so the whole file name is the name.
    [Theory]
    [InlineData(".md", ".md")]
    [InlineData(@"work\.txt", ".txt")]
    public void A_file_with_no_name_before_its_extension_is_named_by_its_whole_file_name(string relativePath, string expectedName)
    {
        Assert.Equal(expectedName, Parse("Body", relativePath).Name);
        Assert.Equal(expectedName, Parse("---\nname:\n---\nBody", relativePath).Name);
    }

    [Fact]
    public void An_empty_keyword_means_none()
    {
        Assert.Null(Parse("---\nname: A\nkeyword:\n---\nBody").Keyword);
    }

    [Fact]
    public void Keys_are_matched_in_any_case_and_other_lines_are_ignored()
    {
        var snippet = Parse("---\nNAME: Loud\ntags: one, two\n\nnot a pair\n: no key\nKeyword: ;l\n---\nBody");

        Assert.Equal("Loud", snippet.Name);
        Assert.Equal(";l", snippet.Keyword);
        Assert.Equal("Body", snippet.Body);
    }

    [Fact]
    public void A_value_keeps_everything_after_the_first_colon()
    {
        var snippet = Parse("---\nname: Meeting: weekly\nkeyword: :mtg\n---\nBody");

        Assert.Equal("Meeting: weekly", snippet.Name);
        Assert.Equal(":mtg", snippet.Keyword);
    }

    [Fact]
    public void Front_matter_can_close_on_the_last_line_with_nothing_after_it()
    {
        var snippet = Parse("---\nname: Empty\n---");

        Assert.Equal("Empty", snippet.Name);
        Assert.Equal("", snippet.Body);
    }

    [Fact]
    public void Front_matter_with_windows_line_endings_can_close_with_nothing_after_it()
    {
        var snippet = Parse("---\r\nname: A\r\n---\r\n");

        Assert.Equal("A", snippet.Name);
        Assert.Equal("", snippet.Body);
    }

    // The loader's decoder normally takes a byte order mark off, but nothing here depends on that.
    [Fact]
    public void A_byte_order_mark_in_front_of_the_text_is_ignored()
    {
        var snippet = Parse("\uFEFF---\nname: Marked\nkeyword: ;m\n---\nBody\n");

        Assert.Equal("Marked", snippet.Name);
        Assert.Equal(";m", snippet.Keyword);
        Assert.Equal("Body", snippet.Body);
    }

    [Fact]
    public void A_byte_order_mark_is_not_part_of_a_body_without_front_matter()
    {
        Assert.Equal("Body", Parse("\uFEFFBody\n").Body);
    }

    [Fact]
    public void A_body_can_hold_a_line_of_dashes()
    {
        var snippet = Parse("---\nname: Rule\n---\nabove\n---\nbelow\n");

        Assert.Equal("above\n---\nbelow", snippet.Body);
    }

    [Theory]
    [InlineData("--- \nname: A\n---\nBody")]
    [InlineData(" ---\nname: A\n---\nBody")]
    [InlineData("----\nname: A\n----\nBody")]
    public void Only_a_first_line_that_is_exactly_three_dashes_opens_front_matter(string text)
    {
        var snippet = Parse(text);

        Assert.Equal("sig", snippet.Name);
        Assert.Equal(text, snippet.Body);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("---\n")]
    [InlineData("---\nname: Never closed\nBody\n")]
    [InlineData("---\nname: Closed with a space\n--- \nBody\n")]
    public void Front_matter_that_is_never_closed_makes_the_file_unusable(string text)
    {
        Assert.Null(SnippetFile.Parse(text, "sig.md", @"C:\lib\snippets\sig.md"));
    }

    [Theory]
    [InlineData("sig.md", true)]
    [InlineData("sig.TXT", true)]
    [InlineData("Sig.Md", true)]
    [InlineData("sig.markdown", false)]
    [InlineData("sig.md.bak", false)]
    [InlineData("sig", false)]
    [InlineData("md", false)]
    public void Only_md_and_txt_files_are_snippets(string fileName, bool expected)
    {
        Assert.Equal(expected, SnippetFile.HasSnippetExtension(fileName));
    }
}
