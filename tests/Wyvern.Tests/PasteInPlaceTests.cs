using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;

namespace Wyvern.Tests;

/// <summary>
/// The paste that takes the place of a keyword the owner has just typed, a paste that its
/// target calls off, and the text the paste service says the owner has on the clipboard.
/// Everything here runs against fakes. No test presses a key, moves the focus or touches the
/// real clipboard; the waits are a list the test works through by hand.
/// </summary>
public class PasteInPlaceTests
{
    // The launcher's own window. A paste in place must never go to it or ask anything of it.
    private sealed class LauncherTarget(List<string> events) : IPasteTarget
    {
        public bool Activate()
        {
            events.Add("focus given back to the launcher's window");
            return true;
        }

        public void SendPaste() => events.Add("Ctrl+V pressed for the launcher's window");
    }

    // The window a keyword was typed in.
    private sealed class TypedTarget(List<string> events, string name = "the typed window") : IPasteTarget
    {
        public bool StillInFront { get; set; } = true;

        public Exception? AskingFails { get; set; }

        public Exception? KeyPressFails { get; set; }

        /// <summary>
        /// Run when the keys are asked for, before anything else is done for them: what happens
        /// elsewhere after the paste service has looked at the clipboard and before the target
        /// answers.
        /// </summary>
        public Action? BeforeKeys { get; set; }

        /// <summary>How many times the keys were asked for, whether or not they were pressed.</summary>
        public int SendPasteCalls { get; private set; }

        public bool Activate()
        {
            events.Add($"asked whether {name} is in front");
            if (AskingFails is not null)
            {
                throw AskingFails;
            }

            return StillInFront;
        }

        public void SendPaste()
        {
            SendPasteCalls++;
            BeforeKeys?.Invoke();
            if (KeyPressFails is not null)
            {
                throw KeyPressFails;
            }

            events.Add($"keyword taken away and Ctrl+V pressed in {name}");
        }
    }

    private const string Earlier = "what I copied earlier";
    private const string Pressed = "keyword taken away and Ctrl+V pressed in the typed window";

    // The one line the look before the keys leaves in the log when it cannot read the clipboard.
    private const string LookFailed =
        "The clipboard could not be read before the keys of a paste in place, so no key was pressed";

    private const string PutBackFailed = "The clipboard could not be put back after a paste";

    private readonly List<string> _events = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly List<string> _notes = [];
    private readonly List<(string Message, Exception Error)> _reports = [];
    private readonly FakeClipboard _clipboard;
    private readonly TypedTarget _typed;
    private readonly PasteService _paste;

    public PasteInPlaceTests()
    {
        _clipboard = new FakeClipboard(_events) { Text = Earlier };
        _typed = new TypedTarget(_events);
        _paste = new PasteService(
            _clipboard,
            new LauncherTarget(_events),
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (message, error) => _reports.Add((message, error)));
    }

    /// <summary>Lets the oldest wait run out.</summary>
    private void NextWaitEnds()
    {
        var wait = _waits[0];
        _waits.RemoveAt(0);
        wait.Action();
    }

    /// <summary>How many times the clipboard was read and answered (a read that failed is not counted).</summary>
    private int Reads => _events.Count(entry => entry == "clipboard read");

    /// <summary>An entry of the clipboard history that is text and nothing else.</summary>
    private static IReadOnlyList<ClipboardBlock> TextOnly(string text) =>
        [new(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(text))];

