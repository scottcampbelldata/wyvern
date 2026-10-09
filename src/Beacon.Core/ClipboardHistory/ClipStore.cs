using System.Text;
using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>A picture file that was copied as a file, with its content, on its way into the history.</summary>
/// <param name="Name">The file name it has, without its folder.</param>
/// <param name="Extension">The extension to keep it under: lower case, with its dot, one of <see cref="ClipPaths.PictureExtensions"/>.</param>
/// <param name="Bytes">The file's bytes, unchanged.</param>
public sealed record DraftPicture(string Name, string Extension, byte[] Bytes)
{
    /// <summary>The type's name and how many bytes the picture has, and neither its name nor its bytes.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(DraftPicture)} {{ Bytes = {Bytes.Length} }}");
}

/// <summary>
/// Something that was just copied, on its way into the history: every form the clipboard
/// offered that is within its limit. The content here is the owner's: it is never logged.
/// </summary>
/// <param name="SourceApp">The program it was copied from, such as "chrome.exe", or null.</param>
public sealed record ClipDraft(string? SourceApp)
{
    /// <summary>The plain text, whole. Null when there is none.</summary>
    public string? Text { get; init; }

    /// <summary>The clipboard's HTML, byte for byte, or null.</summary>
    public byte[]? Html { get; init; }

    /// <summary>The clipboard's rich text, byte for byte, or null.</summary>
    public byte[]? Rtf { get; init; }

    /// <summary>The clipboard's picture as the bytes of a PNG file, or null.</summary>
    public byte[]? ImagePng { get; init; }

    /// <summary>The full paths of the files that were copied.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Those of the copied files that are pictures and whose content is to be kept.</summary>
    public IReadOnlyList<DraftPicture> Pictures { get; init; } = [];

    /// <summary>For a copied link, the words it had on the page, or null.</summary>
    public string? Label { get; init; }

    /// <summary>True when there is nothing to keep.</summary>
    public bool IsEmpty => Text is null && Html is null && Rtf is null && ImagePng is null && Files.Count == 0;

    /// <summary>The type's name and how many files and pictures the draft has, and nothing it holds.</summary>
    public override string ToString() =>
        FormattableString.Invariant($"{nameof(ClipDraft)} {{ Files = {Files.Count}, Pictures = {Pictures.Count} }}");
}

/// <summary>
/// The clipboard history of one data folder, for one machine: what is in memory, and this
/// machine's own files on disk. It reads every machine's files and writes only its own.
/// </summary>
/// <remarks>
/// <para>Every method does its work before it returns, disk included, and one lock makes them
/// take turns. So call them from a background thread, never from the UI thread;
/// <see cref="ClipHistoryService"/> does that. <see cref="Entries"/>, <see cref="IsWriter"/> and
/// <see cref="ScrubOwed"/> can be read from any thread and never wait.</para>
/// <para>Two copies of Beacon can be pointed at one data folder. Two writers appending to the
/// same day file, or one rewriting it while the other appends, would lose records. So a store
/// holds a lock file open in its machine's folder for as long as it lives, and a second store
/// on the same folder finds it held: that one reads the history and writes nothing. The same
/// goes for a machine folder that is a link: what is behind a link is not ours to write.</para>
/// <para>A machine can have had another name before: after "deviceName" was changed, or the PC
/// was renamed, the folder of the earlier name still holds what this machine copied. The store is
/// told those names, and such a folder stays this machine's to scrub: an entry in it that is
/// deleted or cleared leaves the disk like one of the current folder, and is not merely hidden.
/// Nothing is ever added to an earlier folder, and one that another copy of Beacon holds is left
/// alone for that time.</para>
/// <para>A delete or a clear is in force for every machine as soon as its record is written, and
/// the entry leaves <see cref="Entries"/> then. Taking the entry off this machine's disk is the
/// scrub, and a scrub can fail (another program has a file open, or the day's folder is a link).
/// The store then remembers which days failed, says so in <see cref="ScrubOwed"/>, and tries
/// those days again at the end of every write it makes, without pausing, until they have
/// succeeded: only those days, so that a write does not read every day file again. A load and a
/// clear scrub every day, and say in the log, once, when what was owed has left the disk.</para>
/// </remarks>
public sealed class ClipStore : IDisposable
{
    /// <summary>
    /// The file a store holds open to show that the folder is taken. Its name starts with ".~",
    /// which sync leaves out, and it is removed when it is let go of.
    /// </summary>
    public const string LockFileName = ".~lock";

