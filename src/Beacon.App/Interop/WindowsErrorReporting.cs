using System.IO;
using System.Runtime.InteropServices;

namespace Beacon.App.Interop;

/// <summary>
/// Asks Windows not to report Beacon's crashes. Beacon has no crash reporting of its own, but
/// Windows has one for every program: when a program crashes or stops answering, Windows Error
/// Reporting can build a report that holds a part of the program's memory and send it to
/// Microsoft. Beacon's memory holds what the owner copied, and the rule is that no crash
/// report leaves the machine.
/// </summary>
/// <remarks>
/// <para>Two things are asked of Windows, each on its own, so that one that fails does not
/// stop the other. What each is documented to do is said where it is called.</para>
/// <para>The one that counts is the exclusion, which is what Microsoft's documentation gives a
/// program to be left out by ("To exclude your application from Windows error reporting, use
/// the WerAddExcludedApplication function"). Beacon's program file is added, by its name, to
/// the current user's list of programs that Windows Error Reporting leaves out, and Windows
/// keeps that list in the registry: under <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\Windows
/// Error Reporting</c>, in <c>ExcludedApplications</c>. It stays: it is still there after
/// Beacon has closed and after Beacon has been removed, so from the second start on it also
/// holds for a crash that comes before this is called. It goes by the name of the file alone,
/// so it holds for any program of that name that this user runs. Nothing here takes it out
/// again, because that would take it from another copy of Beacon that is still running; the
/// owner can, by deleting that value, and <c>WerRemoveExcludedApplication</c> is the call that
/// does the same. When the exclusion could not be made the owner is told
/// (<see cref="NotOptedOutNote"/>), because then a crash can still be reported.</para>
/// <para>The other holds for this run of the process only and stops nothing: a report that
/// is made all the same is made without the heap, which is where what was copied is held.</para>
/// <para>The error mode of the process is not used, though it is the one switch for a single
/// process of which the documentation says that Windows Error Reporting is not invoked
/// (SetErrorMode with SEM_NOGPFAULTERRORBOX). The same page says: "A child process inherits
/// the error mode of its parent process." Beacon is a launcher. The owner's scripts and
/// whatever they start, the editor and Explorer would all start with that mode, and would
/// then crash without Windows' dialog and without its report. How other programs crash is not
/// Beacon's to change.</para>
/// </remarks>
public static class WindowsErrorReporting
{
    /// <summary>
    /// Shown once for each run of Beacon when its program file could not be added to the
    /// programs that Windows leaves out of error reporting.
    /// </summary>
    public const string NotOptedOutNote =
        "Beacon could not opt out of Windows crash reports. See the README for how to switch them off yourself.";

    // WER_FAULT_REPORTING_FLAG_NOHEAP. Its number is from the Windows SDK's header, werapi.h,
    // and not from a Learn page: the page for WerSetFlags names the flag and says what it does,
    // but gives no number for it.
    private const uint NoHeapInReports = 0x0001;

    // For Marshal.GetExceptionForHR: its page says that IntPtr(-1) makes the exception "just
    // from the error code", ignoring whatever error information the thread happens to hold.
    private static readonly IntPtr NoErrorInfo = new(-1);

