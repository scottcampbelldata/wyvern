using Wyvern.Core.Library;
using Wyvern.Core.Platform;

namespace Wyvern.Core.AutoExpand;

/// <summary>
/// Snippet auto-expand. While it is switched on, a keyboard hook tells this class of every key
/// that is pressed; it keeps the last few characters, and when they end with a snippet's
/// keyword it takes the keyword away and pastes the snippet in its place.
/// </summary>
/// <remarks>
/// <para><b>What is kept.</b> The characters are in a <see cref="TypingBuffer"/>: as many as the
/// longest keyword has, never more than 64, in memory only. They are forgotten at every break
/// in the typing, when the hook is renewed, and when auto-expand is switched off. Nothing here
/// writes a character, a keyword or the name of a snippet anywhere: what goes to the log is
/// fixed words and counts, and nothing at all is logged for a key or for a keyword that was
/// replaced. With auto-expand off there is no hook, no thread for one, and this class is told
/// of no key. The one exception is a hook that could not be removed when that was asked for;
/// see below.</para>
/// <para><b>Two threads.</b> The hook has a thread of its own (see <see cref="IKeyboardHook"/>),
/// so that a key press in another program never waits for Wyvern's UI thread. This class has a
/// half for each. The UI half is the public members, the replacement and the renewal: it is
/// used from the thread that made the object, and <see cref="Configure"/>,
/// <see cref="UseLibrary"/>, <see cref="Dispose"/> and the replacement refuse any other. The
/// hook half is one <c>Listener</c> for each time the hook is installed: it holds the typed
/// characters and decides everything a key press decides, on the hook's thread only.</para>
/// <para><b>What crosses between them</b>, and nothing else does. Going in: the keywords, as
/// one <see cref="KeywordSet"/> that is never changed once it is made. The UI thread swaps it
/// into the listener, and the hook's thread picks it up at its next key; a listener that has
/// been closed holds none and looks at no key, which is how switching off goes in. Coming out:
/// a keyword that was typed, as an action handed to <c>post</c>, which gives it to the UI
/// thread without waiting for it. And the count of keys seen, which the hook's thread writes
/// and the UI thread reads when a replacement looks whether the owner has typed on. Two more
/// things cross, and both are safe. The mark that a failure inside the callback has been
/// handed over to be logged, so that it is said once for a run: it may be changed on either
/// thread, and is only ever changed as one whole value, with <see cref="Interlocked"/>. And
/// what a listener kept, which is the typed characters, the window they were typed in and
/// whether a dead key is waiting: it is the hook's thread's alone for as long as that thread
/// runs, and is handed to the UI thread once, to be cleared, after the hook has been removed
/// and its thread has ended. There is no lock: the hook's thread never waits for the UI
/// thread, whatever that is busy with.</para>
/// <para><b>The callback.</b> <c>Listener.KeyDown</c> is what the hook calls. Every key press
/// in every program waits for it, so it only looks at the key, changes the buffer and, for a
/// keyword, posts the replacement: no file, no log, no clipboard, no wait, and nothing can
/// leave it as an exception.</para>
/// <para><b>The replacement.</b> It runs on the UI thread, through the paste service: the
/// snippet goes on the clipboard, and a moment later (<see cref="PasteService.InPlaceDelay"/>)
/// the keys are pressed, the Backspaces that take the keyword away together with the Ctrl+V.
/// Right before the keys are pressed it is looked at once more. When the owner has pressed
/// another key since the keyword, when another window is in front, or when a modifier key is
/// held, the paste is called off: no key is pressed, the clipboard is put back, and the
/// keyword stays as it was typed. Nothing is said, because nothing was asked for. A UI thread
/// that is busy only makes the replacement later, and one that comes too late is called off in
/// the same way.</para>
/// <para><b>Renewing.</b> Windows removes a hook, without telling the program, when its
/// callback was once not answered in time. On a thread of its own, with a callback that waits
/// for nothing, that should not happen in ordinary use, but nothing would show it if it did.
/// So the hook is taken out and put in again by every Reload, and every
/// <see cref="RenewEvery"/> when no key was seen in the <see cref="QuietBeforeRenewing"/>
/// after that: a hook that has just delivered a key is plainly there, and a keyword that is
/// being typed is not interrupted. The log counts the times the hook could not be given back
/// to Windows when it was removed. That is all that is known then: Windows does not say
/// whether it had taken the hook away before.</para>
/// <para><b>A hook that could not be removed.</b> Removing a hook fails when its thread does
/// not end in time. That hook may then still be installed until Wyvern closes, and nothing
/// can look whether it is. Its listener was closed before the removal was tried, so every key
/// it still reports is dropped without being looked at, and at the first such key the hook's
/// own thread clears what the listener kept. These hooks are counted. While the count is
/// above zero the log does not say that no hook is installed: when auto-expand is switched
/// off it gives the count instead, and when it is switched on it gives the count after its
/// usual line. The owner is told, once for each such hook, when he switches auto-expand off: at
/// the first switching off at or after the failure, whether the removal that failed was in that
/// call or at an earlier Reload or renewal while it was on. When the setting asks for a hook, a
/// new one is installed all the same.</para>
/// </remarks>
public sealed class AutoExpander : IDisposable
{
    /// <summary>How often it is looked at whether the hook can be taken out and put in again.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long is then waited for a key. When one is seen the hook is plainly there and is
    /// left alone; when none is, nobody is typing and the hook is renewed.
    /// </summary>
    public static readonly TimeSpan QuietBeforeRenewing = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long is waited before looking again when a modifier key is still held as a keyword
    /// has been typed, which it is after a keyword that ends in a capital or in a sign that
    /// needs Shift.
    /// </summary>
    public static readonly TimeSpan ModifierWait = TimeSpan.FromMilliseconds(25);

