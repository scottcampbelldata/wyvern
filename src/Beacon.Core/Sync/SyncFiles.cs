using System.Security;
using System.Security.Cryptography;

namespace Beacon.Core.Sync;

/// <summary>A file's size and the time it was last written: what tells cheaply whether it has changed.</summary>
public readonly record struct LocalStat(long Size, DateTimeOffset MtimeUtc)
{
    /// <summary>The type's name and the size.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(LocalStat)} {{ Size = {Size} }}");
}

/// <summary>How a change to a file in the data folder went.</summary>
public enum FileOutcome
{
    /// <summary>It was done.</summary>
    Done,

    /// <summary>
    /// The file is not as it was when the run looked at it: it was edited, made or removed in
    /// the meantime. Nothing was touched; the next run sees how things stand.
    /// </summary>
    Changed,

    /// <summary>The path is not one sync may touch: it is not a safe relative path, or a folder on the way is a link or a file. Nothing was touched.</summary>
    Unsafe,

    /// <summary>Windows refused: the file is held by another program, or may not be written. Nothing was changed.</summary>
    Failed,
}

/// <summary>
/// The only code of sync that reads, writes, renames or deletes a file in the data folder.
/// Every path is checked here once more before it is used, whoever checked it before: it must
/// be a safe relative path that sync would upload, it must lie inside the data folder, and no
/// folder on the way to it may be a link, so that nothing is ever written or deleted through a
/// link into another place. A file is written to a temporary file beside it that then takes its
/// place, so it is never found half written.
/// </summary>
public static class SyncFiles
{
    /// <summary>What the name of sync's own temporary file starts with. Such a name is never synced.</summary>
    public const string TemporaryPrefix = ".~beacon-sync-";

    private const string TemporarySuffix = ".tmp";
    private const int TemporaryRandomLength = 16;

    // A file is replaced in at most this many tries, with a pause that starts at 50 ms and
    // doubles: an editor or a scanner that has it open lets go within moments.
    private const int ReplaceAttempts = 5;
    private const int FirstPauseMilliseconds = 50;

    // A time before this, or more than a day ahead of now, is not given to a file.
    private static readonly DateTimeOffset EarliestTime = new(1980, 1, 2, 0, 0, 0, TimeSpan.Zero);

