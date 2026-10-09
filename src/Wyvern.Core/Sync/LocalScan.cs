using System.Security.Cryptography;

namespace Wyvern.Core.Sync;

/// <summary>How a path stands on this machine against the record of the last sync.</summary>
public enum LocalChange
{
    /// <summary>There is no file, and the record does not know the path.</summary>
    Absent,

    /// <summary>There is a file that the record does not know, or knows only as deleted.</summary>
    New,

    /// <summary>The file is as it was when it was last synced.</summary>
    Unchanged,

    /// <summary>The file's content is not what it was when it was last synced.</summary>
    Changed,

    /// <summary>The record knows a file here, and it is gone.</summary>
    Deleted,

    /// <summary>The file is gone, and the record already says so: its tombstone is on the server.</summary>
    Gone,
}

/// <summary>One file in the data folder as a run found it.</summary>
/// <param name="Path">Its path under the data folder, with forward slashes, as spelt on the disk.</param>
/// <param name="Stat">Its size and time.</param>
/// <param name="Change"><see cref="LocalChange.New"/>, <see cref="LocalChange.Unchanged"/> or <see cref="LocalChange.Changed"/>.</param>
/// <param name="Touched">True when the file was written again with the same content: nothing to sync, and the record takes its new time.</param>
public sealed record LocalFile(string Path, LocalStat Stat, LocalChange Change, bool Touched = false)
{
    /// <summary>The type's name and the change, and never the path.</summary>
    public override string ToString() => $"{nameof(LocalFile)} {{ Change = {Change} }}";
}

/// <summary>The data folder as one run found it.</summary>
public sealed class LocalView
{
    private readonly Dictionary<string, LocalFile> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknown = new(StringComparer.Ordinal);
    private readonly List<string> _unknownFolders = [];

    /// <summary>An empty view, which <see cref="LocalScan"/> fills.</summary>
    public LocalView()
    {
    }

    /// <summary>A view of these files. For the tests of what is decided from a view.</summary>
    /// <param name="unknown">Paths whose files could not be looked at.</param>
    /// <param name="unknownFolders">Folders that could not be listed, as paths without a slash at the end.</param>
    public LocalView(IEnumerable<LocalFile> files, IEnumerable<string>? unknown = null, IEnumerable<string>? unknownFolders = null)
    {
        foreach (var file in files)
        {
            Add(SyncPath.Key(file.Path), file);
        }

        foreach (string path in unknown ?? [])
        {
            AddUnknown(SyncPath.Key(path));
        }

        foreach (string folder in unknownFolders ?? [])
        {
            AddUnknownFolder(SyncPath.Key(folder));
        }
    }

    /// <summary>The files that sync works with, by their key (<see cref="SyncPath.Key"/>).</summary>
    public IReadOnlyDictionary<string, LocalFile> Files => _files;

    /// <summary>How many files are over <see cref="SyncLimits.MaxFileBytes"/> and so not synced.</summary>
    public int TooLarge { get; internal set; }

    /// <summary>
    /// How many folders and files are links, which are never followed. A link stays a link, so
    /// this count is the same at every run until the owner removes it: it is no reason to try
    /// again.
    /// </summary>
    public int Links { get; internal set; }

    /// <summary>
    /// How many folders and files could not be looked at this time, and may be at another:
    /// a folder that could not be listed, a file that could not be asked about or read, a
    /// folder or file of which it could not be told whether it is a link. Links are not counted
    /// here (see <see cref="Links"/>).
    /// </summary>
    public int Unreadable { get; internal set; }

    /// <summary>
    /// How many files have a name that cannot be synced, or share their key
    /// (<see cref="SyncPath.Key"/>) with another file: the name of another file in other case,
    /// or with a letter that is lower-cased to the same one. Of files that share a key none is
    /// in <see cref="Files"/> and the key is unknown to the run, whatever each of them is: a
    /// file, a link, or a file that cannot be read. Every one of them is counted here, with
    /// one exception: the one that the scan met first, when that one is itself a link, too
    /// large or unreadable, is counted as that (<see cref="Links"/>, <see cref="TooLarge"/>,
    /// <see cref="Unreadable"/>). Every later one is counted here without being looked at.
    /// </summary>
    public int BadNames { get; internal set; }

