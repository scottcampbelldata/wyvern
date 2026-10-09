namespace Beacon.Core.Platform;

/// <summary>Puts text into the window the owner was working in before the launcher opened.</summary>
public interface IPasteService
{
    /// <summary>Pastes the text there. The text is the owner's: it is never logged or shown in a notification.</summary>
    void Paste(string text);

    /// <summary>
    /// Puts these blocks on the clipboard, pastes there, and leaves them on the clipboard. This
    /// is for an entry of the clipboard history, which the owner may want to paste again.
    /// </summary>
    void PasteAndKeep(IReadOnlyList<ClipboardBlock> blocks);

    /// <summary>
    /// Puts blocks on the clipboard and leaves them there, with no paste. A paste service that
    /// was not made for it refuses.
    /// </summary>
    void Keep(IReadOnlyList<ClipboardBlock> blocks) => throw new NotSupportedException();
}

/// <summary>
/// A window to paste into, and the key press that pastes into it. Implemented with Windows
/// calls in the app; everything that decides what happens, and in what order, is in
/// <see cref="PasteService"/>. The launcher's target is the window that had the focus when the
/// launcher was asked to show. Auto-expand makes a target of its own for each keyword that was
/// typed: the window it was typed in, which is in front already.
/// </summary>
public interface IPasteTarget
{
    /// <summary>Gives the focus back to that window, when it does not have it.</summary>
    /// <returns>False when there is no such window, it has gone, or Windows will not switch to it.</returns>
    bool Activate();

    /// <summary>Presses Ctrl+V for whichever window has the focus.</summary>
    /// <exception cref="PasteCalledOffException">The paste is not to be made after all. Nothing was pressed.</exception>
    void SendPaste();
}

/// <summary>
/// Thrown by <see cref="IPasteTarget.SendPaste"/> to say that the paste is not to be made after
/// all. That is nobody's failure, and nothing was pressed: the paste service puts the clipboard
/// back at once and says nothing. Auto-expand calls a paste off when the owner has typed on, or
/// has gone to another window, since the keyword.
/// </summary>
public sealed class PasteCalledOffException : Exception
{
    public PasteCalledOffException()
        : base("The paste was called off before any key was pressed.")
    {
    }
}

/// <summary>
/// Pastes by way of the clipboard, in one of three ways. <see cref="Paste"/>, for a text, puts
/// the clipboard back as it was afterwards. Its steps: remember the text on the clipboard; put
/// the new text there; give the focus back to the window the owner was in; after a moment,
/// press Ctrl+V; after a longer moment, put the remembered text back.
/// <see cref="PasteInPlace"/>, for a keyword the owner has just typed, is the same sequence
/// with a window of its own (the one the keyword was typed in, which is in front already) and
/// one more step: a look at the clipboard just before the keys are pressed.
/// <see cref="PasteAndKeep"/>, for an entry of the clipboard history, puts nothing back. Its
/// steps: put the entry's blocks on the clipboard; give the focus back; after a moment, press
/// Ctrl+V. The entry stays on the clipboard, and a paste of another kind that was still
/// waiting to put its text back no longer does.
/// </summary>
/// <remarks>
/// Use from one thread only, the UI thread, and have <c>later</c> run its action on that same
/// thread. Nothing here waits or blocks. The steps that run from <c>later</c> never throw: a
/// timer has nobody above it to catch anything, so every failure there goes to <c>report</c>.
/// There is one of these for the whole app, so that a paste always knows of the one before it:
/// whichever way a text is pasted, the clipboard goes back to what the owner had on it.
/// </remarks>
public sealed class PasteService : IPasteService
{
    /// <summary>
    /// How long the window that gets the focus back is given before Ctrl+V is pressed. Pressed
    /// any sooner, the key can arrive while the launcher is still the window in front.
    /// </summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a paste in place leaves between putting the text on the clipboard and pressing
    /// the keys. No focus is moved for it, but the programs that watch the clipboard open it
    /// as soon as it changes, Beacon's own history among them, and a program that is asked to
    /// paste while another has the clipboard open pastes nothing. Used by
    /// <see cref="PasteInPlace"/> only, so that it can be changed without touching a paste
    /// from the launcher.
    /// </summary>
    public static readonly TimeSpan InPlaceDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long after the key press the earlier text is put back. The program being pasted into
    /// reads the clipboard when it gets round to the key press, so putting the earlier text
    /// back at once would make it paste that instead.
    /// </summary>
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Shown when there is no window to paste into.</summary>
    public const string NoWindowNote =
        "There was no window to paste into. The text is on the clipboard: press Ctrl+V where you want it.";

