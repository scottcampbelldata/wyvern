using Wyvern.Core.Platform;

namespace Wyvern.Tests;

/// <summary>The waits are a list the test works through by hand, so nothing here depends on time passing.</summary>
public class NoteQueueTests
{
    private readonly List<string> _shown = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly NoteQueue _queue;

    public NoteQueueTests()
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
    public void The_gap_is_six_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), NoteQueue.Gap);
    }

    [Fact]
    public void The_first_note_is_shown_at_once_and_starts_the_gap()
    {
        _queue.Add("settings note");

        Assert.Equal(["settings note"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);
    }

    [Fact]
    public void Further_notes_wait_their_turn_each_a_gap_after_the_one_before_it_and_in_the_order_they_came()
    {
        _queue.Add("first");
        _queue.Add("second");
        _queue.Add("third");

        Assert.Equal(["first"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);

        GapEnds();

        Assert.Equal(["first", "second"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);

        GapEnds();

        Assert.Equal(["first", "second", "third"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);
    }

    [Fact]
    public void A_note_that_comes_while_others_are_waiting_goes_to_the_back()
    {
        _queue.Add("first");
        _queue.Add("second");
        GapEnds();
        _queue.Add("third");

        Assert.Equal(["first", "second"], _shown);

        GapEnds();

        Assert.Equal(["first", "second", "third"], _shown);
    }

    [Fact]
    public void When_nothing_is_waiting_the_gap_ends_quietly_and_the_next_note_is_shown_at_once()
    {
        _queue.Add("first");
        GapEnds();

        Assert.Empty(_waits);

        _queue.Add("second");

        Assert.Equal(["first", "second"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);
    }

    [Fact]
    public void The_note_that_is_being_shown_is_not_added_again()
    {
        _queue.Add("same");
        _queue.Add("same");
        _queue.Add("same");

        GapEnds();

        Assert.Equal(["same"], _shown);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_note_that_is_already_waiting_is_not_added_again_wherever_it_waits()
    {
        _queue.Add("first");
        _queue.Add("second");
        _queue.Add("third");
        _queue.Add("second");
        _queue.Add("third");
        _queue.Add("first");

        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["first", "second", "third"], _shown);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_note_that_was_shown_earlier_and_has_gone_is_shown_again_when_it_comes_again()
    {
        _queue.Add("hotkey");
        _queue.Add("other");
        GapEnds();
        GapEnds();

        _queue.Add("hotkey");

        Assert.Equal(["hotkey", "other", "hotkey"], _shown);
    }

    [Fact]
    public void A_show_that_throws_does_not_stop_the_queue()
    {
        var shown = new List<string>();
        var waits = new List<Action>();
        var queue = new NoteQueue(
            note =>
            {
                shown.Add(note);
                throw new InvalidOperationException("the tray is broken");
            },
            (_, action) => waits.Add(action));

        queue.Add("first");
        queue.Add("second");
        queue.Add("third");
        waits[0]();
        waits[1]();

        Assert.Equal(["first", "second", "third"], shown);
    }

    [Fact]
    public void A_wait_that_cannot_be_started_does_not_hold_the_notes_back_or_lose_them()
    {
        var shown = new List<string>();
        var queue = new NoteQueue(shown.Add, (_, _) => throw new InvalidOperationException("no timer"));

        queue.Add("first");
        queue.Add("second");
        queue.Add("third");

        Assert.Equal(["first", "second", "third"], shown);
    }
}
