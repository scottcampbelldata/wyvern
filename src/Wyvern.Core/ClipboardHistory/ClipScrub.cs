using System.Text;
using System.Text.Json;

namespace Wyvern.Core.ClipboardHistory;

/// <summary>What a scrub did.</summary>
/// <param name="DaysRewritten">How many day files were written again without some of their lines.</param>
/// <param name="EntriesRemoved">How many entries those rewrites took out.</param>
/// <param name="FilesRemoved">How many files of content were deleted because no record names them any more.</param>
/// <param name="Failures">How many things could not be done. Each is tried again by the next scrub.</param>
public sealed record ScrubResult(int DaysRewritten, int EntriesRemoved, int FilesRemoved, int Failures)
{
    public static ScrubResult Nothing { get; } = new(0, 0, 0, 0);

    public ScrubResult Plus(ScrubResult other) => new(
        DaysRewritten + other.DaysRewritten,
        EntriesRemoved + other.EntriesRemoved,
        FilesRemoved + other.FilesRemoved,
        Failures + other.Failures);
}

/// <summary>
/// Takes deleted and cleared entries off the disk for good. Hiding an entry is not enough: what
/// was copied may be a password, and the file is also what goes to the owner's server. So the
/// machine that wrote an entry writes its day file again without it, and deletes the files of
/// content that no record of that day names any more.
/// </summary>
/// <remarks>
/// Only this machine's own folder is ever given to these methods: another machine's files are
/// that machine's to rewrite, which is what keeps two machines from ever changing the same
/// file. A day file is never changed in place: the lines that stay are written to a temporary
/// file beside it, which then takes its place in one step (tried again a few times when another
/// program has the file open), so that a failure part of the way leaves the day as it was.
/// Delete and clear records always stay, so that the other machine still learns of them.
/// A line that stays is written as it was read, which is not exactly as it is on the disk for a
/// line that is not valid UTF-8 or holds a carriage return on its own: reading the file has
/// already replaced or split those.
/// A folder that is a symbolic link or a junction is never listed, changed or removed: what is
/// behind it is not ours. A machine folder that is one is a failure, and so is each day folder
/// that is one: what a delete or a clear took out of the day file may be behind it, so it is not
/// passed over as a day with nothing to do.
/// </remarks>
public static class ClipScrub
{
    /// <summary>What a file ends with while it is being written. Such a name is never synced, and one left behind is removed.</summary>
    public const string TemporaryExtension = ".tmp";

    private const string FolderIsALink = "This machine's clipboard folder is a link and is left alone";
    private const string DayFolderIsALink = "A day's folder in the clipboard history is a link and is left alone";
    private const string FolderNotListed = "This machine's clipboard folder could not be listed";
    private const string DayNotRewritten = "A day of clipboard history could not be rewritten";
    private const string ContentNotDeleted = "Clipboard content that is no longer in the history could not be deleted";
    private const string TemporaryNotDeleted = "A temporary file that an earlier rewrite left behind could not be deleted";

    // A day file is replaced in at most this many tries. Between two tries the pause starts at
    // 50 ms and doubles: 50, 100, 200 and 400 ms.
    private const int ReplaceAttempts = 5;
    private const int FirstPauseMilliseconds = 50;

    // A content file's name is 64 hex digits and a known extension: its hash is this long, and
    // these are the extensions that this code gives content files.
    private const int HashLength = 64;

    private static readonly string[] KnownExtensions =
    [
        ClipPaths.TextExtension, ClipPaths.HtmlExtension, ClipPaths.RtfExtension, .. ClipPaths.PictureExtensions,
    ];

    /// <summary>Scrubs every day in this machine's folder.</summary>
    /// <param name="deleted">The ids that delete records name, from every machine's files.</param>
    /// <param name="clearedBefore">The latest time any clear record gives, from every machine's files, or null.</param>
    /// <param name="report">Receives fixed words and an exception for the log. Never a name or a path. Each kind of failure is told once per run, however many days or files it was met in: the count in the result says how many there were.</param>
    /// <param name="pause">What waits between two tries at replacing a day file. Null means <see cref="Thread.Sleep(TimeSpan)"/>. A test gives one that does not wait.</param>
    public static ScrubResult Run(string deviceDir, IReadOnlySet<string> deleted, DateTimeOffset? clearedBefore, Action<string, Exception?>? report = null, Action<TimeSpan>? pause = null) =>
        Run(deviceDir, deleted, clearedBefore, report, pause, outcome: null);

