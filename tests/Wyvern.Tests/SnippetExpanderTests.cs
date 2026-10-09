using System.Globalization;
using Wyvern.Core.Library;

namespace Wyvern.Tests;

public class SnippetExpanderTests
{
    // Five to midnight would not tell a 12-hour clock from a 24-hour one; five past three in the afternoon does.
    private static readonly DateTimeOffset Now = new(2026, 3, 5, 15, 4, 59, TimeSpan.FromHours(13));

    private int _clipboardReads;

    private string Expand(string body, string? clipboard = "from the clipboard") =>
        SnippetExpander.Expand(body, Now, () =>
        {
            _clipboardReads++;
            return clipboard;
        });

    [Fact]
    public void A_body_without_placeholders_is_pasted_as_it_is()
    {
        Assert.Equal("Regards,\r\nScott", Expand("Regards,\r\nScott"));
        Assert.Equal("", Expand(""));
        Assert.Equal(0, _clipboardReads);
    }

    [Fact]
    public void Date_is_year_month_day_and_time_is_on_the_24_hour_clock()
    {
        Assert.Equal("Dated 2026-03-05.", Expand("Dated {date}."));
        Assert.Equal("At 15:04.", Expand("At {time}."));
        Assert.Equal("2026-03-0515:04", Expand("{date}{time}"));
    }

    [Fact]
    public void The_date_and_time_do_not_depend_on_the_language_of_the_machine()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Saudi Arabia's default calendar is not the Gregorian one.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            Assert.Equal("2026-03-05 15:04", Expand("{date} {time}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Clipboard_is_replaced_by_the_text_on_the_clipboard()
    {
        Assert.Equal("Re: from the clipboard", Expand("Re: {clipboard}"));
    }

    [Fact]
    public void A_clipboard_without_text_puts_nothing_in()
    {
        Assert.Equal("Re: !", Expand("Re: {clipboard}!", clipboard: null));
    }

    [Fact]
    public void The_clipboard_is_read_once_however_often_it_is_used_and_not_at_all_when_it_is_not()
    {
        Assert.Equal("from the clipboard and from the clipboard", Expand("{clipboard} and {clipboard}"));
        Assert.Equal(1, _clipboardReads);

        Expand("{date} {time} {other}");
        Assert.Equal(1, _clipboardReads);
    }

    [Fact]
    public void Text_that_arrives_from_the_clipboard_is_not_expanded_again()
    {
        Assert.Equal(
            "Quote: {date} {time} {clipboard} | 2026-03-05",
            Expand("Quote: {clipboard} | {date}", clipboard: "{date} {time} {clipboard}"));
    }

    [Theory]
    [InlineData("{name}")]
    [InlineData("{Date}")]
    [InlineData("{ date }")]
    [InlineData("{dat")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{}")]
    [InlineData("if (x) { return; }")]
    [InlineData("{clip board}")]
    public void Anything_else_in_braces_is_left_exactly_as_written(string body)
    {
        Assert.Equal(body, Expand(body));
        Assert.Equal(0, _clipboardReads);
    }

    [Fact]
    public void A_placeholder_right_after_a_stray_brace_is_still_found()
    {
        Assert.Equal("{2026-03-05}", Expand("{{date}}"));
        Assert.Equal("{x 15:04", Expand("{x {time}"));
    }
}