    /// <summary>How many times that is waited for before the keyword is left as typed: one second in all.</summary>
    public const int MaxModifierWaits = 40;

    /// <summary>Shown when the setting is on and Windows would not install the hook.</summary>
    public const string NotSwitchedOnNote = "Snippet auto-expand could not be switched on. Details are in the log file.";

    /// <summary>Shown when a keyword was typed and replacing it failed. The keyword stays as typed.</summary>
    public const string NotReplacedNote = "A snippet keyword you typed could not be replaced. Details are in the log file.";

    /// <summary>
    /// Shown when the owner switched auto-expand off and a hook could not be removed, in that
    /// call or earlier, and he has not been told of it. It is off all the same: whatever that
    /// hook still reports is dropped.
    /// </summary>
    public const string NotRemovedNote = "Snippet auto-expand is switched off, but its keyboard hook could not be removed. Quit Wyvern and start it again to be sure that it is gone. Nothing you type is kept meanwhile.";

    /// <summary>What a member of the UI half says when it is called on another thread.</summary>
    public const string WrongThread = "Snippet auto-expand is set up, and replaces a keyword, on the thread it was made on only.";

    private readonly IKeyboardHook _hook;
    private readonly ITypingTarget _target;
    private readonly PasteService _paste;
    private readonly IClock _clock;
    private readonly Action<Action> _post;
    private readonly Action<TimeSpan, Action> _later;
    private readonly Action<string> _notify;
    private readonly Action<string, Exception?> _report;
    private readonly TimeZoneInfo? _zone;

    // The thread this object was made on: the UI thread.
    private readonly int _thread = Environment.CurrentManagedThreadId;

    // ---- Used on the UI thread only ----

    private KeywordSet _keywords = KeywordSet.Empty;

    // What settings.json says. The hook is wanted while this is true.
    private bool _on;

    // The hook thread's half for the hook that is installed, as far as Wyvern knows. Null
    // while no hook is installed.
    private Listener? _listener;

    // True from a hook that could not be installed until one could, or until the next Configure
    // that asks for a hook, so that the renewals say it once and a Reload says it again.
    private bool _installFailed;
    private bool _disposed;

    // Moved on by every Configure and by Dispose, so by every switching on or off. A renewal
    // that was waiting carries the number it was made under, and does nothing once the number
    // has moved on.
    private int _turn;

    // How many times Remove answered false for a hook this class had installed: the call that
    // gives the hook back to Windows failed, or there was none to give back. No more is known.
    // Windows does not say whether it had taken the hook away before, so this is not a count
    // of that, and 0 does not show that it never happened.
    private int _timesGone;

