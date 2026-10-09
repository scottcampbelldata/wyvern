using System.Diagnostics;
using System.Globalization;
using System.Text;
using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

/// <summary>
/// Every address here is made up, on example.org or example.com, and none is ever fetched: the
/// code under test only reads text and bytes.
/// </summary>
public class ClipLinksTests
{
    // The address that was copied in most tests, and one link to it as a browser writes it.
    private const string Page = "https://example.org/page";
    private const string LinkToPage = """<a href="https://example.org/page">Example page</a>""";

    // Characters that must not stand in this file as themselves are written as numbers: the
    // character that turns the text after it around, the ellipsis that ends a label that was
    // cut, and the two halves of the surrogate pair that is the emoji U+1F600.
    private const char TurnsTextAround = (char)0x202E;
    private const char Ellipsis = (char)0x2026;
    private static readonly string Emoji = $"{(char)0xD83D}{(char)0xDE00}";

    /// <summary>A number as the header of the block writes it: in ten digits.</summary>
    private static string Ten(int number) => number.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>
    /// The clipboard's "HTML Format" block as a browser writes it: header lines whose numbers
    /// are offsets counted in bytes from the start of the block, then the page as UTF-8, with
    /// the piece that was copied between two comments.
    /// </summary>
    private static byte[] Html(string fragment, string moreHeader = "")
    {
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";

        string Header(int startHtml, int endHtml, int startFragment, int endFragment) =>
            "Version:0.9\r\n"
            + "StartHTML:" + Ten(startHtml) + "\r\n"
            + "EndHTML:" + Ten(endHtml) + "\r\n"
            + "StartFragment:" + Ten(startFragment) + "\r\n"
            + "EndFragment:" + Ten(endFragment) + "\r\n"
            + moreHeader;

        // Every number takes ten digits, so the header is as long with zeros in it as with the
        // numbers it will hold.
        int startHtml = Encoding.UTF8.GetByteCount(Header(0, 0, 0, 0));
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(after);

        return Encoding.UTF8.GetBytes(Header(startHtml, endHtml, startFragment, endFragment) + before + fragment + after);
    }

    /// <summary>A block of exactly this many bytes that holds one link to <see cref="Page"/>, with spaces in front of it to fill the block up.</summary>
    private static byte[] HtmlOfLength(int length) =>
        Html(new string(' ', length - Html(LinkToPage).Length) + LinkToPage);

    /// <summary>The shortest header there can be: the version and this text as its StartHTML.</summary>
    private static string HeaderWith(string startHtml) => "Version:0.9\r\nStartHTML:" + startHtml + "\r\n";

    /// <summary>A link to an address with these words, as HTML.</summary>
    private static string Link(string words, string href = Page) => $"<a href=\"{href}\">{words}</a>";

    /// <summary>The label for a copy of <see cref="Page"/> that came with this piece of HTML in a browser's block.</summary>
    private static string? LabelOf(string fragment) => ClipLinks.Label(Html(fragment), Page);

