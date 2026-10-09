using System.Diagnostics;
using System.Text;
using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

public sealed class ClipRetentionTests : IDisposable
{
    // Made-up machines, so that no test depends on the name of the one it runs on.
    private const string Machine = "OTTER-PC";
    private const string OtherMachine = "WALRUS-PC";

    private const int RetentionDays = 90;
    private const string NotRemoved = "Expired clipboard history could not be removed";
    private const string DayFolderIsALink = "A day's folder in the clipboard history is a link and is left alone";

    private static readonly DateOnly Today = new(2026, 10, 3);

    // With 90 days kept: the last day that stays, 90 days before today, and the first that goes.
    private static readonly DateOnly LastKept = new(2026, 7, 5);
    private static readonly DateOnly FirstExpired = new(2026, 7, 4);
    private static readonly DateOnly LongExpired = new(2025, 12, 31);

    // Older than anything a test does, so that a file that was written again shows.
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TempDir _dir = new();
    private readonly List<(string Text, Exception? Cause)> _reports = [];
    private readonly List<string> _links = [];

    public void Dispose()
    {
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

    private string DeviceDir(string machine) => Path.Combine(ClipboardDir, machine);

    private string DayFile(string machine, DateOnly day) => Path.Combine(DeviceDir(machine), ClipPaths.DayFileName(day));

    private string DayFolder(string machine, DateOnly day) => Path.Combine(DeviceDir(machine), ClipPaths.DayName(day));

    /// <summary>The line of an entry that was copied on this day.</summary>
    private static string Entry(string id, DateOnly day) =>
        ClipLine.Write(new EntryRecord(id, new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero), null) { Text = "walrus", TextLength = 6 });

    /// <summary>Writes a day file that holds one entry of that day.</summary>
    private string WriteDay(string machine, DateOnly day)
    {
        Directory.CreateDirectory(DeviceDir(machine));
        string file = DayFile(machine, day);
        File.WriteAllText(file, Entry("a", day) + "\n");
        return file;
    }

