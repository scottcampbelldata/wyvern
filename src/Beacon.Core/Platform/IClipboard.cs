namespace Beacon.Core.Platform;

/// <summary>
/// The Windows clipboard, as far as Beacon needs it. The text that passes through here is the
/// owner's: it must never be logged or put in a notification.
/// </summary>
public interface IClipboard
{
    /// <summary>The text on the clipboard now, or null when it holds no text.</summary>
    /// <exception cref="ClipboardBusyException">Another program has the clipboard open.</exception>
    string? GetText();

    /// <summary>
    /// Replaces what is on the clipboard with this text, marked so that clipboard managers and
    /// the Windows clipboard history leave it out.
    /// </summary>
    /// <exception cref="ClipboardBusyException">Another program has the clipboard open.</exception>
    void SetText(string text);

    /// <summary>
    /// Replaces what is on the clipboard with these blocks, all of them together and in this
    /// order, marked like <see cref="SetText"/> so that clipboard managers, the Windows
    /// clipboard history and Beacon's own history leave it out. This is how an entry of the
    /// clipboard history goes back on the clipboard in every form it was copied in.
    /// </summary>
    /// <remarks>
    /// A block of no bytes is left out: Windows cannot hold one. When a write fails after the
    /// clipboard has been emptied, the clipboard is left holding nothing, never a part of the
    /// blocks.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// There are no blocks, or none of them holds a byte. The clipboard has not been touched:
    /// emptying it for nothing would take away what the owner has on it.
    /// </exception>
    /// <exception cref="ClipboardBusyException">Another program has the clipboard open. The clipboard has not been touched.</exception>
    void SetBlocks(IReadOnlyList<ClipboardBlock> blocks);
}

/// <summary>
/// Only one program can have the clipboard open at a time. This says another one has it right
/// now, which usually passes within a few milliseconds.
/// </summary>
public sealed class ClipboardBusyException : Exception
{
    public ClipboardBusyException()
        : base("The clipboard is in use by another program.")
    {
    }
}

/// <summary>
/// Tries again for a short while when the clipboard is busy, then gives up and lets the
/// exception through, which the launcher reports like any other failed action.
/// </summary>
public sealed class RetryingClipboard : IClipboard
{
    /// <summary>How many times one read or write is tried in all.</summary>
    public const int Attempts = 10;

    /// <summary>How long to wait between two tries. Ten tries take a fifth of a second at most.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(20);

    private readonly IClipboard _inner;
    private readonly Action<TimeSpan> _wait;

    /// <param name="wait">Waits for the given time. Thread.Sleep in the app; a test passes its own.</param>
    public RetryingClipboard(IClipboard inner, Action<TimeSpan> wait)
    {
        _inner = inner;
        _wait = wait;
    }

    public string? GetText() => Retry(_inner.GetText);

    public void SetText(string text) => Retry<object?>(() =>
    {
        _inner.SetText(text);
        return null;
    });

    public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks) => Retry<object?>(() =>
    {
        _inner.SetBlocks(blocks);
        return null;
    });

    private T Retry<T>(Func<T> attempt)
    {
        for (int tries = 1; ; tries++)
        {
            try
            {
                return attempt();
            }
            catch (ClipboardBusyException) when (tries < Attempts)
            {
                _wait(Pause);
            }
        }
    }
}