    [Fact]
    public void The_whole_sequence_in_order()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to the snippet",
                "asked whether the typed window is in front",
                "clipboard read",
                Pressed,
                "clipboard read",
                "clipboard set to " + Earlier,
            ],
            _events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void The_keys_are_pressed_a_moment_after_the_text_went_on_the_clipboard_as_for_any_paste()
    {
        // The programs that watch the clipboard open it as soon as it changes. Pressed at once,
        // the keys could reach a program that then finds the clipboard taken and pastes nothing.
        // The moment has a name of its own, a tenth of a second today, so that it can be
        // changed without touching the launcher's paste.
        _paste.PasteInPlace(_typed, "the snippet");

        Assert.Equal(["clipboard read", "clipboard set to the snippet", "asked whether the typed window is in front"], _events);
        Assert.Equal(PasteService.InPlaceDelay, Assert.Single(_waits).After);
        Assert.Equal(TimeSpan.FromMilliseconds(100), PasteService.InPlaceDelay);

        NextWaitEnds();
        Assert.Equal(Pressed, _events[^1]);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal(PasteService.RestoreDelay, Assert.Single(_waits).After);
    }

    [Fact]
    public void The_launchers_own_window_is_never_asked_or_pasted_into()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();
        NextWaitEnds();

        Assert.DoesNotContain(_events, entry => entry.Contains("launcher"));
    }

    [Fact]
    public void An_empty_text_is_not_pasted_and_nothing_is_touched()
    {
        _paste.PasteInPlace(_typed, "");

        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Equal(0, _clipboard.Calls);
    }

    [Fact]
    public void When_the_clipboard_held_no_text_it_is_left_holding_what_was_pasted()
    {
        _clipboard.Text = null;

        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal(Pressed, _events[^1]);
    }

    [Fact]
    public void Something_the_owner_copies_during_the_wait_is_not_overwritten()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();
        _clipboard.Text = "copied in the meantime";
        NextWaitEnds();

        Assert.Equal("copied in the meantime", _clipboard.Text);
    }

    // ---- A paste that its target calls off ----

    [Fact]
    public void A_paste_that_is_called_off_presses_nothing_puts_the_clipboard_back_at_once_and_says_nothing()
    {
        _typed.KeyPressFails = new PasteCalledOffException();

        _paste.PasteInPlace(_typed, "the snippet");
        Assert.Equal("the snippet", _clipboard.Text);
        NextWaitEnds();

        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to the snippet",
                "asked whether the typed window is in front",
                "clipboard read",
                "clipboard read",
                "clipboard set to " + Earlier,
            ],
            _events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_paste_that_is_called_off_does_not_overwrite_what_the_owner_copied_meanwhile()
    {
        // The clipboard is as the paste left it at the look, so the target is asked for the
        // keys, and calls the paste off. By then the owner has copied something else.
        _typed.BeforeKeys = () => _clipboard.Text = "copied in the meantime";
        _typed.KeyPressFails = new PasteCalledOffException();

        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();

        Assert.Equal(1, _typed.SendPasteCalls);
        Assert.Equal("copied in the meantime", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_paste_that_is_called_off_leaves_the_text_when_the_clipboard_held_none_before()
    {
        // What the clipboard held (a picture, files or nothing) could not be remembered, so
        // there is nothing to put back.
        _clipboard.Text = null;
        _typed.KeyPressFails = new PasteCalledOffException();

        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();

        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void After_a_paste_that_was_called_off_the_next_one_starts_from_the_owners_text()
    {
        _typed.KeyPressFails = new PasteCalledOffException();
        _paste.PasteInPlace(_typed, "the first");
        NextWaitEnds();
        _typed.KeyPressFails = null;

        _paste.PasteInPlace(_typed, "the second");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Single(_events, entry => entry == Pressed);
    }

    [Fact]
    public void Any_failure_to_read_the_clipboard_at_the_look_counts_as_one_that_cannot_be_read_and_the_paste_is_called_off()
    {
        // Not only a clipboard that another program has open: whatever the read throws.
        var failure = new InvalidOperationException("made up");
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.Broken = failure;

        NextWaitEnds();

        // The look reports its own failure first, and the putting back, which cannot read the
        // clipboard either, reports its own after it. Nothing is shown to the owner.
        Assert.Equal(0, _typed.SendPasteCalls);
        Assert.Equal([LookFailed, PutBackFailed], _reports.Select(report => report.Message));
        Assert.All(_reports, report => Assert.Same(failure, report.Error));
        Assert.Empty(_notes);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_put_back_after_a_paste_was_called_off_is_reported_and_nothing_is_thrown()
    {
        // Intact at the look, so the target is asked for the keys and calls the paste off.
        // Then the clipboard goes wrong, and the putting back cannot read it.
        var failure = new InvalidOperationException("made up");
        _typed.BeforeKeys = () => _clipboard.Broken = failure;
        _typed.KeyPressFails = new PasteCalledOffException();
        _paste.PasteInPlace(_typed, "the snippet");

        NextWaitEnds();

        Assert.Equal(1, _typed.SendPasteCalls);
        var report = Assert.Single(_reports);
        Assert.Equal(PutBackFailed, report.Message);
        Assert.Same(failure, report.Error);
        Assert.Empty(_notes);
        Assert.Empty(_waits);
    }

    // ---- The look at the clipboard before the keys of a paste in place ----

    [Fact]
    public void Another_text_on_the_clipboard_when_the_keys_are_due_calls_the_paste_off_and_that_text_is_left_alone()
    {
        const string other = "put there by another program";
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.Text = other;

        NextWaitEnds();

        // The keys would have taken the keyword away and pasted the other text in its place.
        Assert.Equal(0, _typed.SendPasteCalls);
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to the snippet",
                "asked whether the typed window is in front",
                "clipboard read",   // the look before the keys
                "clipboard read",   // the putting back, which finds another text and leaves it
            ],
            _events);
        Assert.Equal(other, _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
        Assert.Equal(other, _paste.OwnerText());
    }

    [Fact]
    public void A_clipboard_with_no_text_when_the_keys_are_due_calls_the_paste_off_and_says_nothing()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.Text = null;

        NextWaitEnds();

        Assert.Equal(0, _typed.SendPasteCalls);
        Assert.Null(_clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
        Assert.Null(_paste.OwnerText());
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_when_the_keys_are_due_calls_the_paste_off_and_leaves_one_line_in_the_log()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.BusyFor = 1;

        NextWaitEnds();

        // The look found the clipboard busy: no key, and one line in the log, with the
        // exception. The putting back could read it again, saw the snippet there, and put the
        // owner's text back, which it says nothing of.
        Assert.Equal(0, _typed.SendPasteCalls);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        var report = Assert.Single(_reports);
        Assert.Equal(LookFailed, report.Message);
        Assert.IsType<ClipboardBusyException>(report.Error);

        // Fixed words only: no semicolon, and nothing of any text.
        Assert.DoesNotContain(";", report.Message);
        Assert.DoesNotContain("snippet", report.Message);
        Assert.DoesNotContain(Earlier, report.Message);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_when_the_keys_are_due_is_reported_also_when_the_paste_remembered_no_text()
    {
        // With no text remembered the putting back returns before it reads anything, so the
        // look's line is the only one, and the only thing that says why nothing was pasted.
        _clipboard.Text = null;
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.BusyFor = 1;

        NextWaitEnds();

        Assert.Equal(0, _typed.SendPasteCalls);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        var report = Assert.Single(_reports);
        Assert.Equal(LookFailed, report.Message);
        Assert.IsType<ClipboardBusyException>(report.Error);
    }

    [Fact]
    public void A_clipboard_that_still_cannot_be_read_for_the_putting_back_is_reported_a_second_time_and_the_snippet_stays()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.BusyFor = 2;

        NextWaitEnds();

        // The look and the putting back both found the clipboard busy. The look's line comes
        // first, then the putting back's own, as for any paste. Nothing is shown to the owner.
        Assert.Equal(0, _typed.SendPasteCalls);
        Assert.Equal(4, _clipboard.Calls);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Equal([LookFailed, PutBackFailed], _reports.Select(report => report.Message));
        Assert.All(_reports, report => Assert.IsType<ClipboardBusyException>(report.Error));
    }

    [Fact]
    public void A_clipboard_that_is_as_the_paste_left_it_is_looked_at_once_before_the_keys_which_are_pressed_once()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();

        Assert.Equal(1, _typed.SendPasteCalls);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal(PasteService.RestoreDelay, Assert.Single(_waits).After);

        NextWaitEnds();

        // Three reads in all: the owner's text at the start, the look, and the putting back.
        // The look comes before the target is asked for the keys, never in between its own looks
        // and its key press.
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to the snippet",
                "asked whether the typed window is in front",
                "clipboard read",
                Pressed,
                "clipboard read",
                "clipboard set to " + Earlier,
            ],
            _events);
        Assert.Equal(3, Reads);
        Assert.Equal(5, _clipboard.Calls);
        Assert.Equal(1, _typed.SendPasteCalls);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    // ---- Any other way the key press does not happen ----

    [Fact]
    public void When_the_window_is_not_in_front_as_the_paste_starts_no_key_is_pressed_and_the_owner_is_told()
    {
        _typed.StillInFront = false;

        _paste.PasteInPlace(_typed, "the snippet");

        Assert.Equal(["clipboard read", "clipboard set to the snippet", "asked whether the typed window is in front"], _events);
        Assert.Empty(_waits);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal([PasteService.NoWindowNote], _notes);
    }

    [Fact]
    public void A_target_that_throws_when_asked_counts_as_no_window_and_is_reported()
    {
        _typed.AskingFails = new InvalidOperationException("made up");

        _paste.PasteInPlace(_typed, "the snippet");

        Assert.Empty(_waits);
        Assert.Equal([PasteService.NoWindowNote], _notes);
        Assert.Equal("The window to paste into could not be reached", Assert.Single(_reports).Message);
        Assert.DoesNotContain(_events, entry => entry.Contains("Ctrl+V"));
    }

    [Fact]
    public void When_the_key_press_fails_the_text_stays_on_the_clipboard_and_the_owner_is_told()
    {
        // The Backspaces go with the Ctrl+V, so the keyword is not taken away either.
        _typed.KeyPressFails = new InvalidOperationException("made up");

        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal([PasteService.NotPastedNote], _notes);
        var report = Assert.Single(_reports);
        Assert.Equal("The paste key press could not be sent", report.Message);
        Assert.Same(_typed.KeyPressFails, report.Error);
    }

    [Fact]
    public void A_busy_clipboard_ends_the_paste_before_anything_waits()
    {
        _clipboard.BusyFor = 1;

        Assert.Throws<ClipboardBusyException>(() => _paste.PasteInPlace(_typed, "the snippet"));

        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_written_ends_the_paste_before_anything_waits()
    {
        // The read goes through and the write does not.
        var clipboard = new WriteRefusingClipboard { Text = Earlier };
        var paste = new PasteService(clipboard, new LauncherTarget(_events), (after, action) => _waits.Add((after, action)), _notes.Add);

        Assert.Throws<ClipboardBusyException>(() => paste.PasteInPlace(_typed, "the snippet"));

        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Equal(Earlier, clipboard.Text);
    }

    private sealed class WriteRefusingClipboard : IClipboard
    {
        public string? Text { get; set; }

        public string? GetText() => Text;

        public void SetText(string text) => throw new ClipboardBusyException();

        public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks) => throw new ClipboardBusyException();
    }

    // ---- One paste after another ----

    [Fact]
    public void Two_pastes_in_place_one_after_the_other_put_the_owners_text_back_once()
    {
        _paste.PasteInPlace(_typed, "the first");
        NextWaitEnds();                              // the first one's keys
        _paste.PasteInPlace(_typed, "the second");   // while the first waits to put the text back

        Assert.Equal("the second", _clipboard.Text);

        // The first paste's wait runs out: it has been overtaken and puts nothing back.
        NextWaitEnds();
        Assert.Equal("the second", _clipboard.Text);

        NextWaitEnds();                              // the second one's keys
        NextWaitEnds();                              // and its putting back
        Assert.Equal(2, _events.Count(entry => entry == Pressed));
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_second_paste_in_place_overtakes_a_first_whose_keys_were_not_pressed_yet()
    {
        _paste.PasteInPlace(_typed, "the first");
        _paste.PasteInPlace(_typed, "the second");

        NextWaitEnds();   // the first one's keys: overtaken, nothing is pressed
        Assert.DoesNotContain(Pressed, _events);

        NextWaitEnds();   // the second one's keys
        NextWaitEnds();   // and its putting back
        Assert.Single(_events, entry => entry == Pressed);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_paste_in_place_overtakes_a_paste_from_the_launcher_that_was_still_settling()
    {
        _paste.Paste("from the launcher");
        _paste.PasteInPlace(_typed, "typed");

        NextWaitEnds();   // the launcher's key press: overtaken
        Assert.DoesNotContain("Ctrl+V pressed for the launcher's window", _events);

        NextWaitEnds();
        NextWaitEnds();
        Assert.Contains(Pressed, _events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_paste_from_the_launcher_that_follows_a_paste_in_place_keeps_the_owners_text()
    {
        _paste.PasteInPlace(_typed, "typed");
        NextWaitEnds();   // the typed paste's keys
        _paste.Paste("from the launcher");

        NextWaitEnds();   // the typed paste's putting back: overtaken
        NextWaitEnds();   // the launcher's key press
        NextWaitEnds();   // the launcher's putting back

        Assert.Contains("Ctrl+V pressed for the launcher's window", _events);
        Assert.Equal(Earlier, _clipboard.Text);
        Assert.Empty(_waits);
    }

    [Fact]
    public void Each_paste_in_place_presses_its_keys_in_its_own_window()
    {
        var other = new TypedTarget(_events, "another window");

        _paste.PasteInPlace(_typed, "the first");
        NextWaitEnds();
        _paste.PasteInPlace(other, "the second");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Contains(Pressed, _events);
        Assert.Contains("keyword taken away and Ctrl+V pressed in another window", _events);
    }

    [Fact]
    public void A_paste_in_place_that_follows_a_clipboard_history_paste_puts_the_text_of_the_entry_back()
    {
        _paste.PasteAndKeep(TextOnly("from history"));
        NextWaitEnds();   // the entry's keys: it puts nothing back and is over

        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();   // the keys
        NextWaitEnds();   // the putting back

        // The entry is what the clipboard held before the snippet, so it is what goes back.
        Assert.Equal("from history", _clipboard.Text);
        Assert.Single(_events, entry => entry == Pressed);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_clipboard_history_paste_that_follows_a_paste_in_place_keeps_the_entry_on_the_clipboard()
    {
        // The entry is the very text of the snippet: a putting back that was not overtaken
        // would find the text it looks for and put the owner's text over the entry.
        var entry = TextOnly("the snippet");
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();   // the keys
        _paste.PasteAndKeep(entry);

        NextWaitEnds();   // the paste in place's putting back: overtaken
        NextWaitEnds();   // the entry's keys, pressed in the launcher's window

        Assert.Same(entry, _clipboard.Blocks);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.DoesNotContain("clipboard set to " + Earlier, _events);
        Assert.Contains("Ctrl+V pressed for the launcher's window", _events);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    // ---- The text the owner has on the clipboard ----

    [Fact]
    public void The_owners_text_is_what_the_clipboard_holds_when_no_paste_is_waiting()
    {
        Assert.Equal(Earlier, _paste.OwnerText());

        _clipboard.Text = null;
        Assert.Null(_paste.OwnerText());
    }

    [Fact]
    public void While_a_paste_is_under_way_the_owners_text_is_the_remembered_one()
    {
        _paste.PasteInPlace(_typed, "the snippet");

        // The clipboard holds the snippet now. That is not what the owner copied.
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal(Earlier, _paste.OwnerText());

        NextWaitEnds();   // the keys
        Assert.Equal(Earlier, _paste.OwnerText());
    }

    [Fact]
    public void While_a_paste_from_the_launcher_is_under_way_the_owners_text_is_the_remembered_one_too()
    {
        _paste.Paste("the snippet");
        Assert.Equal(Earlier, _paste.OwnerText());

        NextWaitEnds();   // the key press
        Assert.Equal(Earlier, _paste.OwnerText());
    }

    [Fact]
    public void While_a_clipboard_history_paste_is_waiting_the_owners_text_is_what_the_clipboard_holds()
    {
        // The entry went on the clipboard because the owner chose it, so it is what the owner
        // has. Nothing is remembered for it to put back.
        _paste.PasteAndKeep(TextOnly("from history"));
        Assert.Equal("from history", _paste.OwnerText());

        NextWaitEnds();   // the keys
        Assert.Equal("from history", _paste.OwnerText());
    }

    [Fact]
    public void What_the_owner_copied_since_the_paste_is_the_owners_text()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        _clipboard.Text = "copied in the meantime";

        Assert.Equal("copied in the meantime", _paste.OwnerText());
    }

    [Fact]
    public void When_the_clipboard_held_no_text_before_the_paste_the_owner_has_none()
    {
        _clipboard.Text = null;
        _paste.PasteInPlace(_typed, "the snippet");

        Assert.Null(_paste.OwnerText());
    }

    [Fact]
    public void Once_the_earlier_text_is_back_the_owners_text_is_what_the_clipboard_holds()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Equal(Earlier, _paste.OwnerText());
    }

    [Fact]
    public void Asking_for_the_owners_text_changes_nothing_and_the_earlier_text_is_still_put_back()
    {
        _paste.PasteInPlace(_typed, "the snippet");
        NextWaitEnds();

        _paste.OwnerText();
        _paste.OwnerText();
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Single(_waits);

        NextWaitEnds();
        Assert.Equal(Earlier, _clipboard.Text);
    }

    [Fact]
    public void A_busy_clipboard_is_passed_on_by_the_question_for_the_owners_text()
    {
        _clipboard.BusyFor = 1;

        Assert.Throws<ClipboardBusyException>(() => _paste.OwnerText());
    }

    [Fact]
    public void A_second_snippet_that_uses_the_clipboard_gets_the_owners_text_and_not_the_first_snippet()
    {
        // What auto-expand does for two keywords typed one straight after the other: it asks
        // for the owner's text, fills that in, and pastes.
        _paste.PasteInPlace(_typed, "first snippet");
        NextWaitEnds();
        string second = "second: " + _paste.OwnerText();
        _paste.PasteInPlace(_typed, second);

        Assert.Equal("second: " + Earlier, _clipboard.Text);
        NextWaitEnds();
        NextWaitEnds();
        NextWaitEnds();
        Assert.Equal(Earlier, _clipboard.Text);
    }

    // ---- The launcher's own pastes are as they were ----

    [Fact]
    public void A_paste_from_the_launcher_does_not_look_at_the_clipboard_before_its_key_press_and_presses_it_whatever_is_there()
    {
        // The look before the keys is for a paste in place only. The launcher's paste presses
        // its key as it always did, and reads the clipboard twice: at the start, and to put
        // the owner's text back.
        _paste.Paste("from the launcher");
        _clipboard.Text = "put there by another program";

        NextWaitEnds();

        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to from the launcher",
                "focus given back to the launcher's window",
                "Ctrl+V pressed for the launcher's window",
            ],
            _events);
        Assert.Equal(1, Reads);

        NextWaitEnds();

        Assert.Equal(2, Reads);
        Assert.Equal("put there by another program", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_clipboard_history_paste_does_not_look_at_the_clipboard_before_its_key_press_either()
    {
        _paste.PasteAndKeep(TextOnly("from history"));
        _clipboard.Text = "put there by another program";

        NextWaitEnds();

        // Not read at all, as before: the one call to the clipboard was the write.
        Assert.Equal(
            [
                "clipboard set to 1 blocks",
                "focus given back to the launcher's window",
                "Ctrl+V pressed for the launcher's window",
            ],
            _events);
        Assert.Equal(1, _clipboard.Calls);
        Assert.Equal(0, Reads);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_paste_from_the_launcher_that_its_target_calls_off_puts_the_clipboard_back_too()
    {
        // The launcher's target never does this, but the rule is the paste service's and holds
        // for whichever target says so.
        var events = new List<string>();
        var clipboard = new FakeClipboard(events) { Text = Earlier };
        var target = new TypedTarget(events) { KeyPressFails = new PasteCalledOffException() };
        var paste = new PasteService(clipboard, target, (after, action) => _waits.Add((after, action)), _notes.Add);

        paste.Paste("the snippet");
        NextWaitEnds();

        Assert.Equal(Earlier, clipboard.Text);
        Assert.Empty(_notes);
        Assert.Empty(_waits);
    }
}
