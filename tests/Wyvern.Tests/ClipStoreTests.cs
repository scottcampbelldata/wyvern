using System.Diagnostics;
using System.Text;
using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Tests;

public sealed class ClipStoreTests : IDisposable
{
    // Made-up machines, so that no test depends on the name of the one it runs on.
    private const string Machine = "OTTER-PC";
    private const string OtherMachine = "WALRUS-PC";

    private const int RetentionDays = 90;
    private const string Garbage = "{ this is not a record";
    private const string Taken = "Another copy of Wyvern is using this machine's clipboard folder, so this copy does not write to it";
    private const string NotPrepared = "This machine's clipboard folder could not be prepared, so clipboard history is not recorded";
    private const string DayNotRead = "A day of clipboard history could not be read";
    private const string DayNotRewritten = "A day of clipboard history could not be rewritten";
    private const string LoadedStart = "Clipboard history loaded:";
    private const string FolderIsALink = "This machine's clipboard folder is a link, so nothing is recorded into it";
    private const string DayFolderIsALink = "A day's folder in the clipboard history is a link, so the copy was not recorded";
    private const string Full = "Today's clipboard history is full";

    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateOnly Yesterday = new(2026, 10, 2);
    private static readonly DateOnly Tomorrow = new(2026, 10, 4);

    // Where the fake clock starts.
    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // Older than anything a test does, so that a file that was written again shows.
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // The longest a test waits for another thread. It is only ever reached when a test fails.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly List<ClipStore> _stores = [];
    private readonly List<string> _reports = [];
    private readonly List<string> _links = [];
    private readonly List<TimeSpan> _pauses = [];
    private int _ids;

    public void Dispose()
    {
        // A store holds its lock file open, so every store is let go of before the folder is deleted.
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        // A recursive delete must never meet a link: the links go first, and only the links.
        foreach (string link in _links)
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }

