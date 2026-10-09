using System.Text.Json;
using Beacon.Core.Library;

namespace Beacon.Tests;

public class QuicklinkTests
{
    [Fact]
    public void The_id_is_the_prefix_and_the_name()
    {
        Assert.Equal("quicklink:Search GitHub", new Quicklink("Search GitHub", "https://github.com", null).Id);
    }

    [Theory]
    [InlineData("https://github.com/search?q={query}", true)]
    [InlineData(@"C:\notes\{query}.md", true)]
    [InlineData("https://github.com", false)]
    [InlineData("https://example.org/{Query}", false)]
    [InlineData("https://example.org/{ query }", false)]
    public void A_link_takes_an_argument_only_when_it_holds_the_placeholder_exactly(string link, bool expected)
    {
        Assert.Equal(expected, new Quicklink("x", link, null).TakesArgument);
    }

    [Theory]
    [InlineData("https://github.com/search?q={query}", "fuzzy matcher", "https://github.com/search?q=fuzzy%20matcher")]
    [InlineData("https://github.com/search?q={query}", "tom & jerry", "https://github.com/search?q=tom%20%26%20jerry")]
    [InlineData("https://example.org/?q={query}", "a+b=c/d?e#f", "https://example.org/?q=a%2Bb%3Dc%2Fd%3Fe%23f")]
    [InlineData("https://example.org/?q={query}", "café", "https://example.org/?q=caf%C3%A9")]
    [InlineData("http://intranet/find/{query}", "100%", "http://intranet/find/100%25")]
    [InlineData("HTTPS://Example.org/?q={query}", "a b", "HTTPS://Example.org/?q=a%20b")]
    public void In_a_web_address_the_argument_is_escaped(string link, string argument, string expected)
    {
        Assert.Equal(expected, new Quicklink("x", link, null).Resolve(argument));
    }

    [Theory]
    [InlineData(@"C:\notes\{query}.md", "tom & jerry", @"C:\notes\tom & jerry.md")]
    [InlineData("obsidian://open?file={query}", "a b&c", "obsidian://open?file=a b&c")]
    [InlineData("httpx://example.org/{query}", "a b", "httpx://example.org/a b")]
    [InlineData(@"\\server\share\{query}", "100%", @"\\server\share\100%")]
    public void Anywhere_else_the_argument_goes_in_unchanged(string link, string argument, string expected)
    {
        Assert.Equal(expected, new Quicklink("x", link, null).Resolve(argument));
    }

    [Fact]
    public void Every_placeholder_is_replaced()
    {
        var quicklink = new Quicklink("x", "https://example.org/{query}/compare/{query}", null);

        Assert.Equal("https://example.org/a%20b/compare/a%20b", quicklink.Resolve("a b"));
    }

    [Fact]
    public void An_empty_argument_puts_nothing_in()
    {
        Assert.Equal("https://example.org/?q=", new Quicklink("x", "https://example.org/?q={query}", null).Resolve(""));
    }

    // The argument holds the placeholder with letters either side. A wrong implementation that
    // replaced one placeholder at a time and then looked again would grow the text or loop;
    // replacing a placeholder with itself, as the argument "{query}" does, could not show that.
    [Fact]
    public void A_placeholder_inside_the_argument_is_not_replaced_again()
    {
        Assert.Equal(@"C:\a\x{query}y\x{query}y", new Quicklink("x", @"C:\a\{query}\{query}", null).Resolve("x{query}y"));
    }

    // The argument's own braces are escaped, so there is nothing in it to replace a second time.
    [Fact]
    public void In_a_web_address_the_argument_is_escaped_and_every_placeholder_is_replaced()
    {
        Assert.Equal("https://a/x%7Bquery%7Dy/x%7Bquery%7Dy", new Quicklink("x", "https://a/{query}/{query}", null).Resolve("x{query}y"));
    }
}

public class QuicklinkFileTests
{
    [Fact]
    public void Reads_names_links_and_keywords_in_the_order_written()
    {
        var parsed = QuicklinkFile.Parse("""
            [
              { "name": "Search GitHub", "keyword": "gh", "link": "https://github.com/search?q={query}" },
              { "name": "Notes folder", "link": "C:\\Notes" }
            ]
            """);

        Assert.Equal(
            [
                new Quicklink("Search GitHub", "https://github.com/search?q={query}", "gh"),
                new Quicklink("Notes folder", @"C:\Notes", null),
            ],
            parsed.Quicklinks);
        Assert.Equal(0, parsed.Skipped);
    }

