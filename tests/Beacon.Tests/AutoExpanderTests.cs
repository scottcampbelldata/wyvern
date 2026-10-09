using System.Reflection;
using Beacon.Core.AutoExpand;
using Beacon.Core.Library;
using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Snippet auto-expand from the key press to the clipboard being put back, with the real paste
/// service and fakes for everything Windows does. No test installs a keyboard hook, presses a
/// key, moves the focus or touches the real clipboard: a "key" here is a method call, and the
/// waits and the posted work are lists the test works through by hand.
/// </summary>
public class AutoExpanderTests
{
    private const nint Notepad = 100;
    private const nint Browser = 200;
    private const nint BeaconWindow = 900;
    private const string Earlier = "what I copied earlier";

    // Stands for a modifier key that is held for as long as the test goes on.
    private const int Throughout = 1_000_000;

    // Thirteen hours ahead of UTC. The fake clock says noon on 3 October in UTC, which is one
    // in the morning on 4 October there, so a date taken from UTC by mistake shows up.
    private static readonly TimeZoneInfo FarEast =
        TimeZoneInfo.CreateCustomTimeZone("Beacon test zone", TimeSpan.FromHours(13), "Beacon test zone", "Beacon test zone");

    // Stands for the hook that Windows calls. Nothing is installed anywhere: Install only
    // keeps the callback, and Press calls it as Windows would.
    private sealed class FakeHook(List<string> events) : IKeyboardHook
    {
        private Action<TypedKey>? _keyDown;
        private Action<TypedKey>? _last;

        public int Installs { get; private set; }

        public int Threads { get; private set; }

        /// <summary>Every callback a hook was installed with, oldest first.</summary>
        public List<Action<TypedKey>> Given { get; } = [];

        /// <summary>True while a hook is installed as far as the hook object itself knows. One that could not be removed is not counted.</summary>
        public bool Installed => _keyDown is not null;

        public Exception? InstallFails { get; set; }

        /// <summary>
        /// Set to have Remove throw, as the app's hook does when its thread does not end in
        /// time. Like that hook, this one has let go of what it could not remove by the time
        /// it throws, so a later Install is accepted and starts afresh. The hook that was left
        /// behind may still call its callback: a test calls it from <see cref="Given"/>.
        /// </summary>
        public Exception? RemoveFails { get; set; }

        /// <summary>Set when Windows has taken the hook away without saying so: no key arrives, and removing it fails.</summary>
        public bool TakenAway { get; set; }

        public void Install(Action<TypedKey> keyDown)
        {
            // As the app's hook does: the thread is started first, and ends again when
            // Windows refuses the hook.
            Threads++;
            if (InstallFails is not null)
            {
                throw InstallFails;
            }

            Assert.False(Installed, "a hook was installed while one was there");
            _keyDown = _last = keyDown;
            Given.Add(keyDown);
            Installs++;
            events.Add("hook installed");
        }

        public bool Remove()
        {
            if (RemoveFails is not null)
            {
                _keyDown = null;
                throw RemoveFails;
            }

            if (_keyDown is null)
            {
                return false;
            }

            _keyDown = null;
            events.Add("hook removed");
            bool known = !TakenAway;
            TakenAway = false;
            return known;
        }

        /// <summary>A key goes down. With no hook installed, or with <see cref="TakenAway"/> set (the call that gives the hook back will fail), nobody is told.</summary>
        public void Press(TypedKey key)
        {
            if (!TakenAway)
            {
                _keyDown?.Invoke(key);
            }
        }

        /// <summary>A key that was on its way when the hook was removed: the callback is called all the same.</summary>
        public void PressLate(TypedKey key) => _last?.Invoke(key);
    }

    // Stands for the window that is typed in. It presses nothing: it notes what it was asked.
    private sealed class FakeTyping(List<string> events) : ITypingTarget
    {
        public nint InFront { get; set; } = Notepad;

        /// <summary>For how many more questions a modifier key counts as held.</summary>
        public int ModifierHeldFor { get; set; }

        public int ModifierQuestions { get; private set; }

        public Exception? KeyPressFails { get; set; }

        /// <summary>
        /// Run once, inside the next question about the window in front and before it is
        /// answered: what happens elsewhere while Windows is being asked.
        /// </summary>
        public Action? DuringWindowQuestion { get; set; }

        /// <summary>The same for the next question about a modifier key.</summary>
        public Action? DuringModifierQuestion { get; set; }

        public bool IsInFront(nint window)
        {
            var during = DuringWindowQuestion;
            DuringWindowQuestion = null;
            during?.Invoke();
            return window == InFront;
        }

        public bool ModifierHeld()
        {
            ModifierQuestions++;
            var during = DuringModifierQuestion;
            DuringModifierQuestion = null;
            during?.Invoke();
            if (ModifierHeldFor > 0)
            {
                ModifierHeldFor--;
                return true;
            }

            return false;
        }

        public IPasteTarget Replacing(nint window, int erase) => new Target(this, events, window, erase);

        // As the app's own target does, it looks once more itself and refuses by throwing.
        private sealed class Target(FakeTyping owner, List<string> events, nint window, int erase) : IPasteTarget
        {
            public bool Activate() => window == owner.InFront;

            public void SendPaste()
            {
                if (owner.KeyPressFails is not null)
                {
                    throw owner.KeyPressFails;
                }

                if (window != owner.InFront)
                {
                    throw new InvalidOperationException("another window is in front");
                }

                events.Add($"{erase} Backspaces and Ctrl+V pressed in window {window}");
            }
        }
    }

    // The launcher's own window, which auto-expand must never paste into.
    private sealed class LauncherTarget : IPasteTarget
    {
        public bool Activate() => throw new InvalidOperationException("Auto-expand asked for the launcher's window.");

        public void SendPaste() => throw new InvalidOperationException("Auto-expand pasted into the launcher's window.");
    }

    private readonly List<string> _events = [];
    private readonly List<Action> _posted = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly List<string> _notes = [];
    private readonly List<(string Message, Exception? Error)> _reports = [];
    private readonly FakeClock _clock = new();
    private readonly FakeClipboard _clipboard;
    private readonly FakeHook _hook;
    private readonly FakeTyping _typing;
    private readonly PasteService _paste;
    private readonly AutoExpander _expander;

    public AutoExpanderTests()
    {
        _clipboard = new FakeClipboard(_events) { Text = Earlier };
        _hook = new FakeHook(_events);
        _typing = new FakeTyping(_events);
        _paste = new PasteService(
            _clipboard,
            new LauncherTarget(),
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (message, error) => _reports.Add((message, error)));
        _expander = new AutoExpander(
            _hook,
            _typing,
            _paste,
            _clock,
            _posted.Add,
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (message, error) => _reports.Add((message, error)),
            FarEast);
    }

