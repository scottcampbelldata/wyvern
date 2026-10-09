using System.Diagnostics;
using System.Text;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>
/// Which clipboard folders are a machine's own after its name has changed, and that a delete
/// then takes an entry of the earlier folder off the disk and does not merely hide it.
/// Everything that is "copied" here is made up and exists only in a temporary folder.
/// </summary>
public sealed class ClipFoldersTests : IDisposable
{
    private const string Old = "OLD-OTTER";
    private const string New = "OTTER-PC";
    private const string NotUnderstood = "The list of this machine's clipboard folders could not be understood. It was left as it is, and this folder's name was not added.";
    private const string EarlierIsALink = "A folder of an earlier name of this machine is a link and is left alone";
    private const string EarlierNotScrubbed = "A folder of an earlier name of this machine could not be taken for scrubbing, so anything deleted from it may stay there for now";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly List<string> _reports = [];
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

    private string FoldersFile => _dir.File("clipboard-folders.json");

    private string ClipDir => _dir.File("clipboard");

    private string DayFile(string device) => Path.Combine(ClipDir, device, ClipPaths.DayFileName(ClipPaths.DayOf(_clock.UtcNow)));

    private string DayFolder(string device) => Path.Combine(ClipDir, device, ClipPaths.DayName(ClipPaths.DayOf(_clock.UtcNow)));

    private ClipStore Store(string device, params string[] earlier)
    {
        var store = new ClipStore(ClipDir, device, 90, _clock, (text, _) => _reports.Add(text), null, _ => { }, earlier);
        store.Load();
        return store;
    }

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