    /// <summary>Shown when the key press could not be sent.</summary>
    public const string NotPastedNote =
        "The text could not be pasted. It is on the clipboard: press Ctrl+V where you want it. Details are in the log file.";

    /// <summary>Shown when there is no window to paste an entry of the clipboard history into.</summary>
    public const string NoWindowKeptNote =
        "There was no window to paste into. The entry is on the clipboard: press Ctrl+V where you want it.";

    /// <summary>Shown when the key press for an entry of the clipboard history could not be sent.</summary>
    public const string NotPastedKeptNote =
        "The entry could not be pasted. It is on the clipboard: press Ctrl+V where you want it. Details are in the log file.";

    private readonly IClipboard _clipboard;
    private readonly IPasteTarget _target;
    private readonly Action<TimeSpan, Action> _later;
    private readonly Action<string> _notify;
    private readonly Action<string, Exception> _report;
    private Waiting? _waiting;

    /// <param name="target">The window the launcher pastes into: the one that had the focus when it was asked to show.</param>
    /// <param name="later">Runs the action once, after the given time, on the thread this service is used from.</param>
    /// <param name="notify">Shows one of this class's fixed sentences to the owner.</param>
    /// <param name="report">Receives fixed text and an exception for the log.</param>
    public PasteService(
        IClipboard clipboard,
        IPasteTarget target,
        Action<TimeSpan, Action> later,
        Action<string> notify,
        Action<string, Exception>? report = null)
    {
        _clipboard = clipboard;
        _target = target;
        _later = later;
        _notify = notify;
        _report = report ?? ((_, _) => { });
    }

    /// <summary>
    /// Starts the paste and returns at once; the key press and the putting back follow by way
    /// of <c>later</c>. If the clipboard held something other than text, it is left holding the
    /// pasted text. If there is no window to paste into, the text stays on the clipboard and
    /// the owner is told. An empty text is not pasted: nothing is touched.
    /// </summary>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened. Nothing has been pasted.</exception>
    public void Paste(string text) => Start(text, _target, SettleDelay);

    /// <summary>
    /// Pastes the text into the window the owner is typing in, in place of a keyword that was
    /// just typed there. It is the same sequence as <see cref="Paste"/>: the text is put on the
    /// clipboard marked, the keys are pressed a moment later (<see cref="InPlaceDelay"/>), and
    /// what the owner had on the clipboard is put back afterwards. Two things differ. The
    /// window is given for this one paste. And the clipboard is looked at once more just before
    /// the keys, which a paste from the launcher does not do. The target's key press also takes
    /// the keyword away, so the keyword is only ever taken away together with the paste. The
    /// target can call the paste off at that moment by throwing
    /// <see cref="PasteCalledOffException"/>: then no key is pressed, the clipboard is put back
    /// at once, and the owner is told nothing. So it is when the look finds that the clipboard
    /// no longer holds the text put there, or cannot read it; a clipboard that could not be
    /// read also leaves one line in the log, with the exception, which no other reason for a
    /// call-off does.
    /// Any other failure of the key press leaves the text on the clipboard and tells the owner,
    /// as for any paste. An empty text is not pasted: nothing is touched.
    /// </summary>
    /// <param name="target">The window the keyword was typed in. Its <see cref="IPasteTarget.Activate"/> moves no focus.</param>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened. Nothing has been pasted and no key will be pressed.</exception>
    public void PasteInPlace(IPasteTarget target, string text) => Start(text, target, InPlaceDelay, inPlace: true);

    /// <summary>
    /// The text the owner has on the clipboard, for a snippet's {clipboard}. Usually that is
    /// what the clipboard holds. But from the moment a paste has put its text on the clipboard
    /// until it has put the earlier text back, the clipboard holds what is being pasted, and
    /// the owner's own text is the one this service remembers: that one is the answer then.
    /// Two keywords typed one straight after the other would otherwise give the second snippet
    /// the first snippet as its {clipboard}. Nothing is changed by asking.
    /// </summary>
    /// <returns>Null when the owner has no text on the clipboard.</returns>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened.</exception>
    public string? OwnerText() => OwnersOf(_clipboard.GetText());

