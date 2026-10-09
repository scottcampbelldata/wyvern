using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Notes that report something that happened, such as a script ending. Two of them can have
/// the same words and still be two things the owner needs to be told. The waits are a list the
/// test works through by hand, as in <see cref="NoteQueueTests"/>.
/// </summary>
public class NoteQueueRepeatTests
{
    private readonly List<string> _shown = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly NoteQueue _queue;

    public NoteQueueRepeatTests()
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
    public void A_note_that_reports_an_event_is_shown_again_although_the_same_words_are_on_screen()
    {
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);

        Assert.Equal(["Done"], _shown);

        GapEnds();

        Assert.Equal(["Done", "Done"], _shown);
    }

    [Fact]
    public void Several_with_the_same_words_all_wait_their_turn_and_none_is_lost()
    {
        _queue.Add("first");
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done", evenIfRepeated: true);

        GapEnds();
        GapEnds();
        GapEnds();

        Assert.Equal(["first", "Done", "Done", "Done"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);
    }

    [Fact]
    public void A_note_about_how_things_stand_is_still_not_repeated_even_after_one_that_reports_an_event()
    {
        _queue.Add("Done", evenIfRepeated: true);
        _queue.Add("Done");
        _queue.Add("bad files");
        _queue.Add("bad files");

        GapEnds();
        GapEnds();

        Assert.Equal(["Done", "bad files"], _shown);
    }

    [Fact]
    public void With_nothing_on_screen_it_is_shown_at_once_like_any_other_note()
    {
        _queue.Add("Done", evenIfRepeated: true);

        Assert.Equal(["Done"], _shown);
        Assert.Equal(NoteQueue.Gap, Assert.Single(_waits).After);
    }
}
