using Beacon.Core.Commands;
using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

public sealed class LauncherViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly UsageStore _usage;
    private readonly List<string> _events = [];

    public LauncherViewModelTests()
    {
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
    }

    public void Dispose() => _dir.Dispose();

    private LauncherViewModel NewViewModel(params SearchItem[] extraItems)
    {
        var commands = new CommandProvider(
        [
            new BuiltInCommand("alpha", "Alpha One", "", () => _events.Add("ran alpha")),
            new BuiltInCommand("also", "Alpha Two", "", () => _events.Add("ran also")),
            new BuiltInCommand("broken", "Broken", "", () => throw new InvalidOperationException("boom")),
        ]);
        var extras = new FixedItems(extraItems);
        var viewModel = new LauncherViewModel(new SearchEngine([commands, extras], _usage), _usage);
        viewModel.Dismissed += (_, _) => _events.Add("dismissed");
        return viewModel;
    }

    private sealed class FixedItems(SearchItem[] items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);
    }

    /// <summary>An item that cannot run until something has been typed for it, like a quicklink with a placeholder.</summary>
    private SearchItem NeedsArgument(string id, string title, string prompt = "Search for") =>
        new(id, title, "", ItemKind.Quicklink, Run: null, Argument: new ItemArgument(prompt, text => _events.Add($"ran {id} with '{text}'")));

    private static void Select(LauncherViewModel viewModel, string title) =>
        viewModel.SelectedIndex = viewModel.Results.Select(r => r.Title).ToList().IndexOf(title);

    [Fact]
    public void Typing_fills_results_and_selects_the_first()
    {
        var viewModel = NewViewModel();

        viewModel.Query = "alpha";

        Assert.Equal(["Alpha One", "Alpha Two"], viewModel.Results.Select(r => r.Title));
        Assert.Equal(0, viewModel.SelectedIndex);
    }

    [Fact]
    public void Clearing_the_query_goes_back_to_what_is_listed_before_anything_is_typed()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";

        viewModel.Query = "";

        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
        Assert.Equal(viewModel.Results.Count > 0 ? 0 : -1, viewModel.SelectedIndex);
    }

    [Fact]
    public void Selection_moves_and_stops_at_both_ends()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";

        viewModel.MoveSelection(1);
        Assert.Equal(1, viewModel.SelectedIndex);

        viewModel.MoveSelection(1);
        Assert.Equal(1, viewModel.SelectedIndex);

        viewModel.MoveSelection(-5);
        Assert.Equal(0, viewModel.SelectedIndex);
    }

    [Fact]
    public void Moving_selection_with_no_results_does_nothing()
    {
        var viewModel = NewViewModel();

        viewModel.MoveSelection(1);

        Assert.Equal(-1, viewModel.SelectedIndex);
    }

    [Fact]
    public void Running_dismisses_first_then_runs_then_records_use_and_clears_the_query()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";
        viewModel.MoveSelection(1);

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "ran also"], _events);
        Assert.Equal(1.05, _usage.Boost("command:also"), precision: 3);
        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
    }

    [Fact]
    public void Running_with_nothing_selected_does_nothing()
    {
        var viewModel = NewViewModel();

        viewModel.RunSelected();

        Assert.Empty(_events);
    }

    [Fact]
    public void Secondary_action_runs_when_the_item_has_one()
    {
        var item = new SearchItem("file:a", "Zebra", "", ItemKind.File, () => _events.Add("opened"), RunSecondary: () => _events.Add("revealed"));
        var viewModel = NewViewModel(item);
        viewModel.Query = "zebra";

        viewModel.RunSelected(secondary: true);

        Assert.Equal(["dismissed", "revealed"], _events);
    }

    [Fact]
    public void Secondary_action_on_an_item_without_one_does_nothing()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";

        viewModel.RunSelected(secondary: true);

        Assert.Empty(_events);
        Assert.Equal("alpha", viewModel.Query);
    }

    [Fact]
    public void Failing_action_is_reported_and_not_counted_as_a_use()
    {
        var viewModel = NewViewModel();
        ActionFailedEventArgs? failure = null;
        viewModel.ActionFailed += (_, args) => failure = args;
        viewModel.Query = "broken";

        viewModel.RunSelected();

        Assert.NotNull(failure);
        Assert.Equal("command:broken", failure.ItemId);
        Assert.Equal("boom", failure.Exception.Message);
        Assert.Equal(1.0, _usage.Boost("command:broken"));
        Assert.Equal("", viewModel.Query);
    }

    [Fact]
    public void Selection_change_is_announced_every_time_results_are_replaced()
    {
        var viewModel = NewViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.Query = "alpha";
        viewModel.Query = "alph";

        Assert.Equal(2, changed.Count(name => name == nameof(LauncherViewModel.SelectedIndex)));
        Assert.Equal(2, changed.Count(name => name == nameof(LauncherViewModel.Query)));
    }

    [Fact]
    public void Tab_on_an_item_that_takes_an_argument_enters_argument_mode()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search", "Search zebras for"));
        viewModel.Query = "zeb";

        Assert.True(viewModel.EnterArgumentMode());

        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("Zebra Search", viewModel.ArgumentTitle);
        Assert.Equal("Search zebras for", viewModel.ArgumentPrompt);
        Assert.Equal("", viewModel.Query);
        Assert.Empty(viewModel.Results);
        Assert.Equal(-1, viewModel.SelectedIndex);
        Assert.Empty(_events);
    }

    [Fact]
    public void Tab_on_an_item_without_an_argument_does_nothing()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";

        Assert.False(viewModel.EnterArgumentMode());

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.ArgumentTitle);
        Assert.Equal("", viewModel.ArgumentPrompt);
        Assert.Equal("alpha", viewModel.Query);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.Equal(0, viewModel.SelectedIndex);
    }

    [Fact]
    public void Tab_with_nothing_selected_does_nothing()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));

        Assert.False(viewModel.EnterArgumentMode());
        Assert.False(viewModel.InArgumentMode);
    }

    [Fact]
    public void Tab_in_argument_mode_changes_nothing()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "typed";

        Assert.False(viewModel.EnterArgumentMode());

        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("typed", viewModel.Query);
    }

    [Fact]
    public void Typing_in_argument_mode_is_not_searched_for()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();

        viewModel.Query = "alpha";

        Assert.Equal("alpha", viewModel.Query);
        Assert.Empty(viewModel.Results);
        Assert.Equal(-1, viewModel.SelectedIndex);
    }

    [Fact]
    public void Enter_in_argument_mode_dismisses_runs_with_the_text_records_the_use_and_returns_to_search_mode()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = " tom & jerry ";

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "ran quicklink:zebra with ' tom & jerry '"], _events);
        Assert.Equal(1.05, _usage.Boost("quicklink:zebra"), precision: 3);
        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);

        // Back in search mode: typing searches again.
        viewModel.Query = "alpha";
        Assert.Equal(2, viewModel.Results.Count);
    }

    [Fact]
    public void An_empty_argument_is_allowed()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "ran quicklink:zebra with ''"], _events);
    }

    [Fact]
    public void Enter_on_an_item_that_cannot_run_without_its_argument_asks_for_it_instead()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";

        viewModel.RunSelected();

        Assert.Empty(_events);
        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("Zebra Search", viewModel.ArgumentTitle);
        Assert.Equal(1.0, _usage.Boost("quicklink:zebra"));
    }

    [Fact]
    public void An_item_with_both_runs_plainly_on_Enter_and_asks_for_its_argument_on_Tab()
    {
        var both = new SearchItem(
            "script:zebra",
            "Zebra Script",
            "",
            ItemKind.Script,
            () => _events.Add("ran plainly"),
            Argument: new ItemArgument("Name", text => _events.Add($"ran with '{text}'")));
        var viewModel = NewViewModel(both);

        viewModel.Query = "zeb";
        viewModel.RunSelected();
        Assert.Equal(["dismissed", "ran plainly"], _events);

        _events.Clear();
        viewModel.Query = "zeb";
        Assert.True(viewModel.EnterArgumentMode());
        viewModel.Query = "x";
        viewModel.RunSelected();
        Assert.Equal(["dismissed", "ran with 'x'"], _events);
    }

    [Fact]
    public void Esc_leaves_argument_mode_and_puts_the_search_text_and_the_selection_back()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:alpha", "Alpha Three"));
        viewModel.Query = "alpha";
        Select(viewModel, "Alpha Three");
        Assert.NotEqual(0, viewModel.SelectedIndex);
        viewModel.EnterArgumentMode();
        viewModel.Query = "half typed";

        Assert.True(viewModel.LeaveArgumentMode());

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.ArgumentTitle);
        Assert.Equal("alpha", viewModel.Query);
        Assert.Equal(3, viewModel.Results.Count);
        Assert.Equal("Alpha Three", viewModel.Results[viewModel.SelectedIndex].Title);
        Assert.Empty(_events);
    }

    [Fact]
    public void Esc_in_search_mode_is_left_to_the_window()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";

        Assert.False(viewModel.LeaveArgumentMode());

        Assert.Equal("alpha", viewModel.Query);
        Assert.Equal(2, viewModel.Results.Count);
    }

    [Fact]
    public void Backspace_on_an_empty_argument_leaves_argument_mode()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();

        Assert.True(viewModel.Backspace());

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("zeb", viewModel.Query);
        Assert.Single(viewModel.Results);
    }

    [Fact]
    public void Backspace_with_argument_text_left_is_an_ordinary_key()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "x";

        Assert.False(viewModel.Backspace());

        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("x", viewModel.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("alpha")]
    public void Backspace_in_search_mode_is_an_ordinary_key(string query)
    {
        var viewModel = NewViewModel();
        viewModel.Query = query;

        Assert.False(viewModel.Backspace());
        Assert.Equal(query, viewModel.Query);
    }

    [Fact]
    public void Hiding_the_window_returns_to_search_mode_with_an_empty_input()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "half typed";

        viewModel.Reset();

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.ArgumentTitle);
        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
        Assert.Empty(_events);

        // The search text from before is gone too: Esc has nothing to go back to.
        Assert.False(viewModel.LeaveArgumentMode());
        viewModel.Query = "alpha";
        Assert.Equal(2, viewModel.Results.Count);
    }

    [Fact]
    public void Hiding_the_window_in_search_mode_clears_the_input()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "alpha";

        viewModel.Reset();

        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
    }

    [Fact]
    public void Ctrl_Enter_in_argument_mode_does_nothing()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "x";

        viewModel.RunSelected(secondary: true);

        Assert.Empty(_events);
        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("x", viewModel.Query);
    }

    [Fact]
    public void A_failing_argument_action_is_reported_and_not_counted_as_a_use()
    {
        var failing = new SearchItem(
            "quicklink:zebra",
            "Zebra Search",
            "",
            ItemKind.Quicklink,
            Run: null,
            Argument: new ItemArgument("Search for", _ => throw new InvalidOperationException("boom")));
        var viewModel = NewViewModel(failing);
        ActionFailedEventArgs? failure = null;
        viewModel.ActionFailed += (_, args) => failure = args;
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "x";

        viewModel.RunSelected();

        Assert.NotNull(failure);
        Assert.Equal("quicklink:zebra", failure.ItemId);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.Equal(1.0, _usage.Boost("quicklink:zebra"));
        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.Query);
    }

    [Fact]
    public void The_text_typed_is_what_runs_even_when_the_window_resets_as_it_hides()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        // A window that has just been hidden tells the view model so, before the action has run.
        viewModel.Dismissed += (_, _) => viewModel.Reset();
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "kept";

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "ran quicklink:zebra with 'kept'"], _events);
        Assert.Equal(1.05, _usage.Boost("quicklink:zebra"), precision: 3);
    }

    [Fact]
    public void A_dismissed_handler_that_throws_still_leaves_search_mode_with_an_empty_input()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Dismissed += (_, _) => throw new InvalidOperationException("the window broke");
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "typed";

        // Whether the exception reaches the caller is not what matters here.
        _ = Record.Exception(() => viewModel.RunSelected());

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.ArgumentTitle);
        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
    }

    [Fact]
    public void A_dismissed_handler_that_throws_in_search_mode_still_empties_the_input()
    {
        var viewModel = NewViewModel();
        viewModel.Dismissed += (_, _) => throw new InvalidOperationException("the window broke");
        viewModel.Query = "alpha";

        _ = Record.Exception(() => viewModel.RunSelected());

        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
    }

    [Fact]
    public void An_action_failed_handler_that_throws_still_leaves_search_mode_with_an_empty_input()
    {
        var failing = new SearchItem(
            "quicklink:zebra",
            "Zebra Search",
            "",
            ItemKind.Quicklink,
            Run: null,
            Argument: new ItemArgument("Search for", _ => throw new InvalidOperationException("boom")));
        var viewModel = NewViewModel(failing);
        viewModel.ActionFailed += (_, _) => throw new InvalidOperationException("the notice broke");
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        viewModel.Query = "typed";

        _ = Record.Exception(() => viewModel.RunSelected());

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.Query);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
    }

    [Fact]
    public void Hiding_the_window_from_argument_mode_announces_the_mode_again()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        viewModel.EnterArgumentMode();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.Reset();

        Assert.Contains(nameof(LauncherViewModel.InArgumentMode), changed);
        Assert.Contains(nameof(LauncherViewModel.ArgumentTitle), changed);
        Assert.Contains(nameof(LauncherViewModel.ArgumentPrompt), changed);
    }

    [Fact]
    public void In_search_mode_the_selected_action_still_runs_when_the_window_resets_as_it_hides()
    {
        var viewModel = NewViewModel();
        viewModel.Dismissed += (_, _) => viewModel.Reset();
        viewModel.Query = "alpha";
        viewModel.MoveSelection(1);

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "ran also"], _events);
        Assert.Equal(1.05, _usage.Boost("command:also"), precision: 3);
    }

    [Fact]
    public void Entering_and_leaving_argument_mode_is_announced()
    {
        var viewModel = NewViewModel(NeedsArgument("quicklink:zebra", "Zebra Search"));
        viewModel.Query = "zeb";
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.EnterArgumentMode();

        Assert.Contains(nameof(LauncherViewModel.InArgumentMode), changed);
        Assert.Contains(nameof(LauncherViewModel.ArgumentTitle), changed);
        Assert.Contains(nameof(LauncherViewModel.ArgumentPrompt), changed);
        Assert.Contains(nameof(LauncherViewModel.Query), changed);
        Assert.Contains(nameof(LauncherViewModel.SelectedIndex), changed);

        changed.Clear();
        viewModel.LeaveArgumentMode();

        Assert.Contains(nameof(LauncherViewModel.InArgumentMode), changed);
        Assert.Contains(nameof(LauncherViewModel.ArgumentTitle), changed);
        Assert.Contains(nameof(LauncherViewModel.ArgumentPrompt), changed);
        Assert.Contains(nameof(LauncherViewModel.Query), changed);
    }
}
