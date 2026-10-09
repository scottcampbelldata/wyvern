using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Wyvern.Core.AutoExpand;

namespace Wyvern.App.Interop;

/// <summary>
/// The low-level keyboard hook of snippet auto-expand: the only place in Wyvern that asks
/// Windows to be told of key presses. It is installed only while "snippetAutoExpand" is true.
/// While it is installed, Windows calls <c>Listening.OnKey</c> for every key that is pressed
/// or let go of in any program that runs with the owner's own rights; Windows does not call it
/// for keys typed into a program that runs as administrator, or on the sign-in and "Do you
/// want to allow" screens.
/// </summary>
/// <remarks>
/// <para>What this class does with a key press: for a key that goes down it works out which
/// window is in front, whether Ctrl, Alt or a Windows key is held, and which character the key
/// types, and hands those facts on as one <see cref="TypedKey"/>. It keeps nothing (the two
/// arrays used to work out a character are empty again before each call returns), decides
/// nothing, and never stops a key from reaching the program it was typed in: every call ends
/// by passing the key on. What is kept and what happens is decided in
/// <see cref="AutoExpander"/>.</para>
/// <para><b>Threads.</b> The page for LowLevelKeyboardProc
/// (learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc) says: "This hook is called
/// in the context of the thread that installed it. The call is made by sending a message to
/// the thread that installed the hook. Therefore, the thread that installed the hook must have
/// a message loop." Every key press in every program waits for that call, and a hook that
/// answers too slowly "is silently removed". So each hook is installed on a thread of its own
/// (<see cref="LoopThread"/>) that does nothing else: it is started by <see cref="Install"/>,
/// the hook is installed and removed on it, the callback runs on it, and it has ended when
/// <see cref="Remove"/> returns. With auto-expand off no such thread exists.
/// <see cref="Install"/> and <see cref="Remove"/> are for the thread this object was made on,
/// the UI thread, and refuse any other. The callback never touches the UI thread and never
/// waits for it: nothing in it waits, reads or writes a file, or writes the log, and nothing
/// can leave it as an exception. The first failure of a run is handed to the callback this
/// class was made with, which passes it on to be logged afterwards.</para>
/// <para>Every Windows call here is written from Microsoft's documentation and has not been
/// run by any automated test: no test may install a keyboard hook. Each call names the page
/// its details are from, all of them under learn.microsoft.com/windows/win32/api/winuser
/// unless another is given.</para>
/// </remarks>
public sealed class KeyboardHook : IKeyboardHook
{
    // SetWindowsHookExW, parameter idHook: "WH_KEYBOARD_LL 13: Installs a hook procedure that
    // monitors low-level keyboard input events."
    private const int LowLevelKeyboard = 13;

    // LowLevelKeyboardProc, parameter code: "HC_ACTION 0: The wParam and lParam parameters
    // contain information about a keyboard message." Its wParam is one of WM_KEYDOWN,
    // WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP. The numbers of the two for a key that goes down
    // are on their own pages (learn.microsoft.com/windows/win32/inputdev/wm-keydown and
    // .../wm-syskeydown).
    private const int KeyboardAction = 0;
    private const int KeyDownMessage = 0x0100;       // WM_KEYDOWN
    private const int SystemKeyDownMessage = 0x0104; // WM_SYSKEYDOWN: a key that goes down while Alt is held, and F10

    // KBDLLHOOKSTRUCT, member flags: "LLKHF_INJECTED 0x00000010: Test the event-injected (from
    // any process) flag", and bit 5, LLKHF_ALTDOWN: "The context code. The value is 1 if the
    // ALT key is pressed".
    private const uint InjectedFlag = 0x10;
    private const uint AltDownFlag = 0x20;

    // KBDLLHOOKSTRUCT, what lParam points to: DWORD vkCode; DWORD scanCode; DWORD flags; DWORD
    // time; ULONG_PTR dwExtraInfo. Only the first three are used. Each is read straight from the
    // memory Windows hands over, with Marshal.ReadInt32 at these offsets, so that no object is
    // made for a key press. Three DWORDs come first, which puts them at 0, 4 and 8 on x86, x64
    // and arm64 alike.
    private const int VirtualKeyOffset = 0;
    private const int ScanCodeOffset = 4;
    private const int FlagsOffset = 8;

    // ToUnicodeEx, parameter wFlags: "If bit 2 is set, keyboard state is not changed (Windows
    // 10, version 1607 and newer)". Bit 2 is the value 4.
    private const uint LeaveKeyboardStateAlone = 0x4;

    // The first Windows on which that flag is honoured: Windows 10, version 1607, build 14393.
    private const int FirstBuildWithThatFlag = 14393;

