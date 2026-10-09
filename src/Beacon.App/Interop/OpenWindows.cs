using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Beacon.Core.Commands;

namespace Beacon.App.Interop;

/// <summary>
/// The windows that are open on the desktop, as Alt+Tab would list them, and bringing one to
/// the front. A window's title is read to be shown in the launcher and for nothing else: it
/// is not logged and not kept. Use on the UI thread.
/// </summary>
public sealed class OpenWindows : IWindowList
{
    /// <summary>What the icon key of a window's program starts with: the program's file follows.</summary>
    public const string IconKeyPrefix = "program:";

    // A title longer than this is cut: it is only shown.
    private const int MostTitle = 512;

    // What each program is called, by its file, so that its file is asked once.
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<OpenWindow> List()
    {
        var found = new List<OpenWindow>();
        try
        {
            WindowCallback each = (window, _) =>
            {
                if (Listed(window) is { } open)
                {
                    found.Add(open);
                }

                return true;
            };

            // EnumWindows gives the windows from the front to the back, which is the order
            // they were last used in.
            _ = EnumWindows(each, IntPtr.Zero);
            GC.KeepAlive(each);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // What was found is listed.
        }

        return found;
    }

    public void Activate(nint handle)
    {
        if (!IsWindow(handle))
        {
            return;
        }

        // A minimised window is put back on the screen first: SetForegroundWindow does not do that.
        if (IsIconic(handle))
        {
            _ = ShowWindow(handle, ShowRestored);
        }

        _ = SetForegroundWindow(handle);
    }

    public void Close(nint handle)
    {
        // WM_CLOSE is what the window's own close button sends: the program decides what to do.
        if (IsWindow(handle))
        {
            _ = PostMessage(handle, CloseMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void ForceQuit(nint handle)
    {
        if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out uint process) == 0 || process == (uint)Environment.ProcessId)
        {
            return;
        }

        // The window of an app from the Store belongs to a program that Windows runs to
        // frame every such app. Ending that would take the windows of all of them: the
        // app's own program is the one that is ended, or nothing is.
        if (IsFrameHost(process))
        {
            process = FramedProgram(handle, process);
            if (process == 0)
            {
                throw new WindowNotEndedException("The program of the window could not be told from the one that frames it.");
            }
        }

        // The desktop and the taskbar are a program too, and File Explorer's windows are
        // its windows. Ending it would take them along, so it is left alone.
        IntPtr shell = GetShellWindow();
        if (shell != IntPtr.Zero && GetWindowThreadProcessId(shell, out uint desktop) != 0 && desktop == process)
        {
            throw new WindowNotEndedException("The window belongs to the Windows desktop, which is not ended.");
        }

        // Ended at once, as End task in Task Manager does. A program that runs with more
        // rights than Beacon cannot be ended from here, which throws and is reported.
        using var program = Process.GetProcessById((int)process);
        program.Kill();
    }

    private static bool IsFrameHost(uint process) =>
        FileOf(process) is { } file && Path.GetFileName(file).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    // The program of the app inside a frame: the one that a window within the frame belongs
    // to and that is not the frame's own. Zero when there is none, as for an app that is
    // suspended.
    private static uint FramedProgram(IntPtr frame, uint frameHost)
    {
        uint found = 0;
        WindowCallback each = (child, _) =>
        {
            if (GetWindowThreadProcessId(child, out uint owner) != 0 && owner != frameHost)
            {
                found = owner;
                return false;
            }

            return true;
        };
        _ = EnumChildWindows(frame, each, IntPtr.Zero);
        GC.KeepAlive(each);
        return found;
    }

    // A window is listed when Alt+Tab would list it: it is shown, it is not hidden by Windows
    // (a window of another desktop, or a suspended app's), it has a title, it is not a tool
    // window, and it is a window in its own right and not one that another window owns,
    // unless it asks to be listed all the same. Beacon's own windows are left out.
    private OpenWindow? Listed(IntPtr window)
    {
        if (!IsWindowVisible(window) || Cloaked(window))
        {
            return null;
        }

        int style = GetWindowLong(window, ExtendedStyleIndex);
        bool asksToBeListed = (style & AppWindow) != 0;
        if ((style & ToolWindow) != 0 && !asksToBeListed)
        {
            return null;
        }

        if (GetWindow(window, Owner) != IntPtr.Zero && !asksToBeListed)
        {
            return null;
        }

        if (GetWindowThreadProcessId(window, out uint process) == 0 || process == (uint)Environment.ProcessId)
        {
            return null;
        }

        var title = new StringBuilder(MostTitle);
        if (GetWindowText(window, title, title.Capacity) <= 0)
        {
            return null;
        }

        string? file = FileOf(process);
        return new OpenWindow(window, title.ToString(), file is null ? "" : NameOf(file), file is null ? null : IconKeyPrefix + file);
    }

    private static bool Cloaked(IntPtr window)
    {
        try
        {
            return DwmGetWindowAttribute(window, CloakedAttribute, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // The program's file, or null when Windows does not say: asked with the least rights there
    // are, which works for a program that runs as administrator as well.
    private static string? FileOf(uint process)
    {
        IntPtr handle = OpenProcess(QueryLimitedInformation, false, process);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(1024);
            int length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) && length > 0 ? path.ToString(0, length) : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    // What the program calls itself in its file, or the file's name without its ending.
    private string NameOf(string file)
    {
        if (_names.TryGetValue(file, out string? known))
        {
            return known;
        }

        string name = Path.GetFileNameWithoutExtension(file);
        try
        {
            if (FileVersionInfo.GetVersionInfo(file).FileDescription?.Trim() is { Length: > 0 } called)
            {
                name = called;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The file's name it is.
        }

        _names[file] = name;
        return name;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr data);

    private const int ExtendedStyleIndex = -20;          // GWL_EXSTYLE
    private const int ToolWindow = 0x00000080;           // WS_EX_TOOLWINDOW
    private const int AppWindow = 0x00040000;            // WS_EX_APPWINDOW
    private const uint Owner = 4;                        // GW_OWNER
    private const int ShowRestored = 9;                  // SW_RESTORE
    private const uint CloseMessage = 0x0010;            // WM_CLOSE
    private const int CloakedAttribute = 14;             // DWMWA_CLOAKED
    private const uint QueryLimitedInformation = 0x1000; // PROCESS_QUERY_LIMITED_INFORMATION

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback each, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int how);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback each, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint which);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Force Quit left a window's program running, because ending it would have ended more than was asked for. The log names this type, so that it can be told from a program Windows would not let Beacon end.</summary>
public sealed class WindowNotEndedException(string words) : InvalidOperationException(words);