    // How old one of sync's own temporary files has to be before it is taken for a leftover.
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    /// <summary>
    /// True when the text names a folder that may be the data folder: a full path, with a drive
    /// or a share, that is not the root of a drive or of a share (<c>C:\</c>,
    /// <c>\\server\share</c>). A root is never synced, and nothing is written or deleted in it:
    /// it holds a whole drive, and a whole share is no folder to trust. This is the one rule
    /// that <see cref="SyncFiles"/> and <see cref="LocalScan"/> both go by. It is decided from
    /// the text alone, before anything is asked of the disk or of the network.
    /// </summary>
    /// <remarks>
    /// A text with a tilde in it is not handed to the system to be made whole, as any other
    /// is: a name such as <c>PROGRA~1</c> may be a short name, and the system would look its
    /// long name up on the disk, or on the server of a share. Such a text is judged exactly as
    /// it is written. It may be the data folder when it names something below its root and
    /// every name in it is one the system would leave as it is: none that is <c>.</c> or
    /// <c>..</c>, and none that ends in a dot or a space. So <c>C:\DATA~1</c> may be one, and
    /// <c>C:\DATA~1\..</c> and <c>C:\DATA~1\sub\..</c> are refused without being worked out.
    /// </remarks>
    public static bool IsDataFolder(string dataRoot)
    {
        if (string.IsNullOrEmpty(dataRoot) || !Path.IsPathFullyQualified(dataRoot))
        {
            return false;
        }

        if (dataRoot.Contains('~'))
        {
            return IsPlainlyBelowItsRoot(dataRoot);
        }

        try
        {
            // Normalised first, so that C:\data\.. is taken for the root of C: as well. Plain
            // text work for a text with no tilde in it, which is all that gets here.
            string full = Path.GetFullPath(dataRoot);
            string root = Path.GetPathRoot(full) ?? "";
            return !string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    // True when a full path, taken as it is written, has at least one name after its root and
    // no name that the system would drop or change when it made the path whole: "." and "..",
    // and any name that ends in a dot or a space. With none of those, making the path whole
    // leaves every name in place, so the path cannot come to be its own root.
    private static bool IsPlainlyBelowItsRoot(string full)
    {
        var rest = full.AsSpan(Path.GetPathRoot(full.AsSpan()).Length);
        bool below = false;
        while (!rest.IsEmpty)
        {
            int end = rest.IndexOfAny('\\', '/');
            var name = end < 0 ? rest : rest[..end];
            rest = end < 0 ? [] : rest[(end + 1)..];
            if (name.IsEmpty)
            {
                continue;
            }

            if (name[^1] is '.' or ' ')
            {
                return false;
            }

            below = true;
        }

        return below;
    }

    /// <summary>The file's size and time, or null when it is not there, cannot be asked about, or is not a path sync may touch.</summary>
    public static LocalStat? Stat(string dataRoot, string path)
    {
        try
        {
            return Target(dataRoot, path) is { } full && new FileInfo(full) is { Exists: true } file ? StatOf(file) : null;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a whole file, of at most <see cref="SyncLimits.MaxFileBytes"/>, without shutting out
    /// a program that has it open.
    /// </summary>
    /// <param name="stat">The file's size and time as they stood just before it was read.</param>
    /// <returns>The bytes, or null when the file is not there, is too large, cannot be read, or is not a path sync may touch.</returns>
    public static byte[]? Read(string dataRoot, string path, out LocalStat stat)
    {
        stat = default;
        try
        {
            if (Target(dataRoot, path) is not { } full)
            {
                return null;
            }

            var file = new FileInfo(full);
            if (!file.Exists)
            {
                return null;
            }

            stat = StatOf(file);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > SyncLimits.MaxFileBytes)
            {
                return null;
            }

            byte[] content = new byte[stream.Length];
            stream.ReadExactly(content);
            return content;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Writes a file, making its folders when they are not there.</summary>
    /// <param name="mtimeUtc">The time to give the file, when it is a sane one; otherwise it keeps the time it was written at.</param>
    /// <param name="expected">
    /// How the file must still stand for it to be replaced. Null means that there must be no
    /// file: one that is there is never overwritten.
    /// </param>
    /// <param name="written">The file's size and time afterwards, when the answer is <see cref="FileOutcome.Done"/>.</param>
    /// <param name="pause">What waits between two tries at replacing a file that is held. Left out everywhere except in tests: <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    public static FileOutcome Write(string dataRoot, string path, ReadOnlySpan<byte> content, DateTimeOffset mtimeUtc, LocalStat? expected, DateTimeOffset now, out LocalStat written, Action<TimeSpan>? pause = null)
    {
        written = default;
        string? temporary = null;
        try
        {
            if (Target(dataRoot, path) is not { } full)
            {
                return FileOutcome.Unsafe;
            }

            if (Stat(dataRoot, path) != expected)
            {
                return FileOutcome.Changed;
            }

            string folder = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(folder);
            temporary = Path.Combine(folder, TemporaryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TemporaryRandomLength / 2)) + TemporarySuffix);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (mtimeUtc >= EarliestTime && mtimeUtc <= now.AddDays(1))
            {
                File.SetLastWriteTimeUtc(temporary, mtimeUtc.UtcDateTime);
            }

            // What the file will be: a rename keeps the size and the last-write time of the file
            // it moves. Taken here, before the move, so that nothing asked after it can turn a
            // file that was written into a failure.
            var stamped = StatOf(new FileInfo(temporary));

            if (expected is null)
            {
                // Fails when a file has appeared there meanwhile, which is then left alone.
                try
                {
                    File.Move(temporary, full, overwrite: false);
                }
                catch (IOException) when (File.Exists(full))
                {
                    TryDelete(temporary);
                    return FileOutcome.Changed;
                }
            }
            else if (!Replace(dataRoot, path, temporary, full, expected.Value, pause ?? (static length => Thread.Sleep(length))))
            {
                TryDelete(temporary);
                return FileOutcome.Changed;
            }

            temporary = null;
            written = stamped;
            return FileOutcome.Done;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }

            return FileOutcome.Failed;
        }
    }

    /// <summary>
    /// Deletes a file, when it still stands as expected. A file that is known to be gone is
    /// done; one that cannot be asked about is not, and is <see cref="FileOutcome.Failed"/>.
    /// </summary>
    /// <param name="cannotBeAsked">
    /// Left out everywhere except in tests. How the file stands is always asked of the disk
    /// (<see cref="Stat"/>), and nothing given here can stand in for that look. When this
    /// answers true, the answer that came back is thrown away and the file is treated as one
    /// that could not be asked about, which a test has no other way to bring about.
    /// </param>
    public static FileOutcome Delete(string dataRoot, string path, LocalStat expected, Func<bool>? cannotBeAsked = null)
    {
        try
        {
            if (Target(dataRoot, path) is not { } full)
            {
                return FileOutcome.Unsafe;
            }

            var asked = Stat(dataRoot, path);
            if (cannotBeAsked?.Invoke() == true)
            {
                asked = null;
            }

            if (asked is not { } now)
            {
                // Stat says null for a file that is not there and for one that cannot be asked
                // about. Done is for a file known to be gone; any other is not deleted, and the
                // caller is not told that it is. "Not there" is known only while the data
                // folder itself is there: with the folder gone or out of reach (its drive
                // unplugged, its share not answering), Windows says "path not found" for every
                // file in it, and the caller would enter each of them as deleted.
                return IsAbsent(full) && IsThere(dataRoot) ? FileOutcome.Done : FileOutcome.Failed;
            }

            if (now != expected)
            {
                return FileOutcome.Changed;
            }

            File.Delete(full);
            return FileOutcome.Done;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return FileOutcome.Failed;
        }
    }

    /// <summary>
    /// Renames a file inside the data folder, when it still stands as expected and nothing has
    /// the new name. This is how the version that lost a conflict is kept.
    /// </summary>
    public static FileOutcome Move(string dataRoot, string from, string to, LocalStat expected)
    {
        try
        {
            if (Target(dataRoot, from) is not { } source || Target(dataRoot, to) is not { } target)
            {
                return FileOutcome.Unsafe;
            }

            if (Stat(dataRoot, from) != expected || File.Exists(target))
            {
                return FileOutcome.Changed;
            }

            File.Move(source, target, overwrite: false);
            return FileOutcome.Done;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return FileOutcome.Failed;
        }
    }

    /// <summary>True when the name is exactly that of one of sync's own temporary files.</summary>
    public static bool IsTemporaryName(string name) =>
        name.Length == TemporaryPrefix.Length + TemporaryRandomLength + TemporarySuffix.Length
        && name.StartsWith(TemporaryPrefix, StringComparison.Ordinal)
        && name.EndsWith(TemporarySuffix, StringComparison.Ordinal)
        && name.AsSpan(TemporaryPrefix.Length, TemporaryRandomLength).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>
    /// Deletes one of sync's own temporary files that a run which was cut off left behind, when
    /// it is more than an hour old. Any other file is somebody else's and is not touched.
    /// </summary>
    /// <remarks>
    /// Its age is the time it was created, not the time it was last written: <see cref="Write"/>
    /// stamps its temporary file with the time of the file it is writing, which may be a year
    /// old, so that another process would take a file that is in use for a leftover.
    /// </remarks>
    /// <returns>True when the file was a leftover and is gone.</returns>
    public static bool RemoveLeftover(FileInfo file, DateTimeOffset now)
    {
        try
        {
            if (!IsTemporaryName(file.Name) || now - new DateTimeOffset(file.CreationTimeUtc) < LeftoverAge)
            {
                return false;
            }

            file.Delete();
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    internal static LocalStat StatOf(FileInfo file) => new(file.Length, new DateTimeOffset(file.LastWriteTimeUtc));

    internal static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException;

    // The full name of the file a path stands for, or null when sync must not touch it.
    private static string? Target(string dataRoot, string path)
    {
        if (!SyncPath.TryParse(path, out _) || !SyncPath.IsSynced(path))
        {
            return null;
        }

        // Before anything is asked of the disk: the root of a drive or of a share is no data folder.
        if (!IsDataFolder(dataRoot))
        {
            return null;
        }

        string root = Path.TrimEndingDirectorySeparator(dataRoot);
        string full = SyncPath.ToLocal(root, path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // No folder on the way may be a link, and none may be a file. A folder that is not
        // there yet has nothing below it, so the walk ends there.
        string folder = root;
        string[] names = path.Split('/');
        for (int i = 0; i < names.Length - 1; i++)
        {
            folder = Path.Combine(folder, names[i]);
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                if (File.Exists(folder))
                {
                    return null;
                }

                break;
            }

            if (info.LinkTarget is not null)
            {
                return null;
            }
        }

        if (Directory.Exists(full) || new FileInfo(full) is { Exists: true, LinkTarget: not null })
        {
            return null;
        }

        return full;
    }

    // True only when the disk says that neither a file nor a folder is at the name. Not when it
    // cannot be asked: any other failure goes up to the caller, which takes it for a failure.
    private static bool IsAbsent(string full)
    {
        try
        {
            _ = File.GetAttributes(full);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    // True when the data folder is there as a folder at this moment. Whatever keeps that from
    // being told (the folder gone, its drive or share out of reach, no right to ask) is false:
    // Directory.Exists answers false for every one of them, and does not throw.
    private static bool IsThere(string dataRoot) => Directory.Exists(Path.TrimEndingDirectorySeparator(dataRoot));

    // Moves the temporary file over the file. This fails while any other handle is open on the
    // file, so it is tried again after a pause that doubles. Returns false, and moves nothing,
    // when the file no longer stands as the run saw it.
    private static bool Replace(string dataRoot, string path, string temporary, string file, LocalStat expected, Action<TimeSpan> pause)
    {
        var length = TimeSpan.FromMilliseconds(FirstPauseMilliseconds);
        for (int attempt = 1; ; attempt++)
        {
            // Looked at again before every try, not only before the first: a try is made after a
            // pause, and the pause is for another program that has the file open and may leave
            // in it what the owner has just typed. Changed, gone, or not to be asked about all
            // mean that it is not as it was. What is left is the instant between this look and
            // the move. The calls that Core may make cannot close it: none of them renames a
            // file only if it has not changed.
            if (Stat(dataRoot, path) != expected)
            {
                return false;
            }

            try
            {
                File.Move(temporary, file, overwrite: true);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                pause(length);
                length *= 2;
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // It is removed as a leftover by a later run.
        }
    }
}