    // How many hooks could not be removed when that was asked for: Remove threw. Each may
    // still be installed until Wyvern closes, and nothing can look whether it is. While this
    // is above 0 the log does not say that no hook is installed.
    private int _notRemoved;

    // How many of those the owner has been told of, by NotRemovedNote. It moves up to
    // _notRemoved when he switches auto-expand off, and so a hook that could not be removed
    // while it was on is told of at the first switching off after it, once.
    private int _notRemovedTold;

    // ---- Used on any thread ----

    // 1 once a failure inside the callback has been handed over to be logged, so that it is
    // said once for a run. Set back to 0 when handing it over failed, so that a later failure
    // tries again. Only ever changed with Interlocked.
    private int _keyFailureTold;

    /// <param name="paste">
    /// The app's one paste service. The replacement goes through it, so that the clipboard is
    /// handled as for a snippet run from the launcher and two pastes know of each other.
    /// </param>
    /// <param name="post">
    /// Hands the action to the thread this object is made on and returns at once, without
    /// waiting for it to run. It is called on the hook's thread, from inside the callback.
    /// </param>
    /// <param name="later">Runs the action once, after the given time, on the thread this object is made on. Called on that thread only.</param>
    /// <param name="notify">Shows one of this class's fixed sentences to the owner.</param>
    /// <param name="report">Receives fixed words and counts for the log, with an exception when there is one.</param>
    /// <param name="zone">The time zone of {date} and {time}. Left out everywhere except in tests.</param>
    public AutoExpander(
        IKeyboardHook hook,
        ITypingTarget target,
        PasteService paste,
        IClock clock,
        Action<Action> post,
        Action<TimeSpan, Action> later,
        Action<string> notify,
        Action<string, Exception?> report,
        TimeZoneInfo? zone = null)
    {
        _hook = hook;
        _target = target;
        _paste = paste;
        _clock = clock;
        _post = post;
        _later = later;
        _notify = notify;
        _report = report;
        _zone = zone;
    }

    /// <summary>True while settings.json has auto-expand switched on. UI thread.</summary>
    public bool IsOn => _on;

    /// <summary>
    /// Call at startup and at every Reload, with what settings.json says and the library's
    /// snippets as they are now. Off: the hook is removed with its thread, everything kept is
    /// forgotten, and the log says that no hook is installed. On: the hook is installed, or
    /// taken out and put in again when it was there, and the log says how many keywords are
    /// watched for. This never throws for anything the hook does: a hook that cannot be
    /// installed is logged, the owner is told, and it is tried again every
    /// <see cref="RenewEvery"/>. A hook that cannot be removed is logged and counted. From
    /// then on the log does not say that no hook is installed, but how many could not be
    /// removed and may still be; and when auto-expand is switched off with such a hook that the
    /// owner has not been told of, he is told once (<see cref="NotRemovedNote"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on another thread than the one this object was made on.</exception>
    public void Configure(bool on, IReadOnlyList<Snippet> snippets)
    {
        OnOwnThread();
        if (_disposed)
        {
            return;
        }

        int turn = ++_turn;
        try
        {
            RemoveHook();
            _on = on;
            if (!on)
            {
                _keywords = KeywordSet.Empty;
                if (_notRemoved > 0)
                {
                    // A hook could not be removed, in this call or in an earlier one, and may
                    // still be installed. The line below this block would say what is not
                    // known, so this one is written in its place, every time.
                    Report($"Snippet auto-expand is off. Keyboard hooks that could not be removed when asked, and may still be installed until Wyvern closes: {_notRemoved}. A key they report is dropped and nothing of it is kept. Keyboard hooks installed by this run of Wyvern: {_hook.Installs}, threads started for them: {_hook.Threads}");
                    if (_notRemoved > _notRemovedTold)
                    {
                        // The owner has just switched it off, and a hook stayed that he has
                        // not been told of: the one that failed to go in this call, or one
                        // that failed at a Reload or a renewal while it was on, when he was
                        // told nothing because the hook was wanted. He is told once for each,
                        // here, and not again at the next switching off.
                        _notRemovedTold = _notRemoved;
                        Notify(NotRemovedNote);
                    }

                    return;
                }

                Report($"Snippet auto-expand is off: no keyboard hook is installed and no thread is running for one. Keyboard hooks installed by this run of Wyvern: {_hook.Installs}, threads started for them: {_hook.Threads}");
                return;
            }

            _keywords = new KeywordSet(snippets);

            // A Reload, or switching on again, that cannot install the hook is logged again:
            // the notification below says that details are in the log file. Only the retries
            // every RenewEvery stay quiet after the first line.
            _installFailed = false;
            if (InstallHook())
            {
                Report($"Snippet auto-expand is on: a keyboard hook is installed, on a thread of its own. Keywords: {_keywords.Count}, left out: {_keywords.LeftOut}");
                if (_notRemoved > 0)
                {
                    // The hook is wanted now, so the owner is not told: this line is enough.
                    Report($"Earlier keyboard hooks that could not be removed when asked, and may still be installed until Wyvern closes: {_notRemoved}");
                }
            }
            else
            {
                Notify(NotSwitchedOnNote);
            }

            Schedule(RenewEvery, () => Renew(turn));
        }
        catch (Exception ex)
        {
            Report("Snippet auto-expand could not be set up", ex);
        }
    }