    // ToUnicodeEx, parameter lpKeyState: "If the high-order bit of a byte is set, the key is
    // down. The low bit, if set, indicates that the key is toggled on. In this function, only
    // the toggle bit of the CAPS LOCK key is relevant."
    private const byte Down = 0x80;
    private const byte ToggledOn = 0x01;

    // The thread this object was made on, the UI thread, by way of its dispatcher.
    private readonly Dispatcher _made = Dispatcher.CurrentDispatcher;
    private readonly int _ownProcess = Environment.ProcessId;

    // Where the first failure of a run inside the callback is handed. Called on a hook's
    // thread. Whether one has been handed is 1 or 0, only ever changed with Interlocked.
    private readonly Action<Exception> _failed;
    private int _failureTold;

    // The hook that is installed, with its thread. UI thread only. Null while there is none.
    private Listening? _listening;

    /// <param name="failed">
    /// Handed what went wrong inside the callback, once for a run at most, from inside the
    /// callback and so on the hook's thread. It must only pass the exception on to be logged
    /// later, and return at once without waiting for another thread: the log is not written
    /// while a key press is waiting.
    /// </param>
    public KeyboardHook(Action<Exception> failed)
    {
        _failed = failed;
    }

    public int Installs { get; private set; }

    public int Threads { get; private set; }

    public void Install(Action<TypedKey> keyDown)
    {
        // Dispatcher.VerifyAccess throws InvalidOperationException on any thread but the one
        // this object was made on.
        _made.VerifyAccess();

        // Without the flag that leaves the keyboard state alone, working out a key's character
        // would swallow the owner's dead keys (the accent typed before a letter). No thread is
        // started for a hook that will not be installed.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstBuildWithThatFlag))
        {
            throw new PlatformNotSupportedException("Snippet auto-expand needs Windows 10, version 1607, or later.");
        }

        if (_listening is not null)
        {
            Remove();
        }

        var listening = new Listening(this, keyDown);
        Threads++;

        // Returns once the hook is installed on its new thread, or throws what Windows said.
        listening.Start();
        _listening = listening;
        Installs++;
    }

    public bool Remove()
    {
        _made.VerifyAccess();
        if (_listening is not { } listening)
        {
            return false;
        }

        _listening = null;

        // Returns once the hook is removed and its thread has ended. The answer is false when
        // the call that gives the hook back to Windows failed, or the loop had ended before
        // that call was made (see Listening.Unhook). It is not taken to mean that Windows
        // removed the hook by itself: the documentation does not say that.
        return listening.Stop();
    }

    // Called on a hook's thread.
    private void Tell(Exception error)
    {
        if (Interlocked.Exchange(ref _failureTold, 1) != 0)
        {
            return;
        }

        try
        {
            _failed(error);
        }
        catch (Exception)
        {
            // Nowhere left to say so, and nothing may leave the callback. Nothing has been
            // said either, so it does not count as said: the mark is set back, and the next
            // failure tries again.
            Interlocked.Exchange(ref _failureTold, 0);
        }
    }

    // One hook with the thread it lives on. Start and Stop are called on the UI thread;
    // everything else here runs on the hook's own thread and is touched by no other.
    private sealed class Listening
    {
        private readonly KeyboardHook _owner;
        private readonly Action<TypedKey> _keyDown;

        // The callback, kept here for as long as this object lives, which is at least until
        // Stop has returned. Windows is given a pointer made from this delegate, and the page
        // for SetWindowsHookEx says: "In .NET apps, you must ensure the callback is not moved
        // around by the garbage collector". A delegate made on the spot for the call would be
        // held by nothing and could be collected while Windows still calls it.
        private readonly HookProcedure _procedure;

        // The two arrays ToUnicodeEx is given, made once. With the three values of a key read
        // straight from the memory Windows hands over, and a TypedKey that is a structure, the
        // callback makes no object on its way. Both arrays are cleared again before the callback
        // returns, so that nothing of a key is left in this object between two keys or after
        // the hook is removed.
        //
        // Both are allocated pinned, so that the garbage collector never moves them. A
        // collection that compacts the memory copies an ordinary array and leaves the old copy,
        // with a key in it, in freed memory, where clearing this one would not reach it. Each
        // array is zeroed when it is made.
        private readonly byte[] _keyState = GC.AllocateArray<byte>(256, pinned: true);
        private readonly char[] _characters = GC.AllocateArray<char>(8, pinned: true);

        private LoopThread? _thread;
        private IntPtr _hook;

        // Whether UnhookWindowsHookEx answered nonzero. It stays false when the call was never
        // made, because the loop had ended before Stop got to it. Written on the hook's thread,
        // and read on the UI thread once that thread has ended.
        private bool _known;

