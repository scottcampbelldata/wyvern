using Wyvern.Core.Library;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

public sealed class SnippetProviderTests : IDisposable
{
    private sealed class FakePaste : IPasteService
    {
        public List<string> Pasted { get; } = [];

        public void Paste(string text) => Pasted.Add(text);

        public void PasteAndKeep(IReadOnlyList<ClipboardBlock> blocks) =>
            throw new NotSupportedException("A snippet is never pasted this way.");
    }

    private sealed class FixedItems(params SearchItem[] items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);
    }

    // Thirteen hours ahead of UTC. The fake clock says noon on 3 October in UTC, which is one
    // in the morning on 4 October there, so a date taken from UTC by mistake shows up.
    private static readonly TimeZoneInfo FarEast =
        TimeZoneInfo.CreateCustomTimeZone("Wyvern test zone", TimeSpan.FromHours(13), "Wyvern test zone", "Wyvern test zone");

    private readonly TempDir _dir = new();
    private readonly FakePaste _paste = new();
    private readonly FakeClipboard _clipboard = new() { Text = "from the clipboard" };
    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeClock _clock = new();
    private LibrarySnapshot _library = LibrarySnapshot.Empty;

    public void Dispose() => _dir.Dispose();

    private SnippetProvider NewProvider() => new(() => _library, _paste, _clipboard, _launcher, _clock, FarEast);

    private void Load(params Snippet[] snippets) => _library = new LibrarySnapshot([], snippets, []);

    private static Snippet Snippet(string relativePath, string name, string body, string? keyword = null) =>
        new(relativePath, @"C:\lib\snippets\" + relativePath, name, keyword, body);

    [Fact]
    public void A_snippet_becomes_an_item_that_shows_its_first_line()
    {
        Load(Snippet(@"work\sig.MD", "Email signature", "Regards,\nScott"));

        var result = Assert.Single(NewProvider().Search("email"));

        Assert.Equal(@"snippet:work\sig.MD", result.Item.Id);
        Assert.Equal("Email signature", result.Item.Title);
        Assert.Equal("Regards,", result.Item.Subtitle);
        Assert.Equal(ItemKind.Snippet, result.Item.Kind);
        Assert.Equal("filetype:.md", result.Item.IconKey);
        Assert.Null(result.Item.Argument);
        Assert.Equal(FuzzyMatcher.Score("email", "Email signature"), result.Score);
    }

    [Theory]
    [InlineData("\r\n\r\n   \r\n  Second paragraph first  \r\nmore", "Second paragraph first")]
    [InlineData("one line", "one line")]
    [InlineData("", "")]
    [InlineData("  \n\t\n", "")]
    public void The_subtitle_is_the_first_line_with_something_on_it(string body, string expected)
    {
        Load(Snippet("a.md", "Alpha", body));

        Assert.Equal(expected, Assert.Single(NewProvider().Search("alpha")).Item.Subtitle);
    }

    [Fact]
    public void A_long_first_line_is_cut_to_fit_one_row()
    {
        Load(Snippet("a.md", "Alpha", new string('x', 500) + "\nsecond line"));

        string subtitle = Assert.Single(NewProvider().Search("alpha")).Item.Subtitle;

        Assert.Equal(SnippetProvider.MaxSubtitleLength, subtitle.Length);
        Assert.Equal(new string('x', 99) + "…", subtitle);
    }

    [Fact]
    public void A_long_first_line_is_never_cut_in_the_middle_of_a_character()
    {
        // The emoji is two UTF-16 characters, at positions 98 and 99. Cutting after position 98
        // would leave half of it in front of the ellipsis.
        Load(Snippet("a.md", "Alpha", new string('x', 98) + "\U0001F600 and then some more text to fill the row"));

        string subtitle = Assert.Single(NewProvider().Search("alpha")).Item.Subtitle;

        Assert.Equal(new string('x', 98) + "…", subtitle);
    }

    [Fact]
    public void Enter_pastes_the_body_with_its_placeholders_filled_in_local_time()
    {
        Load(Snippet("stamp.txt", "Stamp", "Seen {date} at {time}: {clipboard}"));
        var item = Assert.Single(NewProvider().Search("stamp")).Item;

        Assert.NotNull(item.Run);
        item.Run();

        Assert.Equal(["Seen 2026-10-04 at 01:00: from the clipboard"], _paste.Pasted);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public void Placeholders_are_filled_in_when_the_snippet_is_used_not_when_it_was_found()
    {
        Load(Snippet("stamp.txt", "Stamp", "{time} {clipboard}"));
        var item = Assert.Single(NewProvider().Search("stamp")).Item;
        Assert.NotNull(item.Run);

        _clock.Advance(TimeSpan.FromMinutes(90));
        _clipboard.Text = "copied since";
        item.Run();

        Assert.Equal(["02:30 copied since"], _paste.Pasted);
    }

    [Fact]
    public void The_local_time_follows_daylight_saving_in_the_owners_zone()
    {
        // One hour ahead of UTC, two in summer: daylight time runs from the last Sunday of March
        // to the last Sunday of October. Built here so the test does not depend on the zones
        // installed on the machine.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Wyvern test zone with summer time", TimeSpan.FromHours(1), "Wyvern test zone", "Wyvern test zone", "Wyvern test zone summer time", [rule]);
        Load(Snippet("stamp.txt", "Stamp", "{date} {time}"));
        var item = Assert.Single(new SnippetProvider(() => _library, _paste, _clipboard, _launcher, _clock, zone).Search("stamp")).Item;
        Assert.NotNull(item.Run);

        _clock.UtcNow = new DateTimeOffset(2026, 7, 10, 22, 30, 0, TimeSpan.Zero);
        item.Run();
        _clock.UtcNow = new DateTimeOffset(2026, 1, 10, 22, 30, 0, TimeSpan.Zero);
        item.Run();

        Assert.Equal(["2026-07-11 00:30", "2026-01-10 23:30"], _paste.Pasted);
    }

    [Fact]
    public void A_clipboard_without_text_puts_nothing_in()
    {
        _clipboard.Text = null;
        Load(Snippet("re.txt", "Reply", "Re: {clipboard}!"));
        var item = Assert.Single(NewProvider().Search("reply")).Item;

        Assert.NotNull(item.Run);
        item.Run();

        Assert.Equal(["Re: !"], _paste.Pasted);
    }

    [Fact]
    public void The_clipboard_is_left_alone_by_a_snippet_that_does_not_use_it()
    {
        Load(Snippet("sig.md", "Signature", "Regards {date}"));
        var item = Assert.Single(NewProvider().Search("sig")).Item;

        Assert.NotNull(item.Run);
        item.Run();

        Assert.Equal(0, _clipboard.Calls);
        Assert.Single(_paste.Pasted);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_fails_the_action_and_nothing_is_pasted()
    {
        _clipboard.BusyFor = 1;
        Load(Snippet("re.txt", "Reply", "Re: {clipboard}"));
        var item = Assert.Single(NewProvider().Search("reply")).Item;

        Assert.NotNull(item.Run);
        Assert.Throws<ClipboardBusyException>(item.Run);

        Assert.Empty(_paste.Pasted);
    }

    [Fact]
    public void Ctrl_Enter_opens_the_snippets_file_and_pastes_nothing()
    {
        Load(Snippet(@"work\sig.md", "Email signature", "Regards"));
        var item = Assert.Single(NewProvider().Search("email")).Item;

        Assert.NotNull(item.RunSecondary);
        item.RunSecondary();

        Assert.Equal([@"open C:\lib\snippets\work\sig.md"], _launcher.Calls);
        Assert.Empty(_paste.Pasted);
    }

    [Fact]
    public void A_snippet_is_found_by_its_keyword_whatever_its_name()
    {
        Load(Snippet("sig.md", "Email signature", "Regards", keyword: ";sig"), Snippet("addr.md", "Address", "1 Main St", keyword: ";addr"));
        var provider = NewProvider();

        var exact = Assert.Single(provider.Search(";SIG"));
        Assert.Equal("Email signature", exact.Item.Title);
        Assert.Equal(KeywordMatcher.ExactKeywordScore, exact.Score);

        Assert.Equal(["Email signature", "Address"], provider.Search(";").Select(result => result.Item.Title));
    }

    [Fact]
    public void Only_the_name_and_keyword_are_matched_never_the_body()
    {
        Load(Snippet("sig.md", "Email signature", "Regards from Zanzibar"));

        Assert.Empty(NewProvider().Search("zanzibar"));
        Assert.Single(NewProvider().Search("e"));
    }

    [Fact]
    public void A_library_loaded_again_is_searched_from_the_next_keystroke()
    {
        var provider = NewProvider();
        Load(Snippet("sig.md", "Signature", "old body"));
        var before = Assert.Single(provider.Search("sig")).Item;
        Assert.Same(before, provider.Search("si")[0].Item);

        Load(Snippet("sig.md", "Signature", "new body"));
        var after = Assert.Single(provider.Search("sig")).Item;

        Assert.Equal("new body", after.Subtitle);
        Assert.NotNull(after.Run);
        after.Run();
        Assert.Equal(["new body"], _paste.Pasted);
    }

    [Fact]
    public void At_equal_score_a_snippet_ranks_above_a_file()
    {
        Load(Snippet("budget.md", "Budget", "The budget"));
        var usage = new UsageStore(_dir.File("usage.json"), _clock);
        var file = new SearchItem(@"file:C:\Docs\Budget", "Budget", "", ItemKind.File, () => { });
        var engine = new SearchEngine([new FixedItems(file), NewProvider()], usage);

        Assert.Equal([ItemKind.Snippet, ItemKind.File], engine.Search("budget").Select(item => item.Kind));
    }
}
