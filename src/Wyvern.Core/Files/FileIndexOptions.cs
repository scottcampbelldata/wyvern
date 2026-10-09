using Wyvern.Core.Settings;

namespace Wyvern.Core.Files;

/// <summary>Which folders to index and which folder names to skip, worked out from settings.json.</summary>
public sealed class FileIndexOptions
{
    public static readonly IReadOnlyList<string> DefaultExcludedNames = ["node_modules", ".git", "bin", "obj", ".vs", "__pycache__"];

    private FileIndexOptions(IReadOnlyList<string> folders, IReadOnlySet<string> excludedNames, IReadOnlyList<string> rejected)
    {
        Folders = folders;
        ExcludedNames = excludedNames;
        Rejected = rejected;
    }

    /// <summary>Full paths without a trailing separator. None is inside another.</summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>Folder names that are never indexed or entered. Compared without regard to case.</summary>
    public IReadOnlySet<string> ExcludedNames { get; }

    /// <summary>Entries of "indexedFolders" that are not usable full paths, exactly as written.</summary>
    public IReadOnlyList<string> Rejected { get; }

    /// <summary>Documents, Desktop and Downloads of the current user.</summary>
    public static IReadOnlyList<string> DefaultFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    ];

    /// <summary>
    /// A missing "indexedFolders" or "excludedFolderNames" key means the defaults. Environment
    /// variables such as %USERPROFILE% are expanded. A folder listed twice, or listed inside
    /// another listed folder, is indexed once.
    /// </summary>
    public static FileIndexOptions From(WyvernSettings settings)
    {
        var folders = new List<string>();
        var rejected = new List<string>();
        foreach (string written in settings.IndexedFolders ?? DefaultFolders())
        {
            if (string.IsNullOrWhiteSpace(written))
            {
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(written.Trim());
            if (!Path.IsPathFullyQualified(expanded)
                || ExpandedPath.HasUnexpandedVariable(expanded)
                || !TryNormalize(expanded, out string folder))
            {
                rejected.Add(written);
                continue;
            }

            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        var outermost = folders
            .Where(folder => !folders.Any(other => !ReferenceEquals(other, folder) && FilePaths.IsUnder(folder, other)))
            .ToList();

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in settings.ExcludedFolderNames ?? DefaultExcludedNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                excluded.Add(name.Trim());
            }
        }

        return new FileIndexOptions(outermost, excluded, rejected);
    }

    /// <returns>The indexed folder that contains the path, or null when none does.</returns>
    public string? FolderOf(string path)
    {
        foreach (string folder in Folders)
        {
            if (FilePaths.IsUnder(path, folder))
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the path is inside a folder whose name is excluded, or is such a folder itself.
    /// A file may carry an excluded name; only folders are excluded.
    /// </summary>
    /// <param name="folder">The indexed folder containing the path, from <see cref="FolderOf"/>.</param>
    public bool IsExcluded(string folder, string path, bool isDirectory)
    {
        string[] segments = path[folder.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        int folderSegments = isDirectory ? segments.Length : segments.Length - 1;
        for (int i = 0; i < folderSegments; i++)
        {
            if (ExcludedNames.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
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
