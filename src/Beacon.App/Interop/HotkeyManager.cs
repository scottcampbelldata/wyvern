using System.Runtime.InteropServices;
using System.Windows.Interop;
using Beacon.Core.Settings;

namespace Beacon.App.Interop;

/// <summary>
/// Registers one system-wide hotkey and raises <see cref="Pressed"/> when it is used.
/// Create and use on the UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyId = 1;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _sink;

    // The hook, kept here for as long as the window lives, as the clipboard listener keeps its
    // own. The documentation of HwndSource says: "The actual hooks are held by a weak reference.
    // Therefore, make sure that you manage the lifetime of your hook delegate." A delegate made
    // on the spot for AddHook would be held by nothing else.
    private readonly HwndSourceHook _hook;

    private Hotkey? _current;

    public HotkeyManager()
    {
        _hook = OnMessage;

        // A message-only window: it never appears on screen and exists only to receive WM_HOTKEY.
        _sink = new HwndSource(new HwndSourceParameters("Beacon hotkey sink")
        {
            ParentWindow = MessageOnlyParent,
            WindowStyle = 0,
        });
        _sink.AddHook(_hook);
    }

    public event EventHandler? Pressed;

    /// <summary>True while a hotkey is registered.</summary>
    public bool HasHotkey => _current is not null;

    /// <summary>The hotkey that is registered, or null when none is.</summary>
    public Hotkey? Current => _current;

    /// <summary>
    /// Replaces the hotkey registered earlier. If Windows refuses the new one, which means
    /// another app already owns it, the earlier hotkey is put back so the launcher stays reachable.
    /// </summary>
    /// <param name="fallback">
    /// The hotkey to put back when none is registered now and Windows refuses the new one: the
    /// one this manager had until <see cref="Clear"/> gave it up a moment ago, to leave the key
    /// free while another manager was given its new hotkey. Null when there was none.
    /// </param>
    /// <returns>True when the new hotkey is now registered.</returns>
    public bool Register(Hotkey hotkey, Hotkey? fallback = null)
    {
        Hotkey? previous = _current ?? fallback;
        Unregister();
        if (TryRegister(hotkey))
        {
            return true;
        }

        if (previous is { } earlier)
        {
            TryRegister(earlier);
        }

        return false;
    }

    /// <summary>
    /// Gives up the hotkey: for a setting that has been taken out, and for one that is about to
    /// be changed, so that its key is free meanwhile. Does nothing when none is registered.
    /// </summary>
    public void Clear() => Unregister();

    public void Dispose()
    {
        Unregister();
        _sink.RemoveHook(_hook);
        _sink.Dispose();
    }

    private bool TryRegister(Hotkey hotkey)
    {
        if (!RegisterHotKey(_sink.Handle, HotkeyId, (uint)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey))
        {
            return false;
        }

        _current = hotkey;
        return true;
    }

    private void Unregister()
    {
        if (_current is not null)
        {
            UnregisterHotKey(_sink.Handle, HotkeyId);
            _current = null;
        }
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
