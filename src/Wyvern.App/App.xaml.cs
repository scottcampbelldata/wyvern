using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Wyvern.App.Interop;
using Wyvern.Core.Diagnostics;
using Wyvern.Core.Platform;
using Wyvern.Core.Settings;

namespace Wyvern.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _isFirstInstance;
    private WyvernShell? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before anything writes into Wyvern's own folder: bring over the files of the app's
        // old name, Beacon, the first time.
        bool movedOldData;
        try
        {
            movedOldData = MoveOldData();
        }
        catch (Exception)
        {
            MessageBox.Show(
                "Wyvern could not copy your settings and data over from Beacon. Quit Beacon if it is running and start Wyvern again.",
                "Wyvern",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // First of all, before anything that could fail: Windows is asked to leave Wyvern out
        // of its crash reports. Whether it did is kept until there is a shell to tell the owner
        // through, further down.
        bool optedOut = OptOutOfCrashReports();

        base.OnStartup(e);

        var paths = WyvernPaths.Default();
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
            // Wyvern is already running in this session; its hotkey and tray icon are the way in.
            Shutdown();
            return;
        }

        try
        {
            _shell = new WyvernShell(this);
        }
        catch (Exception ex)
        {
            // Nothing is on screen yet, so a failure here would otherwise be invisible.
            new FileLog(paths.LogFile, new SystemClock()).Error("Wyvern could not start", ex);
            MessageBox.Show(
                "Wyvern could not start. Details are in the log file.",
                "Wyvern",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }

        // Said once for each run, when Wyvern could not be put on the list of programs that
        // Windows leaves out of its crash reports: a crash can then still be reported, and the
        // log alone would not tell the owner. Through the shell's own notifications, in its
        // turn behind what startup has said. Outside the try above, so that it can never be
        // taken for a Wyvern that could not start; there is no shell when that failed.
        if (!optedOut)
        {
            _shell?.Tell(WindowsErrorReporting.NotOptedOutNote);
        }

        if (movedOldData)
        {
            _shell?.Tell("Your settings and data from Beacon were copied over. The old Beacon folder was left as it was.");
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

    // Wyvern's memory holds what the owner copied, and Windows has a crash reporter of its own
    // that can send a part of a crashed program's memory to Microsoft. So Windows is asked to
    // leave Wyvern out of it (WindowsErrorReporting says how, and what stays behind in the
    // registry). In a try of its own: whatever goes wrong here is logged in fixed words, when
    // the log can be reached, and Wyvern starts all the same. The log is only made when there
    // is something to write. Answers whether Wyvern is now on the list of programs that
    // Windows leaves out, which is false as well when the asking itself failed.
    private static bool OptOutOfCrashReports()
    {
        FileLog? log = null;
        void Report(string words, Exception? error)
        {
            log ??= new FileLog(WyvernPaths.Default().LogFile, new SystemClock());
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
                Report("Wyvern could not ask Windows to leave it out of error reporting", ex);
            }
            catch (Exception)
            {
                // The log could not be reached either. Wyvern starts anyway.
            }

            return false;
        }
    }

    // Copies %LOCALAPPDATA%\Beacon to Wyvern's folder the first time (RenameMigration). Refuses
    // while the old Beacon is running, since it may be writing to those files.
    private static bool MoveOldData()
    {
        string? oldRoot = RenameMigration.OldRoot();
        string newRoot = WyvernPaths.Default().Root;
        if (oldRoot is null || !RenameMigration.IsNeeded(oldRoot, newRoot))
        {
            return false;
        }

        if (Mutex.TryOpenExisting(InstanceName("Beacon", oldRoot), out Mutex? running))
        {
            running.Dispose();
            throw new InvalidOperationException("Beacon is still running.");
        }

        return RenameMigration.Run(oldRoot, newRoot);
    }

    // One instance per data folder: a copy started with WYVERN_HOME for development or testing
    // can run beside the installed one, which uses the default folder.
    private static string SingleInstanceName(WyvernPaths paths) => InstanceName("Wyvern", paths.Root);

    private static string InstanceName(string app, string root)
    {
        string full = Path.GetFullPath(root).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return $@"Local\{app}.SingleInstance." + hash;
    }
}
