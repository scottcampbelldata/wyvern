using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Everything here runs against fakes. No test presses a key, moves the focus or touches the
/// real clipboard; the waits are a list the test works through by hand.
/// </summary>
public class PasteServiceTests
{
    private sealed class FakeTarget(List<string> events) : IPasteTarget
    {
        public bool HasWindow { get; set; } = true;

        public Exception? ActivateFails { get; set; }

        public Exception? KeyPressFails { get; set; }

        public bool Activate()
        {
            events.Add("focus given back");
            if (ActivateFails is not null)
            {
                throw ActivateFails;
            }

            return HasWindow;
        }

        public void SendPaste()
        {
            if (KeyPressFails is not null)
            {
                throw KeyPressFails;
            }

            events.Add("Ctrl+V pressed");
        }
    }

    private readonly List<string> _events = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly List<string> _notes = [];
    private readonly List<(string Message, Exception Error)> _reports = [];
    private readonly FakeClipboard _clipboard;
    private readonly FakeTarget _target;
    private readonly PasteService _paste;

    public PasteServiceTests()
    {
        _clipboard = new FakeClipboard(_events) { Text = "what I copied earlier" };
        _target = new FakeTarget(_events);
        _paste = new PasteService(
            _clipboard,
            _target,
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

    [Fact]
    public void The_whole_sequence_in_order()
    {
        _paste.Paste("the snippet");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to the snippet",
                "focus given back",
                "Ctrl+V pressed",
                "clipboard read",
                "clipboard set to what I copied earlier",
            ],
            _events);
        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void The_key_is_not_pressed_until_the_focus_has_had_a_moment_to_settle()
    {
        _paste.Paste("the snippet");

        Assert.Equal(["clipboard read", "clipboard set to the snippet", "focus given back"], _events);
        Assert.Equal(PasteService.SettleDelay, Assert.Single(_waits).After);
    }

    [Fact]
    public void The_earlier_text_is_not_put_back_until_the_program_has_had_time_to_read_the_clipboard()
    {
        _paste.Paste("the snippet");
        NextWaitEnds();

        Assert.Equal("Ctrl+V pressed", _events[^1]);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal(PasteService.RestoreDelay, Assert.Single(_waits).After);
    }

    [Fact]
    public void When_the_clipboard_held_no_text_it_is_left_holding_what_was_pasted()
    {
        _clipboard.Text = null;

        _paste.Paste("the snippet");
        NextWaitEnds();
        NextWaitEnds();

        Assert.Equal(["clipboard read", "clipboard set to the snippet", "focus given back", "Ctrl+V pressed"], _events);
        Assert.Equal("the snippet", _clipboard.Text);
    }

    [Fact]
    public void With_no_window_to_paste_into_the_text_stays_on_the_clipboard_and_the_owner_is_told()
    {
        _target.HasWindow = false;

        _paste.Paste("the snippet");

        Assert.Equal(["clipboard read", "clipboard set to the snippet", "focus given back"], _events);
        Assert.Empty(_waits);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal([PasteService.NoWindowNote], _notes);
    }

    [Fact]
    public void Something_the_owner_copies_during_the_wait_is_not_overwritten()
    {
        _paste.Paste("the snippet");
        NextWaitEnds();
        _clipboard.Text = "copied just now";

        NextWaitEnds();

        Assert.Equal("copied just now", _clipboard.Text);
    }

    [Fact]
    public void A_second_paste_before_the_first_has_finished_still_ends_with_the_owners_own_text()
    {
        _paste.Paste("first snippet");
        NextWaitEnds();
        Assert.Equal("first snippet", _clipboard.Text);

        // The clipboard holds the first snippet now. That must not be taken for the owner's text.
        _paste.Paste("second snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Equal(2, _events.Count(e => e == "Ctrl+V pressed"));
        // The first paste's putting back was overtaken and did nothing: the text went back once.
        Assert.Equal(1, _events.Count(e => e == "clipboard set to what I copied earlier"));
        Assert.Equal("clipboard set to what I copied earlier", _events[^1]);
    }

    [Fact]
    public void A_second_paste_before_the_first_key_press_replaces_it()
    {
        _paste.Paste("first snippet");
        _paste.Paste("second snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        Assert.Equal(1, _events.Count(e => e == "Ctrl+V pressed"));
        Assert.Equal("what I copied earlier", _clipboard.Text);
    }

    [Fact]
    public void A_second_paste_keeps_nothing_back_when_the_clipboard_held_no_text_to_begin_with()
    {
        _clipboard.Text = null;
        _paste.Paste("first snippet");
        NextWaitEnds();

        _paste.Paste("second snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        Assert.Equal("second snippet", _clipboard.Text);
    }

    [Fact]
    public void A_second_paste_does_not_overwrite_what_the_owner_copied_after_the_first()
    {
        _paste.Paste("first snippet");
        NextWaitEnds();

        // The clipboard no longer holds the first snippet: this is newer than anything remembered.
        _clipboard.Text = "copied just now";
        _paste.Paste("second snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        Assert.Equal("copied just now", _clipboard.Text);
    }

    [Fact]
    public void The_same_snippet_pasted_twice_in_quick_succession_puts_the_earlier_text_back_once()
    {
        _paste.Paste("the snippet");
        NextWaitEnds();
        _paste.Paste("the snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        // If the first paste's putting back had not been overtaken, it would have run before the
        // second key press and the second paste would have pasted the earlier text.
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to the snippet",
                "focus given back",
                "Ctrl+V pressed",
                "clipboard read",
                "clipboard set to the snippet",
                "focus given back",
                "Ctrl+V pressed",
                "clipboard read",
                "clipboard set to what I copied earlier",
            ],
            _events);
        Assert.Equal("what I copied earlier", _clipboard.Text);
    }

    [Fact]
    public void An_empty_text_does_nothing_at_all()
    {
        _paste.Paste("");

        Assert.Equal(0, _clipboard.Calls);
        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
        Assert.Equal("what I copied earlier", _clipboard.Text);
    }

    [Fact]
    public void A_window_that_cannot_be_reached_is_treated_as_no_window()
    {
        _target.ActivateFails = new InvalidOperationException("private words from whatever failed");

        _paste.Paste("the snippet");

        Assert.Equal(["clipboard read", "clipboard set to the snippet", "focus given back"], _events);
        Assert.Empty(_waits);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal([PasteService.NoWindowNote], _notes);
        var report = Assert.Single(_reports);
        Assert.Equal("The window to paste into could not be reached", report.Message);
        Assert.Same(_target.ActivateFails, report.Error);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_fails_the_paste_before_anything_else_happens()
    {
        _clipboard.BusyFor = 1;

        Assert.Throws<ClipboardBusyException>(() => _paste.Paste("the snippet"));

        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Equal("what I copied earlier", _clipboard.Text);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_written_fails_the_paste_before_the_focus_moves()
    {
        var events = new List<string>();
        var clipboard = new WriteRefusingClipboard(events);
        var target = new FakeTarget(events);
        var paste = new PasteService(clipboard, target, (after, action) => _waits.Add((after, action)), _notes.Add);

        Assert.Throws<ClipboardBusyException>(() => paste.Paste("the snippet"));

        Assert.Equal(["clipboard read"], events);
        Assert.Empty(_waits);
    }

    private sealed class WriteRefusingClipboard(List<string> events) : IClipboard
    {
        public string? GetText()
        {
            events.Add("clipboard read");
            return "what I copied earlier";
        }

        public void SetText(string text) => throw new ClipboardBusyException();

        public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks) => throw new ClipboardBusyException();
    }

    [Fact]
    public void A_key_press_that_fails_is_reported_and_the_text_is_left_on_the_clipboard()
    {
        _target.KeyPressFails = new InvalidOperationException("private words from whatever failed");

        _paste.Paste("the snippet");
        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Equal("the snippet", _clipboard.Text);
        Assert.Equal([PasteService.NotPastedNote], _notes);
        var report = Assert.Single(_reports);
        Assert.Equal("The paste key press could not be sent", report.Message);
        Assert.Same(_target.KeyPressFails, report.Error);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_put_back_is_reported_and_nothing_is_thrown()
    {
        _paste.Paste("the snippet");
        NextWaitEnds();
        _clipboard.BusyFor = 1;

        NextWaitEnds();

        var report = Assert.Single(_reports);
        Assert.Equal("The clipboard could not be put back after a paste", report.Message);
        Assert.IsType<ClipboardBusyException>(report.Error);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_written_when_putting_back_is_reported_and_nothing_is_thrown()
    {
        var clipboard = new SecondWriteRefusingClipboard();
        var paste = new PasteService(
            clipboard,
            _target,
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (message, error) => _reports.Add((message, error)));
        paste.Paste("the snippet");
        NextWaitEnds();

        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Equal("the snippet", clipboard.Text);
        var report = Assert.Single(_reports);
        Assert.Equal("The clipboard could not be put back after a paste", report.Message);
        Assert.IsType<ClipboardBusyException>(report.Error);
        Assert.Empty(_notes);
    }

    /// <summary>Reads without trouble and takes the first write, but refuses every write after that.</summary>
    private sealed class SecondWriteRefusingClipboard : IClipboard
    {
        private int _writes;

        public string? Text { get; private set; } = "what I copied earlier";

        public string? GetText() => Text;

        public void SetText(string text)
        {
            if (++_writes > 1)
            {
                throw new ClipboardBusyException();
            }

            Text = text;
        }

        // Counts as a write like any other.
        public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks) => SetText("blocks");
    }

    [Fact]
    public void A_note_that_cannot_be_shown_does_not_escape_the_delayed_step_and_a_later_paste_still_works()
    {
        var paste = new PasteService(
            _clipboard,
            _target,
            (after, action) => _waits.Add((after, action)),
            _ => throw new InvalidOperationException("nowhere to show a note"),
            (message, error) => _reports.Add((message, error)));
        _target.KeyPressFails = new InvalidOperationException("boom");

        paste.Paste("the snippet");
        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Equal("The paste key press could not be sent", Assert.Single(_reports).Message);

        // The key press failed, so the clipboard still holds the first snippet. That is what the
        // second paste finds, and keeps.
        _target.KeyPressFails = null;
        paste.Paste("second snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        Assert.Equal(1, _events.Count(e => e == "Ctrl+V pressed"));
        Assert.Equal("the snippet", _clipboard.Text);
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_escape_the_putting_back()
    {
        var paste = new PasteService(
            _clipboard,
            _target,
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (_, _) => throw new InvalidOperationException("the log cannot be written"));
        paste.Paste("the snippet");
        NextWaitEnds();
        _clipboard.BusyFor = 1;

        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Equal("the snippet", _clipboard.Text);
    }

    [Fact]
    public void A_timer_that_cannot_be_set_for_the_putting_back_does_not_escape_and_leaves_no_paste_unfinished()
    {
        var timerFails = true;
        var paste = new PasteService(
            _clipboard,
            _target,
            (after, action) =>
            {
                if (timerFails && after == PasteService.RestoreDelay)
                {
                    timerFails = false;
                    throw new InvalidOperationException("no timer to be had");
                }

                _waits.Add((after, action));
            },
            _notes.Add,
            (message, error) => _reports.Add((message, error)));
        paste.Paste("the snippet");

        NextWaitEnds();

        Assert.Equal("Ctrl+V pressed", _events[^1]);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        var report = Assert.Single(_reports);
        Assert.Equal("The paste could not be finished", report.Message);
        Assert.IsType<InvalidOperationException>(report.Error);

        // Nothing is waiting to put anything back, so the clipboard is read afresh and holds the
        // first snippet. The earlier text remembered by the first paste is not taken for it.
        paste.Paste("second snippet");
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }

        Assert.Equal("the snippet", _clipboard.Text);
    }

    [Fact]
    public void What_is_pasted_and_what_was_there_never_appear_in_a_note_or_a_report()
    {
        const string pasted = "PASTED-MARKER-4471";
        const string earlier = "EARLIER-MARKER-9302";
        var timerFails = false;
        var paste = new PasteService(
            _clipboard,
            _target,
            (after, action) =>
            {
                if (timerFails && after == PasteService.RestoreDelay)
                {
                    throw new InvalidOperationException(pasted + earlier);
                }

                _waits.Add((after, action));
            },
            _notes.Add,
            (message, error) => _reports.Add((message, error)));

        // The exceptions carry the markers in their messages, as a careless caller's might.
        // No window.
        _clipboard.Text = earlier;
        _target.HasWindow = false;
        paste.Paste(pasted);

        // A window that cannot be reached.
        _clipboard.Text = earlier;
        _target.HasWindow = true;
        _target.ActivateFails = new InvalidOperationException(pasted + earlier);
        paste.Paste(pasted);
        _target.ActivateFails = null;

        // A key press that fails.
        _clipboard.Text = earlier;
        _target.KeyPressFails = new InvalidOperationException(pasted + earlier);
        paste.Paste(pasted);
        NextWaitEnds();
        _target.KeyPressFails = null;

        // A clipboard that cannot be put back.
        _clipboard.Text = earlier;
        paste.Paste(pasted);
        NextWaitEnds();
        _clipboard.BusyFor = 1;
        NextWaitEnds();

        // A timer that cannot be set for the putting back.
        _clipboard.Text = earlier;
        timerFails = true;
        paste.Paste(pasted);
        NextWaitEnds();

        // Every path above leaves a note, a report or both. Counting them makes sure each path
        // really ran: three notes (no window, window unreachable, key press) and four reports
        // (window unreachable, key press, putting back, timer).
        Assert.Equal(3, _notes.Count);
        Assert.Equal(4, _reports.Count);
        Assert.DoesNotContain(_notes, note => note.Contains(pasted) || note.Contains(earlier));
        Assert.DoesNotContain(_reports, report => report.Message.Contains(pasted) || report.Message.Contains(earlier));
    }
}

public class RetryingClipboardTests
{
    private readonly FakeClipboard _inner = new() { Text = "hello" };
    private readonly List<TimeSpan> _waits = [];

    private RetryingClipboard NewClipboard() => new(_inner, _waits.Add);

    [Fact]
    public void A_clipboard_that_is_free_is_used_once_with_no_waiting()
    {
        var clipboard = NewClipboard();

        Assert.Equal("hello", clipboard.GetText());
        clipboard.SetText("world");

        Assert.Equal("world", _inner.Text);
        Assert.Equal(2, _inner.Calls);
        Assert.Empty(_waits);
    }

    [Fact]
    public void A_busy_clipboard_is_tried_again_after_a_short_wait()
    {
        _inner.BusyFor = 3;

        Assert.Equal("hello", NewClipboard().GetText());

        Assert.Equal(4, _inner.Calls);
        Assert.Equal([RetryingClipboard.Pause, RetryingClipboard.Pause, RetryingClipboard.Pause], _waits);
    }

    [Fact]
    public void Writing_is_tried_again_too()
    {
        _inner.BusyFor = 2;

        NewClipboard().SetText("world");

        Assert.Equal("world", _inner.Text);
        Assert.Equal(3, _inner.Calls);
        Assert.Equal(2, _waits.Count);
    }

    [Fact]
    public void It_works_on_the_last_try()
    {
        _inner.BusyFor = RetryingClipboard.Attempts - 1;

        Assert.Equal("hello", NewClipboard().GetText());

        Assert.Equal(RetryingClipboard.Attempts, _inner.Calls);
    }

    [Fact]
    public void After_ten_tries_it_gives_up_and_says_the_clipboard_is_busy()
    {
        _inner.BusyFor = int.MaxValue;

        Assert.Throws<ClipboardBusyException>(() => NewClipboard().SetText("world"));

        Assert.Equal(10, RetryingClipboard.Attempts);
        Assert.Equal(10, _inner.Calls);
        Assert.Equal(9, _waits.Count);
        Assert.Equal("hello", _inner.Text);
    }

    [Fact]
    public void Any_other_failure_is_passed_on_at_once()
    {
        _inner.Broken = new InvalidOperationException("boom");

        Assert.Throws<InvalidOperationException>(() => NewClipboard().GetText());

        Assert.Equal(1, _inner.Calls);
        Assert.Empty(_waits);
    }
}
