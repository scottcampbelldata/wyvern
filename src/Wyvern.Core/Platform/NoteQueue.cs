using System.Globalization;

namespace Wyvern.Core.Platform;

/// <summary>
/// Shows notifications to the owner one at a time, a few seconds apart. The first is shown at
/// once; each of the others is shown a <see cref="Gap"/> after the one before it was shown.
/// A note which is the same as the one on screen, or the same as one already waiting, is not
/// added a second time, unless it reports something that happened rather than how things
/// stand. At most <see cref="MaxWaiting"/> notes wait behind the one on screen; one that comes
/// when that many are waiting is not added but counted, and the count is told in one further
/// note after the ones that waited.
/// </summary>
/// <remarks>
/// A notification replaces the one before it, so two that came within a moment of each other
/// would show only the second: at startup the note about settings.json is followed at once by
/// the library's note about files that could not be loaded. Without a limit, ten quick runs of
/// a script would give a minute of stale notifications, and a failure that came after them
/// would wait behind them all. Use from one thread only, the UI thread, and have <c>later</c>
/// run its action on that same thread. Nothing here waits or blocks, and nothing here throws
/// out of <c>later</c>'s action.
/// </remarks>
public sealed class NoteQueue
{
    /// <summary>How long a note is left alone before the next one is shown. A balloon stays up for five seconds.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(6);

    /// <summary>How many notes wait behind the one on screen. A note that comes when this many are waiting is counted, not added.</summary>
    public const int MaxWaiting = 3;

    private readonly Action<string> _show;
    private readonly Action<TimeSpan, Action> _later;
    private readonly Queue<string> _waiting = new();

    // The note shown last, until the gap that follows it has ended. Null means nothing is held back.
    private string? _showing;

    // How many notes came when the queue was full and were not added. It is told, as one note,
    // when the notes that waited have all been shown, and starts again from nothing after that.
    private int _notShown;

    /// <param name="show">Shows one note to the owner.</param>
    /// <param name="later">Runs the action once, after the given time, on the thread this queue is used from.</param>
    public NoteQueue(Action<string> show, Action<TimeSpan, Action> later)
    {
        _show = show;
        _later = later;
    }

    /// <summary>Shows the note now, or when the ones before it have had their turn.</summary>
    /// <param name="evenIfRepeated">
    /// False for a note that says how things stand, such as which library files could not be
    /// used: saying it again while it is on screen or waiting tells the owner nothing new, so it
    /// is not added again. True for a note that reports something that happened, such as a
    /// script ending: two scripts that both end with "Done" are two things that happened, and
    /// each is shown, unless <see cref="MaxWaiting"/> notes are already waiting. Then it is
    /// counted.
    /// </param>
    public void Add(string note, bool evenIfRepeated = false)
    {
        if (_showing is null)
        {
            Show(note);
            return;
        }

        // A repeat is dropped before the limit is looked at: it says nothing new, so there is
        // nothing to count.
        if (!evenIfRepeated && (note == _showing || _waiting.Contains(note)))
        {
            return;
        }

        if (_waiting.Count >= MaxWaiting)
        {
            _notShown++;
            return;
        }

        _waiting.Enqueue(note);
    }

    // Fixed words and a number, as every note here must be.
    private static string NotShownNote(int count) =>
        count == 1
            ? "1 more notification was not shown."
            : $"{count.ToString(CultureInfo.InvariantCulture)} more notifications were not shown.";

    private void Show(string note)
    {
        _showing = note;
        try
        {
            _show(note);
        }
        catch (Exception)
        {
            // A notification is a courtesy, and this can run from a timer with nobody above it
            // to catch anything. A note that cannot be shown must not stop the ones after it.
        }

        try
        {
            _later(Gap, GapEnds);
        }
        catch (Exception)
        {
            // With no way to wait there is no gap to keep. Going on at once loses no note.
            GapEnds();
        }
    }

    private void GapEnds()
    {
        if (_waiting.TryDequeue(out string? next))
        {
            Show(next);
        }
        else if (_notShown > 0)
        {
            // With the count as it stands now, notes that came while the others were being shown included.
            int count = _notShown;
            _notShown = 0;
            Show(NotShownNote(count));
        }
        else
        {
            _showing = null;
        }
    }
}
