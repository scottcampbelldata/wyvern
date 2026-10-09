using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;

namespace Wyvern.Tests;

/// <summary>
/// Pasting an entry of the clipboard history, which stays on the clipboard afterwards. As in
/// <see cref="PasteServiceTests"/>, everything here runs against fakes. No test presses a key,
/// moves the focus or touches the real clipboard; the waits are a list the test works through
/// by hand.
/// </summary>
public class PasteAndKeepTests
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

    // An entry that was copied from a web page: its HTML and a picture, and no plain text.
    private readonly IReadOnlyList<ClipboardBlock> _blocks =
    [
        new(ClipboardFormat.Html, [1, 2, 3, 0]),
        new(ClipboardFormat.Dib, [4, 5, 6]),
    ];

    private readonly List<string> _events = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly List<string> _notes = [];
    private readonly List<(string Message, Exception Error)> _reports = [];
    private readonly FakeClipboard _clipboard;
    private readonly FakeTarget _target;
    private readonly PasteService _paste;

    // The clipboard that tries again is tested by itself, around a clipboard of its own.
    private readonly FakeClipboard _inner = new();
    private readonly List<TimeSpan> _pauses = [];

    public PasteAndKeepTests()
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

    /// <summary>An entry that is text and nothing else.</summary>
    private static IReadOnlyList<ClipboardBlock> TextOnly(string text) =>
        [new(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(text))];

    /// <summary>Lets the oldest wait run out.</summary>
    private void NextWaitEnds()
    {
        var wait = _waits[0];
        _waits.RemoveAt(0);
        wait.Action();
    }

    /// <summary>Lets every wait run out, the oldest first, with those that are started meanwhile.</summary>
    private void EveryWaitEnds()
    {
        while (_waits.Count > 0)
        {
            NextWaitEnds();
        }
    }

    private RetryingClipboard NewRetryingClipboard() => new(_inner, _pauses.Add);

    [Fact]
    public void The_whole_sequence_in_order()
    {
        _paste.PasteAndKeep(_blocks);
        NextWaitEnds();

        Assert.Equal(["clipboard set to 2 blocks", "focus given back", "Ctrl+V pressed"], _events);
        Assert.Same(_blocks, _clipboard.Blocks);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void The_key_is_not_pressed_until_the_focus_has_had_a_moment_to_settle()
    {
        _paste.PasteAndKeep(_blocks);

        Assert.Equal(["clipboard set to 2 blocks", "focus given back"], _events);
        Assert.Equal(PasteService.SettleDelay, Assert.Single(_waits).After);
    }

    [Fact]
    public void After_the_key_press_nothing_is_put_back_and_the_blocks_stay_on_the_clipboard()
    {
        _paste.PasteAndKeep(_blocks);
        NextWaitEnds();

        // No wait is left that could put anything back, and the one call to the clipboard was
        // the write: it was never read and never written a second time.
        Assert.Equal("Ctrl+V pressed", _events[^1]);
        Assert.Empty(_waits);
        Assert.Equal(1, _clipboard.Calls);
        Assert.DoesNotContain("clipboard read", _events);
        Assert.Same(_blocks, _clipboard.Blocks);
    }

    [Fact]
    public void With_no_blocks_nothing_is_touched()
    {
        _paste.PasteAndKeep([]);

        Assert.Equal(0, _clipboard.Calls);
        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Null(_clipboard.Blocks);
    }

    [Fact]
    public void With_no_blocks_an_earlier_paste_still_puts_its_text_back()
    {
        _paste.Paste("snippet");
        NextWaitEnds();

        _paste.PasteAndKeep([]);
        EveryWaitEnds();

        Assert.Equal("clipboard set to what I copied earlier", _events[^1]);
        Assert.Equal("what I copied earlier", _clipboard.Text);
    }

    [Fact]
    public void With_no_window_to_paste_into_the_blocks_stay_on_the_clipboard_and_the_owner_is_told()
    {
        _target.HasWindow = false;

        _paste.PasteAndKeep(_blocks);

        Assert.Equal(["clipboard set to 2 blocks", "focus given back"], _events);
        Assert.Empty(_waits);
        Assert.Same(_blocks, _clipboard.Blocks);
        Assert.Equal([PasteService.NoWindowKeptNote], _notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_window_that_cannot_be_reached_is_treated_as_no_window()
    {
        _target.ActivateFails = new InvalidOperationException("private words from whatever failed");

        _paste.PasteAndKeep(_blocks);

        Assert.Equal(["clipboard set to 2 blocks", "focus given back"], _events);
        Assert.Empty(_waits);
        Assert.Same(_blocks, _clipboard.Blocks);
        Assert.Equal([PasteService.NoWindowKeptNote], _notes);
        var report = Assert.Single(_reports);
        Assert.Equal("The window to paste into could not be reached", report.Message);
        Assert.Same(_target.ActivateFails, report.Error);
    }

    [Fact]
    public void A_key_press_that_fails_is_reported_and_the_blocks_are_left_on_the_clipboard()
    {
        _target.KeyPressFails = new InvalidOperationException("private words from whatever failed");

        _paste.PasteAndKeep(_blocks);
        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Same(_blocks, _clipboard.Blocks);
        Assert.Equal(1, _clipboard.Calls);

        // The sentence for an entry, not the one for a snippet's text.
        Assert.Equal([PasteService.NotPastedKeptNote], _notes);
        var report = Assert.Single(_reports);
        Assert.Equal("The paste key press could not be sent", report.Message);
        Assert.Same(_target.KeyPressFails, report.Error);
    }

    [Fact]
    public void A_note_that_cannot_be_shown_does_not_escape_the_delayed_step()
    {
        var paste = new PasteService(
            _clipboard,
            _target,
            (after, action) => _waits.Add((after, action)),
            _ => throw new InvalidOperationException("nowhere to show a note"),
            (message, error) => _reports.Add((message, error)));
        _target.KeyPressFails = new InvalidOperationException("boom");

        paste.PasteAndKeep(_blocks);
        NextWaitEnds();

        Assert.Empty(_waits);
        Assert.Same(_blocks, _clipboard.Blocks);
        Assert.Equal("The paste key press could not be sent", Assert.Single(_reports).Message);
    }

    [Fact]
    public void A_busy_clipboard_fails_the_paste_before_anything_else_happens()
    {
        _clipboard.BusyFor = 1;

        Assert.Throws<ClipboardBusyException>(() => _paste.PasteAndKeep(_blocks));

        // The focus did not move and no key press is waiting.
        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Null(_clipboard.Blocks);
    }

    [Fact]
    public void A_busy_clipboard_leaves_an_earlier_paste_to_put_its_text_back()
    {
        _paste.Paste("snippet");
        NextWaitEnds();
        _clipboard.BusyFor = 1;

        Assert.Throws<ClipboardBusyException>(() => _paste.PasteAndKeep(_blocks));
        EveryWaitEnds();

        // The entry never got as far as the clipboard, so the snippet's paste ends as it would
        // have without it.
        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Equal(1, _events.Count(e => e == "Ctrl+V pressed"));
    }

    [Fact]
    public void An_earlier_paste_puts_nothing_back_once_an_entry_has_been_pasted()
    {
        _paste.Paste("snippet");
        NextWaitEnds();

        // The snippet's putting back is still waiting. The entry takes over from it.
        _paste.PasteAndKeep(_blocks);
        EveryWaitEnds();

        // After the entry went on the clipboard it was not even read again, let alone written.
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to snippet",
                "focus given back",
                "Ctrl+V pressed",
                "clipboard set to 2 blocks",
                "focus given back",
                "Ctrl+V pressed",
            ],
            _events);
        Assert.Same(_blocks, _clipboard.Blocks);
    }

    [Fact]
    public void An_entry_that_is_the_very_text_of_the_snippet_before_it_is_kept_as_well()
    {
        var blocks = TextOnly("snippet");
        _paste.Paste("snippet");
        NextWaitEnds();

        // The clipboard holds the snippet's own text again, which is what the snippet's putting
        // back looks for before it writes. It must not take the entry for its own paste.
        _paste.PasteAndKeep(blocks);
        EveryWaitEnds();

        Assert.Same(blocks, _clipboard.Blocks);
        Assert.Equal("snippet", _clipboard.Text);
        Assert.DoesNotContain("clipboard set to what I copied earlier", _events);
    }

    [Fact]
    public void An_earlier_paste_puts_nothing_back_when_the_entry_found_no_window_to_paste_into()
    {
        var blocks = TextOnly("snippet");
        _paste.Paste("snippet");
        NextWaitEnds();
        _target.HasWindow = false;

        _paste.PasteAndKeep(blocks);
        EveryWaitEnds();

        // The owner was told the entry is on the clipboard, so it has to be there still.
        Assert.Equal([PasteService.NoWindowKeptNote], _notes);
        Assert.Same(blocks, _clipboard.Blocks);
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to snippet",
                "focus given back",
                "Ctrl+V pressed",
                "clipboard set to 1 blocks",
                "focus given back",
            ],
            _events);
    }

    [Fact]
    public void An_entry_pasted_before_the_snippets_key_press_replaces_it()
    {
        _paste.Paste("snippet");
        _paste.PasteAndKeep(_blocks);
        EveryWaitEnds();

        // One key press, made while the clipboard held the entry, and nothing put back after it.
        Assert.Equal(
            [
                "clipboard read",
                "clipboard set to snippet",
                "focus given back",
                "clipboard set to 2 blocks",
                "focus given back",
                "Ctrl+V pressed",
            ],
            _events);
        Assert.Same(_blocks, _clipboard.Blocks);
    }

    [Fact]
    public void A_snippet_pasted_after_an_entry_puts_the_text_of_the_entry_back()
    {
        _paste.PasteAndKeep(TextOnly("from history"));
        NextWaitEnds();
        Assert.Empty(_waits);

        _paste.Paste("snippet");
        NextWaitEnds();
        Assert.Equal("snippet", _clipboard.Text);
        NextWaitEnds();

        // The entry is what the clipboard held before the snippet, so it is what goes back.
        Assert.Equal("clipboard set to from history", _events[^1]);
        Assert.Equal("from history", _clipboard.Text);
        Assert.Empty(_waits);
    }

    [Fact]
    public void Once_the_key_is_pressed_the_paste_is_over_and_a_later_paste_remembers_nothing_of_it()
    {
        _paste.PasteAndKeep(_blocks);
        NextWaitEnds();

        // Text with nothing in it is the one thing on the clipboard that a paste of an entry
        // which had not finished could be taken to have put there. This one has finished, so
        // the snippet finds that text like any other and puts it back.
        _clipboard.Text = "";
        _paste.Paste("snippet");
        EveryWaitEnds();

        Assert.Equal("clipboard set to ", _events[^1]);
        Assert.Equal("", _clipboard.Text);
    }

    [Fact]
    public void A_snippet_pasted_before_the_key_press_for_an_entry_replaces_it()
    {
        _paste.PasteAndKeep(TextOnly("from history"));
        _paste.Paste("snippet");
        EveryWaitEnds();

        // The one key press is the snippet's: it comes after the snippet went on the clipboard.
        Assert.Equal(
            [
                "clipboard set to 1 blocks",
                "focus given back",
                "clipboard read",
                "clipboard set to snippet",
                "focus given back",
                "Ctrl+V pressed",
                "clipboard read",
                "clipboard set to from history",
            ],
            _events);
        Assert.Equal(1, _events.Count(e => e == "Ctrl+V pressed"));
        Assert.Equal("from history", _clipboard.Text);
    }

    [Fact]
    public void A_block_of_no_bytes_is_left_out_of_what_goes_on_the_clipboard()
    {
        ClipboardBlock html = new(ClipboardFormat.Html, [1, 2, 3, 0]);
        ClipboardBlock text = new(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("made up"));

        _inner.SetBlocks([new(ClipboardFormat.Rtf, []), html, new(ClipboardFormat.Dib, []), text]);

        // Windows cannot hold a block of no bytes, so the clipboard holds the other two, in their order.
        Assert.Equal([html, text], _inner.Blocks);
        Assert.Equal("made up", _inner.Text);
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public void Putting_no_blocks_on_the_clipboard_is_refused_and_the_clipboard_is_left_as_it_was()
    {
        _inner.Text = "what I copied earlier";

        Assert.Throws<ArgumentException>(() => _inner.SetBlocks([]));

        Assert.Equal("what I copied earlier", _inner.Text);
        Assert.Null(_inner.Blocks);
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public void Putting_only_blocks_of_no_bytes_on_the_clipboard_is_refused_and_the_clipboard_is_left_as_it_was()
    {
        _inner.Text = "what I copied earlier";

        Assert.Throws<ArgumentException>(
            () => _inner.SetBlocks([new(ClipboardFormat.Html, []), new(ClipboardFormat.UnicodeText, [])]));

        // Emptying the clipboard for them would leave the owner with nothing at all.
        Assert.Equal("what I copied earlier", _inner.Text);
        Assert.Null(_inner.Blocks);
    }

    [Fact]
    public void Blocks_with_nothing_in_them_are_refused_before_the_clipboard_is_opened()
    {
        _inner.BusyFor = 1;

        Assert.Throws<ArgumentException>(() => _inner.SetBlocks([new(ClipboardFormat.Html, [])]));

        // The clipboard was not asked for: the try that would have found it busy is still to come.
        Assert.Equal(1, _inner.BusyFor);
    }

    [Fact]
    public void Blocks_with_nothing_in_them_are_not_tried_a_second_time()
    {
        Assert.Throws<ArgumentException>(() => NewRetryingClipboard().SetBlocks([new(ClipboardFormat.Html, [])]));

        Assert.Equal(1, _inner.Calls);
        Assert.Empty(_pauses);
        Assert.Null(_inner.Blocks);
    }

    [Fact]
    public void An_entry_of_nothing_but_empty_blocks_fails_the_paste_before_anything_else_happens()
    {
        Assert.Throws<ArgumentException>(() => _paste.PasteAndKeep([new(ClipboardFormat.Html, [])]));

        // The focus did not move, no key press is waiting, and the clipboard holds what it held.
        Assert.Empty(_events);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Null(_clipboard.Blocks);
    }

    [Fact]
    public void An_entry_of_nothing_but_empty_blocks_leaves_an_earlier_paste_to_put_its_text_back()
    {
        _paste.Paste("snippet");
        NextWaitEnds();

        Assert.Throws<ArgumentException>(() => _paste.PasteAndKeep([new(ClipboardFormat.Html, [])]));
        EveryWaitEnds();

        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Equal(1, _events.Count(e => e == "Ctrl+V pressed"));
    }

    [Fact]
    public void Blocks_are_passed_on_as_the_same_list_and_a_free_clipboard_is_written_once()
    {
        NewRetryingClipboard().SetBlocks(_blocks);

        Assert.Same(_blocks, _inner.Blocks);
        Assert.Equal(1, _inner.Calls);
        Assert.Empty(_pauses);
    }

    [Fact]
    public void Writing_blocks_to_a_busy_clipboard_is_tried_again_after_a_short_wait()
    {
        _inner.BusyFor = 3;

        NewRetryingClipboard().SetBlocks(_blocks);

        Assert.Same(_blocks, _inner.Blocks);
        Assert.Equal(4, _inner.Calls);
        Assert.Equal([RetryingClipboard.Pause, RetryingClipboard.Pause, RetryingClipboard.Pause], _pauses);
    }

    [Fact]
    public void Writing_blocks_works_on_the_last_try()
    {
        _inner.BusyFor = RetryingClipboard.Attempts - 1;

        NewRetryingClipboard().SetBlocks(_blocks);

        Assert.Same(_blocks, _inner.Blocks);
        Assert.Equal(RetryingClipboard.Attempts, _inner.Calls);
    }

    [Fact]
    public void After_every_try_writing_blocks_gives_up_and_says_the_clipboard_is_busy()
    {
        _inner.BusyFor = int.MaxValue;

        Assert.Throws<ClipboardBusyException>(() => NewRetryingClipboard().SetBlocks(_blocks));

        Assert.Equal(RetryingClipboard.Attempts, _inner.Calls);
        Assert.Equal(Enumerable.Repeat(RetryingClipboard.Pause, RetryingClipboard.Attempts - 1), _pauses);
        Assert.Null(_inner.Blocks);
    }

    [Fact]
    public void Any_other_failure_when_writing_blocks_is_passed_on_at_once()
    {
        _inner.Broken = new InvalidOperationException("boom");

        Assert.Throws<InvalidOperationException>(() => NewRetryingClipboard().SetBlocks(_blocks));

        Assert.Equal(1, _inner.Calls);
        Assert.Empty(_pauses);
    }
}
