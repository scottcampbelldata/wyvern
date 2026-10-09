using System.ComponentModel;
using System.Runtime.InteropServices;
using Wyvern.Core.AutoExpand;
using Wyvern.Core.Platform;

namespace Wyvern.App.Interop;

/// <summary>
/// Remembers which window the owner was working in when the launcher was asked to show, and
/// can give that window the focus back and press Ctrl+V for it. For auto-expand it also
/// presses the keys that put a snippet in the place of a keyword typed in the window in front:
/// Backspace, once for each character of the keyword, and then Ctrl+V. This is the only place
/// in Wyvern that sends a key press, and Ctrl+V and Backspace are the only keys it sends. Use
/// on the UI thread.
/// </summary>
public sealed class ForegroundWindowTarget : IPasteTarget, ITypingTarget
{
    private const uint Keyboard = 1;              // INPUT_KEYBOARD
    private const uint KeyUp = 0x0002;            // KEYEVENTF_KEYUP
    private const uint VirtualKeyToScanCode = 0;  // MAPVK_VK_TO_VSC
    private const ushort BackspaceKey = 0x08;     // VK_BACK
    private const ushort ShiftKey = 0x10;         // VK_SHIFT: either Shift key
    private const ushort ControlKey = 0x11;       // VK_CONTROL: either Ctrl key
    private const ushort AltKey = 0x12;           // VK_MENU: either Alt key
    private const ushort VKey = 0x56;             // the V key
    private const ushort LeftWindowsKey = 0x5B;   // VK_LWIN
    private const ushort RightWindowsKey = 0x5C;  // VK_RWIN

    // A window class name is at most 256 characters long, and GetClassName writes a zero
    // character after it. With room for both, no name is ever cut short, so a longer name can
    // never be mistaken for one of the short ones below.
    private const int ClassNameCapacity = 257;