    /// <summary>
    /// Asks the two things. Call it first when Beacon starts, before anything that could fail.
    /// Never throws.
    /// </summary>
    /// <param name="report">
    /// Receives fixed words for the log: with an exception for each thing that could not be
    /// done, and once without, when both were done. A callback that throws is ignored.
    /// </param>
    /// <returns>
    /// True when Windows took Beacon's program file onto the list of programs it leaves out of
    /// error reporting. False when it did not, whatever the reason: the call failed or threw, or
    /// Beacon is not running from its own program file. The caller tells the owner
    /// (<see cref="NotOptedOutNote"/>). How the other thing went does not count here.
    /// </returns>
    public static bool OptOut(Action<string, Exception?> report)
    {
        bool heapKeptOut = false;
        try
        {
            // WerSetFlags: "Sets the Windows Error Reporting (WER) settings for the current
            // process", and returns S_OK or an error code. WER_FAULT_REPORTING_FLAG_NOHEAP:
            // "Do not collect heap information in the event of an application crash or
            // non-response." It stops no report; it is for one that is made in spite of the
            // exclusion. Nothing is said of a child process inheriting these settings. The
            // call sets the flags, it does not add to them; Beacon sets no other.
            int result = WerSetFlags(NoHeapInReports);
            heapKeptOut = result >= 0;
            if (!heapKeptOut)
            {
                Tell(report, "Windows could not be told to keep Beacon's memory out of error reports", Marshal.GetExceptionForHR(result, NoErrorInfo));
            }
        }
        catch (Exception ex)
        {
            Tell(report, "Windows could not be told to keep Beacon's memory out of error reports", ex);
        }

        bool excluded = false;
        try
        {
            if (OwnProgramFile() is not { } name)
            {
                // Started through another program, such as dotnet.exe: the name Windows
                // would go by is that program's, and it is not Beacon's to leave out.
                Tell(report, "Beacon is not running from its own program file, so it was not added to the programs that Windows leaves out of error reporting", null);
            }
            else
            {
                // WerAddExcludedApplication: "Adds the specified application to the list of
                // applications that are to be excluded from Windows Error Reporting." The
                // first argument is "the name of the executable file for the application,
                // including the file name extension", as a Unicode string. The second, when
                // it is false, adds it "to the list of excluded applications for the current
                // user" only, and that list "is stored under the HKEY_CURRENT_USER registry
                // hive"; it is the list for all users that the page says takes the right to
                // write to HKEY_LOCAL_MACHINE. It returns S_OK, or an error code, such as
                // E_ACCESSDENIED when the list cannot be written. What it answers for a name
                // that is on the list already the page does not say; an error code is taken
                // to mean that the exclusion was not made.
                int result = WerAddExcludedApplication(name, allUsers: false);
                excluded = result >= 0;
                if (!excluded)
                {
                    Tell(report, "Beacon could not be added to the programs that Windows leaves out of error reporting", Marshal.GetExceptionForHR(result, NoErrorInfo));
                }
            }
        }
        catch (Exception ex)
        {
            excluded = false;
            Tell(report, "Beacon could not be added to the programs that Windows leaves out of error reporting", ex);
        }

        if (excluded && heapKeptOut)
        {
            Tell(report, "Windows was asked to leave Beacon out of error reporting, by the name of its program file for this user, and to keep its memory out of any report of this run", null);
        }

        return excluded;
    }

    // The name of the file this process was started from, with its extension and without its
    // folder, when that file is Beacon's own: the one named after this assembly. Null when the
    // process is another program that has loaded Beacon, or when Windows does not say.
    private static string? OwnProgramFile()
    {
        string? path = Environment.ProcessPath;
        string? assembly = typeof(WindowsErrorReporting).Assembly.GetName().Name;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(assembly))
        {
            return null;
        }

        string name = Path.GetFileName(path);
        return string.Equals(name, assembly + ".exe", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    // The callback is the caller's code. One that throws must not stop what is still to do.
    private static void Tell(Action<string, Exception?> report, string words, Exception? error)
    {
        try
        {
            report(words, error);
        }
        catch (Exception)
        {
            // Nothing more can be done about a log that is broken.
        }
    }

    // HRESULT WerSetFlags(DWORD dwFlags), in Kernel32.dll. An HRESULT is a 32-bit number in a
    // 64-bit process too, and negative when it is an error.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int WerSetFlags(uint flags);

    // HRESULT WerAddExcludedApplication(PCWSTR pwzExeName, BOOL bAllUsers), in Wer.dll. The
    // name goes as a pointer to UTF-16 text that ends with a zero, and BOOL is a 32-bit number.
    // Wer.dll is looked for in the System32 folder of Windows and nowhere else, so that a file
    // of that name beside Beacon is never loaded in its place.
    [DllImport("wer.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WerAddExcludedApplication(string exeName, [MarshalAs(UnmanagedType.Bool)] bool allUsers);
}