    /// <summary>
    /// Call whenever the library has been loaded again: the keywords are those of these
    /// snippets from now on. They are swapped in, and the hook's thread uses them from its
    /// next key, so a keyword that is half typed can still be finished, against the new
    /// keywords. While auto-expand is off this does nothing, and no keyword is held.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on another thread than the one this object was made on.</exception>
    public void UseLibrary(IReadOnlyList<Snippet> snippets)
    {
        OnOwnThread();
        if (_disposed || !_on)
        {
            return;
        }

        var before = _keywords;
        _keywords = new KeywordSet(snippets);
        _listener?.Use(_keywords);
        if (_keywords.Count != before.Count || _keywords.LeftOut != before.LeftOut)
        {
            Report($"Snippet auto-expand keywords: {_keywords.Count}, left out: {_keywords.LeftOut}");
        }
    }

    /// <summary>
    /// For when Wyvern closes: the hook is removed, and everything kept is forgotten. When the
    /// removal went well, the hook's thread has ended when this returns. When the removal threw
    /// (the thread did not end in time), that is not so: nothing is thrown from here, the
    /// failure goes to the log, and the hook may still be installed with its thread still
    /// running. Whatever that hook reports is dropped, because the listener was closed before
    /// the removal was tried. Nothing is replaced afterwards either way. Call it before
    /// whatever <c>post</c>, <c>notify</c> and <c>report</c> lead to is closed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on another thread than the one this object was made on.</exception>
    public void Dispose()
    {
        OnOwnThread();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _turn++;
        _on = false;
        RemoveHook();
        _keywords = KeywordSet.Empty;
    }

    // Runs on the UI thread: from post, after the callback for the keyword's last character
    // has returned, and again from later while a modifier key is being waited for.
    private void Replace(Typed typed)
    {
        // Not inside the try: a post that ran this on the hook's thread is a mistake in the
        // wiring, and must not go on to the clipboard, the notification or the log from there.
        OnOwnThread();
        try
        {
            // Called off without a word when auto-expand was switched off since, when another
            // key was pressed since, or when another window is in front: the keyword stays as
            // it was typed. Nothing has been touched yet.
            if (!Stands(typed) || !_target.IsInFront(typed.Window))
            {
                return;
            }

            // The keyword's last character was typed with Shift, or another modifier key is
            // still down. It is let go of in a moment; pressed now, the keys would be other
            // shortcuts. Looked at again shortly, and given up after a second.
            if (_target.ModifierHeld())
            {
                if (typed.Waits++ < MaxModifierWaits)
                {
                    _later(ModifierWait, () => Replace(typed));
                }

                return;
            }

            // For {clipboard}, the text the owner has: while an earlier paste still waits to
            // put it back, the clipboard holds that paste's snippet instead.
            string text = SnippetExpander.ExpandNow(typed.Snippet.Body, _clock, _zone, _paste.OwnerText);

            // Reading the clipboard waits for the program that owns it, and the owner can have
            // typed on meanwhile. Nothing has been changed so far.
            if (text.Length == 0 || !Stands(typed))
            {
                return;
            }

            var keys = _target.Replacing(typed.Window, typed.Snippet.Keyword!.Length);
            _paste.PasteInPlace(new LastLook(this, typed, keys), text);
        }
        catch (Exception ex)
        {
            Report("A typed keyword could not be replaced by its snippet", ex);
            Notify(NotReplacedNote);
        }
    }

