using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Beacon.Core.Platform;

namespace Beacon.App.Interop;

public sealed class ShellProcessLauncher : IProcessLauncher
{
    // Started by its full path, so that a file called explorer.exe in the folder Beacon was
    // started from cannot be run in its place.
    private static readonly string Explorer =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // The same goes for Notepad, which is the editor of last resort for a script.
    private static readonly string Notepad =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    private const string EditVerb = "edit";

    public void LaunchApp(string appId) => ShellOpen(@"shell:AppsFolder\" + appId);

    public void OpenPath(string path) => ShellOpen(path);

    // Explorer wants the path as one quoted argument straight after "/select,".
    public void RevealInFolder(string path) =>
        Process.Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();

    // The whole link is handed to Windows as the one thing to open. It is never split into a
    // program and its arguments. A blank link is refused here too, in case a caller has not
    // checked: there is nothing to open, and Windows would be asked to open nothing.
    public void OpenLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new InvalidOperationException("There is no link to open.");
        }

        ShellOpen(link);
    }

    // A script must be opened for editing and never run. "Open", which is what ShellOpen asks
    // for, runs a .cmd, .bat or .js file, and a .py file where Python is installed. So Windows
    // is asked for the file type's "Edit" action instead, the one that is registered for the
    // type under that name, and only when the file type has one: asking for an action that is
    // not there must not be left to Windows to make something of. That is not always the
    // action Explorer's right-click menu shows: for a .ps1 file Explorer's "Edit" is registered
    // elsewhere, and Beacon opens Notepad. A file type without one, and an "Edit" action that
    // fails, get Notepad. A file that has gone is reported rather than offered as a new file.
    public void EditFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The file to edit is not there.");
        }

        var edit = new ProcessStartInfo(path) { UseShellExecute = true };
        if (edit.Verbs.Contains(EditVerb, StringComparer.OrdinalIgnoreCase))
        {
            edit.Verb = EditVerb;
            try
            {
                Process.Start(edit)?.Dispose();
                return;
            }
            catch (Win32Exception)
            {
                // The program behind the "Edit" action is missing or would not start.
            }
        }

        var notepad = new ProcessStartInfo(Notepad) { UseShellExecute = false };
        notepad.ArgumentList.Add(path);
        Process.Start(notepad)?.Dispose();
    }

    // Throws Win32Exception when Windows cannot open the target, and InvalidOperationException
    // when there is no target at all; the caller reports either, by type only.
    // ERROR_CANCELLED: the owner said no when Windows asked.
    private const int Cancelled = 1223;

    public void RunAsAdministrator(string file)
    {
        try
        {
            // The "runas" verb is what Run as administrator in Explorer does: Windows asks first.
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            // Nothing was started, as the owner wanted.
        }
    }

    private static void ShellOpen(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
