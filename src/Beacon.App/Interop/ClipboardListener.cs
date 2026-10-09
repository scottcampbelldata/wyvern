using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Beacon.Core.Platform;

namespace Beacon.App.Interop;

/// <summary>
/// Tells when the clipboard's content has changed, and nothing more: it never looks at the
/// clipboard itself. Windows sends a message to a window that has asked for it; this is that
/// window. While it is stopped no message comes, so a Beacon whose clipboard capture is paused
/// is told nothing about what the owner copies. Create, use and dispose on the UI thread:
/// <see cref="Start"/>, <see cref="Stop"/> and <see cref="Dispose"/> throw
/// <see cref="InvalidOperationException"/> on any other thread.
/// </summary>
public sealed class ClipboardListener : IClipboardListener, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;  // WM_CLIPBOARDUPDATE
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _sink;

    // The hook, kept here for as long as the window lives. The documentation of HwndSource
    // says that it holds its hooks by a weak reference and that the caller must keep the
    // delegate alive. A delegate made on the spot for AddHook would be held by nothing else.
    private readonly HwndSourceHook _hook;

    private readonly Action<string, Exception> _report;

    // Read by the window procedure on the UI thread. Stop clears it first, on whichever thread
    // it is called, so it is volatile.
    private volatile Action? _changed;
    private bool _listening;
    private bool _disposed;

    /// <param name="report">
    /// Receives fixed words and an exception for the log: when the callback given to
    /// <see cref="Start"/> throws, and when Windows would not take the window off its list.
    /// </param>
    public ClipboardListener(Action<string, Exception> report)
    {
        _report = report;
        _hook = OnMessage;

        // A message-only window: it never appears on screen and exists only to receive WM_CLIPBOARDUPDATE.
        _sink = new HwndSource(new HwndSourceParameters("Beacon clipboard listener")
        {
            ParentWindow = MessageOnlyParent,
            WindowStyle = 0,
        });
        _sink.AddHook(_hook);
    }

    public void Start(Action changed)
    {
        // Dispatcher.VerifyAccess throws InvalidOperationException on any thread but the one
        // that made the window. The window's messages come on that thread only, and the fields
        // here are that thread's. A call from another thread is the wiring's mistake, and it is
        // an exception that the caller reports, not something that seems to have worked.
        _sink.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_listening)
        {
            // AddClipboardFormatListener: puts the window on the list of those that are sent
            // WM_CLIPBOARDUPDATE after every change of the clipboard's content. It returns
            // false when it fails. Nothing is sent for what is on the clipboard already.
            if (!AddClipboardFormatListener(_sink.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _listening = true;
        }

        _changed = changed;
    }

    public void Stop()
    {
        // Cleared first, before anything that can fail and on whichever thread this is: from
        // here on no clipboard change is passed on, so nothing is read, whatever happens below.
        _changed = null;

        // As in Start. On the wrong thread this throws with the window still on the list, and
        // the caller is told so by the exception.
        _sink.Dispatcher.VerifyAccess();
        if (_listening)
        {
            _listening = false;

            // RemoveClipboardFormatListener: takes the window off that list, after which no
            // WM_CLIPBOARDUPDATE comes. It returns false when it fails. Then the window may
            // still be told of changes; with the callback cleared above they go nowhere, and
            // the failure is reported in fixed words. The error is read at once, before any
            // other call replaces it. A later Start asks Windows again, and its caller reports
            // what that answers.
            if (!RemoveClipboardFormatListener(_sink.Handle))
            {
                Report(
                    "The clipboard listener could not be removed; clipboard changes are no longer passed on",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // On the wrong thread this throws, with the callback cleared and nothing else done, so
        // that a later call on the right thread can still finish the job.
        Stop();
        _disposed = true;
        _sink.RemoveHook(_hook);
        _sink.Dispose();
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            handled = true;
            try
            {
                _changed?.Invoke();
            }
            catch (Exception ex)
            {
                // Nothing may throw out of a window message.
                Report("A clipboard change could not be passed on", ex);
            }
        }

        return IntPtr.Zero;
    }

    // The report is the caller's code and can fail too. That must not become an exception out
    // of the window procedure, nor out of Stop or Dispose.
    private void Report(string words, Exception error)
    {
        try
        {
            _report(words, error);
        }
        catch (Exception)
        {
            // Nothing more can be done about a log that is broken.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);
}