    // True while the keyword can still be replaced as far as this class knows: the hook that
    // saw the keyword is the one installed now, which no hook is once auto-expand is switched
    // off, and that hook has seen no key that can change a text since the keyword. The count
    // is read as the hook's thread last wrote it; nothing is waited for.
    private bool Stands(Typed typed) =>
        ReferenceEquals(typed.Listener, _listener) && typed.Keys == typed.Listener.Keys;

    // Runs from later, every RenewEvery for as long as this turn lasts.
    private void Renew(int turn)
    {
        if (turn != _turn)
        {
            return;
        }

        if (_listener is { } listener)
        {
            // A key seen in the next few seconds shows that the hook is there, and that the
            // owner is typing. Only a hook that stays quiet is renewed.
            long seen = listener.Keys;
            Schedule(QuietBeforeRenewing, () => RenewWhenQuiet(turn, listener, seen));
            return;
        }

        // A hook that could not be installed is tried again at once.
        Reinstall();
        Schedule(RenewEvery, () => Renew(turn));
    }

    // Runs from later, QuietBeforeRenewing after Renew.
    private void RenewWhenQuiet(int turn, Listener listener, long seen)
    {
        // Within one turn the hook is only ever renewed from here, so this listener is still
        // the one of the hook that is installed.
        if (turn != _turn)
        {
            return;
        }

        if (listener.Keys == seen)
        {
            Reinstall();
        }

        Schedule(RenewEvery, () => Renew(turn));
    }

    private void Reinstall()
    {
        try
        {
            bool wasFailing = _installFailed;

            // Whether or not Windows still had the hook, keys may have been missed: the new
            // hook starts with nothing kept.
            RemoveHook();
            if (InstallHook() && wasFailing)
            {
                Report("The keyboard hook for snippet auto-expand is installed now");
            }
        }
        catch (Exception ex)
        {
            Report("The keyboard hook for snippet auto-expand could not be renewed", ex);
        }
    }

    private void Schedule(TimeSpan wait, Action action)
    {
        try
        {
            _later(wait, action);
        }
        catch (Exception ex)
        {
            Report("The keyboard hook for snippet auto-expand will not be renewed until Reload", ex);
        }
    }

    private bool InstallHook()
    {
        // Each hook gets a listener of its own, with nothing kept. A thread that an earlier
        // hook left behind can then only ever reach the listener it was given.
        var listener = new Listener(this, _keywords);
        try
        {
            _hook.Install(listener.KeyDown);
            _listener = listener;
            _installFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            listener.Close();
            if (!_installFailed)
            {
                _installFailed = true;
                Report("The keyboard hook for snippet auto-expand could not be installed", ex);
            }

            return false;
        }
    }

    // A hook that could not be removed is counted in _notRemoved: Remove threw.
    private void RemoveHook()
    {
        if (_listener is not { } listener)
        {
            return;
        }

        // First, so that a key that is on its way is not looked at whatever happens below, and
        // so that a replacement that is waiting no longer stands.
        _listener = null;
        listener.Close();
        try
        {
            bool known = _hook.Remove();

            // Remove has returned, so the hook's thread has ended and what it kept is this
            // thread's to clear.
            listener.Forget();
            if (!known)
            {
                // All that is known: the hook could not be given back, or the hook object
                // had none to give back. Whether Windows had taken it away is not known.
                _timesGone++;
                Report($"The keyboard hook for snippet auto-expand could not be given back to Windows when it was removed. Windows may have taken it away before. Times in this run: {_timesGone}");
            }
        }
        catch (Exception ex)
        {
            // Remove threw: the hook may still be installed and its thread may still be
            // running. The hook object has let go of both, so it is not asked to remove them
            // again, and nothing can look whether they are still there: the hook is counted,
            // and from now on the log does not say that no hook is installed.
            //
            // That thread's listener is left to it untouched. It was closed above, before
            // Remove was called, so whatever that hook still reports is dropped: the listener
            // looks at no key, and at the first key it is told of, its own thread clears the
            // few characters it kept. When no key comes they stay in memory, reachable by
            // nothing, until Wyvern closes.
            //
            // Installing goes on as before: when the setting says on, Configure and the
            // renewal ask for a new hook, which gets a new listener and a new thread.
            _notRemoved++;
            Report("The keyboard hook for snippet auto-expand could not be removed", ex);
        }
    }