    /// <summary>
    /// How much of the largest day file that can be read (<see cref="ClipLimits.MaxDayFileBytes"/>)
    /// is kept back for delete and clear records. An add is refused once the file would pass the
    /// limit less this, so that a day whose file is full can still be deleted from and cleared.
    /// </summary>
    public const long DeleteReserveBytes = 64 * 1024;

    private const string FolderIsALink = "This machine's clipboard folder is a link, so nothing is recorded into it";
    private const string FolderTaken = "Another copy of Beacon is using this machine's clipboard folder, so this copy does not write to it";
    private const string FolderNotPrepared = "This machine's clipboard folder could not be prepared, so clipboard history is not recorded";
    private const string DayFolderIsALink = "A day's folder in the clipboard history is a link, so the copy was not recorded";
    private const string EarlierFolderIsALink = "A folder of an earlier name of this machine is a link and is left alone";
    private const string EarlierFolderNotScrubbed = "A folder of an earlier name of this machine could not be taken for scrubbing, so anything deleted from it may stay there for now";
    private const string DayIsFull = "Today's clipboard history is full";
    private const string ClockSuspectLine = "Clipboard history is dated more than a day ahead of this machine's clock, so new copies may be removed";

    // The words of the exception that a delete or a clear that has no room left is refused with.
    // The service catches it, logs it by type and tells the owner; it never holds a name or a path.
    private const string DayIsFullError = "Today's clipboard history is full, so this change could not be written";

    // Windows' error for "the file is open in another program, in a way that does not allow this".
    private const int SharingViolation = unchecked((int)0x80070020);

    // The lock file is tried this many times, this long apart: a sharing violation can be
    // passing, such as a scanner that is looking at a file a power cut left behind.
    private const int LockAttempts = 3;
    private static readonly TimeSpan LockPause = TimeSpan.FromMilliseconds(50);

    // How far ahead of the clock the newest record of this machine on the disk may be, when the
    // history is read, and still be followed. Further than that, the record is from a clock that
    // was wrong, and is not chased.
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

    // How far ahead of the clock a clear's time, or this machine's newest record, has to be before
    // the clock is said to be suspect: more than a day. Only said, never acted on.
    private static readonly TimeSpan ClockSuspectAhead = TimeSpan.FromDays(1);

    // The pause of the scrub that is made at the end of a write while one is owed: none, so that
    // a file that is still held costs five quick tries and no sleep.
    private static readonly Action<TimeSpan> NoPause = static _ => { };

    private readonly string _clipboardDir;
    private readonly string _deviceDir;
    private readonly int _retentionDays;
    private readonly IClock _clock;
    private readonly Action<string, Exception?> _report;
    private readonly Func<string> _newId;
    private readonly Action<TimeSpan> _pause;
    private readonly Lock _gate = new();

    // The names of the folders this machine writes or wrote, and the folders of the earlier names.
    private readonly HashSet<string> _ownDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _earlierDirs = [];
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private FileStream? _lock;
    private List<ClipEntry> _entries = [];
    private IReadOnlyList<ClipEntry> _published = [];
    private DateTimeOffset? _clearedBefore;
    private DateTimeOffset? _assigned;
    private DateTimeOffset? _followed;
    private bool _scrubOwed;
    private readonly SortedSet<DateOnly> _owedDays = [];
    private bool _owedFolder;
    private DateOnly? _fullTold;
    private bool _hidForeign;
    private bool _clockSuspect;
    private int _badLinesTold;

    // A failure to scrub a folder of an earlier name is said once for each kind in the life of
    // the store: it is tried again at every load, and would be said again every time.
    private bool _earlierLinkTold;
    private bool _earlierNotScrubbedTold;

