using System.Globalization;
using Beacon.Core.Library;

namespace Beacon.Tests;

/// <summary>
/// The one rule for filling in a snippet's placeholders at the moment it is used, which the
/// launcher and auto-expand both go by.
/// </summary>
public class SnippetExpandNowTests
{
    // Thirteen hours ahead of UTC. The fake clock says noon on 3 October in UTC, which is one
    // in the morning on 4 October there, so a date taken from UTC by mistake shows up.
    private static readonly TimeZoneInfo FarEast =
        TimeZoneInfo.CreateCustomTimeZone("Beacon test zone", TimeSpan.FromHours(13), "Beacon test zone", "Beacon test zone");

    private readonly FakeClock _clock = new();
    private int _clipboardReads;

    private string? Clipboard()
    {
        _clipboardReads++;
        return "from the clipboard";
    }

    [Fact]
    public void The_date_and_the_time_are_those_of_the_clock_in_the_zone_that_is_given()
    {
        Assert.Equal("2026-10-04 01:00", SnippetExpander.ExpandNow("{date} {time}", _clock, FarEast, Clipboard));
    }

    [Fact]
    public void The_clock_is_read_each_time_so_a_later_use_gives_a_later_time()
    {
        Assert.Equal("01:00", SnippetExpander.ExpandNow("{time}", _clock, FarEast, Clipboard));

        _clock.Advance(TimeSpan.FromMinutes(75));

        Assert.Equal("02:15", SnippetExpander.ExpandNow("{time}", _clock, FarEast, Clipboard));
    }

    [Fact]
    public void Without_a_zone_the_machines_own_is_used()
    {
        // Worked out here from the same clock and the machine's zone, so the test holds on any machine.
        var local = TimeZoneInfo.ConvertTime(_clock.UtcNow, TimeZoneInfo.Local);
        string expected = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        Assert.Equal(expected, SnippetExpander.ExpandNow("{date} {time}", _clock, zone: null, Clipboard));
    }

    [Fact]
    public void The_clipboard_is_asked_for_once_and_only_when_the_body_uses_it()
    {
        Assert.Equal("Regards", SnippetExpander.ExpandNow("Regards", _clock, FarEast, Clipboard));
        Assert.Equal("on 2026-10-04", SnippetExpander.ExpandNow("on {date}", _clock, FarEast, Clipboard));
        Assert.Equal(0, _clipboardReads);

        Assert.Equal(
            "from the clipboard and from the clipboard",
            SnippetExpander.ExpandNow("{clipboard} and {clipboard}", _clock, FarEast, Clipboard));
        Assert.Equal(1, _clipboardReads);
    }

    [Fact]
    public void A_clipboard_without_text_puts_nothing_in()
    {
        Assert.Equal("[]", SnippetExpander.ExpandNow("[{clipboard}]", _clock, FarEast, () => null));
    }

    [Fact]
    public void Text_that_arrives_from_the_clipboard_is_not_expanded_again()
    {
        Assert.Equal("{date} {time}", SnippetExpander.ExpandNow("{clipboard}", _clock, FarEast, () => "{date} {time}"));
    }

    [Fact]
    public void It_gives_what_Expand_gives_for_the_same_moment()
    {
        const string Body = "Dear {name}, on {date} at {time}: {clipboard}";
        var moment = TimeZoneInfo.ConvertTime(_clock.UtcNow, FarEast);

        Assert.Equal(
            SnippetExpander.Expand(Body, moment, () => "from the clipboard"),
            SnippetExpander.ExpandNow(Body, _clock, FarEast, () => "from the clipboard"));
    }
}
