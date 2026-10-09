using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>
/// The rules for paths. A path can arrive from the server inside a blob, and it becomes the name
/// of a file that is written or deleted, so what <c>TryParse</c> refuses matters most. Nothing
/// here touches a disk: these are rules about text.
/// </summary>
public class SyncPathTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 14, 5, 9, TimeSpan.Zero);

    [Theory]
    [InlineData("library/quicklinks.json")]
    [InlineData("library/snippets/work/sig.md")]
    [InlineData("clipboard/OTTER-PC/2026-10-03.jsonl")]
    [InlineData("clipboard/Otter PC/2026-10-03/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.png")]
    [InlineData("notes.txt")]
    [InlineData("library/snippets/r\u00E9sum\u00E9 (2).md")]
    public void An_ordinary_relative_path_is_accepted_as_it_is(string text)
    {
        Assert.True(SyncPath.TryParse(text, out string path));
        Assert.Equal(text, path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/library/a.md")]                 // rooted
    [InlineData("library//a.md")]                 // an empty name
    [InlineData("library/a.md/")]                 // ends in a slash
    [InlineData("library\\a.md")]                 // a backslash
    [InlineData("C:/Windows/a.md")]               // a drive
    [InlineData("C:a.md")]                        // a drive without a root
    [InlineData("../a.md")]                       // up and out
    [InlineData("library/../../a.md")]
    [InlineData("library/./a.md")]
    [InlineData("library/a.md:stream")]           // an alternate data stream
    [InlineData("library/NUL")]                   // a device
    [InlineData("library/nul.txt")]
    [InlineData("library/COM1/a.md")]
    [InlineData("library/a.md.")]                 // Windows would drop the dot
    [InlineData("library/a.md ")]                 // and the space
    [InlineData("library/ a.md")]
    [InlineData("library/a?.md")]
    [InlineData("library/a*.md")]
    [InlineData("library/a|b.md")]
    [InlineData("library/a\"b.md")]
    [InlineData("library/a<b>.md")]
    [InlineData("library/a\tb.md")]               // a control character
    [InlineData("//server/share/a.md")]           // a share
    [InlineData("//./pipe/a")]                    // a device path
    [InlineData("//?/C:/a.md")]
    public void Anything_that_could_leave_the_data_folder_or_name_something_else_is_refused(string? text)
    {
        Assert.False(SyncPath.TryParse(text, out string path));
        Assert.Equal("", path);
    }

    [Fact]
    public void A_name_that_holds_half_of_a_surrogate_pair_is_refused()
    {
        string broken = "library/a" + (char)0xD83D + ".md";

        Assert.False(SyncPath.TryParse(broken, out _));
    }

    [Fact]
    public void A_path_or_a_name_that_is_too_long_is_refused()
    {
        string longName = "library/" + new string('a', SyncLimits.MaxNameLength + 1);
        string manyNames = string.Join('/', Enumerable.Repeat("abcdefghi", 41)); // 409 characters

        Assert.False(SyncPath.TryParse(longName, out _));
        Assert.False(SyncPath.TryParse(manyNames, out _));
        Assert.True(SyncPath.TryParse("library/" + new string('a', SyncLimits.MaxNameLength), out _));
    }

    [Fact]
    public void A_path_of_exactly_400_characters_is_accepted_and_one_of_401_is_not()
    {
        string at = Folders(39) + new string('z', 10);
        string over = Folders(39) + new string('z', 11);
        Assert.Equal(400, at.Length);
        Assert.Equal(401, over.Length);

        Assert.True(SyncPath.TryParse(at, out string accepted));
        Assert.Equal(at, accepted);
        Assert.False(SyncPath.TryParse(over, out string refused));
        Assert.Equal("", refused);
    }

    [Fact]
    public void A_name_of_exactly_255_characters_is_accepted_and_one_of_256_is_not()
    {
        Assert.True(SyncPath.TryParse(new string('a', 255), out _));
        Assert.False(SyncPath.TryParse(new string('a', 256), out _));
        Assert.True(SyncPath.TryParse("library/" + new string('a', 255), out _));
        Assert.False(SyncPath.TryParse("library/" + new string('a', 256), out _));
    }

    [Fact]
    public void A_clipboard_path_prints_neither_the_machine_nor_the_day()
    {
        var path = new ClipboardPath("OTTER-PC", new DateOnly(2026, 10, 3));

        string printed = path.ToString();

        Assert.Equal("ClipboardPath", printed);
        Assert.DoesNotContain("OTTER", printed);
        Assert.DoesNotContain("2026", printed);
        Assert.DoesNotContain("10", printed);
    }

    [Fact]
    public void Two_spellings_of_one_file_have_the_same_key()
    {
        Assert.Equal("library/snippets/sig.md", SyncPath.Key("Library/Snippets/Sig.MD"));
        Assert.Equal(SyncPath.Key("clipboard/OTTER-PC/a.txt"), SyncPath.Key("clipboard/otter-pc/A.TXT"));
    }

    [Theory]
    [InlineData("library/snippets/sig.md", true)]
    [InlineData("clipboard/OTTER-PC/2026-10-03.jsonl", true)]
    [InlineData("library/tmp", true)]                              // a name, not an ending
    [InlineData("library/a.tmpl", true)]
    [InlineData("clipboard/OTTER-PC/2026-10-03.jsonl.tmp", false)] // being written
    [InlineData("library/a.TMP", false)]
    [InlineData("clipboard/OTTER-PC/.~lock", false)]               // the store's lock file
    [InlineData("library/~$letter.docx", false)]                   // an editor's lock file
    [InlineData("library/.~work/a.md", false)]                     // in a folder with such a name
    [InlineData("library/old.tmp/a.md", false)]
    public void Temporary_files_and_lock_files_are_not_synced(string path, bool synced)
    {
        Assert.Equal(synced, SyncPath.IsSynced(path));
    }

    [Fact]
    public void A_path_is_joined_to_the_data_folder_with_the_separators_of_Windows()
    {
        Assert.Equal(@"D:\Beacon\data\library\snippets\sig.md", SyncPath.ToLocal(@"D:\Beacon\data", "library/snippets/sig.md"));
    }

    [Fact]
    public void A_day_file_and_a_file_in_a_day_folder_belong_to_a_machine_and_a_day()
    {
        var day = new DateOnly(2026, 10, 3);

        Assert.Equal(new ClipboardPath("OTTER-PC", day), SyncPath.Clipboard("clipboard/OTTER-PC/2026-10-03.jsonl"));
        Assert.Equal(new ClipboardPath("OTTER-PC", day), SyncPath.Clipboard("clipboard/OTTER-PC/2026-10-03/abc.png"));
        Assert.Equal(new ClipboardPath("OTTER-PC", day), SyncPath.Clipboard("Clipboard/OTTER-PC/2026-10-03/deeper/abc.png"));
    }

    [Fact]
    public void Any_other_file_in_a_machines_folder_belongs_to_that_machine_and_to_no_day()
    {
        Assert.Equal(new ClipboardPath("OTTER-PC", null), SyncPath.Clipboard("clipboard/OTTER-PC/notes.txt"));
        Assert.Equal(new ClipboardPath("OTTER-PC", null), SyncPath.Clipboard("clipboard/OTTER-PC/2026-13-45.jsonl"));
        Assert.Equal(new ClipboardPath("OTTER-PC", null), SyncPath.Clipboard("clipboard/OTTER-PC/pictures/abc.png"));
    }

    [Theory]
    [InlineData("library/snippets/sig.md")]
    [InlineData("clipboard/readme.txt")]
    [InlineData("clipboard")]
    [InlineData("clipboards/OTTER-PC/2026-10-03.jsonl")]
    public void A_path_outside_every_machines_clipboard_folder_is_not_a_clipboard_path(string path)
    {
        Assert.Null(SyncPath.Clipboard(path));
    }

    [Fact]
    public void A_conflict_copy_is_named_beside_the_file_and_keeps_its_extension()
    {
        Assert.Equal(
            "library/snippets/sig.conflict-OTTER-PC-20261003-140509.md",
            SyncPath.ConflictCopy("library/snippets/sig.md", "OTTER-PC", Time));
        Assert.Equal(
            "library/quicklinks.conflict-Otter PC-20261003-140509-3.json",
            SyncPath.ConflictCopy("library/quicklinks.json", "Otter PC", Time, number: 3));
        Assert.Equal("README.conflict-OTTER-PC-20261003-140509", SyncPath.ConflictCopy("README", "OTTER-PC", Time));
    }

    [Fact]
    public void The_time_in_a_conflict_copys_name_is_in_UTC()
    {
        var local = new DateTimeOffset(2026, 10, 3, 16, 5, 9, TimeSpan.FromHours(2));

        Assert.Equal("a.conflict-PC-20261003-140509.md", SyncPath.ConflictCopy("a.md", "PC", local));
    }

    [Fact]
    public void A_conflict_copy_of_a_file_with_a_very_long_name_is_still_a_path_sync_accepts()
    {
        string path = "library/snippets/" + new string('w', 240) + ".md";

        string? copy = SyncPath.ConflictCopy(path, new string('D', 40), Time, number: 12);

        Assert.NotNull(copy);
        Assert.True(SyncPath.TryParse(copy, out _));
        Assert.True(SyncPath.IsConflictCopy(NameOf(copy)));
        Assert.EndsWith(".md", copy);
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string FolderOf(string path) => path[..(path.LastIndexOf('/') + 1)];

    // Whole folder names of nine characters and a slash, so that a path is as long as a test needs.
    private static string Folders(int count) => string.Concat(Enumerable.Repeat("abcdefghi/", count));

    [Fact]
    public void A_conflict_copy_of_a_name_that_starts_with_many_dots_throws_nothing_and_is_a_path_sync_accepts()
    {
        string path = "library/" + new string('.', 130) + "a.md";
        Assert.True(SyncPath.TryParse(path, out _));

        string? copy = SyncPath.ConflictCopy(path, "PC", Time);

        Assert.True(copy is null || SyncPath.TryParse(copy, out _));
    }

    [Fact]
    public void A_stem_that_is_cut_to_nothing_leaves_a_copy_whose_name_starts_with_the_mark()
    {
        // The 120 characters that are kept are all dots, and the dots are trimmed away.
        string path = "library/" + new string('.', 130) + "a.md";

        Assert.Equal("library/.conflict-PC-20261003-140509.md", SyncPath.ConflictCopy(path, "PC", Time));
        Assert.Equal(".conflict-PC-20261003-140509.gitignore", SyncPath.ConflictCopy(".gitignore", "PC", Time));
    }

    [Fact]
    public void A_very_long_extension_is_no_extension_and_the_name_of_the_copy_keeps_to_the_longest_name()
    {
        // ".xxxx..." of 201 characters is not an extension; kept whole it made a name of 268.
        string path = "library/snippets/1." + new string('x', 200);
        Assert.True(SyncPath.TryParse(path, out _));

        string? copy = SyncPath.ConflictCopy(path, new string('D', 40), Time);

        Assert.NotNull(copy);
        Assert.True(SyncPath.TryParse(copy, out _));
        Assert.True(NameOf(copy).Length <= SyncLimits.MaxNameLength);
        Assert.StartsWith("library/snippets/1.", copy);
        Assert.True(SyncPath.IsConflictCopy(NameOf(copy)));
    }

    [Fact]
    public void An_extension_of_sixteen_characters_is_kept_and_one_of_seventeen_is_not()
    {
        string sixteen = "library/" + new string('a', 20) + "." + new string('b', 15);
        string seventeen = "library/" + new string('a', 20) + "." + new string('b', 16);

        string? kept = SyncPath.ConflictCopy(sixteen, "PC", Time);
        string? cut = SyncPath.ConflictCopy(seventeen, "PC", Time);

        Assert.Equal("library/" + new string('a', 20) + ".conflict-PC-20261003-140509." + new string('b', 15), kept);
        Assert.Equal("library/" + new string('a', 20) + "." + new string('b', 16) + ".conflict-PC-20261003-140509", cut);
    }

    [Fact]
    public void A_copy_that_would_make_the_path_longer_than_400_is_made_shorter_by_cutting_the_stem()
    {
        // A folder of 300 and a name of 83: the copy has 31 characters of its own (the mark, "PC",
        // a dash, the time and the extension), so 69 characters of the stem are what is left.
        string path = Folders(30) + new string('n', 80) + ".md";

        string? copy = SyncPath.ConflictCopy(path, "PC", Time);

        Assert.NotNull(copy);
        Assert.Equal(SyncLimits.MaxPathLength, copy.Length);
        Assert.True(SyncPath.TryParse(copy, out _));
        Assert.Equal(Folders(30) + new string('n', 69) + ".conflict-PC-20261003-140509.md", copy);
    }

    [Fact]
    public void A_path_of_387_characters_gets_a_copy_that_fits_or_none_and_never_one_over_400()
    {
        string path = Folders(37) + new string('n', 14) + ".md";
        Assert.Equal(387, path.Length);

        string? copy = SyncPath.ConflictCopy(path, new string('D', 40), Time);

        Assert.True(copy is null || (copy.Length <= SyncLimits.MaxPathLength && SyncPath.TryParse(copy, out _)));
    }

    [Fact]
    public void When_no_name_fits_there_is_no_copy()
    {
        // 380 characters of folders leave 20 for the name, and the copy's own part is 69.
        string path = Folders(38) + "a.md";
        Assert.True(SyncPath.TryParse(path, out _));

        Assert.Null(SyncPath.ConflictCopy(path, new string('D', 40), Time));
        Assert.Null(SyncPath.ConflictCopy("a.md", new string('D', 300), Time));
    }

    [Fact]
    public void A_long_device_name_takes_the_room_of_the_stem_and_the_name_ends_at_255()
    {
        string path = "library/" + new string('w', 200) + ".md";

        string? copy = SyncPath.ConflictCopy(path, new string('D', 120), Time);

        Assert.NotNull(copy);
        Assert.Equal(SyncLimits.MaxNameLength, NameOf(copy).Length);
        Assert.True(SyncPath.TryParse(copy, out _));
    }

    [Fact]
    public void A_cut_in_the_middle_of_a_surrogate_pair_drops_the_half_that_is_left()
    {
        // 'a' and then pairs: the 120 characters kept end in the first half of a pair.
        string path = "library/a" + string.Concat(Enumerable.Repeat("\U0001F600", 80)) + ".md";
        Assert.True(SyncPath.TryParse(path, out _));

        string? copy = SyncPath.ConflictCopy(path, "PC", Time);

        Assert.Equal("library/a" + string.Concat(Enumerable.Repeat("\U0001F600", 59)) + ".conflict-PC-20261003-140509.md", copy);
    }

    public static TheoryData<string> PathsThatTryParseAccepts()
    {
        string pairs = string.Concat(Enumerable.Repeat("\U0001F600", 100));
        return new TheoryData<string>
        {
            "a.md",
            "x",
            ".gitignore",
            "library/snippets/sig.md",
            "library/snippets/archive.tar.gz",
            "library/snippets/résumé (2).md",
            "clipboard/Otter PC/2026-10-03/" + new string('0', 64) + ".png",
            "library/" + new string('w', 240) + ".md",                              // a long stem
            "library/" + new string('k', 252) + ".md",                               // a name of exactly 255
            "library/1." + new string('x', 200),                                     // a long extension
            "library/" + new string('a', 20) + "." + new string('b', 15),            // an extension of 16
            "library/" + new string('a', 20) + "." + new string('b', 16),            // an extension of 17
            "library/notes. b",                                                      // a space in the extension
            "library/" + new string('.', 130) + "a.md",                              // dots at the start
            "library/" + new string('.', 254) + "a",
            "library/" + string.Concat(Enumerable.Repeat("a. ", 60)) + "z.md",       // dots and spaces inside
            "library/" + string.Concat(Enumerable.Repeat("x .", 60)) + "y.md",
            "library/a" + string.Concat(Enumerable.Repeat("\U0001F600", 80)) + ".md", // a pair across the cut at 120
            "library/" + pairs + ".md",                                              // a pair across the cut at any odd length
            "library/b" + pairs + "tail.md",
            Folders(30) + new string('n', 80) + ".md",                                // the path decides the room
            Folders(32) + "abcdefg/" + "contractor-notes.md",                         // a room that cuts the stem to "con"
            Folders(34) + new string('q', 50) + ".json",
            Folders(37) + new string('n', 14) + ".md",                                // 387
            Folders(38) + "a.md",                                                    // no copy fits
            Folders(39) + new string('z', 7) + ".md",                                // 400
        };
    }

    [Theory]
    [MemberData(nameof(PathsThatTryParseAccepts))]
    public void A_conflict_copy_is_a_path_sync_accepts_in_the_same_folder_and_is_known_as_a_copy_or_there_is_none(string path)
    {
        Assert.True(SyncPath.TryParse(path, out _), "the made-up path itself must be one sync accepts");

        foreach (string device in new[] { "D", new string('D', 40) })
        {
            foreach (int number in new[] { 1, 2, 12 })
            {
                string? copy = SyncPath.ConflictCopy(path, device, Time, number);
                if (copy is null)
                {
                    continue;
                }

                string where = $"a path of {path.Length}, a device of {device.Length}, number {number}";
                Assert.True(SyncPath.TryParse(copy, out _), where);
                Assert.Equal(FolderOf(path), FolderOf(copy));
                Assert.True(SyncPath.IsConflictCopy(NameOf(copy)), where);
            }
        }
    }

    [Theory]
    [InlineData("sig.conflict-OTTER-PC-20261003-140509.md", true)]
    [InlineData("sig.conflict-Otter PC-20261003-140509-2.md", true)]
    [InlineData("deploy.conflict-a-b-c-20261003-140509.ps1", true)]
    [InlineData("README.conflict-PC-20261003-140509", true)]
    [InlineData("sig.md", false)]
    [InlineData("conflict-notes.md", false)]
    [InlineData("sig.conflict-PC.md", false)]
    [InlineData("sig.conflict-PC-2026-10-03.md", false)]
    [InlineData("sig.conflict-PC-20261003-140509.md.bak", false)]
    [InlineData("sig.conflict-PC-٢٠٢٦١٠٠٣-140509.md", false)]    // Arabic-Indic digits in the date
    [InlineData("sig.conflict-PC-20261003-١٤٠٥٠٩.md", false)]              // in the time
    [InlineData("sig.conflict-PC-20261003-140509-٢.md", false)]                                    // in the count
    [InlineData("README.conflict-PC-20261003-140509\n", false)]                                         // a line feed after the name
    public void A_conflict_copy_is_known_by_its_name(string name, bool isCopy)
    {
        Assert.Equal(isCopy, SyncPath.IsConflictCopy(name));
    }

    [Fact]
    public void Every_name_ConflictCopy_makes_is_one_IsConflictCopy_knows()
    {
        foreach (string path in new[] { "a.md", "library/scripts/deploy.ps1", "library/quicklinks.json", "x" })
        {
            string? copy = SyncPath.ConflictCopy(path, "My-PC.2", Time, number: 2);

            Assert.NotNull(copy);
            Assert.True(SyncPath.IsConflictCopy(NameOf(copy)), copy);
        }
    }
}
