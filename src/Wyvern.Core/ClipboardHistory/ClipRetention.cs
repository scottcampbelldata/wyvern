namespace Wyvern.Core.ClipboardHistory;

/// <summary>
/// Removes clipboard history that is older than the owner keeps it. A day's records and its
/// content go together, and the day is the one in the path: no file is opened to decide.
/// Every machine deletes its own copies of expired days, whichever machine wrote them, so
/// nothing waits on the machine that owns a day being switched on.
/// </summary>
/// <remarks>
/// A folder that is a symbolic link or a junction is never listed, emptied or removed: what is
/// behind it is not ours. A machine folder that is one is passed over in silence (the store says
/// so when it is opened); an expired day's folder that is one is told, once for the run, in fixed
/// words: what is in it is expired and stays.
/// </remarks>
public static class ClipRetention
{
    private const string NotRemoved = "Expired clipboard history could not be removed";
    private const string DayFolderIsALink = "A day's folder in the clipboard history is a link and is left alone";

    /// <summary>
    /// True when the day is more than <paramref name="retentionDays"/> days before today. Both are
    /// days in UTC. A number of days below 1 makes no sense and expires nothing, and a number too
    /// large for the calendar simply expires nothing either: neither throws.
    /// </summary>
    public static bool IsExpired(DateOnly day, DateOnly today, int retentionDays) =>
        retentionDays >= 1 && (long)today.DayNumber - day.DayNumber > retentionDays;

    /// <summary>
    /// Deletes every expired day file, and the content in every expired day folder, under every
    /// machine's folder. Only what this code would have written is touched: a file named for a
    /// day, and in a folder named for a day the files named by a hash and a known extension and
    /// the temporary files of such names. A day folder is removed when nothing is left in it.
    /// One file or folder that cannot be removed does not stop the others.
    /// </summary>
    /// <param name="report">Receives fixed words and an exception for the log, once for the run however many things could not be removed, and once for the run when an expired day's folder is a link. Never a name or a path.</param>
    /// <returns>How many day files and day folders were removed.</returns>
    public static int Run(string clipboardDir, int retentionDays, DateOnly today, Action<string, Exception?>? report = null)
    {
        int removed = 0;
        Exception? failure = null;
        bool linkSeen = false;
        foreach (string deviceDir in ClipReader.DeviceFolders(clipboardDir))
        {
            try
            {
                if (ClipScrub.IsLink(deviceDir))
                {
                    continue;
                }

                foreach (var (day, file) in ClipReader.DayFiles(deviceDir))
                {
                    if (!IsExpired(day, today, retentionDays))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failure ??= ex;
                    }
                }

                foreach (string folder in Directory.GetDirectories(deviceDir))
                {
                    if (ClipPaths.TryParseDayFolder(Path.GetFileName(folder), out var day)
                        && IsExpired(day, today, retentionDays)
                        && RemoveDayFolder(folder, ref failure, ref linkSeen))
                    {
                        removed++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One machine's folder that cannot be cleaned does not stop the others. What
                // is left is tried again tomorrow.
                failure ??= ex;
            }
        }

        if (failure is not null)
        {
            report?.Invoke(NotRemoved, failure);
        }

        if (linkSeen)
        {
            report?.Invoke(DayFolderIsALink, null);
        }

        return removed;
    }

    /// <returns>True when the folder is gone.</returns>
    private static bool RemoveDayFolder(string folder, ref Exception? failure, ref bool linkSeen)
    {
        try
        {
            if (ClipScrub.IsLink(folder))
            {
                linkSeen = true;
                return false;
            }

            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (!ClipPaths.IsBlobName(name) && !ClipScrub.IsTemporaryContent(name))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure ??= ex;
                }
            }

            if (Directory.EnumerateFileSystemEntries(folder).Any())
            {
                // Something that is not ours is in there, or a file that could not be deleted.
                // It stays, and so does its folder.
                return false;
            }

            Directory.Delete(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure ??= ex;
            return false;
        }
    }
}