    /// <summary>Gives a folder and everything under it an old date, so that one that is written, created in or deleted from shows.</summary>
    private static void MakeOld(string folder)
    {
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, longAgo);
        }

        foreach (string directory in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(directory, longAgo);
        }

        Directory.SetLastWriteTimeUtc(folder, longAgo);
    }

    /// <summary>Everything under a folder and the folder itself: each folder with the time it was last written, and each file with its bytes and that time.</summary>
    private static SortedDictionary<string, string> Snapshot(string folder)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["."] = "a folder written " + Directory.GetLastWriteTimeUtc(folder).Ticks,
        };
        foreach (string directory in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories))
        {
            found[Path.GetRelativePath(folder, directory)] = "a folder written " + Directory.GetLastWriteTimeUtc(directory).Ticks;
        }

        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            found[Path.GetRelativePath(folder, file)] = Convert.ToHexString(File.ReadAllBytes(file)) + " written " + File.GetLastWriteTimeUtc(file).Ticks;
        }

        return found;
    }

    // The same, with the earlier folder moved out and linked back, so that the link leads to a
    // folder of this test's own (the second result) that holds what a scrub through the link
    // would change: a day file whose second entry has been deleted on another machine, and the
    // content that entry names. The folder behind the link has an old date throughout, so that
    // anything done in it shows. The first result is the id of the entry that is not deleted.
    private (string Plain, string Elsewhere) CopiedUnderTheEarlierNameBehindALink()
    {
        var (plain, withFile) = CopiedUnderTheEarlierName();
        string elsewhere = _dir.File("elsewhere");
        Directory.Move(Path.Combine(ClipDir, Old), elsewhere);
        Junction(Path.Combine(ClipDir, Old), elsewhere);
        using (var other = Store("SEAL-PC"))
        {
            Assert.True(other.Delete(withFile));
        }

        MakeOld(elsewhere);
        return (plain, elsewhere);
    }

    // What the machine copied while it still had its earlier name: a text, and a text with HTML,
    // whose HTML is a file of content in the day's folder.
    private (string Plain, string WithFile) CopiedUnderTheEarlierName()
    {
        using var store = Store(Old);
        string plain = store.Add(new ClipDraft("notepad.exe") { Text = "walrus one" })!.Id;
        _clock.Advance(TimeSpan.FromSeconds(5));
        string withFile = store.Add(new ClipDraft("msedge.exe") { Text = "otter two", Html = Encoding.UTF8.GetBytes("<b>otter two</b>") })!.Id;
        _clock.Advance(TimeSpan.FromSeconds(5));
        return (plain, withFile);
    }

    [Theory]
    [InlineData("OTTER-PC", true)]
    [InlineData("Otter PC (2)", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("NUL", false)]
    [InlineData("otter.", false)]
    [InlineData(" otter", false)]
    [InlineData("a-name-that-is-longer-than-the-forty-characters", false)]
    public void A_folder_name_is_exactly_what_a_device_name_is_made_into(string? name, bool expected)
    {
        Assert.Equal(expected, ClipFolders.IsFolderName(name));
    }

    [Fact]
    public void With_no_file_this_machine_has_written_under_no_name()
    {
        Assert.Empty(ClipFolders.Load(FoldersFile));
    }

    [Fact]
    public void A_name_is_remembered_once_in_the_order_the_names_came()
    {
        ClipFolders.Remember(FoldersFile, Old);
        ClipFolders.Remember(FoldersFile, New);
        ClipFolders.Remember(FoldersFile, "old-otter");
        ClipFolders.Remember(FoldersFile, New);

        Assert.Equal([Old, New], ClipFolders.Load(FoldersFile));
        Assert.False(File.Exists(FoldersFile + ".tmp"));
    }

    [Fact]
    public void The_file_is_one_a_person_can_read()
    {
        ClipFolders.Remember(FoldersFile, Old);
        ClipFolders.Remember(FoldersFile, New);

        Assert.Equal("{\n  \"folders\": [\n    \"OLD-OTTER\",\n    \"OTTER-PC\"\n  ]\n}", File.ReadAllText(FoldersFile).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"OTTER-PC\"]")]
    [InlineData("{ \"folders\": \"OTTER-PC\" }")]
    public void A_file_that_cannot_be_understood_gives_no_names(string content)
    {
        File.WriteAllText(FoldersFile, content);

        Assert.Empty(ClipFolders.Load(FoldersFile));
    }

    [Fact]
    public void Entries_that_are_not_a_folders_name_are_left_out_of_what_is_read()
    {
        File.WriteAllText(FoldersFile, """{ "folders": ["..", "OTTER-PC", 7, "a/b", "otter-pc", "C:\\Windows", "SEAL"] }""");

        Assert.Equal(["OTTER-PC", "SEAL"], ClipFolders.Load(FoldersFile));
    }

    [Fact]
    public void A_name_that_is_not_a_folders_name_is_not_remembered()
    {
        ClipFolders.Remember(FoldersFile, "..");
        ClipFolders.Remember(FoldersFile, "a\\b");

        Assert.False(File.Exists(FoldersFile));
    }

    [Fact]
    public void At_most_32_names_are_kept_and_the_oldest_go_first()
    {
        for (int i = 1; i <= 34; i++)
        {
            ClipFolders.Remember(FoldersFile, "PC-" + i);
        }

        var names = ClipFolders.Load(FoldersFile);

        Assert.Equal(32, names.Count);
        Assert.Equal("PC-3", names[0]);
        Assert.Equal("PC-34", names[^1]);
    }

    [Fact]
    public void A_list_that_cannot_be_written_is_reported_and_nothing_is_thrown()
    {
        Directory.CreateDirectory(FoldersFile + ".tmp");

        ClipFolders.Remember(FoldersFile, New, (text, error) => _reports.Add(text + (error is null ? "" : " with a cause")));

        Assert.Equal(["The list of this machine's clipboard folders could not be written with a cause"], _reports);
        Assert.Empty(ClipFolders.Load(FoldersFile));
    }

    // ---- A list that could not be read is never written over

    [Fact]
    public void A_list_that_is_not_there_is_missing_and_not_unreadable()
    {
        Assert.Equal(ClipFoldersState.Missing, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);

        // Nor is it there when the folder it would be in is not.
        Assert.Equal(ClipFoldersState.Missing, ClipFolders.TryLoad(_dir.File(Path.Combine("no-such-folder", "clipboard-folders.json")), out names));
        Assert.Empty(names);
    }

    [Fact]
    public void A_good_list_is_read_with_its_names_in_order_and_what_is_no_folders_name_left_out()
    {
        File.WriteAllText(FoldersFile, """{ "folders": ["OLD-OTTER", "..", "OTTER-PC"] }""");

        Assert.Equal(ClipFoldersState.Read, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Equal([Old, New], names);
    }

    [Fact]
    public void A_list_with_no_names_in_it_is_still_a_list()
    {
        File.WriteAllText(FoldersFile, """{ "folders": [] }""");

        Assert.Equal(ClipFoldersState.Read, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("not json")]
    [InlineData("[\"OTTER-PC\"]")]
    [InlineData("{}")]
    [InlineData("{ \"folders\": \"OTTER-PC\" }")]
    [InlineData("{ \"folders\": 5 }")]
    public void A_damaged_list_is_unreadable_and_gives_no_names(string content)
    {
        File.WriteAllText(FoldersFile, content);

        Assert.Equal(ClipFoldersState.Unreadable, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);
    }

    [Fact]
    public void A_list_of_zero_bytes_is_missing_and_not_unreadable()
    {
        // It holds no name, and Wyvern's own writer never leaves one: it is as good as not there.
        File.WriteAllBytes(FoldersFile, []);

        Assert.Equal(ClipFoldersState.Missing, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);
    }

    [Fact]
    public void A_name_is_written_into_a_list_of_zero_bytes_as_into_one_that_is_not_there()
    {
        File.WriteAllBytes(FoldersFile, []);

        ClipFolders.Remember(FoldersFile, New, Said);

        Assert.Equal(ClipFoldersState.Read, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Equal([New], names);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_list_saved_with_a_byte_order_mark_is_read_and_a_name_is_added_to_it_with_the_earlier_ones_kept()
    {
        // What an editor that marks its UTF-8 files leaves behind.
        File.WriteAllText(FoldersFile, """{ "folders": ["OLD-OTTER"] }""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(FoldersFile)[..3]);

        Assert.Equal(ClipFoldersState.Read, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Equal([Old], names);

        ClipFolders.Remember(FoldersFile, New, Said);

        Assert.Equal([Old, New], ClipFolders.Load(FoldersFile));
        Assert.Empty(_reports);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_list_saved_as_UTF_16_is_unreadable_and_is_not_written_over(bool withByteOrderMark)
    {
        File.WriteAllText(FoldersFile, """{ "folders": ["OLD-OTTER"] }""", new UnicodeEncoding(bigEndian: false, byteOrderMark: withByteOrderMark));
        byte[] before = File.ReadAllBytes(FoldersFile);

        Assert.Equal(ClipFoldersState.Unreadable, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);

        ClipFolders.Remember(FoldersFile, New, Said);

        Assert.Equal(before, File.ReadAllBytes(FoldersFile));
        Assert.Equal([NotUnderstood], _reports);
    }

    [Fact]
    public void A_list_that_is_nothing_but_a_byte_order_mark_is_unreadable()
    {
        // Not zero bytes, and not a list.
        File.WriteAllBytes(FoldersFile, [0xEF, 0xBB, 0xBF]);

        Assert.Equal(ClipFoldersState.Unreadable, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);
    }

    [Fact]
    public void A_list_larger_than_the_cap_is_unreadable_though_it_is_valid()
    {
        File.WriteAllText(FoldersFile, TooLargeButValid());

        Assert.Equal(ClipFoldersState.Unreadable, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);
    }

    [Fact]
    public void A_list_that_another_program_holds_open_is_unreadable()
    {
        File.WriteAllText(FoldersFile, """{ "folders": ["OLD-OTTER"] }""");
        using var holder = new FileStream(FoldersFile, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(ClipFoldersState.Unreadable, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Empty(names);
        Assert.Empty(ClipFolders.Load(FoldersFile));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData(" ")]
    [InlineData("[\"OLD-OTTER\"]")]
    [InlineData("{ \"names\": [\"OLD-OTTER\"] }")]
    [InlineData("{ \"folders\": 5 }")]
    public void A_name_is_not_written_over_a_list_that_cannot_be_understood_and_that_is_said_once(string content)
    {
        File.WriteAllText(FoldersFile, content);
        byte[] before = File.ReadAllBytes(FoldersFile);

        ClipFolders.Remember(FoldersFile, New, Said);

        Assert.Equal(before, File.ReadAllBytes(FoldersFile));
        Assert.Equal([NotUnderstood], _reports);
        Assert.False(File.Exists(FoldersFile + ".tmp"));
    }

    [Fact]
    public void A_name_is_not_written_over_a_list_larger_than_the_cap()
    {
        File.WriteAllText(FoldersFile, TooLargeButValid());
        byte[] before = File.ReadAllBytes(FoldersFile);

        ClipFolders.Remember(FoldersFile, New, Said);

        Assert.Equal(before, File.ReadAllBytes(FoldersFile));
        Assert.Equal([NotUnderstood], _reports);
        Assert.False(File.Exists(FoldersFile + ".tmp"));
    }

    [Fact]
    public void A_name_is_not_written_over_a_list_that_another_program_holds_open()
    {
        File.WriteAllText(FoldersFile, """{ "folders": ["OLD-OTTER"] }""");
        byte[] before = File.ReadAllBytes(FoldersFile);

        using (var holder = new FileStream(FoldersFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ClipFolders.Remember(FoldersFile, New, Said);
        }

        Assert.Equal(before, File.ReadAllBytes(FoldersFile));
        Assert.Equal([NotUnderstood], _reports);
        Assert.False(File.Exists(FoldersFile + ".tmp"));
    }

    [Fact]
    public void A_list_with_a_comment_and_a_trailing_comma_is_read_and_a_name_is_added_to_it_with_the_earlier_ones_kept()
    {
        File.WriteAllText(FoldersFile, "{\n  // the names this machine had before\n  \"folders\": [\n    \"OLD-OTTER\", // renamed in the spring\n    \"SEAL-PC\",\n  ],\n}");

        Assert.Equal(ClipFoldersState.Read, ClipFolders.TryLoad(FoldersFile, out var names));
        Assert.Equal([Old, "SEAL-PC"], names);

        ClipFolders.Remember(FoldersFile, New, Said);

        Assert.Equal([Old, "SEAL-PC", New], ClipFolders.Load(FoldersFile));
        Assert.Empty(_reports);
    }

    private void Said(string text, Exception? error) => _reports.Add(text + (error is null ? "" : " with a cause"));

    // Valid, so that it is the size alone that stops it being read.
    private static string TooLargeButValid() => "{ \"folders\": [\"" + Old + "\"], \"note\": \"" + new string('x', 70 * 1024) + "\" }";

    [Fact]
    public void A_delete_takes_an_entry_of_an_earlier_folder_off_the_disk()
    {
        var (plain, withFile) = CopiedUnderTheEarlierName();
        using var store = Store(New, Old);
        Assert.Single(Directory.GetFiles(DayFolder(Old)));

        Assert.True(store.Delete(withFile));

        string left = File.ReadAllText(DayFile(Old));
        Assert.DoesNotContain("otter two", left);
        Assert.Contains("walrus one", left);
        Assert.False(Directory.Exists(DayFolder(Old)));
        Assert.False(store.LastChangeHidForeignEntries);
        Assert.False(store.ScrubOwed);
        Assert.Equal([plain], store.Entries.Select(entry => entry.Id));
        Assert.Contains("\"kind\":\"delete\"", File.ReadAllText(DayFile(New)));
    }

    [Fact]
    public void Without_the_earlier_name_such_an_entry_is_only_hidden_as_before()
    {
        var (_, withFile) = CopiedUnderTheEarlierName();
        using var store = Store(New);

        Assert.True(store.Delete(withFile));

        Assert.Contains("otter two", File.ReadAllText(DayFile(Old)));
        Assert.Single(Directory.GetFiles(DayFolder(Old)));
        Assert.True(store.LastChangeHidForeignEntries);
    }

    [Fact]
    public void A_clear_scrubs_the_earlier_folders_as_well()
    {
        CopiedUnderTheEarlierName();
        using var store = Store(New, Old);

        Assert.True(store.Clear());

        Assert.Equal("", File.ReadAllText(DayFile(Old)));
        Assert.False(Directory.Exists(DayFolder(Old)));
        Assert.False(store.LastChangeHidForeignEntries);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void Reading_the_history_scrubs_an_earlier_folder_of_what_another_machine_deleted()
    {
        var (_, withFile) = CopiedUnderTheEarlierName();

        // The owner deleted the entry on the other machine, whose record of that has arrived.
        using (var other = Store("SEAL-PC"))
        {
            Assert.True(other.Delete(withFile));
            Assert.Contains("otter two", File.ReadAllText(DayFile(Old)));
        }

        using var store = Store(New, Old);

        Assert.DoesNotContain("otter two", File.ReadAllText(DayFile(Old)));
        Assert.False(Directory.Exists(DayFolder(Old)));
    }

    [Fact]
    public void An_earlier_folder_that_another_copy_of_Wyvern_takes_after_the_load_is_left_alone_by_a_delete_and_the_scrub_is_owed()
    {
        var (_, withFile) = CopiedUnderTheEarlierName();
        using var store = Store(New, Old);
        Assert.False(store.ScrubOwed);

        // The other copy takes the folder only now, when the store has loaded, so that what is
        // owed after the delete can only come from the delete's own scrub of that folder.
        using var holder = Store(Old);
        Assert.True(holder.IsWriter);

        Assert.True(store.Delete(withFile));

        Assert.Contains("otter two", File.ReadAllText(DayFile(Old)));
        Assert.False(store.LastChangeHidForeignEntries);
        Assert.True(store.ScrubOwed);

        // Once the other copy has let go, reading the history again makes it good.
        holder.Dispose();
        store.Load();

        Assert.DoesNotContain("otter two", File.ReadAllText(DayFile(Old)));
        Assert.False(store.ScrubOwed);
    }

    [Fact]
    public void What_is_copied_now_goes_into_the_folder_of_the_present_name_only()
    {
        CopiedUnderTheEarlierName();
        string before = File.ReadAllText(DayFile(Old));
        using var store = Store(New, Old);

        store.Add(new ClipDraft("notepad.exe") { Text = "seal three" });

        Assert.Equal(before, File.ReadAllText(DayFile(Old)));
        Assert.Contains("seal three", File.ReadAllText(DayFile(New)));
        Assert.False(File.Exists(Path.Combine(ClipDir, Old, ClipStore.LockFileName)));
    }

    [Fact]
    public void After_a_rename_a_copy_that_is_identical_to_the_newest_entry_of_the_earlier_folder_is_not_stored_again()
    {
        CopiedUnderTheEarlierName();
        using var store = Store(New, Old);

        // The newest entry of this machine is the second of the earlier folder.
        Assert.Null(store.Add(new ClipDraft("msedge.exe") { Text = "otter two", Html = Encoding.UTF8.GetBytes("<b>otter two</b>") }));
        Assert.False(File.Exists(DayFile(New)));
        Assert.Equal(2, store.Entries.Count);

        // Only the newest is looked at: a copy of the one before it is a new copy.
        Assert.NotNull(store.Add(new ClipDraft("notepad.exe") { Text = "walrus one" }));
        Assert.Equal(3, store.Entries.Count);
    }

    [Fact]
    public void Without_the_earlier_name_such_a_copy_is_stored_as_before()
    {
        CopiedUnderTheEarlierName();
        using var store = Store(New);

        Assert.NotNull(store.Add(new ClipDraft("msedge.exe") { Text = "otter two", Html = Encoding.UTF8.GetBytes("<b>otter two</b>") }));
        Assert.True(File.Exists(DayFile(New)));
    }

    [Fact]
    public void An_earlier_name_that_is_not_a_folders_name_is_passed_over_and_nothing_is_scrubbed_where_it_would_lead()
    {
        var (plain, withFile) = CopiedUnderTheEarlierName();

        // The owner deleted the second entry on another machine, and the record of that is on the
        // disk: the next load scrubs every folder that the store takes to be this machine's.
        using (var other = Store("SEAL-PC"))
        {
            Assert.True(other.Delete(withFile));
        }

        // Where each of these names leads when it is joined to the clipboard folder: "" is the
        // clipboard folder itself, "a\b" is a folder two down in it, and ".." is this test's own
        // folder. Each holds a copy of the earlier folder's day file with the deleted entry in it,
        // which is what a scrub of that folder would take the entry out of. All of it is inside
        // this test's temporary folder, so that nothing else can be reached.
        string dayFile = Path.GetFileName(DayFile(Old));
        string[] baits = [Path.Combine(ClipDir, dayFile), Path.Combine(ClipDir, "a", "b", dayFile), Path.Combine(_dir.Path, dayFile)];
        foreach (string bait in baits)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bait)!);
            File.Copy(DayFile(Old), bait);
        }

        using var store = Store(New, "..", "a\\b", "");

        byte[] original = File.ReadAllBytes(DayFile(Old));
        Assert.Contains("otter two", Encoding.UTF8.GetString(original));
        foreach (string bait in baits)
        {
            Assert.Equal(original, File.ReadAllBytes(bait));
        }

        // The folder of the earlier name is not in the list here, so it is not this machine's:
        // what is deleted from it is only hidden, as before.
        Assert.False(store.ScrubOwed);
        Assert.True(store.Delete(plain));
        Assert.True(store.LastChangeHidForeignEntries);
        Assert.Equal(original, File.ReadAllBytes(DayFile(Old)));
        foreach (string bait in baits)
        {
            Assert.Equal(original, File.ReadAllBytes(bait));
        }
    }

    [Fact]
    public void An_earlier_folder_that_is_a_link_is_not_scrubbed_through_and_counts_as_a_failure()
    {
        var (plain, elsewhere) = CopiedUnderTheEarlierNameBehindALink();
        var before = Snapshot(elsewhere);
        Assert.Contains(before, pair => pair.Key.EndsWith(".html", StringComparison.Ordinal));

        using var store = Store(New, Old);

        // The load tried the folder and could not: owed, and nothing was written, created or
        // deleted behind the link, not even a lock file that is let go of again at once (which
        // would still have given the folder a new date).
        Assert.True(store.ScrubOwed);
        Assert.Equal(before, Snapshot(elsewhere));

        // A delete of an entry in that folder is the same: it is gone from the history, and it
        // is owed, and the folder behind the link is as it was.
        Assert.True(store.Delete(plain));
        Assert.False(store.LastChangeHidForeignEntries);
        Assert.True(store.ScrubOwed);
        Assert.DoesNotContain(plain, store.Entries.Select(entry => entry.Id));
        Assert.Equal(before, Snapshot(elsewhere));
    }

    [Fact]
    public void An_earlier_folder_that_is_a_link_is_told_once_however_often_it_is_scrubbed()
    {
        var (plain, _) = CopiedUnderTheEarlierNameBehindALink();

        using var store = Store(New, Old);
        store.Load();
        Assert.True(store.Delete(plain));
        store.Load();

        Assert.Single(_reports, text => text == EarlierIsALink);
        Assert.DoesNotContain(_reports, text => text.Contains(Old, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_earlier_folder_that_another_copy_holds_is_told_once_however_often_it_is_scrubbed()
    {
        var (plain, _) = CopiedUnderTheEarlierName();
        using var store = Store(New, Old);
        using var holder = Store(Old);
        Assert.True(holder.IsWriter);
        Assert.DoesNotContain(EarlierNotScrubbed, _reports);

        store.Load();
        Assert.True(store.Delete(plain));
        store.Load();

        Assert.True(store.ScrubOwed);
        Assert.Single(_reports, text => text == EarlierNotScrubbed);
        Assert.DoesNotContain(_reports, text => text.Contains(Old, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_two_kinds_of_failure_of_an_earlier_folder_are_each_told_once()
    {
        CopiedUnderTheEarlierNameBehindALink();
        using (var other = Store("SEAL-OLD"))
        {
            other.Add(new ClipDraft("notepad.exe") { Text = "seal three" });
        }

        using var store = Store(New, Old, "SEAL-OLD");
        using var holder = Store("SEAL-OLD");
        Assert.True(holder.IsWriter);
        store.Load();
        store.Load();

        Assert.Single(_reports, text => text == EarlierIsALink);
        Assert.Single(_reports, text => text == EarlierNotScrubbed);
    }

    [Theory]
    [InlineData("OTTER-PC")]
    [InlineData("otter-pc")]
    public void The_present_name_among_the_earlier_names_is_not_scrubbed_through_a_second_handle_on_its_own_lock(string spelling)
    {
        CopiedUnderTheEarlierName();

        // The steady state after the first run: the file holds the earlier name and the present
        // one, and the present name is also what the store is given as its own.
        using var store = Store(New, Old, spelling);

        Assert.True(store.IsWriter);
        Assert.False(store.ScrubOwed);

        // And it stays so: the store's own folder is scrubbed as the one it holds, every time.
        var added = store.Add(new ClipDraft("notepad.exe") { Text = "seal three" });
        Assert.NotNull(added);
        Assert.True(store.Delete(added.Id));
        store.Load();

        Assert.True(store.IsWriter);
        Assert.False(store.ScrubOwed);
    }

    [Fact]
    public async Task The_history_service_remembers_the_name_of_the_folder_it_took()
    {
        using var service = new ClipHistoryService(_clock, foldersFile: FoldersFile);

        service.Configure(ClipDir, Options(New));
        await service.Idle.WaitAsync(Limit);

        Assert.True(service.IsWriter);
        Assert.Equal([New], ClipFolders.Load(FoldersFile));
    }

    [Fact]
    public async Task A_copy_that_may_not_write_the_folder_does_not_claim_its_name()
    {
        using var holder = Store(New);
        using var service = new ClipHistoryService(_clock, foldersFile: FoldersFile, pause: _ => { });

        service.Configure(ClipDir, Options(New));
        await service.Idle.WaitAsync(Limit);

        Assert.False(service.IsWriter);
        Assert.Empty(ClipFolders.Load(FoldersFile));
    }

    [Fact]
    public async Task After_a_change_of_name_a_delete_through_the_service_scrubs_the_earlier_folder_and_tells_of_no_files_that_stay()
    {
        var notes = new List<string>();
        using var service = new ClipHistoryService(_clock, notify: note => { lock (notes) { notes.Add(note); } }, foldersFile: FoldersFile);
        service.Configure(ClipDir, Options(Old));
        string? id = null;
        service.Post(store => id = store.Add(new ClipDraft("notepad.exe") { Text = "walrus one" })!.Id);
        await service.Idle.WaitAsync(Limit);
        _clock.Advance(TimeSpan.FromSeconds(5));

        service.Configure(ClipDir, Options(New));
        service.Delete(id!);
        await service.Idle.WaitAsync(Limit);

        Assert.Equal([Old, New], ClipFolders.Load(FoldersFile));
        Assert.DoesNotContain("walrus one", File.ReadAllText(DayFile(Old)));
        Assert.Empty(service.Current);
        lock (notes)
        {
            Assert.DoesNotContain(ClipHistoryService.ForeignFilesNote, notes);
        }
    }

    private static ClipboardOptions Options(string device) =>
        ClipboardOptions.From(new WyvernSettings { DeviceName = System.Text.Json.JsonSerializer.SerializeToElement(device) }, "MADE-UP-MACHINE");
}
