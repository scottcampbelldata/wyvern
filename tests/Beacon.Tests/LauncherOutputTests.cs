using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>
/// The launcher with items that keep it open and show their result in the output view, as a
/// script in output mode does. The items here are stand-ins: each opens the view the way the
/// script provider does and remembers its ticket, so that a test can say when the run ends.
/// </summary>
public sealed class LauncherOutputTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly UsageStore _usage;
    private readonly OutputViewModel _output = new();
    private readonly List<string> _events = [];
    private readonly Dictionary<string, object> _tickets = [];
    private readonly LauncherViewModel _viewModel;

    public LauncherOutputTests()
    {
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        SearchItem[] items =
        [
            new("script:report.ps1", "Report", "", ItemKind.Script, Run: () => Show("Report", "report"),
                RunSecondary: () => _events.Add("edited report"), KeepOpen: true),
            new("script:restart.ps1", "Restart", "", ItemKind.Script, Run: null,
                RunSecondary: () => _events.Add("edited restart"),
                Argument: new ItemArgument("Container name", text => Show("Restart", $"restart with '{text}'")), KeepOpen: true),
            new("script:broken.ps1", "Broken report", "", ItemKind.Script, Run: () => throw new InvalidOperationException("boom"), KeepOpen: true),
            new("command:plain", "Plain report", "", ItemKind.Command, Run: () => _events.Add("ran plain")),
        ];
        _viewModel = new LauncherViewModel(new SearchEngine([new FixedItems(items)], _usage), _usage, _output);
        _viewModel.Dismissed += (_, _) => _events.Add("dismissed");
        _viewModel.ActionFailed += (_, failure) => _events.Add("failed " + failure.ItemId);
    }

    public void Dispose() => _dir.Dispose();

    private sealed class FixedItems(SearchItem[] items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);
    }

    private void Show(string title, string what)
    {
        _events.Add("ran " + what);
        _tickets[title] = _output.Open(title);
    }

    private void Select(string title) =>
        _viewModel.SelectedIndex = _viewModel.Results.Select(r => r.Title).ToList().IndexOf(title);

    private void RunReport()
    {
        _viewModel.Query = "report";
        Select("Report");
        _viewModel.RunSelected();
    }

    [Fact]
    public void The_view_model_makes_its_own_output_view_when_it_is_given_none()
    {
        var viewModel = new LauncherViewModel(new SearchEngine([], _usage), _usage);

        Assert.NotNull(viewModel.Output);
        Assert.False(viewModel.Output.IsOpen);
        Assert.Same(_output, _viewModel.Output);
    }

    [Fact]
    public void An_item_that_keeps_the_launcher_open_runs_without_dismissing_it_and_shows_the_output_view()
    {
        RunReport();

        Assert.Equal(["ran report"], _events);
        Assert.True(_output.IsOpen);
        Assert.Equal("Report", _output.Title);
        Assert.Equal("Running", _output.Status);
        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);
        Assert.Equal(-1, _viewModel.SelectedIndex);
        Assert.False(_viewModel.InArgumentMode);
        Assert.Equal(1.05, _usage.Boost("script:report.ps1"), precision: 3);
    }

    [Fact]
    public void An_ordinary_item_still_dismisses_the_launcher()
    {
        _viewModel.Query = "plain";

        _viewModel.RunSelected();

        Assert.Equal(["dismissed", "ran plain"], _events);
        Assert.False(_output.IsOpen);
    }

    [Fact]
    public void The_secondary_action_of_an_item_that_keeps_the_launcher_open_dismisses_it_as_usual()
    {
        _viewModel.Query = "report";
        Select("Report");

        _viewModel.RunSelected(secondary: true);

        Assert.Equal(["dismissed", "edited report"], _events);
        Assert.False(_output.IsOpen);
    }

    [Fact]
    public void An_item_that_takes_an_argument_and_keeps_the_launcher_open_runs_from_argument_mode_without_dismissing_it()
    {
        _viewModel.Query = "restart";
        _viewModel.RunSelected();
        Assert.True(_viewModel.InArgumentMode);
        _viewModel.Query = "web 1";

        _viewModel.RunSelected();

        Assert.Equal(["ran restart with 'web 1'"], _events);
        Assert.True(_output.IsOpen);
        Assert.Equal("Restart", _output.Title);
        Assert.False(_viewModel.InArgumentMode);
        Assert.Equal("", _viewModel.Query);
        Assert.Equal("", _viewModel.ArgumentTitle);
        Assert.Empty(_viewModel.Results);
        Assert.Equal(1.05, _usage.Boost("script:restart.ps1"), precision: 3);
    }

    [Fact]
    public void Tab_asks_for_the_argument_of_such_an_item_too()
    {
        _viewModel.Query = "restart";

        Assert.True(_viewModel.EnterArgumentMode());

        Assert.Equal("Container name", _viewModel.ArgumentPrompt);
        Assert.Empty(_events);
    }

    [Fact]
    public void Esc_closes_the_output_view_and_goes_back_to_an_empty_search_with_the_window_still_open()
    {
        RunReport();

        Assert.True(_viewModel.CloseOutput());

        Assert.False(_output.IsOpen);
        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);
        Assert.Equal(["ran report"], _events);

        // The launcher searches again.
        _viewModel.Query = "plain";
        Assert.Equal(["Plain report"], _viewModel.Results.Select(r => r.Title));
    }

    [Fact]
    public void Esc_with_no_output_view_open_is_left_to_the_other_modes()
    {
        _viewModel.Query = "report";

        Assert.False(_viewModel.CloseOutput());

        Assert.Equal(3, _viewModel.Results.Count);
    }

    [Fact]
    public void Closing_the_view_does_not_stop_the_run_and_its_end_is_then_not_shown_in_the_view()
    {
        RunReport();
        _viewModel.CloseOutput();

        bool shown = _output.Complete(_tickets["Report"], "Finished", "the report");

        Assert.False(shown);
        Assert.False(_output.IsOpen);
    }

    [Fact]
    public void The_run_on_show_ends_in_the_view()
    {
        RunReport();

        bool shown = _output.Complete(_tickets["Report"], "Finished", "the report");

        Assert.True(shown);
        Assert.Equal("Finished", _output.Status);
        Assert.Equal("the report", _output.Text);
    }

    [Fact]
    public void Hiding_the_window_closes_the_output_view()
    {
        RunReport();

        _viewModel.Reset();

        Assert.False(_output.IsOpen);
        Assert.Equal("", _output.Text);
        Assert.Equal("", _viewModel.Query);
        Assert.False(_output.Complete(_tickets["Report"], "Finished", "the report"));
    }

    [Fact]
    public void Nothing_can_be_run_while_the_output_view_is_open()
    {
        RunReport();
        _events.Clear();

        // Text reaching the input line now is kept but not searched for.
        _viewModel.Query = "plain";
        Assert.Empty(_viewModel.Results);

        _viewModel.RunSelected();
        _viewModel.RunSelected(secondary: true);
        Assert.False(_viewModel.EnterArgumentMode());
        _viewModel.MoveSelection(1);

        Assert.Empty(_events);
        Assert.True(_output.IsOpen);
        Assert.False(_viewModel.InArgumentMode);
        Assert.Equal(-1, _viewModel.SelectedIndex);
    }

    // Nothing in Beacon opens the view the way the next two tests do: an item that shows its
    // output is run by the launcher, which empties the results as it does so. The view model
    // does not rely on that.

    [Fact]
    public void An_item_that_was_selected_before_the_view_opened_cannot_be_run_or_asked_for_its_argument_while_it_is_open()
    {
        _viewModel.Query = "plain";
        Assert.Equal("Plain report", _viewModel.Results[_viewModel.SelectedIndex].Title);
        _output.Open("Opened from elsewhere");

        _viewModel.RunSelected();
        _viewModel.RunSelected(secondary: true);

        Assert.Empty(_events);

        _viewModel.CloseOutput();
        _viewModel.Query = "restart";
        _output.Open("Opened from elsewhere");

        Assert.False(_viewModel.EnterArgumentMode());
        Assert.False(_viewModel.InArgumentMode);
    }

    [Fact]
    public void An_argument_being_typed_when_the_view_opened_cannot_be_run_while_it_is_open()
    {
        _viewModel.Query = "restart";
        _viewModel.EnterArgumentMode();
        _viewModel.Query = "web 1";
        _output.Open("Opened from elsewhere");

        _viewModel.RunSelected();

        Assert.Empty(_events);
        Assert.True(_viewModel.InArgumentMode);
    }

    [Fact]
    public void Text_that_reached_the_input_line_while_the_view_was_open_is_searched_for_once_it_is_closed()
    {
        RunReport();
        _viewModel.Query = "plain";

        _viewModel.CloseOutput();

        Assert.Equal(["Plain report"], _viewModel.Results.Select(r => r.Title));
        Assert.Equal(0, _viewModel.SelectedIndex);
    }

    [Fact]
    public void An_item_that_keeps_the_launcher_open_and_throws_is_reported_and_leaves_an_empty_search_with_no_output_view()
    {
        _viewModel.Query = "broken";

        _viewModel.RunSelected();

        Assert.Equal(["failed script:broken.ps1"], _events);
        Assert.False(_output.IsOpen);
        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);
        Assert.Equal(1.0, _usage.Boost("script:broken.ps1"), precision: 3);
    }

    // An action that opens the view, because it started its run, and then throws: nothing will
    // ever complete that view, so it would say "Running" for ever.
    private LauncherViewModel ViewModelWithAnItemThatOpensTheViewAndThrows()
    {
        var item = new SearchItem(
            "script:half.ps1",
            "Half done",
            "",
            ItemKind.Script,
            Run: () =>
            {
                _output.Open("Half done");
                throw new InvalidOperationException("boom");
            },
            KeepOpen: true);

        return new LauncherViewModel(new SearchEngine([new FixedItems([item])], _usage), _usage, _output);
    }

    [Fact]
    public void An_item_that_keeps_the_launcher_open_opens_the_view_and_throws_leaves_no_view_saying_running()
    {
        var viewModel = ViewModelWithAnItemThatOpensTheViewAndThrows();
        bool? openWhenReported = null;
        viewModel.ActionFailed += (_, _) => openWhenReported = _output.IsOpen;
        viewModel.Query = "half";

        viewModel.RunSelected();

        // Already closed when the failure is reported, so that whoever reports it finds the launcher as it will stay.
        Assert.False(openWhenReported);
        Assert.False(_output.IsOpen);
        Assert.Equal("", _output.Status);
        Assert.Equal("", viewModel.Query);
        Assert.False(viewModel.InArgumentMode);
    }

    [Fact]
    public void A_listener_of_the_view_that_throws_when_it_is_closed_does_not_stop_the_failure_from_being_reported()
    {
        var viewModel = ViewModelWithAnItemThatOpensTheViewAndThrows();
        bool reported = false;
        viewModel.ActionFailed += (_, _) => reported = true;
        viewModel.Query = "half";
        _output.PropertyChanged += (_, _) =>
        {
            if (!_output.IsOpen)
            {
                throw new InvalidOperationException("listener broke");
            }
        };

        Assert.Throws<InvalidOperationException>(() => viewModel.RunSelected());

        Assert.True(reported);
        Assert.Equal("", viewModel.Query);
    }

    [Fact]
    public void Hiding_the_window_empties_the_input_even_when_a_listener_of_the_view_throws_as_it_closes()
    {
        RunReport();
        _viewModel.Query = "typed while the view was open";
        _output.PropertyChanged += (_, _) => throw new InvalidOperationException("listener broke");

        Assert.Throws<InvalidOperationException>(() => _viewModel.Reset());

        Assert.False(_output.IsOpen);
        Assert.Equal("", _viewModel.Query);
        Assert.False(_viewModel.InArgumentMode);
    }

    [Fact]
    public void Hiding_the_window_leaves_argument_mode_even_when_a_listener_of_the_view_throws_as_it_closes()
    {
        _viewModel.Query = "restart";
        _viewModel.EnterArgumentMode();
        _viewModel.Query = "web 1";
        _output.Open("Opened from elsewhere");
        _output.PropertyChanged += (_, _) => throw new InvalidOperationException("listener broke");

        Assert.Throws<InvalidOperationException>(() => _viewModel.Reset());

        Assert.False(_viewModel.InArgumentMode);
        Assert.Equal("", _viewModel.Query);
        Assert.Equal("", _viewModel.ArgumentTitle);
    }

    [Fact]
    public void A_second_run_takes_over_the_view_and_the_first_then_ends_unseen()
    {
        RunReport();
        _viewModel.CloseOutput();
        _viewModel.Query = "restart";
        _viewModel.RunSelected();
        _viewModel.Query = "db";
        _viewModel.RunSelected();

        Assert.Equal("Restart", _output.Title);
        Assert.False(_output.Complete(_tickets["Report"], "Finished", "the report"));
        Assert.True(_output.Complete(_tickets["Restart"], "Exit code 1", "no such container"));
        Assert.Equal("Restart", _output.Title);
        Assert.Equal("Exit code 1", _output.Status);
    }

    [Fact]
    public void An_action_failed_handler_that_throws_after_a_kept_open_run_still_leaves_an_empty_input()
    {
        _viewModel.ActionFailed += (_, _) => throw new InvalidOperationException("handler broke");
        _viewModel.Query = "broken";

        Assert.Throws<InvalidOperationException>(() => _viewModel.RunSelected());

        Assert.Equal("", _viewModel.Query);
        Assert.False(_viewModel.InArgumentMode);
    }
}
