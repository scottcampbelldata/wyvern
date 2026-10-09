using System.Runtime.InteropServices;
using Wyvern.Core.Commands;
using Windows.Media.Control;

namespace Wyvern.App.Interop;

/// <summary>
/// Tells whatever is playing to play, to pause or to change track, through Windows' own list
/// of what is playing: the one behind the box that the volume keys show. Nothing is read
/// from that list, neither what is playing nor in which program, and no key press is sent.
/// </summary>
internal static class MediaControl
{
    // How long Windows is given to answer. It answers within a few milliseconds.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    public static void Tell(MediaKey key)
    {
        // Asked on another thread and waited for here: the answer of a Windows Runtime call
        // must not be waited for on the thread that it could come back on.
        var asked = Task.Run(async () =>
        {
            var playing = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (playing.GetCurrentSession() is not { } session)
            {
                return false;
            }

            _ = key switch
            {
                MediaKey.PlayPause => await session.TryTogglePlayPauseAsync(),
                MediaKey.NextTrack => await session.TrySkipNextAsync(),
                MediaKey.PreviousTrack => await session.TrySkipPreviousAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };
            return true;
        });

        if (asked.Wait(Patience) && asked.GetAwaiter().GetResult())
        {
            return;
        }

        // Nothing that Windows knows of is playing. An older player is not in that list and
        // listens for the keyboard's media keys instead: it is told with the message that
        // Windows itself makes of such a key, handed to the taskbar, which passes it on.
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero)
        {
            _ = PostMessage(taskbar, AppCommand, IntPtr.Zero, new IntPtr(Command(key) << 16));
        }
    }

    // APPCOMMAND_MEDIA_NEXTTRACK, APPCOMMAND_MEDIA_PREVIOUSTRACK and APPCOMMAND_MEDIA_PLAY_PAUSE.
    // In the message the command stands in the high word of lParam.
    private static int Command(MediaKey key) => key switch
    {
        MediaKey.NextTrack => 11,
        MediaKey.PreviousTrack => 12,
        MediaKey.PlayPause => 14,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private const uint AppCommand = 0x0319; // WM_APPCOMMAND

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
