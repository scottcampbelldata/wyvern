using Beacon.Core.ClipboardHistory;
using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>
/// The launcher in clipboard mode. The history is a list the test fills, and what the keys do
/// with an entry is written down in the same list as the launcher's own events, so that their
/// order can be read off it.
/// </summary>
public sealed class LauncherClipboardTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly UsageStore _usage;
    private readonly OutputViewModel _output = new();
    private readonly List<ClipEntry> _history = [];
    private readonly List<string> _events = [];
    private readonly List<ActionFailedEventArgs> _failures = [];
    private readonly ClipboardViewModel _clipboard;
    private readonly LauncherViewModel _viewModel;

    // When set, every action on an entry throws it after it has been written down.
    private Exception? _broken;

    public LauncherClipboardTests()
    {
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var actions = new ClipboardActions(
            Paste: entry => Did("pasted " + entry.Id),
            Delete: id => Did("deleted " + id),
            Save: entry => Did("saved " + entry.Id),
            OpenLink: link => Did("opened " + link));
        _clipboard = new ClipboardViewModel(() => _history, new FakeClock(), actions, TimeZoneInfo.Utc);
        _viewModel = NewViewModel(_clipboard);
        _viewModel.Dismissed += (_, _) => _events.Add("dismissed");
        _viewModel.ActionFailed += (_, failure) => _failures.Add(failure);
    }

    public void Dispose() => _dir.Dispose();

    private sealed class FixedItems(SearchItem[] items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);
    }

    private LauncherViewModel NewViewModel(ClipboardViewModel clipboard)
    {
        SearchItem[] items =
        [
            // The Clipboard History command as it is made: its action opens the view, and it keeps the launcher open.
            new("command:clipboard-history", "Clipboard History", "", ItemKind.Command, Run: clipboard.Open, KeepOpen: true),
            new("command:plain", "Plain walrus", "", ItemKind.Command, Run: () => _events.Add("ran plain")),
            new("quicklink:otter", "Otter search", "", ItemKind.Quicklink, Run: null,
                Argument: new ItemArgument("Search otters for", text => _events.Add($"ran otter with '{text}'"))),
            new("script:report.ps1", "Report", "", ItemKind.Script, Run: () => _output.Open("Report"), KeepOpen: true),
        ];
        return new LauncherViewModel(new SearchEngine([new FixedItems(items)], _usage), _usage, _output, clipboard);
    }

    private void Did(string what)
    {
        _events.Add(what);
        if (_broken is { } exception)
        {
            throw exception;
        }
    }

    private static ClipEntry Clip(string id) =>
        new(id, new DateTimeOffset(2026, 10, 3, 11, 30, 0, TimeSpan.Zero), "OTTER-PC", new DateOnly(2026, 10, 3), @"C:\made-up\clipboard\OTTER-PC\2026-10-03");

    private static ClipEntry TextClip(string id, string text) => Clip(id) with { Text = text, TextLength = text.Length };

    private static ClipEntry LinkClip(string id, string address) => TextClip(id, address) with { Link = address };

    // Opens the history the way the hotkey does, over the entries given.
    private void OpenOver(params ClipEntry[] entries)
    {
        _history.AddRange(entries);
        _viewModel.OpenClipboard();
    }

    private List<string> RowIds() => _clipboard.Rows.Select(row => row.Entry.Id).ToList();

    // One of the three keys that hide the window and then do something with the selected entry.
    private void Press(string key)
    {
        switch (key)
        {
            case "Enter":
                _viewModel.RunSelected();
                break;
            case "Ctrl+Enter":
                _viewModel.RunSelected(secondary: true);
                break;
            case "Ctrl+S":
                _viewModel.SaveSelectedClip();
                break;
            default:
                Assert.Fail("The test names a key it does not know.");
                break;
        }
    }

    private void AssertClosedAndEmpty()
    {
        Assert.False(_clipboard.IsOpen);
        Assert.Empty(_clipboard.Rows);
        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);
        Assert.False(_viewModel.InArgumentMode);
    }

    [Fact]
    public void The_launcher_holds_the_clipboard_view_it_was_given()
    {
        Assert.Same(_clipboard, _viewModel.Clipboard);
        Assert.False(_viewModel.Clipboard.IsOpen);
    }

    [Fact]
    public void A_launcher_made_without_a_clipboard_view_has_a_closed_one_of_its_own_and_searches_as_before()
    {
        SearchItem[] items = [new("command:plain", "Plain walrus", "", ItemKind.Command, Run: () => _events.Add("ran plain"))];
        var viewModel = new LauncherViewModel(new SearchEngine([new FixedItems(items)], _usage), _usage, _output);
        viewModel.Dismissed += (_, _) => _events.Add("dismissed");

        Assert.NotNull(viewModel.Clipboard);
        Assert.NotSame(_clipboard, viewModel.Clipboard);
        Assert.False(viewModel.Clipboard.IsOpen);

        viewModel.Query = "plain";
        Assert.Equal(["Plain walrus"], viewModel.Results.Select(r => r.Title));
        Assert.Equal(0, viewModel.SelectedIndex);

        viewModel.RunSelected();
        Assert.Equal(["dismissed", "ran plain"], _events);
    }

    [Fact]
    public void An_item_that_keeps_the_launcher_open_and_opens_the_history_shows_it_without_dismissing_the_launcher()
    {
        _history.Add(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "clipboard";

        _viewModel.RunSelected();

        Assert.Empty(_events);
        Assert.True(_clipboard.IsOpen);
        Assert.Equal(["walrus-1"], RowIds());
        Assert.Empty(_viewModel.Results);
        Assert.Equal(-1, _viewModel.SelectedIndex);
        Assert.Equal("", _viewModel.Query);
        Assert.Equal("", _clipboard.Filter);
    }

    [Fact]
    public void Running_the_item_that_opens_the_history_is_counted_as_a_use_of_it()
    {
        _viewModel.Query = "clipboard";

        _viewModel.RunSelected();

        Assert.Equal(["command:clipboard-history"], _usage.IdsStartingWith(""));
        Assert.Equal(1.05, _usage.Boost("command:clipboard-history"), precision: 3);
    }

    [Fact]
    public void The_hotkey_opens_the_history_over_a_search_and_empties_the_input_and_the_results()
    {
        _history.Add(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "plain";
        Assert.Single(_viewModel.Results);

        _viewModel.OpenClipboard();

        Assert.True(_clipboard.IsOpen);
        Assert.Equal(["walrus-1"], RowIds());
        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);
        Assert.Equal(-1, _viewModel.SelectedIndex);
        Assert.Empty(_events);
    }

    // The input box and the result list are bound to these, and show what was there before unless they are told.
    [Fact]
    public void Opening_the_history_over_a_search_announces_the_emptied_input_and_the_selection()
    {
        _viewModel.Query = "plain";
        var announced = new List<string?>();
        _viewModel.PropertyChanged += (_, changed) => announced.Add(changed.PropertyName);

        _viewModel.OpenClipboard();

        Assert.Contains(nameof(LauncherViewModel.Query), announced);
        Assert.Contains(nameof(LauncherViewModel.SelectedIndex), announced);
    }

    [Fact]
    public void Opening_a_history_that_is_already_open_starts_it_afresh_with_an_empty_input()
    {
        OpenOver(TextClip("walrus-1", "walrus"), TextClip("otter-2", "otter"), TextClip("otter-3", "otter pup"));
        _viewModel.Query = "otter";
        _viewModel.MoveSelection(1);

        _viewModel.OpenClipboard();

        Assert.True(_clipboard.IsOpen);
        Assert.Equal("", _viewModel.Query);
        Assert.Equal("", _clipboard.Filter);
        Assert.Equal(["walrus-1", "otter-2", "otter-3"], RowIds());
        Assert.Equal(0, _clipboard.SelectedIndex);
        Assert.Empty(_viewModel.Results);
    }

    [Fact]
    public void The_hotkey_opens_the_history_from_argument_mode_and_gives_up_the_argument()
    {
        _viewModel.Query = "otter";
        Assert.True(_viewModel.EnterArgumentMode());
        _viewModel.Query = "typed for the otter";
        var announced = new List<string?>();
        _viewModel.PropertyChanged += (_, changed) => announced.Add(changed.PropertyName);

        _viewModel.OpenClipboard();

        Assert.True(_clipboard.IsOpen);
        Assert.False(_viewModel.InArgumentMode);
        Assert.Equal("", _viewModel.ArgumentTitle);
        Assert.Equal("", _viewModel.ArgumentPrompt);
        Assert.Contains(nameof(LauncherViewModel.InArgumentMode), announced);
        Assert.Equal("", _viewModel.Query);
        Assert.Equal("", _clipboard.Filter);
        Assert.Empty(_viewModel.Results);
        Assert.Empty(_events);
    }

    // The argument was given up for good: closing the history does not go back to collecting it.
    [Fact]
    public void Closing_a_history_that_was_opened_from_argument_mode_goes_to_an_empty_search_and_not_back_to_the_argument()
    {
        _viewModel.Query = "otter";
        _viewModel.EnterArgumentMode();
        _viewModel.Query = "typed for the otter";
        _viewModel.OpenClipboard();

        Assert.True(_viewModel.CloseClipboard());

        Assert.False(_viewModel.InArgumentMode);
        Assert.False(_viewModel.LeaveArgumentMode());
        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);

        _viewModel.RunSelected();
        Assert.Empty(_events);
    }

    [Fact]
    public void The_hotkey_opens_the_history_from_output_mode_and_closes_the_output_view()
    {
        _viewModel.Query = "report";
        _viewModel.RunSelected();
        Assert.True(_output.IsOpen);
        _viewModel.Query = "typed while the output was on show";

        _viewModel.OpenClipboard();

        Assert.False(_output.IsOpen);
        Assert.True(_clipboard.IsOpen);
        Assert.Equal("", _viewModel.Query);
        Assert.Equal("", _clipboard.Filter);
        Assert.Empty(_viewModel.Results);
        Assert.Empty(_events);
    }

    // "plain" is the name of an item: were it searched for, there would be a result.
    [Fact]
    public void Typing_in_clipboard_mode_filters_the_history_and_is_never_searched_for()
    {
        OpenOver(TextClip("walrus-1", "a plain walrus"), TextClip("otter-2", "otter"));

        _viewModel.Query = "plain";

        Assert.Equal("plain", _viewModel.Query);
        Assert.Equal("plain", _clipboard.Filter);
        Assert.Equal(["walrus-1"], RowIds());
        Assert.Empty(_viewModel.Results);
        Assert.Equal(-1, _viewModel.SelectedIndex);
    }

    [Fact]
    public void Up_and_down_move_the_selection_of_the_history_and_not_that_of_the_results()
    {
        OpenOver(TextClip("walrus-1", "walrus"), TextClip("otter-2", "otter"), TextClip("seal-3", "seal"));

        _viewModel.MoveSelection(2);
        Assert.Equal(2, _clipboard.SelectedIndex);

        _viewModel.MoveSelection(-1);
        Assert.Equal(1, _clipboard.SelectedIndex);
        Assert.Equal("otter-2", _clipboard.Selected?.Id);
        Assert.Equal(-1, _viewModel.SelectedIndex);
    }

    // The launcher empties its results as the history opens, so in the ordinary course nothing
    // is selected for Tab to act on. The view model does not rely on that. Here a listener that
    // was added to the view before the launcher's own throws as the view opens: the launcher is
    // never told, and the item that takes an argument stays selected behind the open history.
    [Fact]
    public void Tab_does_nothing_while_the_history_is_open_even_when_an_item_that_takes_an_argument_is_still_selected()
    {
        var clipboard = new ClipboardViewModel(() => _history, new FakeClock(), zone: TimeZoneInfo.Utc);
        clipboard.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ClipboardViewModel.IsOpen) && clipboard.IsOpen)
            {
                throw new InvalidOperationException("listener broke");
            }
        };
        var viewModel = NewViewModel(clipboard);
        viewModel.Query = "otter";
        Assert.Throws<InvalidOperationException>(clipboard.Open);
        Assert.True(clipboard.IsOpen);
        Assert.NotNull(viewModel.Results[viewModel.SelectedIndex].Argument);

        Assert.False(viewModel.EnterArgumentMode());

        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("otter", viewModel.Query);
    }

    [Fact]
    public void Enter_dismisses_the_launcher_and_only_then_pastes_the_selected_entry()
    {
        OpenOver(TextClip("walrus-1", "walrus"), TextClip("otter-2", "otter"), TextClip("seal-3", "seal"));
        _viewModel.MoveSelection(1);

        _viewModel.RunSelected();

        Assert.Equal(["dismissed", "pasted otter-2"], _events);
        Assert.Empty(_failures);
    }

    [Fact]
    public void After_a_paste_the_history_is_closed_and_the_input_is_empty()
    {
        OpenOver(TextClip("walrus-1", "walrus"), TextClip("otter-2", "otter"));
        _viewModel.Query = "ott";

        _viewModel.RunSelected();

        Assert.Equal(["dismissed", "pasted otter-2"], _events);
        AssertClosedAndEmpty();
        Assert.Equal("", _clipboard.Filter);
    }

    [Fact]
    public void A_paste_is_not_counted_as_a_use()
    {
        OpenOver(TextClip("walrus-1", "walrus"));

        _viewModel.RunSelected();

        Assert.Equal(["dismissed", "pasted walrus-1"], _events);
        Assert.Empty(_usage.IdsStartingWith(""));
        Assert.False(File.Exists(_dir.File("usage.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enter_and_Ctrl_Enter_with_nothing_selected_do_nothing_and_the_history_stays_open(bool secondary)
    {
        OpenOver();
        Assert.Null(_clipboard.Selected);

        _viewModel.RunSelected(secondary);

        Assert.Empty(_events);
        Assert.Empty(_failures);
        Assert.True(_clipboard.IsOpen);
    }

    [Fact]
    public void Ctrl_Enter_on_a_link_dismisses_the_launcher_and_only_then_opens_its_address()
    {
        OpenOver(TextClip("walrus-1", "walrus"), LinkClip("link-2", "https://example.org/otter"));
        _viewModel.MoveSelection(1);

        _viewModel.RunSelected(secondary: true);

        Assert.Equal(["dismissed", "opened https://example.org/otter"], _events);
        AssertClosedAndEmpty();
    }

    [Fact]
    public void Ctrl_Enter_on_an_entry_that_is_not_a_link_does_nothing_and_the_history_stays_open()
    {
        OpenOver(TextClip("walrus-1", "walrus"), LinkClip("link-2", "https://example.org/otter"));
        _viewModel.Query = "wal";

        _viewModel.RunSelected(secondary: true);

        Assert.Empty(_events);
        Assert.True(_clipboard.IsOpen);
        Assert.Equal("wal", _viewModel.Query);
        Assert.Equal(["walrus-1"], RowIds());
    }

    // A picture of the clipboard's own, and a picture kept from a copied file.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ctrl_S_on_an_entry_with_a_picture_dismisses_the_launcher_and_only_then_saves_it(bool keptFromAFile)
    {
        var picture = keptFromAFile
            ? Clip("picture-2") with { Files = [@"C:\harbour\seal.png"], Pictures = [new KeptPicture("seal.png", "kept.png")] }
            : Clip("picture-2") with { Image = "seal.png" };
        OpenOver(TextClip("walrus-1", "walrus"), picture);
        _viewModel.MoveSelection(1);

        _viewModel.SaveSelectedClip();

        Assert.Equal(["dismissed", "saved picture-2"], _events);
        AssertClosedAndEmpty();
    }

    [Fact]
    public void Ctrl_S_on_an_entry_without_a_picture_does_nothing_and_the_history_stays_open()
    {
        OpenOver(TextClip("walrus-1", "walrus"), Clip("picture-2") with { Image = "seal.png" });

        _viewModel.SaveSelectedClip();

        Assert.Empty(_events);
        Assert.True(_clipboard.IsOpen);
        Assert.Equal(["walrus-1", "picture-2"], RowIds());
    }

    [Fact]
    public void Ctrl_S_does_nothing_while_the_history_is_closed()
    {
        OpenOver(Clip("picture-1") with { Image = "seal.png" });
        _viewModel.CloseClipboard();
        _viewModel.Query = "plain";

        _viewModel.SaveSelectedClip();

        Assert.Empty(_events);
        Assert.Equal("plain", _viewModel.Query);
        Assert.Single(_viewModel.Results);
    }

    [Fact]
    public void Ctrl_Delete_deletes_the_selected_entry_by_its_id_and_takes_its_row_out_with_the_history_still_open()
    {
        OpenOver(TextClip("walrus-1", "walrus"), TextClip("otter-2", "otter"), TextClip("seal-3", "seal"));
        _viewModel.MoveSelection(1);

        _viewModel.DeleteSelectedClip();

        Assert.Equal(["deleted otter-2"], _events);
        Assert.Equal(["walrus-1", "seal-3"], RowIds());
        Assert.Equal("seal-3", _clipboard.Selected?.Id);
        Assert.True(_clipboard.IsOpen);
        Assert.Empty(_failures);
    }

    [Fact]
    public void Ctrl_Delete_does_nothing_while_the_history_is_closed()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.CloseClipboard();
        _viewModel.Query = "plain";

        _viewModel.DeleteSelectedClip();

        Assert.Empty(_events);
        Assert.Equal("plain", _viewModel.Query);
        Assert.Single(_viewModel.Results);

        // Nothing was taken out of the list behind the owner's back either.
        _viewModel.OpenClipboard();
        Assert.Equal(["walrus-1"], RowIds());
    }

    [Fact]
    public void Ctrl_Delete_with_nothing_selected_does_nothing()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "narwhal";
        Assert.Null(_clipboard.Selected);

        _viewModel.DeleteSelectedClip();

        Assert.Empty(_events);
        Assert.True(_clipboard.IsOpen);
    }

    // The entry is a link that came with a picture, so that each of the three keys acts on it.
    [Theory]
    [InlineData("Enter", "pasted walrus-1")]
    [InlineData("Ctrl+Enter", "opened https://example.org/walrus")]
    [InlineData("Ctrl+S", "saved walrus-1")]
    public void An_action_that_throws_is_reported_under_the_entrys_item_id_and_the_launcher_still_ends_closed_and_empty(string key, string action)
    {
        OpenOver(LinkClip("walrus-1", "https://example.org/walrus") with { Image = "walrus.png" });
        _viewModel.Query = "example";
        _broken = new InvalidOperationException("boom");

        Press(key);

        Assert.Equal(["dismissed", action], _events);
        var failure = Assert.Single(_failures);
        Assert.Equal("clipboard:walrus-1", failure.ItemId);
        Assert.Same(_broken, failure.Exception);
        AssertClosedAndEmpty();
    }

    // A window that has just been hidden tells the view model so, before the action has run. The
    // history is closed by then, and the entry that was selected is still the one acted on.
    [Theory]
    [InlineData("Enter", "pasted walrus-2")]
    [InlineData("Ctrl+Enter", "opened https://example.org/walrus")]
    [InlineData("Ctrl+S", "saved walrus-2")]
    public void The_selected_entry_is_still_the_one_acted_on_when_the_window_resets_as_it_hides(string key, string action)
    {
        OpenOver(TextClip("otter-1", "otter"), LinkClip("walrus-2", "https://example.org/walrus") with { Image = "walrus.png" });
        _viewModel.MoveSelection(1);
        _viewModel.Dismissed += (_, _) => _viewModel.Reset();

        Press(key);

        Assert.Equal(["dismissed", action], _events);
        Assert.Empty(_failures);
        AssertClosedAndEmpty();
    }

    [Fact]
    public void A_delete_that_throws_is_reported_under_the_entrys_item_id_and_the_history_stays_open()
    {
        OpenOver(TextClip("walrus-1", "walrus"), TextClip("otter-2", "otter"));
        _broken = new InvalidOperationException("boom");

        _viewModel.DeleteSelectedClip();

        Assert.Equal(["deleted walrus-1"], _events);
        var failure = Assert.Single(_failures);
        Assert.Equal("clipboard:walrus-1", failure.ItemId);
        Assert.Same(_broken, failure.Exception);
        Assert.True(_clipboard.IsOpen);
        Assert.Equal(["otter-2"], RowIds());
    }

    [Fact]
    public void A_dismissed_handler_that_throws_stops_the_paste_and_still_leaves_the_launcher_closed_and_empty()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";
        _viewModel.Dismissed += (_, _) => throw new InvalidOperationException("handler broke");

        Assert.Throws<InvalidOperationException>(() => _viewModel.RunSelected());

        Assert.Equal(["dismissed"], _events);
        AssertClosedAndEmpty();
    }

    [Fact]
    public void An_action_failed_handler_that_throws_after_a_paste_still_leaves_the_launcher_closed_and_empty()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";
        _broken = new InvalidOperationException("boom");
        _viewModel.ActionFailed += (_, _) => throw new InvalidOperationException("handler broke");

        Assert.Throws<InvalidOperationException>(() => _viewModel.RunSelected());

        Assert.Single(_failures);
        AssertClosedAndEmpty();
    }

    [Fact]
    public void Esc_closes_the_history_and_goes_back_to_an_empty_search()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";

        Assert.True(_viewModel.CloseClipboard());

        AssertClosedAndEmpty();
        Assert.Equal(-1, _viewModel.SelectedIndex);
        Assert.Empty(_events);
    }

    [Fact]
    public void Esc_with_no_history_open_is_left_to_the_other_modes()
    {
        _viewModel.Query = "plain";

        Assert.False(_viewModel.CloseClipboard());

        Assert.Equal("plain", _viewModel.Query);
        Assert.Equal(["Plain walrus"], _viewModel.Results.Select(r => r.Title));
    }

    [Fact]
    public void Once_the_history_is_closed_typing_is_searched_for_again()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "plain";
        Assert.Empty(_viewModel.Results);
        _viewModel.CloseClipboard();

        _viewModel.Query = "plain";

        Assert.Equal(["Plain walrus"], _viewModel.Results.Select(r => r.Title));
        Assert.Equal(0, _viewModel.SelectedIndex);
        Assert.Equal("", _clipboard.Filter);
    }

    [Fact]
    public void After_a_paste_typing_is_searched_for_again()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.RunSelected();

        _viewModel.Query = "plain";

        Assert.Equal(["Plain walrus"], _viewModel.Results.Select(r => r.Title));
    }

    // The view can be closed by whoever holds it, not only through the launcher. The launcher follows.
    [Fact]
    public void A_history_that_is_closed_from_outside_takes_the_launcher_back_to_an_empty_search()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";

        _clipboard.Close();

        Assert.Equal("", _viewModel.Query);
        Assert.Empty(_viewModel.Results);
    }

    [Fact]
    public void Hiding_the_window_closes_the_history_and_empties_the_input()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";

        _viewModel.Reset();

        AssertClosedAndEmpty();
        Assert.Equal("", _clipboard.Filter);
        Assert.Empty(_events);
    }

    [Fact]
    public void Hiding_the_window_empties_the_input_even_when_a_listener_of_the_history_throws_as_it_closes()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";
        _clipboard.PropertyChanged += (_, _) =>
        {
            if (!_clipboard.IsOpen)
            {
                throw new InvalidOperationException("listener broke");
            }
        };

        Assert.Throws<InvalidOperationException>(() => _viewModel.Reset());

        Assert.False(_clipboard.IsOpen);
        Assert.Equal("", _viewModel.Query);
    }

    // Nothing in Beacon opens the output view over the history. The view model does not rely on that.
    [Fact]
    public void Hiding_the_window_closes_the_history_even_when_a_listener_of_the_output_view_throws_as_it_closes()
    {
        OpenOver(TextClip("walrus-1", "walrus"));
        _viewModel.Query = "wal";
        _output.Open("Opened from elsewhere");
        _output.PropertyChanged += (_, _) => throw new InvalidOperationException("listener broke");

        Assert.Throws<InvalidOperationException>(() => _viewModel.Reset());

        Assert.False(_output.IsOpen);
        AssertClosedAndEmpty();
    }
}
