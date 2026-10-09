using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;

namespace Wyvern.Tests;

/// <summary>
/// A clipboard that exists only in the test. No test may touch the real one: it belongs to
/// whoever is sitting at the machine.
/// </summary>
public sealed class FakeClipboard : IClipboard
{
    private readonly List<string> _events;

    /// <param name="events">Where each call is noted, so a test can check the order of calls across several fakes.</param>
    public FakeClipboard(List<string>? events = null)
    {
        _events = events ?? [];
    }

    /// <summary>What the clipboard holds. Null stands for anything that is not text, or nothing at all.</summary>
    public string? Text { get; set; }

    /// <summary>
    /// The blocks the clipboard was last given, without those of no bytes, when blocks were the
    /// last thing put on it. Null otherwise.
    /// </summary>
    public IReadOnlyList<ClipboardBlock>? Blocks { get; private set; }

    /// <summary>How many of the next calls fail because another program has the clipboard open.</summary>
    public int BusyFor { get; set; }

    /// <summary>When set, every call that gets as far as opening the clipboard throws it.</summary>
    public Exception? Broken { get; set; }

    /// <summary>How many calls were made in all, including those that failed.</summary>
    public int Calls { get; private set; }

    public string? GetText()
    {
        Enter();
        _events.Add("clipboard read");
        return Text;
    }

    public void SetText(string text)
    {
        Enter();
        _events.Add("clipboard set to " + text);
        Text = text;
        Blocks = null;
    }

    public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks)
    {
        Calls++;

        // As the real clipboard does: a block of no bytes is left out, and when that leaves
        // nothing the call is refused before the clipboard is opened, so it is refused whether
        // or not the clipboard is busy and what the clipboard holds stays as it is.
        IReadOnlyList<ClipboardBlock> kept =
            blocks.All(block => block.Bytes.Length > 0) ? blocks : [.. blocks.Where(block => block.Bytes.Length > 0)];
        if (kept.Count == 0)
        {
            throw new ArgumentException("There is nothing in these blocks to put on the clipboard.", nameof(blocks));
        }

        Open();
        _events.Add($"clipboard set to {kept.Count} blocks");

        // The same list when nothing was left out of it, so that a test can tell it is that one.
        Blocks = kept;

        // Reading text afterwards gives the text among the blocks, as the real clipboard does.
        var text = kept.FirstOrDefault(block => block.Format == ClipboardFormat.UnicodeText);
        Text = text is null ? null : ClipboardBytes.Text(text.Bytes);
    }

    private void Enter()
    {
        Calls++;
        Open();
    }

    private void Open()
    {
        if (Broken is not null)
        {
            throw Broken;
        }

        if (BusyFor > 0)
        {
            BusyFor--;
            throw new ClipboardBusyException();
        }
    }
}
