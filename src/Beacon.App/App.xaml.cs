using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Beacon.App.Interop;
using Beacon.Core.Diagnostics;
using Beacon.Core.Platform;
using Beacon.Core.Settings;

namespace Beacon.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _isFirstInstance;
    private BeaconShell? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        // First of all, before anything that could fail: Windows is asked to leave Beacon out
        // of its crash reports. Whether it did is kept until there is a shell to tell the owner
        // through, further down.
        bool optedOut = OptOutOfCrashReports();

        base.OnStartup(e);

        var paths = BeaconPaths.Default();
        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName(paths), out _isFirstInstance);
        }
        catch (UnauthorizedAccessException)
        {
            // A copy running with higher privileges owns the mutex. That still means "already running".
            _isFirstInstance = false;
        }

        if (!_isFirstInstance)
        {
            // Beacon is already running in this session; its hotkey and tray icon are the way in.
            Shutdown();
            return;
        }

        try
        {
            _shell = new BeaconShell(this);
        }
        catch (Exception ex)
        {
            // Nothing is on screen yet, so a failure here would otherwise be invisible.
            new FileLog(paths.LogFile, new SystemClock()).Error("Beacon could not start", ex);
            MessageBox.Show(
                "Beacon could not start. Details are in the log file.",
                "Beacon",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }

        // Said once for each run, when Beacon could not be put on the list of programs that
        // Windows leaves out of its crash reports: a crash can then still be reported, and the
        // log alone would not tell the owner. Through the shell's own notifications, in its
        // turn behind what startup has said. Outside the try above, so that it can never be
        // taken for a Beacon that could not start; there is no shell when that failed.
        if (!optedOut)
        {
            _shell?.Tell(WindowsErrorReporting.NotOptedOutNote);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        if (_isFirstInstance)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // Beacon's memory holds what the owner copied, and Windows has a crash reporter of its own
    // that can send a part of a crashed program's memory to Microsoft. So Windows is asked to
    // leave Beacon out of it (WindowsErrorReporting says how, and what stays behind in the
    // registry). In a try of its own: whatever goes wrong here is logged in fixed words, when
    // the log can be reached, and Beacon starts all the same. The log is only made when there
    // is something to write. Answers whether Beacon is now on the list of programs that
    // Windows leaves out, which is false as well when the asking itself failed.
    private static bool OptOutOfCrashReports()
    {
        FileLog? log = null;
        void Report(string words, Exception? error)
        {
            log ??= new FileLog(BeaconPaths.Default().LogFile, new SystemClock());
            if (error is null)
            {
                log.Info(words);
            }
            else
            {
                log.Error(words, error);
            }
        }

        try
        {
            return WindowsErrorReporting.OptOut(Report);
        }
        catch (Exception ex)
        {
            try
            {
                Report("Beacon could not ask Windows to leave it out of error reporting", ex);
            }
            catch (Exception)
            {
                // The log could not be reached either. Beacon starts anyway.
            }

            return false;
        }
    }

    // One instance per data folder: a copy started with BEACON_HOME for development or testing
    // can run beside the installed one, which uses the default folder.
    private static string SingleInstanceName(BeaconPaths paths)
    {
        string root = Path.GetFullPath(paths.Root).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        return @"Local\Beacon.SingleInstance." + hash;
    }
}
