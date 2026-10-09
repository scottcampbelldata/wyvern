using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Ten quick runs of a script must not give a minute of stale balloons with a later failure
/// waiting behind them. At most three notes wait behind the one on screen; the rest are counted
/// and told as one note after the three. The waits are a list the test works through by hand, as
/// in <see cref="NoteQueueTests"/>.
/// </summary>
public class NoteQueueLimitTests
{
    private readonly List<string> _shown = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly NoteQueue _queue;

    public NoteQueueLimitTests()
    {
        _queue = new NoteQueue(_shown.Add, (after, action) => _waits.Add((after, action)));
    }

    /// <summary>Lets the oldest wait run out.</summary>
    private void GapEnds()
    {
        var wait = _waits[0];
        _waits.RemoveAt(0);
        wait.Action();
    }

    [Fact]
    public void Three_notes_wait_behind_the_one_on_screen()
    {
        Assert.Equal(3, NoteQueue.MaxWaiting);
    }

    [Fact]
    public void Five_notes_added_while_one_is_showing_give_three_more_shows_and_then_the_count_of_the_two_that_were_not()
    {
        _queue.Add("first");
        for (int i = 1; i <= 5; i++)
        {
            _queue.Add($"note {i}");
        }

        Assert.Equal(["first"], _shown);

        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["first", "note 1", "note 2", "note 3"], _shown);

        GapEnds();

        Assert.Equal(["first", "note 1", "note 2", "note 3", "2 more notifications were not shown."], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);

        GapEnds();

        Assert.Empty(_waits);
        Assert.Equal(5, _shown.Count);
    }

    [Fact]
    public void The_one_on_screen_and_three_waiting_are_all_shown_and_nothing_is_counted()
    {
        _queue.Add("first");
        _queue.Add("second");
        _queue.Add("third");
        _queue.Add("fourth");

        GapEnds();
        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["first", "second", "third", "fourth"], _shown);
        Assert.Empty(_waits);
    }

    [Fact]
    public void The_summary_holds_the_count_as_it_stands_when_it_is_shown()
    {
        _queue.Add("first");
        _queue.Add("a");
        _queue.Add("b");
        _queue.Add("c");
        _queue.Add("not shown 1");
        _queue.Add("not shown 2");

        // One of the three has had its turn, so there is room for one more, and then there is not.
        GapEnds();
        _queue.Add("d");
        _queue.Add("not shown 3");
        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["first", "a", "b", "c", "d"], _shown);

        GapEnds();

        Assert.Equal("3 more notifications were not shown.", _shown[^1]);
    }

    [Fact]
    public void One_note_that_was_not_shown_is_told_in_the_singular()
    {
        _queue.Add("first");
        _queue.Add("a");
        _queue.Add("b");
        _queue.Add("c");
        _queue.Add("not shown");

        GapEnds();
        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal("1 more notification was not shown.", _shown[^1]);
    }

    [Fact]
    public void After_the_summary_a_new_note_is_shown_at_once_and_the_count_starts_again()
    {
        _queue.Add("first");
        for (int i = 1; i <= 5; i++)
        {
            _queue.Add($"note {i}");
        }

        for (int i = 0; i < 5; i++)
        {
            GapEnds();
        }

        Assert.Equal("2 more notifications were not shown.", _shown[^1]);
        Assert.Empty(_waits);

        _queue.Add("later");

        Assert.Equal("later", _shown[^1]);

        // Another flood: its count is its own, and does not include the two from before.
        for (int i = 1; i <= 4; i++)
        {
            _queue.Add($"again {i}");
        }

        GapEnds();
        GapEnds();
        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["again 1", "again 2", "again 3", "1 more notification was not shown."], _shown.TakeLast(4));
    }

    [Fact]
    public void A_note_that_comes_while_the_summary_is_on_screen_waits_its_turn_and_is_not_counted()
    {
        _queue.Add("first");
        for (int i = 1; i <= 4; i++)
        {
            _queue.Add($"note {i}");
        }

        GapEnds();
        GapEnds();
        GapEnds();
        GapEnds();
        Assert.Equal("1 more notification was not shown.", _shown[^1]);

        _queue.Add("after the summary");
        GapEnds();

        Assert.Equal(["first", "note 1", "note 2", "note 3", "1 more notification was not shown.", "after the summary"], _shown);
    }

    [Fact]
    public void A_note_that_is_the_same_as_one_on_screen_or_waiting_is_dropped_and_not_counted()
    {
        _queue.Add("first");
        _queue.Add("a");
        _queue.Add("b");
        _queue.Add("c");

        // The queue is full. These repeat what is showing or waiting: they tell the owner nothing new.
        _queue.Add("first");
        _queue.Add("a");
        _queue.Add("c");
        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["first", "a", "b", "c"], _shown);

        GapEnds();

        Assert.Equal(4, _shown.Count);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_note_that_reports_an_event_is_counted_when_the_queue_is_full_even_if_it_is_a_repeat()
    {
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);

        for (int i = 0; i < 4; i++)
        {
            GapEnds();
        }

        Assert.Equal(["Done", "Done", "Done", "Done", "3 more notifications were not shown."], _shown);
    }

    [Fact]
    public void Ten_quick_runs_give_four_notes_and_one_summary_and_then_the_queue_is_quiet()
    {
        for (int i = 1; i <= 10; i++)
        {
            _queue.Add($"Run {i} done", evenIfRepeated: true);
        }

        for (int i = 0; i < 5; i++)
        {
            GapEnds();
        }

        Assert.Equal(
            ["Run 1 done", "Run 2 done", "Run 3 done", "Run 4 done", "6 more notifications were not shown."],
            _shown);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_show_that_throws_for_the_summary_does_not_stop_the_queue()
    {
        var shown = new List<string>();
        var waits = new List<Action>();
        var queue = new NoteQueue(
            note =>
            {
                shown.Add(note);
                if (note.EndsWith("not shown.", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("the tray is broken");
                }
            },
            (_, action) => waits.Add(action));

        queue.Add("first");
        for (int i = 1; i <= 5; i++)
        {
            queue.Add($"note {i}");
        }

        for (int i = 0; i < 4; i++)
        {
            waits[i]();
        }

        Assert.Equal("2 more notifications were not shown.", shown[^1]);

        waits[4]();
        queue.Add("next");

        Assert.Equal("next", shown[^1]);
    }
}
