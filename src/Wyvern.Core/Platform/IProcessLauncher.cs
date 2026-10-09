namespace Wyvern.Core.Platform;

public interface IProcessLauncher
{
    /// <summary>Starts an installed app by the id its <see cref="Apps.AppEntry"/> carries.</summary>
    void LaunchApp(string appId);

    /// <summary>Opens a file or folder with whatever Windows associates with it.</summary>
    void OpenPath(string path);

    /// <summary>Opens the containing folder in Explorer with the file or folder selected.</summary>
    void RevealInFolder(string path);

    /// <summary>
    /// Opens a quicklink: a web address in the default browser, or a path or any other kind of
    /// address with whatever Windows associates with it. Kept apart from <see cref="OpenPath"/>
    /// because a link is text the owner wrote, not the path of something Wyvern found on disk.
    /// </summary>
    void OpenLink(string link);

    /// <summary>
    /// Opens a script file to be edited, and never runs it. Kept apart from
    /// <see cref="OpenPath"/> because what Windows does when a .cmd, .bat or .js file is
    /// simply opened is run it.
    /// </summary>
    void EditFile(string path);

    /// <summary>
    /// Starts a program with an administrator's rights, after Windows has asked the owner. An
    /// owner who says no is not a failure. A launcher that cannot do it throws: only code
    /// that offers the action calls this.
    /// </summary>
    void RunAsAdministrator(string file) => throw new NotSupportedException();
}