        public Listening(KeyboardHook owner, Action<TypedKey> keyDown)
        {
            _owner = owner;
            _keyDown = keyDown;
            _procedure = OnKey;
        }

        // UI thread.
        public void Start() => _thread = LoopThread.Start("Wyvern keyboard hook", Hook, Unhook);

        // UI thread.
        public bool Stop()
        {
            _thread!.Stop();
            GC.KeepAlive(_procedure);
            return _known;
        }

        // On the hook's thread, before its message loop runs.
        private void Hook()
        {
            // SetWindowsHookExW(idHook, lpfn, hmod, dwThreadId). The table of scopes on its
            // page says WH_KEYBOARD_LL is "Global only", so dwThreadId is 0. For hmod it asks
            // for the module that holds the procedure, and warns: "An error may occur if the
            // hMod parameter is NULL and the dwThreadId parameter is zero". The procedure is
            // in this program, so the handle of the program's own file is given, which
            // GetModuleHandleW(NULL) returns
            // (learn.microsoft.com/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandlew:
            // "If this parameter is NULL, GetModuleHandle returns a handle to the file used to
            // create the calling process"). Nothing is loaded into any other program: the page
            // for LowLevelKeyboardProc says "the WH_KEYBOARD_LL hook is not injected into
            // another process. Instead, the context switches back to the process that
            // installed the hook". "If the function fails, the return value is NULL."
            _hook = SetWindowsHookEx(LowLevelKeyboard, _procedure, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        // On the hook's thread, from inside its message loop, so never while OnKey runs.
        private void Unhook()
        {
            // UnhookWindowsHookEx(hhk): "If the function succeeds, the return value is
            // nonzero." Of a failure its page says only "If the function fails, the return
            // value is zero". What a hook that was too slow comes to is on the page for
            // LowLevelKeyboardProc: when the procedure does not answer in time, "on Windows 7
            // and later, the hook is silently removed without being called. There is no way for
            // the application to know whether the hook is removed." So nothing is known of
            // such a hook from this call, and none of it is read into the answer. _known false
            // says that the call that gives the hook back to Windows failed, or that the loop
            // had ended before Stop got to it, and nothing more.
            IntPtr hook = _hook;
            _hook = IntPtr.Zero;
            _known = UnhookWindowsHookEx(hook);
        }

        // LowLevelKeyboardProc(nCode, wParam, lParam). Called by Windows on the hook's thread.
        // "If nCode is less than zero, the hook procedure must pass the message to the
        // CallNextHookEx function without further processing". Otherwise wParam is the
        // message and lParam points to a KBDLLHOOKSTRUCT. The key is always passed on: this
        // hook never returns a value of its own, so it never keeps a key from the program it
        // was typed in.
        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code == KeyboardAction)
                {
                    long message = wParam.ToInt64();
                    if (message is KeyDownMessage or SystemKeyDownMessage)
                    {
                        // KBDLLHOOKSTRUCT: DWORD vkCode; DWORD scanCode; DWORD flags; DWORD time;
                        // ULONG_PTR dwExtraInfo. The first three are read here, as int, and the
                        // cast to uint keeps all 32 bits.
                        uint virtualKey = unchecked((uint)Marshal.ReadInt32(lParam, VirtualKeyOffset));
                        uint scanCode = unchecked((uint)Marshal.ReadInt32(lParam, ScanCodeOffset));
                        uint flags = unchecked((uint)Marshal.ReadInt32(lParam, FlagsOffset));
                        _keyDown(Read(virtualKey, scanCode, flags));
                    }
                }
            }
            catch (Exception ex)
            {
                // Nothing may leave a procedure that Windows calls, and the log is not written
                // from here: every key press in every program is waiting for this to return.
                // The first failure of a run is handed on, to be logged once this has
                // returned. Without that, a mistake in one of the Windows calls above would
                // only ever show as keywords that are not replaced.
                _owner.Tell(ex);
            }

            // CallNextHookEx(hhk, nCode, wParam, lParam): "hhk: This parameter is ignored."
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // Gathers the facts about one key that went down. Nothing here waits: each call below
        // reads a value that Windows already has. This runs on the hook's thread, which owns
        // no window the owner can type in and reads no key messages, so nothing below may
        // depend on what this thread has seen: each call is asked about another thread, or
        // about the keyboard as a whole, and what is relied on is said at the call.
        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)
        {
            // GetForegroundWindow: "The foreground window can be NULL in certain
            // circumstances". GetWindowThreadProcessId returns the thread that made the
            // window, and zero for a handle that is no longer a window; it then leaves the
            // process number alone. Neither depends on the thread that asks.
            IntPtr window = GetForegroundWindow();
            uint process = 0;
            uint thread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out process);
            if (thread == 0)
            {
                window = IntPtr.Zero;
            }