    [Fact]
    public void Comments_trailing_commas_unknown_keys_and_key_case_are_tolerated()
    {
        var parsed = QuicklinkFile.Parse("""
            [
              // { "name": "Example", "link": "https://example.org" }
              { "Name": "Mail", "LINK": "https://mail.example.org", "Keyword": "m", "colour": "red" },
            ]
            """);

        Assert.Equal([new Quicklink("Mail", "https://mail.example.org", "m")], parsed.Quicklinks);
        Assert.Equal(0, parsed.Skipped);
    }

    [Fact]
    public void Text_is_trimmed_and_an_empty_or_null_keyword_means_none()
    {
        var parsed = QuicklinkFile.Parse("""
            [
              { "name": "  Mail  ", "link": " https://mail.example.org ", "keyword": "  " },
              { "name": "Wiki", "link": "https://wiki.example.org", "keyword": null }
            ]
            """);

        Assert.Equal(
            [new Quicklink("Mail", "https://mail.example.org", null), new Quicklink("Wiki", "https://wiki.example.org", null)],
            parsed.Quicklinks);
    }

    [Theory]
    [InlineData("""{ "link": "https://a.example" }""")]
    [InlineData("""{ "name": "A" }""")]
    [InlineData("""{ "name": "", "link": "https://a.example" }""")]
    [InlineData("""{ "name": "A", "link": "   " }""")]
    [InlineData("""{ "name": null, "link": "https://a.example" }""")]
    [InlineData("""{ "name": 7, "link": "https://a.example" }""")]
    [InlineData("""{ "name": "A", "link": ["https://a.example"] }""")]
    [InlineData("""{ "name": "A", "link": "https://a.example", "keyword": 7 }""")]
    // A lone surrogate written as an escape is valid JSON, but its text cannot be read. The
    // escapes below are the six characters backslash, u, d, 8, 0, 0, not a character.
    [InlineData("""{ "name": "\ud800", "link": "https://a.example" }""")]
    [InlineData("""{ "name": "A", "link": "\ud800" }""")]
    [InlineData("""{ "name": "A", "link": "https://a.example", "keyword": "\udc00" }""")]
    [InlineData("""{ "\ud800": 1, "name": "A", "link": "https://a.example" }""")]
    [InlineData("\"just text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void An_entry_that_cannot_be_used_is_skipped_and_the_rest_are_kept(string badEntry)
    {
        var parsed = QuicklinkFile.Parse("[" + badEntry + """, { "name": "Good", "link": "https://good.example" }]""");

        Assert.Equal([new Quicklink("Good", "https://good.example", null)], parsed.Quicklinks);
        Assert.Equal(1, parsed.Skipped);
    }

    [Fact]
    public void A_name_used_before_is_skipped_whatever_its_case()
    {
        var parsed = QuicklinkFile.Parse("""
            [
              { "name": "Mail", "link": "https://first.example" },
              { "name": "MAIL", "link": "https://second.example" },
              { "name": "Wiki", "link": "https://wiki.example" }
            ]
            """);

        Assert.Equal(["Mail", "Wiki"], parsed.Quicklinks.Select(q => q.Name));
        Assert.Equal("https://first.example", parsed.Quicklinks[0].Link);
        Assert.Equal(1, parsed.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    [InlineData("[]")]
    [InlineData("[\n  // nothing yet\n]")]
    // A byte order mark in front of the text is not part of it, and is not white space either.
    [InlineData("\uFEFF[]")]
    [InlineData("\uFEFF")]
    public void A_file_with_nothing_in_it_holds_no_quicklinks(string json)
    {
        var parsed = QuicklinkFile.Parse(json);

        Assert.Empty(parsed.Quicklinks);
        Assert.Equal(0, parsed.Skipped);
    }

    [Theory]
    [InlineData("[ { \"name\": ")]
    [InlineData("not json")]
    [InlineData("""{ "name": "A", "link": "https://a.example" }""")]
    [InlineData("\"text\"")]
    // A file of nothing but comments is refused too, so that the person is told it was not loaded.
    [InlineData("// nothing yet")]
    [InlineData("// nothing yet\n")]
    public void Text_that_is_not_a_json_array_is_refused(string json)
    {
        Assert.ThrowsAny<JsonException>(() => QuicklinkFile.Parse(json));
    }

    // A half of a surrogate pair written as itself, not as an escape, is not text the parser accepts.
    // It reports that as an argument exception, and the caller is promised a JsonException.
    [Fact]
    public void A_lone_surrogate_inside_a_json_string_is_refused_as_text_that_is_not_json()
    {
        Assert.Throws<JsonException>(() => QuicklinkFile.Parse("[\"" + "\ud800" + "\"]"));
    }
}
