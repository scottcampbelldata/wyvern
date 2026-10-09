namespace Beacon.Core.Apps;

/// <param name="Name">Display name, as shown in the Start menu.</param>
/// <param name="Id">The identifier Windows uses to launch the app from its apps folder.</param>
/// <param name="Target">The full path of the program's file, when the app is a program with a file of its own. Null for an app from the Store, and when Windows does not say.</param>
public sealed record AppEntry(string Name, string Id, string? Target = null);

public interface IAppCatalog
{
    /// <summary>Lists installed apps. May be slow; callers run it off the UI thread.</summary>
    IReadOnlyList<AppEntry> GetApps();
}