            // GetAsyncKeyState: "Determines whether a key is up or down at the time the
            // function is called", and "a negative value indicates that the key is currently
            // down". That is the keyboard as it is, whichever thread asks. It answers zero,
            // which reads as "up", when the program in front runs as administrator; no key
            // typed there reaches this hook anyway. The page for LowLevelKeyboardProc warns
            // that the state of the key this call is about has not been updated yet; the keys
            // asked about here are other keys, held from before. Alt is also taken from the
            // event itself (AltDownFlag, in the flags read at offset 8).
            bool chorded = IsDown(KeyCodes.Control) || IsDown(KeyCodes.Alt) || (flags & AltDownFlag) != 0
                || IsDown(KeyCodes.LeftWindows) || IsDown(KeyCodes.RightWindows);

            char character = '\0';
            bool deadKey = false;
            if (window != IntPtr.Zero && virtualKey is > 0 and < 256)
            {
                // The state ToUnicodeEx is to translate the key under: this key down, Shift
                // as it is held, and Caps Lock as it is switched. Ctrl and Alt are left out on
                // purpose: a key pressed with either held is never kept, whatever it types.
                Array.Clear(_keyState);
                _keyState[virtualKey] = Down;
                _keyState[KeyCodes.Shift] = IsDown(KeyCodes.Shift) ? Down : (byte)0;
                _keyState[KeyCodes.LeftShift] = IsDown(KeyCodes.LeftShift) ? Down : (byte)0;
                _keyState[KeyCodes.RightShift] = IsDown(KeyCodes.RightShift) ? Down : (byte)0;

                // GetKeyState: "If the low-order bit is 1, the key is toggled." This is the
                // one value here that the documentation ties to the asking thread: "The key
                // status returned from this function changes as a thread reads key messages
                // from its message queue", and this thread reads none. That Caps Lock is
                // current here all the same, also after it was switched while another program
                // was in front, is relied on without the documentation saying so, and is for
                // the manual checklist. GetAsyncKeyState does not give the switched state.
                _keyState[KeyCodes.CapsLock] = (GetKeyState(KeyCodes.CapsLock) & 1) != 0 ? ToggledOn : (byte)0;

                // ToUnicodeEx(wVirtKey, wScanCode, lpKeyState, pwszBuff, cchBuff, wFlags,
                // dwhkl). Everything it translates from is given to it: the key, the state
                // made above, and the keyboard layout, which is that of the thread whose
                // window is in front (GetKeyboardLayout: "The identifier of the thread to
                // query"), asked for at every key and never kept. With bit 2 of wFlags set
                // "keyboard state is not changed", so the call takes nothing from the program
                // that is typed in. Return value: below zero for a dead key, zero for a key
                // that types nothing, otherwise how many UTF-16 code units were written.
                // Exactly one is a character; more than one is handed on as none.
                int written = ToUnicodeEx(
                    virtualKey,
                    scanCode,
                    _keyState,
                    _characters,
                    _characters.Length,
                    LeaveKeyboardStateAlone,
                    GetKeyboardLayout(thread));
                deadKey = written < 0;
                character = written == 1 ? _characters[0] : '\0';

                // Nothing of the key is left lying in this object: not what it typed, and not
                // its code or the Shift state it was translated under. Both arrays are empty
                // whenever this returns.
                Array.Clear(_characters);
                Array.Clear(_keyState);
            }

            return new TypedKey(
                (int)virtualKey,
                character,
                deadKey,
                chorded,
                injected: (flags & InjectedFlag) != 0,
                inWyvern: process == (uint)_owner._ownProcess,
                window);
        }

        private static bool IsDown(int virtualKey) => GetAsyncKeyState(virtualKey) < 0;
    }

    // HOOKPROC, in the form of LowLevelKeyboardProc: LRESULT CALLBACK (int nCode, WPARAM wParam, LPARAM lParam).
    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);

    // CharSet.Unicode makes this SetWindowsHookExW. No text passes through it.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProcedure procedure, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    // CharSet.Unicode makes this GetModuleHandleW. It is only ever given null.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    // The two arrays are passed as they are: the key state as 256 bytes for Windows to read,
    // the characters as UTF-16 for Windows to write into. The fifth argument is the second
    // array's length in characters.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey,
        uint scanCode,
        [In] byte[] keyState,
        [Out] char[] characters,
        int capacity,
        uint flags,
        IntPtr layout);
}