        _dir.Dispose();
    }

    private string ClipboardDir => _dir.File("clipboard");

    private string DeviceDir(string machine = Machine) => Path.Combine(ClipboardDir, machine);

    private string DayFile(DateOnly day, string machine = Machine) => Path.Combine(DeviceDir(machine), ClipPaths.DayFileName(day));

    private string DayFolder(DateOnly day, string machine = Machine) => Path.Combine(DeviceDir(machine), ClipPaths.DayName(day));

    /// <summary>
    /// A store that has not read anything yet. Its ids count up from "id-1", over all the stores
    /// of one test. It never sleeps: what it would wait for is written to <see cref="_pauses"/>.
    /// </summary>
    private ClipStore Open(string machine = Machine)
    {
        var store = new ClipStore(ClipboardDir, machine, RetentionDays, _clock, (text, _) => _reports.Add(text), () => $"id-{++_ids}", Pause);
        _stores.Add(store);
        return store;
    }

    /// <summary>Stands in for sleeping: no test waits.</summary>
    private void Pause(TimeSpan length) => _pauses.Add(length);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private ClipStore Loaded(string machine = Machine)
    {
        var store = Open(machine);
        store.Load();
        return store;
    }

    /// <summary>What a restart does: the store is let go of, and a new one reads the folder.</summary>
    private ClipStore Reopened(ClipStore store)
    {
        store.Dispose();
        return Loaded(store.Device);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static ClipDraft Copied(string text, string? sourceApp = "notepad.exe") => new(sourceApp) { Text = text };

    /// <summary>A copy that has every form at once. Each call makes new arrays that hold the same bytes.</summary>
    private static ClipDraft EveryForm() => new("notepad.exe")
    {
        Text = "walrus",
        // Not all of it is text: what is stored has to be these bytes and not a reading of them.
        Html = [.. Bytes("<b>walrus</b> from example.org"), 0xFF, 0x00, 0x0D, 0x0A, 0x80],
        Rtf = Bytes(@"{\rtf1 walrus}"),
        ImagePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3],
        Files = [@"C:\made-up\otter.jpg", @"C:\made-up\notes.txt"],
        Pictures = [new DraftPicture("otter.jpg", ".jpg", [0xFF, 0xD8, 0xFF, 4, 5, 6])],
    };

    /// <summary>The record that <see cref="EveryForm"/> has to become.</summary>
    private static EntryRecord EveryFormRecord(string id, DateTimeOffset time)
    {
        var draft = EveryForm();
        return new EntryRecord(id, time, "notepad.exe")
        {
            Text = "walrus",
            TextLength = 6,
            Html = ClipPaths.BlobName(draft.Html, ClipPaths.HtmlExtension),
            Rtf = ClipPaths.BlobName(draft.Rtf, ClipPaths.RtfExtension),
            Image = ClipPaths.BlobName(draft.ImagePng, ClipPaths.ImageExtension),
            Files = draft.Files,
            Pictures = [new KeptPicture("otter.jpg", ClipPaths.BlobName(draft.Pictures[0].Bytes, ".jpg"))],
        };
    }

    private static string TextLine(string id, DateTimeOffset time, string text, string? sourceApp = null) =>
        ClipLine.Write(new EntryRecord(id, time, sourceApp) { Text = text, TextLength = text.Length });

    private static string HtmlLine(string id, DateTimeOffset time, string html) =>
        ClipLine.Write(new EntryRecord(id, time, null) { Html = html });

    private static string DeleteLine(string id, DateTimeOffset time, string target) =>
        ClipLine.Write(new DeleteRecord(id, time, target));

    private static string ClearLine(string id, DateTimeOffset time) =>
        ClipLine.Write(new ClearRecord(id, time, time));

    private static string BadLine(int number) => $"A line of clipboard history could not be read (line {number})";

    /// <summary>Writes a day file by hand, as another machine or an earlier run left it.</summary>
    private string WriteDay(string machine, DateOnly day, params string[] lines)
    {
        Directory.CreateDirectory(DeviceDir(machine));
        string file = DayFile(day, machine);
        File.WriteAllText(file, string.Concat(lines.Select(line => line + "\n")));
        return file;
    }

    /// <summary>Puts a file of content into a day's folder under the name the store would give it.</summary>
    /// <returns>The file's name.</returns>
    private string Content(string machine, DateOnly day, string content, string extension = ClipPaths.HtmlExtension)
    {
        byte[] bytes = Bytes(content);
        string name = ClipPaths.BlobName(bytes, extension);
        Directory.CreateDirectory(DayFolder(day, machine));
        File.WriteAllBytes(Path.Combine(DayFolder(day, machine), name), bytes);
        return name;
    }

    /// <summary>The lines of a day file. None when the file is not there.</summary>
    private IReadOnlyList<string> Lines(DateOnly day, string machine = Machine) =>
        File.Exists(DayFile(day, machine)) ? File.ReadAllLines(DayFile(day, machine)) : [];

    private static IEnumerable<string> Ids(IEnumerable<ClipEntry> entries) => entries.Select(entry => entry.Id);

    /// <summary>Holds a file open the way a program does that lets nobody else in.</summary>
    private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.None);

    /// <summary>Holds a file open the way a virus scanner or an indexer does: everyone else may read, write and delete, and nobody can replace it.</summary>
    private static FileStream HoldShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Makes a real junction. It needs no special privilege, and it is the one program a test of
    /// this class starts: cmd.exe from the Windows folder. The link and the target are both
    /// inside the test's own temporary folder, and the link is removed before the folder is.
    /// </summary>
    private void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        _links.Add(link);

        // The rule the code goes by. If this is null for a junction, the tests that use it prove nothing.
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    /// <summary>
    /// A folder that a link for a machine's folder can lead to, made directly and not through the
    /// link: it holds what a store would touch if it followed the link. A day file with an entry
    /// and the content that entry names, a day long expired, and a temporary file that a rewrite
    /// left. Everything has an old date, so that a file written again shows.
    /// </summary>
    private string MakeDeviceTarget(string name)
    {
        string target = Path.Combine(_dir.Path, name);
        string folder = Path.Combine(target, ClipPaths.DayName(Today));
        Directory.CreateDirectory(folder);
        byte[] bytes = Bytes("<b>walrus</b>");
        string html = ClipPaths.BlobName(bytes, ClipPaths.HtmlExtension);
        File.WriteAllBytes(Path.Combine(folder, html), bytes);
        File.WriteAllText(Path.Combine(target, ClipPaths.DayFileName(Today)), HtmlLine("o-1", Noon.AddHours(-1), html) + "\n");
        File.WriteAllText(Path.Combine(target, ClipPaths.DayFileName(new DateOnly(2026, 1, 1))), DeleteLine("o-0", new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), "elsewhere") + "\n");
        File.WriteAllText(Path.Combine(target, ClipPaths.DayFileName(Today) + ClipScrub.TemporaryExtension), "half written");
        MakeOld(target);
        return target;
    }

    /// <summary>A folder that a link for a day's folder can lead to: content that no record names, and a temporary file of content.</summary>
    private string MakeDayTarget(string name)
    {
        string target = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(target);
        byte[] bytes = Bytes("<b>left over</b>");
        string html = ClipPaths.BlobName(bytes, ClipPaths.HtmlExtension);
        File.WriteAllBytes(Path.Combine(target, html), bytes);
        File.WriteAllBytes(Path.Combine(target, html + ClipScrub.TemporaryExtension), bytes);
        MakeOld(target);
        return target;
    }

    /// <summary>
    /// A way to stop a store in the middle of a load. A load reports its summary last, while the
    /// store holds its lock, so a store whose report is this is held there until it is released.
    /// The wait has the limit of the tests, so a test that fails cannot hang.
    /// </summary>
    private sealed class LoadGate : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Report(string text, Exception? cause)
        {
            if (text.StartsWith(LoadedStart, StringComparison.Ordinal))
            {
                Entered.Set();
                Release.Wait(Limit);
            }
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    /// <summary>A store whose load can be held in its report. The ids and the pauses are those of <see cref="Open"/>.</summary>
    private ClipStore OpenWithGate(LoadGate gate)
    {
        var store = new ClipStore(ClipboardDir, Machine, RetentionDays, _clock, gate.Report, () => $"id-{++_ids}", Pause);
        _stores.Add(store);
        return store;
    }

    /// <summary>Gives every file under a folder an old date, so that one that is written again shows.</summary>
    private static void MakeOld(string folder)
    {
        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, LongAgo);
        }
    }

    /// <summary>
    /// Everything under a folder: each folder, and each file with its bytes and the time it was
    /// last written. A lock file is held open and cannot be read, so only its name is taken.
    /// </summary>
    private static SortedDictionary<string, string> Snapshot(string folder)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
        {
            return found;
        }

        foreach (string directory in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories))
        {
            found[Path.GetRelativePath(folder, directory)] = "a folder";
        }

        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            found[Path.GetRelativePath(folder, file)] = Path.GetFileName(file) == ClipStore.LockFileName
                ? "a lock file"
                : Convert.ToHexString(File.ReadAllBytes(file)) + " written " + File.GetLastWriteTimeUtc(file).Ticks;
        }

        return found;
    }

    [Fact]
    public void A_new_store_on_an_empty_folder_is_the_writer_and_has_no_entries()
    {
        var store = Open();

        Assert.True(store.IsWriter);
        Assert.Empty(store.Entries);
        Assert.Equal(Machine, store.Device);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_new_store_puts_a_lock_file_in_its_machines_folder()
    {
        Open();

        Assert.Equal(Path.Combine(DeviceDir(), ClipStore.LockFileName), Assert.Single(Directory.GetFileSystemEntries(DeviceDir())));
    }

    [Fact]
    public void The_lock_file_stays_through_the_stores_work_and_goes_when_the_store_is_disposed()
    {
        // A load and a clear both scrub the folder, and neither may take the lock file for
        // something left over.
        var store = Loaded();
        string lockFile = Path.Combine(DeviceDir(), ClipStore.LockFileName);
        store.Add(Copied("walrus"));
        store.Clear();
        store.Load();
        Assert.True(File.Exists(lockFile));
        Assert.True(store.IsWriter);

        store.Dispose();

        Assert.False(File.Exists(lockFile));
        Assert.False(store.IsWriter);
    }

    [Fact]
    public void The_lock_file_has_a_name_that_starts_with_a_dot_and_a_tilde()
    {
        Assert.StartsWith(".~", ClipStore.LockFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Disposing_a_store_twice_does_no_harm()
    {
        var store = Open();

        store.Dispose();
        store.Dispose();

        Assert.False(store.IsWriter);
        Assert.True(Open().IsWriter);
    }

    [Fact]
    public void A_lock_file_that_nobody_holds_does_not_shut_a_new_store_out()
    {
        // What a copy of Wyvern that was killed can leave behind.
        Directory.CreateDirectory(DeviceDir());
        File.WriteAllText(Path.Combine(DeviceDir(), ClipStore.LockFileName), "");

        Assert.True(Open().IsWriter);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_gives_a_store_that_reads_and_does_not_write()
    {
        // A file stands where the clipboard folder has to be.
        File.WriteAllText(ClipboardDir, "in the way");

        var store = Open();
        store.Load();

        Assert.False(store.IsWriter);
        Assert.Null(store.Add(Copied("walrus")));
        Assert.Empty(store.Entries);
        Assert.Equal(NotPrepared, _reports[0]);
    }

    [Fact]
    public void After_dispose_a_store_adds_deletes_and_clears_nothing()
    {
        var store = Loaded();
        var entry = store.Add(Copied("walrus"));
        Assert.NotNull(entry);
        store.Dispose();
        var before = Snapshot(ClipboardDir);

        Assert.Null(store.Add(Copied("otter")));
        Assert.False(store.Delete(entry.Id));
        Assert.False(store.Clear());

        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Same(entry, Assert.Single(store.Entries));
    }

    [Fact]
    public void After_dispose_a_store_still_reads_the_history()
    {
        var store = Loaded();
        store.Dispose();
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon, "walrus"));

        store.Load();

        Assert.Equal(["w-1"], Ids(store.Entries));
    }

    [Fact]
    public void A_second_store_on_the_same_folder_and_machine_is_not_the_writer()
    {
        var first = Open();

        var second = Open();

        Assert.True(first.IsWriter);
        Assert.False(second.IsWriter);
        Assert.Equal([Taken], _reports);
    }

    [Fact]
    public void A_store_that_is_not_the_writer_reads_what_the_writer_wrote()
    {
        var first = Loaded();
        first.Add(Copied("walrus"));
        first.Add(Copied("otter"));
        var second = Open();

        second.Load();

        Assert.Equal(["id-2", "id-1"], Ids(second.Entries));
        Assert.Equal(["otter", "walrus"], second.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_store_that_is_not_the_writer_adds_deletes_and_clears_nothing()
    {
        var first = Loaded();
        first.Add(Copied("walrus"));
        var second = Loaded();
        var before = Snapshot(ClipboardDir);

        Assert.Null(second.Add(Copied("otter")));
        Assert.False(second.Delete("id-1"));
        Assert.False(second.Clear());

        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Equal(["id-1"], Ids(second.Entries));
    }

    [Fact]
    public void A_store_that_is_not_the_writer_neither_expires_nor_scrubs_when_it_loads()
    {
        // An entry of this machine that the other machine deleted, and a day that expired long
        // ago: a writer would rewrite the one and remove the other.
        string html = Content(Machine, Today, "<b>walrus</b>");
        WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-1), html));
        WriteDay(Machine, new DateOnly(2026, 1, 1), DeleteLine("o-0", new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), "elsewhere"));
        WriteDay(OtherMachine, Today, DeleteLine("w-1", Noon, "o-1"));
        Open();
        var reader = Open();
        var before = Snapshot(ClipboardDir);

        reader.Load();

        Assert.False(reader.IsWriter);
        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Empty(reader.Entries);
        Assert.EndsWith("0 entries and 0 files scrubbed, 0 expired day files and folders removed", _reports[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void When_the_writer_is_disposed_a_new_store_is_the_writer_again()
    {
        var first = Open();
        var second = Open();

        first.Dispose();
        var third = Open();

        Assert.False(second.IsWriter);
        Assert.True(third.IsWriter);
        Assert.NotNull(third.Add(Copied("walrus")));
    }

    [Fact]
    public void Disposing_a_store_that_is_not_the_writer_leaves_the_writers_lock_alone()
    {
        var first = Open();
        var second = Open();

        second.Dispose();

        Assert.True(first.IsWriter);
        Assert.True(File.Exists(Path.Combine(DeviceDir(), ClipStore.LockFileName)));
        Assert.False(Open().IsWriter);
    }

    [Fact]
    public void A_store_for_another_machine_on_the_same_clipboard_folder_is_a_writer_too()
    {
        var first = Open();

        var other = Open(OtherMachine);

        Assert.True(first.IsWriter);
        Assert.True(other.IsWriter);
        Assert.True(File.Exists(Path.Combine(DeviceDir(OtherMachine), ClipStore.LockFileName)));
        Assert.Empty(_reports);
    }

    [Fact]
    public void Without_a_maker_of_ids_every_record_gets_a_guid_of_its_own()
    {
        var store = new ClipStore(ClipboardDir, Machine, RetentionDays, _clock);
        _stores.Add(store);

        var first = store.Add(Copied("walrus"));
        var second = store.Add(Copied("otter"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(Guid.TryParseExact(first.Id, "D", out _));
        Assert.True(Guid.TryParseExact(second.Id, "D", out _));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void A_store_that_nobody_listens_to_does_its_work_all_the_same()
    {
        // The load has a line to report, and nothing to report it to.
        WriteDay(Machine, Today, Garbage, TextLine("o-1", Noon.AddHours(-1), "walrus"));
        var store = new ClipStore(ClipboardDir, Machine, RetentionDays, _clock);
        _stores.Add(store);

        store.Load();

        Assert.Equal(["o-1"], Ids(store.Entries));
    }

    [Fact]
    public void The_history_can_be_read_from_another_thread_while_the_store_is_at_work()
    {
        // A report is made while the store holds its lock. A read that waited for that lock
        // could not come back before the report does, and here the report waits for the read.
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddHours(-1), "walrus"));
        var cameBack = new List<bool>();
        ClipStore? store = null;
        store = new ClipStore(
            ClipboardDir,
            Machine,
            RetentionDays,
            _clock,
            (_, _) => cameBack.Add(Task.Run(() => store!.Entries.Count).Wait(Limit)));
        _stores.Add(store);

        store.Load();

        Assert.Equal([true], cameBack);
    }

    [Fact]
    public void Short_text_becomes_one_line_in_the_day_file_of_the_clocks_day()
    {
        var store = Loaded();

        store.Add(Copied("walrus"));

        var expected = new EntryRecord("id-1", Noon, "notepad.exe") { Text = "walrus", TextLength = 6 };
        Assert.Equal(ClipLine.Write(expected) + "\n", File.ReadAllText(DayFile(Today)));
    }

    [Fact]
    public void Add_hands_back_the_new_entry()
    {
        var store = Loaded();

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal("id-1", entry.Id);
        Assert.Equal(Noon, entry.Time);
        Assert.Equal(Machine, entry.Device);
        Assert.Equal(Today, entry.Day);
        Assert.Equal(DayFolder(Today), entry.Folder);
        Assert.Equal("walrus", entry.Text);
        Assert.Equal(6, entry.TextLength);
        Assert.Null(entry.TextFile);
        Assert.Equal("notepad.exe", entry.SourceApp);
    }

    [Fact]
    public void An_added_entry_is_in_the_history()
    {
        var store = Loaded();

        var entry = store.Add(Copied("walrus"));

        Assert.Same(entry, Assert.Single(store.Entries));
    }

    [Fact]
    public void Short_text_makes_no_day_folder()
    {
        var store = Loaded();

        store.Add(Copied("walrus"));

        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Fact]
    public void A_record_gets_the_clocks_time_cut_to_whole_seconds()
    {
        _clock.UtcNow = Noon.AddMilliseconds(999);
        var store = Loaded();

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal(Noon, entry.Time);
        Assert.Contains("\"ts\":\"2026-10-03T12:00:00Z\"", File.ReadAllText(DayFile(Today)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_day_and_the_time_of_a_record_are_in_utc_whatever_offset_the_clock_gives()
    {
        // Half past one in the morning of the 4th, two hours east of UTC, is still the 3rd in UTC.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 4, 1, 30, 0, TimeSpan.FromHours(2));
        var store = Loaded();

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 23, 30, 0, TimeSpan.Zero), entry.Time);
        Assert.Equal(TimeSpan.Zero, entry.Time.Offset);
        Assert.Equal(Today, entry.Day);
        Assert.Single(Lines(Today));
        Assert.False(File.Exists(DayFile(Tomorrow)));
    }

    [Fact]
    public void Each_add_puts_one_line_after_the_last_with_no_blank_line_between()
    {
        var store = Loaded();

        store.Add(Copied("walrus"));
        store.Add(Copied("otter", sourceApp: null));
        store.Add(Copied("seal"));

        string expected =
            ClipLine.Write(new EntryRecord("id-1", Noon, "notepad.exe") { Text = "walrus", TextLength = 6 }) + "\n"
            + ClipLine.Write(new EntryRecord("id-2", Noon.AddSeconds(1), null) { Text = "otter", TextLength = 5 }) + "\n"
            + ClipLine.Write(new EntryRecord("id-3", Noon.AddSeconds(2), "notepad.exe") { Text = "seal", TextLength = 4 }) + "\n";
        Assert.Equal(expected, File.ReadAllText(DayFile(Today)));
    }

    [Fact]
    public void An_add_only_makes_the_day_file_longer()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        byte[] afterOne = File.ReadAllBytes(DayFile(Today));
        store.Add(Copied("otter"));
        byte[] afterTwo = File.ReadAllBytes(DayFile(Today));

        store.Add(Copied("seal"));

        byte[] afterThree = File.ReadAllBytes(DayFile(Today));
        Assert.True(afterOne.Length < afterTwo.Length);
        Assert.True(afterTwo.Length < afterThree.Length);
        Assert.Equal(afterOne, afterThree[..afterOne.Length]);
        Assert.Equal(afterTwo, afterThree[..afterTwo.Length]);
    }

    [Fact]
    public void An_add_leaves_everything_that_is_in_the_day_file_exactly_as_it_is()
    {
        // A line in a form the store would not write, line ends it does not use and blank
        // lines: writing the file again from its records or from its lines would change them.
        string before = """{ "kind": "text", "id": "o-1", "ts": "2026-10-03T11:00:00+00:00", "text": "otter" }""" + "\r\n\r\n   \r\n";
        Directory.CreateDirectory(DeviceDir());
        File.WriteAllText(DayFile(Today), before);
        var store = Loaded();

        store.Add(Copied("walrus"));

        var added = new EntryRecord("id-1", Noon, "notepad.exe") { Text = "walrus", TextLength = 6 };
        Assert.Equal(before + ClipLine.Write(added) + "\n", File.ReadAllText(DayFile(Today)));
        Assert.Equal(["id-1", "o-1"], Ids(store.Entries));
    }

    [Fact]
    public void A_record_after_a_line_that_was_cut_short_starts_on_a_line_of_its_own()
    {
        // What a power cut in the middle of a write leaves: half a record and no line break.
        const string cut = "{\"id\":\"o-1\",\"ts\":\"2026-10-03T11:0";
        Directory.CreateDirectory(DeviceDir());
        File.WriteAllText(DayFile(Today), cut);
        var store = Loaded();

        store.Add(Copied("walrus"));

        var added = new EntryRecord("id-1", Noon, "notepad.exe") { Text = "walrus", TextLength = 6 };
        Assert.Equal(cut + "\n" + ClipLine.Write(added) + "\n", File.ReadAllText(DayFile(Today)));
        Assert.Equal(["id-1"], Ids(ClipReader.Load(ClipboardDir).Entries));
    }

    [Fact]
    public void Three_adds_within_the_same_second_get_three_seconds_in_a_row()
    {
        var store = Loaded();

        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        store.Add(Copied("seal"));

        // Newest first.
        Assert.Equal(["id-3", "id-2", "id-1"], Ids(store.Entries));
        Assert.Equal([Noon.AddSeconds(2), Noon.AddSeconds(1), Noon], store.Entries.Select(entry => entry.Time));
    }

    [Fact]
    public void When_the_clock_has_moved_past_the_last_record_its_own_time_is_used_again()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        store.Add(Copied("seal"));
        _clock.Advance(TimeSpan.FromMinutes(10));

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal(Noon.AddMinutes(10), entry.Time);
    }

    [Fact]
    public void A_record_that_would_come_before_the_one_before_it_gets_the_next_second()
    {
        // Three quick copies took the seconds up to two past noon. The clock is one past.
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        store.Add(Copied("seal"));
        _clock.Advance(TimeSpan.FromSeconds(1));

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal(Noon.AddSeconds(3), entry.Time);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(60)]
    public void After_a_load_a_new_record_comes_one_second_after_this_machines_newest_on_disk_when_that_is_up_to_a_minute_ahead(int secondsAhead)
    {
        // The first row is a record of the very second the clock shows. The last is a whole
        // minute ahead, which is the furthest that is still followed.
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddSeconds(secondsAhead), "walrus"));
        var store = Loaded();

        var entry = store.Add(Copied("otter"));

        Assert.NotNull(entry);
        Assert.Equal(Noon.AddSeconds(secondsAhead + 1), entry.Time);
    }

    [Fact]
    public void After_a_load_a_new_record_gets_the_clocks_time_when_this_machines_newest_on_disk_is_behind_it()
    {
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddSeconds(-5), "walrus"));
        var store = Loaded();

        var entry = store.Add(Copied("otter"));

        Assert.NotNull(entry);
        Assert.Equal(Noon, entry.Time);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(3600)]
    public void A_newest_record_more_than_a_minute_ahead_of_the_clock_is_not_followed(int secondsAhead)
    {
        // It was written while the clock was wrong. The clock's own time is used.
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddSeconds(secondsAhead), "walrus"));
        var store = Loaded();

        var entry = store.Add(Copied("otter"));

        Assert.NotNull(entry);
        Assert.Equal(Noon, entry.Time);
    }

    [Fact]
    public void A_delete_record_counts_as_this_machines_newest_record_too()
    {
        WriteDay(Machine, Today, DeleteLine("o-1", Noon.AddSeconds(5), "elsewhere"));
        var store = Loaded();

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal(Noon.AddSeconds(6), entry.Time);
    }

    [Fact]
    public void Another_machines_newer_record_does_not_move_this_machines_time()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddSeconds(30), "walrus"));
        var store = Loaded();

        var entry = store.Add(Copied("otter"));

        Assert.NotNull(entry);
        Assert.Equal(Noon, entry.Time);
    }

    [Fact]
    public void With_the_clock_one_second_before_midnight_two_quick_adds_land_in_two_day_files()
    {
        _clock.UtcNow = new DateTimeOffset(2026, 10, 3, 23, 59, 59, TimeSpan.Zero);
        var store = Loaded();

        var first = store.Add(Copied("walrus"));
        var second = store.Add(Copied("otter"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), second.Time);
        Assert.Equal(Today, first.Day);
        Assert.Equal(Tomorrow, second.Day);
        Assert.Contains("\"id\":\"id-1\"", Assert.Single(Lines(Today)), StringComparison.Ordinal);
        Assert.Contains("\"id\":\"id-2\"", Assert.Single(Lines(Tomorrow)), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_goes_into_the_folder_of_the_records_own_day()
    {
        _clock.UtcNow = new DateTimeOffset(2026, 10, 3, 23, 59, 59, TimeSpan.Zero);
        var store = Loaded();

        var first = store.Add(new ClipDraft(null) { Html = Bytes("<b>walrus</b>") });
        var second = store.Add(new ClipDraft(null) { Html = Bytes("<b>otter</b>") });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(DayFolder(Tomorrow), second.Folder);
        Assert.Equal(Path.Combine(DayFolder(Today), first.Html!), Assert.Single(Directory.GetFiles(DayFolder(Today))));
        Assert.Equal(Path.Combine(DayFolder(Tomorrow), second.Html!), Assert.Single(Directory.GetFiles(DayFolder(Tomorrow))));
    }

    [Fact]
    public void Text_of_exactly_the_longest_start_stays_whole_in_its_record()
    {
        string text = new string('w', ClipEntry.MaxTextStart - 5) + "otter";
        var store = Loaded();

        var entry = store.Add(Copied(text));

        Assert.NotNull(entry);
        Assert.Equal(text, entry.Text);
        Assert.Null(entry.TextFile);
        var record = Assert.IsType<EntryRecord>(ClipLine.Parse(Assert.Single(Lines(Today))));
        Assert.Equal(text, record.Text);
        Assert.Null(record.TextFile);
        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Fact]
    public void Longer_text_is_whole_in_a_file_and_its_start_and_length_are_in_the_record()
    {
        // The end of the text is outside ASCII, so the file also shows that it is UTF-8.
        string start = new string('w', ClipEntry.MaxTextStart - 5) + "otter";
        string whole = start + "t" + (char)0xE9 + "il";
        var store = Loaded();

        store.Add(Copied(whole));

        byte[] bytes = Encoding.UTF8.GetBytes(whole);
        string name = ClipPaths.BlobName(bytes, ClipPaths.TextExtension);
        var expected = new EntryRecord("id-1", Noon, "notepad.exe") { Text = start, TextLength = ClipEntry.MaxTextStart + 4, TextFile = name };
        Assert.Equal(ClipLine.Write(expected) + "\n", File.ReadAllText(DayFile(Today)));

        // Exactly the bytes of the text: no byte order mark before them.
        Assert.Equal(Path.Combine(DayFolder(Today), name), Assert.Single(Directory.GetFiles(DayFolder(Today))));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(DayFolder(Today), name)));
    }

    [Fact]
    public void The_entry_of_a_longer_text_holds_its_start_its_whole_length_and_the_name_of_its_file()
    {
        string start = new string('w', ClipEntry.MaxTextStart - 5) + "otter";
        string whole = start + "tail";
        var store = Loaded();

        var entry = store.Add(Copied(whole));

        Assert.NotNull(entry);
        Assert.Equal(start, entry.Text);
        Assert.Equal(ClipEntry.MaxTextStart + 4, entry.TextLength);
        Assert.Equal(ClipPaths.BlobName(Encoding.UTF8.GetBytes(whole), ClipPaths.TextExtension), entry.TextFile);
        Assert.True(File.Exists(entry.PathOf(entry.TextFile!)));
    }

    [Fact]
    public void An_emoji_across_the_end_of_the_start_is_not_cut_in_half()
    {
        // The emoji is two UTF-16 characters, and only its first half would fit.
        string emoji = char.ConvertFromUtf32(0x1F9AD);
        string whole = new string('w', ClipEntry.MaxTextStart - 1) + emoji + "tail";
        var store = Loaded();

        var entry = store.Add(Copied(whole));

        Assert.NotNull(entry);
        Assert.Equal(new string('w', ClipEntry.MaxTextStart - 1), entry.Text);
        Assert.Equal(ClipEntry.MaxTextStart + 5, entry.TextLength);
        var record = Assert.IsType<EntryRecord>(ClipLine.Parse(Assert.Single(Lines(Today))));
        Assert.Equal(new string('w', ClipEntry.MaxTextStart - 1), record.Text);
        Assert.Equal(ClipEntry.MaxTextStart + 5, record.TextLength);
        Assert.Equal(Encoding.UTF8.GetBytes(whole), File.ReadAllBytes(entry.PathOf(entry.TextFile!)));
    }

    [Fact]
    public void Half_of_a_surrogate_pair_with_no_partner_is_stored_as_the_replacement_character()
    {
        string made = "wal" + (char)0xFFFD + "rus";
        var store = Loaded();

        var entry = store.Add(Copied("wal" + (char)0xD83D + "rus"));

        Assert.NotNull(entry);
        Assert.Equal(made, entry.Text);
        Assert.Equal(made, Assert.IsType<EntryRecord>(ClipLine.Parse(Assert.Single(Lines(Today)))).Text);
    }

    [Fact]
    public void Html_rich_text_a_picture_and_kept_pictures_each_become_a_file_named_by_its_hash_that_holds_exactly_the_bytes_given()
    {
        var draft = EveryForm();
        var store = Loaded();

        store.Add(draft);

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [ClipPaths.BlobName(draft.Html, ".html")] = Convert.ToHexString(draft.Html!),
            [ClipPaths.BlobName(draft.Rtf, ".rtf")] = Convert.ToHexString(draft.Rtf!),
            [ClipPaths.BlobName(draft.ImagePng, ".png")] = Convert.ToHexString(draft.ImagePng!),
            [ClipPaths.BlobName(draft.Pictures[0].Bytes, ".jpg")] = Convert.ToHexString(draft.Pictures[0].Bytes),
        };
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(DayFolder(Today)))
        {
            found[Path.GetFileName(file)] = Convert.ToHexString(File.ReadAllBytes(file));
        }

        // Nothing else is in the folder either: no file under a temporary name is left.
        Assert.Equal(expected, found);
    }

    [Fact]
    public void The_record_of_a_copy_with_every_form_names_each_of_its_files()
    {
        var store = Loaded();

        store.Add(EveryForm());

        Assert.Equal(ClipLine.Write(EveryFormRecord("id-1", Noon)) + "\n", File.ReadAllText(DayFile(Today)));
    }

    [Fact]
    public void The_entry_of_a_copy_with_every_form_names_each_of_its_files()
    {
        var expected = EveryFormRecord("id-1", Noon);
        var store = Loaded();

        var entry = store.Add(EveryForm());

        Assert.NotNull(entry);
        Assert.Equal(expected.Html, entry.Html);
        Assert.Equal(expected.Rtf, entry.Rtf);
        Assert.Equal(expected.Image, entry.Image);
        Assert.Equal(expected.Files, entry.Files);
        Assert.Equal(expected.Pictures, entry.Pictures);
        Assert.True(entry.HasPicture);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_kept_picture_is_a_file_under_the_extension_it_was_given(string extension)
    {
        byte[] bytes = [7, 8, 9, 10];
        var store = Loaded();

        var entry = store.Add(new ClipDraft(null)
        {
            Files = [@"C:\made-up\otter" + extension],
            Pictures = [new DraftPicture("otter" + extension, extension, bytes)],
        });

        string name = ClipPaths.BlobName(bytes, extension);
        Assert.NotNull(entry);
        Assert.Equal(new KeptPicture("otter" + extension, name), Assert.Single(entry.Pictures));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(DayFolder(Today), name)));
    }

    [Fact]
    public void The_same_content_added_twice_the_same_day_is_one_file_that_is_not_written_again()
    {
        var store = Loaded();
        var first = store.Add(Copied("one") with { Html = Bytes("<b>walrus</b>") });
        Assert.NotNull(first);
        string file = Path.Combine(DayFolder(Today), first.Html!);
        File.SetLastWriteTimeUtc(file, LongAgo);

        var second = store.Add(Copied("two") with { Html = Bytes("<b>walrus</b>") });

        Assert.NotNull(second);
        Assert.Equal(first.Html, second.Html);
        Assert.Equal(file, Assert.Single(Directory.GetFiles(DayFolder(Today))));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Every_file_that_a_record_names_is_in_its_days_folder()
    {
        var store = Loaded();
        store.Add(EveryForm());
        store.Add(Copied(new string('w', ClipEntry.MaxTextStart + 1)));
        store.Add(new ClipDraft(null) { ImagePng = [1, 2, 3] });

        var names = Lines(Today)
            .Select(line => Assert.IsType<EntryRecord>(ClipLine.Parse(line)))
            .SelectMany(record => record.FileNames())
            .ToList();

        Assert.Equal(6, names.Count);
        Assert.All(names, name => Assert.True(File.Exists(Path.Combine(DayFolder(Today), name))));
    }

    [Fact]
    public void When_content_cannot_be_written_no_record_is_written_and_nothing_is_added()
    {
        // A file stands where the day's folder has to be made.
        var store = Loaded();
        File.WriteAllText(DayFolder(Today), "in the way");

        Assert.ThrowsAny<IOException>(() => store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") }));

        Assert.Empty(Lines(Today));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void When_the_record_cannot_be_written_nothing_is_added()
    {
        var store = Loaded();
        var first = store.Add(Copied("walrus"));
        string before = File.ReadAllText(DayFile(Today));

        using (Hold(DayFile(Today)))
        {
            Assert.ThrowsAny<IOException>(() => store.Add(Copied("otter")));
        }

        Assert.Same(first, Assert.Single(store.Entries));
        Assert.Equal(before, File.ReadAllText(DayFile(Today)));
    }

    // ---- Content that was written for a record that could not be written is taken away again

    private string[] FilesInDayFolder() =>
        Directory.Exists(DayFolder(Today)) ? Directory.GetFiles(DayFolder(Today)).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray() : [];

    [Fact]
    public void Content_files_that_an_add_wrote_are_deleted_again_when_its_record_cannot_be_appended()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        string before = File.ReadAllText(DayFile(Today));

        using (Hold(DayFile(Today)))
        {
            Assert.ThrowsAny<IOException>(() => store.Add(EveryForm() with { Text = new string('o', ClipEntry.MaxTextStart + 1) }));
        }

        // Every form had a file of its own, and none is left.
        Assert.Empty(FilesInDayFolder());
        Assert.Equal(before, File.ReadAllText(DayFile(Today)));
        Assert.Single(store.Entries);
        Assert.False(store.ScrubOwed);
    }

    [Fact]
    public void A_content_file_that_was_there_before_the_add_stays_when_its_record_cannot_be_appended()
    {
        var store = Loaded();
        var first = store.Add(Copied("walrus") with { Html = Bytes("<b>shared</b>") });
        Assert.NotNull(first);
        string shared = ClipPaths.BlobName(Bytes("<b>shared</b>"), ClipPaths.HtmlExtension);

        using (Hold(DayFile(Today)))
        {
            Assert.ThrowsAny<IOException>(() => store.Add(Copied("otter") with { Html = Bytes("<b>shared</b>"), Rtf = Bytes(@"{\rtf1 otter}") }));
        }

        // The first entry still has its file; the one that only this add made is gone.
        Assert.Equal([shared], FilesInDayFolder());
        Assert.True(File.Exists(first.PathOf(first.Html!)));
    }

    [Fact]
    public void A_file_that_cannot_be_written_takes_away_the_files_that_were_written_before_it_for_the_same_add()
    {
        var store = Loaded();
        byte[] rtf = Bytes(@"{\rtf1 otter}");

        // A folder stands where the file of the rich text has to be made: the HTML is written first.
        Directory.CreateDirectory(Path.Combine(DayFolder(Today), ClipPaths.BlobName(rtf, ClipPaths.RtfExtension)));

        Assert.ThrowsAny<Exception>(() => store.Add(Copied("otter") with { Html = Bytes("<b>otter</b>"), Rtf = rtf }));

        Assert.Empty(FilesInDayFolder());
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(DayFile(Today)));

        // What was not made by the add is not touched.
        Assert.True(Directory.Exists(Path.Combine(DayFolder(Today), ClipPaths.BlobName(rtf, ClipPaths.RtfExtension))));
    }

    [Fact]
    public void An_add_that_works_keeps_all_its_files()
    {
        var store = Loaded();

        var entry = store.Add(EveryForm());

        Assert.NotNull(entry);
        Assert.Equal(4, FilesInDayFolder().Length);
    }

    [Fact]
    public void A_draft_that_holds_only_a_label_and_a_program_is_empty()
    {
        Assert.True(new ClipDraft("notepad.exe") { Label = "otter" }.IsEmpty);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("rich text")]
    [InlineData("picture")]
    [InlineData("files")]
    public void A_draft_that_holds_any_one_form_is_not_empty(string form)
    {
        var draft = form switch
        {
            "text" => new ClipDraft(null) { Text = "walrus" },
            "html" => new ClipDraft(null) { Html = Bytes("<b>walrus</b>") },
            "rich text" => new ClipDraft(null) { Rtf = Bytes(@"{\rtf1 walrus}") },
            "picture" => new ClipDraft(null) { ImagePng = [1, 2, 3] },
            _ => new ClipDraft(null) { Files = [@"C:\made-up\otter.txt"] },
        };

        Assert.False(draft.IsEmpty);
    }

    [Fact]
    public void A_draft_with_nothing_in_it_gives_null_and_writes_nothing()
    {
        var store = Loaded();
        var before = Snapshot(ClipboardDir);

        var entry = store.Add(new ClipDraft("notepad.exe") { Label = "otter" });

        Assert.Null(entry);
        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Empty(store.Entries);
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData(null)]
    public void The_same_content_as_this_machines_newest_entry_gives_null_and_writes_nothing_whatever_its_source_program(string? sourceApp)
    {
        var store = Loaded();
        var first = store.Add(Copied("walrus", "notepad.exe"));
        var before = Snapshot(ClipboardDir);

        var second = store.Add(Copied("walrus", sourceApp));

        Assert.Null(second);
        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Same(first, Assert.Single(store.Entries));
    }

    [Fact]
    public void The_same_content_in_every_form_gives_null_and_writes_nothing()
    {
        var store = Loaded();
        store.Add(EveryForm());
        var before = Snapshot(ClipboardDir);

        var second = store.Add(EveryForm());

        Assert.Null(second);
        Assert.Equal(before, Snapshot(ClipboardDir));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("no text")]
    [InlineData("html")]
    [InlineData("no html")]
    [InlineData("rich text")]
    [InlineData("picture")]
    [InlineData("files")]
    [InlineData("kept picture")]
    public void Content_that_differs_in_one_form_is_stored_again(string what)
    {
        var changed = what switch
        {
            "text" => EveryForm() with { Text = "otter" },
            "no text" => EveryForm() with { Text = null },
            "html" => EveryForm() with { Html = Bytes("<i>otter</i>") },
            "no html" => EveryForm() with { Html = null },
            "rich text" => EveryForm() with { Rtf = Bytes(@"{\rtf1 otter}") },
            "picture" => EveryForm() with { ImagePng = [9, 9, 9] },
            "files" => EveryForm() with { Files = [@"C:\made-up\otter.jpg"] },
            _ => EveryForm() with { Pictures = [new DraftPicture("otter.jpg", ".jpg", [9, 9, 9])] },
        };
        var store = Loaded();
        store.Add(EveryForm());

        var second = store.Add(changed);

        Assert.NotNull(second);
        Assert.Equal(2, Lines(Today).Count);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void Long_text_that_differs_only_after_its_start_is_stored_again()
    {
        var store = Loaded();
        store.Add(Copied(new string('w', ClipEntry.MaxTextStart) + "one"));

        var second = store.Add(Copied(new string('w', ClipEntry.MaxTextStart) + "two"));

        Assert.NotNull(second);
        Assert.Equal(2, Directory.GetFiles(DayFolder(Today)).Length);
    }

    [Fact]
    public void The_same_long_text_gives_null()
    {
        var store = Loaded();
        store.Add(Copied(new string('w', ClipEntry.MaxTextStart) + "one"));

        var second = store.Add(Copied(new string('w', ClipEntry.MaxTextStart) + "one"));

        Assert.Null(second);
        Assert.Single(Lines(Today));
    }

    [Fact]
    public void Content_is_stored_again_when_another_entry_was_added_in_between()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));

        var again = store.Add(Copied("walrus"));

        Assert.NotNull(again);
        Assert.Equal(["walrus", "otter", "walrus"], store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void Content_is_stored_again_after_the_newest_entry_was_deleted()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Delete("id-1");

        var again = store.Add(Copied("walrus"));

        Assert.NotNull(again);
        Assert.Same(again, Assert.Single(store.Entries));
    }

    [Fact]
    public void The_same_content_as_an_entry_of_another_machine_is_stored_all_the_same()
    {
        // The other machine's entry is the newest of the whole history.
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddSeconds(-1), "walrus"));
        var store = Loaded();

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.Equal(["id-1", "w-1"], Ids(store.Entries));
    }

    [Fact]
    public void A_newer_entry_of_another_machine_does_not_hide_this_machines_newest_from_the_comparison()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddSeconds(30), "otter"));
        store.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));

        var again = store.Add(Copied("walrus"));

        Assert.Null(again);
        Assert.Equal(["w-1", "id-1"], Ids(store.Entries));
    }

    [Fact]
    public void The_same_content_is_not_stored_again_after_a_restart()
    {
        var draft = EveryForm() with { Text = new string('w', ClipEntry.MaxTextStart) + "tail" };
        var store = Loaded();
        store.Add(draft);
        var reopened = Reopened(store);
        var before = Snapshot(ClipboardDir);

        var again = reopened.Add(EveryForm() with { Text = new string('w', ClipEntry.MaxTextStart) + "tail" });

        Assert.Null(again);
        Assert.Equal(before, Snapshot(ClipboardDir));
    }

    [Fact]
    public void Deleting_an_own_entry_appends_a_delete_record_to_todays_file_and_takes_the_entrys_line_out()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter", sourceApp: null));

        bool deleted = store.Delete("id-1");

        Assert.True(deleted);
        string expected =
            TextLine("id-2", Noon.AddSeconds(1), "otter") + "\n"
            + DeleteLine("id-3", Noon.AddSeconds(2), "id-1") + "\n";
        Assert.Equal(expected, File.ReadAllText(DayFile(Today)));
    }

    [Fact]
    public void A_deleted_entry_is_gone_from_the_history()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));

        store.Delete("id-1");

        Assert.Equal(["id-2"], Ids(store.Entries));
    }

    [Fact]
    public void The_delete_record_goes_into_todays_file_also_when_the_entry_is_in_an_older_days_file()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        _clock.Advance(TimeSpan.FromDays(1));

        bool deleted = store.Delete("id-1");

        Assert.True(deleted);
        Assert.Equal(DeleteLine("id-2", Noon.AddDays(1), "id-1") + "\n", File.ReadAllText(DayFile(Tomorrow)));
        Assert.Empty(Lines(Today));
    }

    [Fact]
    public void Deleting_an_own_entry_deletes_its_content()
    {
        var store = Loaded();
        store.Add(EveryForm() with { Text = new string('w', ClipEntry.MaxTextStart + 1) });
        Assert.Equal(5, Directory.GetFiles(DayFolder(Today)).Length);

        store.Delete("id-1");

        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Fact]
    public void Content_that_another_entry_of_that_day_names_stays_when_one_of_the_two_is_deleted()
    {
        var store = Loaded();
        var first = store.Add(Copied("one") with { Html = Bytes("<b>walrus</b>"), Rtf = Bytes(@"{\rtf1 walrus}") });
        store.Add(Copied("two") with { Html = Bytes("<b>walrus</b>") });
        Assert.NotNull(first);

        store.Delete(first.Id);

        Assert.Equal(first.PathOf(first.Html!), Assert.Single(Directory.GetFiles(DayFolder(Today))));
    }

    [Fact]
    public void Deleting_an_id_the_history_does_not_have_gives_false_and_writes_nothing()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        var before = Snapshot(ClipboardDir);

        bool deleted = store.Delete("no-such-id");

        Assert.False(deleted);
        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Equal(["id-1"], Ids(store.Entries));
    }

    [Fact]
    public void Deleting_the_same_entry_a_second_time_gives_false_and_writes_nothing()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Delete("id-1");
        var before = Snapshot(ClipboardDir);

        bool again = store.Delete("id-1");

        Assert.False(again);
        Assert.Equal(before, Snapshot(ClipboardDir));
    }

    [Fact]
    public void Deleting_another_machines_entry_appends_a_delete_record_to_this_machines_file_and_hides_the_entry()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();

        bool deleted = store.Delete("w-1");

        Assert.True(deleted);
        Assert.Equal(DeleteLine("id-1", Noon, "w-1") + "\n", File.ReadAllText(DayFile(Today)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Deleting_another_machines_entry_changes_nothing_in_that_machines_folder()
    {
        string html = Content(OtherMachine, Today, "<b>walrus</b>");
        WriteDay(OtherMachine, Today, HtmlLine("w-1", Noon.AddHours(-1), html));
        MakeOld(DeviceDir(OtherMachine));
        var store = Loaded();
        var before = Snapshot(DeviceDir(OtherMachine));

        store.Delete("w-1");

        Assert.Equal(before, Snapshot(DeviceDir(OtherMachine)));
        Assert.Equal(3, before.Count);
    }

    [Fact]
    public void An_entry_of_another_machine_that_was_deleted_here_stays_gone_after_a_restart()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"), TextLine("w-2", Noon.AddHours(-1).AddSeconds(1), "otter"));
        var store = Loaded();
        store.Delete("w-1");

        var reopened = Reopened(store);

        Assert.Equal(["w-2"], Ids(reopened.Entries));
    }

    [Fact]
    public void When_the_delete_record_cannot_be_written_nothing_is_deleted()
    {
        var store = Loaded();
        var entry = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        var before = Snapshot(DeviceDir());

        using (Hold(DayFile(Today)))
        {
            Assert.ThrowsAny<IOException>(() => store.Delete("id-1"));
        }

        Assert.Same(entry, Assert.Single(store.Entries));
        Assert.Equal(before, Snapshot(DeviceDir()));
    }

    [Fact]
    public void Clear_appends_one_record_whose_before_is_its_own_time_and_later_than_records_of_the_same_second()
    {
        // Three quick copies took the seconds up to two past noon, and the clock is still at noon.
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        store.Add(Copied("seal"));

        bool cleared = store.Clear();

        Assert.True(cleared);
        var record = Assert.IsType<ClearRecord>(ClipLine.Parse(Lines(Today)[^1]));
        Assert.Equal(new ClearRecord("id-4", Noon.AddSeconds(3), Noon.AddSeconds(3)), record);
        Assert.Single(Lines(Today), line => ClipLine.Parse(line) is ClearRecord);
    }

    [Fact]
    public void Clear_takes_every_older_entry_of_every_machine_out_of_the_history()
    {
        WriteDay(OtherMachine, Yesterday, TextLine("w-1", Noon.AddDays(-1), "one"));
        WriteDay(OtherMachine, Today, TextLine("w-2", Noon.AddSeconds(-1), "two"));
        WriteDay(Machine, Yesterday, TextLine("o-1", Noon.AddDays(-1), "three"));
        var store = Loaded();
        store.Add(Copied("walrus"));
        Assert.Equal(4, store.Entries.Count);

        store.Clear();

        Assert.Empty(store.Entries);
    }

    [Fact]
    public void An_entry_of_another_machine_that_is_not_older_than_the_clear_stays_in_the_history()
    {
        // This machine has written nothing, so the clear gets the clock's own time: noon.
        WriteDay(
            OtherMachine,
            Today,
            TextLine("w-older", Noon.AddSeconds(-1), "one"),
            TextLine("w-same", Noon, "two"),
            TextLine("w-later", Noon.AddMinutes(30), "three"));
        var store = Loaded();

        store.Clear();

        Assert.Equal(["w-later", "w-same"], Ids(store.Entries));
    }

    [Fact]
    public void After_a_clear_this_machines_day_files_hold_only_delete_and_clear_records()
    {
        string olderDelete = DeleteLine("o-2", Noon.AddDays(-1).AddMinutes(5), "elsewhere");
        WriteDay(Machine, Yesterday, TextLine("o-1", Noon.AddDays(-1), "seal"), olderDelete, Garbage);
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        store.Delete("id-1");

        store.Clear();

        // The line that could not be read goes as well: its whole day is before the clear.
        Assert.Equal([olderDelete], Lines(Yesterday));
        Assert.Equal([DeleteLine("id-3", Noon.AddSeconds(2), "id-1"), ClearLine("id-4", Noon.AddSeconds(3))], Lines(Today));
    }

    [Fact]
    public void Clear_takes_a_line_of_todays_file_that_cannot_be_read_off_the_disk_too()
    {
        // Nobody can name such a line in a delete, so only a clear can remove it.
        WriteDay(Machine, Today, Garbage);
        var store = Loaded();

        store.Clear();

        Assert.Equal([ClearLine("id-1", Noon)], Lines(Today));
    }

    [Fact]
    public void Clear_deletes_this_machines_content()
    {
        var store = Loaded();
        store.Add(EveryForm());
        _clock.Advance(TimeSpan.FromDays(1));
        store.Add(new ClipDraft(null) { ImagePng = [1, 2, 3] });
        Assert.Equal(2, Directory.GetDirectories(DeviceDir()).Length);

        store.Clear();

        Assert.Empty(Directory.GetDirectories(DeviceDir()));
    }

    [Fact]
    public void Clear_changes_nothing_in_another_machines_folder()
    {
        string html = Content(OtherMachine, Today, "<b>walrus</b>");
        WriteDay(OtherMachine, Today, HtmlLine("w-1", Noon.AddHours(-1), html), Garbage);
        MakeOld(DeviceDir(OtherMachine));
        var store = Loaded();
        store.Add(Copied("otter"));
        var before = Snapshot(DeviceDir(OtherMachine));

        store.Clear();

        Assert.Equal(before, Snapshot(DeviceDir(OtherMachine)));
        Assert.Equal(3, before.Count);
        Assert.Empty(store.Entries);
    }

    // ---- A delete or a clear that hides what another folder holds leaves its files where they are, and says so

    [Fact]
    public void Deleting_an_entry_of_another_machines_folder_says_that_it_was_hidden_and_not_taken_off_the_disk()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        Assert.False(store.LastChangeHidForeignEntries);

        Assert.True(store.Delete("w-1"));

        Assert.True(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void Deleting_an_entry_of_this_machines_own_folder_does_not_say_so_and_the_next_delete_forgets_the_last()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        var own = store.Add(Copied("otter"));
        Assert.NotNull(own);
        Assert.True(store.Delete("w-1"));
        Assert.True(store.LastChangeHidForeignEntries);

        Assert.True(store.Delete(own.Id));

        Assert.False(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_delete_that_does_nothing_does_not_say_so_and_forgets_the_last_one()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        Assert.True(store.Delete("w-1"));

        Assert.False(store.Delete("no-such-id"));

        Assert.False(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_delete_that_cannot_be_written_does_not_say_so()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        Assert.Throws<IOException>(() => store.Delete("w-1"));

        Assert.False(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_clear_that_takes_an_entry_of_another_machines_folder_out_of_view_says_so()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        store.Add(Copied("otter"));

        Assert.True(store.Clear());

        Assert.True(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_clear_of_this_machines_own_entries_only_does_not_say_so()
    {
        var store = Loaded();
        store.Add(Copied("otter"));

        Assert.True(store.Clear());

        Assert.False(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_clear_that_leaves_the_entry_of_another_machine_that_is_not_older_than_it_does_not_say_so()
    {
        // The other machine's entry is later than the clear, so it stays in view.
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(1), "walrus"));
        var store = Loaded();
        store.Add(Copied("otter"));

        Assert.True(store.Clear());

        Assert.Equal(["w-1"], Ids(store.Entries));
        Assert.False(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_clear_of_an_empty_history_does_not_say_so_and_forgets_the_last_one()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        Assert.True(store.Delete("w-1"));
        Assert.True(store.LastChangeHidForeignEntries);

        Assert.True(store.Clear());

        Assert.False(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void Another_machines_folder_is_still_not_rewritten_when_what_was_hidden_is_said()
    {
        string html = Content(OtherMachine, Today, "<b>walrus</b>");
        WriteDay(OtherMachine, Today, HtmlLine("w-1", Noon.AddHours(-1), html));
        MakeOld(DeviceDir(OtherMachine));
        var store = Loaded();
        var before = Snapshot(DeviceDir(OtherMachine));

        store.Delete("w-1");
        Assert.True(store.LastChangeHidForeignEntries);

        Assert.Equal(before, Snapshot(DeviceDir(OtherMachine)));
    }

    [Fact]
    public void An_entry_added_right_after_a_clear_in_the_same_second_survives_the_clear()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Clear();

        var entry = store.Add(Copied("otter"));

        Assert.NotNull(entry);
        Assert.Equal(Noon.AddSeconds(2), entry.Time);
        Assert.Same(entry, Assert.Single(store.Entries));
    }

    [Fact]
    public void An_entry_added_right_after_a_clear_in_the_same_second_survives_a_restart()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Clear();
        store.Add(Copied("otter"));

        var reopened = Reopened(store);

        Assert.Equal(["id-3"], Ids(reopened.Entries));
        Assert.Equal([ClearLine("id-2", Noon.AddSeconds(1)), TextLine("id-3", Noon.AddSeconds(2), "otter", "notepad.exe")], Lines(Today));
    }

    [Fact]
    public void Clear_on_an_empty_history_still_writes_its_record()
    {
        var store = Loaded();

        bool cleared = store.Clear();

        Assert.True(cleared);
        Assert.Equal([ClearLine("id-1", Noon)], Lines(Today));
    }

    [Fact]
    public void A_clear_after_a_restart_is_still_later_than_what_this_machine_wrote_before_it()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        store.Add(Copied("seal"));
        var reopened = Reopened(store);

        reopened.Clear();

        Assert.Equal([ClearLine("id-4", Noon.AddSeconds(3))], Lines(Today));
        Assert.Empty(reopened.Entries);
    }

    [Fact]
    public void A_clear_does_not_reach_an_own_entry_that_is_more_than_a_minute_ahead_of_the_clock()
    {
        // This pins the one exception that the summary of Clear documents. A record that far
        // ahead of the clock when the history was read was written while the clock was wrong,
        // and the clear is not moved after it: a clear that far ahead would keep clearing what
        // is copied next.
        string ahead = TextLine("o-1", Noon.AddMinutes(5), "walrus");
        WriteDay(Machine, Today, ahead);
        var store = Loaded();

        store.Clear();

        Assert.Equal(["o-1"], Ids(store.Entries));
        Assert.Equal([ahead, ClearLine("id-1", Noon)], Lines(Today));
    }

    // ---- A history that is dated far ahead of the clock is said

    private const string ClockSuspectLine = "Clipboard history is dated more than a day ahead of this machine's clock, so new copies may be removed";

    [Fact]
    public void A_clear_dated_more_than_a_day_ahead_of_the_clock_is_a_suspect_clock_and_is_said_once_in_the_log()
    {
        WriteDay(OtherMachine, Today, ClearLine("w-c", Noon.AddDays(1).AddSeconds(1)));

        var store = Loaded();

        Assert.True(store.ClockSuspect);
        Assert.Single(_reports, text => text == ClockSuspectLine);

        // Not said again by the next load while it stays so.
        store.Load();
        Assert.True(store.ClockSuspect);
        Assert.Single(_reports, text => text == ClockSuspectLine);
    }

    [Fact]
    public void A_clear_dated_exactly_a_day_ahead_of_the_clock_is_not_a_suspect_clock()
    {
        WriteDay(OtherMachine, Today, ClearLine("w-c", Noon.AddDays(1)));

        var store = Loaded();

        Assert.False(store.ClockSuspect);
        Assert.DoesNotContain(ClockSuspectLine, _reports);
    }

    [Fact]
    public void This_machines_newest_record_more_than_a_day_ahead_of_the_clock_is_a_suspect_clock()
    {
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddDays(3), "walrus"));

        var store = Loaded();

        Assert.True(store.ClockSuspect);
        Assert.Single(_reports, text => text == ClockSuspectLine);
    }

    [Fact]
    public void A_record_of_another_machine_far_ahead_of_the_clock_is_not_a_suspect_clock()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddDays(3), "walrus"));

        var store = Loaded();

        Assert.False(store.ClockSuspect);
        Assert.DoesNotContain(ClockSuspectLine, _reports);
    }

    [Fact]
    public void A_history_that_is_not_ahead_of_the_clock_is_no_suspect_clock_and_neither_is_an_empty_one()
    {
        Assert.False(Loaded().ClockSuspect);
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddMinutes(5), "walrus"), ClearLine("o-c", Noon.AddHours(-1)));

        Assert.False(Loaded().ClockSuspect);
        Assert.DoesNotContain(ClockSuspectLine, _reports);
    }

    [Fact]
    public void A_suspect_clock_is_said_again_when_it_has_been_put_right_and_goes_wrong_again()
    {
        WriteDay(OtherMachine, Today, ClearLine("w-c", Noon.AddDays(5)));
        var store = Loaded();
        Assert.True(store.ClockSuspect);

        // The clock catches up with the clear, which is no longer ahead of it.
        _clock.Advance(TimeSpan.FromDays(5));
        store.Load();
        Assert.False(store.ClockSuspect);

        _clock.Advance(TimeSpan.FromDays(-5));
        store.Load();

        Assert.True(store.ClockSuspect);
        Assert.Equal(2, _reports.Count(text => text == ClockSuspectLine));
    }

    [Fact]
    public void A_history_dated_ahead_of_the_clock_is_treated_as_it_was_before_it_was_said()
    {
        WriteDay(OtherMachine, Today, ClearLine("w-c", Noon.AddDays(2)));
        var store = Loaded();
        var added = store.Add(Copied("walrus"));
        Assert.NotNull(added);

        // The clear that is ahead of the clock removes what was copied after it was written, as before.
        var reloaded = Reopened(store);

        Assert.True(reloaded.ClockSuspect);
        Assert.Empty(reloaded.Entries);
        Assert.False(File.ReadAllText(DayFile(Today)).Contains("walrus", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clear_on_one_machine_takes_the_other_machines_entries_off_its_disk_when_that_one_loads()
    {
        var other = Loaded(OtherMachine);
        other.Add(Copied("seal") with { Html = Bytes("<b>seal</b>") });
        var store = Loaded();
        _clock.Advance(TimeSpan.FromSeconds(10));
        store.Clear();

        other.Load();

        Assert.Empty(other.Entries);
        Assert.Empty(Lines(Today, OtherMachine));
        Assert.False(Directory.Exists(DayFolder(Today, OtherMachine)));
    }

    [Fact]
    public void When_the_clear_record_cannot_be_written_nothing_is_cleared()
    {
        var store = Loaded();
        var entry = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        var before = Snapshot(DeviceDir());

        using (Hold(DayFile(Today)))
        {
            Assert.ThrowsAny<IOException>(() => store.Clear());
        }

        Assert.Same(entry, Assert.Single(store.Entries));
        Assert.Equal(before, Snapshot(DeviceDir()));
    }

    [Fact]
    public void The_history_is_empty_until_load_has_run()
    {
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddHours(-1), "walrus"));
        var store = Open();
        Assert.Empty(store.Entries);

        store.Load();

        Assert.Equal(["o-1"], Ids(store.Entries));
    }

    [Fact]
    public void Load_picks_up_a_day_file_that_arrived_from_another_machine()
    {
        var store = Loaded();
        store.Add(Copied("otter"));
        WriteDay(OtherMachine, Yesterday, TextLine("w-1", Noon.AddDays(-1), "walrus"));

        store.Load();

        Assert.Equal(["id-1", "w-1"], Ids(store.Entries));
        var arrived = store.Entries[1];
        Assert.Equal(OtherMachine, arrived.Device);
        Assert.Equal(Yesterday, arrived.Day);
        Assert.Equal(DayFolder(Yesterday, OtherMachine), arrived.Folder);
        Assert.Equal("walrus", arrived.Text);
    }

    [Fact]
    public void Load_puts_what_is_on_disk_in_place_of_what_was_in_memory()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        File.Delete(DayFile(Today));

        store.Load();

        Assert.Empty(store.Entries);
    }

    [Fact]
    public void What_add_wrote_is_what_load_reads_back()
    {
        var store = Loaded();
        var added = store.Add(EveryForm() with { Text = new string('w', ClipEntry.MaxTextStart) + "tail" });
        Assert.NotNull(added);

        var read = Assert.Single(Reopened(store).Entries);

        Assert.Equal(added.Id, read.Id);
        Assert.Equal(added.Time, read.Time);
        Assert.Equal(added.Device, read.Device);
        Assert.Equal(added.Day, read.Day);
        Assert.Equal(added.Folder, read.Folder);
        Assert.Equal(added.SourceApp, read.SourceApp);
        Assert.Equal(added.Text, read.Text);
        Assert.Equal(added.TextLength, read.TextLength);
        Assert.Equal(added.TextFile, read.TextFile);
        Assert.Equal(added.Html, read.Html);
        Assert.Equal(added.Rtf, read.Rtf);
        Assert.Equal(added.Image, read.Image);
        Assert.Equal(added.Files, read.Files);
        Assert.Equal(added.Pictures, read.Pictures);
    }

    [Fact]
    public void An_added_entry_takes_its_place_by_time_among_the_entries_of_another_machine()
    {
        WriteDay(OtherMachine, Today, TextLine("w-early", Noon.AddHours(-1), "one"), TextLine("w-late", Noon.AddMinutes(30), "two"));
        var store = Loaded();

        store.Add(Copied("walrus"));

        Assert.Equal(["w-late", "id-1", "w-early"], Ids(store.Entries));
    }

    [Fact]
    public void Entries_of_the_same_time_are_in_the_same_order_after_an_add_as_after_a_load()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon, "one"));
        WriteDay("ALBATROSS-PC", Today, TextLine("a-1", Noon, "two"));
        var store = Loaded();

        store.Add(Copied("walrus"));
        var afterAdd = Ids(store.Entries).ToList();
        store.Load();

        // By machine: the albatross, this otter, the walrus.
        Assert.Equal(["a-1", "id-1", "w-1"], afterAdd);
        Assert.Equal(afterAdd, Ids(store.Entries));
    }

    [Fact]
    public void Load_takes_an_own_entry_that_another_machine_deleted_off_this_machines_disk()
    {
        var store = Loaded();
        var gone = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        store.Add(Copied("otter"));
        string keptLine = Lines(Today)[1];
        Assert.NotNull(gone);
        WriteDay(OtherMachine, Today, DeleteLine("w-1", Noon.AddMinutes(5), gone.Id));

        store.Load();

        Assert.Equal(["id-2"], Ids(store.Entries));
        Assert.Equal([keptLine], Lines(Today));
        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Fact]
    public void Load_takes_own_entries_that_another_machine_cleared_off_this_machines_disk()
    {
        var store = Loaded();
        store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        store.Add(Copied("otter"));
        string keptLine = Lines(Today)[1];

        // The clear is at the very second of the second entry, which is therefore not older.
        WriteDay(OtherMachine, Today, ClearLine("w-1", Noon.AddSeconds(1)));

        store.Load();

        Assert.Equal(["id-2"], Ids(store.Entries));
        Assert.Equal([keptLine], Lines(Today));
        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Fact]
    public void Load_makes_good_a_delete_of_this_machine_whose_scrub_never_ran()
    {
        // What is left when Wyvern is stopped between writing the record and rewriting the day.
        string deletion = DeleteLine("o-2", Noon.AddHours(-1), "o-1");
        string html = Content(Machine, Today, "<b>walrus</b>");
        WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-2), html), deletion);

        var store = Loaded();

        Assert.Empty(store.Entries);
        Assert.Equal([deletion], Lines(Today));
        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(91, false)]
    public void Load_first_removes_the_days_of_every_machine_that_are_older_than_the_retention(int daysOn, bool kept)
    {
        foreach (string machine in (string[])[Machine, OtherMachine])
        {
            string html = Content(machine, Today, "<b>walrus</b>");
            WriteDay(machine, Today, HtmlLine("entry-of-" + machine, Noon, html));
        }

        _clock.Advance(TimeSpan.FromDays(daysOn));

        var store = Loaded();

        Assert.Equal(kept, File.Exists(DayFile(Today)));
        Assert.Equal(kept, Directory.Exists(DayFolder(Today)));
        Assert.Equal(kept, File.Exists(DayFile(Today, OtherMachine)));
        Assert.Equal(kept, Directory.Exists(DayFolder(Today, OtherMachine)));

        // Removed before anything was read: no entry of a day that has gone is in the history.
        Assert.Equal(kept ? 2 : 0, store.Entries.Count);
    }

    [Fact]
    public void Load_writes_nothing_in_another_machines_folder()
    {
        // The other machine's entry is deleted and cleared by records of this machine, and its
        // folders hold left-over content and temporary files: all of it is for that machine.
        string named = Content(OtherMachine, Today, "<b>walrus</b>");
        Content(OtherMachine, Today, "<b>left over</b>");
        File.WriteAllText(Path.Combine(DayFolder(Today, OtherMachine), named + ClipScrub.TemporaryExtension), "half written");
        string day = WriteDay(OtherMachine, Today, HtmlLine("w-1", Noon.AddHours(-2), named), Garbage);
        File.WriteAllText(day + ClipScrub.TemporaryExtension, "half written");
        WriteDay(Machine, Today, DeleteLine("o-1", Noon.AddHours(-1), "w-1"), ClearLine("o-2", Noon.AddMinutes(-30)));
        MakeOld(DeviceDir(OtherMachine));
        var before = Snapshot(DeviceDir(OtherMachine));

        var store = Loaded();

        Assert.Equal(before, Snapshot(DeviceDir(OtherMachine)));
        Assert.Equal(6, before.Count);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Load_reports_a_summary_that_starts_with_fixed_words()
    {
        Loaded();

        Assert.Equal(
            "Clipboard history loaded: 0 entries from 0 day files, 0 lines skipped, 0 files unreadable, 0 entries and 0 files scrubbed, 0 expired day files and folders removed",
            Assert.Single(_reports));
        Assert.StartsWith(LoadedStart, _reports[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_counts_what_the_load_read_scrubbed_and_removed()
    {
        string html = Content(Machine, Today, "<b>walrus</b>");
        WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-3), html), TextLine("o-2", Noon.AddHours(-2), "otter"), Garbage);
        WriteDay(Machine, new DateOnly(2026, 1, 1), TextLine("o-0", new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), "seal"));
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"), DeleteLine("w-2", Noon.AddMinutes(-30), "o-1"));

        Loaded();

        Assert.Equal(
            "Clipboard history loaded: 2 entries from 2 day files, 1 lines skipped, 0 files unreadable, 1 entries and 1 files scrubbed, 1 expired day files and folders removed",
            _reports[^1]);
    }

    [Fact]
    public void A_line_that_cannot_be_read_is_reported_by_its_number_once_and_not_again_by_the_next_load()
    {
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddHours(-2), "walrus"), Garbage, TextLine("o-2", Noon.AddHours(-1), "otter"));
        var store = Loaded();

        store.Load();

        Assert.Single(_reports, report => report == BadLine(2));
        Assert.Equal(2, _reports.Count(report => report.StartsWith(LoadedStart, StringComparison.Ordinal)));
        Assert.Equal(3, _reports.Count);
    }

    [Fact]
    public void Lines_that_cannot_be_read_are_reported_again_when_their_number_changes()
    {
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddHours(-2), "walrus"), Garbage);
        var store = Loaded();
        WriteDay(OtherMachine, Today, Garbage);
        int told = _reports.Count;

        store.Load();

        Assert.Equal([BadLine(2), BadLine(1)], _reports.Skip(told).SkipLast(1));
    }

    [Fact]
    public void More_lines_that_cannot_be_read_than_are_listed_are_counted_together()
    {
        WriteDay(Machine, Today, Enumerable.Repeat(Garbage, ClipReader.MaxBadLinesListed + 3).ToArray());

        Loaded();

        Assert.Equal(Enumerable.Range(1, ClipReader.MaxBadLinesListed).Select(BadLine), _reports.Take(ClipReader.MaxBadLinesListed));
        Assert.Equal("3 more lines of clipboard history could not be read", _reports[ClipReader.MaxBadLinesListed]);
        Assert.Equal(ClipReader.MaxBadLinesListed + 2, _reports.Count);
    }

    [Fact]
    public void No_report_ever_holds_content_a_program_a_file_name_or_a_path()
    {
        var store = Loaded();
        var draft = EveryForm() with { Text = "walrus at example.org " + new string('w', ClipEntry.MaxTextStart) };
        var entry = store.Add(draft);
        Assert.NotNull(entry);
        string[] content = Directory.GetFiles(DayFolder(Today)).Select(file => Path.GetFileName(file)).ToArray();
        File.AppendAllText(DayFile(Today), "walrus " + Garbage + "\n");
        string other = WriteDay(OtherMachine, Today, TextLine("w-1", Noon, "otter"), "otter " + Garbage);

        // Lines that cannot be read; a folder that is taken; a day of another machine that
        // cannot be read; a day of this machine that can be neither read nor rewritten.
        store.Load();
        Open();
        using (Hold(other))
        {
            store.Load();
        }

        using (Hold(DayFile(Today)))
        {
            store.Load();
        }

        store.Load();
        store.Delete(entry.Id);
        store.Delete("w-1");
        store.Clear();

        Assert.Contains(BadLine(2), _reports);
        Assert.Contains(Taken, _reports);
        Assert.Contains(DayNotRead, _reports);
        Assert.Contains(DayNotRewritten, _reports);
        string[] withheld =
        [
            "walrus", "otter", "notepad", "example.org", "made-up", "id-", "w-1", "2026-10-03", ".jsonl", ".html", ".txt", ".rtf", ".png", ".jpg", "\\",
            _dir.Path, Path.GetFileName(_dir.Path), .. content,
        ];
        Assert.All(_reports, report => Assert.All(withheld, text => Assert.DoesNotContain(text, report, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("clear")]
    [InlineData("load")]
    public void A_list_of_entries_that_was_handed_out_is_not_changed_by_what_happens_later(string what)
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        store.Add(Copied("otter"));
        var handedOut = store.Entries;

        switch (what)
        {
            case "add":
                store.Add(Copied("seal"));
                break;
            case "delete":
                store.Delete("id-1");
                break;
            case "clear":
                store.Clear();
                break;
            default:
                WriteDay(OtherMachine, Today, TextLine("w-1", Noon, "seal"));
                store.Load();
                break;
        }

        Assert.Equal(["id-2", "id-1"], Ids(handedOut));
        Assert.NotEqual(2, store.Entries.Count);
    }

    // ---- A machine folder that is a link is read and never written

    [Fact]
    public void A_machine_folder_that_is_a_link_is_read_and_nothing_is_recorded_into_it()
    {
        string elsewhere = MakeDeviceTarget("elsewhere");
        Junction(DeviceDir(), elsewhere);
        var before = Snapshot(elsewhere);

        var store = Open();
        Assert.Equal([FolderIsALink], _reports);
        store.Load();

        Assert.False(store.IsWriter);
        Assert.Null(store.Add(Copied("otter")));
        Assert.False(store.Delete("o-1"));
        Assert.False(store.Clear());

        // What is behind the link is read, as a reader reads it, and is left exactly as it is:
        // no lock file, no expired day removed, no leftover cleaned up, nothing recorded.
        Assert.Equal(["o-1"], Ids(store.Entries));
        Assert.Equal(before, Snapshot(elsewhere));
        Assert.False(File.Exists(Path.Combine(elsewhere, ClipStore.LockFileName)));
        Assert.False(store.ScrubOwed);

        // Said once, and not after every call.
        Assert.Single(_reports, text => text == FolderIsALink);
    }

    // ---- A scrub that failed is owed

    [Fact]
    public void A_delete_whose_day_file_cannot_be_replaced_is_owed_and_the_next_add_makes_it_good()
    {
        var store = Loaded();
        var gone = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        Assert.NotNull(gone);
        string goneLine = Lines(Today)[0];
        string content = gone.PathOf(gone.Html!);
        Assert.False(store.ScrubOwed);

        using (HoldShared(DayFile(Today)))
        {
            // True: the entry is deleted for every machine from this moment.
            Assert.True(store.Delete("id-1"));

            // And the disk has not caught up: the line and the content are still there.
            Assert.Empty(store.Entries);
            Assert.True(store.ScrubOwed);
            Assert.Contains(goneLine, Lines(Today));
            Assert.True(File.Exists(content));
            Assert.Equal([Ms(50), Ms(100), Ms(200), Ms(400)], _pauses);

            // Another write while the file is still held: five quick tries and no sleep.
            Assert.NotNull(store.Add(Copied("seal")));
            Assert.True(store.ScrubOwed);
            Assert.Equal(4, _pauses.Count);
        }

        Assert.NotNull(store.Add(Copied("otter")));

        Assert.False(store.ScrubOwed);
        Assert.DoesNotContain(goneLine, Lines(Today));
        Assert.False(File.Exists(content));
        Assert.False(Directory.Exists(DayFolder(Today)));
        Assert.Equal(4, _pauses.Count);
        Assert.Equal(["id-4", "id-3"], Ids(store.Entries));
    }

    [Fact]
    public void A_delete_whose_content_cannot_be_deleted_is_owed_and_the_next_add_makes_it_good()
    {
        var store = Loaded();
        var gone = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        Assert.NotNull(gone);
        string goneLine = Lines(Today)[0];
        string content = gone.PathOf(gone.Html!);

        using (Hold(content))
        {
            Assert.True(store.Delete("id-1"));

            // The line went and the file of content stayed.
            Assert.Empty(store.Entries);
            Assert.True(store.ScrubOwed);
            Assert.DoesNotContain(goneLine, Lines(Today));
            Assert.True(File.Exists(content));
        }

        Assert.NotNull(store.Add(Copied("otter")));

        Assert.False(store.ScrubOwed);
        Assert.False(File.Exists(content));
        Assert.False(Directory.Exists(DayFolder(Today)));
        Assert.Equal(["id-3"], Ids(store.Entries));
    }

    [Fact]
    public void A_clear_whose_day_file_cannot_be_replaced_is_owed_and_the_next_clear_makes_it_good()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        string goneLine = Lines(Today)[0];

        using (HoldShared(DayFile(Today)))
        {
            Assert.True(store.Clear());

            Assert.Empty(store.Entries);
            Assert.True(store.ScrubOwed);
            Assert.Contains(goneLine, Lines(Today));
            Assert.Equal([Ms(50), Ms(100), Ms(200), Ms(400)], _pauses);
        }

        Assert.True(store.Clear());

        Assert.False(store.ScrubOwed);
        Assert.Equal([ClearLine("id-2", Noon.AddSeconds(1)), ClearLine("id-3", Noon.AddSeconds(2))], Lines(Today));
    }

    [Fact]
    public void While_a_scrub_is_owed_a_delete_ends_with_a_scrub_of_the_days_that_are_owed_and_not_only_its_own()
    {
        var store = Loaded();
        var gone = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        Assert.NotNull(gone);
        _clock.Advance(TimeSpan.FromDays(1));
        store.Add(Copied("otter"));
        string content = gone.PathOf(gone.Html!);
        using (Hold(content))
        {
            Assert.True(store.Delete("id-1"));
            Assert.True(store.ScrubOwed);
        }

        // The entry deleted now is of tomorrow, and the content that is owed is of today: the day that is owed is scrubbed too.
        Assert.True(store.Delete("id-2"));

        Assert.False(File.Exists(content));
        Assert.False(store.ScrubOwed);
    }

    [Fact]
    public void A_load_whose_scrub_fails_leaves_a_scrub_owed_that_the_next_load_makes_good()
    {
        string html = Content(Machine, Today, "<b>walrus</b>");
        string file = WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-1), html), TextLine("o-2", Noon.AddMinutes(-30), "otter"));
        WriteDay(OtherMachine, Today, DeleteLine("w-1", Noon.AddMinutes(-10), "o-1"));
        var store = Open();

        using (HoldShared(file))
        {
            store.Load();

            Assert.True(store.ScrubOwed);
            Assert.Equal(["o-2"], Ids(store.Entries));
            Assert.Equal([Ms(50), Ms(100), Ms(200), Ms(400)], _pauses);
        }

        store.Load();

        Assert.False(store.ScrubOwed);
        Assert.Equal([TextLine("o-2", Noon.AddMinutes(-30), "otter")], Lines(Today));
        Assert.False(Directory.Exists(DayFolder(Today)));
    }

    [Fact]
    public void A_store_that_is_not_the_writer_never_has_a_scrub_owed()
    {
        string html = Content(Machine, Today, "<b>walrus</b>");
        string file = WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-1), html));
        WriteDay(OtherMachine, Today, DeleteLine("w-1", Noon.AddMinutes(-10), "o-1"));
        Open();
        var reader = Open();
        _pauses.Clear();

        using (HoldShared(file))
        {
            reader.Load();
        }

        Assert.False(reader.IsWriter);
        Assert.False(reader.ScrubOwed);
        Assert.Empty(_pauses);
    }

    [Fact]
    public void A_store_that_is_let_go_of_has_no_scrub_owed_any_more()
    {
        string html = Content(Machine, Today, "<b>walrus</b>");
        string file = WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-1), html));
        WriteDay(OtherMachine, Today, DeleteLine("w-1", Noon.AddMinutes(-10), "o-1"));
        var store = Open();
        using (HoldShared(file))
        {
            store.Load();
            Assert.True(store.ScrubOwed);
        }

        store.Dispose();

        Assert.False(store.ScrubOwed);
    }

    // ---- A scrub that is owed is made good day by day, and said once

    private static string MadeGood(int entries, int files) =>
        $"A scrub that was owed is made good: {entries} entries and {files} files removed";

    private bool MadeGoodWasSaid => _reports.Any(text => text.StartsWith("A scrub that was owed is made good:", StringComparison.Ordinal));

    /// <summary>An entry with a file of content, to be deleted while that file is held, so that its day is owed.</summary>
    private (ClipEntry Gone, string Content) OwingEntry(ClipStore store)
    {
        var gone = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        Assert.NotNull(gone);
        return (gone, gone.PathOf(gone.Html!));
    }

    [Fact]
    public void While_a_scrub_is_owed_a_write_scrubs_only_the_days_that_are_owed_and_a_load_scrubs_them_all()
    {
        var store = Loaded();
        var (gone, content) = OwingEntry(store);

        // Content that no record names, in the folder of another day: only a scrub of that day, or of all of them, deletes it.
        string stray = Path.Combine(DayFolder(Yesterday), Content(Machine, Yesterday, "<b>stray</b>"));
        using (Hold(content))
        {
            Assert.True(store.Delete(gone.Id));
            Assert.True(store.ScrubOwed);

            // A delete scrubs its own day. A write while the scrub is owed scrubs the owed day, and no other.
            Assert.NotNull(store.Add(Copied("otter")));
            Assert.True(store.ScrubOwed);
            Assert.True(File.Exists(stray));
        }

        Assert.NotNull(store.Add(Copied("seal")));

        Assert.False(store.ScrubOwed);
        Assert.False(File.Exists(content));
        Assert.True(File.Exists(stray));

        store.Load();

        Assert.False(File.Exists(stray));
    }

    [Fact]
    public void When_what_was_owed_has_left_the_disk_one_counted_line_says_so_and_it_is_not_said_before_or_again()
    {
        var store = Loaded();
        var (gone, content) = OwingEntry(store);
        using (Hold(content))
        {
            Assert.True(store.Delete(gone.Id));
            store.Add(Copied("otter"));
            Assert.False(MadeGoodWasSaid);
        }

        store.Add(Copied("seal"));

        Assert.Single(_reports, text => text == MadeGood(entries: 0, files: 1));
        Assert.False(store.ScrubOwed);

        store.Add(Copied("a walrus"));
        store.Load();
        Assert.Single(_reports, text => text.StartsWith("A scrub that was owed is made good:", StringComparison.Ordinal));
    }

    [Fact]
    public void No_line_about_a_scrub_made_good_is_said_when_nothing_was_owed()
    {
        var store = Loaded();
        var (gone, _) = OwingEntry(store);
        store.Add(Copied("otter"));
        store.Delete(gone.Id);
        store.Clear();
        store.Load();

        Assert.False(MadeGoodWasSaid);
    }

    [Fact]
    public void A_load_that_makes_good_what_was_owed_says_so_with_what_it_removed()
    {
        string html = Content(Machine, Today, "<b>walrus</b>");
        string file = WriteDay(Machine, Today, HtmlLine("o-1", Noon.AddHours(-1), html), TextLine("o-2", Noon.AddMinutes(-30), "otter"));
        WriteDay(OtherMachine, Today, DeleteLine("w-1", Noon.AddMinutes(-10), "o-1"));
        var store = Open();
        using (HoldShared(file))
        {
            store.Load();
            Assert.True(store.ScrubOwed);
        }

        store.Load();

        Assert.False(store.ScrubOwed);
        Assert.Single(_reports, text => text == MadeGood(entries: 1, files: 1));
    }

    [Fact]
    public void A_clear_that_makes_good_what_was_owed_says_so_with_what_it_removed()
    {
        var store = Loaded();
        store.Add(Copied("walrus"));
        using (HoldShared(DayFile(Today)))
        {
            Assert.True(store.Clear());
            Assert.True(store.ScrubOwed);
        }

        Assert.True(store.Clear());

        Assert.False(store.ScrubOwed);
        Assert.Single(_reports, text => text == MadeGood(entries: 1, files: 0));
    }

    [Fact]
    public void A_day_whose_folder_is_a_link_stays_owed_and_is_tried_again_by_each_write_without_a_report()
    {
        var store = Loaded();
        var entry = store.Add(Copied("walrus"));
        Assert.NotNull(entry);
        string elsewhere = MakeDayTarget("elsewhere");
        Junction(DayFolder(Today), elsewhere);
        var before = Snapshot(elsewhere);

        // What a delete would have left behind the link is owed, and said once.
        Assert.True(store.Delete(entry.Id));
        Assert.True(store.ScrubOwed);
        Assert.Single(_reports, text => text == DayFolderIsALinkWhenScrubbing);

        _reports.Clear();
        Assert.NotNull(store.Add(Copied("otter")));
        Assert.NotNull(store.Add(Copied("seal")));

        Assert.True(store.ScrubOwed);
        Assert.Empty(_reports);
        Assert.Equal(before, Snapshot(elsewhere));
    }

    private const string DayFolderIsALinkWhenScrubbing = "A day's folder in the clipboard history is a link and is left alone";

    [Fact]
    public void Today_being_full_is_said_once_for_the_day_and_not_by_every_call_that_is_refused()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        Assert.Null(store.Add(Copied("a")));
        Assert.Null(store.Add(Copied("b")));
        Assert.Throws<IOException>(() => store.Delete("w-1"));
        Assert.Throws<IOException>(() => store.Clear());
        Assert.Null(store.Add(Copied("c")));

        Assert.Single(_reports, text => text == Full);

        // Tomorrow it is said again, once. (Today's file goes first, so that the disk need not hold two of them.)
        File.Delete(DayFile(Today));
        _clock.Advance(TimeSpan.FromDays(1));
        GrowTo(DayFile(Tomorrow), ClipLimits.MaxDayFileBytes - 10);
        Assert.Null(store.Add(Copied("d")));
        Assert.Null(store.Add(Copied("e")));

        Assert.Equal(2, _reports.Count(text => text == Full));
    }

    // ---- The store always follows a time it handed out itself

    [Fact]
    public void Seventy_adds_with_the_clock_standing_still_get_seventy_rising_seconds_and_a_clear_takes_them_all()
    {
        var store = Loaded();
        for (int i = 1; i <= 70; i++)
        {
            Assert.NotNull(store.Add(Copied("copy " + i)));
        }

        Assert.Equal(Enumerable.Range(0, 70).Reverse().Select(second => Noon.AddSeconds(second)), store.Entries.Select(entry => entry.Time));
        Assert.Equal("id-70", store.Entries[0].Id);
        Assert.Equal(70, Lines(Today).Count);

        store.Clear();

        Assert.Empty(store.Entries);
        Assert.Equal([ClearLine("id-71", Noon.AddSeconds(70))], Lines(Today));
    }

    [Fact]
    public void A_load_in_the_middle_of_a_long_run_of_adds_changes_neither_the_order_nor_the_next_time()
    {
        var store = Loaded();
        for (int i = 1; i <= 70; i++)
        {
            store.Add(Copied("copy " + i));
        }

        var ids = Ids(store.Entries).ToList();
        var times = store.Entries.Select(entry => entry.Time).ToList();

        // The newest record on disk is 69 seconds ahead of the clock: the load does not follow
        // it, and the store does not need to, for it handed that second out itself.
        store.Load();

        Assert.Equal(ids, Ids(store.Entries));
        Assert.Equal(times, store.Entries.Select(entry => entry.Time));
        var next = store.Add(Copied("after"));
        Assert.NotNull(next);
        Assert.Equal(Noon.AddSeconds(70), next.Time);
        Assert.Same(next, store.Entries[0]);

        store.Clear();

        Assert.Empty(store.Entries);
        Assert.Equal([ClearLine("id-72", Noon.AddSeconds(71))], Lines(Today));
    }

    [Fact]
    public void A_load_that_cannot_read_todays_file_does_not_lower_a_time_that_was_handed_out()
    {
        var store = Loaded();
        for (int i = 1; i <= 70; i++)
        {
            store.Add(Copied("copy " + i));
        }

        using (Hold(DayFile(Today)))
        {
            store.Load();
        }

        var next = store.Add(Copied("after"));

        Assert.NotNull(next);
        Assert.Equal(Noon.AddSeconds(70), next.Time);
    }

    [Fact]
    public void Deletes_and_clears_take_a_second_too_and_are_followed_just_the_same()
    {
        var store = Loaded();
        for (int i = 1; i <= 70; i++)
        {
            store.Add(Copied("copy " + i));
        }

        for (int i = 1; i <= 5; i++)
        {
            Assert.True(store.Delete("id-" + i));
        }

        var next = store.Add(Copied("after"));

        // Seventy seconds for the copies and five for the deletes.
        Assert.NotNull(next);
        Assert.Equal(Noon.AddSeconds(75), next.Time);
        Assert.Same(next, store.Entries[0]);
        Assert.Equal(66, store.Entries.Count);
    }

    // ---- IsWriter, ScrubOwed and Entries never wait

    [Fact]
    public async Task IsWriter_ScrubOwed_and_Entries_answer_while_the_store_is_inside_a_load()
    {
        WriteDay(Machine, Today, TextLine("o-1", Noon.AddHours(-1), "walrus"));
        using var gate = new LoadGate();
        var store = OpenWithGate(gate);
        var load = Task.Run(store.Load);
        try
        {
            Assert.True(gate.Entered.Wait(Limit));

            // Asked from another thread, so that a wait for the store's lock is a failure of
            // this test (it times out) and not a hang of it.
            var answers = Task.Run(() => (store.IsWriter, store.ScrubOwed, store.Entries.Count));
            Assert.Equal((true, false, 1), await answers.WaitAsync(Limit));

            // They answered while the load was still held where it is.
            Assert.False(load.IsCompleted);
        }
        finally
        {
            gate.Release.Set();
        }

        await load.WaitAsync(Limit);
    }

    // ---- A load never forgets a delete

    [Fact]
    public void A_load_that_cannot_read_this_machines_day_file_does_not_bring_back_an_entry_deleted_here()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"), TextLine("w-2", Noon.AddHours(-1).AddSeconds(1), "otter"));
        var store = Loaded();
        Assert.True(store.Delete("w-1"));

        // The delete record is in this machine's day file, which is held while the store reads.
        using (Hold(DayFile(Today)))
        {
            store.Load();
        }

        Assert.Equal(["w-2"], Ids(store.Entries));
    }

    [Fact]
    public void A_load_that_cannot_read_this_machines_day_file_does_not_bring_back_what_a_clear_took()
    {
        WriteDay(OtherMachine, Yesterday, TextLine("w-1", Noon.AddDays(-1), "walrus"));
        var store = Loaded();
        Assert.True(store.Clear());
        Assert.Empty(store.Entries);

        using (Hold(DayFile(Today)))
        {
            store.Load();
        }

        Assert.Empty(store.Entries);
    }

    [Fact]
    public void A_load_that_reads_a_later_clear_keeps_the_later_of_the_two_times()
    {
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddMinutes(-20), "walrus"), TextLine("w-2", Noon.AddMinutes(-5), "otter"));
        var store = Loaded();
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(store.Clear());

        // Another machine has since cleared up to a time before this machine's clear, which is
        // in this machine's day file, held while the store reads: the only clear the load sees
        // is the earlier one, and the later one is what the store remembers.
        WriteDay("ALBATROSS-PC", Today, ClearLine("a-1", Noon.AddMinutes(-10)));
        using (Hold(DayFile(Today)))
        {
            store.Load();
        }

        Assert.Empty(store.Entries);
    }

    // ---- Nothing is written into a day folder that is a link

    [Fact]
    public void A_copy_that_needs_a_file_of_content_is_not_recorded_when_the_days_folder_is_a_link()
    {
        var store = Loaded();
        string elsewhere = MakeDayTarget("elsewhere");
        Junction(DayFolder(Today), elsewhere);
        var before = Snapshot(elsewhere);

        var html = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });
        var longText = store.Add(Copied(new string('w', ClipEntry.MaxTextStart + 1)));

        Assert.Null(html);
        Assert.Null(longText);
        Assert.Equal(before, Snapshot(elsewhere));
        Assert.False(File.Exists(DayFile(Today)));
        Assert.Empty(store.Entries);
        Assert.Equal(2, _reports.Count(text => text == DayFolderIsALink));
    }

    [Fact]
    public void A_copy_that_needs_no_file_of_content_is_recorded_all_the_same_when_the_days_folder_is_a_link()
    {
        var store = Loaded();
        string elsewhere = MakeDayTarget("elsewhere");
        Junction(DayFolder(Today), elsewhere);
        var before = Snapshot(elsewhere);

        var entry = store.Add(Copied("walrus"));

        // The day's file is beside the folder and not behind the link.
        Assert.NotNull(entry);
        Assert.Equal([TextLine("id-1", Noon, "walrus", "notepad.exe")], Lines(Today));
        Assert.Equal(before, Snapshot(elsewhere));
        Assert.DoesNotContain(DayFolderIsALink, _reports);
    }

    [Fact]
    public void A_refused_copy_does_not_use_up_a_second()
    {
        var store = Loaded();
        Junction(DayFolder(Today), MakeDayTarget("elsewhere"));
        Assert.Null(store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") }));

        var entry = store.Add(Copied("otter"));

        Assert.NotNull(entry);
        Assert.Equal(Noon, entry.Time);
    }

    // ---- A kept picture that could not be read back is never written

    private static string PicturesLeftOut(int count) => $"Pictures of a copy that could not be recorded were left out: {count}";

    [Theory]
    [InlineData(".exe", "otter.exe")]
    [InlineData(@"..\x", "otter.jpg")]
    [InlineData("", "otter.jpg")]
    [InlineData(".JPG", "otter.jpg")]
    [InlineData(".jpg", "")]
    public void A_kept_picture_that_would_not_read_back_is_left_out_and_the_rest_of_the_entry_is_recorded(string extension, string name)
    {
        var store = Loaded();

        var entry = store.Add(new ClipDraft("notepad.exe")
        {
            Text = "walrus",
            Files = [@"C:\made-up\otter.jpg"],
            Pictures = [new DraftPicture(name, extension, [1, 2, 3])],
        });

        Assert.NotNull(entry);
        Assert.Empty(entry.Pictures);
        Assert.Equal("walrus", entry.Text);

        // Nothing was written for it: no folder, no file under a name made of the extension.
        Assert.False(Directory.Exists(DayFolder(Today)));
        Assert.Equal(
            [Path.Combine(DeviceDir(), ClipStore.LockFileName), DayFile(Today)],
            Directory.GetFileSystemEntries(DeviceDir()).Order(StringComparer.Ordinal));
        Assert.Single(_reports, text => text == PicturesLeftOut(1));

        // What was written reads back, which is the point: the entry is not lost at the next start.
        var read = Assert.Single(Reopened(store).Entries);
        Assert.Equal("walrus", read.Text);
        Assert.Equal([@"C:\made-up\otter.jpg"], read.Files);
        Assert.Empty(read.Pictures);
    }

    [Fact]
    public void Of_several_kept_pictures_only_those_that_would_not_read_back_are_left_out_and_counted_once()
    {
        var good = new DraftPicture("otter.jpg", ".jpg", [1, 2, 3]);
        var store = Loaded();

        var entry = store.Add(new ClipDraft(null)
        {
            Files = [@"C:\made-up\a.exe", @"C:\made-up\otter.jpg", @"C:\made-up\b"],
            Pictures = [new DraftPicture("a.exe", ".exe", [9]), good, new DraftPicture("b", "", [8])],
        });

        Assert.NotNull(entry);
        string name = ClipPaths.BlobName(good.Bytes, ".jpg");
        Assert.Equal(new KeptPicture("otter.jpg", name), Assert.Single(entry.Pictures));
        Assert.Equal(Path.Combine(DayFolder(Today), name), Assert.Single(Directory.GetFiles(DayFolder(Today))));
        Assert.Single(_reports, text => text == PicturesLeftOut(2));
        Assert.Equal(new KeptPicture("otter.jpg", name), Assert.Single(Assert.Single(Reopened(store).Entries).Pictures));
    }

    [Fact]
    public void A_draft_with_nothing_left_after_its_pictures_are_left_out_is_not_recorded()
    {
        var store = Loaded();
        var before = Snapshot(ClipboardDir);

        var entry = store.Add(new ClipDraft(null) { Pictures = [new DraftPicture("otter.exe", ".exe", [1, 2, 3])] });

        Assert.Null(entry);
        Assert.Equal(before, Snapshot(ClipboardDir));
        Assert.Empty(store.Entries);
    }

    // ---- A day file is never grown past what can be read back, and the last of it is for delete and clear

    private const string FullWords = "Today's clipboard history is full, so this change could not be written";

    /// <summary>Makes a day file longer without writing anything: what the file system gives a file that is made longer is zeros that are never stored.</summary>
    private static void GrowTo(string file, long length)
    {
        using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    [Fact]
    public void An_add_that_would_grow_the_day_file_past_the_limit_writes_nothing_and_says_so()
    {
        var store = Loaded();
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        var entry = store.Add(Copied("walrus") with { Html = Bytes("<b>walrus</b>") });

        Assert.Null(entry);
        Assert.Equal(ClipLimits.MaxDayFileBytes - 10, new FileInfo(DayFile(Today)).Length);
        Assert.False(Directory.Exists(DayFolder(Today)));
        Assert.Empty(store.Entries);
        Assert.Single(_reports, text => text == Full);
    }

    [Fact]
    public void An_add_is_refused_once_the_file_would_pass_the_limit_less_the_reserve_for_delete_and_clear()
    {
        var store = Loaded();
        long length = ClipLimits.MaxDayFileBytes - ClipStore.DeleteReserveBytes - 10;
        GrowTo(DayFile(Today), length);

        var entry = store.Add(Copied("walrus"));

        Assert.Equal(64 * 1024, ClipStore.DeleteReserveBytes);
        Assert.Null(entry);
        Assert.Equal(length, new FileInfo(DayFile(Today)).Length);
        Assert.Empty(store.Entries);
        Assert.Single(_reports, text => text == Full);
    }

    [Fact]
    public void An_add_that_fits_below_the_reserve_is_stored()
    {
        var store = Loaded();
        long length = ClipLimits.MaxDayFileBytes - ClipStore.DeleteReserveBytes - 1000;
        GrowTo(DayFile(Today), length);

        var entry = store.Add(Copied("walrus"));

        Assert.NotNull(entry);
        Assert.True(new FileInfo(DayFile(Today)).Length <= ClipLimits.MaxDayFileBytes - ClipStore.DeleteReserveBytes);
        Assert.DoesNotContain(Full, _reports);
    }

    [Fact]
    public void A_delete_may_use_the_reserve_that_an_add_may_not()
    {
        // An entry of another machine, so that the delete does not scrub this machine's day file,
        // which would have to read all of it.
        WriteDay(OtherMachine, Today, TextLine("w-1", Noon.AddHours(-1), "walrus"));
        var store = Loaded();
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 1000);

        Assert.Null(store.Add(Copied("otter")));
        Assert.True(store.Delete("w-1"));

        Assert.Empty(store.Entries);
        long length = new FileInfo(DayFile(Today)).Length;
        Assert.True(length > ClipLimits.MaxDayFileBytes - 1000);
        Assert.True(length <= ClipLimits.MaxDayFileBytes);
    }

    [Fact]
    public void A_delete_that_would_grow_the_day_file_past_the_limit_even_with_the_reserve_writes_nothing_and_throws()
    {
        var store = Loaded();
        var entry = store.Add(Copied("walrus"));
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        var failure = Assert.Throws<IOException>(() => store.Delete("id-1"));

        Assert.Equal(FullWords, failure.Message);
        Assert.Equal(ClipLimits.MaxDayFileBytes - 10, new FileInfo(DayFile(Today)).Length);
        Assert.Same(entry, Assert.Single(store.Entries));
        Assert.Single(_reports, text => text == Full);
    }

    [Fact]
    public void A_clear_that_would_grow_the_day_file_past_the_limit_even_with_the_reserve_writes_nothing_and_throws()
    {
        var store = Loaded();
        var entry = store.Add(Copied("walrus"));
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        var failure = Assert.Throws<IOException>(() => store.Clear());

        Assert.Equal(FullWords, failure.Message);
        Assert.Equal(ClipLimits.MaxDayFileBytes - 10, new FileInfo(DayFile(Today)).Length);
        Assert.Same(entry, Assert.Single(store.Entries));
        Assert.Single(_reports, text => text == Full);
    }

    // ---- The lock is tried three times

    private string LockPath => Path.Combine(DeviceDir(), ClipStore.LockFileName);

    private ClipStore OpenWithPause(Action<TimeSpan> pause)
    {
        var store = new ClipStore(ClipboardDir, Machine, RetentionDays, _clock, (text, _) => _reports.Add(text), () => $"id-{++_ids}", pause);
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void A_lock_that_is_let_go_of_during_the_first_pause_is_taken()
    {
        Directory.CreateDirectory(DeviceDir());
        var holder = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pauses = new List<TimeSpan>();
        try
        {
            var store = OpenWithPause(length =>
            {
                pauses.Add(length);
                holder.Dispose();
            });

            Assert.True(store.IsWriter);
            Assert.Equal([Ms(50)], pauses);
            Assert.Empty(_reports);
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public void A_lock_that_is_let_go_of_during_the_second_pause_is_taken()
    {
        Directory.CreateDirectory(DeviceDir());
        var holder = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pauses = new List<TimeSpan>();
        try
        {
            var store = OpenWithPause(length =>
            {
                pauses.Add(length);
                if (pauses.Count == 2)
                {
                    holder.Dispose();
                }
            });

            Assert.True(store.IsWriter);
            Assert.Equal([Ms(50), Ms(50)], pauses);
            Assert.Empty(_reports);
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public void A_lock_that_is_never_let_go_of_is_tried_three_times_with_a_pause_between_and_then_says_it_is_taken()
    {
        Directory.CreateDirectory(DeviceDir());
        using var holder = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var store = Open();

        Assert.False(store.IsWriter);
        Assert.Equal([Ms(50), Ms(50)], _pauses);
        Assert.Equal([Taken], _reports);
    }

    [Fact]
    public void A_second_real_store_on_the_folder_is_still_a_reader_after_its_tries()
    {
        var first = Open();
        Assert.Empty(_pauses);

        var second = Open();

        Assert.True(first.IsWriter);
        Assert.False(second.IsWriter);
        Assert.Equal([Ms(50), Ms(50)], _pauses);
        Assert.Equal([Taken], _reports);
    }

    [Fact]
    public void A_folder_that_cannot_be_prepared_is_not_waited_for()
    {
        File.WriteAllText(ClipboardDir, "in the way");

        Open();

        Assert.Empty(_pauses);
        Assert.Equal([NotPrepared], _reports);
    }

    // ---- What no other test would notice

    [Fact]
    public async Task An_add_waits_for_the_store_to_finish_what_it_is_doing()
    {
        using var gate = new LoadGate();
        var store = OpenWithGate(gate);
        var load = Task.Run(store.Load);
        Task<ClipEntry?>? add = null;
        try
        {
            Assert.True(gate.Entered.Wait(Limit));
            add = Task.Run(() => store.Add(Copied("walrus")));

            // The one place a test of this class waits for a fixed time: what it asserts is
            // that something does not happen.
            await Assert.ThrowsAsync<TimeoutException>(() => add.WaitAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            gate.Release.Set();
        }

        await load.WaitAsync(Limit);
        var entry = await add!.WaitAsync(Limit);
        Assert.NotNull(entry);
        Assert.Same(entry, Assert.Single(store.Entries));
        Assert.Equal("walrus", entry.Text);
    }
}