    private static Snippet Snippet(string name, string? keyword, string body) =>
        new(name + ".md", @"C:\lib\snippets\" + name + ".md", name, keyword, body);

    private static readonly Snippet Signature = Snippet("signature", ";sig", "Regards, Walrus");

    private static TypedKey Letter(char character, nint window = Notepad) =>
        new(0x41, character, deadKey: false, chorded: false, injected: false, inBeacon: false, window);

    /// <summary>Auto-expand switched on with these snippets, and what that put in the lists forgotten.</summary>
    private void SwitchOn(params Snippet[] snippets)
    {
        _expander.Configure(on: true, snippets.Length == 0 ? [Signature] : snippets);
        _events.Clear();
        _reports.Clear();
    }

    private void Type(string text, nint window = Notepad)
    {
        foreach (char character in text)
        {
            _hook.Press(Letter(character, window));
        }
    }

    private void Press(int virtualKey, nint window = Notepad, bool chorded = false) =>
        _hook.Press(new TypedKey(virtualKey, (char)0, deadKey: false, chorded, injected: false, inBeacon: false, window));

    /// <summary>The key that types an acute accent onto the character that follows.</summary>
    private static TypedKey DeadKey(bool chorded = false, bool injected = false, nint window = Notepad) =>
        new(0xDE, (char)0xB4, deadKey: true, chorded, injected, inBeacon: false, window);

    /// <summary>
    /// Keys reported through the callback one hook was installed with, whatever has become of
    /// that hook since: removed, or left behind because it could not be removed.
    /// </summary>
    private static void TypeThrough(Action<TypedKey> callback, string text)
    {
        foreach (char character in text)
        {
            callback(Letter(character));
        }
    }

    // What the listener of one hook keeps. Nothing gives it out, so a test reaches for the
    // private field: the callback a hook was installed with is a method of its listener.
    private static object? KeptBy(Action<TypedKey> callback, string field)
    {
        object? listener = callback.Target;
        Assert.True(listener is not null, "the callback is not a method of an object");
        var found = listener.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(found is not null, $"the listener has no field {field}");
        return found.GetValue(listener);
    }

    /// <summary>The typed characters that hook's listener keeps.</summary>
    private static TypingBuffer BufferOf(Action<TypedKey> callback) =>
        Assert.IsType<TypingBuffer>(KeptBy(callback, "_buffer"));

    /// <summary>Whether that hook's listener is waiting for the character a dead key puts its accent on.</summary>
    private static bool AfterDeadKey(Action<TypedKey> callback) =>
        Assert.IsType<bool>(KeptBy(callback, "_afterDeadKey"));

    /// <summary>Runs what was posted, oldest first, including what that posts in turn.</summary>
    private void RunPosted()
    {
        while (_posted.Count > 0)
        {
            var action = _posted[0];
            _posted.RemoveAt(0);
            action();
        }
    }

    /// <summary>Lets the oldest wait of this length run out.</summary>
    private void WaitEnds(TimeSpan length)
    {
        int index = _waits.FindIndex(wait => wait.After == length);
        Assert.True(index >= 0, $"nothing is waiting for {length}");
        var wait = _waits[index];
        _waits.RemoveAt(index);
        wait.Action();
    }

    private int WaitsOf(TimeSpan length) => _waits.Count(wait => wait.After == length);

    /// <summary>A keyword typed, the posted work run, and the paste service's moment before the keys over.</summary>
    private void TypeAndLetSettle(string text, nint window = Notepad)
    {
        Type(text, window);
        RunPosted();
        WaitEnds(PasteService.InPlaceDelay);
    }

    private List<string> KeyPresses() => [.. _events.Where(entry => entry.Contains("Ctrl+V"))];

    // ---- Switching on and off ----

    [Fact]
    public void Switched_off_no_hook_is_installed_and_the_log_says_so_with_the_count()
    {
        _expander.Configure(on: false, [Signature]);

        Assert.False(_hook.Installed);
        Assert.Equal(0, _hook.Installs);
        Assert.Equal(0, _hook.Threads);
        Assert.False(_expander.IsOn);
        Assert.Equal(
            "Snippet auto-expand is off: no keyboard hook is installed and no thread is running for one. Keyboard hooks installed by this run of Beacon: 0, threads started for them: 0",
            Assert.Single(_reports).Message);
        Assert.Null(_reports[0].Error);
        Assert.Empty(_notes);
        Assert.Empty(_waits);
        Assert.DoesNotContain("hook installed", _events);
    }

    [Fact]
    public void Before_it_is_configured_nothing_is_installed()
    {
        Assert.False(_hook.Installed);
        Assert.False(_expander.IsOn);
        Assert.Empty(_events);
        Assert.Empty(_reports);
    }

    [Fact]
    public void Switched_on_the_hook_is_installed_and_the_log_says_how_many_keywords_there_are()
    {
        _expander.Configure(
            on: true,
            [Signature, Snippet("plain", null, "no keyword"), Snippet("address", ";addr", "1 Otter Way"), Snippet("twice", ";sig", "another"), Snippet("long", new string('k', 65), "too long")]);

        Assert.True(_hook.Installed);
        Assert.True(_expander.IsOn);
        Assert.Equal(1, _hook.Installs);
        Assert.Equal(
            "Snippet auto-expand is on: a keyboard hook is installed, on a thread of its own. Keywords: 2, left out: 2",
            Assert.Single(_reports).Message);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Switched_off_again_the_hook_is_removed_and_what_was_typed_does_nothing()
    {
        SwitchOn();
        Type(";si");

        _expander.Configure(on: false, [Signature]);

        Assert.False(_hook.Installed);
        Assert.Equal(["hook removed"], _events);
        Assert.Equal(
            "Snippet auto-expand is off: no keyboard hook is installed and no thread is running for one. Keyboard hooks installed by this run of Beacon: 1, threads started for them: 1",
            Assert.Single(_reports).Message);

        // A key that was on its way when the hook went is ignored.
        _hook.PressLate(Letter('g'));
        Assert.Empty(_posted);
    }

    [Fact]
    public void A_reload_while_it_is_on_takes_the_hook_out_and_puts_it_in_again_and_forgets_what_was_typed()
    {
        SwitchOn();
        Type(";si");

        _expander.Configure(on: true, [Signature]);

        Assert.Equal(["hook removed", "hook installed"], _events);
        Assert.Equal(2, _hook.Installs);
        Type("g");
        Assert.Empty(_posted);

        // The keyword typed whole after the reload is replaced as before.
        Type(";sig");
        Assert.Single(_posted);
    }

    [Fact]
    public void A_hook_that_cannot_be_installed_is_logged_and_the_owner_is_told()
    {
        var refused = new InvalidOperationException("made up");
        _hook.InstallFails = refused;

        _expander.Configure(on: true, [Signature]);

        Assert.False(_hook.Installed);
        var report = Assert.Single(_reports);
        Assert.Equal("The keyboard hook for snippet auto-expand could not be installed", report.Message);
        Assert.Same(refused, report.Error);
        Assert.Equal([AutoExpander.NotSwitchedOnNote], _notes);
    }

    [Fact]
    public void A_hook_that_could_not_be_installed_is_tried_again_and_the_log_says_when_it_is_there()
    {
        _hook.InstallFails = new InvalidOperationException("made up");
        _expander.Configure(on: true, [Signature]);
        _reports.Clear();

        // Still refused: nothing more is said.
        WaitEnds(AutoExpander.RenewEvery);
        Assert.Empty(_reports);
        Assert.False(_hook.Installed);

        _hook.InstallFails = null;
        WaitEnds(AutoExpander.RenewEvery);

        Assert.True(_hook.Installed);
        Assert.Equal("The keyboard hook for snippet auto-expand is installed now", Assert.Single(_reports).Message);
        Type(";sig");
        Assert.Single(_posted);
    }

    [Fact]
    public void A_hook_that_cannot_be_installed_is_logged_again_at_every_reload_and_not_again_by_the_renewals()
    {
        const string Line = "The keyboard hook for snippet auto-expand could not be installed";
        _hook.InstallFails = new InvalidOperationException("made up");

        _expander.Configure(on: true, [Signature]);
        Assert.Equal(Line, Assert.Single(_reports).Message);
        Assert.Equal([AutoExpander.NotSwitchedOnNote], _notes);

        // A Reload that fails again: the notification says that details are in the log, so
        // the log has a line of its own for this one too.
        _reports.Clear();
        _notes.Clear();
        _expander.Configure(on: true, [Signature]);
        Assert.Equal(Line, Assert.Single(_reports).Message);
        Assert.Equal([AutoExpander.NotSwitchedOnNote], _notes);

        // Off, and on again with the hook still refused: the line again, and the note.
        _reports.Clear();
        _notes.Clear();
        _expander.Configure(on: false, [Signature]);
        _expander.Configure(on: true, [Signature]);
        Assert.Equal(1, _reports.Count(report => report.Message == Line));
        Assert.Equal([AutoExpander.NotSwitchedOnNote], _notes);

        // The renewals try again every ten minutes and stay quiet. Of the three waits that
        // are pending, the first two belong to turns that have ended and do nothing. The next
        // two are renewals of this turn, and the hook is refused at both.
        _reports.Clear();
        _notes.Clear();
        WaitEnds(AutoExpander.RenewEvery);
        WaitEnds(AutoExpander.RenewEvery);
        WaitEnds(AutoExpander.RenewEvery);
        WaitEnds(AutoExpander.RenewEvery);
        Assert.Empty(_reports);
        Assert.Empty(_notes);
        Assert.False(_hook.Installed);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
    }

    [Fact]
    public void A_key_that_arrives_while_no_hook_is_installed_is_not_looked_at()
    {
        // The hook was taken out to be renewed and could not be put in again. A key that was
        // on its way through the old hook still reaches the callback. Beacon believes that no
        // hook is installed, and acts on no key while it believes so.
        SwitchOn();
        _hook.InstallFails = new InvalidOperationException("made up");
        RenewalComesRound();
        Assert.False(_hook.Installed);

        foreach (char character in ";sig")
        {
            _hook.PressLate(Letter(character));
        }

        Assert.Empty(_posted);
    }

    [Fact]
    public void Configure_never_throws_whatever_the_hook_does()
    {
        SwitchOn();
        _hook.InstallFails = new InvalidOperationException("made up");
        _hook.RemoveFails = new InvalidOperationException("made up");

        _expander.Configure(on: true, [Signature]);
        _expander.Configure(on: false, [Signature]);

        Assert.Contains(_reports, report => report.Message == "The keyboard hook for snippet auto-expand could not be removed");
        Assert.Contains(_reports, report => report.Message == "The keyboard hook for snippet auto-expand could not be installed");
    }

    // ---- A hook that could not be removed ----
    //
    // Removing can fail: the hook's thread did not end in time. That hook may then still be
    // installed until Beacon closes, and nothing can look whether it is. The log must not go
    // on to say that no hook is installed.

    [Fact]
    public void Switched_off_with_a_hook_that_cannot_be_removed_the_log_does_not_say_that_none_is_installed_and_the_owner_is_told_once()
    {
        SwitchOn();
        var refused = new TimeoutException("made up");
        _hook.RemoveFails = refused;

        _expander.Configure(on: false, [Signature]);

        Assert.False(_expander.IsOn);
        Assert.Equal(
            [
                "The keyboard hook for snippet auto-expand could not be removed",
                "Snippet auto-expand is off. Keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 1. A key they report is dropped and nothing of it is kept. Keyboard hooks installed by this run of Beacon: 1, threads started for them: 1",
            ],
            _reports.Select(report => report.Message));
        Assert.Same(refused, _reports[0].Error);
        Assert.Null(_reports[1].Error);
        Assert.DoesNotContain(_reports, report => report.Message.Contains("no keyboard hook is installed"));
        Assert.Equal(
            "Snippet auto-expand is switched off, but its keyboard hook could not be removed. Quit Beacon and start it again to be sure that it is gone. Nothing you type is kept meanwhile.",
            AutoExpander.NotRemovedNote);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);

        // Asked again, with nothing to remove this time. The hook that could not be removed is
        // not forgotten: the log gives the count again and still does not say that none is
        // installed. The owner has been told, and is not told a second time.
        _reports.Clear();
        _expander.Configure(on: false, []);

        Assert.Equal(
            "Snippet auto-expand is off. Keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 1. A key they report is dropped and nothing of it is kept. Keyboard hooks installed by this run of Beacon: 1, threads started for them: 1",
            Assert.Single(_reports).Message);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);
    }

