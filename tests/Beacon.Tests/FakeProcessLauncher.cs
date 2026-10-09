using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>Records what it was asked to open instead of opening anything.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    /// <summary>One line per call, such as "link https://example.org" or "open C:\notes.txt".</summary>
    public List<string> Calls { get; } = [];

    public void LaunchApp(string appId) => Calls.Add("launch " + appId);

    public void OpenPath(string path) => Calls.Add("open " + path);

    public void RevealInFolder(string path) => Calls.Add("reveal " + path);

    public void OpenLink(string link) => Calls.Add("link " + link);

    public void EditFile(string path) => Calls.Add("edit " + path);

    public void RunAsAdministrator(string file) => Calls.Add("admin " + file);
}
