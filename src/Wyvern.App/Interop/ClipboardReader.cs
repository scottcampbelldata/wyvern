using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Wyvern.Core.Platform;

namespace Wyvern.App.Interop;

/// <summary>
/// Reads the Windows clipboard for the clipboard history. It does two things and decides
/// nothing: it says which program owns what is on the clipboard, and it copies whole blocks of
/// bytes out of the clipboard when it is asked for them. What is asked for, and what the bytes
/// mean, is decided in <c>Wyvern.Core.ClipboardHistory</c>.
/// </summary>
/// <remarks>
/// <para>The clipboard is opened with a window that this reader makes for itself and uses for
/// nothing else. Windows lets one window at a time have the clipboard open, so an opening that
/// names a window of its own fails while another window has the clipboard open, and makes the
/// openings of other windows fail while it has. An opener that names no window is outside that
/// promise. By what is known of Windows beyond its documentation, openers are told apart by
/// their window alone, so one that names none is shut out by this reader's window too, and
/// shuts it out. For the same reason the window must not be the one
/// <see cref="WindowsClipboard"/> writes with, nor the listener's: two openings that name the
/// same window do not shut each other out. And for that reason this reader is for one reading
/// at a time, which is how capture uses it. Should two threads ever read through it at once,
/// one takes the clipboard from the other, and the one that lost it throws
/// <see cref="ClipboardBusyException"/> when it finds that it could not close.</para>
/// <para>Create and dispose on the UI thread, which is the thread the window then belongs to.
/// <see cref="Read{T}"/> and <see cref="Owner"/> are for any thread; capture calls both on its
/// background line. Dispose only after capture has stopped reading: a reading that is under
/// way when the window is destroyed still closes the clipboard itself, but what Windows makes
/// of an open clipboard whose window has gone is not documented. After <see cref="Dispose"/>
/// every <see cref="Read{T}"/> throws <see cref="ObjectDisposedException"/> and the clipboard
/// is not touched.</para>
/// <para>Every format that is ever copied out here is one whose content is a block of memory:
/// text, CF_DIB, CF_HDROP and the formats that go by a name (HTML, rich text and the markers).
/// A format whose content is another kind of handle, such as CF_BITMAP, is refused.
/// Nothing here writes to the clipboard.</para>
/// <para>A format that goes by a name is known here by that name and by nothing else: the name
/// is what <see cref="ClipboardFormat"/> carries, and Windows is asked for its number each
/// time. So every named format that capture asks about is understood without a list of names
/// being kept here, the four markers among them: the three that Windows defines and the older
/// <c>Clipboard Viewer Ignore</c> (<see cref="ClipboardFormat.ViewerIgnore"/>), which capture
/// only ever asks about with <see cref="IOpenClipboard.Has"/>, and that copies nothing out.</para>
/// </remarks>
public sealed class ClipboardReader : IClipboardReader, IDisposable
{
    private const uint UnicodeText = 13;          // CF_UNICODETEXT
    private const uint Dib = 8;                   // CF_DIB
    private const uint FileDrop = 15;             // CF_HDROP
    private const uint FirstRegistered = 0xC000;  // the numbers Windows gives to formats that go by a name
    private const uint LastRegistered = 0xFFFF;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    // A message-only window: it never appears on screen. No message that comes to it matters;
    // it exists so that OpenClipboard has a window to associate the open clipboard with, one
    // that no other code in Wyvern and no other program opens the clipboard with. It never
    // becomes the clipboard's owner, because that takes EmptyClipboard, which is never called
    // here.
    private readonly HwndSource _source;

    // The window's handle, taken once in the constructor on the UI thread, so that Read needs
    // nothing of the HwndSource on the thread it runs on: only this number.
    private readonly IntPtr _window;

    // Written by Dispose on the UI thread and read by Read on capture's thread.
    private volatile bool _disposed;

    public ClipboardReader()
    {
        _source = new HwndSource(new HwndSourceParameters("Wyvern clipboard reader")
        {
            ParentWindow = MessageOnlyParent,
            WindowStyle = 0,
        });
        _window = _source.Handle;

        // A null window must never reach OpenClipboard: that is the opening with no window,
        // which this reader exists to avoid.
        if (_window == IntPtr.Zero)
        {
            _source.Dispose();
            throw new InvalidOperationException("The clipboard reader's window could not be made.");
        }
    }

