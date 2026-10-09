using System.Text;

namespace Beacon.Core.ClipboardHistory;

/// <summary>One line of a day's file as it was read.</summary>
/// <param name="Number">Its place in the file, counting from 1.</param>
/// <param name="Raw">The line as written, without its line break.</param>
/// <param name="Record">What it says, or null when it is blank or cannot be read.</param>
public sealed record ClipFileLine(int Number, string Raw, ClipRecord? Record)
{
    /// <summary>True for a line with nothing on it, which is neither a record nor a mistake.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Raw);

    /// <summary>The type's name and the line's number, and not the line, which is what the owner copied.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(ClipFileLine)} {{ Number = {Number} }}");
}

/// <summary>What reading every machine's files gave.</summary>
/// <param name="Entries">The history: every entry that is not deleted or cleared, newest first.</param>
/// <param name="Deleted">The id of every entry that a delete record names, on whichever machine.</param>
/// <param name="ClearedBefore">The latest time any clear record gives, or null when there is none. Entries older than it are cleared.</param>
/// <param name="NewestRecord">For each machine's folder, the time of its newest record of any kind.</param>
/// <param name="DayFiles">How many day files were read.</param>
/// <param name="UnreadableFiles">How many day files could not be opened and were left out.</param>
/// <param name="BadLines">The numbers of the first lines that could not be read, at most <see cref="ClipReader.MaxBadLinesListed"/>.</param>
/// <param name="BadLineCount">How many lines could not be read in all.</param>
public sealed record ClipLoad(
    IReadOnlyList<ClipEntry> Entries,
    IReadOnlySet<string> Deleted,
    DateTimeOffset? ClearedBefore,
    IReadOnlyDictionary<string, DateTimeOffset> NewestRecord,
    int DayFiles,
    int UnreadableFiles,
    IReadOnlyList<int> BadLines,
    int BadLineCount);

/// <summary>
/// Reads the clipboard history from disk: every machine's folder under the clipboard folder,
/// and in each the file for every day. Nothing is written here.
/// </summary>
public static class ClipReader
{
    /// <summary>How many line numbers are handed back one by one. A file of nonsense would otherwise fill the log.</summary>
    public const int MaxBadLinesListed = 20;

    /// <summary>
    /// The lines of one day's file, in order. The file is read as UTF-8, and opened so that
    /// another program reading or writing it at the same moment (a sync tool, an editor) is
    /// not shut out and does not shut this out. Each line is read whole, so a file larger than
    /// <see cref="ClipLimits.MaxDayFileBytes"/> is not read at all: its length is looked at as
    /// soon as it is open, before any line, and it counts as a file that cannot be read.
    /// </summary>
    /// <exception cref="IOException">The file could not be opened or read, or is larger than <see cref="ClipLimits.MaxDayFileBytes"/>. The message of the second says nothing about which file it is.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    public static IEnumerable<ClipFileLine> ReadLines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > ClipLimits.MaxDayFileBytes)
        {
            throw new IOException("A day of clipboard history is too large to read");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        int number = 0;
        while (reader.ReadLine() is { } raw)
        {
            number++;
            yield return new ClipFileLine(number, raw, string.IsNullOrWhiteSpace(raw) ? null : ClipLine.Parse(raw));
        }
    }

    /// <summary>The machine folders under the clipboard folder, in a fixed order. None when the folder is not there.</summary>
    public static IReadOnlyList<string> DeviceFolders(string clipboardDir)
    {
        try
        {
            return Directory.Exists(clipboardDir)
                ? Directory.GetDirectories(clipboardDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The day files in one machine's folder, oldest first, each with its day. Any other file is left out.</summary>
    public static IReadOnlyList<(DateOnly Day, string File)> DayFiles(string deviceDir)
    {
        try
        {
            var found = new List<(DateOnly Day, string File)>();
            foreach (string file in Directory.GetFiles(deviceDir))
            {
                if (ClipPaths.TryParseDayFile(Path.GetFileName(file), out var day))
                {
                    found.Add((day, file));
                }
            }

            found.Sort((a, b) => a.Day.CompareTo(b.Day));
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads everything and merges it: the entries of every machine, without those a delete
    /// record names and without those older than the latest time a clear record gives, newest
    /// first. A line that cannot be read is skipped and counted; a file that cannot be opened or
    /// is larger than <see cref="ClipLimits.MaxDayFileBytes"/> is skipped and counted, and
    /// <paramref name="report"/> is told with fixed words. An id that appears twice counts once,
    /// the first time it is met.
    /// </summary>
    public static ClipLoad Load(string clipboardDir, Action<string, Exception?>? report = null)
    {
        var entries = new List<ClipEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var newest = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var badLines = new List<int>();
        DateTimeOffset? clearedBefore = null;
        int dayFiles = 0;
        int unreadable = 0;
        int badLineCount = 0;

        foreach (string deviceDir in DeviceFolders(clipboardDir))
        {
            string device = Path.GetFileName(deviceDir);
            foreach (var (day, file) in DayFiles(deviceDir))
            {
                string folder = Path.Combine(deviceDir, ClipPaths.DayName(day));
                try
                {
                    foreach (var line in ReadLines(file))
                    {
                        if (line.IsBlank)
                        {
                            continue;
                        }

                        if (line.Record is not { } record)
                        {
                            badLineCount++;
                            if (badLines.Count < MaxBadLinesListed)
                            {
                                badLines.Add(line.Number);
                            }

                            continue;
                        }

                        if (!newest.TryGetValue(device, out var latest) || record.Time > latest)
                        {
                            newest[device] = record.Time;
                        }

                        switch (record)
                        {
                            case DeleteRecord delete:
                                deleted.Add(delete.Target);
                                break;
                            case ClearRecord clear:
                                if (clearedBefore is null || clear.Before > clearedBefore)
                                {
                                    clearedBefore = clear.Before;
                                }

                                break;
                            case EntryRecord entry when seen.Add(entry.Id):
                                entries.Add(ClipEntry.From(entry, device, day, folder));
                                break;
                        }
                    }

                    dayFiles++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another program has the file locked, or it went away while it was being
                    // read. What was read of it so far is kept; the rest comes with the next load.
                    unreadable++;
                    report?.Invoke("A day of clipboard history could not be read", ex);
                }
            }
        }

        return new ClipLoad(Visible(entries, deleted, clearedBefore), deleted, clearedBefore, newest, dayFiles, unreadable, badLines, badLineCount);
    }

    /// <summary>True when a delete record names the entry, or a clear record gives a time later than the entry's.</summary>
    public static bool IsRemoved(string id, DateTimeOffset time, IReadOnlySet<string> deleted, DateTimeOffset? clearedBefore) =>
        deleted.Contains(id) || (clearedBefore is { } before && time < before);

    /// <summary>
    /// The entries that are not deleted or cleared, newest first. Two with the same time keep a
    /// fixed order: by machine, and on one machine the one written later comes first.
    /// </summary>
    public static IReadOnlyList<ClipEntry> Visible(IEnumerable<ClipEntry> entries, IReadOnlySet<string> deleted, DateTimeOffset? clearedBefore) =>
        entries
            .Where(entry => !IsRemoved(entry.Id, entry.Time, deleted, clearedBefore))
            .Select((entry, index) => (entry, index))
            .OrderByDescending(pair => pair.entry.Time)
            .ThenBy(pair => pair.entry.Device, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(pair => pair.index)
            .Select(pair => pair.entry)
            .ToList();
}
