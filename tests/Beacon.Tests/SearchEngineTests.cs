using Beacon.Core.Search;

namespace Beacon.Tests;

public sealed class SearchEngineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly UsageStore _usage;

    public SearchEngineTests()
    {
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
    }

    public void Dispose() => _dir.Dispose();

    private static SearchItem Item(string id, string title, ItemKind kind = ItemKind.App) =>
        new(id, title, "", kind, () => { });

    private sealed class FixedProvider(params SearchResult[] results) : ISearchProvider
    {
        public List<string> Queries { get; } = [];

        public IReadOnlyList<SearchResult> Search(string query)
        {
            Queries.Add(query);
            return results;
        }
    }

    private static string[] Ids(IEnumerable<SearchItem> items) => items.Select(i => i.Id).ToArray();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_returns_nothing_and_asks_no_provider(string query)
    {
        var provider = new FixedProvider(new SearchResult(Item("a", "Alpha"), 100));
        var engine = new SearchEngine([provider], _usage);

        Assert.Empty(engine.Search(query));
        Assert.Empty(provider.Queries);
    }

    [Fact]
    public void Query_is_trimmed_before_it_reaches_providers()
    {
        var provider = new FixedProvider();
        var engine = new SearchEngine([provider], _usage);

        engine.Search("  calc ");

        Assert.Equal(["calc"], provider.Queries);
    }

    [Fact]
    public void Results_from_all_providers_are_merged_best_first()
    {
        var first = new FixedProvider(new SearchResult(Item("a", "Alpha"), 50), new SearchResult(Item("b", "Bravo"), 90));
        var second = new FixedProvider(new SearchResult(Item("c", "Charlie"), 70));
        var engine = new SearchEngine([first, second], _usage);

        Assert.Equal(["b", "c", "a"], Ids(engine.Search("x")));
    }

    [Fact]
    public void Frequently_chosen_item_overtakes_a_slightly_better_match()
    {
        var provider = new FixedProvider(new SearchResult(Item("a", "Alpha"), 100), new SearchResult(Item("b", "Bravo"), 90));
        var engine = new SearchEngine([provider], _usage);
        for (int i = 0; i < 5; i++)
        {
            _usage.RecordUse("b");
        }

        Assert.Equal(["b", "a"], Ids(engine.Search("x")));
    }

    [Fact]
    public void At_equal_score_files_rank_below_other_kinds()
    {
        var provider = new FixedProvider(
            new SearchResult(Item("f", "Alpha.txt", ItemKind.File), 80),
            new SearchResult(Item("c", "Zulu", ItemKind.Command), 80),
            new SearchResult(Item("a", "Yankee", ItemKind.App), 80));
        var engine = new SearchEngine([provider], _usage);

        Assert.Equal(["a", "c", "f"], Ids(engine.Search("x")));
    }

    [Fact]
    public void Returns_at_most_fifty_results()
    {
        var results = Enumerable.Range(0, 80).Select(i => new SearchResult(Item("id" + i, "Item " + i), 100 - i)).ToArray();
        var engine = new SearchEngine([new FixedProvider(results)], _usage);

        var found = engine.Search("x");

        Assert.Equal(50, found.Count);
        Assert.Equal("id0", found[0].Id);
    }

    [Fact]
    public void TitleMatcher_keeps_only_items_whose_title_matches()
    {
        IReadOnlyList<SearchItem> items = [Item("n", "Notepad"), Item("c", "Calculator")];

        var results = TitleMatcher.Match("note", items);

        var only = Assert.Single(results);
        Assert.Equal("n", only.Item.Id);
        Assert.Equal(FuzzyMatcher.Score("note", "Notepad"), only.Score);
    }

    private sealed class BrokenProvider : ISearchProvider
    {
        public bool Broken { get; set; } = true;

        public IReadOnlyList<SearchResult> Search(string query) =>
            Broken ? throw new InvalidOperationException("boom") : [new SearchResult(Item("fixed", "Fixed"), 10)];
    }

    [Fact]
    public void A_provider_that_throws_does_not_stop_the_others()
    {
        var working = new FixedProvider(new SearchResult(Item("a", "Alpha"), 50));
        var engine = new SearchEngine([new BrokenProvider(), working], _usage);

        Assert.Equal(["a"], Ids(engine.Search("x")));
    }

    [Fact]
    public void A_failing_provider_is_reported_once_until_it_works_again()
    {
        var broken = new BrokenProvider();
        var reported = new List<(ISearchProvider Provider, Exception Error)>();
        var engine = new SearchEngine([broken], _usage, (provider, error) => reported.Add((provider, error)));

        engine.Search("x");
        engine.Search("xy");
        Assert.Single(reported);
        Assert.Same(broken, reported[0].Provider);
        Assert.IsType<InvalidOperationException>(reported[0].Error);

        broken.Broken = false;
        Assert.Equal(["fixed"], Ids(engine.Search("x")));

        broken.Broken = true;
        engine.Search("x");
        Assert.Equal(2, reported.Count);
    }
}
