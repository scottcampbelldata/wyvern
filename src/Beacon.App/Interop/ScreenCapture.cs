using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Beacon.App.Interop;

/// <summary>
/// Keeps a window out of pictures of the screen. The two sync windows show the sync key, and
/// a passphrase is typed into one of them: a screenshot, a screen recording or a tool that
/// keeps snapshots of the screen would otherwise keep the key.
/// </summary>
public static class ScreenCapture
{
    // WDA_EXCLUDEFROMCAPTURE: "The window is displayed only on a monitor. Everywhere else, the
    // window does not appear at all." It is honoured from Windows 10, version 2004.
    private const uint ExcludeFromCapture = 0x00000011;

    /// <summary>
    /// Asks Windows to leave the window out of screen captures. Call it once the window has a
    /// handle. Never throws.
    /// </summary>
    /// <returns>False when Windows refused; the window then shows in captures like any other.</returns>
    public static bool Exclude(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            return handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, ExcludeFromCapture);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // BOOL SetWindowDisplayAffinity(HWND hWnd, DWORD dwAffinity), in User32.dll.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
