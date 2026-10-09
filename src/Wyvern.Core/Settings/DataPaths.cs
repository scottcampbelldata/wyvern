using System.Text.RegularExpressions;
using Wyvern.Core.Sync;

namespace Wyvern.Core.Settings;

/// <summary>What the "dataDir" setting asks for, worked out from settings.json.</summary>
/// <param name="Folder">The full path to use, without a trailing separator, or null for the default folder.</param>
/// <param name="Rejected">
/// The setting exactly as written when it could not be used, otherwise null. The default folder
/// is used in its place. It is text from settings.json: show it on screen, never log it.
/// </param>
public sealed record DataDirSetting(string? Folder, string? Rejected)
{
    /// <summary>
    /// A missing or empty "dataDir" means the default folder. Environment variables such as
    /// %USERPROFILE% are expanded. Anything that is not a full path is refused, because a
    /// relative path would depend on where Wyvern happened to be started from. So is the root
    /// of a drive or of a share: sync never works on one (<see cref="SyncFiles.IsDataFolder"/>,
    /// the one rule for that, decided from the text alone), and the library and the clipboard
    /// history would otherwise go on using it without anything telling the owner.
    /// </summary>
    public static DataDirSetting From(WyvernSettings settings)
    {
        string? written = settings.DataDir;
        if (string.IsNullOrWhiteSpace(written))
        {
            return new DataDirSetting(null, null);
        }

        string expanded = Environment.ExpandEnvironmentVariables(written.Trim());
        return Path.IsPathFullyQualified(expanded)
            && !ExpandedPath.HasUnexpandedVariable(expanded)
            && TryNormalize(expanded, out string folder)
            && SyncFiles.IsDataFolder(folder)
            ? new DataDirSetting(folder, null)
            : new DataDirSetting(null, written);
    }

    private static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            normalized = "";
            return false;
        }
    }
}

/// <summary>A check shared by the settings that name folders, run after environment variables are expanded.</summary>
internal static partial class ExpandedPath
{
    /// <summary>
    /// True when a name between two percent signs is still there after expansion, as in
    /// D:\%USERNAM%\Wyvern: the variable does not exist, and the text would otherwise be taken
    /// as a folder literally named that. A single percent sign (D:\50% done) is an ordinary character.
    /// </summary>
    public static bool HasUnexpandedVariable(string expanded) => UnexpandedVariable().IsMatch(expanded);

    // A percent sign, then one or more characters that are not a percent sign or a path separator,
    // then another percent sign.
    [GeneratedRegex(@"%[^%\\/]+%")]
    private static partial Regex UnexpandedVariable();
}

/// <summary>
/// The data folder and where things are kept inside it. This is the part of Wyvern's storage
/// that holds what the owner writes (the library), as opposed to the per-machine files beside
/// settings.json.
/// </summary>
public sealed class DataPaths(string root)
{
    /// <summary>The data folder itself.</summary>
    public string Root { get; } = root;

    /// <summary>Quicklinks, snippets and scripts live here, each as a plain file the owner can edit.</summary>
    public string LibraryDir => Path.Combine(Root, "library");

    public string QuicklinksFile => Path.Combine(LibraryDir, "quicklinks.json");

    public string SnippetsDir => Path.Combine(LibraryDir, "snippets");

    /// <summary>Every .ps1, .py, .cmd, .bat and .js file under this folder is a command.</summary>
    public string ScriptsDir => Path.Combine(LibraryDir, "scripts");

    /// <summary>Clipboard history lives here, in one folder for each machine, each holding one file for each day.</summary>
    public string ClipboardDir => Path.Combine(Root, "clipboard");
}