    /// <param name="clipboardDir">The clipboard folder of the data folder. It holds one folder for each machine.</param>
    /// <param name="device">This machine's folder name, already made safe as a folder name.</param>
    /// <param name="report">
    /// Receives progress and problems for the log: fixed words, counts and line numbers, and an
    /// exception that is logged by type only. Never content, a program's name or a path.
    /// </param>
    /// <param name="newId">Makes the id of a new record. Left out everywhere except in tests: a random GUID.</param>
    /// <param name="pause">What waits between two tries at the lock file and between two tries at replacing a day file. Left out everywhere except in tests: <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    /// <param name="earlierDevices">
    /// The folder names this machine wrote under before it had its present name
    /// (<see cref="ClipFolders"/>). A name that is not safe as one folder name is passed over.
    /// </param>
    public ClipStore(
        string clipboardDir,
        string device,
        int retentionDays,
        IClock clock,
        Action<string, Exception?>? report = null,
        Func<string>? newId = null,
        Action<TimeSpan>? pause = null,
        IReadOnlyList<string>? earlierDevices = null)
    {
        _clipboardDir = clipboardDir;
        _deviceDir = Path.Combine(clipboardDir, device);
        _retentionDays = retentionDays;
        _clock = clock;
        _report = report ?? ((_, _) => { });
        _newId = newId ?? (() => Guid.NewGuid().ToString());
        _pause = pause ?? (static length => Thread.Sleep(length));
        Device = device;
        _ownDevices.Add(device);
        foreach (string earlier in earlierDevices ?? [])
        {
            // A name from a file is joined to the clipboard folder, so only one that is a plain
            // folder name is taken.
            if (ClipFolders.IsFolderName(earlier) && _ownDevices.Add(earlier))
            {
                _earlierDirs.Add(Path.Combine(clipboardDir, earlier));
            }
        }

        _lock = TakeFolder();
    }

    /// <summary>This machine's folder name.</summary>
    public string Device { get; }

    /// <summary>
    /// True when this store may write. False when another copy of Beacon holds this machine's
    /// folder, when the folder is a link, when it could not be made, and after
    /// <see cref="Dispose"/>: the history can then be read, and nothing is added, deleted,
    /// cleared, scrubbed or expired. Never waits for the store's other work.
    /// </summary>
    public bool IsWriter => Volatile.Read(ref _lock) is not null;

    /// <summary>
    /// True while a scrub that failed has not been made good: something deleted or cleared may
    /// still be in a file on this machine's disk, although it has left <see cref="Entries"/>.
    /// It is set when a scrub the store runs answers with failures, and cleared when the scrubs
    /// of the days that failed, or a later full scrub, answer with none. It is never set for a store that is not the writer. Never
    /// waits for the store's other work.
    /// </summary>
    public bool ScrubOwed => Volatile.Read(ref _scrubOwed);

    /// <summary>
    /// True when the last <see cref="Delete"/> or <see cref="Clear"/> took an entry of a folder
    /// that this machine does not own out of the history: its files are not this machine's to
    /// rewrite, so they stay on this disk until the machine that owns them removes them or they
    /// expire. False after any <see cref="Delete"/> or <see cref="Clear"/> that did not do that,
    /// and before the first. Never waits for the store's other work.
    /// </summary>
    public bool LastChangeHidForeignEntries => Volatile.Read(ref _hidForeign);

    /// <summary>
    /// True when the last <see cref="Load"/> found that the latest clear's time, or this machine's
    /// newest record, is more than a day ahead of the clock: written while a clock was wrong.
    /// A clear that far ahead removes what is copied next, at each scrub, and a record that far
    /// ahead is not followed (see <see cref="Load"/>). Nothing about how such records are treated
    /// changes; this only says that they are there, so that the owner can be told. Never waits
    /// for the store's other work.
    /// </summary>
    public bool ClockSuspect => Volatile.Read(ref _clockSuspect);

    /// <summary>The history as of the last change: newest first. Empty until <see cref="Load"/> has run. A list that was handed out never changes.</summary>
    public IReadOnlyList<ClipEntry> Entries => Volatile.Read(ref _published);

    /// <summary>
    /// Reads every machine's files again and puts the result in place of what was in memory.
    /// Before that, expired days are removed; after it, this machine's own files are scrubbed
    /// of entries that a delete or clear record in any machine's files removes. Run at
    /// startup, once a day, and whenever files may have arrived from another machine. What this
    /// store has deleted or cleared stays so, also when a file that says it could not be read
    /// this time.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            bool writer = IsWriter;
            int expired = writer ? ClipRetention.Run(_clipboardDir, _retentionDays, ClipPaths.DayOf(_clock.UtcNow), _report) : 0;
            var load = ClipReader.Load(_clipboardDir, _report);

            // A load adds to what is known to be deleted and cleared and never drops any of it:
            // a pass that could not read the file that holds a record must not bring back what
            // that record removed.
            _deleted.UnionWith(load.Deleted);
            _clearedBefore = Later(_clearedBefore, load.ClearedBefore);