    /// <summary>Scrubs every day in this machine's folder, and says which of them could not be made good.</summary>
    /// <param name="outcome">When it is given, receives the days that had a failure and whether there was a failure that belongs to no day.</param>
    internal static ScrubResult Run(string deviceDir, IReadOnlySet<string> deleted, DateTimeOffset? clearedBefore, Action<string, Exception?>? report, Action<TimeSpan>? pause, ScrubOutcome? outcome)
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        void Tell(string text, Exception? cause)
        {
            if (told.Add(text))
            {
                report?.Invoke(text, cause);
            }
        }

        var days = new SortedSet<DateOnly>();
        int failures = 0;
        try
        {
            if (Directory.Exists(deviceDir))
            {
                if (IsLink(deviceDir))
                {
                    Tell(FolderIsALink, null);
                    outcome?.FolderFailed();
                    return new ScrubResult(0, 0, 0, 1);
                }

                foreach (var found in ClipReader.DayFiles(deviceDir))
                {
                    days.Add(found.Day);
                }

                // A day's folder can outlive its file. Its content is then named by nothing.
                foreach (string folder in Directory.GetDirectories(deviceDir))
                {
                    if (ClipPaths.TryParseDayFolder(Path.GetFileName(folder), out var day))
                    {
                        days.Add(day);
                    }
                }

                failures = RemoveTemporaryFiles(deviceDir, Tell);
                if (failures > 0)
                {
                    outcome?.FolderFailed();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell(FolderNotListed, ex);
            outcome?.FolderFailed();
            return new ScrubResult(0, 0, 0, 1);
        }

        var result = new ScrubResult(0, 0, 0, failures);
        foreach (var day in days)
        {
            var scrubbed = RunDay(deviceDir, day, deleted, clearedBefore, Tell, pause);
            if (scrubbed.Failures > 0)
            {
                outcome?.DayFailed(day);
            }

            result = result.Plus(scrubbed);
        }

        return result;
    }

    /// <summary>Scrubs one day in this machine's folder: its file of records, then its folder of content.</summary>
    /// <param name="pause">What waits between two tries at replacing the day file. Null means <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    public static ScrubResult RunDay(string deviceDir, DateOnly day, IReadOnlySet<string> deleted, DateTimeOffset? clearedBefore, Action<string, Exception?>? report = null, Action<TimeSpan>? pause = null)
    {
        try
        {
            if (Directory.Exists(deviceDir) && IsLink(deviceDir))
            {
                report?.Invoke(FolderIsALink, null);
                return new ScrubResult(0, 0, 0, 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report?.Invoke(FolderNotListed, ex);
            return new ScrubResult(0, 0, 0, 1);
        }

        pause ??= static length => Thread.Sleep(length);
        string file = Path.Combine(deviceDir, ClipPaths.DayFileName(day));
        string folder = Path.Combine(deviceDir, ClipPaths.DayName(day));
        int rewritten = 0;
        int entriesRemoved = 0;
        int failures = 0;

        // The names of the files that the lines which stay still name. Null when that is not
        // known because the day file could not be read or written: no content is deleted then,
        // since an entry that is still on disk must keep its files.
        HashSet<string>? named = [];
        try
        {
            if (File.Exists(file))
            {
                var lines = ClipReader.ReadLines(file).ToList();
                bool[] stays = Stays(lines, day, deleted, clearedBefore, out entriesRemoved, out int linesRemoved);
                if (linesRemoved > 0)
                {
                    Rewrite(file, lines.Where((_, index) => stays[index]).Select(line => line.Raw), pause);
                    rewritten = 1;
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    if (!stays[i])
                    {
                        continue;
                    }

                    if (lines[i].Record is EntryRecord entry)
                    {
                        named.UnionWith(entry.FileNames());
                    }
                    else if (lines[i].Record is null)
                    {
                        // A line this build cannot read stays, and so does whatever it names: it
                        // may be an entry of a newer build, whose files are not ours to delete.
                        AddNamesIn(lines[i].Raw, named);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report?.Invoke(DayNotRewritten, ex);
            named = null;
            entriesRemoved = 0;
            failures++;
        }

        int filesRemoved = 0;
        if (named is not null)
        {
            (filesRemoved, int failed) = RemoveUnnamed(folder, named, report);
            failures += failed;
        }

        return new ScrubResult(rewritten, entriesRemoved, filesRemoved, failures);
    }

    /// <summary>
    /// True when the folder is a symbolic link or a junction. Judged by the link's target, and
    /// not by the reparse-point attribute: a folder that a cloud-sync tool looks after carries
    /// that attribute without being a link, and it must still be scrubbed.
    /// </summary>
    internal static bool IsLink(string folder) => new DirectoryInfo(folder).LinkTarget is not null;

    /// <summary>True for the name of the temporary file that a rewrite of a day file uses: that file's name and the temporary extension.</summary>
    internal static bool IsTemporaryDayFile(string name) =>
        name.EndsWith(TemporaryExtension, StringComparison.Ordinal)
        && ClipPaths.TryParseDayFile(name[..^TemporaryExtension.Length], out _);

    /// <summary>True for the name of a temporary file of content: the name of a content file and the temporary extension.</summary>
    internal static bool IsTemporaryContent(string name) =>
        name.EndsWith(TemporaryExtension, StringComparison.Ordinal)
        && ClipPaths.IsBlobName(name[..^TemporaryExtension.Length]);

    // Which lines stay. An entry goes when it is deleted or cleared. Delete and clear records
    // stay. A line that cannot be read has no time of its own, but lines are written in the
    // order things happened, so it is at least as old as the next record that can be read, or
    // as the end of the day when none follows. It goes when a clear covers that time: otherwise
    // Clear would leave on disk the one kind of line nobody can ever name in a delete. It also
    // goes when it is a JSON object with a text "id" that a delete names: the owner deleted that
    // entry on another machine, which may run a newer build that writes what this one cannot
    // read. Nothing else about such a line is looked at. In every other case it stays, because
    // a scrub must not destroy what it does not understand.
    private static bool[] Stays(
        IReadOnlyList<ClipFileLine> lines,
        DateOnly day,
        IReadOnlySet<string> deleted,
        DateTimeOffset? clearedBefore,
        out int entriesRemoved,
        out int linesRemoved)
    {
        bool[] stays = new bool[lines.Count];
        entriesRemoved = 0;
        linesRemoved = 0;
        var noLaterThan = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.IsBlank)
            {
                // Dropped when the file is written again, but never a reason to write it again.
                continue;
            }

            if (line.Record is not { } record)
            {
                if (NamesDeletedId(line.Raw, deleted))
                {
                    entriesRemoved++;
                }
                else
                {
                    stays[i] = !(clearedBefore is { } before && noLaterThan <= before);
                }
            }
            else
            {
                noLaterThan = record.Time;
                stays[i] = record is not EntryRecord || !ClipReader.IsRemoved(record.Id, record.Time, deleted, clearedBefore);
                if (!stays[i])
                {
                    entriesRemoved++;
                }
            }

            if (!stays[i])
            {
                linesRemoved++;
            }
        }

        return stays;
    }

    // The one lenient reading of a line that is not a record this build knows: is it a JSON
    // object with a text "id", and does a delete name that id? It never throws, whatever the line
    // holds: what cannot be read this way is simply not an object with an id.
    private static bool NamesDeletedId(string raw, IReadOnlySet<string> deleted)
    {
        if (deleted.Count == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } text
                && deleted.Contains(text);
        }
        catch (JsonException)
        {
            // Not JSON, or nested deeper than the reader goes.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Text that holds half of a surrogate pair written as an escape is valid JSON, but
            // reading it throws this.
            return false;
        }
        catch (ArgumentException)
        {
            // Half of a surrogate pair as a character in the line itself.
            return false;
        }
    }

    // Adds every content file name that the text holds: 64 lower-case hex digits, a dot and a
    // known extension. Whatever the line is, JSON or not, such a name in it counts, and it counts
    // wherever it stands: for each dot that a known extension follows, the 64 characters in front
    // of it are judged, and nothing else about what is round them, so a name glued to letters or
    // digits on either side is still found.
    private static void AddNamesIn(string text, HashSet<string> named)
    {
        for (int dot = text.IndexOf('.'); dot >= 0; dot = text.IndexOf('.', dot + 1))
        {
            if (dot < HashLength)
            {
                continue;
            }

            foreach (string extension in KnownExtensions)
            {
                if (text.AsSpan(dot).StartsWith(extension, StringComparison.Ordinal))
                {
                    string name = string.Concat(text.AsSpan(dot - HashLength, HashLength), extension);
                    if (ClipPaths.IsBlobName(name))
                    {
                        named.Add(name);
                    }
                }
            }
        }
    }

    private static void Rewrite(string file, IEnumerable<string> lines, Action<TimeSpan> pause)
    {
        string temporary = file + TemporaryExtension;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1 << 16, leaveOpen: true))
                {
                    foreach (string line in lines)
                    {
                        writer.Write(line);
                        writer.Write('\n');
                    }
                }

                // On the disk before it takes the place of the day file, so that a power cut
                // cannot leave the day file replaced by something half written.
                stream.Flush(flushToDisk: true);
            }

            Replace(temporary, file, pause);
        }
        catch
        {
            // The day file is as it was. The temporary file holds clipboard content and must
            // not stay behind; the failure that matters is the one being passed on.
            TryDelete(temporary);
            throw;
        }
    }

    // Moves the temporary file over the day file. This fails whenever any other handle is open
    // on the day file, even one that shares everything, which is how a virus scanner or an
    // indexer opens a file it has just been told about: and the store has just appended to this
    // very file. Such a program lets go within moments, so the move is tried again after a pause
    // that doubles from 50 ms, five times in all. File.Replace is not used.
    private static void Replace(string temporary, string file, Action<TimeSpan> pause)
    {
        var length = TimeSpan.FromMilliseconds(FirstPauseMilliseconds);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, file, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                pause(length);
                length *= 2;
            }
        }
    }

    // Deletes the files of content in a day's folder that no record names, and the folder itself
    // when nothing is left in it. Only files this code would have written are touched: a hash
    // and a known extension, or a temporary file of such a name. Anything else there is somebody
    // else's. A folder that is a link is not touched at all. One file that cannot be deleted does
    // not stop the others: each is a failure, and the first exception is the one told.
    private static (int Removed, int Failed) RemoveUnnamed(string folder, HashSet<string> named, Action<string, Exception?>? report)
    {
        int removed = 0;
        int failed = 0;
        Exception? first = null;
        try
        {
            if (!Directory.Exists(folder))
            {
                return (0, 0);
            }

            // What is behind a link is not ours to delete, so what a delete or a clear took out of
            // the day file may still be in there: that is a failure, told, and not a day with
            // nothing to do. It stays one for as long as the link is there.
            if (IsLink(folder))
            {
                report?.Invoke(DayFolderIsALink, null);
                return (0, 1);
            }

            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);
                bool temporary = IsTemporaryContent(name);
                if (!temporary && !(ClipPaths.IsBlobName(name) && !named.Contains(name)))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    if (!temporary)
                    {
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    first ??= ex;
                }
            }

            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed++;
            first ??= ex;
        }

        if (first is not null)
        {
            report?.Invoke(ContentNotDeleted, first);
        }

        return (removed, failed);
    }

    // A temporary file beside the day files is what an interrupted rewrite left. It holds
    // clipboard content that may since have been deleted. Only the name a rewrite gives it is
    // touched: a day file's name and the temporary extension. Each one that cannot be deleted is
    // a failure, and is told once.
    // The whole name is checked here, name by name, and not with a search pattern: a pattern can
    // say only how a name ends, and would take any file that ends in ".tmp", which is somebody
    // else's. (In .NET 10 "*.tmp" does not match ".tmpx", as the old .NET Framework did through
    // short file names; it is not for that reason.)
    private static int RemoveTemporaryFiles(string deviceDir, Action<string, Exception?> tell)
    {
        int failures = 0;
        foreach (string file in Directory.GetFiles(deviceDir))
        {
            if (!IsTemporaryDayFile(Path.GetFileName(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                tell(TemporaryNotDeleted, ex);
            }
        }

        return failures;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done here. The next scrub tries again.
        }
    }
}

/// <summary>
/// What a full scrub could not make good, for a caller that wants to try again where it failed:
/// the days that had a failure, and whether anything failed that belongs to no day (the folder
/// could not be listed, or a temporary file beside the day files could not be deleted).
/// </summary>
internal sealed class ScrubOutcome
{
    /// <summary>The days that had a failure.</summary>
    public SortedSet<DateOnly> Days { get; } = [];

    /// <summary>True when something failed that belongs to no day.</summary>
    public bool Folder { get; private set; }

    public void DayFailed(DateOnly day) => Days.Add(day);

    public void FolderFailed() => Folder = true;
}