    private void OnOwnThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException(WrongThread);
        }
    }

    // Called on the hook's thread, from inside the callback: the log is not written from
    // there, so the failure is posted, once for this run. A failure that could not be posted
    // does not use that once up.
    private void TellOfKey(Exception error)
    {
        if (Interlocked.Exchange(ref _keyFailureTold, 1) != 0)
        {
            return;
        }

        try
        {
            _post(() => Report("A key press could not be looked at for snippet auto-expand", error));
        }
        catch (Exception)
        {
            // Nowhere left to say so, and nothing may leave the callback. Nothing has been
            // said either, so it does not count as said: the next failure tries again.
            Interlocked.Exchange(ref _keyFailureTold, 0);
        }
    }

    // The notification and the log belong to the caller, and either can fail. That must not
    // become an exception out of a timer or out of what Windows calls.
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

    private void Report(string message, Exception? error = null)
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

    // The hook thread's half: one for each time the hook is installed. KeyDown is what the
    // hook calls, on its own thread, and everything a key press decides is decided there with
    // what this object holds. The UI thread only swaps the keywords in (Use, Close), reads
    // the count of keys (Keys), and clears what is left once the hook's thread has ended
    // (Forget). A listener whose hook could not be removed, or could not be installed, is
    // never cleared by the UI thread, because its thread may still be running: closed, it
    // clears itself, on its own thread, at the next key it is told of.
    private sealed class Listener(AutoExpander owner, KeywordSet keywords)
    {
        // Hook thread only, until that thread has ended.
        private readonly TypingBuffer _buffer = new();

        // The keywords to look for, or null once this listener is closed. Written by the UI
        // thread and read by the hook's thread at every key, both through Volatile. A
        // KeywordSet is never changed once it is made, so no more than the reference crosses.
        private KeywordSet? _keywords = keywords;

        // The keywords the buffer was last made ready for. Hook thread only.
        private KeywordSet? _ready;

        // The window the last key was pressed in, and whether a dead key has been pressed
        // whose accent has not yet gone onto a character, in whichever window: a change of
        // window empties the buffer and leaves this as it is. Hook thread only.
        private nint _window;
        private bool _afterDeadKey;

        // Counts the keys seen that can change a text. Written by the hook's thread only, and
        // read by the UI thread, both through Volatile.
        private long _keys;

        /// <summary>How many keys that can change a text this hook has seen. Any thread; nothing is waited for.</summary>
        public long Keys => Volatile.Read(ref _keys);

        /// <summary>UI thread: these are the keywords from the next key on.</summary>
        public void Use(KeywordSet keywords) => Volatile.Write(ref _keywords, keywords);

        /// <summary>UI thread: no key is looked at from now on.</summary>
        public void Close() => Volatile.Write(ref _keywords, null);

        /// <summary>UI thread, and only once the hook's thread has ended: clears what that thread kept.</summary>
        public void Forget()
        {
            _buffer.Clear();
            _window = 0;
            _afterDeadKey = false;
        }

        // What the hook calls for every key that goes down, on the hook's thread. See the
        // remarks of AutoExpander for what it may and may not do.
        public void KeyDown(TypedKey key)
        {
            try
            {
                var keywords = Volatile.Read(ref _keywords);
                if (keywords is null)
                {
                    // A key that was on its way when the hook was removed, or one from a hook
                    // that could not be removed. It is not looked at. What this listener kept
                    // is cleared here, by the thread it belongs to; the UI thread clears it
                    // only for a hook whose thread has ended.
                    _buffer.Clear();
                    _afterDeadKey = false;
                    return;
                }

                if (!ReferenceEquals(keywords, _ready))
                {
                    // The library was loaded again since the last key. What was typed so far
                    // is kept, as far as the new keywords need it.
                    _ready = keywords;
                    _buffer.Use(keywords);
                }

                if (key.Window != _window)
                {
                    // Another window than the last key was pressed in: what was kept was typed
                    // there. This goes for any key, a Shift key by itself included, because it
                    // shows that the owner has been somewhere else in between. A dead key that
                    // is waiting for its character is not forgotten here: whether Windows
                    // carries a waiting accent from one window to the next is not known, so the
                    // next character typed, wherever, is still one that is not kept.
                    _window = key.Window;
                    _buffer.Clear();
                }

                var kind = key.Kind;
                if (key.DeadKey)
                {
                    // A dead key puts its accent on the next character that is typed, whatever
                    // else this key press is, so it is the fact that counts here and not the
                    // kind: a dead key pressed in a chord (with Ctrl or Alt held) is a break
                    // by its kind. The hook translates every key, a chord included, with Ctrl
                    // and Alt left out of the state it translates under, so a key that is a
                    // dead key by itself is reported as one also when it is pressed in a
                    // chord. What the hook cannot see is a dead key that exists only at the
                    // AltGr level of a layout: translated without Ctrl and Alt, that key is
                    // not a dead key, and it is not reported as one.
                    _afterDeadKey = true;
                }

                if (kind == KeyKind.Modifier)
                {
                    return;
                }

                long keys = _keys + 1;
                Volatile.Write(ref _keys, keys);
                if (_afterDeadKey && kind == KeyKind.Character && !key.DeadKey)
                {
                    // The character after a dead key makes one character with it, such as an e
                    // with an accent, which is not the character this key press says. It is
                    // not kept, and what was kept is forgotten. The dead key is used up by it,
                    // and by nothing else, a change of window included: a key that types no
                    // character (an arrow, Backspace, a key pressed with Ctrl held) is handled
                    // as it always is, and Windows may still put the accent on the character
                    // that follows.
                    // So a keyword may be missed after a dead key, but nothing is ever
                    // replaced that is not the keyword.
                    _afterDeadKey = false;
                    kind = KeyKind.Break;
                }

                switch (kind)
                {
                    case KeyKind.Character:
                        if (_buffer.Add(key.Character) is { } snippet)
                        {
                            // The buffer is empty again. The replacement runs on the UI thread,
                            // whenever that gets to it, and only if no other key has come by
                            // then. Handing it over waits for nothing.
                            var typed = new Typed(snippet, key.Window, keys, this);
                            owner._post(() => owner.Replace(typed));
                        }

                        break;

                    case KeyKind.Backspace:
                        _buffer.Backspace();
                        break;

                    default:
                        _buffer.Clear();
                        break;
                }
            }
            catch (Exception ex)
            {
                // Nothing may leave the callback: it is called by Windows.
                _buffer.Clear();
                owner.TellOfKey(ex);
            }
        }
    }

    // A keyword that has been typed and is to be replaced: its snippet, the window it was
    // typed in, the hook that saw it and how many keys that hook had seen at its last
    // character. Made on the hook's thread and not changed there afterwards; the UI thread
    // alone counts the waits. A class and not a record, so that nothing prints the snippet it
    // holds.
    private sealed class Typed(Snippet snippet, nint window, long keys, Listener listener)
    {
        public Snippet Snippet { get; } = snippet;

        public nint Window { get; } = window;

        public long Keys { get; } = keys;

        public Listener Listener { get; } = listener;

        /// <summary>How many times a modifier key has been waited for.</summary>
        public int Waits { get; set; }
    }

    // What the paste service is given as the window to paste into. It adds one last look to
    // the app's own target, at the moment the paste service is about to press the keys, which
    // is a moment after the snippet went on the clipboard. Only this class knows whether the
    // owner has typed on meanwhile.
    private sealed class LastLook(AutoExpander owner, Typed typed, IPasteTarget keys) : IPasteTarget
    {
        // No focus is moved and nothing is decided yet: the look that counts is the one below.
        public bool Activate() => true;

        public void SendPaste()
        {
            // The Backspaces would now go to another window, or be other shortcuts, or take
            // away something else than the keyword. Called off: the paste service puts the
            // clipboard back and presses nothing. The count of keys is looked at last, after
            // the two questions to Windows, so that a key that arrives while they are being
            // answered is seen as well.
            if (!owner._target.IsInFront(typed.Window) || owner._target.ModifierHeld() || !owner.Stands(typed))
            {
                throw new PasteCalledOffException();
            }

            keys.SendPaste();
        }
    }
}
