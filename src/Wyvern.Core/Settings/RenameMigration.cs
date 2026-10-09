using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Core.Settings;

/// <summary>
/// Wyvern used to be called Beacon and kept its files in %LOCALAPPDATA%\Beacon. The first time
/// Wyvern starts, that folder is copied to Wyvern's own. The old folder is left as it is.
/// </summary>
public static class RenameMigration
{
    /// <summary>The folder name under %LOCALAPPDATA% that the app used before the rename.</summary>
    public const string OldFolderName = "Beacon";

    // Downloaded setup programs of the old app. Not worth copying.
    private const string UpdatesFolderName = "updates";

    /// <summary>The old folder, or null when this run uses a folder of its own (WYVERN_HOME).</summary>
    public static string? OldRoot()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WyvernPaths.HomeVariable)))
        {
            return null;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldFolderName);
    }

    /// <summary>True when there is something to copy: the old folder is there and the new one is not.</summary>
    public static bool IsNeeded(string oldRoot, string newRoot) => Directory.Exists(oldRoot) && !Directory.Exists(newRoot);

    /// <summary>
    /// Copies <paramref name="oldRoot"/> to <paramref name="newRoot"/> when it is needed. The copy
    /// is made beside the new folder first and only gets its real name once it is whole, so a
    /// copy that fails halfway leaves no new folder and is tried again on the next start.
    /// </summary>
    /// <returns>True when a copy was made.</returns>
    public static bool Run(string oldRoot, string newRoot)
    {
        if (!IsNeeded(oldRoot, newRoot))
        {
            return false;
        }

        string staging = newRoot + ".copying";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        try
        {
            Copy(oldRoot, staging, top: true);
            Directory.Move(staging, newRoot);
        }
        catch
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // Removed on the next try instead.
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }

        return true;
    }

    private static void Copy(string from, string to, bool top)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(file);
            if (name == ClipStore.LockFileName)
            {
                // Only says the folder is in use. Made again when it is.
                continue;
            }

            File.Copy(file, Path.Combine(to, name));
        }

        foreach (string folder in Directory.GetDirectories(from))
        {
            string name = Path.GetFileName(folder);
            if ((top && name == UpdatesFolderName) || new DirectoryInfo(folder).LinkTarget is not null)
            {
                continue;
            }

            Copy(folder, Path.Combine(to, name), top: false);
        }
    }
}