    /// <summary>
    /// Who put the present content on the clipboard. Capture calls this from inside the callback
    /// of <see cref="Read{T}"/>, on the same thread, while this reader holds the clipboard open,
    /// so that the answer belongs to the same copy as everything else that is read.
    /// </summary>
    /// <remarks>
    /// That is safe with every call made here. None of them opens the clipboard, and none needs
    /// it open or closed: opening the clipboard does not change who owns it, only emptying it
    /// does. None sends a message to another program's window, so none waits for the program
    /// that owns the clipboard: they ask Windows about a window, and then about a process. The
    /// cost is those questions and nothing more, but every other program waits for the clipboard
    /// meanwhile, which is why capture asks only for a change it has not dealt with.
    /// </remarks>
    public ClipboardOwner Owner()
    {
        try
        {
            // GetClipboardOwner: the window that last put content on the clipboard, or null
            // when the clipboard has no owner, which is so when that window has since been
            // closed. The clipboard can hold content all the same. It does not have to be open
            // for this call.
            IntPtr owner = GetClipboardOwner();
            if (owner != IntPtr.Zero && ProcessOf(owner) is { } ownerProcess)
            {
                return ownerProcess == (uint)Environment.ProcessId
                    ? new ClipboardOwner(null, IsWyvern: true)
                    : new ClipboardOwner(NameOf(ownerProcess), IsWyvern: false);
            }

            // Without an owner, the window in front is the best guess at where the copy was
            // made. It is only a guess, so it never counts as Wyvern's own write: that is told
            // by the owner alone, and by the markers.
            // GetForegroundWindow: the window the owner is working in. It can be null.
            IntPtr front = GetForegroundWindow();
            if (front != IntPtr.Zero && ProcessOf(front) is { } frontProcess && frontProcess != (uint)Environment.ProcessId)
            {
                return new ClipboardOwner(NameOf(frontProcess), IsWyvern: false);
            }
        }
        catch (Exception)
        {
            // Whatever it was (the process ended between two calls, Windows will not say what
            // it is, or anything else at all), who copied cannot be told. Nothing is let out:
            // the interface promises that this never throws, and it is called with the
            // clipboard held.
        }

        return new ClipboardOwner(null, IsWyvern: false);
    }

