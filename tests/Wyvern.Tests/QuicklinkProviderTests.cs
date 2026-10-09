using Wyvern.Core.Library;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

public sealed class QuicklinkProviderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeProcessLauncher _launcher = new();
    private LibrarySnapshot _library = LibrarySnapshot.Empty;

    public void Dispose() => _dir.Dispose();

    private QuicklinkProvider NewProvider() => new(() => _library, _launcher);

    private void Load(params Quicklink[] quicklinks) => _library = new LibrarySnapshot(quicklinks, [], []);

    private sealed class FixedItems(params SearchItem[] items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);
    }

    [Fact]
    public void A_quicklink_becomes_an_item_that_shows_its_link_as_written()
    {
        Load(new Quicklink("Team wiki", "https://wiki.example.org/Home Page", null));

        var result = Assert.Single(NewProvider().Search("wiki"));

        Assert.Equal("quicklink:Team wiki", result.Item.Id);
        Assert.Equal("Team wiki", result.Item.Title);
        Assert.Equal("https://wiki.example.org/Home Page", result.Item.Subtitle);
        Assert.Equal(ItemKind.Quicklink, result.Item.Kind);
        Assert.Equal("filetype:.url", result.Item.IconKey);
        Assert.Null(result.Item.RunSecondary);
        Assert.Equal(FuzzyMatcher.Score("wiki", "Team wiki"), result.Score);
    }

    [Fact]
    public void Enter_opens_a_link_that_needs_nothing_typed()
    {
        Load(new Quicklink("Team wiki", "https://wiki.example.org", null));
        var item = Assert.Single(NewProvider().Search("wiki")).Item;

        Assert.Null(item.Argument);
        Assert.NotNull(item.Run);
        item.Run();

        Assert.Equal(["link https://wiki.example.org"], _launcher.Calls);
    }

    [Fact]
    public void A_link_with_a_placeholder_runs_only_with_an_argument_which_is_escaped_for_the_web()
    {
        Load(new Quicklink("Search GitHub", "https://github.com/search?q={query}", "gh"));
        var item = Assert.Single(NewProvider().Search("search")).Item;

        Assert.Null(item.Run);
        Assert.NotNull(item.Argument);
        Assert.Equal("Query", item.Argument.Prompt);
        Assert.Equal("https://github.com/search?q={query}", item.Subtitle);

        item.Argument.Run("tom & jerry");
        item.Argument.Run("");

        Assert.Equal(
            ["link https://github.com/search?q=tom%20%26%20jerry", "link https://github.com/search?q="],
            _launcher.Calls);
    }

    [Fact]
    public void A_local_link_gets_the_argument_unchanged()
    {
        Load(new Quicklink("Open note", @"C:\Notes\{query}.md", null));
        var item = Assert.Single(NewProvider().Search("note")).Item;

        Assert.NotNull(item.Argument);
        item.Argument.Run("tom & jerry");

        Assert.Equal([@"link C:\Notes\tom & jerry.md"], _launcher.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_link_that_comes_out_blank_is_not_handed_to_the_shell(string argument)
    {
        // Nothing but the placeholder, and nothing typed for it.
        Load(new Quicklink("Whatever I type", "{query}", null));
        var item = Assert.Single(NewProvider().Search("whatever")).Item;
        Assert.NotNull(item.Argument);

        Assert.Throws<InvalidOperationException>(() => item.Argument.Run(argument));

        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public void A_link_that_only_the_placeholder_makes_something_of_is_still_opened_when_text_is_typed()
    {
        Load(new Quicklink("Whatever I type", "{query}", null));
        var item = Assert.Single(NewProvider().Search("whatever")).Item;
        Assert.NotNull(item.Argument);

        item.Argument.Run(@"C:\Notes");

        Assert.Equal([@"link C:\Notes"], _launcher.Calls);
    }

    [Fact]
    public void A_quicklink_is_found_by_its_keyword_whatever_its_name()
    {
        Load(
            new Quicklink("Search GitHub", "https://github.com/search?q={query}", "gh"),
            new Quicklink("Team wiki", "https://wiki.example.org", "w"));
        var provider = NewProvider();

        var exact = Assert.Single(provider.Search("GH"));
        Assert.Equal("Search GitHub", exact.Item.Title);
        Assert.Equal(KeywordMatcher.ExactKeywordScore, exact.Score);

        var byStart = Assert.Single(provider.Search("g"), result => result.Item.Title == "Search GitHub");
        Assert.True(byStart.Score < KeywordMatcher.ExactKeywordScore);
    }

    [Fact]
    public void There_is_no_minimum_query_length()
    {
        Load(new Quicklink("Team wiki", "https://wiki.example.org", null));

        Assert.Single(NewProvider().Search("t"));
    }

    [Fact]
    public void What_does_not_match_is_not_returned()
    {
        Load(new Quicklink("Team wiki", "https://wiki.example.org", "w"));

        Assert.Empty(NewProvider().Search("xyz"));
        Assert.Empty(NewProvider().Search("example"));
    }

    [Fact]
    public void A_library_loaded_again_is_searched_from_the_next_keystroke()
    {
        var provider = NewProvider();
        Load(new Quicklink("Team wiki", "https://wiki.example.org", null));
        Assert.Single(provider.Search("wiki"));

        Load(new Quicklink("Company wiki", "https://wiki.example.com", null), new Quicklink("Mail", "https://mail.example.com", null));

        Assert.Equal("Company wiki", Assert.Single(provider.Search("wiki")).Item.Title);
        Assert.Single(provider.Search("mail"));
    }

    [Fact]
    public void Rows_are_made_once_for_each_load_not_for_each_search()
    {
        Load(new Quicklink("Team wiki", "https://wiki.example.org", null));
        var provider = NewProvider();

        var first = provider.Search("wiki")[0].Item;
        var second = provider.Search("wik")[0].Item;

        Assert.Same(first, second);
    }

    [Fact]
    public void Typing_a_keyword_in_full_puts_its_item_first_even_against_a_favourite_with_a_matching_name()
    {
        Load(new Quicklink("Search GitHub", "https://github.com/search?q={query}", "gh"));
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var favourite = new SearchItem("app:gh", "gh", "", ItemKind.App, () => { });
        for (int i = 0; i < 20; i++)
        {
            usage.RecordUse(favourite.Id);
        }

        var engine = new SearchEngine([new FixedItems(favourite), NewProvider()], usage);

        Assert.Equal(["Search GitHub", "gh"], engine.Search("gh").Select(item => item.Title));
        // One letter short of the keyword, the favourite is ahead again.
        Assert.Equal("gh", engine.Search("g")[0].Title);
    }

    [Fact]
    public void At_equal_score_a_quicklink_ranks_above_a_file()
    {
        Load(new Quicklink("Budget", "https://budget.example.org", null));
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var file = new SearchItem(@"file:C:\Docs\Budget", "Budget", "", ItemKind.File, () => { });
        var engine = new SearchEngine([new FixedItems(file), NewProvider()], usage);

        Assert.Equal([ItemKind.Quicklink, ItemKind.File], engine.Search("budget").Select(item => item.Kind));
    }
}
