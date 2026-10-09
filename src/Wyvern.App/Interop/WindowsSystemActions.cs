using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Wyvern.Core.Commands;
using Microsoft.Win32;

namespace Wyvern.App.Interop;

/// <summary>
/// What the launcher asks Windows to do with the whole PC: lock, sleep, restart and the like,
/// the Recycle Bin, the sound, light or dark, and showing the desktop. Use on the UI thread.
/// </summary>
public sealed class WindowsSystemActions : ISystemActions
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public void Lock()
    {
        // LockWorkStation: "If the function succeeds, the return value is nonzero." It only
        // asks: the screen locks a moment later, and everything that runs keeps running.
        if (!LockWorkStation())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Sleep()
    {
        // SetSuspendState(bHibernate, bForce, bWakeupEventsDisabled): false, false, false is
        // sleep, asked for in the ordinary way, with the PC free to wake for what it is set
        // to wake for. The call comes back when the PC has woken.
        if (!SetSuspendState(false, false, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Hibernate()
    {
        // The same call with bHibernate true. It fails on a PC where hibernation is off.
        if (!SetSuspendState(true, false, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    // shutdown.exe in the Windows system folder does these three as the Start menu does them:
    // /r restarts, /s shuts down, /l signs out, and /t 0 means now. Programs are asked to
    // close, and one with unsaved work can still hold Windows up.
    public void Restart() => Shutdown("/r /t 0");

    public void ShutDown() => Shutdown("/s /t 0");

    public void SignOut() => Shutdown("/l");

    public bool EmptyRecycleBin()
    {
        // SHEmptyRecycleBin(hwnd, pszRootPath, dwFlags): a null path is every drive's bin.
        // It answers S_OK (zero), and E_UNEXPECTED when there was nothing in any bin.
        int answer = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressWindow | NoSound);
        if (answer == Unexpected)
        {
            return false;
        }

        Marshal.ThrowExceptionForHR(answer);
        return true;
    }

    public void OpenRecycleBin() =>
        Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:RecycleBinFolder");

    public bool? ToggleMute() => WithSound<bool?>(sound =>
    {
        Marshal.ThrowExceptionForHR(sound.GetMute(out bool muted));
        var none = Guid.Empty;
        Marshal.ThrowExceptionForHR(sound.SetMute(!muted, ref none));
        return !muted;
    });

    public int? ChangeVolume(int by) => WithSound<int?>(sound =>
    {
        Marshal.ThrowExceptionForHR(sound.GetMasterVolumeLevelScalar(out float level));
        var none = Guid.Empty;
        if (by > 0)
        {
            // Louder is of no use with the sound off, so it comes on, as with the volume key.
            Marshal.ThrowExceptionForHR(sound.SetMute(false, ref none));
        }

        return Put(sound, Percent(level) + by);
    });

    public int? SetVolume(int percent) => WithSound<int?>(sound => Put(sound, percent));

    public bool ToggleDarkMode()
    {
        // The two values Settings > Personalisation > Colours writes: one for apps, one for
        // the taskbar and Start. 1 is light, 0 is dark, and a value that is not there is light.
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        bool light = key.GetValue("AppsUseLightTheme") is not int apps || apps != 0;
        int now = light ? 0 : 1;
        key.SetValue("AppsUseLightTheme", now, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", now, RegistryValueKind.DWord);

        // Running programs are told that the colours changed, as Settings tells them. One
        // that does not answer is not waited for.
        _ = SendMessageTimeout(Everyone, SettingChange, IntPtr.Zero, "ImmersiveColorSet", AbortIfHung, 200, out _);
        return light;
    }

    public void ShowDesktop()
    {
        // The shell's own object for scripts has the Win+D of the taskbar as ToggleDesktop.
        Type shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("The Windows shell cannot be asked.");
        object shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("The Windows shell cannot be asked.");
        try
        {
            shellType.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Media(MediaKey key) => MediaControl.Tell(key);

    private static void Shutdown(string arguments) => Start(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments);

    private static void Start(string program, string arguments)
    {
        using var started = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true });
    }

    private static int Percent(float level) => Math.Clamp((int)Math.Round(level * 100), 0, 100);

    private static int Put(IAudioEndpointVolume sound, int percent)
    {
        int put = Math.Clamp(percent, 0, 100);
        var none = Guid.Empty;
        Marshal.ThrowExceptionForHR(sound.SetMasterVolumeLevelScalar(put / 100f, ref none));
        return put;
    }

    // What sound is played through by default, asked for its volume control. Null when the
    // PC has no such thing: no sound card, or nothing plugged in.
    private static T? WithSound<T>(Func<IAudioEndpointVolume, T> ask)
    {
        var devices = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        IMMDevice? device = null;
        object? control = null;
        try
        {
            if (devices.GetDefaultAudioEndpoint(Render, Multimedia, out device) != 0 || device is null)
            {
                return default;
            }

            var volume = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref volume, InProcessServer, IntPtr.Zero, out control) != 0 || control is null)
            {
                return default;
            }

            return ask((IAudioEndpointVolume)control);
        }
        finally
        {
            if (control is not null)
            {
                Marshal.ReleaseComObject(control);
            }

            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            Marshal.ReleaseComObject(devices);
        }
    }

    // BOOL LockWorkStation(), in User32.dll.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    // BOOLEAN SetSuspendState(BOOLEAN, BOOLEAN, BOOLEAN), in PowrProf.dll. A BOOLEAN is one byte.
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    // HRESULT SHEmptyRecycleBinW(HWND, LPCWSTR, DWORD), in Shell32.dll, with SHERB_NOCONFIRMATION
    // (1), SHERB_NOPROGRESSUI (2) and SHERB_NOSOUND (4). E_UNEXPECTED is 0x8000FFFF.
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressWindow = 0x2;
    private const uint NoSound = 0x4;
    private const int Unexpected = unchecked((int)0x8000FFFF);

    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? rootPath, uint flags);

    // SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet",
    // SMTO_ABORTIFHUNG, milliseconds, result), in User32.dll.
    private static readonly IntPtr Everyone = new(0xFFFF);
    private const uint SettingChange = 0x001A;
    private const uint AbortIfHung = 0x0002;

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint milliseconds, out IntPtr result);

    // The Windows sound interfaces (mmdeviceapi.h and endpointvolume.h), cut down to what is
    // called. The methods stand in the order the interfaces have them: one that is not
    // called keeps its place under a name that says so. eRender is 0, eMultimedia is 1 and
    // CLSCTX_INPROC_SERVER is 1.
    private const int Render = 0;
    private const int Multimedia = 1;
    private const int InProcessServer = 1;

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpointsNotCalled();

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? activated);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotifyNotCalled();

        [PreserveSig]
        int UnregisterControlChangeNotifyNotCalled();

        [PreserveSig]
        int GetChannelCountNotCalled();

        [PreserveSig]
        int SetMasterVolumeLevelNotCalled();

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevelNotCalled();

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevelNotCalled();

        [PreserveSig]
        int SetChannelVolumeLevelScalarNotCalled();

        [PreserveSig]
        int GetChannelVolumeLevelNotCalled();

        [PreserveSig]
        int GetChannelVolumeLevelScalarNotCalled();

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
