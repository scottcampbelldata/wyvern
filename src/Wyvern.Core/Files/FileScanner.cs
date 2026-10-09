using System.Security;

namespace Wyvern.Core.Files;

/// <summary>Lists the files and folders under a folder. Reads names and attributes only, never contents.</summary>
public static class FileScanner
{
    /// <summary>Hidden and system items are left out of the index.</summary>
    public const FileAttributes SkippedAttributes = FileAttributes.Hidden | FileAttributes.System;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = SkippedAttributes,
    };

    /// <summary>
    /// Everything under <paramref name="folder"/>, not including the folder itself. Folders whose
    /// name is in <paramref name="excludedNames"/> are left out along with their contents. A
    /// folder that cannot be read, or that disappears during the scan, is skipped.
    /// </summary>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public static List<FileEntry> Scan(string folder, IReadOnlySet<string> excludedNames, CancellationToken cancellation)
    {
        var entries = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out string? directory))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                foreach (var child in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Options))
                {
                    bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                    if (isDirectory && excludedNames.Contains(child.Name))
                    {
                        continue;
                    }

                    // Every entry in one folder shares this one string for its Directory.
                    entries.Add(new FileEntry(directory, child.Name, isDirectory));
                    if (isDirectory && !IsLink(child))
                    {
                        pending.Push(Path.Combine(directory, child.Name));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // The folder vanished or cannot be read. The rest of the scan carries on.
            }
        }

        return entries;
    }

    /// <summary>
    /// True for a symbolic link or junction. Those are indexed as folders but never entered,
    /// because a link can point back up the tree and make the scan loop for ever. Cloud folders
    /// such as OneDrive's are reparse points too, but have no link target, and are entered.
    /// </summary>
    public static bool IsLink(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return item.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell, so do not risk entering it
        }
    }
}
