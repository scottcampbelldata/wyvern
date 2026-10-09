using System.IO;
using Wyvern.Core.Settings;
using Microsoft.Win32;

namespace Wyvern.App.Interop;

/// <summary>Adds or removes Wyvern from the programs Windows starts at sign-in, for the current user only.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wyvern";

    /// <summary>
    /// Does nothing unless this is an installed copy: a Release build, running as Wyvern.exe,
    /// with the default data folder. A development or test run must never change what Windows
    /// starts at sign-in.
    /// </summary>
    public static void Apply(bool startWithWindows)
    {
#if DEBUG
        // A debug build must never register itself: it would point Windows at a bin\Debug path.
        _ = startWithWindows;
#else
        string? executable = Environment.ProcessPath;
        bool isWyvernExe = executable is not null
            && string.Equals(Path.GetFileNameWithoutExtension(executable), "Wyvern", StringComparison.OrdinalIgnoreCase);
        bool usesThrowawayFolder = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WyvernPaths.HomeVariable));
        if (!isWyvernExe || usesThrowawayFolder)
        {
            // Started through dotnet.exe, or pointed at a throwaway data folder with WYVERN_HOME.
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        RemoveOldEntry(key);
        if (startWithWindows)
        {
            key.SetValue(ValueName, $"\"{executable}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
#endif
    }

#if !DEBUG
    // The app was called Beacon before, and an installed Beacon left its own entry here. It is
    // taken out so the old app does not start beside this one and take the same hotkey. Only
    // when the entry really points at that app: a Beacon.exe with Beacon.Core.dll beside it.
    private static void RemoveOldEntry(RegistryKey key)
    {
        const string oldName = "Beacon";
        if (key.GetValue(oldName) is not string command)
        {
            return;
        }

        string executable = command.Trim().Trim('"');
        if (string.Equals(Path.GetFileName(executable), "Beacon.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(Path.GetDirectoryName(executable) ?? "", "Beacon.Core.dll")))
        {
            key.DeleteValue(oldName, throwOnMissingValue: false);
        }
    }
#endif
}