    /// <summary>
    /// Starts the paste and returns at once; the key press follows by way of <c>later</c>.
    /// Nothing is put back afterwards: the blocks stay on the clipboard, and an earlier paste
    /// that was still waiting to put its text back no longer does. If there is no window to
    /// paste into, the blocks stay on the clipboard and the owner is told. With no blocks
    /// nothing is touched.
    /// </summary>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened. Nothing has been pasted.</exception>
    /// <exception cref="ArgumentException">
    /// None of the blocks holds a byte, which the clipboard refuses (see
    /// <see cref="IClipboard.SetBlocks"/>). Nothing has been pasted, the clipboard has not been
    /// touched, and an earlier paste still puts its text back.
    /// </exception>
    public void PasteAndKeep(IReadOnlyList<ClipboardBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        _clipboard.SetBlocks(blocks);

        // From here an earlier paste must not put anything back: the clipboard now holds what
        // the owner has just chosen to have on it.
        _waiting = null;
        if (!Activate(_target))
        {
            Notify(NoWindowKeptNote);
            return;
        }

        var mine = _waiting = new Waiting(_target, original: null, pasted: "", keep: true);
        _later(SettleDelay, () => PressPaste(mine));
    }

    /// <summary>
    /// Puts blocks on the clipboard and leaves them there, with no paste: for an entry of the
    /// clipboard history that the owner wants on the clipboard again. An earlier paste that was
    /// still waiting to put its text back no longer does, because the clipboard now holds
    /// what the owner has just chosen to have on it. With no blocks nothing is touched.
    /// </summary>
    /// <exception cref="ClipboardBusyException">The clipboard could not be opened. Nothing has changed.</exception>
    /// <exception cref="ArgumentException">None of the blocks holds a byte, which the clipboard refuses. Nothing has changed.</exception>
    public void Keep(IReadOnlyList<ClipboardBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        _clipboard.SetBlocks(blocks);
        _waiting = null;
    }

    // A paste of a text, into the launcher's window or into the window that is being typed in.
    // The moment is how long is left before the keys are pressed. A paste in place looks at the
    // clipboard once more before they are.
    private void Start(string text, IPasteTarget target, TimeSpan moment, bool inPlace = false)
    {
        // Pasting nothing would still take over the clipboard and press a key for no reason,
        // and an empty text cannot be told apart from a clipboard that holds none.
        if (text.Length == 0)
        {
            return;
        }

        string? original = OwnersOf(_clipboard.GetText());
        _clipboard.SetText(text);

        // From here the earlier paste must not put anything back: this one has taken over.
        _waiting = null;
        if (!Activate(target))
        {
            Notify(NoWindowNote);
            return;
        }

        var mine = _waiting = new Waiting(target, original, text, inPlace: inPlace);
        _later(moment, () => PressPaste(mine));
    }

    // What the owner has on the clipboard, given what the clipboard holds now. While an earlier
    // paste is still waiting to put its text back, and the clipboard still holds what that
    // paste put there, what it remembered is the owner's own text. Had the owner copied
    // something since, the clipboard would hold that instead, which is newer, and that is the
    // answer. This is the one rule for a paste that follows another and for {clipboard}.
    private string? OwnersOf(string? onClipboard) =>
        _waiting is { } earlier && onClipboard == earlier.Pasted ? earlier.Original : onClipboard;

    /// <summary>A target that fails is the same to the owner as one with no window.</summary>
    private bool Activate(IPasteTarget target)
    {
        try
        {
            return target.Activate();
        }
        catch (Exception ex)
        {
            Report("The window to paste into could not be reached", ex);
            return false;
        }
    }