    /// <summary>
    /// Opens the clipboard with this reader's own window, hands it to <paramref name="read"/>
    /// and closes it again, whatever that does. The result is returned only when the clipboard
    /// was this reader's from the opening to the closing, and nothing changed its content in
    /// between but the owning program making the content that was asked of it.
    /// </summary>
    /// <exception cref="ClipboardBusyException">
    /// Another window has the clipboard open, or the reading was interrupted: the clipboard's
    /// change number moved between two of the reader's calls, or the clipboard was no longer
    /// this reader's to close. The caller reads again.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    public bool InUse()
    {
        try
        {
            // GetOpenClipboardWindow: the window that has the clipboard open now, or null when
            // none has. It opens nothing and asks nothing of that window.
            return GetOpenClipboardWindow() != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public T Read<T>(Func<IOpenClipboard, T> read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // OpenClipboard: the documentation says that it fails while another window has the
        // clipboard open, and that the open clipboard is associated with the window passed in.
        // So it is given this reader's own window and never null. Of an opening with no window
        // the documentation says only that it is associated with the current task; it promises
        // nothing between two openers that both pass no window, which is how command-line
        // tools open the clipboard, and by what is known of Windows beyond its documentation
        // such a second opening succeeds and takes the clipboard over.
        //
        // The call is made on the caller's thread, and the window belongs to the UI thread.
        // Windows associates the opening with the window that is given, and the documentation
        // does not restrict which thread may give it. Whether Windows accepts a window of
        // another thread here is one of the things only a real run can confirm. If it did not,
        // this would return false and nothing would be read.
        //
        // It returns false when it fails. Nothing was opened then, so nothing is closed.
        if (!OpenClipboard(_window))
        {
            throw new ClipboardBusyException();
        }

        // Belt and braces. With a window of its own the reader should never lose the clipboard
        // while it holds it, and nobody but the owning program should be able to change its
        // content. Two things are watched all the same, and a reading that either of them
        // speaks against is never returned: it is thrown as "busy", like a failed opening, so
        // that capture's retry reads again.
        //
        // The first is the clipboard's change number, with one movement that is expected and
        // is not an interruption. A program can put a format on the clipboard without its
        // content and make the content only when it is asked for ("Delayed Rendering" on the
        // Clipboard Operations page). The page for GetClipboardSequenceNumber says that the
        // number is then not moved on until the content is made, and the page for
        // WM_RENDERFORMAT says that it is made when an application asks for the format, by the
        // owner, while the asking application holds the clipboard open. That is inside our own
        // GetClipboardData, for every copy from a program that makes its content late. So the
        // reading keeps the number it expects to find. It starts as the number right after the
        // opening. Whatever the number moves by inside a call of GetClipboardData is the owner
        // making what was asked for, and the number after the call is what is expected from
        // then on. A number that is found to have moved at any other moment, between two of
        // our calls, is an interruption: it is looked for right before every GetClipboardData
        // and once more before the closing.
        //
        // Nothing is lost by allowing the movement inside the call. Another program cannot
        // start a new copy without emptying the clipboard, and the page for EmptyClipboard
        // says that it must have opened the clipboard first. While this reader holds it, that
        // would be a takeover, and a takeover is what the second thing is there for:
        // CloseClipboard answering false, which is taken to mean that the clipboard was no
        // longer ours to close. Without a takeover only the owner can put content on the
        // clipboard while it is held (the page for SetClipboardData wants the owner, and
        // wants the clipboard opened by the caller except in answer to WM_RENDERFORMAT), and
        // that is content of the same copy.
        bool interrupted;
        bool closed;
        T result;
        try
        {
            // GetClipboardSequenceNumber: a number Windows moves on whenever the clipboard's
            // content changes or is emptied. It needs no open clipboard. Taken here, right
            // after the opening, it is the number the reading expects to find. It is zero when
            // this process may not use the clipboard at all; the opening above would hardly
            // have succeeded then, and two zeros are equal.
            var open = new Open(GetClipboardSequenceNumber());
            result = read(open);

            // Before the closing: was a movement between two calls seen while blocks were
            // copied, or has the number moved since the last block was asked for?
            interrupted = open.WasInterrupted();
        }
        finally
        {
            // CloseClipboard: must follow every successful OpenClipboard, and is called on the
            // thread that opened, once, whichever way the callback ended. It returns false
            // when it fails, and the documentation says no more than that. After an opening
            // that succeeded on this thread, a failure is taken to mean that the clipboard was
            // no longer this thread's to close: someone took it over, or it was closed under us.
            closed = CloseClipboard();
        }

        // When the callback threw, its exception has left through the finally above and this
        // is not reached: nothing is returned then, so there is nothing to protect.
        if (interrupted || !closed)
        {
            throw new ClipboardBusyException();
        }

        return result;
    }

    /// <summary>Destroys the reader's window. Call on the UI thread, after capture has stopped reading.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // HwndSource.Dispose destroys the window at once when it is called on the thread that
        // made it. Nothing here closes a clipboard: a reading that is still under way closes
        // it in its own finally, on its own thread, and is not returned if that fails.
        _source.Dispose();
    }

    // GetWindowThreadProcessId returns the thread that made the window, and zero when the
    // handle is no longer a window. It leaves the process number alone in that case.
    private static uint? ProcessOf(IntPtr window) =>
        GetWindowThreadProcessId(window, out uint process) != 0 ? process : null;

    // The process's name as Windows lists it, without ".exe". Process.GetProcessById throws
    // ArgumentException when the process has ended, and the name cannot be had of every
    // process; Owner catches whatever is thrown.
    private static string NameOf(uint process)
    {
        using var found = Process.GetProcessById((int)process);
        return found.ProcessName;
    }

    // The number Windows knows a format by: fixed for a standard format, asked for by name for
    // any other. RegisterClipboardFormat returns the number a name already has, whichever
    // program registered it, and zero when it fails. No list of names is kept here: any name
    // that Wyvern.Core gives a format is asked for as it is, "Clipboard Viewer Ignore" like the
    // others, so a format added there needs nothing added here.
    //
    // A failure is thrown, as on the writing side, and never answered as "the format is not
    // there": for a marker that is on the clipboard that answer would let content be kept that
    // its program said must not be. Thrown from inside a reading, it means that nothing is
    // recorded for that copy.
    private static uint FormatId(ClipboardFormat format)
    {
        if (format.Id != 0)
        {
            return format.Id;
        }

        if (string.IsNullOrEmpty(format.Name))
        {
            throw new ArgumentException("A clipboard format has a number or a name.", nameof(format));
        }

        uint id = RegisterClipboardFormat(format.Name);
        if (id == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return id;
    }

    // The formats whose content is a block of memory, which is all that GlobalSize and
    // GlobalLock may be given: CF_UNICODETEXT and CF_DIB are memory objects, CF_HDROP is a
    // block that holds a DROPFILES structure, and a registered format (0xC000 to 0xFFFF) is
    // always put on the clipboard as a block. The content of other standard formats is another
    // kind of handle (a bitmap, a metafile, a palette), or nothing that is known here.
    private static bool IsMemoryBlock(uint id) =>
        id is UnicodeText or Dib or FileDrop or (>= FirstRegistered and <= LastRegistered);

    /// <summary>The clipboard between OpenClipboard and CloseClipboard. Only <see cref="Read{T}"/> makes one.</summary>
    private sealed class Open : IOpenClipboard
    {
        // The change number this reading expects to find: the one taken right after the
        // opening, and after every GetClipboardData the one found then. Read explains why.
        private uint _expected;

        // Set, and never cleared, when the number was found to have moved between two of this
        // reading's calls. It is a private field of the object that Read holds, which the
        // callback cannot reach: whatever the callback does afterwards, catching an exception
        // out of Bytes included, the mark is still there when Read asks.
        private bool _interrupted;

        /// <param name="opened">The clipboard's change number right after the opening.</param>
        public Open(uint opened) => _expected = opened;

        // GetClipboardSequenceNumber: a number that goes up whenever the clipboard's content
        // changes. Zero when the clipboard cannot be reached at all. The caller is given the
        // number as it stands now, not the one this reading expects.
        public uint Sequence => GetClipboardSequenceNumber();

        /// <summary>
        /// True when the change number moved between two of this reading's calls: it was seen
        /// to have moved right before a block was asked for, or it is not now what it was
        /// after the last block was asked for. <see cref="Read{T}"/> asks this once, after the
        /// callback and before the closing.
        /// </summary>
        public bool WasInterrupted() => _interrupted || GetClipboardSequenceNumber() != _expected;

        // IsClipboardFormatAvailable: nonzero when the clipboard offers the format. It reads
        // nothing and asks the owning program for nothing: a format whose content is still to
        // be made is offered all the same, and the owner is asked to make it only when the
        // content is asked for, which is GetClipboardData. So the change number is not
        // expected to move here, and nothing is bracketed. If it moved all the same, that is
        // a movement between two calls, and the next look finds it. A name whose number
        // Windows will not give throws (see FormatId): it is never taken for "not there".
        public bool Has(ClipboardFormat format) => IsClipboardFormatAvailable(FormatId(format));

        public byte[]? Bytes(ClipboardFormat format, int maxBytes, out bool tooLarge, int reserve = 0)
        {
            tooLarge = false;

            // The caller's mistakes are refused before anything is asked of the clipboard,
            // whatever is on it: a limit or a room in front that is negative, the two together
            // more than one array can hold (so that the sum further down cannot run over), and
            // a format whose content is not a block of memory.
            ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(reserve);
            if ((long)maxBytes + reserve > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "The size limit and the room in front of the block are together more than one array can hold.");
            }

            uint id = FormatId(format);
            if (!IsMemoryBlock(id))
            {
                throw new ArgumentOutOfRangeException(nameof(format), "Only text, a picture block, a file list and the formats that go by a name are copied as blocks of memory.");
            }

            if (!IsClipboardFormatAvailable(id))
            {
                return null;
            }

            // Right before the content is asked for: a change number that is not the expected
            // one moved between two of our calls, and the reading is marked as interrupted.
            // It goes on all the same; Read throws the whole of it away after the closing.
            if (GetClipboardSequenceNumber() != _expected)
            {
                _interrupted = true;
            }

            // GetClipboardData: the clipboard keeps the handle it returns. It is not freed
            // here and not left locked, and it is not used after the clipboard is closed.
            // Null when the owning program could not supply the content, and when the
            // clipboard is not open on this thread. When the content is still to be made,
            // this is where Windows asks the owner for it and waits.
            IntPtr handle = GetClipboardData(id);

            // Right after it has returned, with a handle or without: whatever the number
            // moved by inside the call was the owner making what was asked for, so the
            // number as it stands now is the one expected from here on.
            _expected = GetClipboardSequenceNumber();
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            // GlobalSize: how many bytes the block has, which can be a little more than was
            // asked for when it was made. Zero for a handle that is not a block of memory.
            // Nothing past these bytes is read.
            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0)
            {
                return null;
            }

            if (size > (ulong)maxBytes)
            {
                tooLarge = true;
                return null;
            }

            // GlobalLock: a pointer to the block's first byte, or null when it fails.
            IntPtr memory = GlobalLock(handle);
            if (memory == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                byte[] bytes = new byte[reserve + (int)size];
                Marshal.Copy(memory, bytes, reserve, (int)size);
                return bytes;
            }
            finally
            {
                // The block must be unlocked before the clipboard is closed.
                GlobalUnlock(handle);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    // Takes the handle of a block, not a pointer into it, and returns the block's size in bytes.
    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}
