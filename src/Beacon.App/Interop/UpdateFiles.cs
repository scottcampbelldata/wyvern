using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Beacon.Core.Update;

namespace Beacon.App.Interop;

/// <summary>
/// Fetches the update files from the owner's update server, with the curl that is part of
/// Windows. Beacon has no code of its own that opens a connection: this starts curl.exe from
/// the Windows system folder with the one fixed list of arguments, which can fetch one
/// address into one file and nothing else. Nothing of the owner's is sent: the request is a
/// plain "give me this file". Use on a background thread.
/// </summary>
public sealed class UpdateFiles(string folder) : IUpdateSource
{
    private static string Curl => Path.Combine(Environment.SystemDirectory, "curl.exe");

    public byte[]? Fetch(string url, int maxBytes)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".fetch");
        try
        {
            if (!Run(url, file, maxBytes, UpdateDownload.SmallFileSeconds))
            {
                return null;
            }

            var fetched = new FileInfo(file);
            return fetched.Exists && fetched.Length <= maxBytes ? File.ReadAllBytes(file) : null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed when Beacon next starts.
            }
        }
    }

    public bool Download(string url, string toFile, long maxBytes) =>
        Run(url, toFile, maxBytes, UpdateDownload.InstallerSeconds)
        && new FileInfo(toFile) is { Exists: true } fetched
        && fetched.Length <= maxBytes;

    /// <summary>
    /// True when this Beacon runs from a program file of its own name, as an installed one
    /// does. A copy under another name, made to try something out, never looks for updates:
    /// the setup program would install over the real one.
    /// </summary>
    public static bool RunsAsInstalled() =>
        Path.GetFileName(Environment.ProcessPath ?? "").Equals("Beacon.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the setup program of an update. It installs without asking anything, waits for
    /// this Beacon to have quit, and starts the new one. The caller quits Beacon right after.
    /// </summary>
    /// <exception cref="Win32Exception">Windows would not start it.</exception>
    public static void StartSetup(string setupFile)
    {
        var start = new ProcessStartInfo(setupFile) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setupFile)! };
        foreach (string argument in UpdateDownload.SetupArguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var setup = Process.Start(start) ?? throw new InvalidOperationException("The setup program did not start.");
    }

    // True when curl ended saying that it fetched the file whole.
    private static bool Run(string url, string toFile, long maxBytes, int seconds)
    {
        if (!File.Exists(Curl))
        {
            return false;
        }

        var start = new ProcessStartInfo(Curl) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in UpdateDownload.CurlArguments(url, toFile, maxBytes, seconds))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var curl = Process.Start(start);
            if (curl is null)
            {
                return false;
            }

            // curl ends by itself when its own time is up; this is for a curl that does not.
            if (!curl.WaitForExit((seconds + 15) * 1000))
            {
                curl.Kill();
                return false;
            }

            return curl.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