    private void PressPaste(Waiting mine)
    {
        if (!ReferenceEquals(_waiting, mine))
        {
            return;
        }

        // This runs from a timer, with nobody above it to catch anything. Whatever goes wrong
        // here, the paste is over and the next one starts from the clipboard as it is.
        try
        {
            // A paste in place looks at the clipboard once more before the keys. The keys take
            // the keyword away and then paste, so if anything else has put something on the
            // clipboard in the moment since this paste did (a clipboard tool, a password
            // manager clearing it, another device's clipboard arriving), they would take the
            // keyword away and paste that in its place. When the clipboard does not hold
            // exactly what was pasted, or cannot be read, the paste is called off as the target
            // would call it off: no key is pressed, the clipboard is put back at once (which
            // leaves another program's text alone), and the owner is told nothing. A clipboard
            // that could not be read leaves one line in the log (see HoldsWhatWasPasted); the
            // other cases leave none. The look is made before the target is asked for the keys,
            // so that it never comes between the target's own last looks and its key press.
            // Two kinds of text do not read back from the clipboard as they were written, and
            // for that reason are never pasted in place: one with a zero character in it, and
            // one with half of a surrogate pair in it (written as a replacement character). For
            // such a text, every time, nothing is pasted and the clipboard keeps the snippet:
            // the putting back compares what it reads with the text pasted, as the look did,
            // and finds no match either, so the owner's earlier text is not put back. A paste
            // from the launcher makes no such look.
            if (mine.InPlace && !HoldsWhatWasPasted(mine))
            {
                PutBack(mine);
                return;
            }

            try
            {
                mine.Target.SendPaste();
            }
            catch (PasteCalledOffException)
            {
                // The target has called the paste off, and nothing was pressed. The clipboard
                // goes back to what the owner had on it, now, and nothing is said.
                PutBack(mine);
                return;
            }
            catch (Exception ex)
            {
                // Nothing was pasted, so the text is left where the owner can still get at it.
                _waiting = null;
                Report("The paste key press could not be sent", ex);
                Notify(mine.Keep ? NotPastedKeptNote : NotPastedNote);
                return;
            }

            if (mine.Keep)
            {
                // Nothing is put back, so the paste is over. A paste that follows finds the
                // clipboard as it is, holding what was just pasted.
                _waiting = null;
                return;
            }

            _later(RestoreDelay, () => PutBack(mine));
        }
        catch (Exception ex)
        {
            _waiting = null;
            Report("The paste could not be finished", ex);
        }
    }

    // Whether the clipboard still holds what this paste put there. A clipboard that cannot be
    // read (another program has it open, or anything else is wrong with it) cannot be told, and
    // counts as not holding it. The paste is called off then, and this is the one place that
    // says why: the failure goes to the log, with the exception, and nowhere else, so that the
    // owner is not told. PutBack, which follows, reads the clipboard again only when this paste
    // remembered a text (it returns before it reads otherwise), and reports its own failure,
    // after this one, as it does for any paste. A clipboard that reads fine and holds another
    // text, or none, is not a failure and is not reported.
    private bool HoldsWhatWasPasted(Waiting mine)
    {
        try
        {
            return _clipboard.GetText() == mine.Pasted;
        }
        catch (Exception ex)
        {
            Report("The clipboard could not be read before the keys of a paste in place, so no key was pressed", ex);
            return false;
        }
    }

    private void PutBack(Waiting mine)
    {
        if (!ReferenceEquals(_waiting, mine))
        {
            return;
        }

        _waiting = null;
        if (mine.Original is null)
        {
            // The clipboard held a picture, files or nothing. That cannot be put back, so it
            // keeps what was pasted.
            return;
        }

        // This runs from a timer, with nobody above it to catch anything. The paste is already
        // marked as finished above, so a failure from here on leaves nothing stuck.
        try
        {
            // Only while the clipboard still holds what was pasted. Anything else was copied
            // by the owner since, and is newer than what was remembered.
            if (_clipboard.GetText() == mine.Pasted)
            {
                _clipboard.SetText(mine.Original);
            }
        }
        catch (Exception ex)
        {
            Report("The clipboard could not be put back after a paste", ex);
        }
    }

    // The notification and the log belong to the caller, and either can fail. That must not
    // turn a small failure of the paste into an exception out of a timer.
    private void Notify(string note)
    {
        try
        {
            _notify(note);
        }
        catch (Exception)
        {
            // Nowhere left to say so.
        }
    }

    private void Report(string message, Exception error)
    {
        try
        {
            _report(message, error);
        }
        catch (Exception)
        {
            // Nowhere left to say so.
        }
    }

    /// <summary>
    /// One paste that has not finished. <see cref="Target"/> is the window it goes to.
    /// <see cref="Original"/> is null when the clipboard held no text. <see cref="Pasted"/> is
    /// what that paste put on the clipboard. <see cref="Keep"/> is true for a paste that puts
    /// nothing back afterwards. <see cref="InPlace"/> is true for a paste in place, which looks
    /// at the clipboard once more before its keys are pressed.
    /// </summary>
    private sealed class Waiting(IPasteTarget target, string? original, string pasted, bool keep = false, bool inPlace = false)
    {
        public IPasteTarget Target { get; } = target;

        public string? Original { get; } = original;

        public string Pasted { get; } = pasted;

        public bool Keep { get; } = keep;

        public bool InPlace { get; } = inPlace;
    }
}
