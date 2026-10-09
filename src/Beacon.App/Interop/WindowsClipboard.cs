using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Beacon.Core.Platform;

namespace Beacon.App.Interop;

/// <summary>
/// Reads text from the Windows clipboard, and writes text or a set of blocks to it. Each call
/// opens the clipboard once and closes it again; when another program has it open the call
/// throws <see cref="ClipboardBusyException"/> and the caller decides whether to try again.
/// A write that fails before the clipboard has been emptied leaves on it what was there; one
/// that fails after leaves it empty, never holding a part of what was to be written.
/// Create and use on the UI thread. After <see cref="Dispose"/> every call throws
/// <see cref="ObjectDisposedException"/> and the clipboard is not touched.
/// </summary>
public sealed class WindowsClipboard : IClipboard, IDisposable
{
    private const uint UnicodeText = 13;          // CF_UNICODETEXT
    private const uint MoveableZeroed = 0x0042;   // GHND: GMEM_MOVEABLE (0x0002) and GMEM_ZEROINIT (0x0040)
    private static readonly IntPtr MessageOnlyParent = new(-3);

    // Windows and well-behaved clipboard managers leave out anything that carries these, and
    // so does Beacon's own clipboard history, which reads them by the same names. Each goes
    // with the number of zero bytes its block holds.
    //
    // Three are Windows' own (the "Cloud Clipboard and Clipboard History Formats" on the
    // Clipboard Formats page). ExcludeClipboardContentFromMonitorProcessing: "Place any data
    // on the clipboard in this format", so it needs only to be there. The other two must hold
    // "a serialized DWORD value of zero": four bytes.
    //
    // "Clipboard Viewer Ignore" is not Windows' own, and Microsoft's documentation does not
    // describe it. It is an older agreement among clipboard managers and password managers.
    // By what is known of it beyond that documentation, it says what it says by being there,
    // as the first does, and nothing is agreed about what its block holds. That is how Beacon's
    // own capture reads it: it only asks whether the clipboard offers the format. So what the
    // block holds does not matter, but there has to be a block. A handle of null would not do:
    // the page for SetClipboardData says that null means the window "renders the format upon
    // request" and must then answer WM_RENDERFORMAT and WM_RENDERALLFORMATS, which Beacon's
    // window does not. Nor would a block of no bytes: the page for GlobalAlloc says that zero
    // bytes of movable memory give a handle "marked as discarded", and such a handle cannot be
    // locked to be filled (NewBlock would throw, and nothing at all would be written). So it
    // is given the smallest block there is, one byte, holding zero.
    private static readonly (ClipboardFormat Format, int Bytes)[] KeepOutOfHistoryFormats =
    [
        (ClipboardFormat.KeepOutOfMonitors, sizeof(uint)),
        (ClipboardFormat.ViewerIgnore, 1),
        (ClipboardFormat.CanIncludeInHistory, sizeof(uint)),
        (ClipboardFormat.CanUploadToCloud, sizeof(uint)),
    ];

    // A window that never appears. Windows records it as the owner of what Beacon puts on the
    // clipboard, which is one of the two ways the clipboard history knows Beacon's own writes.
    // The other is the four markers.
    private readonly HwndSource _owner = new(new HwndSourceParameters("Beacon clipboard owner")
    {
        ParentWindow = MessageOnlyParent,
        WindowStyle = 0,
    });

    // The numbers Windows gave the formats above, in the same order. Null until the first
    // write has asked for them.
    private uint[]? _keepOutOfHistoryIds;
    private bool _disposed;

