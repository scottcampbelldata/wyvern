using System.Text;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// The file operations of sync, in a throwaway data folder. What matters most here is what is
/// refused: a path that is not safe, a folder on the way that is a link, and a file that is no
/// longer as the run saw it.
/// </summary>
public sealed class SyncFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Earlier = new(2026, 9, 30, 8, 15, 0, TimeSpan.Zero);
    private static readonly byte[] Walrus = Encoding.UTF8.GetBytes("walrus");

    private readonly SyncSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private FileOutcome Write(string path, string text, LocalStat? expected, DateTimeOffset? mtime = null, Action<TimeSpan>? pause = null) =>
        SyncFiles.Write(_box.Data, path, Encoding.UTF8.GetBytes(text), mtime ?? Earlier, expected, Now, out _, pause ?? (_ => { }));

    private LocalStat StatOf(string path) => SyncFiles.Stat(_box.Data, path) ?? throw new InvalidOperationException("There is no such file in the sandbox.");

    [Fact]
    public void A_new_file_is_written_with_its_folders_its_content_and_its_time()
    {
        var outcome = SyncFiles.Write(_box.Data, "library/snippets/work/sig.md", Walrus, Earlier, expected: null, Now, out var written);

        Assert.Equal(FileOutcome.Done, outcome);
        Assert.Equal("walrus", _box.Read("library/snippets/work/sig.md"));
        Assert.Equal(new LocalStat(6, Earlier), written);
        Assert.Equal(written, StatOf("library/snippets/work/sig.md"));
        Assert.Equal(["library/snippets/work/sig.md"], _box.Files());
    }

    [Fact]
    public void A_file_that_is_there_is_never_overwritten_by_a_write_that_expected_none()
    {
        _box.Write("library/a.md", "mine");

        Assert.Equal(FileOutcome.Changed, Write("library/a.md", "theirs", expected: null));

        Assert.Equal("mine", _box.Read("library/a.md"));
        Assert.Equal(["library/a.md"], _box.Files());
    }

    [Fact]
    public void A_file_that_still_stands_as_expected_is_replaced()
    {
        _box.Write("library/a.md", "old", Earlier);

        Assert.Equal(FileOutcome.Done, Write("library/a.md", "new text", StatOf("library/a.md"), mtime: Now));

        Assert.Equal("new text", _box.Read("library/a.md"));
        Assert.Equal(Now, _box.Mtime("library/a.md"));
        Assert.Equal(["library/a.md"], _box.Files());
    }

    [Fact]
    public void A_file_that_was_edited_since_the_run_looked_is_not_replaced()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        _box.Write("library/a.md", "edited just now", Now);

        Assert.Equal(FileOutcome.Changed, Write("library/a.md", "from the server", seen));

        Assert.Equal("edited just now", _box.Read("library/a.md"));
    }

    [Fact]
    public void A_file_that_was_removed_since_the_run_looked_is_not_written_again()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        _box.Delete("library/a.md");

        Assert.Equal(FileOutcome.Changed, Write("library/a.md", "from the server", seen));

        Assert.False(_box.Exists("library/a.md"));
    }

    [Theory]
    [InlineData(2099)]
    [InlineData(1970)]
    public void A_time_that_makes_no_sense_is_not_given_to_a_file(int year)
    {
        var nonsense = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(FileOutcome.Done, Write("library/a.md", "text", expected: null, mtime: nonsense));

        Assert.NotEqual(nonsense, _box.Mtime("library/a.md"));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("library/../../outside.md")]
    [InlineData("/outside.md")]
    [InlineData("C:/outside.md")]
    [InlineData("library/NUL")]
    [InlineData("library/a.md:stream")]
    [InlineData("clipboard/OTTER-PC/.~lock")]
    [InlineData("library/a.md.tmp")]
    public void A_path_that_is_not_safe_or_not_synced_is_never_written_deleted_read_or_moved(string path)
    {
        var anything = new LocalStat(0, Earlier);

        Assert.Equal(FileOutcome.Unsafe, Write(path, "text", expected: null));
        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Delete(_box.Data, path, anything));
        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Move(_box.Data, path, "library/b.md", anything));
        Assert.Null(SyncFiles.Read(_box.Data, path, out _));
        Assert.Null(SyncFiles.Stat(_box.Data, path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_box.Data));
        Assert.False(File.Exists(Path.Combine(_box.Root, "outside.md")));
    }

    [Fact]
    public void Nothing_is_written_read_or_deleted_through_a_folder_that_is_a_link()
    {
        string elsewhere = Path.Combine(_box.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "theirs.md"), "not ours");
        _box.Junction(_box.Full("library/linked"), elsewhere);
        var info = new FileInfo(Path.Combine(elsewhere, "theirs.md"));
        var theirs = new LocalStat(info.Length, new DateTimeOffset(info.LastWriteTimeUtc));

        Assert.Equal(FileOutcome.Unsafe, Write("library/linked/new.md", "text", expected: null));
        Assert.Equal(FileOutcome.Unsafe, Write("library/linked/deeper/new.md", "text", expected: null));
        Assert.Equal(FileOutcome.Unsafe, Write("library/linked/theirs.md", "text", theirs));
        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Delete(_box.Data, "library/linked/theirs.md", theirs));
        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Move(_box.Data, "library/linked/theirs.md", "library/ours.md", theirs));
        Assert.Null(SyncFiles.Read(_box.Data, "library/linked/theirs.md", out _));
        Assert.Null(SyncFiles.Stat(_box.Data, "library/linked/theirs.md"));

        Assert.Equal(["theirs.md"], Directory.EnumerateFileSystemEntries(elsewhere).Select(Path.GetFileName));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "theirs.md")));
    }

    [Fact]
    public void A_path_that_would_go_through_a_file_or_onto_a_folder_is_refused()
    {
        _box.Write("library/notes", "a file where a folder is wanted");
        Directory.CreateDirectory(_box.Full("library/folder.md"));

        Assert.Equal(FileOutcome.Unsafe, Write("library/notes/a.md", "text", expected: null));
        Assert.Equal(FileOutcome.Unsafe, Write("library/folder.md", "text", expected: null));

        Assert.Equal("a file where a folder is wanted", _box.Read("library/notes"));
        Assert.True(Directory.Exists(_box.Full("library/folder.md")));
    }

    [Fact]
    public void A_file_that_is_held_is_replaced_once_it_is_let_go_of()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");

        // In a using, so that a failed assertion cannot leave the file open over the sandbox's cleanup.
        using var held = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var pauses = new List<TimeSpan>();

        var outcome = Write("library/a.md", "new", seen, pause: length =>
        {
            pauses.Add(length);
            if (pauses.Count == 2)
            {
                held.Dispose();
            }
        });

        Assert.Equal(FileOutcome.Done, outcome);
        Assert.Equal("new", _box.Read("library/a.md"));
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], pauses);
    }

    [Fact]
    public void A_file_the_owner_edited_while_the_replace_waited_for_it_is_not_replaced()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        using var holder = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        int pauses = 0;

        // The program that holds the file saves what the owner typed, and lets go.
        var outcome = Write("library/a.md", "from the server", seen, pause: _ =>
        {
            pauses++;
            holder.Write("typed by the owner meanwhile"u8);
            holder.Dispose();
            Assert.NotEqual(seen, StatOf("library/a.md"));
        });

        Assert.Equal(FileOutcome.Changed, outcome);
        Assert.Equal(1, pauses);
        Assert.Equal("typed by the owner meanwhile", _box.Read("library/a.md"));
        Assert.Equal(["library/a.md"], _box.Files());
    }

    [Fact]
    public void A_file_that_was_deleted_while_the_replace_waited_for_it_is_not_made_again()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        using var holder = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int pauses = 0;

        var outcome = Write("library/a.md", "from the server", seen, pause: _ =>
        {
            pauses++;
            holder.Dispose();
            _box.Delete("library/a.md");
        });

        Assert.Equal(FileOutcome.Changed, outcome);
        Assert.Equal(1, pauses);
        Assert.False(_box.Exists("library/a.md"));
        Assert.Empty(_box.Files());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_is_given_back_as_written_is_what_the_target_has_after_a_replace(bool timeIsSane)
    {
        _box.Write("library/a.md", "old", Earlier);
        var mtime = timeIsSane ? Now : new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = SyncFiles.Write(_box.Data, "library/a.md", "a longer text"u8, mtime, StatOf("library/a.md"), Now, out var written, _ => { });

        Assert.Equal(FileOutcome.Done, outcome);
        Assert.Equal(StatOf("library/a.md"), written);
        Assert.Equal(13, written.Size);
        Assert.Equal(timeIsSane, written.MtimeUtc == Now);
    }

    [Fact]
    public void A_file_that_stays_held_is_left_as_it_was_and_no_temporary_file_stays_behind()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        using var held = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
        int pauses = 0;

        var outcome = Write("library/a.md", "new", seen, pause: _ => pauses++);

        Assert.Equal(FileOutcome.Failed, outcome);
        Assert.Equal(4, pauses);
        Assert.Equal(["library/a.md"], _box.Files());
    }

    [Fact]
    public void A_file_is_deleted_only_while_it_stands_as_expected()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        _box.Write("library/a.md", "edited", Now);

        Assert.Equal(FileOutcome.Changed, SyncFiles.Delete(_box.Data, "library/a.md", seen));
        Assert.Equal("edited", _box.Read("library/a.md"));

        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/a.md", StatOf("library/a.md")));
        Assert.False(_box.Exists("library/a.md"));

        // Already gone is done.
        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/a.md", seen));
    }

    [Fact]
    public void A_file_that_is_held_is_not_deleted()
    {
        _box.Write("library/a.md", "old", Earlier);
        using var held = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Equal(FileOutcome.Failed, SyncFiles.Delete(_box.Data, "library/a.md", StatOf("library/a.md")));
        Assert.True(_box.Exists("library/a.md"));
    }

    [Fact]
    public void A_file_is_done_with_when_it_and_its_folder_are_known_to_be_gone()
    {
        var anything = new LocalStat(0, Earlier);

        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/never-there.md", anything));
        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/no/such/folder/a.md", anything));
    }

    [Fact]
    public void A_file_is_not_taken_for_gone_while_the_data_folder_itself_is_not_there()
    {
        // The data folder is taken away after the run looked at it, as when its drive is
        // unplugged: Windows then says "path not found" for every file in it. That is no
        // answer about the file, which is where it was, in a folder that is somewhere else.
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        string away = _box.Data + "-away";
        Directory.Move(_box.Data, away);
        try
        {
            Assert.Equal(FileOutcome.Failed, SyncFiles.Delete(_box.Data, "library/a.md", seen));
            Assert.Equal(FileOutcome.Failed, SyncFiles.Delete(_box.Data, "library/never-there.md", seen));
            Assert.Equal(FileOutcome.Failed, SyncFiles.Delete(_box.Data, "library/no/such/folder/a.md", seen));
            Assert.True(File.Exists(Path.Combine(away, "library", "a.md")));
        }
        finally
        {
            Directory.Move(away, _box.Data);
        }

        // A data folder that was never there is the same.
        Assert.Equal(FileOutcome.Failed, SyncFiles.Delete(Path.Combine(_box.Root, "nowhere"), "library/a.md", seen));

        // With the folder back, the file is there to be deleted, and one that is not is done.
        Assert.Equal("old", _box.Read("library/a.md"));
        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/never-there.md", seen));
        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/a.md", seen));
        Assert.False(_box.Exists("library/a.md"));
    }

    // Stat gives null for a file that is not there and for one that cannot be asked about, and
    // Delete must tell them apart. No such file can be made here without special rights: Stat
    // does not look at a file's size, and a file whose attributes are denied (in a folder that
    // cannot be listed) is still answered for. So Delete is told that the file it asked about
    // could not be asked about, which is all it can be told from outside.
    [Fact]
    public void A_file_that_cannot_be_asked_about_is_not_taken_for_gone()
    {
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");

        var outcome = SyncFiles.Delete(_box.Data, "library/a.md", seen, cannotBeAsked: () => true);

        Assert.Equal(FileOutcome.Failed, outcome);
        Assert.Equal("old", _box.Read("library/a.md"));
    }

    [Fact]
    public void How_a_file_stands_always_comes_from_the_disk_and_what_a_test_puts_in_cannot_stand_in_for_that_look()
    {
        // The owner edits the file after the run looked at it. Whatever is handed to Delete
        // from outside, the look that guards the delete is the real one: the file stays.
        _box.Write("library/a.md", "old", Earlier);
        var seen = StatOf("library/a.md");
        _box.Write("library/a.md", "edited by the owner", Now);
        int asked = 0;

        var outcome = SyncFiles.Delete(_box.Data, "library/a.md", seen, cannotBeAsked: () =>
        {
            asked++;
            return false;
        });

        Assert.Equal(FileOutcome.Changed, outcome);
        Assert.Equal(1, asked);
        Assert.Equal("edited by the owner", _box.Read("library/a.md"));

        // And a file that stands as expected is deleted when the answer is let through.
        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/a.md", StatOf("library/a.md"), cannotBeAsked: () => false));
        Assert.False(_box.Exists("library/a.md"));
    }

    [Fact]
    public void A_file_that_is_not_there_is_gone_however_the_question_is_put()
    {
        var anything = new LocalStat(0, Earlier);

        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/never-there.md", anything, cannotBeAsked: () => true));
        Assert.Equal(FileOutcome.Done, SyncFiles.Delete(_box.Data, "library/no/such/folder/a.md", anything, cannotBeAsked: () => true));
    }

    [Fact]
    public void A_folder_is_never_deleted_whatever_is_said_about_asking()
    {
        Directory.CreateDirectory(_box.Full("library/folder.md"));

        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Delete(_box.Data, "library/folder.md", new LocalStat(0, Earlier), cannotBeAsked: () => true));

        Assert.True(Directory.Exists(_box.Full("library/folder.md")));
    }

    [Fact]
    public void A_file_is_renamed_beside_itself_and_keeps_its_content_and_time()
    {
        _box.Write("library/a.md", "mine", Earlier);

        var outcome = SyncFiles.Move(_box.Data, "library/a.md", "library/a.conflict-PC-20261003-120000.md", StatOf("library/a.md"));

        Assert.Equal(FileOutcome.Done, outcome);
        Assert.Equal(["library/a.conflict-PC-20261003-120000.md"], _box.Files());
        Assert.Equal("mine", _box.Read("library/a.conflict-PC-20261003-120000.md"));
        Assert.Equal(Earlier, _box.Mtime("library/a.conflict-PC-20261003-120000.md"));
    }

    [Fact]
    public void A_rename_never_replaces_a_file_and_never_moves_one_that_changed()
    {
        _box.Write("library/a.md", "mine", Earlier);
        _box.Write("library/b.md", "other", Earlier);
        var seen = StatOf("library/a.md");

        Assert.Equal(FileOutcome.Changed, SyncFiles.Move(_box.Data, "library/a.md", "library/b.md", seen));

        _box.Write("library/a.md", "edited", Now);
        Assert.Equal(FileOutcome.Changed, SyncFiles.Move(_box.Data, "library/a.md", "library/c.md", seen));

        Assert.Equal(["library/a.md", "library/b.md"], _box.Files());
        Assert.Equal("other", _box.Read("library/b.md"));
    }

    [Fact]
    public void A_file_is_read_whole_with_its_size_and_time_while_another_program_has_it_open()
    {
        _box.Write("library/a.md", "walrus", Earlier);
        using var editor = new FileStream(_box.Full("library/a.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        byte[]? content = SyncFiles.Read(_box.Data, "library/a.md", out var stat);

        Assert.Equal(Walrus, content);
        Assert.Equal(new LocalStat(6, Earlier), stat);
    }

    [Fact]
    public void A_file_that_is_not_there_or_is_locked_or_is_too_large_is_not_read()
    {
        _box.Write("library/locked.md", "text");
        using var locked = new FileStream(_box.Full("library/locked.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using (var large = new FileStream(_box.Full("library/large.bin"), FileMode.CreateNew))
        {
            large.SetLength(SyncLimits.MaxFileBytes + 1);
        }

        Assert.Null(SyncFiles.Read(_box.Data, "library/nothing.md", out _));
        Assert.Null(SyncFiles.Read(_box.Data, "library/locked.md", out _));
        Assert.Null(SyncFiles.Read(_box.Data, "library/large.bin", out var stat));
        Assert.Equal(SyncLimits.MaxFileBytes + 1, stat.Size);
    }

    [Theory]
    [InlineData(".~wyvern-sync-0123456789abcdef.tmp", true)]
    [InlineData(".~wyvern-sync-0123456789abcde.tmp", false)]
    [InlineData(".~wyvern-sync-0123456789ABCDEF.tmp", false)]
    [InlineData(".~wyvern-sync-0123456789abcdef.tmpx", false)]
    [InlineData("x.~wyvern-sync-0123456789abcdef.tmp", false)]
    [InlineData("2026-10-03.jsonl.tmp", false)]
    [InlineData("notes.tmp", false)]
    public void Only_the_exact_name_sync_gives_a_temporary_file_is_its_own(string name, bool own)
    {
        Assert.Equal(own, SyncFiles.IsTemporaryName(name));
    }

    [Fact]
    public void A_leftover_of_sync_is_removed_once_it_is_an_hour_old_and_nothing_else_is()
    {
        _box.Write("library/.~wyvern-sync-0123456789abcdef.tmp", "half a file", Now.AddMinutes(-61), created: Now.AddMinutes(-61));
        _box.Write("library/.~wyvern-sync-fedcba9876543210.tmp", "being written now", Now.AddMinutes(-59), created: Now.AddMinutes(-59));
        _box.Write("library/notes.tmp", "somebody else's", Now.AddDays(-30), created: Now.AddDays(-30));

        var removed = _box.Files().Where(path => SyncFiles.RemoveLeftover(new FileInfo(_box.Full(path)), Now)).ToList();

        Assert.Equal(["library/.~wyvern-sync-0123456789abcdef.tmp"], removed);
        Assert.Equal(["library/.~wyvern-sync-fedcba9876543210.tmp", "library/notes.tmp"], _box.Files());
    }

    [Fact]
    public void A_leftovers_age_is_the_time_it_was_made_not_the_time_it_was_stamped_with()
    {
        // Write stamps its temporary file with the time of the file it is writing, which may be old.
        _box.Write("library/.~wyvern-sync-0123456789abcdef.tmp", "in use, made a minute ago", Now.AddYears(-1), created: Now.AddMinutes(-1));
        _box.Write("library/.~wyvern-sync-fedcba9876543210.tmp", "left behind, made two hours ago", Now, created: Now.AddHours(-2));

        var removed = _box.Files().Where(path => SyncFiles.RemoveLeftover(new FileInfo(_box.Full(path)), Now)).ToList();

        Assert.Equal(["library/.~wyvern-sync-fedcba9876543210.tmp"], removed);
        Assert.Equal(["library/.~wyvern-sync-0123456789abcdef.tmp"], _box.Files());
    }

    [Fact]
    public void Nothing_is_moved_into_a_folder_that_is_a_link()
    {
        string elsewhere = Path.Combine(_box.Root, "elsewhere");
        _box.Junction(_box.Full("library/linked"), elsewhere);
        _box.Write("library/a.md", "mine", Earlier);

        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Move(_box.Data, "library/a.md", "library/linked/a.md", StatOf("library/a.md")));

        Assert.Equal("mine", _box.Read("library/a.md"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void A_stat_prints_its_size_and_no_time()
    {
        var one = new LocalStat(1234567, Earlier);
        var other = new LocalStat(1234567, Now);

        Assert.Equal("LocalStat { Size = 1234567 }", one.ToString());
        Assert.Equal(one.ToString(), other.ToString());
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:/")]
    [InlineData("C:")]
    [InlineData(@"c:\")]
    [InlineData(@"C:\.")]
    [InlineData(@"C:\data\..")]
    [InlineData(@"\\otter-server\share")]
    [InlineData(@"\\otter-server\share\")]
    [InlineData("//otter-server/share/")]
    [InlineData(@"\\otter-server\share\data\..")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\?\UNC\otter-server\share")]
    [InlineData("data")]
    [InlineData(@"\data")]
    [InlineData("C:data")]
    [InlineData(".")]
    [InlineData("")]
    // With a tilde in it a text is judged as it is written, and is given to no disk and no
    // server: a root stays a root, and what would have to be worked out is refused.
    [InlineData(@"\\otter-server\SHARE~1")]
    [InlineData(@"\\otter-server\SHARE~1\")]
    [InlineData(@"\\OTTER~1\share")]
    [InlineData(@"\\?\UNC\otter-server\SHARE~1")]
    [InlineData(@"\\otter-server\share\DATA~1\..")]
    [InlineData(@"\\otter-server\share\DATA~1\.")]
    [InlineData(@"C:\PROGRA~1\..")]
    [InlineData(@"C:\DATA~1\deeper\..\..")]
    [InlineData(@"C:\~\.. ")]
    [InlineData(@"C:\DATA~1.")]
    [InlineData("C:DATA~1")]
    [InlineData(@"\DATA~1")]
    [InlineData("~")]
    [InlineData(@"~\data")]
    public void The_root_of_a_drive_or_of_a_share_and_a_path_that_is_not_complete_are_no_data_folder(string text)
    {
        Assert.False(SyncFiles.IsDataFolder(text));
    }

    [Theory]
    [InlineData(@"C:\data")]
    [InlineData(@"C:\data\")]
    [InlineData(@"D:\Wyvern\data")]
    [InlineData(@"C:\Users\otter\AppData\Local\Wyvern\data")]
    [InlineData(@"\\otter-server\share\data")]
    [InlineData(@"\\otter-server\share\data\")]
    [InlineData(@"\\?\C:\data")]
    [InlineData(@"C:\DATA~1")]
    [InlineData(@"C:\DATA~1\")]
    [InlineData(@"C:/Users/OTTERP~1/AppData/Local/Wyvern/data")]
    [InlineData(@"D:\notes ~ drafts\data")]
    [InlineData(@"\\otter-server\share\DATA~1")]
    [InlineData(@"\\otter-server\share\DATA~1\")]
    [InlineData(@"\\?\C:\DATA~1")]
    public void Any_folder_below_the_root_of_a_drive_or_of_a_share_may_be_a_data_folder(string text)
    {
        Assert.True(SyncFiles.IsDataFolder(text));
    }

    // A short name, such as PROGRA~1, is one the system looks up on the disk, or on the server
    // of a share, when it is asked to make a path whole. The rule is decided from the text
    // alone, so a text with a tilde in it is not given to the system: it is judged as it is
    // written. That shows in what it then refuses, here on names inside the sandbox.
    [Fact]
    public void A_text_with_a_tilde_in_it_is_judged_as_it_is_written()
    {
        string folder = Path.Combine(_box.Root, "DATA~1");

        Assert.True(SyncFiles.IsDataFolder(folder));
        Assert.True(SyncFiles.IsDataFolder(folder + @"\"));
        Assert.True(SyncFiles.IsDataFolder(folder + @"\deeper"));

        // Not worked out, and so not taken for the folder they would come to.
        Assert.False(SyncFiles.IsDataFolder(folder + @"\."));
        Assert.False(SyncFiles.IsDataFolder(folder + @"\.."));
        Assert.False(SyncFiles.IsDataFolder(folder + @"\deeper\.."));
        Assert.False(SyncFiles.IsDataFolder(folder + @"\..\DATA~1"));

        // Names the system would change: a dot or a space at the end.
        Assert.False(SyncFiles.IsDataFolder(folder + "."));
        Assert.False(SyncFiles.IsDataFolder(folder + " "));
        Assert.False(SyncFiles.IsDataFolder(folder + @"\deeper. \x"));
        Assert.False(Directory.Exists(folder));
    }

    // Text only: the root of a drive is refused before anything is asked of the disk, so none
    // of this touches C:\. (The same for a share is covered by the rule above, not by these
    // calls, which would hand a share to a real file API if the rule did not hold.)
    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    public void Nothing_is_done_in_a_data_folder_that_is_the_root_of_a_drive(string root)
    {
        var anything = new LocalStat(0, Earlier);

        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Write(root, "library/a.md", Walrus, Earlier, expected: null, Now, out _, _ => { }));
        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Delete(root, "library/a.md", anything));
        Assert.Equal(FileOutcome.Unsafe, SyncFiles.Move(root, "library/a.md", "library/b.md", anything));
        Assert.Null(SyncFiles.Read(root, "library/a.md", out _));
        Assert.Null(SyncFiles.Stat(root, "library/a.md"));
    }
}
