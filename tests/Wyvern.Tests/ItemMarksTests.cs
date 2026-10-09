using Wyvern.Core.Files;
using Wyvern.Core.Launcher;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>Favorites and aliases: how they are kept, how an alias finds its item, and what the launcher lists and does with them.</summary>
public sealed class ItemMarksTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _file;

    public ItemMarksTests() => _file = _dir.File("favorites.json");

    public void Dispose() => _dir.Dispose();

    private sealed class Listed(params SearchItem[] items) : ISearchProvider, IBrowsableProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);

        public IReadOnlyList<SearchItem> Browse() => items;
    }

    private sealed class Hidden(params SearchItem[] items) : ISearchProvider, IItemLookup
    {
        public bool Broken { get; set; }

        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);

        public SearchItem? Find(string itemId) => Broken ? throw new InvalidOperationException("no") : items.FirstOrDefault(item => item.Id == itemId);
    }

    private static SearchItem Item(string id, string title, ItemKind kind = ItemKind.App) => new(id, title, "", kind, () => { });

    private static readonly SearchItem Notepad = Item("app:notepad", "Notepad");
    private static readonly SearchItem Paint = Item("app:paint", "Paint");
    private static readonly SearchItem History = Item("command:clipboard-history", "Clipboard History", ItemKind.Command);
    private static readonly SearchItem Reload = Item("command:reload", "Reload", ItemKind.Command);
    private static readonly SearchItem Signature = Item("snippet:sig.md", "Signature", ItemKind.Snippet);
    private static readonly SearchItem Budget = Item(@"file:C:\made-up\Budget.xlsx", "Budget.xlsx", ItemKind.File);

    [Fact]
    public void Favorites_are_kept_in_the_order_they_were_made_and_read_again_from_the_file()
    {
        var marks = new ItemMarks(_file);

        Assert.True(marks.ToggleFavorite("app:paint"));
        Assert.True(marks.ToggleFavorite("app:notepad"));
        Assert.True(marks.ToggleFavorite("snippet:sig.md"));
        Assert.False(marks.ToggleFavorite("app:paint"));

        Assert.Equal(["app:notepad", "snippet:sig.md"], marks.Favorites);
        Assert.True(marks.IsFavorite("app:notepad"));
        Assert.False(marks.IsFavorite("app:paint"));
        Assert.False(marks.IsFavorite("APP:NOTEPAD"));
        Assert.Equal(["app:notepad", "snippet:sig.md"], new ItemMarks(_file).Favorites);
    }

    [Fact]
    public void A_favorite_moves_up_and_down_its_list_as_far_as_there_is_room()
    {
        var marks = new ItemMarks(_file);
        foreach (string id in new[] { "a", "b", "c" })
        {
            marks.ToggleFavorite(id);
        }

        Assert.True(marks.MoveFavorite("c", -1));
        Assert.Equal(["a", "c", "b"], marks.Favorites);
        Assert.True(marks.MoveFavorite("a", 1));
        Assert.Equal(["c", "a", "b"], marks.Favorites);
        Assert.True(marks.MoveFavorite("b", -5));
        Assert.Equal(["b", "c", "a"], marks.Favorites);

        Assert.False(marks.MoveFavorite("b", -1));
        Assert.False(marks.MoveFavorite("a", 1));
        Assert.False(marks.MoveFavorite("heron", 1));
        Assert.Equal(["b", "c", "a"], new ItemMarks(_file).Favorites);
    }

    [Fact]
    public void There_is_room_for_fifty_favorites_and_no_more()
    {
        var marks = new ItemMarks(_file);
        for (int i = 0; i < ItemMarks.MaxFavorites; i++)
        {
            Assert.True(marks.ToggleFavorite("item:" + i));
        }

        Assert.Null(marks.ToggleFavorite("one more"));
        Assert.Equal(50, marks.Favorites.Count);
        Assert.False(marks.ToggleFavorite("item:0"));
        Assert.True(marks.ToggleFavorite("one more"));
    }

    [Fact]
    public void An_alias_is_one_word_that_belongs_to_one_item_and_is_found_whatever_its_case()
    {
        var marks = new ItemMarks(_file);

        Assert.Equal(AliasChange.Set, marks.SetAlias("app:notepad", "  np "));
        Assert.Equal(AliasChange.Set, marks.SetAlias("snippet:sig.md", "Sg"));

        Assert.Equal("np", marks.AliasOf("app:notepad"));
        Assert.Null(marks.AliasOf("app:paint"));
        Assert.Equal("app:notepad", marks.IdOf("NP"));
        Assert.Equal("snippet:sig.md", marks.IdOf(" sg "));
        Assert.Null(marks.IdOf("n"));
        Assert.Null(marks.IdOf(""));
        Assert.Null(marks.IdOf(new string('n', 500)));

        Assert.Equal(AliasChange.Taken, marks.SetAlias("app:paint", "NP"));
        Assert.Equal(AliasChange.NotOneWord, marks.SetAlias("app:paint", "two words"));
        Assert.Equal(AliasChange.NotOneWord, marks.SetAlias("app:paint", "two\twords"));
        Assert.Equal(AliasChange.TooLong, marks.SetAlias("app:paint", new string('p', 21)));
        Assert.Equal(AliasChange.Set, marks.SetAlias("app:paint", new string('p', 20)));
        Assert.Null(marks.AliasOf("app:heron"));
    }

    [Fact]
    public void An_item_can_change_its_alias_keep_it_and_lose_it()
    {
        var marks = new ItemMarks(_file);
        marks.SetAlias("app:notepad", "np");

        Assert.Equal(AliasChange.Set, marks.SetAlias("app:notepad", "NP"));
        Assert.Equal(AliasChange.Set, marks.SetAlias("app:notepad", "pad"));
        Assert.Null(marks.IdOf("np"));
        Assert.Equal("app:notepad", marks.IdOf("pad"));

        Assert.Equal(AliasChange.Removed, marks.SetAlias("app:notepad", "   "));
        Assert.Equal(AliasChange.None, marks.SetAlias("app:notepad", ""));
        Assert.Null(marks.AliasOf("app:notepad"));
        Assert.Null(new ItemMarks(_file).IdOf("pad"));
    }

    [Fact]
    public void Aliases_are_read_again_from_the_file()
    {
        var marks = new ItemMarks(_file);
        marks.SetAlias("app:notepad", "np");
        marks.ToggleFavorite("app:paint");

        var again = new ItemMarks(_file);

        Assert.Equal("np", again.AliasOf("app:notepad"));
        Assert.Equal("app:notepad", again.IdOf("np"));
        Assert.Equal(["app:paint"], again.Favorites);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    [InlineData("null")]
    public void A_file_that_cannot_be_read_starts_without_marks_and_is_written_anew_by_the_next_one(string content)
    {
        File.WriteAllText(_file, content);

        var marks = new ItemMarks(_file);

        Assert.Empty(marks.Favorites);
        Assert.True(marks.ToggleFavorite("app:notepad"));
        Assert.Equal(["app:notepad"], new ItemMarks(_file).Favorites);
    }

    [Fact]
    public void A_file_edited_by_hand_gives_what_can_be_used_of_it()
    {
        File.WriteAllText(_file, """
            {
              "favorites": ["app:notepad", "", null, "app:notepad", "app:paint"],
              "aliases": { "app:notepad": "np", "app:paint": "NP", "snippet:sig.md": "two words", "command:reload": null, "": "x", "app:calc": " c " }
            }
            """);

        var marks = new ItemMarks(_file);

        Assert.Equal(["app:notepad", "app:paint"], marks.Favorites);
        Assert.Equal("np", marks.AliasOf("app:notepad"));
        Assert.Null(marks.AliasOf("app:paint"));
        Assert.Null(marks.AliasOf("snippet:sig.md"));
        Assert.Null(marks.AliasOf("command:reload"));
        Assert.Equal("c", marks.AliasOf("app:calc"));
    }

    [Fact]
    public void A_file_that_cannot_be_written_does_not_stop_a_mark_from_being_made()
    {
        // A folder stands where the file would be.
        Directory.CreateDirectory(_file);
        var marks = new ItemMarks(_file);

        Assert.True(marks.ToggleFavorite("app:notepad"));
        Assert.Equal(AliasChange.Set, marks.SetAlias("app:notepad", "np"));
        Assert.Equal(["app:notepad"], marks.Favorites);
    }

    private SearchEngine Engine(ItemMarks marks, out UsageStore usage, params ISearchProvider[] providers)
    {
        usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        return new SearchEngine(providers, usage, marks: marks);
    }

    [Fact]
    public void The_item_whose_alias_is_typed_stands_first_even_when_what_was_typed_is_nothing_like_its_name()
    {
        var marks = new ItemMarks(_file);
        marks.SetAlias(Signature.Id, "pa");
        var engine = Engine(marks, out _, new Listed(Notepad, Paint, Signature));

        // "pa" finds Paint and Notepad by their names; the snippet is fetched by its alias.
        Assert.Equal(["Signature", "Paint", "Notepad"], engine.Search("pa").Select(item => item.Title));
        Assert.Equal(["Signature", "Paint", "Notepad"], engine.Search(" PA ").Select(item => item.Title));

        // Only the whole alias counts.
        Assert.Equal(["Paint"], engine.Search("pai").Select(item => item.Title));
        Assert.DoesNotContain("Signature", engine.Search("p").Select(item => item.Title));
    }

    [Fact]
    public void An_item_found_by_its_name_and_by_its_alias_is_listed_once_and_first_whatever_was_used_more()
    {
        var marks = new ItemMarks(_file);
        marks.SetAlias(Paint.Id, "no");
        var engine = Engine(marks, out var usage, new Listed(Notepad, Paint), new Listed(Paint));
        for (int i = 0; i < 20; i++)
        {
            usage.RecordUse(Notepad.Id);
        }

        Assert.Equal(["Paint", "Notepad"], engine.Search("no").Select(item => item.Title));
    }

    [Fact]
    public void An_alias_finds_a_file_which_is_not_listed_until_it_is_looked_for()
    {
        var marks = new ItemMarks(_file);
        marks.SetAlias(Budget.Id, "zz");
        var files = new Hidden(Budget);
        var failures = new List<string>();
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var engine = new SearchEngine([new Listed(Notepad), files], usage, (provider, _) => failures.Add(provider.GetType().Name), marks);

        // Nothing of "zz" is in the name of the file: it is fetched by its id.
        Assert.Equal(["Budget.xlsx"], engine.Search("zz").Select(item => item.Title));

        // An alias whose item is nowhere finds nothing, and a provider that throws is left out and told of once.
        marks.SetAlias("app:gone", "gn");
        Assert.Empty(engine.Search("gn"));
        files.Broken = true;
        Assert.Empty(engine.Search("zz"));
        Assert.Empty(engine.Search("zz"));
        Assert.Equal(["Hidden"], failures);
    }

    [Fact]
    public void Without_marks_the_engine_has_no_favorites_and_no_aliases()
    {
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var engine = new SearchEngine([new Listed(Notepad)], usage);

        Assert.Null(engine.Marks);
        Assert.Empty(engine.Favorites());
        Assert.Equal(["Notepad"], engine.Search("note").Select(item => item.Title));
    }

    [Fact]
    public void The_favorites_that_are_there_are_given_in_the_owner_s_order_and_one_that_is_gone_stays_a_favorite()
    {
        var marks = new ItemMarks(_file);
        foreach (string id in new[] { Signature.Id, "app:uninstalled", Budget.Id, Notepad.Id })
        {
            marks.ToggleFavorite(id);
        }

        var engine = Engine(marks, out _, new Listed(Notepad, Paint, Signature), new Hidden(Budget));

        Assert.Equal(["Signature", "Budget.xlsx", "Notepad"], engine.Favorites().Select(item => item.Title));
        Assert.Contains("app:uninstalled", marks.Favorites);
    }

    private LauncherViewModel Launcher(ItemMarks? marks, List<string> said, params ISearchProvider[] providers)
    {
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var launcher = new LauncherViewModel(new SearchEngine(providers, usage, marks: marks), usage) { Say = said.Add };
        launcher.Refresh();
        return launcher;
    }

    private static string[] Rows(LauncherViewModel launcher) =>
        launcher.Results.Select(item => launcher.SectionOf(item) + ": " + item.Title).ToArray();

    [Fact]
    public void Favorites_stand_first_under_their_own_heading_and_nowhere_else()
    {
        var marks = new ItemMarks(_file);
        marks.ToggleFavorite(Paint.Id);
        marks.ToggleFavorite(History.Id);
        marks.ToggleFavorite(Budget.Id);
        var launcher = Launcher(marks, [], new Listed(Notepad, Paint, History, Reload, Signature), new Hidden(Budget));

        Assert.Equal(
            [
                "Favorites: Paint", "Favorites: Clipboard History", "Favorites: Budget.xlsx",
                "Commands: Reload", "Snippets: Signature", "Applications: Notepad",
            ],
            Rows(launcher));
    }

    [Fact]
    public void A_favorite_takes_no_place_among_the_suggestions()
    {
        var marks = new ItemMarks(_file);
        marks.ToggleFavorite(Notepad.Id);
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        usage.RecordUse(Notepad.Id);
        usage.RecordUse(Paint.Id);
        var launcher = new LauncherViewModel(new SearchEngine([new Listed(Notepad, Paint, History, Reload)], usage, marks: marks), usage);
        launcher.Refresh();

        Assert.Equal(["Favorites: Notepad", "Suggestions: Clipboard History", "Suggestions: Paint", "Commands: Reload"], Rows(launcher));
    }

    [Fact]
    public void Making_the_selected_item_a_favorite_keeps_the_window_open_and_the_item_selected()
    {
        var marks = new ItemMarks(_file);
        var said = new List<string>();
        var launcher = Launcher(marks, said, new Listed(Notepad, Paint, Reload));
        bool dismissed = false;
        launcher.Dismissed += (_, _) => dismissed = true;
        launcher.MoveSelection(2);
        Assert.Equal("Paint", launcher.Results[launcher.SelectedIndex].Title);
        Assert.True(launcher.CanMarkSelected);
        Assert.False(launcher.SelectedIsFavorite);

        launcher.ToggleFavoriteSelected();

        Assert.False(dismissed);
        Assert.Equal(["Added to Favorites"], said);
        Assert.Equal(["Favorites: Paint", "Commands: Reload", "Applications: Notepad"], Rows(launcher));
        Assert.Equal(0, launcher.SelectedIndex);
        Assert.True(launcher.SelectedIsFavorite);

        launcher.ToggleFavoriteSelected();

        Assert.Equal(["Added to Favorites", "Removed from Favorites"], said);
        Assert.Equal(["Commands: Reload", "Applications: Notepad", "Applications: Paint"], Rows(launcher));
        Assert.Equal("Paint", launcher.Results[launcher.SelectedIndex].Title);
        Assert.Empty(marks.Favorites);
    }

    [Fact]
    public void A_favorite_is_made_from_what_a_search_found_as_well_and_the_search_stays_as_it_was()
    {
        var marks = new ItemMarks(_file);
        var launcher = Launcher(marks, [], new Listed(Notepad, Paint), new Hidden(Budget));
        launcher.Query = "budget";

        launcher.ToggleFavoriteSelected();

        Assert.Equal([Budget.Id], marks.Favorites);
        Assert.Equal("budget", launcher.Query);
        Assert.Equal(["Results: Budget.xlsx"], Rows(launcher));
        launcher.Query = "";
        Assert.Equal("Favorites: Budget.xlsx", Rows(launcher)[0]);
    }

    [Fact]
    public void The_selected_favorite_moves_up_and_down_and_stays_selected()
    {
        var marks = new ItemMarks(_file);
        marks.ToggleFavorite(Notepad.Id);
        marks.ToggleFavorite(Paint.Id);
        marks.ToggleFavorite(Reload.Id);
        var launcher = Launcher(marks, [], new Listed(Notepad, Paint, Reload));
        launcher.MoveSelection(2);

        launcher.MoveSelectedFavorite(-1);

        Assert.Equal(["Favorites: Notepad", "Favorites: Reload", "Favorites: Paint"], Rows(launcher));
        Assert.Equal(1, launcher.SelectedIndex);

        launcher.MoveSelectedFavorite(-1);
        launcher.MoveSelectedFavorite(-1);
        Assert.Equal(["Favorites: Reload", "Favorites: Notepad", "Favorites: Paint"], Rows(launcher));
        Assert.Equal(0, launcher.SelectedIndex);
    }

    [Fact]
    public void An_alias_is_asked_for_in_the_input_line_and_enter_goes_back_to_the_item_in_its_list()
    {
        var marks = new ItemMarks(_file);
        var said = new List<string>();
        var launcher = Launcher(marks, said, new Listed(Notepad, Paint));
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        bool dismissed = false;
        launcher.Dismissed += (_, _) => dismissed = true;
        launcher.Query = "pai";

        launcher.AskAliasForSelected();

        Assert.True(launcher.InArgumentMode);
        Assert.Equal(LauncherViewModel.SetAlias, launcher.ArgumentTitle);
        Assert.Equal(LauncherViewModel.AliasPrompt, launcher.ArgumentPrompt);
        Assert.Equal("", launcher.Query);

        launcher.Query = "pt";
        launcher.RunSelected();

        Assert.False(dismissed);
        Assert.False(launcher.InArgumentMode);
        Assert.Equal(["Alias Set"], said);
        Assert.Equal("pt", marks.AliasOf(Paint.Id));
        Assert.Equal("pt", launcher.AliasOf(Paint));
        Assert.Equal("", launcher.AliasOf(Notepad));
        Assert.Equal("pai", launcher.Query);
        Assert.Equal("Paint", launcher.Results[launcher.SelectedIndex].Title);

        // Giving an alias is not a use of the item.
        Assert.Equal(1.0, new UsageStore(_dir.File("usage.json"), new FakeClock()).Boost(Paint.Id));
        Assert.Equal(1.0, usage.Boost(Paint.Id));

        launcher.Query = "pt";
        Assert.Equal("Paint", launcher.Results[0].Title);
    }

    [Theory]
    [InlineData("", "Alias Removed")]
    [InlineData("np", "That Alias Is Taken")]
    [InlineData("two words", "An Alias Is One Word")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaa", "An Alias Is 20 Characters at Most")]
    public void What_was_typed_for_an_alias_is_answered_in_fixed_words(string typed, string words)
    {
        var marks = new ItemMarks(_file);
        marks.SetAlias(Notepad.Id, "np");
        marks.SetAlias(Paint.Id, "pt");
        var said = new List<string>();
        var launcher = Launcher(marks, said, new Listed(Notepad, Paint));
        launcher.Query = "paint";

        launcher.AskAliasForSelected();
        launcher.Query = typed;
        launcher.RunSelected();

        Assert.Equal([words], said);
        Assert.Equal(typed.Length == 0 ? null : "pt", marks.AliasOf(Paint.Id));
        Assert.Equal("Paint", launcher.Results[launcher.SelectedIndex].Title);
    }

    [Fact]
    public void Esc_at_the_alias_gives_it_up()
    {
        var marks = new ItemMarks(_file);
        var said = new List<string>();
        var launcher = Launcher(marks, said, new Listed(Notepad, Paint));
        launcher.MoveSelection(1);
        launcher.AskAliasForSelected();
        launcher.Query = "pt";

        Assert.True(launcher.LeaveArgumentMode());

        Assert.Empty(said);
        Assert.Null(marks.AliasOf(Paint.Id));
        Assert.Equal("Paint", launcher.Results[launcher.SelectedIndex].Title);
    }

    [Fact]
    public void What_is_not_there_the_next_time_cannot_be_marked_and_nothing_can_without_marks()
    {
        var marks = new ItemMarks(_file);
        var sum = Item("calc", "12", ItemKind.Calculation);
        var window = Item("window:20", "Inbox", ItemKind.Window);
        var launcher = Launcher(marks, [], new Listed(sum, window, Notepad));
        launcher.Query = "12";
        Assert.False(launcher.CanMarkSelected);
        launcher.ToggleFavoriteSelected();
        launcher.AskAliasForSelected();
        Assert.False(launcher.InArgumentMode);
        Assert.Empty(marks.Favorites);

        launcher.Query = "inbox";
        Assert.False(launcher.CanMarkSelected);

        var plain = Launcher(null, [], new Listed(Notepad));
        Assert.False(plain.CanMarkSelected);
        Assert.False(plain.SelectedIsFavorite);
        plain.ToggleFavoriteSelected();
        plain.AskAliasForSelected();
        plain.MoveSelectedFavorite(1);
        Assert.False(plain.InArgumentMode);
        Assert.Equal("", plain.AliasOf(Notepad));
    }

    [Fact]
    public void The_file_provider_gives_a_file_by_its_id_when_the_index_holds_it()
    {
        var index = new FileIndex();
        index.Add(new FileEntry(@"C:\made-up\docs", "Budget 2026.xlsx", IsDirectory: false));
        var files = new FileProvider(index, new FakeProcessLauncher(), () => []);

        var found = files.Find(@"file:C:\made-up\docs\Budget 2026.xlsx");

        Assert.Equal("Budget 2026.xlsx", found!.Title);
        Assert.Equal(ItemKind.File, found.Kind);
        Assert.Null(files.Find(@"file:C:\made-up\docs\gone.xlsx"));
        Assert.Null(files.Find("app:notepad"));
    }
}