    /// <summary>How many of sync's own leftover temporary files were removed.</summary>
    public int LeftoversRemoved { get; internal set; }

    /// <summary>
    /// False for a path about which this run knows nothing: its file is too large, could not be
    /// read, or lies in a folder that could not be listed or is a link. Such a path is left
    /// exactly as it is on both sides. Above all it is never taken for deleted.
    /// </summary>
    public bool Knows(string key) => !_unknown.Contains(key) && !_unknownFolders.Exists(folder => key.StartsWith(folder, StringComparison.Ordinal));

    internal void Add(string key, LocalFile file) => _files[key] = file;

    internal void Remove(string key) => _files.Remove(key);

    internal void AddUnknown(string key) => _unknown.Add(key);

    internal void AddUnknownFolder(string keyOfFolder) => _unknownFolders.Add(keyOfFolder + "/");
}

/// <summary>
/// Looks at every file under the data folder and says how each stands against the record of
/// the last sync. A file whose size and time are those of the record is taken as unchanged
/// without being read; one of the same size and another time is read and hashed, and is
/// unchanged when the hash is the record's; any other file the record knows has changed.
/// </summary>
/// <remarks>
/// What cannot be looked at is never passed over in silence, because a file that is missing
/// from this view is a file the run would report as deleted to the owner's other machine. A
/// folder that cannot be listed, a folder that is a link (links are not followed), a file that
/// is too large or cannot be read: each makes the paths it stands for unknown to this run
/// (<see cref="LocalView.Knows"/>). So do two files whose keys are equal
/// (<see cref="SyncPath.Key"/>), for both of them: a key stands for one file, and neither is
/// synced as long as there are two. When the data folder itself cannot be listed there is no
/// view at all, and the run does nothing.
/// </remarks>
public static class LocalScan
{
    /// <returns>
    /// The view, or null when the data folder is not there, cannot be listed, or is no data
    /// folder (<see cref="SyncFiles.IsDataFolder"/>: not a full path, or the root of a drive
    /// or of a share).
    /// </returns>
    public static LocalView? Run(string dataRoot, SyncState state, DateTimeOffset now)
    {
        // Before anything is asked of the disk: a data folder that is the root of a drive or of
        // a share, or is not a full path, is no data folder, whether it is there or not.
        if (!SyncFiles.IsDataFolder(dataRoot))
        {
            return null;
        }

        var view = new LocalView();
        string root = Path.TrimEndingDirectorySeparator(dataRoot);
        try
        {
            var folder = new DirectoryInfo(root);
            if (!folder.Exists)
            {
                return null;
            }

            List(folder, "", state, now, view, isRoot: true);
            return view;
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            return null;
        }
    }

