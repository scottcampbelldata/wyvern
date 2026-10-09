namespace Beacon.Core.Files;

/// <summary>
/// Lists everything under a folder. <see cref="FileScanner.Scan"/> is the one Beacon uses; a
/// test passes its own to decide exactly when a scan finishes.
/// </summary>
public delegate IReadOnlyCollection<FileEntry> FolderScan(
    string folder, IReadOnlySet<string> excludedNames, CancellationToken cancellation);

/// <summary>
/// Applies what the file system reports (something appeared, disappeared or was renamed) to the
/// index, using the same rules as the scanner for what is left out.
/// </summary>
public sealed class FileIndexUpdater
{
    private readonly FileIndex _index;
    private readonly FileIndexOptions _options;
    private readonly FolderScan _scan;

    public FileIndexUpdater(FileIndex index, FileIndexOptions options, FolderScan? scan = null)
    {
        _index = index;
        _options = options;
        _scan = scan ?? FileScanner.Scan;
    }

    /// <param name="cancellation">Stops the scan of a folder that was moved in.</param>
    /// <exception cref="OperationCanceledException">The scan of a folder that was moved in was cancelled.</exception>
    public void Created(string path, CancellationToken cancellation = default)
    {
        if (TryDescribe(path, out var entry, out bool enter))
        {
            Add(path, entry, enter, cancellation);
        }
    }

    public void Deleted(string path) => _index.Remove(path);

    /// <param name="cancellation">Stops the scan of a folder that was moved in.</param>
    /// <exception cref="OperationCanceledException">The scan of a folder that was moved in was cancelled.</exception>
    public void Renamed(string oldPath, string newPath, CancellationToken cancellation = default)
    {
        if (!TryDescribe(newPath, out var entry, out bool enter))
        {
            // Renamed to something that is not indexed, such as an excluded folder name.
            _index.Remove(oldPath);
        }
        else if (!_index.Rename(oldPath, newPath))
        {
            // The old name was not indexed, so this is the first the index hears of it.
            Add(newPath, entry, enter, cancellation);
        }
    }

    /// <summary>Scans one indexed folder again and swaps its entries in one step.</summary>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public void Rescan(string folder, CancellationToken cancellation) =>
        _index.ReplaceUnder(folder, _scan(folder, _options.ExcludedNames, cancellation));

    private void Add(string path, FileEntry entry, bool enter, CancellationToken cancellation)
    {
        _index.Add(entry);
        if (enter)
        {
            // A folder moved or copied in arrives as one event; its contents are not announced.
            _index.AddRange(_scan(path, _options.ExcludedNames, cancellation));
        }
    }

    /// <param name="enter">True for a folder whose contents should be scanned too.</param>
    /// <returns>False when the path should not be in the index.</returns>
    private bool TryDescribe(string path, out FileEntry entry, out bool enter)
    {
        entry = default;
        enter = false;
        path = Path.TrimEndingDirectorySeparator(path);
        if (_options.FolderOf(path) is not { } folder || path.Length == folder.Length)
        {
            return false; // outside every indexed folder, or an indexed folder itself
        }

        // A build or an npm install under an indexed folder raises tens of thousands of events
        // inside folders that are never indexed. This tests only the folders above the path,
        // by name and without touching the disk, so those events cost almost nothing.
        if (_options.IsExcluded(folder, path, isDirectory: false))
        {
            return false;
        }

        FileSystemInfo item;
        bool enterable;
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileScanner.SkippedAttributes) != 0 || IsInsideSkippedFolder(path, folder))
            {
                return false;
            }

            item = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            enterable = item is DirectoryInfo && !FileScanner.IsLink(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // already gone again, or not readable
        }

        bool isDirectory = item is DirectoryInfo;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || _options.IsExcluded(folder, path, isDirectory))
        {
            return false;
        }

        entry = new FileEntry(Path.TrimEndingDirectorySeparator(directory), item.Name, isDirectory);
        enter = enterable;
        return true;
    }

    // The scanner never enters a hidden or system folder, so nothing inside one belongs in the
    // index however it got there. The indexed folder itself is not checked: the owner listed it.
    private static bool IsInsideSkippedFolder(string path, string folder)
    {
        for (string? parent = Path.GetDirectoryName(path);
             parent is not null && parent.Length > folder.Length;
             parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileScanner.SkippedAttributes) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