            var scrub = ScrubResult.Nothing;
            if (writer)
            {
                scrub = ScrubEveryDay(_report, _pause);
            }

            _entries = [.. load.Entries.Where(entry => !ClipReader.IsRemoved(entry.Id, entry.Time, _deleted, _clearedBefore))];

            // The newest record of this machine on the disk is followed unless it is far ahead
            // of the clock, which is a record of a clock that was wrong. This is the only place
            // that limit is applied: a second the store handed out itself is always followed.
            _followed = load.NewestRecord.TryGetValue(Device, out var newest) && newest - Now() <= ClockSlack ? newest : null;
            Publish();

            // A clear or a record of this machine that is a day or more ahead of the clock is said
            // once, when it is first found, and again if it has been put right and goes wrong again.
            // Nothing else is done about it.
            var now = Now();
            bool suspect = (_clearedBefore is { } cleared && cleared - now > ClockSuspectAhead)
                || (load.NewestRecord.TryGetValue(Device, out var own) && own - now > ClockSuspectAhead);
            if (suspect && !_clockSuspect)
            {
                _report(ClockSuspectLine, null);
            }

            Volatile.Write(ref _clockSuspect, suspect);

            // Line numbers are said when the number of bad lines changes, not after every load:
            // the history is read again every day and after every sync.
            if (load.BadLineCount != _badLinesTold)
            {
                _badLinesTold = load.BadLineCount;
                foreach (int number in load.BadLines)
                {
                    _report($"A line of clipboard history could not be read (line {number})", null);
                }

                if (load.BadLineCount > load.BadLines.Count)
                {
                    _report($"{load.BadLineCount - load.BadLines.Count} more lines of clipboard history could not be read", null);
                }
            }