    // The classes of the shell's own windows: the desktop is Progman or WorkerW, the taskbar is
    // Shell_TrayWnd, and the taskbar on each further monitor is Shell_SecondaryTrayWnd. Ctrl+V
    // with text does nothing in any of them.
    private static readonly string[] ShellClasses =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    ];

    private IntPtr _remembered;

    /// <summary>The window that was remembered when the launcher last opened, or zero. For the commands that move it.</summary>
    public IntPtr Remembered => _remembered;

    /// <summary>
    /// Call just before the launcher shows itself. Remembers the window in front when a snippet
    /// can be pasted into it. Otherwise nothing is remembered, and a paste then finds no window:
    /// that is so when no window is in front, when the one in front is Wyvern's own (the
    /// launcher was opened from the tray menu), and when it is the desktop or the taskbar.
    /// What was remembered the time before is never kept.
    /// </summary>
    public void Remember()
    {
        // Forgotten first, so every way out of this method except the last line leaves nothing
        // remembered. A handle kept from an earlier opening may be hours old, and Windows uses
        // handles again: by now it could name a different window altogether.
        _remembered = IntPtr.Zero;

        // GetForegroundWindow: the window the owner is working in. It can be null, for example
        // while a window is losing activation.
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return;
        }

        if (!CanBePastedInto(window))
        {
            return;
        }

        _remembered = window;
    }

    public bool Activate()
    {
        // IsWindow: zero when the handle no longer names a window.
        if (_remembered == IntPtr.Zero || !IsWindow(_remembered))
        {
            return false;
        }

        // IsIconic: nonzero for a minimised window. SetForegroundWindow does not put such a
        // window back on the screen, so there is nothing there for the owner to paste into
        // and see. A minimised window counts as no window.
        if (IsIconic(_remembered))
        {
            return false;
        }

        // SetForegroundWindow: nonzero when Windows says the window was brought to the front.
        // That answer is not the last word: Windows may only flash the taskbar button. SendPaste
        // looks again before it presses anything.
        return SetForegroundWindow(_remembered);
    }

    public void SendPaste()
    {
        if (_remembered == IntPtr.Zero)
        {
            throw new NoWindowRememberedException();
        }

        // The key press goes to whichever window is in front now, so that is the one to check.
        // Activate ran a moment ago, but Windows may have refused to switch, or another window
        // may have come to the front since.
        if (!BelongsWithRemembered(GetForegroundWindow()))
        {
            throw new AnotherWindowInFrontException();
        }

        // SendInput does not reset the keyboard: a key the owner is holding down still counts.
        // With Shift, Alt or a Windows key held, the window would see another shortcut, such as
        // Ctrl+Shift+V. Nothing is pressed then, and the text stays on the clipboard.
        if (IsDown(ShiftKey) || IsDown(AltKey) || IsDown(LeftWindowsKey) || IsDown(RightWindowsKey))
        {
            throw new ModifierKeyHeldException();
        }

        Input[] presses;
        if (IsDown(ControlKey))
        {
            // The owner is holding Ctrl, so V alone makes Ctrl+V. Ctrl is not pressed again,
            // and above all it is not released: a Ctrl-up sent here would leave Windows
            // believing Ctrl is up while the owner still holds it down.
            presses =
            [
                Key(VKey, 0),
                Key(VKey, KeyUp),
            ];
        }
        else
        {
            presses =
            [
                Key(ControlKey, 0),
                Key(VKey, 0),
                Key(VKey, KeyUp),
                Key(ControlKey, KeyUp),
            ];
        }

        Send(presses);
    }

    /// <summary>
    /// For auto-expand. True when <paramref name="window"/>, the one a keyword was typed in, is
    /// the window in front at this moment and a text can be pasted into it. Nothing is changed
    /// by asking: no focus is moved and nothing is remembered.
    /// </summary>
    public bool IsInFront(nint window) =>
        window != IntPtr.Zero
        && GetForegroundWindow() == window
        && CanBePastedInto(window);

    /// <summary>For auto-expand. True while a Shift, Ctrl, Alt or Windows key is held down.</summary>
    public bool ModifierHeld() => AnyModifierDown();

    /// <summary>
    /// For auto-expand. The target of one paste into <paramref name="window"/>, whose key press
    /// is <paramref name="erase"/> Backspaces and then Ctrl+V. It has nothing to do with the
    /// window that <see cref="Remember"/> keeps for the launcher, and does not change it.
    /// </summary>
    public IPasteTarget Replacing(nint window, int erase) => new TypedKeyword(this, window, erase);

    // The window a keyword was typed in, for the one paste that takes the keyword's place.
    private sealed class TypedKeyword(ForegroundWindowTarget owner, IntPtr window, int erase) : IPasteTarget
    {
        // Nothing is moved: the window is in front already, or the keyword is left alone.
        public bool Activate() => owner.IsInFront(window);

        public void SendPaste()
        {
            // The key events are made first, MapVirtualKey calls and all, so that the looks
            // below are the last thing that happens before the call to Windows. They are
            // made whether or not a look passes: nothing is sent until the call at the end.
            //
            // The Backspaces and the Ctrl+V are handed to Windows in one call. The page for
            // SendInput says of the events of one call: "These events are not interspersed with
            // other keyboard or mouse input events inserted either by the user (with the
            // keyboard or mouse) or by calls to keybd_event, mouse_event, or other calls to
            // SendInput." So nothing the owner types can land between the keyword being taken
            // away and the snippet arriving, and the keyword is never taken away by itself.
            var presses = new Input[(Math.Max(erase, 0) * 2) + 4];
            int next = 0;
            for (int i = 0; i < erase; i++)
            {
                presses[next++] = Key(BackspaceKey, 0);
                presses[next++] = Key(BackspaceKey, KeyUp);
            }

            presses[next++] = Key(ControlKey, 0);
            presses[next++] = Key(VKey, 0);
            presses[next++] = Key(VKey, KeyUp);
            presses[next] = Key(ControlKey, KeyUp);

            // Looked at once more at the very last moment, whatever the caller has looked at.
            // The keys go to whichever window is in front now, and Backspace sent to another
            // window would take away something of the owner's. A keyword that cannot be
            // replaced any more is left alone without a word, so the paste is called off here.
            // The paste service does not take PasteCalledOffException for a key press that
            // Windows refused: it presses nothing, puts the clipboard back and says nothing.
            if (!owner.IsInFront(window))
            {
                throw new PasteCalledOffException();
            }

            // With Ctrl held, Backspace takes away a whole word; with Shift, Alt or a Windows
            // key held, Ctrl+V is another shortcut. Nothing is pressed then, and the paste is
            // called off in the same way. This look is the last: nothing but the call below
            // stands between it and Windows.
            if (AnyModifierDown())
            {
                throw new PasteCalledOffException();
            }

            Send(presses);
        }
    }

    // True when a text can be pasted into the window: it is a window, it is not one of
    // Wyvern's own, and it is not the desktop or the taskbar.
    private static bool CanBePastedInto(IntPtr window)
    {
        // GetWindowThreadProcessId returns the thread that made the window, and zero when the
        // handle is no longer a window. It leaves the process number alone in that case.
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0)
        {
            return false;
        }

        // One of Wyvern's own windows. Opening the launcher from the tray menu ends up here:
        // the tray icon puts a hidden window of its own in front before it shows the menu.
        if (process == (uint)Environment.ProcessId)
        {
            return false;
        }

        return !IsShellWindow(window);
    }

    // True for the desktop and the taskbar, and also when the window's class cannot be read,
    // which means the window has gone. None of these is a window to paste into.
    private static bool IsShellWindow(IntPtr window)
    {
        // GetShellWindow: the shell's desktop window, or null when no shell is running. The
        // window passed in is never null, so a null answer here matches nothing.
        if (window == GetShellWindow())
        {
            return true;
        }

        // GetClassName returns the number of characters it wrote, not counting the zero
        // character at the end, and zero when it fails.
        char[] name = new char[ClassNameCapacity];
        int length = GetClassName(window, name, name.Length);
        if (length <= 0)
        {
            return true;
        }

        string className = new(name, 0, length);
        foreach (string shellClass in ShellClasses)
        {
            // Windows does not tell class names apart by upper and lower case.
            if (string.Equals(className, shellClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // True when the window is the remembered one, or was made by the same thread as the
    // remembered one: a dialog or a popup of the same program. Call only with a window remembered.
    private bool BelongsWithRemembered(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (window == _remembered)
        {
            return true;
        }

        // GetWindowThreadProcessId returns zero for a handle that is no longer a window. Two
        // zeros are not the same thread, so the remembered window's answer is checked for zero.
        uint rememberedThread = GetWindowThreadProcessId(_remembered, out _);
        uint thread = GetWindowThreadProcessId(window, out _);
        return rememberedThread != 0 && thread == rememberedThread;
    }

    // GetAsyncKeyState: the top bit of the answer is set while the key is down, which makes the
    // number negative. The other bits say nothing reliable. A failed call returns zero, which
    // reads here as not down.
    private static bool IsDown(ushort virtualKey) => GetAsyncKeyState(virtualKey) < 0;

    private static bool AnyModifierDown() =>
        IsDown(ShiftKey) || IsDown(ControlKey) || IsDown(AltKey) || IsDown(LeftWindowsKey) || IsDown(RightWindowsKey);

    // The virtual key says which key it is. The scan code is filled in as well because some
    // programs, Remote Desktop and virtual machines among them, read that instead. MapVirtualKey
    // returns zero when the keyboard layout has no such key, and for Ctrl it gives the left one.
    private static Input Key(ushort virtualKey, uint flags) => new()
    {
        Type = Keyboard,
        Data = new InputData
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = (ushort)MapVirtualKey(virtualKey, VirtualKeyToScanCode),
                Flags = flags,
            },
        },
    };

    // SendInput returns how many of the events it put into the input stream. The third
    // argument is the size of one INPUT; Windows refuses the call when it is wrong.
    private static void Send(Input[] presses)
    {
        uint sent = SendInput((uint)presses.Length, presses, Marshal.SizeOf<Input>());
        if (sent != presses.Length)
        {
            // The error number of the call that failed, taken before another call is made, so
            // that the exception still carries it.
            int error = Marshal.GetLastWin32Error();

            // The page for SendInput does not say that a call can insert some of its events
            // and not the rest. If one ever does, a Ctrl that Wyvern pressed may have gone down
            // without coming up, and every key the owner then pressed would be a shortcut. So
            // one more call lets that Ctrl go, and its result is not looked at: the failure is
            // thrown either way. It is sent only when Wyvern's own Ctrl key-down stands among
            // the events that went in: the first one among these events has an index of 0 or
            // more, and below the number inserted. For a typed keyword that key-down stands
            // behind the Backspaces, so when Windows took only Backspaces no Ctrl went down and
            // none is let go. When nothing was inserted, nothing was pressed and nothing more
            // is sent. Nor is anything sent when these events held no Ctrl key-down of Wyvern's
            // own: the launcher's paste does not press Ctrl while the owner holds it, and a
            // Ctrl key-up there would let go, for Windows, of a key the owner is still holding.
            // What was handed over and what went in decide this, not the keyboard.
            int ownControl = IndexOfOwnControlDown(presses);
            if (ownControl >= 0 && ownControl < sent)
            {
                _ = SendInput(1, [Key(ControlKey, KeyUp)], Marshal.SizeOf<Input>());
            }

            throw new Win32Exception(error);
        }
    }

    // The index of the first key-down of the Ctrl key among the events: the Ctrl key, and no
    // key-up flag. -1 when there is none. Read from the events themselves. No Windows call is
    // made, and no key state is asked.
    private static int IndexOfOwnControlDown(Input[] presses)
    {
        for (int i = 0; i < presses.Length; i++)
        {
            if (presses[i].Data.Keyboard.VirtualKey == ControlKey && (presses[i].Data.Keyboard.Flags & KeyUp) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    // CharSet.Unicode makes this GetClassNameW and passes the array as it is, as UTF-16
    // characters, for Windows to write into. The last argument is the array's length in characters.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    // CharSet.Unicode makes this MapVirtualKeyW. No text passes through it.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    // INPUT. Windows checks the size it is told, so the layout has to match exactly: the type,
    // then the largest of the three kinds of input, which is the mouse one.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    // MOUSEINPUT. Never filled in; it is here only to give the structure its full size.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // KEYBDINPUT
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

// SendPaste refuses to press anything in three situations. Each has a type of its own, because
// the log writes the type of an exception and never its message, and one type for all three
// would make them impossible to tell apart. The text of each is fixed.

/// <summary>No window was remembered to paste into: there was none when the launcher opened, or it has gone.</summary>
public sealed class NoWindowRememberedException : InvalidOperationException
{
    public NoWindowRememberedException()
        : base("Ctrl+V was not pressed: no window is remembered to paste into.")
    {
    }
}

/// <summary>The window in front is not the one the launcher was opened from, and Ctrl+V would go to it.</summary>
public sealed class AnotherWindowInFrontException : InvalidOperationException
{
    public AnotherWindowInFrontException()
        : base("Ctrl+V was not pressed: the window in front is not the one the launcher was opened from.")
    {
    }
}

/// <summary>Shift, Alt or a Windows key is held down, which would turn Ctrl+V into another shortcut.</summary>
public sealed class ModifierKeyHeldException : InvalidOperationException
{
    public ModifierKeyHeldException()
        : base("Ctrl+V was not pressed: Shift, Alt or the Windows key was being held down.")
    {
    }
}
