using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The emoji picker by itself: what the grid holds as text is typed, and how the selection moves in it. The emoji are a list the test fills with plain letters.</summary>
public sealed class EmojiViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<EmojiItem> _all = [];
    private readonly Dictionary<string, double> _boosts = [];
    private readonly List<string> _pasted = [];
    private readonly List<string> _copied = [];
    private readonly EmojiViewModel _view;

    public EmojiViewModelTests()
    {
        _view = new EmojiViewModel(() => _all, new EmojiActions(_pasted.Add, _copied.Add), id => _boosts.GetValueOrDefault(id, 1.0));
    }

    public void Dispose() => _dir.Dispose();

    // So many emoji under one heading, called "<group> 0", "<group> 1" and so on, each a text of its own.
    private void Add(string group, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _all.Add(new EmojiItem(group[..1] + i, group + " " + i, group));
        }
    }

    // The grid as lines of text: a heading as it is, a line of emoji as their texts, the selected one in brackets.
    private string[] Grid() => _view.Rows
        .Select(row => row.Heading ?? string.Join(" ", row.Cells.Select(cell => cell.IsSelected ? "[" + cell.Emoji.Text + "]" : cell.Emoji.Text)))
        .ToArray();

    [Fact]
    public void A_picker_that_is_closed_shows_nothing()
    {
        Add("Faces", 3);

        Assert.False(_view.IsOpen);
        Assert.Empty(_view.Rows);
        Assert.Null(_view.Selected);
        Assert.Equal(-1, _view.SelectedRow);
        Assert.Equal("", _view.Title);
        Assert.Equal("", _view.Prompt);

        _view.Filter = "face";
        _view.Move(1, 1);
        Assert.Empty(_view.Rows);
    }

    [Fact]
    public void Opened_it_lists_everything_under_its_headings_eight_to_a_line_with_the_first_selected()
    {
        Add("Faces", 10);
        Add("Animals", 3);

        _view.Open();

        Assert.True(_view.IsOpen);
        Assert.Equal(EmojiViewModel.Heading, _view.Title);
        Assert.Equal(EmojiViewModel.FilterPrompt, _view.Prompt);
        Assert.Equal(
            [
                "Faces",
                "[F0] F1 F2 F3 F4 F5 F6 F7",
                "F8 F9",
                "Animals",
                "A0 A1 A2",
            ],
            Grid());
        Assert.Equal("F0", _view.Selected!.Text);
        Assert.Equal(1, _view.SelectedRow);
        Assert.Equal("", _view.Status);
    }

    [Fact]
    public void The_emoji_picked_before_stand_first_the_most_picked_first_and_again_under_their_own_heading()
    {
        Add("Faces", 3);
        Add("Animals", 2);
        _boosts["emoji:A1"] = 3.0;
        _boosts["emoji:F2"] = 5.0;

        _view.Open();

        Assert.Equal(
            [
                EmojiViewModel.FrequentGroup,
                "[F2] A1",
                "Faces",
                "F0 F1 F2",
                "Animals",
                "A0 A1",
            ],
            Grid());
    }

    [Fact]
    public void No_more_than_two_lines_are_listed_as_picked_before()
    {
        Add("Faces", 30);
        for (int i = 0; i < 30; i++)
        {
            _boosts["emoji:F" + i] = 2.0 + i;
        }

        _view.Open();

        Assert.Equal(EmojiViewModel.FrequentGroup, _view.Rows[0].Heading);
        Assert.Equal(8, _view.Rows[1].Cells.Count);
        Assert.Equal(8, _view.Rows[2].Cells.Count);
        Assert.Equal("Faces", _view.Rows[3].Heading);
        Assert.Equal("F29", _view.Rows[1].Cells[0].Emoji.Text);
    }

    [Fact]
    public void Typing_keeps_the_emoji_whose_names_hold_every_word_under_one_heading()
    {
        _all.Add(new EmojiItem("a", "Grinning Face", "Faces"));
        _all.Add(new EmojiItem("b", "Face With Tears Of Joy", "Faces"));
        _all.Add(new EmojiItem("c", "Cat Face", "Animals"));
        _all.Add(new EmojiItem("d", "Party Popper", "Activities"));
        _boosts["emoji:d"] = 9.0;
        _view.Open();

        _view.Filter = "FACE";
        Assert.Equal([EmojiViewModel.ResultsGroup, "[a] b c"], Grid());

        _view.Filter = "  face   joy ";
        Assert.Equal([EmojiViewModel.ResultsGroup, "[b]"], Grid());

        _view.Filter = "tears grinning";
        Assert.Empty(_view.Rows);
        Assert.Null(_view.Selected);
        Assert.Equal(EmojiViewModel.NoMatchStatus, _view.Status);

        // Emptied again, everything is back, with what was picked before first.
        _view.Filter = "";
        Assert.Equal(EmojiViewModel.FrequentGroup, _view.Rows[0].Heading);
        Assert.Equal("d", _view.Selected!.Text);
        Assert.Equal("", _view.Status);
    }

    [Fact]
    public void An_emoji_is_found_by_typing_the_character_itself()
    {
        _all.Add(new EmojiItem("a", "Grinning Face", "Faces"));
        _all.Add(new EmojiItem("b", "Cat Face", "Animals"));
        _view.Open();

        _view.Filter = "b";

        Assert.Equal([EmojiViewModel.ResultsGroup, "[b]"], Grid());
    }

    [Fact]
    public void With_no_emoji_at_all_the_picker_says_so()
    {
        _view.Open();

        Assert.Empty(_view.Rows);
        Assert.Equal(EmojiViewModel.NoneStatus, _view.Status);

        _view.Move(1, 0);
        Assert.Null(_view.Selected);
    }

    [Fact]
    public void Left_and_right_go_through_the_emoji_in_reading_order_and_stop_at_the_ends()
    {
        Add("Faces", 9);
        Add("Animals", 2);
        _view.Open();

        _view.Move(-1, 0);
        Assert.Equal("F0", _view.Selected!.Text);

        _view.Move(7, 0);
        Assert.Equal("F7", _view.Selected!.Text);
        Assert.Equal(1, _view.SelectedRow);

        // On from the end of one line to the start of the next, and over a heading.
        _view.Move(1, 0);
        Assert.Equal("F8", _view.Selected!.Text);
        Assert.Equal(2, _view.SelectedRow);
        _view.Move(1, 0);
        Assert.Equal("A0", _view.Selected!.Text);
        Assert.Equal(4, _view.SelectedRow);

        _view.Move(5, 0);
        Assert.Equal("A1", _view.Selected!.Text);

        _view.Move(-1, 0);
        Assert.Equal("A0", _view.Selected!.Text);
    }

    [Fact]
    public void Up_and_down_keep_the_place_in_the_line_and_go_to_its_end_where_the_line_is_shorter()
    {
        Add("Faces", 20);
        Add("Animals", 3);
        _view.Open();
        _view.Move(6, 0);
        Assert.Equal("F6", _view.Selected!.Text);

        _view.Move(0, 1);
        Assert.Equal("F14", _view.Selected!.Text);

        // The third line of faces has four: the place is its last.
        _view.Move(0, 1);
        Assert.Equal("F19", _view.Selected!.Text);

        // Over the heading into the animals, which are three.
        _view.Move(0, 1);
        Assert.Equal("A2", _view.Selected!.Text);

        // The last line: down stays.
        _view.Move(0, 1);
        Assert.Equal("A2", _view.Selected!.Text);

        _view.Move(0, -1);
        Assert.Equal("F18", _view.Selected!.Text);

        // Several lines at once stop at the first.
        _view.Move(0, -10);
        Assert.Equal("F2", _view.Selected!.Text);
        _view.Move(0, 10);
        Assert.Equal("A2", _view.Selected!.Text);
    }

    [Fact]
    public void Only_the_selected_cell_says_that_it_is_and_it_says_so_when_that_changes()
    {
        Add("Faces", 4);
        _view.Open();
        var first = _view.Rows[1].Cells[0];
        var third = _view.Rows[1].Cells[2];
        var changed = new List<string>();
        first.PropertyChanged += (_, e) => changed.Add("first " + e.PropertyName);
        third.PropertyChanged += (_, e) => changed.Add("third " + e.PropertyName);

        _view.Move(2, 0);

        Assert.False(first.IsSelected);
        Assert.True(third.IsSelected);
        Assert.Equal(["first IsSelected", "third IsSelected"], changed);
        Assert.Equal(1, _view.Rows.SelectMany(row => row.Cells).Count(cell => cell.IsSelected));
    }

    [Fact]
    public void A_click_selects_the_cell_that_was_clicked_and_a_cell_from_an_earlier_grid_is_ignored()
    {
        Add("Faces", 4);
        _view.Open();
        var stale = _view.Rows[1].Cells[3];

        _view.Select(_view.Rows[1].Cells[2]);
        Assert.Equal("F2", _view.Selected!.Text);

        _view.Filter = "faces 1";
        _view.Select(stale);
        Assert.Equal("F1", _view.Selected!.Text);
    }

    [Fact]
    public void The_selection_is_announced_when_it_moves_and_when_the_grid_is_made_again()
    {
        Add("Faces", 4);
        var announced = new List<string?>();
        _view.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        _view.Open();
        Assert.Contains(nameof(EmojiViewModel.IsOpen), announced);
        Assert.Contains(nameof(EmojiViewModel.Selected), announced);

        announced.Clear();
        _view.Move(1, 0);
        Assert.Equal([nameof(EmojiViewModel.Selected)], announced);

        // A move that goes nowhere says nothing.
        announced.Clear();
        _view.Move(0, 1);
        Assert.Empty(announced);

        _view.Filter = "2";
        Assert.Contains(nameof(EmojiViewModel.Selected), announced);
    }

    [Fact]
    public void Closing_lets_go_of_everything_and_opening_again_starts_afresh()
    {
        Add("Faces", 4);
        _view.Open();
        _view.Filter = "faces 3";

        Assert.True(_view.Close());

        Assert.False(_view.IsOpen);
        Assert.Empty(_view.Rows);
        Assert.Null(_view.Selected);
        Assert.Equal("", _view.Filter);
        Assert.False(_view.Close());

        _view.Open();
        Assert.Equal("F0", _view.Selected!.Text);
        Assert.Equal(2, _view.Rows.Count);
    }

    private LauncherViewModel Launcher(out UsageStore usage, params SearchItem[] items)
    {
        usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        return new LauncherViewModel(new SearchEngine([new Listed(items)], usage), usage, emoji: _view);
    }

    private sealed class Listed(SearchItem[] items) : ISearchProvider, IBrowsableProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);

        public IReadOnlyList<SearchItem> Browse() => items;
    }

    [Fact]
    public void In_the_launcher_typing_filters_the_picker_and_there_are_no_results_while_it_is_open()
    {
        Add("Faces", 4);
        var launcher = Launcher(out _, new SearchItem("command:x", "Faces Command", "", ItemKind.Command, () => { }));
        launcher.Refresh();
        Assert.Single(launcher.Results);

        _view.Open();

        Assert.Empty(launcher.Results);
        Assert.Equal("", launcher.Query);

        launcher.Query = "faces 2";
        Assert.Equal("F2", _view.Selected!.Text);
        Assert.Empty(launcher.Results);
        Assert.False(launcher.EnterArgumentMode());

        // Esc: back to the search, empty, with the launcher's own list again.
        Assert.True(launcher.CloseEmoji());
        Assert.Equal("", launcher.Query);
        Assert.Single(launcher.Results);
        Assert.False(launcher.CloseEmoji());
    }

    [Fact]
    public void Enter_pastes_the_selected_emoji_hides_the_window_first_counts_the_use_and_ends_in_an_empty_search()
    {
        Add("Faces", 4);
        var launcher = Launcher(out var usage);
        var order = new List<string>();
        launcher.Dismissed += (_, _) => order.Add("dismissed");
        _view.Open();
        launcher.Query = "faces 2";

        launcher.RunSelected();

        Assert.Equal(["F2"], _pasted);
        Assert.Empty(_copied);
        Assert.Equal(["dismissed"], order);
        Assert.False(_view.IsOpen);
        Assert.Equal("", launcher.Query);
        Assert.True(usage.Boost("emoji:F2") > 1.0);
    }

    [Fact]
    public void Ctrl_enter_does_nothing_in_the_picker_and_the_copy_key_copies_without_pasting()
    {
        Add("Faces", 4);
        var launcher = Launcher(out var usage);
        _view.Open();

        launcher.RunSelected(secondary: true);
        Assert.True(_view.IsOpen);
        Assert.Empty(_pasted);

        launcher.CopySelectedEmoji();
        Assert.Equal(["F0"], _copied);
        Assert.Empty(_pasted);
        Assert.False(_view.IsOpen);
        Assert.True(usage.Boost("emoji:F0") > 1.0);

        // With the picker closed the copy key does nothing.
        launcher.CopySelectedEmoji();
        Assert.Single(_copied);
    }

    [Fact]
    public void A_paste_that_fails_is_reported_by_the_emoji_s_id_and_the_launcher_still_ends_in_an_empty_search()
    {
        Add("Faces", 2);
        var failing = new EmojiViewModel(() => _all, new EmojiActions(_ => throw new InvalidOperationException("no window"), _ => { }));
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var launcher = new LauncherViewModel(new SearchEngine([], usage), usage, emoji: failing);
        var failures = new List<string>();
        launcher.ActionFailed += (_, failure) => failures.Add(failure.ItemId);
        failing.Open();

        launcher.RunSelected();

        Assert.Equal(["emoji:F0"], failures);
        Assert.False(failing.IsOpen);
        Assert.Equal(1.0, usage.Boost("emoji:F0"));
    }

    [Fact]
    public void Hiding_the_window_closes_the_picker()
    {
        Add("Faces", 2);
        var launcher = Launcher(out _);
        _view.Open();
        launcher.Query = "faces";

        launcher.Reset();

        Assert.False(_view.IsOpen);
        Assert.Equal("", launcher.Query);
    }

    [Fact]
    public void Opening_the_clipboard_history_closes_the_picker_and_the_other_way_round()
    {
        Add("Faces", 2);
        var launcher = Launcher(out _);
        _view.Open();

        launcher.OpenClipboard();
        Assert.False(_view.IsOpen);
        Assert.True(launcher.Clipboard.IsOpen);

        _view.Open();
        Assert.True(_view.IsOpen);
        Assert.False(launcher.Clipboard.IsOpen);
    }
}
