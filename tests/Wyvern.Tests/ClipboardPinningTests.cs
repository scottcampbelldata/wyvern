using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Launcher;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>Pinned entries in the clipboard history view: where they stand, and what pinning, unpinning and deleting do to the list.</summary>
public sealed class ClipboardPinningTests : IDisposable
{
    private static readonly DateTimeOffset Copied = new(2026, 10, 3, 11, 30, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly List<ClipEntry> _history = [];
    private readonly List<string> _said = [];
    private readonly List<string> _deleted = [];
    private readonly FakeClock _clock = new();
    private readonly string _day;
    private readonly ClipPins _pins;
    private readonly ClipboardViewModel _view;

    public ClipboardPinningTests()
    {
        _day = Path.Combine(_dir.Path, "clipboard", "OTTER-PC", "2026-10-03");
        Directory.CreateDirectory(_day);
        _pins = new ClipPins(Path.Combine(_dir.Path, "clipboard-pins"));
        _view = new ClipboardViewModel(
            () => _history,
            _clock,
            new ClipboardActions(_ => { }, _deleted.Add, _ => { }, _ => { }) { Say = _said.Add },
            TimeZoneInfo.Utc,
            _pins);
    }

    public void Dispose() => _dir.Dispose();

    // Entries are given newest first, as the history gives them: each a minute older than the one before.
    private ClipEntry TextClip(string id, string text, int minutesEarlier = 0) =>
        new ClipEntry(id, Copied.AddMinutes(-minutesEarlier), "OTTER-PC", new DateOnly(2026, 10, 3), _day) { Text = text, TextLength = text.Length };

    private void Fill(params string[] ids)
    {
        _history.Clear();
        for (int i = 0; i < ids.Length; i++)
        {
            _history.Add(TextClip(ids[i], "text of " + ids[i], i));
        }
    }

    private string[] Listed() => _view.Rows.Select(row => (row.Pinned ? "pinned:" : row.Group + ":") + row.Entry.Id).ToArray();

    [Fact]
    public void A_view_without_pins_lists_nothing_as_pinned_and_pins_nothing()
    {
        Fill("a", "b");
        var view = new ClipboardViewModel(() => _history, _clock, new ClipboardActions(_ => { }, _ => { }, _ => { }, _ => { }) { Say = _said.Add });
        view.Open();

        Assert.False(view.CanPin);
        view.TogglePin();

        Assert.All(view.Rows, row => Assert.False(row.Pinned));
        Assert.Empty(_said);
    }

    [Fact]
    public void Pinned_entries_stand_first_under_their_own_heading_and_are_not_listed_a_second_time()
    {
        Fill("a", "b", "c");
        _pins.Pin(_history[1]);

        _view.Open();

        Assert.True(_view.CanPin);
        Assert.Equal(["pinned:b", "Today:a", "Today:c"], Listed());
        Assert.Equal(ClipboardViewModel.PinnedGroup, _view.Rows[0].Group);
        Assert.Equal(0, _view.SelectedIndex);
        Assert.True(_view.SelectedIsPinned);
    }

    [Fact]
    public void Pinning_moves_the_row_to_the_top_keeps_it_selected_and_says_so()
    {
        Fill("a", "b", "c");
        _view.Open();
        _view.SelectedIndex = 2;

        _view.TogglePin();

        Assert.Equal(["pinned:c", "Today:a", "Today:b"], Listed());
        Assert.Equal(0, _view.SelectedIndex);
        Assert.Equal("c", _view.Selected!.Id);
        Assert.True(_view.SelectedIsPinned);
        Assert.True(_pins.IsPinned("c"));
        Assert.Equal([ClipboardViewModel.PinnedWords], _said);
    }

    [Fact]
    public void The_entry_pinned_last_is_the_first_of_the_pinned()
    {
        Fill("a", "b", "c");
        _view.Open();
        _view.SelectedIndex = 1;
        _view.TogglePin();
        _view.SelectedIndex = 2;
        _view.TogglePin();

        Assert.Equal(["pinned:c", "pinned:b", "Today:a"], Listed());
    }

    [Fact]
    public void Unpinning_puts_the_row_back_under_its_day_keeps_it_selected_and_says_so()
    {
        Fill("a", "b", "c");
        _pins.Pin(_history[2]);
        _view.Open();

        _view.TogglePin();

        Assert.Equal(["Today:a", "Today:b", "Today:c"], Listed());
        Assert.Equal("c", _view.Selected!.Id);
        Assert.False(_view.SelectedIsPinned);
        Assert.False(_pins.IsPinned("c"));
        Assert.Equal([ClipboardViewModel.UnpinnedWords], _said);
    }

    [Fact]
    public void A_pinned_entry_stays_when_the_history_has_let_go_of_it_and_goes_when_it_is_unpinned()
    {
        Fill("a", "b");
        _pins.Pin(_history[1]);

        // The day expired, or was cleared.
        Fill("a");
        _view.Open();
        Assert.Equal(["pinned:b", "Today:a"], Listed());
        Assert.Equal("text of b", _view.PreviewText);

        _view.TogglePin();

        Assert.Equal(["Today:a"], Listed());
        Assert.Equal("a", _view.Selected!.Id);
    }

    [Fact]
    public void A_pinned_picture_is_shown_from_the_pins_own_folder()
    {
        string image = new string('4', 64) + ClipPaths.ImageExtension;
        File.WriteAllText(Path.Combine(_day, image), "picture bytes");
        _history.Add(new ClipEntry("p", Copied, "OTTER-PC", new DateOnly(2026, 10, 3), _day) { Image = image });
        _view.Open();

        _view.TogglePin();

        string kept = Path.Combine(_dir.Path, "clipboard-pins", image);
        Assert.Equal(kept, _view.Rows[0].Picture);
        Assert.Equal(kept, _view.PreviewImage);
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void An_entry_whose_content_has_gone_is_not_pinned_and_the_view_says_so()
    {
        _history.Add(new ClipEntry("p", Copied, "OTTER-PC", new DateOnly(2026, 10, 3), _day) { Image = new string('4', 64) + ClipPaths.ImageExtension });
        _view.Open();

        _view.TogglePin();

        Assert.Equal(["Today:p"], Listed());
        Assert.Equal([ClipboardViewModel.PinContentGoneWords], _said);
    }

    [Fact]
    public void When_the_most_are_pinned_the_view_says_so()
    {
        for (int i = 0; i < ClipPins.MaxPins; i++)
        {
            _pins.Pin(TextClip("pin-" + i, "text"));
        }

        Fill("a");
        _view.Open();
        _view.SelectedIndex = ClipPins.MaxPins;

        _view.TogglePin();

        Assert.False(_pins.IsPinned("a"));
        Assert.Equal([ClipboardViewModel.TooManyPinsWords], _said);
        Assert.Equal("a", _view.Selected!.Id);
    }

    [Fact]
    public void What_is_typed_and_the_choice_of_kinds_narrow_the_pinned_entries_too()
    {
        _history.Add(TextClip("a", "harbour seal"));
        _history.Add(TextClip("b", "grey heron", 1));
        _history.Add(TextClip("c", "https://example.com/seals", 2) with { Link = "https://example.com/seals" });
        _pins.Pin(_history[1]);
        _pins.Pin(_history[2]);
        _view.Open();
        Assert.Equal(["pinned:c", "pinned:b", "Today:a"], Listed());

        _view.Filter = "heron";
        Assert.Equal(["pinned:b"], Listed());

        _view.Filter = "";
        _view.TypeFilter = ClipTypeFilter.Links;
        Assert.Equal(["pinned:c"], Listed());

        _view.TypeFilter = ClipTypeFilter.Text;
        Assert.Equal(["pinned:b", "Today:a"], Listed());
    }

    [Fact]
    public void Deleting_a_pinned_entry_unpins_it_as_well()
    {
        Fill("a", "b");
        _pins.Pin(_history[1]);
        _view.Open();

        var removed = _view.Remove();

        Assert.Equal("b", removed!.Id);
        Assert.False(_pins.IsPinned("b"));

        // The history still has it until the deleting is done, and it does not come back as an ordinary row.
        Assert.Equal(["Today:a"], Listed());
        _view.Refresh();
        Assert.Equal(["Today:a"], Listed());
    }

    [Fact]
    public void Deleting_an_entry_that_is_not_pinned_leaves_the_pins_alone()
    {
        Fill("a", "b");
        _pins.Pin(_history[1]);
        _view.Open();
        _view.SelectedIndex = 1;

        _view.Remove();

        Assert.Equal(["pinned:b"], Listed());
        Assert.True(_pins.IsPinned("b"));
    }

    [Fact]
    public void No_more_rows_are_listed_than_the_most_with_pinned_entries_counted()
    {
        for (int i = 0; i < 3; i++)
        {
            _pins.Pin(TextClip("pin-" + i, "text"));
        }

        Fill(Enumerable.Range(0, ClipboardViewModel.MaxRows + 20).Select(i => "h" + i).ToArray());
        _view.Open();

        Assert.Equal(ClipboardViewModel.MaxRows, _view.Rows.Count);
        Assert.Equal(3, _view.Rows.Count(row => row.Pinned));
    }

    [Fact]
    public void A_refresh_keeps_a_pinned_row_selected()
    {
        Fill("a", "b");
        _pins.Pin(_history[1]);
        _view.Open();
        Assert.Equal("b", _view.Selected!.Id);

        _history.Insert(0, TextClip("new", "just copied"));
        _view.Refresh();

        Assert.Equal(["pinned:b", "Today:new", "Today:a"], Listed());
        Assert.Equal("b", _view.Selected!.Id);
    }

    [Fact]
    public void The_launcher_pins_only_while_the_history_is_open()
    {
        Fill("a");
        var usage = new UsageStore(_dir.File("usage.json"), _clock);
        var launcher = new LauncherViewModel(new SearchEngine([], usage), usage, clipboard: _view);

        launcher.TogglePinSelectedClip();
        Assert.False(_pins.IsPinned("a"));

        launcher.OpenClipboard();
        launcher.TogglePinSelectedClip();
        Assert.True(_pins.IsPinned("a"));
    }
}
