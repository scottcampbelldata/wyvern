using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// A clipboard to read from that exists only in the test. No test may read the real one: it
/// holds whatever the person at the machine last copied.
/// </summary>
public sealed class FakeClipboardReader : IClipboardReader
{
    private readonly Dictionary<ClipboardFormat, byte[]> _blocks = [];
    private readonly HashSet<ClipboardFormat> _withheld = [];
    private bool _held;

    /// <summary>Who is said to have put the content there.</summary>
    public ClipboardOwner WhoOwns { get; set; } = new("notepad", IsBeacon: false);

    /// <summary>The clipboard's change number. <see cref="Copy"/> raises it by one.</summary>
    public uint Sequence { get; set; } = 1;

    /// <summary>Added to <see cref="Sequence"/> each time a block is copied out, as when a program makes its content only when asked.</summary>
    public uint SequenceStep { get; set; }

    /// <summary>How many of the next tries to open it fail because another program has it open.</summary>
    public int BusyFor { get; set; }

    /// <summary>When set, opening it throws this.</summary>
    public Exception? Broken { get; set; }

    /// <summary>
    /// Called each time a try finds the clipboard busy, before the try fails: another program has
    /// it open, and meanwhile a copy can land. A test uses it to change what is on the clipboard
    /// and who owns it between two tries.
    /// </summary>
    public Action? WhenBusy { get; set; }

    /// <summary>
    /// Called with the format each time a block has been copied out, while the clipboard is still
    /// held and before the caller asks for anything else. A test uses it to pause or stop capture
    /// in the middle of a reading, after one form of it has been copied.
    /// </summary>
    public Action<ClipboardFormat>? WhenCopied { get; set; }

    /// <summary>How many of the next times it is asked whether a window has it open the answer is yes.</summary>
    public int InUseFor { get; set; }

    /// <summary>How many times it was asked whether a window has it open.</summary>
    public int InUseAsked { get; private set; }

    /// <summary>When set, asking whether a window has it open throws this.</summary>
    public Exception? InUseBroken { get; set; }

    public bool InUse()
    {
        InUseAsked++;
        if (InUseBroken is not null)
        {
            throw InUseBroken;
        }

        if (InUseFor > 0)
        {
            InUseFor--;
            return true;
        }

        return false;
    }

    /// <summary>How many times opening it was tried, including the tries that failed.</summary>
    public int Tries { get; private set; }

    /// <summary>How many times the owner was asked.</summary>
    public int OwnerAsked { get; private set; }

    /// <summary>How many times the owner was asked while the clipboard was not held open. Who owns it can change at any time then.</summary>
    public int OwnerAskedWhileClosed { get; private set; }

    /// <summary>Every format whose block was asked for, in order.</summary>
    public List<ClipboardFormat> Asked { get; } = [];

    /// <summary>How many times anything at all was asked about a format: whether it is there, or for its block.</summary>
    public int Looks { get; private set; }

    /// <summary>A new copy: everything that was there goes, these blocks come, and the change number goes up.</summary>
    public void Copy(params (ClipboardFormat Format, byte[] Bytes)[] blocks)
    {
        _blocks.Clear();
        _withheld.Clear();
        foreach (var (format, bytes) in blocks)
        {
            _blocks[format] = bytes;
        }

        Sequence++;
    }

    /// <summary>
    /// Makes the clipboard offer these formats without any block to read: asked whether it has
    /// them it says yes, asked for their block it gives none. As when a marker's block is empty
    /// or cannot be had. Put in place by a copy, so it follows <see cref="Copy"/>.
    /// </summary>
    public void Withhold(params ClipboardFormat[] formats)
    {
        foreach (ClipboardFormat format in formats)
        {
            _withheld.Add(format);
        }
    }

    public ClipboardOwner Owner()
    {
        OwnerAsked++;
        if (!_held)
        {
            OwnerAskedWhileClosed++;
        }

        return WhoOwns;
    }

    public T Read<T>(Func<IOpenClipboard, T> read)
    {
        Tries++;
        if (Broken is not null)
        {
            throw Broken;
        }

        if (BusyFor > 0)
        {
            BusyFor--;
            WhenBusy?.Invoke();
            throw new ClipboardBusyException();
        }

        _held = true;
        try
        {
            return read(new Open(this));
        }
        finally
        {
            _held = false;
        }
    }

    private sealed class Open(FakeClipboardReader clipboard) : IOpenClipboard
    {
        public uint Sequence => clipboard.Sequence;

        public bool Has(ClipboardFormat format)
        {
            clipboard.Looks++;
            return clipboard._blocks.ContainsKey(format) || clipboard._withheld.Contains(format);
        }

        public byte[]? Bytes(ClipboardFormat format, int maxBytes, out bool tooLarge, int reserve = 0)
        {
            clipboard.Looks++;
            clipboard.Asked.Add(format);
            tooLarge = false;
            if (!clipboard._blocks.TryGetValue(format, out byte[]? block))
            {
                return null;
            }

            if (block.Length > maxBytes)
            {
                tooLarge = true;
                return null;
            }

            clipboard.Sequence += clipboard.SequenceStep;
            byte[] copy = new byte[reserve + block.Length];
            block.CopyTo(copy, reserve);
            clipboard.WhenCopied?.Invoke(format);
            return copy;
        }
    }
}

/// <summary>Stands in for the window that Windows tells of clipboard changes. A test says when one happened.</summary>
public sealed class FakeClipboardListener : IClipboardListener
{
    private Action? _changed;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool Listening => _changed is not null;

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    /// <summary>When set, <see cref="Start"/> throws it.</summary>
    public Exception? StartFails { get; set; }

    public void Start(Action changed)
    {
        if (StartFails is not null)
        {
            throw StartFails;
        }

        Starts++;
        _changed = changed;
    }

    public void Stop()
    {
        Stops++;
        _changed = null;
    }

    /// <summary>Says the clipboard has changed. Nothing is told while it is stopped, as with the real one.</summary>
    public void Changed() => _changed?.Invoke();
}