    /// <summary>Puts a file of content into a day's folder under the name the store would give it.</summary>
    /// <returns>The file's full path.</returns>
    private string Content(string machine, DateOnly day, string content = "<b>walrus</b>", string extension = ClipPaths.HtmlExtension)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        Directory.CreateDirectory(DayFolder(machine, day));
        string file = Path.Combine(DayFolder(machine, day), ClipPaths.BlobName(bytes, extension));
        File.WriteAllBytes(file, bytes);
        return file;
    }

    /// <summary>Writes a file under any name and gives it an old date, so that it shows when it is written again.</summary>
    /// <returns>The file's full path.</returns>
    private static string Other(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name);
        File.WriteAllText(file, "otter");
        File.SetLastWriteTimeUtc(file, LongAgo);
        return file;
    }

    /// <summary>Holds a file open the way a program does that lets nobody else in.</summary>
    private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.None);

    private int Run() => ClipRetention.Run(ClipboardDir, RetentionDays, Today, (text, cause) => _reports.Add((text, cause)));

    /// <summary>
    /// Makes a real junction. It needs no special privilege, and it is the one program a test of
    /// this class starts: cmd.exe from the Windows folder. The link and the target are both
    /// inside the test's own temporary folder.
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

        // The rule the code goes by. If this is null for a junction, the tests below prove nothing.
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    /// <summary>
    /// The target of a junction, outside the clipboard folder: a folder that holds everything
    /// retention would touch if it followed the link, at its top level (an expired day file, a
    /// file of content, and a temporary file of each) and in folders named for expired days.
    /// </summary>
    private string MakeJunctionTarget()
    {
        string target = _dir.File("elsewhere");
        string dayFolder = Path.Combine(target, ClipPaths.DayName(FirstExpired));
        Directory.CreateDirectory(dayFolder);
        Directory.CreateDirectory(Path.Combine(target, ClipPaths.DayName(LongExpired)));
        byte[] bytes = Encoding.UTF8.GetBytes("<b>otter</b>");
        string blob = ClipPaths.BlobName(bytes, ClipPaths.HtmlExtension);
        string[] files =
        [
            Path.Combine(target, ClipPaths.DayFileName(FirstExpired)),
            Path.Combine(target, ClipPaths.DayFileName(FirstExpired) + ClipScrub.TemporaryExtension),
            Path.Combine(target, blob),
            Path.Combine(target, blob + ClipScrub.TemporaryExtension),
            Path.Combine(dayFolder, blob),
            Path.Combine(dayFolder, blob + ClipScrub.TemporaryExtension),
        ];
        File.WriteAllText(files[0], Entry("a", FirstExpired) + "\n");
        File.WriteAllText(files[1], Entry("gone", FirstExpired) + "\n");
        foreach (string file in files.Skip(2))
        {
            File.WriteAllBytes(file, bytes);
        }

        foreach (string file in files)
        {
            File.SetLastWriteTimeUtc(file, LongAgo);
        }

        return target;
    }

    /// <summary>Every file and folder under a folder with its bytes and its date, so that a change of any kind shows.</summary>
    private static string[] Snapshot(string folder) =>
        Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? path + "|" + Convert.ToHexString(File.ReadAllBytes(path)) + "|" + File.GetLastWriteTimeUtc(path).Ticks
                : path + "|folder")
            .ToArray();

    [Theory]
    [InlineData(2026, 10, 4, false)]
    [InlineData(2026, 10, 3, false)]
    [InlineData(2026, 10, 2, false)]
    [InlineData(2026, 7, 5, false)]
    [InlineData(2026, 7, 4, true)]
    [InlineData(2025, 10, 3, true)]
    public void With_90_days_a_day_90_days_before_today_is_kept_and_one_91_days_before_is_expired(int year, int month, int day, bool expired)
    {
        // 5 July is 90 days before 3 October, and 4 July is 91.
        Assert.Equal(expired, ClipRetention.IsExpired(new DateOnly(year, month, day), Today, 90));
    }

    [Theory]
    [InlineData(1, 2026, 10, 2, false)]
    [InlineData(1, 2026, 10, 1, true)]
    [InlineData(30, 2026, 9, 3, false)]
    [InlineData(30, 2026, 9, 2, true)]
    [InlineData(100_000, 1, 1, 1, true)]
    [InlineData(1_000_000, 1, 1, 1, false)]
    public void The_number_of_days_kept_is_the_one_given(int retentionDays, int year, int month, int day, bool expired)
    {
        Assert.Equal(expired, ClipRetention.IsExpired(new DateOnly(year, month, day), Today, retentionDays));
    }

    [Fact]
    public void Expired_day_files_and_the_content_of_expired_day_folders_go_under_every_machines_folder()
    {
        string[] expired =
        [
            WriteDay(Machine, FirstExpired),
            Content(Machine, FirstExpired),
            WriteDay(OtherMachine, LongExpired),
            Content(OtherMachine, LongExpired),
        ];

        Run();

        Assert.All(expired, file => Assert.False(File.Exists(file)));
        Assert.False(Directory.Exists(DayFolder(Machine, FirstExpired)));
        Assert.False(Directory.Exists(DayFolder(OtherMachine, LongExpired)));
    }

    [Fact]
    public void Days_that_are_not_expired_are_kept_with_their_content_beside_ones_that_go()
    {
        WriteDay(Machine, FirstExpired);
        WriteDay(OtherMachine, FirstExpired);
        string[] kept =
        [
            WriteDay(Machine, LastKept),
            Content(Machine, LastKept),
            WriteDay(Machine, Today),
            Content(Machine, Today),
            WriteDay(OtherMachine, LastKept),
            Content(OtherMachine, LastKept),
        ];

        Run();

        Assert.All(kept, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public void The_number_handed_back_counts_the_day_files_and_the_day_folders_that_went()
    {
        // One machine loses a file and its folder, the other a file alone and a folder alone.
        WriteDay(Machine, FirstExpired);
        Content(Machine, FirstExpired);
        WriteDay(OtherMachine, FirstExpired);
        Content(OtherMachine, LongExpired);
        WriteDay(OtherMachine, LastKept);
        Content(OtherMachine, LastKept);

        int removed = Run();

        Assert.Equal(4, removed);
    }

    [Fact]
    public void Nothing_expired_gives_zero()
    {
        WriteDay(Machine, LastKept);
        Content(Machine, LastKept);

        int removed = Run();

        Assert.Equal(0, removed);
        Assert.Empty(_reports);
    }

    [Fact]
    public void The_day_in_the_name_decides_and_not_what_the_file_holds()
    {
        Directory.CreateDirectory(DeviceDir(Machine));
        string expiredByName = DayFile(Machine, FirstExpired);
        File.WriteAllText(expiredByName, Entry("a", Today) + "\n");
        string keptByName = DayFile(Machine, LastKept);
        File.WriteAllText(keptByName, Entry("b", LongExpired) + "\n");

        Run();

        Assert.False(File.Exists(expiredByName));
        Assert.True(File.Exists(keptByName));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("2026-01-01.jsonl.tmp")]
    [InlineData("2026-01-01.jsonl.bak")]
    [InlineData("2026-01-01.json")]
    [InlineData("2026-01-01.JSONL")]
    [InlineData("2026-1-1.jsonl")]
    [InlineData("2026-02-30.jsonl")]
    [InlineData("old 2026-01-01.jsonl")]
    public void A_file_that_is_not_named_exactly_as_a_day_file_is_left_alone(string name)
    {
        // The second row is what an interrupted rewrite leaves. The scrub of the machine that
        // owns it removes that, not the retention.
        string file = Other(DeviceDir(Machine), name);
        WriteDay(Machine, FirstExpired);

        int removed = Run();

        Assert.Equal("otter", File.ReadAllText(file));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(1, removed);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("2026-1-1")]
    [InlineData("2026-01-01.old")]
    [InlineData("2026-02-30")]
    public void A_folder_that_is_not_named_exactly_as_a_day_folder_is_left_alone_with_what_is_in_it(string name)
    {
        string folder = Path.Combine(DeviceDir(Machine), name);
        string content = Other(folder, ClipPaths.BlobName([1, 2, 3], ClipPaths.HtmlExtension));
        string temporary = Other(folder, "half-written.tmp");

        int removed = Run();

        Assert.True(File.Exists(content));
        Assert.True(File.Exists(temporary));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void A_file_in_the_clipboard_folder_itself_is_left_alone()
    {
        // Days are looked for under the machines' folders and nowhere else.
        string file = Other(ClipboardDir, ClipPaths.DayFileName(FirstExpired));

        int removed = Run();

        Assert.True(File.Exists(file));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void In_an_expired_day_folder_only_content_and_temporary_files_are_deleted()
    {
        string html = Content(Machine, FirstExpired);
        string picture = Content(Machine, FirstExpired, "a picture of a walrus", ".jpeg");
        string temporary = Other(DayFolder(Machine, FirstExpired), ClipPaths.BlobName([1, 2, 3], ClipPaths.ImageExtension) + ClipScrub.TemporaryExtension);
        string notes = Other(DayFolder(Machine, FirstExpired), "notes.txt");

        Run();

        Assert.False(File.Exists(html));
        Assert.False(File.Exists(picture));
        Assert.False(File.Exists(temporary));
        Assert.Equal(notes, Assert.Single(Directory.GetFileSystemEntries(DayFolder(Machine, FirstExpired))));
    }

    [Fact]
    public void An_expired_day_folder_that_holds_something_else_stays_with_that_in_it_and_is_not_counted()
    {
        Content(Machine, FirstExpired);
        string notes = Other(DayFolder(Machine, FirstExpired), "notes.txt");

        int removed = Run();

        Assert.Equal("otter", File.ReadAllText(notes));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(notes));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void An_expired_day_folder_that_holds_a_folder_stays()
    {
        Content(Machine, FirstExpired);
        string kept = Other(Path.Combine(DayFolder(Machine, FirstExpired), "inner"), ClipPaths.BlobName([1, 2, 3], ClipPaths.HtmlExtension));

        int removed = Run();

        Assert.True(File.Exists(kept));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void An_expired_day_folder_with_nothing_in_it_is_removed_and_counted()
    {
        Directory.CreateDirectory(DayFolder(Machine, FirstExpired));

        int removed = Run();

        Assert.False(Directory.Exists(DayFolder(Machine, FirstExpired)));
        Assert.Equal(1, removed);
    }

    [Fact]
    public void A_clipboard_folder_that_is_not_there_gives_zero_and_is_not_made()
    {
        int removed = Run();

        Assert.Equal(0, removed);
        Assert.False(Directory.Exists(ClipboardDir));
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_machine_whose_expired_day_cannot_be_deleted_does_not_stop_the_other_machines()
    {
        // The machine that fails comes first in the order the folders are gone through.
        string locked = WriteDay(Machine, FirstExpired);
        string other = WriteDay(OtherMachine, FirstExpired);

        int removed;
        using (Hold(locked))
        {
            removed = Run();
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));
        Assert.Equal(1, removed);
    }

    [Fact]
    public void An_expired_day_that_cannot_be_deleted_is_reported_in_fixed_words_without_a_name_or_a_path()
    {
        string locked = WriteDay(Machine, FirstExpired);

        using (Hold(locked))
        {
            Run();
        }

        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(NotRemoved, text);
        Assert.IsAssignableFrom<IOException>(cause);
    }

    [Fact]
    public void A_failure_does_not_come_out_when_nobody_listens_for_the_report()
    {
        string locked = WriteDay(Machine, FirstExpired);

        int removed;
        using (Hold(locked))
        {
            removed = ClipRetention.Run(ClipboardDir, RetentionDays, Today);
        }

        Assert.Equal(0, removed);
    }

    [Fact]
    public void What_could_not_be_removed_is_removed_by_the_next_run()
    {
        string locked = WriteDay(Machine, FirstExpired);
        using (Hold(locked))
        {
            Run();
        }

        int removed = Run();

        Assert.False(File.Exists(locked));
        Assert.Equal(1, removed);
    }

    // ---- A number of days that makes no sense deletes nothing (item H)

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void With_less_than_one_day_kept_no_day_is_expired(int retentionDays)
    {
        Assert.False(ClipRetention.IsExpired(Today, Today, retentionDays));
        Assert.False(ClipRetention.IsExpired(FirstExpired, Today, retentionDays));
        Assert.False(ClipRetention.IsExpired(DateOnly.MinValue, Today, retentionDays));
    }

    [Fact]
    public void A_very_large_number_of_days_does_not_throw_and_expires_no_day()
    {
        Assert.False(ClipRetention.IsExpired(DateOnly.MinValue, Today, int.MaxValue));
        Assert.False(ClipRetention.IsExpired(LongExpired, Today, int.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void A_number_of_days_that_makes_no_sense_deletes_nothing(int retentionDays)
    {
        string[] files =
        [
            WriteDay(Machine, Today),
            WriteDay(Machine, FirstExpired),
            Content(Machine, FirstExpired),
            WriteDay(OtherMachine, LongExpired),
            Content(OtherMachine, LongExpired),
        ];

        int removed = ClipRetention.Run(ClipboardDir, retentionDays, Today, (text, cause) => _reports.Add((text, cause)));

        Assert.Equal(0, removed);
        Assert.All(files, file => Assert.True(File.Exists(file)));
        Assert.Empty(_reports);
    }

    // ---- Links are not followed (item D)

    [Fact]
    public void A_machine_folder_that_is_a_link_is_passed_over_and_the_other_machines_are_still_cleaned()
    {
        string elsewhere = MakeJunctionTarget();
        Junction(DeviceDir(Machine), elsewhere);
        string other = WriteDay(OtherMachine, FirstExpired);
        string[] before = Snapshot(elsewhere);

        int removed = Run();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(other));
        Assert.True(Directory.Exists(DeviceDir(Machine)));
        Assert.Equal(before, Snapshot(elsewhere));
        Assert.Empty(_reports);
    }

    [Fact]
    public void An_expired_day_folder_that_is_a_link_is_passed_over_neither_emptied_nor_removed_and_is_told_in_fixed_words()
    {
        string elsewhere = MakeJunctionTarget();
        string dayFile = WriteDay(Machine, FirstExpired);
        Junction(DayFolder(Machine, FirstExpired), elsewhere);
        string[] before = Snapshot(elsewhere);

        int removed = Run();

        // The day file beside the link is an ordinary expired day file and goes.
        Assert.Equal(1, removed);
        Assert.False(File.Exists(dayFile));
        Assert.True(Directory.Exists(DayFolder(Machine, FirstExpired)));
        Assert.Equal(before, Snapshot(elsewhere));

        // Not passed over in silence: what is in it is expired, and stays.
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(DayFolderIsALink, text);
        Assert.Null(cause);
    }

    [Fact]
    public void Several_expired_day_folders_that_are_links_are_told_once_for_the_run_and_other_failures_are_told_as_well()
    {
        MakeJunctionTarget();
        string second = _dir.File("elsewhere-too");
        Directory.CreateDirectory(second);
        Junction(DayFolder(Machine, FirstExpired), _dir.File("elsewhere"));
        Junction(DayFolder(OtherMachine, LongExpired), second);
        string locked = WriteDay(Machine, LongExpired);

        using (Hold(locked))
        {
            Run();
        }

        Assert.Equal(2, _reports.Count);
        Assert.Single(_reports, report => report.Text == DayFolderIsALink);
        Assert.Single(_reports, report => report.Text == NotRemoved);
    }

    [Fact]
    public void A_day_folder_that_is_a_link_and_is_not_expired_is_left_alone_in_silence()
    {
        string elsewhere = MakeJunctionTarget();
        WriteDay(Machine, LastKept);
        Junction(DayFolder(Machine, LastKept), elsewhere);

        int removed = Run();

        Assert.Equal(0, removed);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_day_folder_that_is_a_link_is_told_again_by_every_run_for_as_long_as_it_is_one()
    {
        Junction(DayFolder(Machine, FirstExpired), MakeJunctionTarget());

        Run();
        Run();

        Assert.Equal(2, _reports.Count(report => report.Text == DayFolderIsALink));
    }

    // ---- Only the temporary files this code writes are removed (item E)

    [Theory]
    [InlineData("notes.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.pdf.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde.txt.tmp")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.txt.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.html.tmp.tmp")]
    [InlineData("2026-07-04.jsonl.tmp")]
    public void In_an_expired_day_folder_a_temporary_file_that_is_not_named_for_content_stays_with_its_folder(string name)
    {
        string html = Content(Machine, FirstExpired);
        string file = Other(DayFolder(Machine, FirstExpired), name);

        int removed = Run();

        Assert.False(File.Exists(html));
        Assert.Equal("otter", File.ReadAllText(file));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(0, removed);
    }

    // ---- One file that cannot be deleted does not stop the rest (item F)

    [Fact]
    public void An_expired_day_file_that_cannot_be_deleted_does_not_stop_the_rest_of_that_machine()
    {
        // The day files are gone through oldest first, so the held one is met first.
        string locked = WriteDay(Machine, LongExpired);
        string later = WriteDay(Machine, FirstExpired);
        string html = Content(Machine, FirstExpired);

        int removed;
        using (Hold(locked))
        {
            removed = Run();
        }

        // The later day file, and the later day's folder and what was in it.
        Assert.Equal(2, removed);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(later));
        Assert.False(File.Exists(html));
        Assert.False(Directory.Exists(DayFolder(Machine, FirstExpired)));
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(NotRemoved, text);
        Assert.IsAssignableFrom<IOException>(cause);
    }

    [Fact]
    public void Content_that_cannot_be_deleted_does_not_stop_the_other_files_and_is_told_once_for_the_run()
    {
        // A directory listing on NTFS comes in the order of the names, so the first two of the
        // three are the ones that are met first, and the ones that are held.
        string[] files = new[]
            {
                Content(Machine, FirstExpired, "<b>one</b>"),
                Content(Machine, FirstExpired, "<b>two</b>"),
                Content(Machine, FirstExpired, "<b>three</b>"),
            }
            .Order(StringComparer.Ordinal)
            .ToArray();
        string otherMachine = WriteDay(OtherMachine, FirstExpired);

        int removed;
        using (Hold(files[0]))
        using (Hold(files[1]))
        {
            removed = Run();
        }

        // The other machine's day file went. The folder with two files in it stays.
        Assert.Equal(1, removed);
        Assert.False(File.Exists(otherMachine));
        Assert.True(File.Exists(files[0]));
        Assert.True(File.Exists(files[1]));
        Assert.False(File.Exists(files[2]));
        Assert.True(Directory.Exists(DayFolder(Machine, FirstExpired)));
        Assert.Equal(NotRemoved, Assert.Single(_reports).Text);
    }
}