    [Fact]
    public void A_reload_with_a_hook_that_cannot_be_removed_installs_a_new_one_says_so_in_the_log_and_drops_what_the_old_one_reports()
    {
        SwitchOn();
        _hook.RemoveFails = new TimeoutException("made up");

        _expander.Configure(on: true, [Signature]);

        // The setting says on, so a new hook is asked for all the same.
        Assert.Equal(["hook installed"], _events);
        Assert.True(_hook.Installed);
        Assert.Equal(2, _hook.Installs);
        Assert.Equal(2, _hook.Given.Count);
        Assert.Equal(
            [
                "The keyboard hook for snippet auto-expand could not be removed",
                "Snippet auto-expand is on: a keyboard hook is installed, on a thread of its own. Keywords: 1, left out: 0",
                "Earlier keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 1",
            ],
            _reports.Select(report => report.Message));

        // The hook is wanted, so the owner is not told anything: the log line is enough.
        Assert.Empty(_notes);

        // The hook that could not be removed may still be there. What it reports is dropped.
        TypeThrough(_hook.Given[0], ";sig");
        Assert.Empty(_posted);

        // And a keyword typed through the new one is replaced.
        TypeThrough(_hook.Given[1], ";sig");
        Assert.Single(_posted);
        RunPosted();
        WaitEnds(PasteService.InPlaceDelay);
        Assert.Equal(["4 Backspaces and Ctrl+V pressed in window 100"], KeyPresses());

        // A second one that cannot be removed is counted too.
        _reports.Clear();
        _expander.Configure(on: true, [Signature]);
        Assert.Equal(
            "Earlier keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 2",
            _reports[^1].Message);
        Assert.Equal(3, _hook.Given.Count);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_renewal_with_a_hook_that_cannot_be_removed_installs_a_new_one_tells_the_owner_nothing_then_and_tells_him_once_when_he_switches_off()
    {
        SwitchOn();
        _hook.RemoveFails = new TimeoutException("made up");

        RenewalComesRound();

        Assert.Equal(["hook installed"], _events);
        Assert.True(_hook.Installed);
        Assert.Equal(2, _hook.Installs);
        Assert.Equal(2, _hook.Given.Count);
        var report = Assert.Single(_reports);
        Assert.Equal("The keyboard hook for snippet auto-expand could not be removed", report.Message);
        Assert.IsType<TimeoutException>(report.Error);
        Assert.Empty(_notes);

        // What the old hook still reports is dropped, and the new one works.
        TypeThrough(_hook.Given[0], ";sig");
        Assert.Empty(_posted);
        TypeThrough(_hook.Given[1], ";sig");
        Assert.Single(_posted);

        // The renewals go on as before.
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
        Assert.Equal(0, WaitsOf(AutoExpander.QuietBeforeRenewing));

        // The hook that was left behind is not forgotten. The next Reload gives the count
        // after the ordinary line, and the owner is told nothing: the hook is wanted.
        _hook.RemoveFails = null;
        _reports.Clear();
        _expander.Configure(on: true, [Signature]);
        Assert.Equal(
            [
                "Snippet auto-expand is on: a keyboard hook is installed, on a thread of its own. Keywords: 1, left out: 0",
                "Earlier keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 1",
            ],
            _reports.Select(said => said.Message));
        Assert.Empty(_notes);

        // Switching off afterwards does not say that no hook is installed. The removal of the
        // new hook goes well, but the one that failed at the renewal has not been told of yet,
        // so the owner is told now, once.
        _reports.Clear();
        _expander.Configure(on: false, [Signature]);
        Assert.Equal(
            "Snippet auto-expand is off. Keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 1. A key they report is dropped and nothing of it is kept. Keyboard hooks installed by this run of Beacon: 3, threads started for them: 3",
            Assert.Single(_reports).Message);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);

        // Switched off again, with nothing to remove: that hook has been told of, so not again.
        _reports.Clear();
        _expander.Configure(on: false, []);
        Assert.Single(_reports);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);
    }

    [Fact]
    public void Every_removal_that_failed_is_told_of_once_at_the_first_switching_off_at_or_after_it()
    {
        SwitchOn();

        // Two Reloads while it is on, each leaving a hook that could not be removed. The
        // setting asks for a hook, so the owner is told nothing at either.
        _hook.RemoveFails = new TimeoutException("made up");
        _expander.Configure(on: true, [Signature]);
        _expander.Configure(on: true, [Signature]);
        Assert.Empty(_notes);

        // Switched off, and this removal goes well: both earlier failures are told of by one
        // note, and a second switching off says nothing more.
        _hook.RemoveFails = null;
        _expander.Configure(on: false, [Signature]);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);
        _expander.Configure(on: false, [Signature]);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);

        // Switched on again: nothing is said, and the count in the log is two.
        _reports.Clear();
        _expander.Configure(on: true, [Signature]);
        Assert.Equal(
            "Earlier keyboard hooks that could not be removed when asked, and may still be installed until Beacon closes: 2",
            _reports[^1].Message);
        Assert.Equal([AutoExpander.NotRemovedNote], _notes);

        // A removal that fails after that is a new one, and is told of at the next switching
        // off, in the call that fails it, once.
        _hook.RemoveFails = new TimeoutException("made up");
        _expander.Configure(on: false, [Signature]);
        Assert.Equal([AutoExpander.NotRemovedNote, AutoExpander.NotRemovedNote], _notes);
        _expander.Configure(on: false, [Signature]);
        Assert.Equal([AutoExpander.NotRemovedNote, AutoExpander.NotRemovedNote], _notes);

        // Closing says nothing, as before.
        _expander.Dispose();
        Assert.Equal([AutoExpander.NotRemovedNote, AutoExpander.NotRemovedNote], _notes);
    }

    [Fact]
    public void Closing_with_a_hook_that_cannot_be_removed_says_so_in_the_log_and_tells_the_owner_nothing()
    {
        SwitchOn();
        _hook.RemoveFails = new TimeoutException("made up");

        _expander.Dispose();

        Assert.Equal("The keyboard hook for snippet auto-expand could not be removed", Assert.Single(_reports).Message);
        Assert.Empty(_notes);

        // What that hook still reports is dropped, as after any closing.
        TypeThrough(_hook.Given[0], ";sig");
        Assert.Empty(_posted);
    }

    // ---- From the key press to the clipboard being put back ----

    [Fact]
    public void Typing_a_keyword_does_nothing_inside_the_callback_but_post_the_replacement()
    {
        SwitchOn();

        Type(";sig");

        // No clipboard, no key press, no log, no note, no question to Windows: the callback only posted.
        Assert.Empty(_events);
        Assert.Empty(_reports);
        Assert.Empty(_notes);
        Assert.Equal(0, _clipboard.Calls);
        Assert.Equal(0, _typing.ModifierQuestions);
        Assert.Single(_posted);
    }

    [Fact]
    public void The_whole_walk_from_the_last_key_to_the_clipboard_being_put_back()
    {
        SwitchOn();

        // The last key of the keyword: the callback posts and returns.
        Type(";sig");
        Assert.Empty(_events);

        // The posted work: the snippet goes on the clipboard, and nothing is pressed yet.
        RunPosted();
        Assert.Equal(["clipboard read", "clipboard set to Regards, Walrus"], _events);
        Assert.Equal(1, WaitsOf(PasteService.InPlaceDelay));

        // A moment later: the keyword is taken away and the snippet pasted, in one go.
        WaitEnds(PasteService.InPlaceDelay);
        Assert.Equal("4 Backspaces and Ctrl+V pressed in window 100", _events[^1]);
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        // Half a second later: the clipboard holds the earlier text again.
        WaitEnds(PasteService.RestoreDelay);
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to Regards, Walrus",
                "clipboard read",
                "4 Backspaces and Ctrl+V pressed in window 100",
                "clipboard read",
                "clipboard set to " + Earlier,
            ],
            _events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_notes);
        Assert.Empty(_reports);

        // And the next key starts afresh.
        Type("x;sig");
        Assert.Single(_posted);
    }

    [Fact]
    public void As_many_Backspaces_are_pressed_as_the_keyword_has_characters()
    {
        SwitchOn(Snippet("a", "!", "one"), Snippet("b", ";address", "eight"));

        TypeAndLetSettle("!");
        TypeAndLetSettle(";address");

        Assert.Equal(
            ["1 Backspaces and Ctrl+V pressed in window 100", "8 Backspaces and Ctrl+V pressed in window 100"],
            KeyPresses());
    }

    [Fact]
    public void The_placeholders_are_filled_in_as_for_a_snippet_run_from_the_launcher()
    {
        SwitchOn(Snippet("note", ";n", "On {date} at {time}: {clipboard} {other}"));

        Type(";n");
        RunPosted();

        Assert.Equal("On 2026-10-04 at 01:00: " + Earlier + " {other}", _clipboard.Text);
    }

    [Fact]
    public void A_keyword_in_the_middle_of_other_typing_is_replaced()
    {
        SwitchOn();

        TypeAndLetSettle("Dear all, ;sig");

        Assert.Equal(["4 Backspaces and Ctrl+V pressed in window 100"], KeyPresses());
    }

    [Fact]
    public void The_comparison_is_exact_capitals_included()
    {
        SwitchOn();

        Type(";SIG ;Sig");

        Assert.Empty(_posted);
    }

    [Fact]
    public void The_longer_keyword_wins_when_both_end_what_was_typed_and_a_keyword_that_starts_another_fires_first()
    {
        SwitchOn(Snippet("short", "sig", "short body"), Snippet("long", ";worksig", "long body"), Snippet("start", ";w", "start body"));

        // ";w" is the start of ";worksig" and fires at the w, so ";worksig" can never be typed whole.
        Type(";w");
        RunPosted();
        Assert.Equal("start body", _clipboard.Text);

        // Without the ";w" in front, "xworksig" ends with "sig" only.
        Type(" xworksig");
        RunPosted();
        Assert.Equal("short body", _clipboard.Text);
    }

    [Fact]
    public void An_expansion_that_is_empty_changes_nothing()
    {
        _clipboard.Text = null;
        SwitchOn(Snippet("empty", ";e", ""), Snippet("clip", ";c", "{clipboard}"));

        Type(";e");
        RunPosted();
        Type(";c");
        RunPosted();

        Assert.Empty(KeyPresses());
        Assert.Null(_clipboard.Text);
        Assert.Equal(0, WaitsOf(PasteService.InPlaceDelay));
        Assert.Empty(_notes);
    }

    [Fact]
    public void Nothing_is_logged_or_said_for_a_keyword_that_was_replaced()
    {
        SwitchOn();

        TypeAndLetSettle(";sig");
        WaitEnds(PasteService.RestoreDelay);

        Assert.Empty(_reports);
        Assert.Empty(_notes);
    }

    // ---- What breaks the run of typing ----

    [Fact]
    public void Backspace_puts_a_slip_right()
    {
        SwitchOn();

        Type(";sif");
        Press(KeyCodes.Backspace);
        Type("g");

        Assert.Single(_posted);
    }

    [Theory]
    [InlineData(KeyCodes.Enter)]
    [InlineData(KeyCodes.Escape)]
    [InlineData(KeyCodes.Tab)]
    [InlineData(KeyCodes.Left)]
    [InlineData(KeyCodes.Up)]
    [InlineData(KeyCodes.Right)]
    [InlineData(KeyCodes.Down)]
    [InlineData(KeyCodes.Home)]
    [InlineData(KeyCodes.End)]
    [InlineData(KeyCodes.PageUp)]
    [InlineData(KeyCodes.PageDown)]
    [InlineData(KeyCodes.Insert)]
    [InlineData(KeyCodes.Delete)]
    [InlineData(KeyCodes.Alt)]
    [InlineData(KeyCodes.LeftWindows)]
    [InlineData(0x74)]   // F5
    public void A_key_that_moves_or_ends_empties_what_was_kept(int virtualKey)
    {
        SwitchOn();

        Type(";si");
        Press(virtualKey);
        Type("g");

        Assert.Empty(_posted);
    }

    [Theory]
    [InlineData(KeyCodes.Shift)]
    [InlineData(KeyCodes.LeftShift)]
    [InlineData(KeyCodes.RightShift)]
    [InlineData(KeyCodes.Control)]
    [InlineData(KeyCodes.LeftControl)]
    [InlineData(KeyCodes.CapsLock)]
    [InlineData(KeyCodes.NumLock)]
    public void Shift_Ctrl_and_the_lock_keys_by_themselves_do_not_break_the_run(int virtualKey)
    {
        SwitchOn();

        Type(";si");
        Press(virtualKey);
        Type("g");

        Assert.Single(_posted);
    }

    [Fact]
    public void A_key_pressed_with_Ctrl_Alt_or_a_Windows_key_held_empties_what_was_kept_and_is_not_kept_itself()
    {
        SwitchOn();

        Type(";si");
        _hook.Press(new TypedKey(0x56, 'v', deadKey: false, chorded: true, injected: false, inBeacon: false, Notepad));
        Type("g");
        Assert.Empty(_posted);

        // The last character of a keyword typed with such a key held does not finish the keyword.
        Type(";si");
        _hook.Press(new TypedKey(0x47, 'g', deadKey: false, chorded: true, injected: false, inBeacon: false, Notepad));
        Assert.Empty(_posted);
    }

    [Fact]
    public void Typing_in_another_window_than_the_last_key_empties_what_was_kept()
    {
        SwitchOn();

        Type(";si", Notepad);
        Type("g", Browser);

        Assert.Empty(_posted);
    }

    [Fact]
    public void A_keyword_typed_whole_in_the_other_window_is_replaced_there()
    {
        SwitchOn();
        _typing.InFront = Browser;

        Type(";si", Notepad);
        TypeAndLetSettle(";sig", Browser);

        Assert.Equal(["4 Backspaces and Ctrl+V pressed in window 200"], KeyPresses());
    }

    [Fact]
    public void A_key_of_any_kind_pressed_in_another_window_in_between_empties_what_was_kept()
    {
        SwitchOn();

        Type(";si", Notepad);
        Type("x", Browser);
        Type("g", Notepad);
        Assert.Empty(_posted);

        // A Shift key by itself changes no text, but pressed in another window it shows that
        // the owner has been there, and the caret in the first window may be anywhere by now.
        Type(";si", Notepad);
        Press(KeyCodes.Shift, Browser);
        Type("g", Notepad);
        Assert.Empty(_posted);
    }

    [Fact]
    public void Nothing_typed_into_one_of_Beacons_own_windows_is_kept_or_replaced()
    {
        SwitchOn();

        foreach (char character in ";sig")
        {
            _hook.Press(new TypedKey(0x41, character, deadKey: false, chorded: false, injected: false, inBeacon: true, BeaconWindow));
        }

        Assert.Empty(_posted);

        // And what was typed elsewhere before the launcher was opened is forgotten.
        Type(";si", Notepad);
        _hook.Press(new TypedKey(0x41, 'x', deadKey: false, chorded: false, injected: false, inBeacon: true, BeaconWindow));
        Type("g", Notepad);
        Assert.Empty(_posted);
    }

    [Fact]
    public void What_another_program_types_is_never_kept_and_breaks_the_run()
    {
        SwitchOn();

        foreach (char character in ";sig")
        {
            _hook.Press(new TypedKey(0x41, character, deadKey: false, chorded: false, injected: true, inBeacon: false, Notepad));
        }

        Assert.Empty(_posted);

        Type(";si");
        _hook.Press(new TypedKey(KeyCodes.Shift, (char)0, deadKey: false, chorded: false, injected: true, inBeacon: false, Notepad));
        Type("g");
        Assert.Empty(_posted);
    }

    [Fact]
    public void A_key_with_no_window_in_front_empties_what_was_kept()
    {
        SwitchOn();

        Type(";si");
        Type("g", window: 0);
        Type("g");

        Assert.Empty(_posted);
    }

    [Fact]
    public void A_dead_key_empties_what_was_kept_and_the_character_after_it_is_not_kept_either()
    {
        SwitchOn(Snippet("e", "es", "body"));

        // An accent and then e gives one accented letter on screen. The e is not kept, so the
        // s that follows does not make "es".
        _hook.Press(new TypedKey(0xDE, (char)0xB4, deadKey: true, chorded: false, injected: false, inBeacon: false, Notepad));
        Type("es");
        Assert.Empty(_posted);

        // The character after that is kept again.
        Type("es");
        Assert.Single(_posted);
    }

    [Fact]
    public void A_dead_key_in_the_middle_of_a_keyword_breaks_it()
    {
        SwitchOn();

        Type(";si");
        _hook.Press(new TypedKey(0xDE, (char)0, deadKey: true, chorded: false, injected: false, inBeacon: false, Notepad));
        Type("g");
        Type("g");

        Assert.Empty(_posted);
    }

    [Fact]
    public void Backspace_after_a_dead_key_empties_what_was_kept()
    {
        SwitchOn();

        Type(";si");
        _hook.Press(new TypedKey(0xDE, (char)0, deadKey: true, chorded: false, injected: false, inBeacon: false, Notepad));
        Press(KeyCodes.Backspace);
        Type("g");

        Assert.Empty(_posted);
    }

    [Fact]
    public void A_key_in_another_window_does_not_disarm_a_dead_key_and_the_next_character_typed_back_in_the_first_is_swallowed()
    {
        SwitchOn(Snippet("e", "es", "body"));

        // Whether Windows carries a waiting accent from one window to the next is not known, so
        // the rule stays armed across the change of window: the next character is a break,
        // wherever it is typed.
        _hook.Press(DeadKey(window: Notepad));
        Press(KeyCodes.Left, Browser);
        Type("es", Notepad);
        Assert.Empty(_posted);

        // The first character was swallowed and the rule is used up. The next is kept again.
        Type("es", Notepad);
        Assert.Single(_posted);
    }

    [Fact]
    public void A_dead_key_stays_armed_when_the_next_character_is_typed_in_another_window_and_is_swallowed_there()
    {
        SwitchOn(Snippet("e", "es", "body"));

        _hook.Press(DeadKey(window: Notepad));
        Type("es", Browser);
        Assert.Empty(_posted);

        // The buffer was emptied at the change of window as before, and the first character
        // typed there was the one the rule swallowed. The next is kept again.
        Type("es", Browser);
        Assert.Single(_posted);
    }

    [Theory]
    [InlineData(KeyCodes.Left, false)]
    [InlineData(KeyCodes.Enter, false)]
    [InlineData(0x74, false)]            // F5
    [InlineData(KeyCodes.Shift, false)]  // the accent on a capital: dead key, Shift, E
    [InlineData(0x43, true)]             // Ctrl+C
    public void A_key_that_types_no_character_between_a_dead_key_and_a_letter_leaves_the_letter_swallowed(int virtualKey, bool chorded)
    {
        SwitchOn(Snippet("e", "es", "body"));

        // Windows may still put the accent on the e, whatever was pressed in between. Were the
        // e kept, the s would finish a keyword that is not what stands on screen.
        _hook.Press(DeadKey());
        Press(virtualKey, chorded: chorded);
        Type("es");

        Assert.Empty(_posted);
    }

    [Fact]
    public void Backspace_between_a_dead_key_and_a_letter_leaves_the_letter_swallowed()
    {
        SwitchOn(Snippet("e", "es", "body"));

        _hook.Press(DeadKey());
        Press(KeyCodes.Backspace);
        Type("es");

        Assert.Empty(_posted);
    }

    [Theory]
    [InlineData(true, false)]   // a key that is a dead key by itself, pressed with Ctrl or Alt held
    [InlineData(false, true)]   // pressed by a program
    public void A_dead_key_swallows_the_next_character_whatever_else_its_key_press_is(bool chorded, bool injected)
    {
        SwitchOn(Snippet("e", "es", "body"));

        // Such a key press is a break by its kind. It is a dead key all the same. The hook
        // translates a key pressed in a chord with Ctrl and Alt left out, so it does report a
        // key that is a dead key by itself as one. What it cannot see is a dead key that
        // exists only at the AltGr level of a layout.
        _hook.Press(DeadKey(chorded, injected));
        Type("es");

        Assert.Empty(_posted);
    }

    [Fact]
    public void A_dead_key_swallows_one_character_and_the_next_is_kept_again()
    {
        SwitchOn(Snippet("e", "es", "body"));

        _hook.Press(DeadKey());
        Type("e");
        Assert.Empty(_posted);

        // The accent went on the first e. The second one is an e.
        Type("e");
        Assert.Empty(_posted);
        Type("s");
        Assert.Single(_posted);
    }

    [Fact]
    public void Two_dead_keys_swallow_one_character_between_them_and_the_next_is_kept_again()
    {
        SwitchOn(Snippet("e", "es", "body"));

        _hook.Press(DeadKey());
        _hook.Press(DeadKey());
        Type("e");
        Assert.Empty(_posted);

        Type("e");
        Assert.Empty(_posted);
        Type("s");
        Assert.Single(_posted);
    }

    [Fact]
    public void A_keyword_typed_twice_is_replaced_twice()
    {
        SwitchOn();

        TypeAndLetSettle(";sig");
        TypeAndLetSettle(";sig");

        Assert.Equal(2, KeyPresses().Count);
    }

    // ---- When the replacement is called off ----

    [Fact]
    public void A_key_pressed_before_the_posted_work_could_run_leaves_everything_as_it_was_typed()
    {
        SwitchOn();

        Type(";sig");
        Type(" ");
        RunPosted();

        // Nothing was touched: not the clipboard, and no key. Windows was not even asked
        // whether a modifier key is held.
        Assert.Empty(_events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, _clipboard.Calls);
        Assert.Equal(0, _typing.ModifierQuestions);
        Assert.Equal(0, WaitsOf(PasteService.InPlaceDelay));
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_key_pressed_in_the_moment_before_the_keys_calls_the_paste_off_and_the_clipboard_gets_its_text_back()
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        // The owner types on while the snippet is on the clipboard and the keys are not yet
        // pressed. The Backspaces would now take away the space and three characters of the keyword.
        Type(" ");
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, WaitsOf(PasteService.RestoreDelay));
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_key_that_arrives_while_Windows_is_asked_about_a_modifier_key_at_the_last_look_calls_the_paste_off()
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        // The last look asks Windows two things and reads how many keys the hook has seen. The
        // owner's next key arrives while the second question is being answered. The count is
        // read after both, so that key is seen.
        _typing.DuringModifierQuestion = () => Type("x");
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Null(_typing.DuringModifierQuestion);
        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, WaitsOf(PasteService.RestoreDelay));
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_key_that_arrives_while_Windows_is_asked_about_the_window_in_front_at_the_last_look_calls_the_paste_off()
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        _typing.DuringWindowQuestion = () => Type("x");
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Null(_typing.DuringWindowQuestion);
        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, WaitsOf(PasteService.RestoreDelay));
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Theory]
    [InlineData(KeyCodes.Backspace)]
    [InlineData(KeyCodes.Enter)]
    [InlineData(KeyCodes.Left)]
    public void Any_key_that_can_change_the_text_calls_the_paste_off(int virtualKey)
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        Press(virtualKey);
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void A_Shift_key_pressed_and_let_go_of_in_that_moment_does_not_call_the_paste_off()
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        Press(KeyCodes.Shift);
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Single(KeyPresses());
    }

    [Fact]
    public void A_modifier_key_held_at_the_moment_of_the_keys_calls_the_paste_off()
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        _typing.ModifierHeldFor = Throughout;
        WaitEnds(PasteService.InPlaceDelay);

        // With Ctrl held, Backspace would take away a whole word.
        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void Another_window_in_front_when_the_posted_work_runs_leaves_everything_untouched()
    {
        SwitchOn();

        Type(";sig");
        _typing.InFront = Browser;
        RunPosted();

        Assert.Empty(_events);
        Assert.Equal(0, _clipboard.Calls);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void Another_window_in_front_at_the_moment_of_the_keys_calls_the_paste_off()
    {
        SwitchOn();

        Type(";sig");
        RunPosted();
        _typing.InFront = Browser;
        WaitEnds(PasteService.InPlaceDelay);

        // No Backspace goes to the other window, and the clipboard gets its text back.
        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_paste_that_was_called_off_while_the_clipboard_held_no_text_leaves_the_snippet_there()
    {
        // The paste service can only put text back. A picture that was on the clipboard is
        // gone once the snippet has been put there, called off or not.
        _clipboard.Text = null;
        SwitchOn();

        Type(";sig");
        RunPosted();
        Type(" ");
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Empty(KeyPresses());
        Assert.Equal("Regards, Walrus", _clipboard.Text);
    }

    // ---- A keyword that ends with Shift held ----

    [Fact]
    public void A_keyword_whose_last_character_needs_Shift_is_replaced_once_Shift_is_let_go_of()
    {
        SwitchOn(Snippet("mail", "@@", "walrus@example.org"));
        _typing.ModifierHeldFor = 3;

        Type("@@");
        RunPosted();

        // Shift is still down: nothing is touched, and it is looked at again shortly.
        Assert.Empty(_events);
        Assert.Equal(0, _clipboard.Calls);
        Assert.Equal(1, WaitsOf(AutoExpander.ModifierWait));
        WaitEnds(AutoExpander.ModifierWait);
        WaitEnds(AutoExpander.ModifierWait);
        Assert.Equal(0, _clipboard.Calls);

        // Let go of: the replacement is made as usual.
        WaitEnds(AutoExpander.ModifierWait);
        Assert.Equal("walrus@example.org", _clipboard.Text);
        WaitEnds(PasteService.InPlaceDelay);
        Assert.Equal(["2 Backspaces and Ctrl+V pressed in window 100"], KeyPresses());
        Assert.Equal(0, WaitsOf(AutoExpander.ModifierWait));
    }

    [Fact]
    public void A_modifier_key_that_stays_held_is_waited_for_for_a_second_and_then_the_keyword_is_left_as_typed()
    {
        SwitchOn();
        _typing.ModifierHeldFor = Throughout;

        Type(";sig");
        RunPosted();
        int waited = 0;
        while (WaitsOf(AutoExpander.ModifierWait) > 0)
        {
            WaitEnds(AutoExpander.ModifierWait);
            waited++;
        }

        Assert.Equal(AutoExpander.MaxModifierWaits, waited);
        Assert.Equal(TimeSpan.FromSeconds(1), AutoExpander.ModifierWait * AutoExpander.MaxModifierWaits);
        Assert.Empty(_events);
        Assert.Equal(0, _clipboard.Calls);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_key_pressed_while_a_modifier_key_is_waited_for_ends_the_waiting()
    {
        SwitchOn();
        _typing.ModifierHeldFor = Throughout;

        Type(";sig");
        RunPosted();
        WaitEnds(AutoExpander.ModifierWait);
        Type("X");
        _typing.ModifierHeldFor = 0;
        WaitEnds(AutoExpander.ModifierWait);

        Assert.Equal(0, WaitsOf(AutoExpander.ModifierWait));
        Assert.Empty(_events);
        Assert.Equal(0, _clipboard.Calls);
    }

    [Fact]
    public void Another_window_coming_to_the_front_while_a_modifier_key_is_waited_for_ends_the_waiting()
    {
        SwitchOn();
        _typing.ModifierHeldFor = Throughout;

        Type(";sig");
        RunPosted();
        _typing.InFront = Browser;
        WaitEnds(AutoExpander.ModifierWait);

        Assert.Equal(0, WaitsOf(AutoExpander.ModifierWait));
        Assert.Equal(0, _clipboard.Calls);
    }

    // ---- When something fails ----

    [Fact]
    public void A_busy_clipboard_leaves_the_keyword_as_typed_and_says_so_in_fixed_words()
    {
        SwitchOn();
        _clipboard.BusyFor = 1;

        Type(";sig");
        RunPosted();

        Assert.Equal(0, WaitsOf(PasteService.InPlaceDelay));
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal([AutoExpander.NotReplacedNote], _notes);
        var report = Assert.Single(_reports);
        Assert.Equal("A typed keyword could not be replaced by its snippet", report.Message);
        Assert.IsType<ClipboardBusyException>(report.Error);
    }

    [Fact]
    public void A_busy_clipboard_at_the_placeholder_leaves_the_keyword_as_typed_too()
    {
        SwitchOn(Snippet("clip", ";c", "{clipboard}"));
        _clipboard.BusyFor = 1;

        Type(";c");
        RunPosted();

        Assert.Equal(0, WaitsOf(PasteService.InPlaceDelay));
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal([AutoExpander.NotReplacedNote], _notes);
    }

    [Fact]
    public void A_key_press_that_Windows_refuses_leaves_the_text_on_the_clipboard_as_for_any_paste()
    {
        SwitchOn();
        _typing.KeyPressFails = new InvalidOperationException("made up");

        TypeAndLetSettle(";sig");

        Assert.Empty(KeyPresses());
        Assert.Equal("Regards, Walrus", _clipboard.Text);
        Assert.Equal([PasteService.NotPastedNote], _notes);
        Assert.Equal("The paste key press could not be sent", Assert.Single(_reports).Message);
    }

    [Fact]
    public void A_key_delivered_while_the_clipboard_was_being_read_ends_the_replacement_before_anything_is_changed()
    {
        // Reading the clipboard waits for the program that owns it, and Windows can deliver a
        // key meanwhile. Here that key arrives during the read for {clipboard}.
        var events = new List<string>();
        var posted = new List<Action>();
        var hook = new FakeHook(events);
        var typing = new FakeTyping(events);
        var clipboard = new KeyDuringReadClipboard(() => hook.Press(Letter('x')));
        var paste = new PasteService(clipboard, new LauncherTarget(), (_, _) => { }, _notes.Add);
        var expander = new AutoExpander(hook, typing, paste, _clock, posted.Add, (_, _) => { }, _notes.Add, (_, _) => { }, FarEast);
        expander.Configure(on: true, [Snippet("clip", ";c", "[{clipboard}]")]);

        hook.Press(Letter(';'));
        hook.Press(Letter('c'));
        posted[0]();

        Assert.Equal(1, clipboard.Reads);
        Assert.Equal(0, clipboard.Writes);
        Assert.DoesNotContain(events, entry => entry.Contains("Ctrl+V"));
        Assert.Empty(_notes);
    }

    private sealed class KeyDuringReadClipboard(Action duringRead) : IClipboard
    {
        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public string? GetText()
        {
            Reads++;
            duringRead();
            return Earlier;
        }

        public void SetText(string text) => Writes++;

        public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks) => Writes++;
    }

    // ---- Two keywords, the library, the setting, closing ----

    [Fact]
    public void Two_keywords_typed_one_after_the_other_both_get_the_owners_text_and_it_is_put_back_once()
    {
        SwitchOn(Signature, Snippet("quote", ";q", "He said: {clipboard}"));

        TypeAndLetSettle(";sig");
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        // Within the half second in which the first paste waits to put the earlier text back.
        TypeAndLetSettle(";q");

        // Not "He said: Regards, Walrus".
        Assert.Equal("He said: " + Earlier, _clipboard.Text);
        Assert.Equal(
            ["4 Backspaces and Ctrl+V pressed in window 100", "2 Backspaces and Ctrl+V pressed in window 100"],
            KeyPresses());

        // The first wait runs out and puts nothing back; the second puts the owner's text back.
        WaitEnds(PasteService.RestoreDelay);
        Assert.Equal("He said: " + Earlier, _clipboard.Text);
        WaitEnds(PasteService.RestoreDelay);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(1, _events.Count(entry => entry == "clipboard set to " + Earlier));
    }

    [Fact]
    public void A_second_keyword_typed_before_the_first_ones_keys_were_pressed_takes_the_firsts_place()
    {
        SwitchOn(Signature, Snippet("quote", ";q", "He said: {clipboard}"));

        Type(";sig");
        RunPosted();

        // The owner types on, and what he types is another keyword. The first paste is called
        // off by the keys that came since; the second one is made, with the owner's own text.
        Type(";q");
        RunPosted();
        Assert.Equal("He said: " + Earlier, _clipboard.Text);

        WaitEnds(PasteService.InPlaceDelay);   // the first one's moment: overtaken, nothing pressed
        Assert.Empty(KeyPresses());
        WaitEnds(PasteService.InPlaceDelay);   // the second one's
        Assert.Equal(["2 Backspaces and Ctrl+V pressed in window 100"], KeyPresses());

        WaitEnds(PasteService.RestoreDelay);
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void A_keyword_half_typed_when_the_library_is_loaded_again_is_finished_against_the_new_library()
    {
        SwitchOn();

        Type(";si");
        _expander.UseLibrary([Snippet("signature", ";sig", "The new signature")]);
        Type("g");
        RunPosted();

        Assert.Equal("The new signature", _clipboard.Text);
    }

    [Fact]
    public void A_keyword_that_has_gone_from_the_library_is_no_longer_replaced()
    {
        SwitchOn();

        Type(";si");
        _expander.UseLibrary([Snippet("signature", null, "no keyword any more")]);
        Type("g");
        Type(";sig");

        Assert.Empty(_posted);
    }

    [Fact]
    public void A_keyword_that_was_typed_before_the_library_changed_is_replaced_by_the_snippet_it_matched()
    {
        SwitchOn();

        Type(";sig");
        _expander.UseLibrary([Snippet("signature", ";sig", "The new signature")]);
        RunPosted();

        Assert.Equal("Regards, Walrus", _clipboard.Text);
    }

    [Fact]
    public void A_new_library_is_logged_by_its_count_only_and_only_when_the_count_changed()
    {
        SwitchOn();

        _expander.UseLibrary([Snippet("signature", ";sig", "another body")]);
        Assert.Empty(_reports);

        _expander.UseLibrary([Signature, Snippet("address", ";addr", "1 Otter Way"), Snippet("long", new string('k', 65), "too long")]);
        Assert.Equal("Snippet auto-expand keywords: 2, left out: 1", Assert.Single(_reports).Message);
    }

    [Fact]
    public void While_it_is_off_a_new_library_is_not_looked_at_and_nothing_is_logged()
    {
        _expander.Configure(on: false, []);
        _reports.Clear();

        _expander.UseLibrary([Signature]);

        Assert.Empty(_reports);
        Assert.False(_hook.Installed);
    }

    [Fact]
    public void Switched_off_before_the_posted_work_could_run_nothing_is_touched()
    {
        SwitchOn();

        Type(";sig");
        _expander.Configure(on: false, [Signature]);
        _events.Clear();
        RunPosted();

        Assert.Empty(_events);
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void Switched_off_and_on_again_before_the_posted_work_could_run_nothing_is_touched()
    {
        SwitchOn();

        Type(";sig");
        _expander.Configure(on: true, [Signature]);
        _events.Clear();
        RunPosted();

        Assert.Empty(_events);
    }

    [Fact]
    public void Switched_off_in_the_moment_before_the_keys_the_paste_is_called_off_and_the_clipboard_gets_its_text_back()
    {
        SwitchOn();
        Type(";sig");
        RunPosted();
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        _expander.Configure(on: false, [Signature]);
        WaitEnds(PasteService.InPlaceDelay);

        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Switched_off_while_a_paste_waits_to_put_the_text_back_the_owners_text_is_still_put_back()
    {
        SwitchOn();
        TypeAndLetSettle(";sig");
        Assert.Single(KeyPresses());

        _expander.Configure(on: false, [Signature]);
        WaitEnds(PasteService.RestoreDelay);

        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void Closing_removes_the_hook_and_nothing_is_replaced_or_installed_afterwards()
    {
        SwitchOn();
        Type(";sig");

        _expander.Dispose();
        _expander.Dispose();

        Assert.False(_hook.Installed);
        Assert.Equal(["hook removed"], _events);
        _events.Clear();

        // The replacement that was posted, a key that was on its way, a renewal that was
        // waiting, a library that was loaded and a Reload: none of them does anything now.
        RunPosted();
        _hook.PressLate(Letter('x'));
        WaitEnds(AutoExpander.RenewEvery);
        _expander.UseLibrary([Signature]);
        _expander.Configure(on: true, [Signature]);

        Assert.Empty(_events);
        Assert.False(_hook.Installed);
        Assert.Empty(_waits);
        Assert.False(_expander.IsOn);
    }

    [Fact]
    public void Closing_in_the_moment_before_the_keys_calls_the_paste_off()
    {
        SwitchOn();
        Type(";sig");
        RunPosted();

        _expander.Dispose();

        // Were the timer still to run before Beacon is gone, no key would be pressed and the
        // owner's text would go back. When Beacon is gone first, the clipboard keeps the
        // snippet, which is marked so that no clipboard history records it.
        WaitEnds(PasteService.InPlaceDelay);
        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void Closing_while_a_paste_waits_to_put_the_text_back_does_not_disturb_the_paste_service()
    {
        SwitchOn();
        TypeAndLetSettle(";sig");

        _expander.Dispose();

        Assert.Equal("Regards, Walrus", _clipboard.Text);
        WaitEnds(PasteService.RestoreDelay);
        Assert.Equal(Earlier, _clipboard.Text);
    }

    // ---- Renewing the hook ----

    /// <summary>
    /// One round of the renewal: the ten minutes run out, and then the few seconds in which a
    /// key would show that the hook is there.
    /// </summary>
    private void RenewalComesRound()
    {
        WaitEnds(AutoExpander.RenewEvery);
        WaitEnds(AutoExpander.QuietBeforeRenewing);
    }

    [Fact]
    public void While_nobody_types_the_hook_is_taken_out_and_put_in_again_every_ten_minutes()
    {
        SwitchOn();

        RenewalComesRound();
        RenewalComesRound();

        Assert.Equal(TimeSpan.FromMinutes(10), AutoExpander.RenewEvery);
        Assert.Equal(TimeSpan.FromSeconds(5), AutoExpander.QuietBeforeRenewing);
        Assert.Equal(["hook removed", "hook installed", "hook removed", "hook installed"], _events);
        Assert.Equal(3, _hook.Installs);
        Assert.Equal(3, _hook.Threads);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
        Assert.Equal(0, WaitsOf(AutoExpander.QuietBeforeRenewing));
        Assert.Empty(_reports);
    }

    [Fact]
    public void When_the_ten_minutes_run_out_nothing_is_touched_yet_only_the_quiet_seconds_begin()
    {
        SwitchOn();

        WaitEnds(AutoExpander.RenewEvery);

        Assert.Empty(_events);
        Assert.Equal(0, WaitsOf(AutoExpander.RenewEvery));
        Assert.Equal(1, WaitsOf(AutoExpander.QuietBeforeRenewing));
    }

    [Fact]
    public void A_hook_that_delivers_a_key_in_those_seconds_is_left_alone_so_that_a_keyword_being_typed_is_not_cut_off()
    {
        SwitchOn();
        Type(";s");
        WaitEnds(AutoExpander.RenewEvery);
        Type("i");

        WaitEnds(AutoExpander.QuietBeforeRenewing);

        Assert.Empty(_events);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
        Type("g");
        Assert.Single(_posted);
    }

    [Fact]
    public void Keys_from_before_those_seconds_do_not_keep_the_hook_from_being_renewed()
    {
        SwitchOn();
        Type("abc");

        RenewalComesRound();

        Assert.Equal(["hook removed", "hook installed"], _events);
    }

    [Fact]
    public void A_Shift_key_by_itself_in_those_seconds_is_not_typing_and_the_hook_is_renewed()
    {
        SwitchOn();
        WaitEnds(AutoExpander.RenewEvery);
        Press(KeyCodes.Shift);

        WaitEnds(AutoExpander.QuietBeforeRenewing);

        Assert.Equal(["hook removed", "hook installed"], _events);
    }

    [Fact]
    public void Renewing_forgets_what_was_typed_because_keys_may_have_been_missed()
    {
        SwitchOn();
        Type(";si");

        RenewalComesRound();
        Type("g");

        Assert.Equal(["hook removed", "hook installed"], _events);
        Assert.Empty(_posted);
    }

    [Fact]
    public void A_keyword_typed_in_those_seconds_is_still_replaced_when_the_posted_work_runs_late()
    {
        SwitchOn();
        WaitEnds(AutoExpander.RenewEvery);
        Type(";sig");
        Assert.Single(_posted);

        // The UI thread was busy: the quiet seconds run out before the posted work is reached.
        // A key was seen in them, so the hook stays, and the keyword is replaced after all.
        WaitEnds(AutoExpander.QuietBeforeRenewing);
        Assert.Empty(_events);
        RunPosted();
        Assert.Equal("Regards, Walrus", _clipboard.Text);
    }

    [Fact]
    public void A_hook_that_could_not_be_given_back_is_noticed_at_the_next_renewal_and_counted_in_the_log()
    {
        SwitchOn();
        _hook.TakenAway = true;

        // No key arrives any more, however much is typed.
        Type(";sig");
        Assert.Empty(_posted);

        RenewalComesRound();

        // What is known is that the hook could not be given back, and the log says no more
        // than that: Windows does not tell a program whether it had taken a hook away.
        Assert.Equal(
            "The keyboard hook for snippet auto-expand could not be given back to Windows when it was removed. Windows may have taken it away before. Times in this run: 1",
            Assert.Single(_reports).Message);
        Assert.Null(_reports[0].Error);
        Type(";sig");
        Assert.Single(_posted);

        _hook.TakenAway = true;
        RenewalComesRound();
        Assert.EndsWith("Times in this run: 2", _reports[^1].Message);
    }

    [Fact]
    public void A_hook_that_could_not_be_given_back_is_noticed_and_counted_at_a_reload_and_at_switching_off_too()
    {
        SwitchOn();
        _hook.TakenAway = true;

        _expander.Configure(on: true, [Signature]);

        Assert.Equal(
            [
                "The keyboard hook for snippet auto-expand could not be given back to Windows when it was removed. Windows may have taken it away before. Times in this run: 1",
                "Snippet auto-expand is on: a keyboard hook is installed, on a thread of its own. Keywords: 1, left out: 0",
            ],
            _reports.Select(report => report.Message));
        Type(";sig");
        Assert.Single(_posted);

        _hook.TakenAway = true;
        _expander.Configure(on: false, [Signature]);
        Assert.Contains(
            _reports,
            report => report.Message == "The keyboard hook for snippet auto-expand could not be given back to Windows when it was removed. Windows may have taken it away before. Times in this run: 2");

        // Remove returned, so the hook's thread has ended and no hook is left: the ordinary line.
        Assert.Equal(
            "Snippet auto-expand is off: no keyboard hook is installed and no thread is running for one. Keyboard hooks installed by this run of Beacon: 2, threads started for them: 2",
            _reports[^1].Message);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_hook_that_could_not_be_removed_is_not_counted_as_one_that_could_not_be_given_back()
    {
        SwitchOn();
        _hook.RemoveFails = new TimeoutException("made up");

        _expander.Configure(on: false, [Signature]);

        Assert.Equal("The keyboard hook for snippet auto-expand could not be removed", _reports[0].Message);
        Assert.IsType<TimeoutException>(_reports[0].Error);
        Assert.DoesNotContain(_reports, report => report.Message.Contains("could not be given back") || report.Message.Contains("Times in this run"));

        // The keys that hook still delivers are not looked at.
        TypeThrough(_hook.Given[0], ";sig");
        Assert.Empty(_posted);
    }

    [Fact]
    public void A_reload_starts_one_line_of_renewals_and_the_earlier_one_ends()
    {
        SwitchOn();
        _expander.Configure(on: true, [Signature]);
        _events.Clear();

        // Two waits are there: the one from before the reload, which does nothing, and the new one.
        Assert.Equal(2, WaitsOf(AutoExpander.RenewEvery));
        WaitEnds(AutoExpander.RenewEvery);
        Assert.Empty(_events);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
        Assert.Equal(0, WaitsOf(AutoExpander.QuietBeforeRenewing));

        RenewalComesRound();
        Assert.Equal(["hook removed", "hook installed"], _events);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
    }

    [Fact]
    public void A_reload_in_the_quiet_seconds_ends_that_renewal()
    {
        SwitchOn();
        WaitEnds(AutoExpander.RenewEvery);
        _expander.Configure(on: true, [Signature]);
        _events.Clear();

        WaitEnds(AutoExpander.QuietBeforeRenewing);

        Assert.Empty(_events);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
    }

    [Fact]
    public void Switched_off_no_renewal_is_made_and_none_waits_once_the_last_one_has_run_out()
    {
        SwitchOn();
        _expander.Configure(on: false, [Signature]);
        _events.Clear();

        WaitEnds(AutoExpander.RenewEvery);

        Assert.Empty(_events);
        Assert.False(_hook.Installed);
        Assert.Empty(_waits);
    }

    [Fact]
    public void Switched_off_in_the_quiet_seconds_nothing_is_installed_again()
    {
        SwitchOn();
        WaitEnds(AutoExpander.RenewEvery);
        _expander.Configure(on: false, [Signature]);
        _events.Clear();

        WaitEnds(AutoExpander.QuietBeforeRenewing);

        Assert.Empty(_events);
        Assert.False(_hook.Installed);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_renewal_that_fails_is_logged_once_and_the_next_one_is_still_made()
    {
        SwitchOn();
        _hook.InstallFails = new InvalidOperationException("made up");

        RenewalComesRound();

        // No hook is there now, so the next round does not wait for a quiet moment.
        WaitEnds(AutoExpander.RenewEvery);

        Assert.Equal("The keyboard hook for snippet auto-expand could not be installed", Assert.Single(_reports).Message);
        Assert.Equal(1, WaitsOf(AutoExpander.RenewEvery));
        Assert.Equal(0, WaitsOf(AutoExpander.QuietBeforeRenewing));

        _hook.InstallFails = null;
        WaitEnds(AutoExpander.RenewEvery);
        Assert.True(_hook.Installed);
        Assert.Equal("The keyboard hook for snippet auto-expand is installed now", _reports[^1].Message);
    }

    [Fact]
    public void A_timer_that_cannot_be_set_is_logged_and_does_not_stop_auto_expand()
    {
        var reports = new List<(string Message, Exception? Error)>();
        var refused = new InvalidOperationException("made up");
        var expander = new AutoExpander(
            new FakeHook([]),
            new FakeTyping([]),
            _paste,
            _clock,
            _posted.Add,
            (_, _) => throw refused,
            _notes.Add,
            (message, error) => reports.Add((message, error)),
            FarEast);

        expander.Configure(on: true, [Signature]);

        Assert.True(expander.IsOn);
        Assert.Equal(2, reports.Count);
        Assert.Equal("The keyboard hook for snippet auto-expand will not be renewed until Reload", reports[1].Message);
        Assert.Same(refused, reports[1].Error);
        Assert.Empty(_notes);
    }

    // ---- Privacy, and what must never leave the callback ----

    [Fact]
    public void Nothing_that_was_typed_and_no_keyword_or_snippet_ever_reaches_the_log_or_a_notification()
    {
        var secret = Snippet("private-otter-name", ";tusk", "the-private-walrus-body");
        _expander.Configure(on: true, [secret]);
        TypeAndLetSettle("seal-password ;tusk");
        _expander.UseLibrary([secret, Snippet("private-seal-name", ";flipper", "another-private-body")]);
        _clipboard.BusyFor = 1;
        Type(";flipper");
        RunPosted();
        _hook.TakenAway = true;
        RenewalComesRound();
        _expander.Configure(on: false, []);

        Assert.True(_reports.Count >= 5);
        foreach (string said in _reports.Select(report => report.Message).Concat(_notes))
        {
            foreach (string never in new[] { "tusk", "flipper", "walrus", "otter", "seal", "private", "password", "body", ";" })
            {
                Assert.DoesNotContain(never, said, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void An_exception_inside_the_callback_never_leaves_it_and_is_reported_once_and_afterwards()
    {
        var events = new List<string>();
        var posted = new List<Action>();
        var reports = new List<(string Message, Exception? Error)>();
        var hook = new FakeHook(events);
        var refused = new InvalidOperationException("made up");
        bool refuseNextPost = false;
        var expander = new AutoExpander(
            hook,
            new FakeTyping(events),
            _paste,
            _clock,
            action =>
            {
                if (refuseNextPost)
                {
                    refuseNextPost = false;
                    throw refused;
                }

                posted.Add(action);
            },
            (_, _) => { },
            _notes.Add,
            (message, error) => reports.Add((message, error)),
            FarEast);
        expander.Configure(on: true, [Signature]);
        reports.Clear();

        void TypeKeyword()
        {
            foreach (char character in ";sig")
            {
                hook.Press(Letter(character));
            }
        }

        // The keyword's last key makes the callback post the replacement, and that throws.
        // Nothing comes out of the callback, and nothing is written to the log from inside it:
        // the failure is posted instead.
        refuseNextPost = true;
        TypeKeyword();
        Assert.Empty(reports);
        Assert.Single(posted);

        // When that runs, the log gets fixed words and the exception.
        posted[0]();
        var report = Assert.Single(reports);
        Assert.Equal("A key press could not be looked at for snippet auto-expand", report.Message);
        Assert.Same(refused, report.Error);

        // It is said once for the run, however often it happens.
        refuseNextPost = true;
        TypeKeyword();
        Assert.Single(posted);
        Assert.Single(reports);

        // And auto-expand goes on working.
        TypeKeyword();
        Assert.Equal(2, posted.Count);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_post_that_fails_for_the_failure_too_is_swallowed()
    {
        var hook = new FakeHook([]);
        var expander = new AutoExpander(
            hook,
            new FakeTyping([]),
            _paste,
            _clock,
            _ => throw new InvalidOperationException("made up"),
            (_, _) => { },
            _notes.Add,
            (message, error) => _reports.Add((message, error)),
            FarEast);
        expander.Configure(on: true, [Signature]);
        _reports.Clear();

        foreach (char character in ";sig;sig")
        {
            hook.Press(Letter(character));
        }

        Assert.Empty(_reports);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_failure_inside_the_callback_that_could_not_be_handed_over_is_reported_at_the_next_one_and_then_no_more()
    {
        var posted = new List<Action>();
        var reports = new List<(string Message, Exception? Error)>();
        var hook = new FakeHook([]);
        int refuse = 0;
        var expander = new AutoExpander(
            hook,
            new FakeTyping([]),
            _paste,
            _clock,
            action =>
            {
                if (refuse > 0)
                {
                    refuse--;
                    throw new InvalidOperationException("made up");
                }

                posted.Add(action);
            },
            (_, _) => { },
            _notes.Add,
            (message, error) => reports.Add((message, error)),
            FarEast);
        expander.Configure(on: true, [Signature]);
        reports.Clear();

        void TypeKeyword()
        {
            foreach (char character in ";sig")
            {
                hook.Press(Letter(character));
            }
        }

        // The first failure: the replacement cannot be posted, and the report of that cannot
        // be posted either. Nothing has been said, so it does not count as said.
        refuse = 2;
        TypeKeyword();
        Assert.Equal(0, refuse);
        Assert.Empty(posted);

        // The second: the replacement cannot be posted, and this time the report can.
        refuse = 1;
        TypeKeyword();
        Assert.Equal(0, refuse);
        Assert.Single(posted);
        posted[0]();
        Assert.Equal("A key press could not be looked at for snippet auto-expand", Assert.Single(reports).Message);

        // The third is not reported again: once for a run.
        refuse = 1;
        TypeKeyword();
        Assert.Equal(0, refuse);
        Assert.Single(posted);
        Assert.Single(reports);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_report_or_a_notification_that_throws_does_not_stop_auto_expand()
    {
        var hook = new FakeHook([]);
        var expander = new AutoExpander(
            hook,
            new FakeTyping([]),
            _paste,
            _clock,
            _posted.Add,
            (after, action) => _waits.Add((after, action)),
            _ => throw new InvalidOperationException("made up"),
            (_, _) => throw new InvalidOperationException("made up"),
            FarEast);

        expander.Configure(on: true, [Signature]);
        hook.InstallFails = new InvalidOperationException("made up");
        expander.Configure(on: true, [Signature]);
        expander.Configure(on: false, [Signature]);

        Assert.False(hook.Installed);
    }

    // ---- Two threads ----
    //
    // In the app the hook calls its callback on a thread of its own, and everything else runs
    // on the UI thread. Here the test's own thread stands for the UI thread, and a second,
    // real thread for the hook's. It calls the fake hook only: no real hook, no real key.

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the action on another thread and waits for that thread to end, so this thread is
    /// blocked for as long as the action takes. Returns what the action threw, or null.
    /// </summary>
    private static Exception? OnAnotherThread(Action action)
    {
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(thread.Join(Limit), "the other thread did not end: it was waiting for this one");
        return thrown;
    }

    [Fact]
    public void A_keyword_typed_while_the_UI_thread_is_blocked_is_seen_and_only_handed_over()
    {
        SwitchOn();

        // This thread does nothing but wait while the keys arrive on the other one. Had a key
        // press needed anything from this thread, the other one would never have ended.
        Assert.Null(OnAnotherThread(() => Type(";sig")));

        // All that has happened is that the replacement was handed over: no clipboard, no
        // question about a window or a modifier key, no key pressed.
        Assert.Single(_posted);
        Assert.Empty(_events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, _typing.ModifierQuestions);
        Assert.Equal(0, WaitsOf(PasteService.InPlaceDelay));

        // The replacement is made here, once this thread is free again.
        RunPosted();
        WaitEnds(PasteService.InPlaceDelay);
        Assert.Equal(["4 Backspaces and Ctrl+V pressed in window 100"], KeyPresses());
    }

    [Fact]
    public void A_key_that_arrives_on_the_hooks_thread_while_the_UI_thread_is_blocked_calls_the_late_replacement_off()
    {
        SwitchOn();
        Type(";sig");

        // The UI thread is busy with something else and has not got to the posted work. The
        // owner types on meanwhile: that key is seen at once, on the other thread.
        Assert.Null(OnAnotherThread(() => Type("x")));

        // When the UI thread gets to the replacement, it reads how many keys the hook has
        // seen, finds one more than at the keyword, and touches nothing.
        RunPosted();
        Assert.Empty(_events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, WaitsOf(PasteService.InPlaceDelay));
    }

    [Fact]
    public void A_key_that_arrives_on_the_hooks_thread_in_the_moment_before_the_keys_calls_the_paste_off()
    {
        SwitchOn();
        Type(";sig");
        RunPosted();
        Assert.Equal("Regards, Walrus", _clipboard.Text);

        Assert.Null(OnAnotherThread(() => Type("x")));

        WaitEnds(PasteService.InPlaceDelay);
        Assert.Empty(KeyPresses());
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void Setting_up_is_refused_on_any_thread_but_the_one_the_expander_was_made_on()
    {
        SwitchOn();
        Action[] members =
        [
            () => _expander.Configure(on: true, [Signature]),
            () => _expander.Configure(on: false, []),
            () => _expander.UseLibrary([Signature]),
            () => _expander.Dispose(),
        ];

        foreach (var member in members)
        {
            var refused = Assert.IsType<InvalidOperationException>(OnAnotherThread(member));
            Assert.Equal(AutoExpander.WrongThread, refused.Message);
        }

        // Nothing was changed by any of them, and auto-expand goes on as it was.
        Assert.Empty(_events);
        Assert.Empty(_reports);
        Assert.True(_hook.Installed);
        Assert.True(_expander.IsOn);
        TypeAndLetSettle(";sig");
        Assert.Single(KeyPresses());
    }

    [Fact]
    public void A_replacement_refuses_to_run_on_the_hooks_thread()
    {
        // A post that runs the work where it stands, instead of handing it to the UI thread,
        // is a mistake in the wiring. The replacement then finds itself on the hook's thread
        // and stops before it has asked for a window, a modifier key or the clipboard. The
        // failure goes the way of any failure inside the callback: nothing leaves it.
        var reports = new List<(string Message, Exception? Error)>();
        var hook = new FakeHook([]);
        var typing = new FakeTyping(_events);
        var expander = new AutoExpander(
            hook,
            typing,
            _paste,
            _clock,
            action => action(),
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (message, error) => reports.Add((message, error)),
            FarEast);
        expander.Configure(on: true, [Signature]);
        reports.Clear();
        _waits.Clear();

        var thrown = OnAnotherThread(() =>
        {
            foreach (char character in ";sig")
            {
                hook.Press(Letter(character));
            }
        });

        Assert.Null(thrown);
        Assert.Empty(_events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Equal(0, typing.ModifierQuestions);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        var report = Assert.Single(reports);
        Assert.Equal("A key press could not be looked at for snippet auto-expand", report.Message);
        Assert.Equal(AutoExpander.WrongThread, Assert.IsType<InvalidOperationException>(report.Error).Message);
    }

    [Fact]
    public void A_key_that_reaches_a_hook_that_was_removed_is_not_looked_at_and_does_not_disturb_the_new_hook()
    {
        SwitchOn();
        Type(";si");
        _expander.Configure(on: true, [Signature]);
        var removed = _hook.Given[0];

        // The hook that was removed is given the keyword's last key after all.
        removed(Letter('g'));
        Assert.Empty(_posted);

        // The new hook has its own typed characters and its own count of keys: a key that
        // reaches the old one neither breaks a keyword typed into the new one nor counts as
        // typing on.
        Type(";si");
        removed(Letter('x'));
        Type("g");
        Assert.Single(_posted);
        removed(Letter('x'));
        RunPosted();
        WaitEnds(PasteService.InPlaceDelay);
        Assert.Single(KeyPresses());
    }

    [Fact]
    public void What_a_hook_kept_is_cleared_once_the_hook_has_been_removed()
    {
        SwitchOn();
        Type(";si");
        var kept = BufferOf(_hook.Given[0]);
        Assert.Equal(3, kept.Length);

        // Remove returns, which says that the hook's thread has ended: what that thread kept
        // is now this thread's to clear.
        _expander.Configure(on: false, [Signature]);

        Assert.Equal(["hook removed"], _events);
        Assert.Equal(0, kept.Length);
        Assert.True(kept.IsBlank);
    }

    [Fact]
    public void A_hook_that_could_not_be_removed_clears_what_it_kept_itself_at_the_next_key_it_reports()
    {
        SwitchOn();
        Type(";si");
        var left = _hook.Given[0];
        var kept = BufferOf(left);
        _hook.RemoveFails = new TimeoutException("made up");

        _expander.Configure(on: false, [Signature]);

        // That hook's thread may still be running, so this thread has left what it kept alone.
        Assert.Equal(3, kept.Length);

        // The next key that hook reports is dropped, and the thread that reports it clears
        // what was kept before it returns.
        left(Letter('g'));

        Assert.Empty(_posted);
        Assert.Equal(0, kept.Length);
        Assert.True(kept.IsBlank);
    }

    [Fact]
    public void A_hook_that_could_not_be_removed_lets_go_of_a_dead_key_at_the_next_key_it_reports()
    {
        SwitchOn();
        _hook.Press(DeadKey());
        var left = _hook.Given[0];
        Assert.True(AfterDeadKey(left));
        _hook.RemoveFails = new TimeoutException("made up");

        _expander.Configure(on: false, [Signature]);
        Assert.True(AfterDeadKey(left));

        left(Letter('x'));

        Assert.False(AfterDeadKey(left));
    }

    [Fact]
    public void A_keyword_seen_by_a_hook_that_has_been_renewed_since_is_left_as_typed()
    {
        SwitchOn();
        Type(";sig");
        Assert.Single(_posted);

        // No key was seen in the quiet seconds, so the hook was taken out and put in again
        // before the UI thread got to the posted work. Keys may have been missed in between.
        RenewalComesRound();
        _events.Clear();

        RunPosted();
        Assert.Empty(_events);
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void The_library_can_be_loaded_again_and_again_while_keys_arrive_on_the_hooks_thread()
    {
        var address = Snippet("address", ";addr", "1 Otter Way");
        SwitchOn();
        using var go = new ManualResetEventSlim(false);
        Exception? thrown = null;
        var hookThread = new Thread(() =>
        {
            try
            {
                go.Wait();

                // Letters only: no keyword is ever finished, so nothing is handed over.
                for (int i = 0; i < 20_000; i++)
                {
                    _hook.Press(Letter((char)('a' + (i % 26))));
                }
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        })
        {
            IsBackground = true,
        };
        hookThread.Start();
        go.Set();

        // Each of these swaps a new set of keywords in, with a longest keyword of another
        // length, while the other thread is adding to what it keeps.
        for (int i = 0; i < 2_000; i++)
        {
            _expander.UseLibrary(i % 2 == 0 ? [Signature, address] : [Signature]);
        }

        Assert.True(hookThread.Join(Limit), "the other thread did not end");
        Assert.Null(thrown);

        // No failure was handed over from inside the callback, and the keywords are the last
        // ones swapped in.
        Assert.Empty(_posted);
        Type(";addr");
        Assert.Empty(_posted);
        Type(";sig");
        Assert.Single(_posted);
    }
}