    [Theory]
    [InlineData("http://example.org")]
    [InlineData("https://example.org")]
    [InlineData("https://example.org/")]
    [InlineData("https://example.org/a/b.html?x=1&y=2#part")]
    [InlineData("https://example.com:8443/path")]
    [InlineData("https://www.example.com/caf%C3%A9")]
    public void A_web_address_and_nothing_else_comes_back_as_it_is(string text)
    {
        Assert.Equal(text, ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("HTTP://example.org/a")]
    [InlineData("HTTPS://EXAMPLE.ORG/A")]
    [InlineData("HttpS://example.org")]
    public void The_scheme_may_be_in_upper_case_and_the_text_comes_back_as_it_was_written(string text)
    {
        Assert.Equal(text, ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("  https://example.org/a  ")]
    [InlineData("https://example.org/a\r\n")]
    [InlineData("\n\t https://example.org/a \r\n")]
    public void Spaces_and_line_breaks_around_the_address_are_trimmed(string text)
    {
        Assert.Equal("https://example.org/a", ClipLinks.Address(text));
    }

    [Fact]
    public void No_text_is_not_an_address()
    {
        Assert.Null(ClipLinks.Address(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Text_with_nothing_in_it_is_not_an_address(string text)
    {
        Assert.Null(ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("just some words")]
    [InlineData("example.org")]
    [InlineData("www.example.org/page")]
    [InlineData("see https://example.org")]
    public void Ordinary_text_is_not_an_address(string text)
    {
        Assert.Null(ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("https://example.org/a word")]
    [InlineData("https://example.org/a and some more words")]
    public void An_address_followed_by_a_word_is_not_an_address_and_nothing_else(string text)
    {
        Assert.Null(ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("https://example.org/a\nhttps://example.org/b")]
    [InlineData("https://example.org/a\r\nhttps://example.com/b")]
    public void Two_addresses_on_two_lines_are_not_one_address(string text)
    {
        Assert.Null(ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("ftp://example.org/file.txt")]
    [InlineData("file:///C:/Users/Sam/notes.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:sam@example.org")]
    [InlineData("httpx://example.org")]
    [InlineData("http:example.org")]
    [InlineData("https:/example.org")]
    public void An_address_of_another_kind_is_not_a_web_address(string text)
    {
        Assert.Null(ClipLinks.Address(text));
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("http://")]
    [InlineData("https:///")]
    [InlineData("https:///path")]
    [InlineData("https://?q=1")]
    [InlineData("https://#top")]
    public void An_address_without_a_host_is_not_an_address(string text)
    {
        Assert.Null(ClipLinks.Address(text));
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x20)]
    [InlineData(0xA0)]
    [InlineData(0x2028)]
    [InlineData(0x3000)]
    public void A_tab_or_any_other_white_space_inside_is_refused(int code)
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + (char)code + "b"));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x1B)]
    [InlineData(0x7F)]
    [InlineData(0x9F)]
    public void A_control_character_inside_is_refused(int code)
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + (char)code + "b"));
    }

    // U+202E turns the text after it around, U+200B shows nothing, U+2066 isolates what follows:
    // each can make an address read as another one.
    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x200B)]
    [InlineData(0x2066)]
    [InlineData(0xFEFF)]
    public void A_character_that_shows_nothing_or_turns_text_around_makes_it_not_a_link(int code)
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + (char)code + "b"));
    }

    [Fact]
    public void An_address_of_exactly_the_longest_length_is_accepted()
    {
        string address = "https://example.org/" + new string('a', ClipLinks.MaxAddressLength - 20);

        Assert.Equal(ClipLinks.MaxAddressLength, address.Length);
        Assert.Equal(address, ClipLinks.Address(address));
    }

    [Fact]
    public void An_address_one_character_longer_than_that_is_not_looked_at()
    {
        string address = "https://example.org/" + new string('a', ClipLinks.MaxAddressLength - 19);

        Assert.Null(ClipLinks.Address(address));
    }

    [Fact]
    public void One_link_to_the_copied_address_gives_its_words()
    {
        Assert.Equal("Example page", LabelOf(LinkToPage));
    }

    [Fact]
    public void The_address_of_the_link_may_be_in_single_quotes()
    {
        Assert.Equal("Example page", LabelOf("<a href='https://example.org/page'>Example page</a>"));
    }

    [Fact]
    public void The_address_of_the_link_may_be_without_quotes()
    {
        Assert.Equal("Example page", LabelOf("<a href=https://example.org/page>Example page</a>"));
    }

    [Fact]
    public void The_tag_and_its_href_may_be_in_upper_case()
    {
        Assert.Equal("Example page", LabelOf("""<A HREF="https://example.org/page">Example page</A>"""));
    }

    [Fact]
    public void Other_attributes_may_stand_before_and_after_the_href()
    {
        Assert.Equal(
            "Example page",
            LabelOf("""<a class="link" id=first download href="https://example.org/page" target="_blank" rel="noopener">Example page</a>"""));
    }

    [Theory]
    [InlineData("""<a href = "https://example.org/page">Example page</a>""")]
    [InlineData("<a\r\n   href=\"https://example.org/page\"\r\n>Example page</a>")]
    [InlineData("""<a href=" https://example.org/page ">Example page</a>""")]
    public void Spaces_and_line_breaks_inside_the_tag_and_around_its_address_do_not_matter(string fragment)
    {
        Assert.Equal("Example page", LabelOf(fragment));
    }

    [Fact]
    public void A_data_href_before_the_href_is_not_taken_for_it()
    {
        Assert.Equal(
            "Example page",
            LabelOf("""<a data-href="https://example.com/other" href="https://example.org/page">Example page</a>"""));
    }

    [Fact]
    public void An_href_written_inside_the_value_of_another_attribute_is_not_taken_for_it()
    {
        Assert.Equal(
            "Example page",
            LabelOf("""<a title="href=https://example.com/other" href="https://example.org/page">Example page</a>"""));
    }

    [Theory]
    [InlineData("""<a title="a > b" href="https://example.org/page">Example page</a>""")]
    [InlineData("""<a title='a > b' href="https://example.org/page">Example page</a>""")]
    public void A_closing_bracket_inside_a_quoted_value_does_not_end_the_tag(string fragment)
    {
        Assert.Equal("Example page", LabelOf(fragment));
    }

    [Theory]
    [InlineData("<b>Example</b><span>page</span>")]
    [InlineData("""<span class="title">Example</span> <em>page</em>""")]
    [InlineData("Example<br>page")]
    [InlineData("""<span title="a > b">Example</span> page""")]
    public void Tags_inside_the_link_are_taken_out_and_the_words_they_held_stay_apart(string words)
    {
        Assert.Equal("Example page", LabelOf(Link(words)));
    }

    [Fact]
    public void A_comment_inside_the_words_is_taken_out()
    {
        Assert.Equal("Example page", LabelOf(Link("Example<!-- not shown --> page")));
    }

    // A comment ends at its own end mark, not at the first closing bracket inside it.
    [Fact]
    public void A_comment_with_a_closing_bracket_in_it_is_taken_out_of_the_words_whole()
    {
        Assert.Equal("Example page", LabelOf(Link("Example<!-- not > shown --> page")));
    }

    [Theory]
    [InlineData("Example\npage")]
    [InlineData("Example\r\npage")]
    [InlineData("Example\tpage")]
    [InlineData("Example \r\n\t   page")]
    public void A_line_break_or_a_tab_inside_the_words_becomes_one_space_and_the_words_do_not_run_together(string words)
    {
        Assert.Equal("Example page", LabelOf(Link(words)));
    }

    [Theory]
    [InlineData("  Example page  ")]
    [InlineData("\r\n\tExample page\r\n")]
    public void Spaces_and_line_breaks_at_the_ends_of_the_words_are_dropped(string words)
    {
        Assert.Equal("Example page", LabelOf(Link(words)));
    }

    [Theory]
    [InlineData("https://example.org/search?a=1&amp;b=2")]
    [InlineData("https://example.org/search?a=1&b=2")]
    public void An_ampersand_in_the_href_written_as_a_reference_or_as_itself_matches_the_address(string href)
    {
        byte[] html = Html(Link("Search results", href));

        Assert.Equal("Search results", ClipLinks.Label(html, "https://example.org/search?a=1&b=2"));
    }

    [Theory]
    [InlineData("Tom &amp; Jerry", "Tom & Jerry")]
    [InlineData("1 &lt; 2", "1 < 2")]
    [InlineData("2 &gt; 1", "2 > 1")]
    [InlineData("say &quot;hi&quot;", "say \"hi\"")]
    [InlineData("it&apos;s here", "it's here")]
    [InlineData("one&nbsp;two", "one two")]
    [InlineData("one&#10;two", "one two")]
    [InlineData("caf&#233;", "café")]
    [InlineData("caf&#xE9;", "café")]
    [InlineData("caf&#Xe9;", "café")]
    public void Character_references_in_the_words_are_decoded(string written, string shown)
    {
        Assert.Equal(shown, LabelOf(Link(written)));
    }

    [Theory]
    [InlineData("smile &#x1F600;")]
    [InlineData("smile &#128512;")]
    public void A_number_beyond_the_basic_plane_gives_the_whole_character(string written)
    {
        Assert.Equal("smile " + Emoji, LabelOf(Link(written)));
    }

    [Fact]
    public void Text_that_looks_like_a_tag_once_it_is_decoded_is_kept_as_words()
    {
        Assert.Equal("<b>bold</b> move", LabelOf(Link("&lt;b&gt;bold&lt;/b&gt; move")));
    }

    [Fact]
    public void What_a_reference_decodes_to_is_not_decoded_again()
    {
        // "&amp;lt;" is the five characters "&lt;" on the page, not a "<".
        Assert.Equal("a &lt; b", LabelOf(Link("a &amp;lt; b")));
    }

    [Theory]
    [InlineData("fish &bogus; chips")]
    [InlineData("Tom & Jerry")]
    [InlineData("a &#0; b")]
    [InlineData("a &#xD800; b")]
    [InlineData("a &#1114112; b")]
    [InlineData("a &#xFFFFFFFF; b")]
    [InlineData("a &#; b")]
    [InlineData("a &#x; b")]
    [InlineData("a &#-65; b")]
    [InlineData("a &#99999999999; b")]
    [InlineData("a &verylongname; b")]
    public void A_reference_that_is_not_known_or_stands_for_no_character_is_left_as_it_is_written(string words)
    {
        Assert.Equal(words, LabelOf(Link(words)));
    }

    [Theory]
    [InlineData("""<a href="https://example.org/page">Example page</a> <a href="https://example.org/page">Example page</a>""")]
    [InlineData("""<a href="https://example.org/page">Example page</a> <a href="https://example.com/other">Other</a>""")]
    [InlineData("""<a href="https://example.com/other">Other</a> <a href="https://example.org/page">Example page</a>""")]
    public void Two_links_give_no_label_wherever_they_go(string fragment)
    {
        Assert.Null(LabelOf(fragment));
    }

    [Fact]
    public void A_link_inside_a_link_is_a_second_link()
    {
        Assert.Null(LabelOf("""<a href="https://example.org/page">Example <a href="https://example.com/other">inner</a> page</a>"""));
    }

    [Fact]
    public void One_link_that_goes_elsewhere_gives_no_label()
    {
        Assert.Null(LabelOf("""<a href="https://example.com/other">Example page</a>"""));
    }

    [Theory]
    [InlineData("<p>Example page</p>")]
    [InlineData("<a>Example page</a>")]
    [InlineData("""<a name="page">Example page</a>""")]
    [InlineData("""<abbr href="https://example.org/page">Example page</abbr>""")]
    public void HTML_that_holds_no_link_gives_no_label(string fragment)
    {
        Assert.Null(LabelOf(fragment));
    }

    [Theory]
    [InlineData("<a>Top</a>")]
    [InlineData("""<a name="top">Top</a>""")]
    [InlineData("""<abbr href="https://example.com/other">Other</abbr>""")]
    [InlineData("""<area href="https://example.com/other">""")]
    public void An_a_tag_without_an_href_and_another_tag_with_one_are_not_counted_as_links(string notALink)
    {
        Assert.Equal("Example page", LabelOf(notALink + LinkToPage));
    }

    [Fact]
    public void A_link_inside_a_comment_is_not_counted()
    {
        Assert.Equal("Example page", LabelOf("""<!-- <a href="https://example.com/other">Other</a> -->""" + LinkToPage));
    }

    [Fact]
    public void A_link_that_stands_only_inside_a_comment_gives_no_label()
    {
        Assert.Null(LabelOf("<!-- " + LinkToPage + " -->"));
    }

    [Theory]
    [InlineData("https://example.org/", "https://example.org")]
    [InlineData("https://example.org", "https://example.org/")]
    public void With_and_without_the_slash_after_the_host_is_the_same_address(string href, string address)
    {
        Assert.Equal("Example", ClipLinks.Label(Html(Link("Example", href)), address));
    }

    [Theory]
    [InlineData("https://example.org/page")]
    [InlineData("  https://example.org/page\r\n")]
    [InlineData("<span>https://example.org/page</span>")]
    public void Words_that_are_the_address_itself_give_no_label(string words)
    {
        Assert.Null(LabelOf(Link(words)));
    }

    [Theory]
    [InlineData("https://example.org", "https://example.org/")]
    [InlineData("https://example.org/", "https://example.org")]
    public void Words_that_differ_from_the_address_only_by_the_slash_after_the_host_give_no_label(string words, string address)
    {
        Assert.Null(ClipLinks.Label(Html(Link(words, address)), address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("&nbsp;")]
    [InlineData("&#x202E;")]
    [InlineData("""<img src="https://example.org/logo.png">""")]
    public void Words_with_nothing_to_show_give_no_label(string words)
    {
        Assert.Null(LabelOf(Link(words)));
    }

    [Fact]
    public void Words_of_exactly_the_longest_length_are_kept_whole()
    {
        string words = new string('w', ClipLinks.MaxLabelLength);

        Assert.Equal(words, LabelOf(Link(words)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Longer_words_are_cut_to_the_longest_length_and_end_in_an_ellipsis(int over)
    {
        string words = new string('w', ClipLinks.MaxLabelLength + over);

        string? label = LabelOf(Link(words));

        Assert.Equal(new string('w', ClipLinks.MaxLabelLength - 1) + Ellipsis, label);
    }

    [Fact]
    public void An_emoji_that_the_cut_would_fall_inside_is_dropped_whole()
    {
        // The emoji takes two positions, and only one is left before the ellipsis.
        string start = new string('w', ClipLinks.MaxLabelLength - 2);

        Assert.Equal(start + Ellipsis, LabelOf(Link(start + Emoji + " and many more words after it")));
    }

    [Fact]
    public void An_emoji_that_just_fits_before_the_cut_is_kept_whole()
    {
        string start = new string('w', ClipLinks.MaxLabelLength - 3);

        Assert.Equal(start + Emoji + Ellipsis, LabelOf(Link(start + Emoji + " and many more words after it")));
    }

    [Fact]
    public void A_character_that_turns_the_text_around_is_taken_out_of_the_words()
    {
        Assert.Equal("Example page", LabelOf(Link("Exam" + TurnsTextAround + "ple page")));
    }

    [Fact]
    public void A_character_that_turns_the_text_around_is_taken_out_too_when_it_is_written_as_a_reference()
    {
        Assert.Equal("Example page", LabelOf(Link("Exam&#x202E;ple page")));
    }

    [Fact]
    public void Words_outside_ASCII_are_read_as_UTF_8()
    {
        string words = "Grüße aus dem Café " + Emoji;

        Assert.Equal(words, LabelOf(Link(words)));
    }

    [Fact]
    public void A_link_that_is_never_closed_takes_the_rest_of_the_text_as_its_words()
    {
        Assert.Equal("Example page and the rest", LabelOf("""<a href="https://example.org/page">Example page</p><p>and the rest"""));
    }

    [Fact]
    public void The_end_of_another_tag_whose_name_starts_with_a_does_not_end_the_link()
    {
        Assert.Equal("Example page here", LabelOf(Link("Example <abbr>page</abbr> here")));
    }

    [Fact]
    public void An_end_tag_with_a_space_in_it_ends_the_link()
    {
        Assert.Equal("Example page", LabelOf("""<a href="https://example.org/page">Example page</a >and more"""));
    }

    [Fact]
    public void No_HTML_gives_no_label()
    {
        Assert.Null(ClipLinks.Label(null, Page));
    }

    [Fact]
    public void Empty_HTML_gives_no_label()
    {
        Assert.Null(ClipLinks.Label([], Page));
    }

    [Fact]
    public void HTML_of_exactly_the_largest_size_is_searched()
    {
        byte[] html = HtmlOfLength(ClipLinks.MaxHtmlBytes);

        Assert.Equal(ClipLinks.MaxHtmlBytes, html.Length);
        Assert.Equal("Example page", ClipLinks.Label(html, Page));
    }

    [Fact]
    public void HTML_one_byte_larger_than_that_gives_no_label()
    {
        byte[] html = HtmlOfLength(ClipLinks.MaxHtmlBytes + 1);

        Assert.Equal(ClipLinks.MaxHtmlBytes + 1, html.Length);
        Assert.Null(ClipLinks.Label(html, Page));
    }

    [Fact]
    public void A_block_without_a_single_tag_gives_no_label()
    {
        byte[] html = Encoding.UTF8.GetBytes(HeaderWith(Ten(35)) + "Example page https://example.org/page");

        Assert.Null(ClipLinks.Label(html, Page));
    }

    [Fact]
    public void HTML_without_a_header_starts_at_its_first_tag()
    {
        Assert.Equal("Example page", ClipLinks.Label(Encoding.UTF8.GetBytes(LinkToPage), Page));
    }

    [Fact]
    public void Other_lines_in_the_header_such_as_the_page_it_was_copied_from_are_not_links()
    {
        Assert.Equal("Example page", ClipLinks.Label(Html(LinkToPage, "SourceURL:https://example.com/elsewhere\r\n"), Page));
    }

    [Fact]
    public void The_page_starts_where_StartHTML_says()
    {
        // A link stands between the header and the place the header points at. Were the search
        // to start at the first tag, there would be two links and so no label.
        const string skipped = """<a href="https://example.com/other">Other</a>""";
        int start = Encoding.UTF8.GetByteCount(HeaderWith(Ten(0)) + skipped);

        byte[] html = Encoding.UTF8.GetBytes(HeaderWith(Ten(start)) + skipped + "<html><body>" + LinkToPage + "</body></html>");

        Assert.Equal("Example page", ClipLinks.Label(html, Page));
    }

    [Fact]
    public void StartHTML_is_counted_in_bytes_not_in_characters()
    {
        // Each of the 200 letters before the page is two bytes in UTF-8. Counted in characters
        // the number would point 200 characters too far, beyond the whole link.
        string skipped = """<a href="https://example.com/other">""" + new string((char)0xE9, 200) + "</a>";
        int start = Encoding.UTF8.GetByteCount(HeaderWith(Ten(0)) + skipped);

        byte[] html = Encoding.UTF8.GetBytes(HeaderWith(Ten(start)) + skipped + LinkToPage);

        Assert.Equal("Example page", ClipLinks.Label(html, Page));
    }

    [Theory]
    [InlineData("9999999999")]
    [InlineData("99999999999999999999999999999999999999")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("none")]
    public void With_a_StartHTML_that_is_no_number_or_points_far_outside_the_block_the_page_starts_at_its_first_tag(string startHtml)
    {
        byte[] html = Encoding.UTF8.GetBytes(HeaderWith(startHtml) + LinkToPage);

        Assert.Equal("Example page", ClipLinks.Label(html, Page));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void With_a_StartHTML_that_points_at_the_end_of_the_block_or_just_beyond_the_page_starts_at_its_first_tag(int beyond)
    {
        int length = Encoding.UTF8.GetByteCount(HeaderWith(Ten(0)) + LinkToPage);

        byte[] html = Encoding.UTF8.GetBytes(HeaderWith(Ten(length + beyond)) + LinkToPage);

        Assert.Equal("Example page", ClipLinks.Label(html, Page));
    }

    [Fact]
    public void StartHTML_written_on_the_page_after_the_first_tag_is_not_taken_for_the_header()
    {
        // The number on the page points beyond the link. Were it believed, the search would
        // start there and find no link.
        int afterLink = Encoding.UTF8.GetByteCount(LinkToPage);

        byte[] html = Encoding.UTF8.GetBytes(LinkToPage + "<p>StartHTML:" + Ten(afterLink) + "</p>");

        Assert.Equal("Example page", ClipLinks.Label(html, Page));
    }

    // The label is worked out on the line that records every copy, and a web page can put any
    // HTML beside an address. Each block below is as large as a block may be. Code that takes
    // time in proportion to the size does the work in milliseconds. Code that looks to the end
    // of the block again from each of its characters takes more than ten seconds, and four
    // times as long for every doubling, so the bound cannot fail either way by chance.
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(5);

    /// <summary>The unit written over and over to exactly this many characters, spaces after the last whole unit.</summary>
    private static string Repeated(string unit, int length) =>
        string.Concat(Enumerable.Repeat(unit, length / unit.Length)) + new string(' ', length % unit.Length);

    /// <summary>A block of exactly <see cref="ClipLinks.MaxHtmlBytes"/> bytes whose piece of HTML is the start, then the unit repeated to fill the block, then the end. All ASCII.</summary>
    private static byte[] FullSizeBlock(string start, string unit, string end)
    {
        int room = ClipLinks.MaxHtmlBytes - Html("").Length - start.Length - end.Length;
        byte[] html = Html(start + Repeated(unit, room) + end);

        Assert.Equal(ClipLinks.MaxHtmlBytes, html.Length);
        return html;
    }

    private static void AssertLabelQuickly(byte[] html, string? expected)
    {
        var clock = Stopwatch.StartNew();
        string? label = ClipLinks.Label(html, Page);
        clock.Stop();

        Assert.True(clock.Elapsed < Quickly, $"The label took {clock.Elapsed.TotalSeconds:F1} seconds.");
        Assert.Equal(expected, label);
    }

    [Fact]
    public void A_full_size_block_with_an_address_of_ampersands_is_labelled_quickly()
    {
        // Each "&" could be the start of a reference, and a ";" after it ends one.
        AssertLabelQuickly(FullSizeBlock("<a href=\"", "&", "\">Example page</a>"), null);
    }

    [Fact]
    public void A_full_size_block_with_words_of_ampersands_is_labelled_quickly()
    {
        AssertLabelQuickly(FullSizeBlock("""<a href="https://example.org/page">""", "&", "</a>"), new string('&', ClipLinks.MaxLabelLength - 1) + Ellipsis);
    }

    [Fact]
    public void A_full_size_block_with_words_of_comments_each_followed_by_a_quote_is_labelled_quickly()
    {
        // The two quotes of one unit close each other, and the ">" that ends the comment stands
        // between them. Read as a tag, the text from any "<" has no ">" that ends it, and a
        // comment is not a tag.
        string quotes = string.Concat(Enumerable.Repeat("\" ", (ClipLinks.MaxLabelLength - 1) / 2)) + "\"" + Ellipsis;

        AssertLabelQuickly(FullSizeBlock("""<a href="https://example.org/page">""", "<!--\"-->\"", "</a>"), quotes);
    }

    [Fact]
    public void A_full_size_block_with_words_of_less_than_signs_is_labelled_quickly()
    {
        AssertLabelQuickly(FullSizeBlock("""<a href="https://example.org/page">""", "<", "</a>"), new string('<', ClipLinks.MaxLabelLength - 1) + Ellipsis);
    }

    [Fact]
    public void A_full_size_block_with_words_of_a_tag_start_that_is_never_ended_is_labelled_quickly()
    {
        // "<a <a <a" is one tag that runs to the next ">", and that is the one in "</a>", so the
        // words are all of this up to the "</a" inside it.
        byte[] html = FullSizeBlock("""<a href="https://example.org/page">""", "<a ", "</a>");

        var clock = Stopwatch.StartNew();
        string? label = ClipLinks.Label(html, Page);
        clock.Stop();

        Assert.True(clock.Elapsed < Quickly, $"The label took {clock.Elapsed.TotalSeconds:F1} seconds.");
        Assert.NotNull(label);
        Assert.Equal(ClipLinks.MaxLabelLength, label.Length);
        Assert.StartsWith("<a <a <a ", label);
        Assert.EndsWith(Ellipsis.ToString(), label);
    }

    // The links are counted in a way that is close to a browser's reading of the tags, or the
    // words of a second link could become the label of the first.
    [Fact]
    public void A_tag_name_ends_at_a_slash_so_an_a_followed_by_a_slash_is_a_link()
    {
        Assert.Equal("Example page", LabelOf("""<a/href="https://example.org/page">Example page</a>"""));
    }

    [Fact]
    public void A_second_link_written_with_a_slash_before_its_href_is_counted()
    {
        Assert.Null(LabelOf("""<a href="https://example.org/page"><a/href="https://other.example/">Words of B</a>"""));
    }

    [Fact]
    public void A_less_than_sign_that_starts_no_tag_cannot_hide_the_link_after_it()
    {
        Assert.Equal("words", LabelOf("""1 < 2 <a href="https://example.org/page">words</a>"""));
    }

    [Theory]
    [InlineData("1 < 2 and 3 > 2")]
    [InlineData("I <3 you")]
    [InlineData("a <= b >= c")]
    [InlineData("<< not a tag >>")]
    [InlineData("x <")]
    public void A_less_than_sign_that_is_not_followed_by_a_letter_a_slash_a_bang_or_a_question_mark_is_text(string words)
    {
        Assert.Equal(words, LabelOf(Link(words)));
    }

    [Theory]
    [InlineData("Example</i> page")]
    [InlineData("Example<!DOCTYPE x> page")]
    [InlineData("Example<?x y?> page")]
    [InlineData("Example<I> page")]
    public void A_less_than_sign_followed_by_a_letter_a_slash_a_bang_or_a_question_mark_starts_a_tag(string words)
    {
        Assert.Equal("Example page", LabelOf(Link(words)));
    }

    // Characters that show nothing and are not "format" characters, so that a look for control
    // and format characters does not find them: the single ones, and both ends of each range.
    // Written as numbers, never as the characters.
    public static IEnumerable<object[]> InvisibleButNotFormat() =>
        new[]
        {
            0x034F, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180F, 0x2800, 0x3164, 0xFFA0,
            0xFE00, 0xFE0F, 0xE0100, 0xE01EF,

            // Assigned, and yet they draw nothing: the Khitan small script filler and the
            // musical null notehead.
            0x16FE4, 0x1D159,
        }.Select(code => new object[] { code });

    // Code points that are not assigned to any character, and private-use ones, which stand for
    // whatever the font that is in use says. Neither can be known to show anything.
    [Theory]
    [InlineData(0x2065)]
    [InlineData(0xFFF0)]
    [InlineData(0xE0000)]
    [InlineData(0xE0080)]
    [InlineData(0xE01F0)]
    [InlineData(0xE0FFF)]
    [InlineData(0xE000)]
    [InlineData(0xF0000)]
    public void A_character_that_is_not_assigned_or_is_for_private_use_makes_it_not_a_link(int code)
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + char.ConvertFromUtf32(code) + "b"));
    }

    [Theory]
    [MemberData(nameof(InvisibleButNotFormat))]
    public void A_character_that_shows_nothing_but_is_not_a_format_character_makes_it_not_a_link(int code)
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + char.ConvertFromUtf32(code) + "b"));
    }

    [Theory]
    [InlineData(0x3164)]
    [InlineData(0xE0100)]
    public void Such_a_character_in_the_host_makes_it_not_a_link_too(int code)
    {
        Assert.Null(ClipLinks.Address("https://exam" + char.ConvertFromUtf32(code) + "ple.org/a"));
    }

    [Theory]
    [InlineData(0xE0041)]
    [InlineData(0xE0001)]
    public void A_format_character_outside_the_basic_plane_makes_it_not_a_link(int code)
    {
        // Each of these is two UTF-16 characters, and each of the two alone is a surrogate.
        Assert.Null(ClipLinks.Address("https://example.org/a" + char.ConvertFromUtf32(code) + "b"));
        Assert.Null(ClipLinks.Address("https://exam" + char.ConvertFromUtf32(code) + "ple.org/a"));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void Half_of_a_surrogate_pair_with_no_partner_makes_it_not_a_link(int half)
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + (char)half + "b"));
        Assert.Null(ClipLinks.Address("https://example.org/a" + (char)half));
        Assert.Null(ClipLinks.Address("https://exa" + (char)half + "mple.org/a"));
    }

    [Fact]
    public void A_low_half_before_a_high_half_is_no_pair_so_the_text_is_not_a_link()
    {
        Assert.Null(ClipLinks.Address("https://example.org/a" + (char)0xDE00 + (char)0xD83D + "b"));
    }

    [Theory]
    [InlineData(0x180A)]
    [InlineData(0x2801)]
    [InlineData(0x3165)]
    [InlineData(0xFFA1)]
    [InlineData(0xFE10)]
    [InlineData(0xE9)]
    public void The_characters_next_to_the_ones_that_show_nothing_are_still_allowed(int code)
    {
        string address = "https://example.org/a" + char.ConvertFromUtf32(code) + "b";

        Assert.Equal(address, ClipLinks.Address(address));
    }

    [Fact]
    public void A_whole_pair_that_shows_something_is_allowed()
    {
        string address = "https://example.org/a" + Emoji + "b";

        Assert.Equal(address, ClipLinks.Address(address));
    }

    // A label is shown beside the copied address, so it must not look like another address,
    // and must not show nothing.
    [Theory]
    [InlineData("https://bank.example/login")]
    [InlineData("http://bank.example")]
    [InlineData("  https://bank.example/login\r\n")]
    [InlineData("<span>https://bank.example/login</span>")]
    [InlineData("bank.example")]
    [InlineData("www.bank.example/login")]
    [InlineData("README.md")]
    public void Words_that_look_like_another_address_give_no_label(string words)
    {
        const string evil = "https://evil.example/x";

        Assert.Null(ClipLinks.Label(Html(Link(words, evil)), evil));
    }

    [Theory]
    [InlineData("Visit bank.example now")]
    [InlineData("Sign in to www.bank.example/login")]
    [InlineData("Example page. Read on")]
    [InlineData("Read the docs")]
    [InlineData("Mr. Otter's page, v1.2")]
    [InlineData("bank.example |")]
    [InlineData("Mr. Otter")]
    public void Words_with_white_space_in_them_are_kept_even_when_one_of_them_has_a_dot(string words)
    {
        // A label that holds a dot does not lose its label for that: the real address is
        // always shown beside it. One of its words being an address is another matter.
        Assert.Equal(words, LabelOf(Link(words)));
    }

    // The label is shown beside the address that was copied, but a word in it that is an
    // address would still be read as the place the link goes to.
    [Theory]
    [InlineData("https://bank.example/login -")]
    [InlineData("- https://bank.example/login")]
    [InlineData("Sign in at https://bank.example/login now")]
    [InlineData("http://bank.example Example page")]
    [InlineData("Example <b>page</b> https://bank.example/login")]
    [InlineData("Example&nbsp;page&#10;https://bank.example/login")]
    public void Words_with_another_address_among_them_give_no_label(string words)
    {
        const string evil = "https://evil.example/x";

        Assert.Null(ClipLinks.Label(Html(Link(words, evil)), evil));
    }

    [Fact]
    public void An_address_among_the_words_that_is_cut_off_by_the_end_of_the_label_is_not_shown_and_does_not_matter()
    {
        // The address starts after the cut, so what is shown holds none.
        string words = new string('w', ClipLinks.MaxLabelLength + 10) + " https://bank.example/login";

        Assert.Equal(new string('w', ClipLinks.MaxLabelLength - 1) + Ellipsis, LabelOf(Link(words)));
    }

    [Fact]
    public void Words_that_are_cut_short_into_something_that_looks_like_an_address_give_no_label()
    {
        // The white space comes after the cut, so what is shown has none.
        string words = "https://bank.example/" + new string('x', 250) + " and more words";

        Assert.Null(LabelOf(Link(words)));
    }

    [Theory]
    [MemberData(nameof(InvisibleButNotFormat))]
    public void Words_of_nothing_but_a_character_that_shows_nothing_give_no_label(int code)
    {
        string character = char.ConvertFromUtf32(code);

        Assert.Null(LabelOf(Link(character)));
        Assert.Null(LabelOf(Link("  " + character + " " + character + "  ")));
    }

    [Theory]
    [MemberData(nameof(InvisibleButNotFormat))]
    public void Such_a_character_is_taken_out_of_the_words_around_it(int code)
    {
        Assert.Equal("Example page", LabelOf(Link("Exam" + char.ConvertFromUtf32(code) + "ple page")));
    }

    [Theory]
    [MemberData(nameof(InvisibleButNotFormat))]
    public void Such_a_character_written_as_a_reference_is_taken_out_too(int code)
    {
        Assert.Equal("Example page", LabelOf(Link($"Exam&#x{code:X};ple page")));
        Assert.Null(LabelOf(Link($"&#x{code:X};")));
    }

    // C0 and C1 control characters: ESC, BEL, DEL and U+009B.
    [Theory]
    [InlineData(0x1B)]
    [InlineData(0x07)]
    [InlineData(0x7F)]
    [InlineData(0x9B)]
    public void A_control_character_in_the_words_never_reaches_the_label(int code)
    {
        Assert.Equal("Example page", LabelOf(Link("Exam" + (char)code + "ple page")));
        Assert.Equal("Example page", LabelOf(Link($"Exam&#{code};ple page")));
        Assert.Equal("Example page", LabelOf(Link($"Exam&#x{code:X};ple page")));
    }

    [Fact]
    public void U_0085_is_white_space_as_well_as_a_control_character_so_it_becomes_a_space()
    {
        Assert.Equal("Exam ple page", LabelOf(Link("Exam" + (char)0x85 + "ple page")));
        Assert.Equal("Exam ple page", LabelOf(Link("Exam&#133;ple page")));
    }

    [Fact]
    public void Words_of_nothing_but_control_characters_give_no_label()
    {
        Assert.Null(LabelOf(Link("" + (char)0x1B + (char)0x07 + (char)0x7F + (char)0x9B)));
        Assert.Null(LabelOf(Link("&#27;&#7;&#127;&#155;")));
    }

    private static string Cp(int code) => char.ConvertFromUtf32(code);

    // One rule decides whether the words of a link are fit to show, and three places apply it:
    // capture (ClipLinks.Label), an entry made from a record (ClipEntry.From) and the view
    // (ClipboardViewModel). The same words, as a page or a record wrote them, and what is shown
    // of them: null when there is no label and the address stands alone. The tests of the other
    // two places read this list, so that the three cannot disagree. Every word is plain text
    // that stands as it is inside a link's HTML; characters that must not stand here as
    // themselves are written as numbers.
    public static TheoryData<string, string?> LabelCases() => new()
    {
        // Fit to show, and what is shown of them.
        { "Example page", "Example page" },
        { "Mr. Otter's page, v1.2", "Mr. Otter's page, v1.2" },
        { "Visit bank.example now", "Visit bank.example now" },
        { "About https and http", "About https and http" },
        { "Grüße aus dem Café", "Grüße aus dem Café" },
        { "  Example   page  ", "Example page" },
        { "Name\tAmount", "Name Amount" },
        { "Name\r\nAmount", "Name Amount" },
        { "Name" + Cp(0x2028) + "Amount", "Name Amount" },
        { "Exam" + Cp(0x202E) + "ple page", "Example page" },
        { "Exam" + Cp(0x200B) + "ple page", "Example page" },
        { "Exam" + Cp(0x3164) + "ple page", "Example page" },
        { "Exam" + Cp(0xFE0F) + "ple page", "Example page" },
        { "Exam" + Cp(0xE000) + "ple page", "Example page" },
        { "Exam" + Cp(0x2065) + "ple page", "Example page" },
        { "Exam" + Cp(0xF0000) + "ple page", "Example page" },
        { "Example " + Cp(0xE000) + " page", "Example page" },

        // No label: an address anywhere in the words, however it is written or hidden.
        { "https://bank.example/login", null },
        { "HTTP://BANK.EXAMPLE", null },
        { "HtTpS://bank.example", null },
        { "Sign in at https://bank.example/login now", null },
        { "Example http://bank.example", null },
        { "ht" + Cp(0x200B) + "tps://bank.example/login", null },
        { "h" + Cp(0xE000) + "ttp://bank.example", null },
        { Cp(0x0301) + "https://bank.example/login", null },
        { "Example" + Cp(0x0301) + "https://bank.example/login", null },

        // No label: words that are the name of a host.
        { "bank.example", null },
        { "www.bank.example/login", null },
        { "README.md", null },

        // No label: nothing is left once what shows nothing is out.
        { "", null },
        { "   ", null },
        { Cp(0x200B), null },
        { Cp(0x3164), null },
        { Cp(0x2065), null },
        { Cp(0xE000), null },
        { Cp(0xF0000), null },
        { "  " + Cp(0xE000) + " " + Cp(0x2065) + "  ", null },
    };

    [Theory]
    [MemberData(nameof(LabelCases))]
    public void Capture_makes_the_label_of_the_words_by_the_one_rule(string words, string? shown)
    {
        Assert.Equal(shown, LabelOf(Link(words)));
    }

    [Theory]
    [MemberData(nameof(LabelCases))]
    public void The_label_that_is_fit_to_show_is_the_one_rule_itself(string words, string? shown)
    {
        Assert.Equal(shown, ClipLinks.ShownLabel(words));
    }

    [Fact]
    public void No_words_at_all_are_no_label()
    {
        Assert.Null(ClipLinks.ShownLabel(null));
    }

    [Fact]
    public void An_address_that_a_mark_is_stuck_to_is_no_label_wherever_the_mark_stands()
    {
        // The mark is a combining acute accent. It is what makes the text no address (it does
        // not start with the scheme) and still reads as one.
        Assert.Null(ClipLinks.ShownLabel("a " + Cp(0x0301) + "https://bank.example"));
        Assert.Null(ClipLinks.ShownLabel("a" + Cp(0x0301) + "https://bank.example"));
        Assert.Null(ClipLinks.ShownLabel(Cp(0x0301) + "https://"));
    }

    [Fact]
    public void The_scheme_is_looked_for_in_the_words_that_are_shown_and_not_in_the_words_that_are_cut_off()
    {
        string words = new string('w', ClipLinks.MaxLabelLength + 10) + " https://bank.example/login";

        Assert.Equal(new string('w', ClipLinks.MaxLabelLength - 1) + Ellipsis, ClipLinks.ShownLabel(words));
    }

    [Fact]
    public void The_label_is_judged_as_it_is_cut_for_the_limit_it_is_shown_in()
    {
        // One word with a dot in it, so far as the first eighty characters go.
        string words = "bank.example/" + new string('x', 100) + " and more words";

        Assert.NotNull(ClipLinks.ShownLabel(words));
        Assert.Null(ClipLinks.ShownLabel(words, 80));
    }

    [Fact]
    public void Words_of_a_million_characters_are_made_a_label_at_once()
    {
        string words = string.Concat(Enumerable.Repeat("word" + Cp(0x200B) + "\t" + Cp(0xE000) + " ", 150_000));

        var clock = Stopwatch.StartNew();
        string? label = ClipLinks.ShownLabel(words);
        clock.Stop();

        Assert.True(clock.Elapsed < Quickly, $"The label took {clock.Elapsed.TotalSeconds:F1} seconds.");
        Assert.NotNull(label);
        Assert.Equal(ClipLinks.MaxLabelLength, label.Length);
    }
}
