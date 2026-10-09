using System.IO;
using Beacon.Core.Settings;
using Microsoft.Win32;

namespace Beacon.App.Interop;

/// <summary>Adds or removes Beacon from the programs Windows starts at sign-in, for the current user only.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Beacon";

    /// <summary>
    /// Does nothing unless this is an installed copy: a Release build, running as Beacon.exe,
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
        bool isBeaconExe = executable is not null
            && string.Equals(Path.GetFileNameWithoutExtension(executable), "Beacon", StringComparison.OrdinalIgnoreCase);
        bool usesThrowawayFolder = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BeaconPaths.HomeVariable));
        if (!isBeaconExe || usesThrowawayFolder)
        {
            // Started through dotnet.exe, or pointed at a throwaway data folder with BEACON_HOME.
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
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
}