    private static void List(DirectoryInfo folder, string prefix, SyncState state, DateTimeOffset now, LocalView view, bool isRoot)
    {
        FileSystemInfo[] children;
        try
        {
            children = folder.GetFileSystemInfos();
        }
        catch (Exception ex) when (!isRoot && SyncFiles.IsFileFailure(ex))
        {
            view.Unreadable++;
            view.AddUnknownFolder(SyncPath.Key(prefix.TrimEnd('/')));
            return;
        }

        foreach (var child in children.OrderBy(child => child.Name, StringComparer.Ordinal))
        {
            string path = prefix + child.Name;
            if (!SyncPath.TryParse(path, out _))
            {
                // A name that cannot be synced. It was never uploaded, so nothing is owed for it.
                view.BadNames++;
                continue;
            }

            if (!SyncPath.IsSynced(path))
            {
                if (child is FileInfo temporary && SyncFiles.RemoveLeftover(temporary, now))
                {
                    view.LeftoversRemoved++;
                }

                continue;
            }

            string key = SyncPath.Key(path);
            if (child is FileInfo && view.Files.ContainsKey(key))
            {
                // A second file with the key of one that was already found: the same name in
                // other case in a folder that tells the two apart, or two names that differ in
                // a letter that is lower-cased to the same one (the Kelvin sign and k). A key
                // stands for one file and one blob, and which of the two that is cannot be
                // told from the key. So neither is synced: the first is taken out again, the
                // key is unknown to this run, and both are counted. This is decided before the
                // second is looked at in any way, so it holds whatever the second is: a file,
                // a link, or something that cannot be asked about.
                view.Remove(key);
                view.AddUnknown(key);
                view.BadNames += 2;
                continue;
            }

            if (child is FileInfo && !view.Knows(key))
            {
                // Its key is already unknown: a third file of such a pair, a file whose key is
                // that of one that could not be looked at or is a link, or one whose path, in
                // other case, lies under a folder that could not be. It is not synced either,
                // and is not looked at.
                view.BadNames++;
                continue;
            }

            bool? link = IsLink(child);
            if (link is not false)
            {
                // Not followed: what is behind a link is another place, and what cannot be told
                // to be a link is not risked. Either way the path is unknown to this run. A
                // link is permanent and is counted as one; not being able to tell is a failure.
                if (link is true)
                {
                    view.Links++;
                }
                else
                {
                    view.Unreadable++;
                }

                if (child is DirectoryInfo)
                {
                    view.AddUnknownFolder(key);
                }
                else
                {
                    view.AddUnknown(key);
                }
            }
            else if (child is DirectoryInfo directory)
            {
                List(directory, path + "/", state, now, view, isRoot: false);
            }
            else
            {
                Look((FileInfo)child, path, key, state, view);
            }
        }
    }

    private static void Look(FileInfo file, string path, string key, SyncState state, LocalView view)
    {
        LocalStat stat;
        try
        {
            stat = SyncFiles.StatOf(file);
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            view.Unreadable++;
            view.AddUnknown(key);
            return;
        }

        if (stat.Size > SyncLimits.MaxFileBytes)
        {
            view.TooLarge++;
            view.AddUnknown(key);
            return;
        }

        if (state.Find(key) is not { Kind: SyncedKind.Live } synced)
        {
            view.Add(key, new LocalFile(path, stat, LocalChange.New));
        }
        else if (stat == new LocalStat(synced.Size, synced.MtimeUtc))
        {
            view.Add(key, new LocalFile(synced.Path, stat, LocalChange.Unchanged));
        }
        else if (stat.Size != synced.Size)
        {
            view.Add(key, new LocalFile(synced.Path, stat, LocalChange.Changed));
        }
        else
        {
            // The same size at another time: only its content says whether it changed. It is
            // hashed as it is read, so that none of it is kept in memory. A file that is no
            // longer the size it was listed with has changed.
            bool same;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                same = stream.Length == stat.Size && Convert.ToHexStringLower(SHA256.HashData(stream)) == synced.Sha256;
            }
            catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
            {
                view.Unreadable++;
                view.AddUnknown(key);
                return;
            }

            view.Add(key, new LocalFile(synced.Path, stat, same ? LocalChange.Unchanged : LocalChange.Changed, Touched: same));
        }
    }

    // Whether a folder or file is a link: true, false, or null when that cannot be told. The
    // rule is that of FileScanner.IsLink (a reparse point that has a link target; a cloud
    // folder's reparse point has none, and is entered), except that "cannot be told" is not
    // answered as if it were "is a link": the two are counted apart.
    private static bool? IsLink(FileSystemInfo item)
    {
        try
        {
            return (item.Attributes & FileAttributes.ReparsePoint) != 0 && item.LinkTarget is not null;
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            return null;
        }
    }
}
