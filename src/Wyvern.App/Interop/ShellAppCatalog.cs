using System.Runtime.InteropServices;
using Wyvern.Core.Apps;

namespace Wyvern.App.Interop;

/// <summary>
/// Lists what Windows shows under shell:AppsFolder: desktop programs with Start menu entries
/// and Store apps. Each item's path in that folder is the id used to launch it.
/// </summary>
public sealed class ShellAppCatalog : IAppCatalog
{
    public IReadOnlyList<AppEntry> GetApps()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("The Windows Shell automation object is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var apps = new List<AppEntry>();
            foreach (dynamic item in shell.NameSpace("shell:AppsFolder").Items())
            {
                apps.Add(new AppEntry((string)item.Name, (string)item.Path, Target(item)));
            }

            return apps;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    // The file the app's shortcut starts, when it starts a file that is there: Windows has
    // it as a property of the shortcut. An app from the Store has no such file, and neither
    // has a shortcut to a folder or to a web address.
    private static string? Target(dynamic item)
    {
        try
        {
            return item.ExtendedProperty("System.Link.TargetParsingPath") is string { Length: > 0 } target
                && System.IO.Path.IsPathFullyQualified(target)
                && System.IO.File.Exists(target)
                ? target
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NotSupportedException)
        {
            return null;
        }
    }
}