            _report(
                $"Clipboard history loaded: {_entries.Count} entries from {load.DayFiles} day files, {load.BadLineCount} lines skipped, "
                + $"{load.UnreadableFiles} files unreadable, {scrub.EntriesRemoved} entries and {scrub.FilesRemoved} files scrubbed, {expired} expired day files and folders removed",
                null);
        }
    }

    /// <summary>
    /// Adds what was just copied: writes its large content as files in today's folder, then
    /// appends its record to today's file. The record is appended last, so a record never names
    /// a file that is not there. Nothing is stored when the content is the same as that of the
    /// newest entry of this machine, which may be in the folder of an earlier name (see
    /// <see cref="ClipFolders"/>); when today's folder is a link and the copy needs a file in
    /// it; when today's file would grow past the largest that can be read less
    /// <see cref="DeleteReserveBytes"/>, which only deletes and clears may use; and for a picture that was copied
    /// as a file but could not be read back from its record (it is left out, and the rest of the
    /// copy is kept).
    /// </summary>
    /// <returns>The new entry, or null when nothing was stored.</returns>
    /// <exception cref="IOException">A file could not be written. Nothing was added.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be written. Nothing was added.</exception>
    public ClipEntry? Add(ClipDraft draft)
    {
        lock (_gate)
        {
            if (!IsWriter)
            {
                return null;
            }

            draft = WithoutPicturesThatCannotBeKept(draft);
            if (draft.IsEmpty)
            {
                return null;
            }

            bool owed = _scrubOwed;

            // The names of the files come from the content, so the entry can be compared with
            // the last one before anything is written.
            var files = new List<(string Name, byte[] Bytes)>();
            string? Named(byte[]? bytes, string extension)
            {
                if (bytes is null)
                {
                    return null;
                }

                string name = ClipPaths.BlobName(bytes, extension);
                files.Add((name, bytes));
                return name;
            }

            string? text = draft.Text is null ? null : ClipboardBytes.WellFormed(draft.Text);
            var content = new EntryRecord("", default, draft.SourceApp)
            {
                Text = text is null ? null : ClipEntry.Start(text),
                TextLength = text?.Length ?? 0,
                TextFile = text is { Length: > ClipEntry.MaxTextStart } ? Named(Encoding.UTF8.GetBytes(text), ClipPaths.TextExtension) : null,
                Html = Named(draft.Html, ClipPaths.HtmlExtension),
                Rtf = Named(draft.Rtf, ClipPaths.RtfExtension),
                Image = Named(draft.ImagePng, ClipPaths.ImageExtension),
                Files = draft.Files,
                Pictures = draft.Pictures.Select(picture => new KeptPicture(picture.Name, Named(picture.Bytes, picture.Extension)!)).ToList(),
                Label = draft.Label,
            };

            var newestOwn = _entries.FirstOrDefault(IsOwn);
            if (newestOwn is not null && newestOwn.SameContentAs(ClipEntry.From(content, Device, default, "")))
            {
                return null;
            }

            // Nothing is written, and no second is used up, when the copy is refused.
            var time = NextTime();
            var day = ClipPaths.DayOf(time);
            string folder = Path.Combine(_deviceDir, ClipPaths.DayName(day));
            if (files.Count > 0 && Directory.Exists(folder) && ClipScrub.IsLink(folder))
            {
                _report(DayFolderIsALink, null);
                return null;
            }

            var record = content with { Id = _newId(), Time = time };
            byte[] line = Line(record);
            if (!HasRoom(day, line.Length, forDeleteOrClear: false))
            {
                TellDayIsFull(day);
                return null;
            }

            _assigned = time;

            // The record is appended last, so that it never names a file that is not there. If
            // that cannot be done, the files that this call made are of no entry: they are deleted
            // again, and the ones that were there before stay, for an entry names them.
            var written = new List<string>();
            try
            {
                foreach (var (name, bytes) in files)
                {
                    if (WriteFile(folder, name, bytes))
                    {
                        written.Add(Path.Combine(folder, name));
                    }
                }

                Append(day, line);
            }
            catch
            {
                DeleteWritten(written);
                throw;
            }

            var entry = ClipEntry.From(record, Device, day, folder);
            int at = _entries.FindIndex(existing => ComesBefore(entry, existing));
            _entries.Insert(at < 0 ? _entries.Count : at, entry);
            Publish();
            ScrubIfOwed(owed);
            return entry;
        }
    }

    /// <summary>
    /// Deletes an entry of any machine: appends a delete record to this machine's file for
    /// today, takes the entry out of the history, and, when the entry is this machine's own,
    /// rewrites its day without it and deletes its files. An entry of another machine is taken
    /// off that machine's disk by that machine, when it sees the record. The entry is deleted
    /// for every machine once the record is written: when the rewrite or the deletion of a file
    /// fails, the answer is still true, and <see cref="ScrubOwed"/> says the disk has not
    /// caught up.
    /// </summary>
    /// <returns>False when this store may not write or the history has no such entry.</returns>
    /// <exception cref="IOException">The record could not be written, or today's file has no room left even in the part that is kept for deletes and clears (the words of the exception say so, and nothing else). Nothing was deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The record could not be written. Nothing was deleted.</exception>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            Volatile.Write(ref _hidForeign, false);
            var entry = _entries.FirstOrDefault(existing => existing.Id == id);
            if (!IsWriter || entry is null)
            {
                return false;
            }

            bool owed = _scrubOwed;
            var time = NextTime();
            var day = ClipPaths.DayOf(time);
            byte[] line = Line(new DeleteRecord(_newId(), time, id));
            if (!HasRoom(day, line.Length, forDeleteOrClear: true))
            {
                TellDayIsFull(day);
                throw new IOException(DayIsFullError);
            }

            _assigned = time;
            Append(day, line);
            _deleted.Add(id);
            _entries.Remove(entry);
            Publish();

            // An entry of a folder that is not this machine's is hidden, and its files stay: they
            // are not ours to remove.
            Volatile.Write(ref _hidForeign, !IsOwn(entry));
            if (IsCurrent(entry))
            {
                NoteDayScrub(entry.Day, ClipScrub.RunDay(_deviceDir, entry.Day, _deleted, _clearedBefore, _report, _pause));
            }
            else if (IsOwn(entry))
            {
                // An entry in a folder of an earlier name. A failure there belongs to no day of
                // the current folder, so it waits for the next full scrub like a folder failure.
                if (ScrubEarlier(Path.Combine(_clipboardDir, entry.Device), entry.Day, _report, _pause).Failures > 0)
                {
                    _owedFolder = true;
                    SetOwed(true, 0, 0);
                }
            }

            ScrubIfOwed(owed);
            return true;
        }
    }

    /// <summary>
    /// Clears the history on every machine: appends one clear record, whose time is later than
    /// every record this machine has written, takes every older entry out of the history, and
    /// scrubs this machine's own files. The one exception is a record that was stamped more
    /// than a minute ahead of the clock when the history was read, by a clock that was wrong:
    /// its time is not followed, so the clear does not reach it and it has to be deleted by
    /// itself. When the scrub fails, the answer is still true, and <see cref="ScrubOwed"/> says
    /// the disk has not caught up.
    /// </summary>
    /// <returns>False when this store may not write.</returns>
    /// <exception cref="IOException">The record could not be written, or today's file has no room left even in the part that is kept for deletes and clears (the words of the exception say so, and nothing else). Nothing was cleared.</exception>
    /// <exception cref="UnauthorizedAccessException">The record could not be written. Nothing was cleared.</exception>
    public bool Clear()
    {
        lock (_gate)
        {
            Volatile.Write(ref _hidForeign, false);
            if (!IsWriter)
            {
                return false;
            }

            var time = NextTime();
            var day = ClipPaths.DayOf(time);
            byte[] line = Line(new ClearRecord(_newId(), time, time));
            if (!HasRoom(day, line.Length, forDeleteOrClear: true))
            {
                TellDayIsFull(day);
                throw new IOException(DayIsFullError);
            }

            _assigned = time;
            Append(day, line);
            if (_clearedBefore is null || time > _clearedBefore)
            {
                _clearedBefore = time;
            }

            // Entries of folders that are not this machine's are hidden, and their files stay.
            Volatile.Write(ref _hidForeign, _entries.Any(entry => entry.Time < _clearedBefore && !IsOwn(entry)));
            _entries.RemoveAll(entry => entry.Time < _clearedBefore);
            Publish();

            // A scrub of every day, which is also the one that an owed scrub would be made good by.
            ScrubEveryDay(_report, _pause);
            return true;
        }
    }

    /// <summary>Lets go of this machine's folder, so that another store can take it. The store writes nothing afterwards.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            var held = Interlocked.Exchange(ref _lock, null);

            // A store that does not write owes nothing: the next one on this folder scrubs when it loads.
            Volatile.Write(ref _scrubOwed, false);
            _owedDays.Clear();
            _owedFolder = false;
            try
            {
                held?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The lock file could not be removed. It is not held any more, which is what counts.
            }
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    // True for an entry in a folder this machine writes or wrote: the folder of its present
    // name, and those of the names it had before. This is also what Add looks at for "the newest
    // entry of this machine": after a rename, a copy that is identical to the newest entry of an
    // earlier folder is not stored again, as it would not have been had the name not changed.
    private bool IsOwn(ClipEntry entry) => _ownDevices.Contains(entry.Device);

    // True for an entry in the folder of this machine's present name, which is the one new
    // records go into and the one whose days are remembered when a scrub of them fails.
    private bool IsCurrent(ClipEntry entry) => string.Equals(entry.Device, Device, StringComparison.OrdinalIgnoreCase);

    // Scrubs a folder of an earlier name: one day of it, or every day when none is given. While
    // the scrub runs the folder's lock file is held, as it is for the current folder, so that a
    // copy of Beacon that has that name now is never rewritten under. When another copy holds
    // it, nothing is done and that counts as one failure, which is tried again like any other.
    // A folder that is a link, and a folder that could not be taken, are each said once in the
    // life of the store, in fixed words that name no folder.
    private ScrubResult ScrubEarlier(string folder, DateOnly? day, Action<string, Exception?>? report, Action<TimeSpan> pause)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return ScrubResult.Nothing;
            }

            if (ClipScrub.IsLink(folder))
            {
                TellOnce(ref _earlierLinkTold, EarlierFolderIsALink, null, report);
                return new ScrubResult(0, 0, 0, 1);
            }

            using var held = new FileStream(
                Path.Combine(folder, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return day is { } one
                ? ClipScrub.RunDay(folder, one, _deleted, _clearedBefore, report, pause)
                : ClipScrub.Run(folder, _deleted, _clearedBefore, report, pause);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TellOnce(ref _earlierNotScrubbedTold, EarlierFolderNotScrubbed, ex, report);
            return new ScrubResult(0, 0, 0, 1);
        }
    }

    // Says a failure of ScrubEarlier the first time the store meets that kind, and never again.
    private static void TellOnce(ref bool told, string text, Exception? cause, Action<string, Exception?>? report)
    {
        if (!told && report is not null)
        {
            told = true;
            report(text, cause);
        }
    }

    // The order of the history: newer first; at the same time by machine, and the one added later first.
    private static bool ComesBefore(ClipEntry added, ClipEntry existing) =>
        added.Time > existing.Time
        || (added.Time == existing.Time && string.Compare(added.Device, existing.Device, StringComparison.OrdinalIgnoreCase) <= 0);

    private void Publish() => Volatile.Write(ref _published, _entries.ToArray());

    /// <returns>The open lock file, or null when this store may not write.</returns>
    private FileStream? TakeFolder()
    {
        try
        {
            Directory.CreateDirectory(_deviceDir);
            if (ClipScrub.IsLink(_deviceDir))
            {
                // Scrub and retention leave a folder that is a link alone, so a store that wrote
                // through one could never take its records off the disk again. Nothing is made
                // behind the link, not even the lock file.
                _report(FolderIsALink, null);
                return null;
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return new FileStream(
                        Path.Combine(_deviceDir, LockFileName),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.DeleteOnClose);
                }
                catch (IOException ex) when (ex.HResult == SharingViolation)
                {
                    // Only this says "taken". Any other failure is a folder that cannot be
                    // written, and is said as that. A copy of Beacon that holds the file holds
                    // it for good; a program that is only looking at it lets go within moments.
                    if (attempt == LockAttempts)
                    {
                        _report(FolderTaken, null);
                        return null;
                    }

                    _pause(LockPause);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _report(FolderNotPrepared, ex);
            return null;
        }
    }

    private DateTimeOffset Now()
    {
        var utc = _clock.UtcNow.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    // The time for a new record: now, in whole seconds, and always later than the last second
    // this store handed out and than the newest record of this machine that the last load
    // followed. A machine never writes two records with the same time, so that the order of a
    // quick run of copies is kept, and so that a clear, whose time is its own "before", is later
    // than everything this machine wrote earlier. However far ahead of the clock a run of quick
    // copies has taken the seconds, the next one is the one after: only the value a load takes
    // from the disk is held to the limit of how far ahead of the clock it may be. The caller
    // keeps the time in _assigned once the copy is not refused.
    private DateTimeOffset NextTime()
    {
        var now = Now();
        return Later(_assigned, _followed) is { } last && now <= last ? last.AddSeconds(1) : now;
    }

    // A picture that was copied as a file is written under its extension, and its record has to
    // be read back. The reader refuses a record whose picture has an empty name or a file that
    // is not a hash and one of the picture extensions, and that loses the whole entry at the
    // next start and leaves its content for no scrub to name. The extension is also joined into
    // a path. So such a picture is left out before anything is written; the rest is kept.
    private ClipDraft WithoutPicturesThatCannotBeKept(ClipDraft draft)
    {
        if (draft.Pictures.Count == 0)
        {
            return draft;
        }

        var kept = draft.Pictures
            .Where(picture => !string.IsNullOrEmpty(picture.Name) && ClipPaths.PictureExtensions.Contains(picture.Extension))
            .ToList();
        int left = draft.Pictures.Count - kept.Count;
        if (left == 0)
        {
            return draft;
        }

        _report($"Pictures of a copy that could not be recorded were left out: {left}", null);
        return draft with { Pictures = kept };
    }

    private static byte[] Line(ClipRecord record) => Encoding.UTF8.GetBytes(ClipLine.Write(record) + "\n");

    // A day file larger than ClipLimits.MaxDayFileBytes is not read, and so not scrubbed or
    // shown, ever again. A line is not appended when the file would then be larger: the one more
    // byte is the line break that may have to come first. The last DeleteReserveBytes of that
    // are for the records of a delete and of a clear: an entry that is added is refused sooner,
    // so that a day that has filled up can still be deleted from and cleared.
    private bool HasRoom(DateOnly day, int lineLength, bool forDeleteOrClear)
    {
        var file = new FileInfo(Path.Combine(_deviceDir, ClipPaths.DayFileName(day)));
        long limit = forDeleteOrClear ? ClipLimits.MaxDayFileBytes : ClipLimits.MaxDayFileBytes - DeleteReserveBytes;
        return (file.Exists ? file.Length : 0) + 1 + lineLength <= limit;
    }

    // "Today is full" is said once for each day, and not by every call that is refused: the day
    // goes on being full, and every copy would say it again.
    private void TellDayIsFull(DateOnly day)
    {
        if (_fullTold != day)
        {
            _fullTold = day;
            _report(DayIsFull, null);
        }
    }

    // What the scrub of one day, which a delete has just made, means for what is owed. A day that
    // failed is owed; a day that is owed and went well is not any more.
    private void NoteDayScrub(DateOnly day, ScrubResult result)
    {
        if (result.Failures > 0)
        {
            _owedDays.Add(day);
            SetOwed(true, 0, 0);
        }
        else if (_owedDays.Remove(day))
        {
            SetOwed(_owedDays.Count > 0 || _owedFolder, result.EntriesRemoved, result.FilesRemoved);
        }
    }

    // A scrub of every day, and what it could not make good: which days, and whether something
    // that belongs to no day. For a load and a clear.
    private ScrubResult ScrubEveryDay(Action<string, Exception?>? report, Action<TimeSpan> pause)
    {
        var outcome = new ScrubOutcome();
        var result = ClipScrub.Run(_deviceDir, _deleted, _clearedBefore, report, pause, outcome);
        _owedDays.Clear();
        _owedDays.UnionWith(outcome.Days);
        _owedFolder = outcome.Folder;

        // The folders of this machine's earlier names are scrubbed with it. What fails there
        // belongs to no day of the current folder, so it is owed as a folder failure is.
        foreach (string earlier in _earlierDirs)
        {
            var scrubbed = ScrubEarlier(earlier, day: null, report, pause);
            if (scrubbed.Failures > 0)
            {
                _owedFolder = true;
            }

            result = result.Plus(scrubbed);
        }

        SetOwed(result.Failures > 0, result.EntriesRemoved, result.FilesRemoved);
        return result;
    }

    // While a scrub is owed, a write ends with one more scrub of each day that is owed, and of no
    // other: a scrub of every day reads every day file again, and the other days failed nothing.
    // It does not pause, so a file that is still held costs five quick tries and no sleep, and it
    // does not report: the failure was told when it happened, and would be told again by every
    // copy until the file is let go. What belongs to no day (a leftover temporary file beside the
    // day files, a folder that could not be listed) waits for the next load, clear or retry.
    private void ScrubIfOwed(bool owed)
    {
        if (!owed)
        {
            return;
        }

        int entries = 0;
        int files = 0;
        foreach (var day in _owedDays.ToArray())
        {
            var result = ClipScrub.RunDay(_deviceDir, day, _deleted, _clearedBefore, null, NoPause);
            if (result.Failures == 0)
            {
                _owedDays.Remove(day);
                entries += result.EntriesRemoved;
                files += result.FilesRemoved;
            }
        }

        SetOwed(_owedDays.Count > 0 || _owedFolder, entries, files);
    }

    // Sets whether a scrub is owed. When what was owed has left the disk, one line says so, with
    // what the scrub that made it good removed.
    private void SetOwed(bool owed, int entries, int files)
    {
        bool was = _scrubOwed;
        Volatile.Write(ref _scrubOwed, owed);
        if (was && !owed)
        {
            _report($"A scrub that was owed is made good: {entries} entries and {files} files removed", null);
        }
    }

    // A file of content, named by the hash of its bytes: one that is there already holds the
    // same bytes. It is written under a temporary name and renamed, so a name that exists is
    // always a whole file.
    // True when this call made the file, false when it was there already.
    private static bool WriteFile(string folder, string name, byte[] bytes)
    {
        string file = Path.Combine(folder, name);
        if (File.Exists(file))
        {
            return false;
        }

        Directory.CreateDirectory(folder);
        string temporary = file + ClipScrub.TemporaryExtension;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next scrub removes it. The failure that matters is the one being passed on.
            }

            throw;
        }
    }

    // Deletes files of content that one add made and then found that its record could not be
    // written: nothing names them, and nobody could see or delete them until the next scrub.
    // A file that cannot be deleted now is left for that scrub.
    private static void DeleteWritten(List<string> written)
    {
        foreach (string file in written)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next full scrub removes it.
            }
        }
    }

    // Adds one line to the end of a day's file. The file is never written again from the
    // start. If its last line was cut short (the power went while it was being written), the
    // new record starts on a line of its own, so that it is not lost along with that one.
    private void Append(DateOnly day, byte[] line)
    {
        Directory.CreateDirectory(_deviceDir);
        using var stream = new FileStream(
            Path.Combine(_deviceDir, ClipPaths.DayFileName(day)),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }
}