    public string? GetText()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Open();
        try
        {
            if (!IsClipboardFormatAvailable(UnicodeText))
            {
                return null;
            }

            // The clipboard keeps this handle. It is not freed here and not left locked.
            IntPtr handle = GetClipboardData(UnicodeText);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            // The block was filled by another program and cannot be trusted to end with a zero
            // character. GlobalSize says how many bytes the block has, and nothing past them is
            // read. It returns zero for a handle that is not a block of memory. Two bytes make
            // one character, and an odd byte at the end is left out, so a block of zero bytes
            // or one byte holds no text.
            ulong characters = GlobalSize(handle).ToUInt64() / 2;
            if (characters == 0)
            {
                return null;
            }

            // More characters than a string can hold. The block is not read in part: the caller
            // would take the part for the whole and put that back later in place of the
            // owner's text. Failing here leaves the clipboard as it is.
            if (characters > int.MaxValue)
            {
                throw new InvalidOperationException("The text on the clipboard is too large to read.");
            }

            IntPtr text = GlobalLock(handle);
            if (text == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // Copies exactly this many characters, zero characters included, and looks no further.
                string block = Marshal.PtrToStringUni(text, (int)characters);

                // The text ends at the first zero character. A block may be larger than the text
                // in it, and what follows the zero is not part of the text. With no zero
                // character at all, the whole block is the text.
                int end = block.IndexOf('\0');
                return end < 0 ? block : block[..end];
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Clipboard text ends with a zero character.
        Put([(UnicodeText, Encoding.Unicode.GetBytes(text + '\0'))]);
    }

    public void SetBlocks(IReadOnlyList<ClipboardBlock> blocks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A block of no bytes is left out. Windows cannot hold one: GlobalAlloc gives a handle
        // "marked as discarded" for zero bytes of movable memory, and GlobalLock returns null
        // for a block of zero bytes, so the whole write would fail for the sake of a form that
        // holds nothing. When that leaves no block at all, Put refuses before anything is
        // touched.
        //
        // Asking Windows for the number of a named format can fail, so it is done here, before
        // the clipboard is opened.
        var content = new List<(uint Format, byte[] Bytes)>(blocks.Count);
        foreach (ClipboardBlock block in blocks)
        {
            if (block.Bytes.Length > 0)
            {
                content.Add((FormatId(block.Format), block.Bytes));
            }
        }

        Put([.. content]);
    }

    public void Dispose()
    {
        _disposed = true;
        _owner.Dispose();
    }

    // Replaces what is on the clipboard with these blocks, in this order, behind the markers.
    // There must be at least one block and none of no bytes: SetText's block always holds its
    // zero character, and SetBlocks leaves empty blocks out.
    //
    // The order of the steps, and what a failure at each leaves behind:
    //   1. The numbers of the marker formats (the content's are known already). Nothing is
    //      touched yet.
    //   2. Open the clipboard. When it is busy this throws with no memory taken, so trying
    //      again costs nothing, however large the content is.
    //   3. Make every block: memory from Windows, and the bytes copied in. Whatever fails for
    //      want of memory fails here, while the clipboard still holds what the owner had on it.
    //   4. Empty the clipboard. This cannot be undone, which is why nothing is left to do after
    //      it but hand over blocks that already exist.
    //   5. Hand the blocks over, the markers first.
    //   6. Close the clipboard, always.
    // When anything fails after step 4 the clipboard is emptied a second time before it is
    // closed. The owner's earlier content is gone by then whatever is done, and a part of the
    // new content (the rich text of an entry without its picture, say) must not stay there to
    // be pasted as if it were the whole: a failed write leaves nothing.
    private void Put((uint Format, byte[] Bytes)[] content)
    {
        // With nothing to put there the clipboard must not be emptied: that would take the
        // owner's content away and leave only the markers in its place.
        if (content.Length == 0)
        {
            throw new ArgumentException("There is nothing in these blocks to put on the clipboard.", nameof(content));
        }

        // The markers go on the clipboard first and the content after them, so that at no
        // moment does the clipboard hold content without the markers that keep clipboard
        // managers and the Windows clipboard history away from it. Should the second emptying
        // below ever fail too, what stays behind is at least marked.
        uint[] markers = KeepOutOfHistoryIds();
        uint[] formats = [.. markers, .. content.Select(item => item.Format)];

        // One block for each format, in the same order. A slot is null when there is no block
        // in it for Beacon to free: before the block is made, and after Windows has taken it.
        IntPtr[] blocks = new IntPtr[formats.Length];
        try
        {
            // Throws ClipboardBusyException when another window has the clipboard open. No
            // block has been made yet, and the clipboard is not closed below, because it was
            // not opened: CloseClipboard belongs to a successful OpenClipboard only.
            Open();

            // True once the clipboard has been emptied, and once every block is on it.
            bool emptied = false;
            bool complete = false;
            try
            {
                // The clipboard is open and still holds what it held: OpenClipboard changes
                // neither its content nor its owner. A failure in these two loops is the
                // memory running out, and it leaves the owner's content where it was.
                for (int i = 0; i < markers.Length; i++)
                {
                    // Each marker holds zeros: the 32-bit number zero, or the one byte of the
                    // marker that only has to be there. None is a block of no bytes.
                    blocks[i] = NewBlock(new byte[KeepOutOfHistoryFormats[i].Bytes]);
                }

                for (int i = 0; i < content.Length; i++)
                {
                    blocks[markers.Length + i] = NewBlock(content[i].Bytes);
                }

                // EmptyClipboard frees what was on the clipboard and makes the window the
                // clipboard was opened with its owner, which SetClipboardData needs. It returns
                // false when it fails; the documentation does not say a failed call has
                // changed anything, so nothing more is done to the clipboard then.
                if (!EmptyClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                emptied = true;
                for (int i = 0; i < blocks.Length; i++)
                {
                    // SetClipboardData returns null when it fails, and the block is then still
                    // Beacon's. The error is read at once, before any other call replaces it.
                    if (SetClipboardData(formats[i], blocks[i]) == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    // Windows owns the block now, and it must not be written to or freed here.
                    // Clearing the slot is what keeps it away from GlobalFree below.
                    blocks[i] = IntPtr.Zero;
                }

                complete = true;
            }
            finally
            {
                if (emptied && !complete)
                {
                    // A block was refused part of the way. EmptyClipboard needs the clipboard
                    // open, which it still is, and frees every block that is on it: those are
                    // the ones Windows took above, whose slots are cleared, so nothing is freed
                    // twice. Its result is not looked at. If it fails there is nothing further
                    // to try, and the failure that brought us here is the one to report.
                    EmptyClipboard();
                }

                // Every successful OpenClipboard is followed by CloseClipboard, whichever way
                // this ended, or no other program could use the clipboard. Nothing is put on
                // the clipboard after it.
                CloseClipboard();
            }
        }
        finally
        {
            // Whatever Windows did not take is still Beacon's to free, whichever way this ended:
            // the clipboard was busy (no block was made), the memory ran out part of the way,
            // or a call failed. A block Windows took is never among these: its slot is null.
            foreach (IntPtr block in blocks)
            {
                if (block != IntPtr.Zero)
                {
                    GlobalFree(block);
                }
            }
        }
    }

    private void Open()
    {
        // A null owner must never reach OpenClipboard. The call would succeed, and then
        // EmptyClipboard would empty the clipboard and leave it with no owner, which makes
        // every SetClipboardData fail: the owner's text gone and nothing in its place. The
        // handle is null once the window has gone, whatever made it go.
        IntPtr owner = _owner.Handle;
        if (owner == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(WindowsClipboard));
        }

        // OpenClipboard fails while another window has the clipboard open. The window passed
        // here does not become the clipboard's owner until EmptyClipboard is called.
        if (!OpenClipboard(owner))
        {
            throw new ClipboardBusyException();
        }
    }

    // Asks Windows for the numbers of the marker formats, once. A name that is already
    // registered, by Beacon or by any other program, gets the same number again, and the
    // numbers stay good until the owner signs out. Zero means the call failed; nothing is
    // kept then, and the next write asks again.
    private uint[] KeepOutOfHistoryIds()
    {
        if (_keepOutOfHistoryIds is { } known)
        {
            return known;
        }

        uint[] ids = new uint[KeepOutOfHistoryFormats.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            // Throws when Windows will not give a name its number: without every marker
            // nothing is written.
            ids[i] = FormatId(KeepOutOfHistoryFormats[i].Format);
        }

        _keepOutOfHistoryIds = ids;
        return ids;
    }

    // The number Windows knows a format by. A standard format has a fixed one. Any other goes
    // by its name: RegisterClipboardFormat gives a name that is already registered, by Beacon
    // or by any other program, the same number again, and returns zero when it fails.
    private static uint FormatId(ClipboardFormat format)
    {
        if (format.Id != 0)
        {
            return format.Id;
        }

        uint id = RegisterClipboardFormat(format.Name ?? "");
        if (id == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return id;
    }

    // Allocates a block the clipboard can take and copies the bytes into it. The block belongs
    // to the caller, who frees it unless SetClipboardData accepts it. The clipboard takes
    // movable memory only, which is why the block is locked to get at its bytes.
    private static IntPtr NewBlock(byte[] bytes)
    {
        // GHND: movable, as SetClipboardData demands of a memory object, and filled with
        // zeros. GlobalAlloc gives at least the bytes asked for and may give more, and a
        // program that reads a block by its whole size (GlobalSize) reads those further bytes
        // too. Without the zeros they would be whatever Beacon's memory held there before,
        // handed to every program that looks at the clipboard. It returns null when it fails.
        IntPtr memory = GlobalAlloc(MoveableZeroed, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // GlobalLock returns null when it fails, and also for a block of zero bytes, which
        // GlobalAlloc hands out "marked as discarded". No block of zero bytes is passed in; if
        // one ever were, this would throw here, before the clipboard has been emptied.
        IntPtr target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            GlobalFree(memory);
            throw new Win32Exception(error);
        }

        Marshal.Copy(bytes, 0, target, bytes.Length);

        // Every GlobalLock is matched by a GlobalUnlock, and the memory must be unlocked
        // before the clipboard is closed.
        GlobalUnlock(memory);
        return memory;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    // Takes the handle of a block, not a pointer into it, and returns the block's size in bytes.
    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
