using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Files;

namespace Beacon.Core.Sync;

/// <summary>
/// The id a data folder carries: 32 lower-case hex digits (16 random bytes) in the file
/// <c>.~beacon-sync-id</c>, directly in the data folder. The record of the last sync is made
/// for a folder with its id (<see cref="SyncState.ScopeOf"/>), and is only used for the folder
/// that holds that id.
/// </summary>
/// <remarks>
/// <para>A folder's path says nothing of the folder as it stands on the disk. Without the id, a
/// record would be used against an empty folder whenever the path is the same: Beacon's files
/// copied to another machine without the data, a data folder on a drive that was replaced by
/// an empty one, a data folder emptied by hand to fetch everything again. Every file in the
/// record would then count as deleted here, and the owner's other machine would delete its
/// copies. With the id, such a folder has another id or none, the record is not used, nothing
/// is deleted, and every file is compared afresh by its content.</para>
/// <para>What the id does not tell: a data folder that was put back from a backup. The id file
/// lies in the data folder, so a backup of the folder holds it, and the folder that is put
/// back has the same id as before. The record of the last sync, which is newer than the
/// backup, is then used against the older folder: a file made since the backup counts as
/// deleted here, and a file edited since counts as edited here, so that its older version is
/// sent over the newer one. The id still does not tell such a folder from the one the record
/// was made for. What holds it, in part, is the brake on mass deletion
/// (<see cref="SyncEnd.Held"/>): when the folder misses many of the library files that were
/// synced, or all of them, the run sends nothing and sync is paused, and Restore Missing Files
/// then fetches what is missing and keeps both versions of every file that differs. Not
/// guarded: a folder that misses too few files to begin a pause, whose missing files go as
/// deletions and whose older versions go up as edits; and this machine's own clipboard
/// folders, which the brake does not count. So the advice stands. Whoever puts a data folder
/// back runs Restore Missing Files first, or deletes this file from it (or the record of the
/// last sync) before Beacon runs, and everything is then compared afresh. The id does not tell
/// a folder that was emptied but for this file either: that is what deleting the files one by
/// one looks like.</para>
/// <para>The name starts with <c>.~</c>, so sync never uploads the file and the scan of the
/// data folder passes over it. It is an ordinary file that anyone can see, on purpose:
/// emptying the folder takes it along.</para>
/// </remarks>
public static class SyncFolderId
{
    /// <summary>The name of the file, directly in the data folder.</summary>
    public const string FileName = ".~beacon-sync-id";

    /// <summary>How many hex digits an id has.</summary>
    public const int Length = 32;

    private const string TemporarySuffix = ".tmp";

    private enum Found
    {
        /// <summary>Nothing has the name.</summary>
        Nothing,

        /// <summary>The file holds exactly an id.</summary>
        Id,

        /// <summary>The file holds anything else: nothing, something damaged, something longer.</summary>
        SomethingElse,

        /// <summary>The name is a link or a folder. It is neither read nor replaced.</summary>
        Refused,
    }

    /// <summary>
    /// The id of a data folder. When the file is not there it is made; when it holds anything
    /// but an id it is replaced by a new id. Never throws, and follows no link.
    /// </summary>
    /// <param name="dataRoot">The data folder, as a full path.</param>
    /// <param name="id">The id when the answer is true, otherwise empty.</param>
    /// <returns>
    /// False when the data folder is not there or is not given by a full path, when the file is
    /// a link or a folder, and when it can be neither read nor made (held by another program,
    /// or not to be written). The caller then does nothing and asks again later.
    /// </returns>
    public static bool TryGet(string dataRoot, out string id)
    {
        id = "";
        try
        {
            string root = Path.TrimEndingDirectorySeparator(dataRoot);
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            {
                return false;
            }

            string file = Path.Combine(root, FileName);

            // Twice at most: when another process made the file between the look and the
            // making, theirs is read at the second turn.
            for (int turn = 0; turn < 2; turn++)
            {
                switch (Read(file, out id))
                {
                    case Found.Id:
                        return true;

                    case Found.Refused:
                        return false;

                    case Found.SomethingElse:
                        return Make(root, file, replace: true, out id);

                    default:
                        if (Make(root, file, replace: false, out id))
                        {
                            return true;
                        }

                        break;
                }
            }

            return false;
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            id = "";
            return false;
        }
    }

    private static Found Read(string file, out string id)
    {
        id = "";
        if (Directory.Exists(file))
        {
            return Found.Refused;
        }

        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return Found.Nothing;
        }

        if (FileScanner.IsLink(info))
        {
            // Not followed: what is behind a link is another place.
            return Found.Refused;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != Length)
        {
            return Found.SomethingElse;
        }

        Span<byte> bytes = stackalloc byte[Length];
        stream.ReadExactly(bytes);
        if (bytes.IndexOfAnyExcept("0123456789abcdef"u8) >= 0)
        {
            return Found.SomethingElse;
        }

        id = Encoding.ASCII.GetString(bytes);
        return Found.Id;
    }

    // Writes a new id to a temporary file beside the id file, flushed, which then takes the
    // name in one step: the id file is never found half written. Without "replace" the step
    // fails when something has the name by now, and that file is left alone.
    private static bool Make(string root, string file, bool replace, out string id)
    {
        id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length / 2));

        // One of sync's own temporary names: never uploaded, and removed by a later run should
        // this one be cut off before it is done.
        string temporary = Path.Combine(root, SyncFiles.TemporaryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + TemporarySuffix);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.ASCII.GetBytes(id));
                stream.Flush(flushToDisk: true);
            }

            if (replace)
            {
                File.Move(temporary, file, overwrite: true);
                return true;
            }

            try
            {
                File.Move(temporary, file, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(file))
            {
                // Another process made it meanwhile. Theirs is the id.
                Remove(temporary);
                id = "";
                return false;
            }
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            Remove(temporary);
            id = "";
            throw;
        }
    }

    private static void Remove(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            // It is removed as a leftover by a later run.
        }
    }
}
