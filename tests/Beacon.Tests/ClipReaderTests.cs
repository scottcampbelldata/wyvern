using System.Text;
using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

/// <summary>
/// Each test builds the folders it needs in a temporary folder of its own: the clipboard folder,
/// in it a folder for each machine, and in that the file of each day.
/// </summary>
public sealed class ClipReaderTests : IDisposable
{
    private const string Otter = "OTTER-PC";
    private const string Walrus = "WALRUS-PC";

    // Every lower-case hex digit, four times over: the 64 characters a SHA-256 is written in.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // The name of the clipboard folder in these tests. No report may hold it.
    private const string ClipboardFolderName = "walrus-store";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Clipboard => _dir.File(ClipboardFolderName);

    private static DateTimeOffset At(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 10, day, hour, minute, second, TimeSpan.Zero);

    private static string TextLine(string id, DateTimeOffset time, string text = "walrus") =>
        ClipLine.Write(new EntryRecord(id, time, "otter.exe") { Text = text, TextLength = text.Length });

    private static string DeleteLine(string id, DateTimeOffset time, string target) =>
        ClipLine.Write(new DeleteRecord(id, time, target));

    private static string ClearLine(string id, DateTimeOffset time, DateTimeOffset before) =>
        ClipLine.Write(new ClearRecord(id, time, before));

    private string DeviceDir(string device)
    {
        string folder = Path.Combine(Clipboard, device);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes a file into a machine's folder as UTF-8 with no byte order mark, exactly as given.</summary>
    private string WriteContent(string device, string name, string content)
    {
        string file = Path.Combine(DeviceDir(device), name);
        File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return file;
    }

    /// <summary>Writes a file into a machine's folder: each line with a line feed after it.</summary>
    private string WriteFile(string device, string name, params string[] lines) =>
        WriteContent(device, name, string.Concat(lines.Select(line => line + "\n")));

    private static IEnumerable<string> Ids(ClipLoad load) => load.Entries.Select(entry => entry.Id);

    private static ClipEntry EntryOn(string device, string id, DateTimeOffset time) =>
        new(id, time, device, new DateOnly(2026, 10, 3), @"C:\Beacon\clipboard\" + device + @"\2026-10-03");

    // The lines of one file.

    [Fact]
    public void Lines_are_numbered_from_1_and_given_as_written_with_what_each_says()
    {
        string first = TextLine("o1", At(3, 9));
        string second = DeleteLine("o2", At(3, 10), "o1");
        string third = ClearLine("o3", At(3, 11), At(3, 8));
        string file = WriteFile(Otter, "2026-10-03.jsonl", first, second, third);

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal([1, 2, 3], lines.Select(line => line.Number));
        Assert.Equal([first, second, third], lines.Select(line => line.Raw));
        Assert.Equal("o1", Assert.IsType<EntryRecord>(lines[0].Record).Id);
        Assert.Equal(new DeleteRecord("o2", At(3, 10), "o1"), lines[1].Record);
        Assert.Equal(new ClearRecord("o3", At(3, 11), At(3, 8)), lines[2].Record);
    }

    [Fact]
    public void A_blank_line_has_no_record_and_is_marked_as_blank()
    {
        string file = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), "", "   ", "\t", TextLine("o2", At(3, 10)));

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal([1, 2, 3, 4, 5], lines.Select(line => line.Number));
        Assert.Equal([false, true, true, true, false], lines.Select(line => line.IsBlank));
        Assert.Equal([false, true, true, true, false], lines.Select(line => line.Record is null));
        Assert.Equal("   ", lines[2].Raw);
    }

    [Fact]
    public void A_line_that_cannot_be_read_has_no_record_and_is_not_marked_as_blank()
    {
        string file = WriteFile(
            Otter,
            "2026-10-03.jsonl",
            TextLine("o1", At(3, 9)),
            "not json",
            """{"id":"o2"}""",
            TextLine("o3", At(3, 10)));

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal([1, 2, 3, 4], lines.Select(line => line.Number));
        Assert.Equal([false, true, true, false], lines.Select(line => line.Record is null));
        Assert.All(lines, line => Assert.False(line.IsBlank));
        Assert.Equal("not json", lines[1].Raw);
        Assert.Equal("o3", lines[3].Record?.Id);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void A_line_ends_at_a_line_feed_with_or_without_a_carriage_return_before_it(string lineBreak)
    {
        string first = TextLine("o1", At(3, 9));
        string second = TextLine("o2", At(3, 10));
        string file = WriteContent(Otter, "2026-10-03.jsonl", first + lineBreak + second + lineBreak);

        var lines = ClipReader.ReadLines(file).ToList();

        // The carriage return is part of the line break, not of the line.
        Assert.Equal([first, second], lines.Select(line => line.Raw));
        Assert.Equal(["o1", "o2"], lines.Select(line => line.Record?.Id));
    }

    [Fact]
    public void A_file_may_mix_both_kinds_of_line_break()
    {
        string first = TextLine("o1", At(3, 9));
        string second = TextLine("o2", At(3, 10));
        string third = TextLine("o3", At(3, 11));
        string file = WriteContent(Otter, "2026-10-03.jsonl", first + "\r\n" + second + "\n" + third + "\r\n");

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal([first, second, third], lines.Select(line => line.Raw));
    }

    [Fact]
    public void A_last_line_without_a_line_break_is_read()
    {
        string first = TextLine("o1", At(3, 9));
        string second = TextLine("o2", At(3, 10));
        string file = WriteContent(Otter, "2026-10-03.jsonl", first + "\n" + second);

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal([first, second], lines.Select(line => line.Raw));
        Assert.Equal("o2", lines[1].Record?.Id);
    }

    [Fact]
    public void A_line_break_at_the_end_of_the_file_does_not_make_one_more_line()
    {
        string file = WriteContent(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)) + "\n");

        Assert.Single(ClipReader.ReadLines(file));
    }

    [Fact]
    public void An_empty_file_has_no_lines()
    {
        string file = WriteContent(Otter, "2026-10-03.jsonl", "");

        Assert.Empty(ClipReader.ReadLines(file));
    }

    [Fact]
    public void A_byte_order_mark_at_the_start_of_the_file_is_not_part_of_the_first_line()
    {
        string first = TextLine("o1", At(3, 9));
        string file = Path.Combine(DeviceDir(Otter), "2026-10-03.jsonl");
        File.WriteAllText(file, first + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(file)[0]);

        var line = Assert.Single(ClipReader.ReadLines(file));

        Assert.Equal(first, line.Raw);
        Assert.Equal("o1", line.Record?.Id);
    }

    [Fact]
    public void Text_outside_ascii_is_read_as_utf_8()
    {
        // An "e" with an acute accent, which is two bytes in UTF-8, and an emoji, which is four.
        // The line is put together by hand so that the file holds those bytes and not escapes.
        string text = "caf" + (char)0xE9 + " " + char.ConvertFromUtf32(0x1F600);
        string line = "{\"id\":\"o1\",\"ts\":\"2026-10-03T09:00:00Z\",\"kind\":\"text\",\"text\":\"" + text + "\"}";
        string file = WriteFile(Otter, "2026-10-03.jsonl", line);
        Assert.Contains((byte)0xC3, File.ReadAllBytes(file));
        Assert.Contains((byte)0xF0, File.ReadAllBytes(file));

        var read = Assert.Single(ClipReader.ReadLines(file));

        Assert.Equal(line, read.Raw);
        Assert.Equal(text, Assert.IsType<EntryRecord>(read.Record).Text);
    }

    [Fact]
    public void A_byte_that_is_not_utf_8_does_not_stop_the_file_being_read()
    {
        // 0xFF never appears in UTF-8. It stands in the middle of the first line's text.
        string first = TextLine("o1", At(3, 9), "wal?rus");
        byte[] bytes = Encoding.UTF8.GetBytes(first + "\n" + TextLine("o2", At(3, 10)) + "\n");
        bytes[Array.IndexOf(bytes, (byte)'?')] = 0xFF;
        string file = Path.Combine(DeviceDir(Otter), "2026-10-03.jsonl");
        File.WriteAllBytes(file, bytes);

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("wal" + (char)0xFFFD + "rus", Assert.IsType<EntryRecord>(lines[0].Record).Text);
        Assert.Equal("o2", lines[1].Record?.Id);
    }

    [Fact]
    public void Bytes_that_spell_half_a_surrogate_pair_are_never_read_as_one()
    {
        // 0xED 0xA0 0xBD is how U+D83D would be written if UTF-8 allowed it, which it does not.
        // A line that held half a pair as a character could not be read as JSON at all.
        string first = TextLine("o1", At(3, 9), "wal???rus");
        byte[] bytes = Encoding.UTF8.GetBytes(first + "\n" + TextLine("o2", At(3, 10)) + "\n");
        int at = Array.IndexOf(bytes, (byte)'?');
        bytes[at] = 0xED;
        bytes[at + 1] = 0xA0;
        bytes[at + 2] = 0xBD;
        string file = Path.Combine(DeviceDir(Otter), "2026-10-03.jsonl");
        File.WriteAllBytes(file, bytes);

        var lines = ClipReader.ReadLines(file).ToList();

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines[0].Raw, char.IsSurrogate);
        Assert.Equal("o1", lines[0].Record?.Id);
        Assert.Equal("o2", lines[1].Record?.Id);
    }

    [Fact]
    public void A_file_that_another_program_has_open_for_writing_is_read()
    {
        string file = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        using var other = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var line = Assert.Single(ClipReader.ReadLines(file));

        Assert.Equal("o1", line.Record?.Id);
    }

    [Fact]
    public void A_file_held_open_with_delete_on_close_is_read()
    {
        // A handle that will delete the file when it closes lets other readers in only when they
        // allow deletion too. Some editors and sync tools hold a file this way.
        string file = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        using var holder = new FileStream(
            file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

        var line = Assert.Single(ClipReader.ReadLines(file));

        Assert.Equal("o1", line.Record?.Id);
    }

    [Fact]
    public void While_a_file_is_being_read_another_program_can_still_add_to_it()
    {
        string file = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), TextLine("o2", At(3, 10)));
        using var lines = ClipReader.ReadLines(file).GetEnumerator();
        Assert.True(lines.MoveNext());

        // The reader has the file open now. Something that adds a line to it must not be refused.
        using (var writer = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            writer.Write(Encoding.UTF8.GetBytes(TextLine("o3", At(3, 11)) + "\n"));
        }

        Assert.Equal(["o1", "o2", "o3"], ClipReader.ReadLines(file).Select(line => line.Record?.Id));
    }

    [Fact]
    public void While_a_file_is_being_read_another_program_can_still_delete_it()
    {
        string file = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), TextLine("o2", At(3, 10)));
        using var lines = ClipReader.ReadLines(file).GetEnumerator();
        Assert.True(lines.MoveNext());

        // A sync tool that replaces the file takes the old one away first.
        Assert.Null(Record.Exception(() => File.Delete(file)));
    }

    [Fact]
    public void A_file_that_is_not_there_cannot_be_read()
    {
        string missing = Path.Combine(DeviceDir(Otter), "2026-10-03.jsonl");

        Assert.ThrowsAny<IOException>(() => ClipReader.ReadLines(missing).ToList());
    }

    [Fact]
    public void A_file_that_another_program_has_locked_cannot_be_read()
    {
        string file = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => ClipReader.ReadLines(file).ToList());
    }

    /// <summary>
    /// Makes a day file of exactly this many bytes without writing them: one good line first, and
    /// the rest of the length is what the file system gives a file that is made longer, which is
    /// zeros that are never stored. The good line is there so that a reader that did not look at
    /// the length first would hand it back.
    /// </summary>
    private string WriteDayFileOfLength(string device, string name, long length, string firstLine)
    {
        string file = Path.Combine(DeviceDir(device), name);
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(firstLine + "\n"));
        stream.SetLength(length);
        return file;
    }

    [Fact]
    public void A_day_file_larger_than_the_limit_cannot_be_read_and_none_of_it_is_handed_back()
    {
        string file = WriteDayFileOfLength(Otter, "2026-10-03.jsonl", ClipLimits.MaxDayFileBytes + 1, TextLine("o1", At(3, 9)));
        Assert.Equal(ClipLimits.MaxDayFileBytes + 1, new FileInfo(file).Length);

        // The first line is a good one. The length is looked at before any line is read, so it
        // is not handed back, and the zeros after it are never read into one line.
        var thrown = Assert.Throws<IOException>(() =>
        {
            using var lines = ClipReader.ReadLines(file).GetEnumerator();
            lines.MoveNext();
        });

        Assert.NotEqual("", thrown.Message.Trim());
        string[] secrets = [_dir.Path, Path.GetFileName(_dir.Path), ClipboardFolderName, Otter, "2026-10-03", "2026", "jsonl", @"\", "/", ":"];
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, thrown.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_day_file_as_large_as_the_limit_is_not_refused_for_its_length()
    {
        string file = WriteDayFileOfLength(Otter, "2026-10-03.jsonl", ClipLimits.MaxDayFileBytes, TextLine("o1", At(3, 9)));
        Assert.Equal(ClipLimits.MaxDayFileBytes, new FileInfo(file).Length);

        // Only the first line is asked for: the rest of this file is a line of 256 MB of zeros,
        // which a test has no business reading.
        using var lines = ClipReader.ReadLines(file).GetEnumerator();

        Assert.True(lines.MoveNext());
        Assert.Equal(1, lines.Current.Number);
        Assert.Equal("o1", Assert.IsType<EntryRecord>(lines.Current.Record).Id);
    }

    // The folders of the machines and the files of the days.

    [Fact]
    public void The_machine_folders_come_in_the_order_of_their_names()
    {
        DeviceDir(Walrus);
        DeviceDir(Otter);

        Assert.Equal([Path.Combine(Clipboard, Otter), Path.Combine(Clipboard, Walrus)], ClipReader.DeviceFolders(Clipboard));
    }

    [Fact]
    public void A_file_in_the_clipboard_folder_is_not_a_machine()
    {
        DeviceDir(Otter);
        File.WriteAllText(Path.Combine(Clipboard, "notes.txt"), "walrus");
        File.WriteAllText(Path.Combine(Clipboard, "2026-10-03.jsonl"), TextLine("top", At(3, 9)) + "\n");

        Assert.Equal([Path.Combine(Clipboard, Otter)], ClipReader.DeviceFolders(Clipboard));
    }

    [Fact]
    public void A_clipboard_folder_that_is_not_there_has_no_machine_folders()
    {
        Assert.Empty(ClipReader.DeviceFolders(_dir.File("not-created")));
    }

    [Fact]
    public void A_clipboard_folder_with_nothing_in_it_has_no_machine_folders()
    {
        Directory.CreateDirectory(Clipboard);

        Assert.Empty(ClipReader.DeviceFolders(Clipboard));
    }

    [Fact]
    public void The_day_files_of_a_machine_come_oldest_first_each_with_its_day()
    {
        string fourth = WriteFile(Otter, "2026-10-04.jsonl");
        string lastYear = WriteFile(Otter, "2025-12-31.jsonl");
        string second = WriteFile(Otter, "2026-10-02.jsonl");

        var found = ClipReader.DayFiles(Path.Combine(Clipboard, Otter));

        Assert.Equal([new DateOnly(2025, 12, 31), new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 4)], found.Select(day => day.Day));
        Assert.Equal([lastYear, second, fourth], found.Select(day => day.File));
    }

    [Fact]
    public void Anything_in_a_machines_folder_that_is_not_a_day_file_is_left_out()
    {
        string real = WriteFile(Otter, "2026-10-03.jsonl");
        WriteFile(Otter, "notes.txt");
        WriteFile(Otter, "2026-10-03.jsonl.tmp");
        WriteFile(Otter, "2026-10-03.conflict-PC-1.jsonl");
        WriteFile(Otter, "2026-10-3.jsonl");
        WriteFile(Otter, "2026-10-04.json");

        // The folder of a day's content files, and a folder that has the name of a day file.
        Directory.CreateDirectory(Path.Combine(Clipboard, Otter, "2026-10-03"));
        Directory.CreateDirectory(Path.Combine(Clipboard, Otter, "2026-10-05.jsonl"));

        var found = ClipReader.DayFiles(Path.Combine(Clipboard, Otter));

        Assert.Equal([real], found.Select(day => day.File));
        Assert.Equal([new DateOnly(2026, 10, 3)], found.Select(day => day.Day));
    }

    [Fact]
    public void A_machine_folder_that_is_not_there_has_no_day_files()
    {
        Assert.Empty(ClipReader.DayFiles(Path.Combine(Clipboard, Otter)));
    }

    // Everything together.

    [Fact]
    public void A_clipboard_folder_that_is_not_there_gives_an_empty_history()
    {
        var load = ClipReader.Load(_dir.File("not-created"));

        Assert.Empty(load.Entries);
        Assert.Empty(load.Deleted);
        Assert.Null(load.ClearedBefore);
        Assert.Empty(load.NewestRecord);
        Assert.Equal(0, load.DayFiles);
        Assert.Equal(0, load.UnreadableFiles);
        Assert.Empty(load.BadLines);
        Assert.Equal(0, load.BadLineCount);
    }

    [Fact]
    public void A_machine_folder_with_no_day_files_gives_an_empty_history()
    {
        DeviceDir(Otter);

        var load = ClipReader.Load(Clipboard);

        Assert.Empty(load.Entries);
        Assert.Equal(0, load.DayFiles);
    }

    [Fact]
    public void The_entries_of_one_machine_over_several_days_come_newest_first()
    {
        WriteFile(Otter, "2026-10-02.jsonl", TextLine("a", At(2, 9)), TextLine("b", At(2, 17)));
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("c", At(3, 8)));
        WriteFile(Otter, "2026-10-04.jsonl", TextLine("d", At(4, 7)), TextLine("e", At(4, 7, 30)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["e", "d", "c", "b", "a"], Ids(load));
        Assert.Equal(3, load.DayFiles);
    }

    [Fact]
    public void Entries_are_ordered_by_their_time_and_not_by_where_they_stand_in_the_file()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("evening", At(3, 20)), TextLine("morning", At(3, 6)), TextLine("noon", At(3, 12)));

        Assert.Equal(["evening", "noon", "morning"], Ids(ClipReader.Load(Clipboard)));
    }

    [Fact]
    public void The_entries_of_two_machines_are_merged_by_time()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), TextLine("o2", At(3, 11)));
        WriteFile(Walrus, "2026-10-02.jsonl", TextLine("w0", At(2, 23)));
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 10)), TextLine("w2", At(3, 12)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["w2", "o2", "w1", "o1", "w0"], Ids(load));
        Assert.Equal(3, load.DayFiles);
    }

    [Fact]
    public void Entries_with_the_same_time_on_two_machines_come_in_the_order_of_the_machines_names()
    {
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 9)));
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));

        Assert.Equal(["o1", "w1"], Ids(ClipReader.Load(Clipboard)));
    }

    [Fact]
    public void Of_entries_with_the_same_time_on_one_machine_the_one_written_later_comes_first()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("first", At(3, 9)), TextLine("second", At(3, 9)), TextLine("third", At(3, 9)));

        Assert.Equal(["third", "second", "first"], Ids(ClipReader.Load(Clipboard)));
    }

    [Fact]
    public void Entries_with_the_same_time_on_two_machines_keep_each_machines_own_order()
    {
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 9)), TextLine("w2", At(3, 9)));
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), TextLine("o2", At(3, 9)));

        Assert.Equal(["o2", "o1", "w2", "w1"], Ids(ClipReader.Load(Clipboard)));
    }

    [Fact]
    public void An_entry_read_from_disk_has_the_forms_its_line_gives()
    {
        var record = new EntryRecord("o1", At(3, 9), "explorer.exe")
        {
            Image = Hash + ".png",
            Html = Hash + ".html",
            Files = [@"C:\Otters\walrus.png", @"C:\Otters\notes.txt"],
            Pictures = [new KeptPicture("walrus.png", Hash + ".png")],
        };
        WriteFile(Otter, "2026-10-03.jsonl", ClipLine.Write(record));

        var entry = Assert.Single(ClipReader.Load(Clipboard).Entries);

        Assert.Equal("o1", entry.Id);
        Assert.Equal(At(3, 9), entry.Time);
        Assert.Equal("explorer.exe", entry.SourceApp);
        Assert.Null(entry.Text);
        Assert.Equal(Hash + ".png", entry.Image);
        Assert.Equal(Hash + ".html", entry.Html);
        Assert.Equal(record.Files, entry.Files);
        Assert.Equal(record.Pictures, entry.Pictures);
    }

    [Fact]
    public void An_entrys_machine_is_the_name_of_the_folder_its_file_is_in()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 10)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal([Walrus, Otter], load.Entries.Select(entry => entry.Device));
    }

    [Fact]
    public void An_entrys_day_is_the_day_in_its_files_name_even_when_its_own_time_is_on_another_day()
    {
        // Ten past midnight on the 4th, written into the file of the 3rd.
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(4, 0, 10)));

        var entry = Assert.Single(ClipReader.Load(Clipboard).Entries);

        Assert.Equal(new DateOnly(2026, 10, 3), entry.Day);
        Assert.Equal(At(4, 0, 10), entry.Time);
    }

    [Fact]
    public void An_entrys_folder_is_the_days_folder_beside_its_file()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(4, 0, 10)));
        WriteFile(Walrus, "2026-10-05.jsonl", TextLine("w1", At(5, 9)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(
            [Path.Combine(Clipboard, Walrus, "2026-10-05"), Path.Combine(Clipboard, Otter, "2026-10-03")],
            load.Entries.Select(entry => entry.Folder));
    }

    // Delete records.

    [Theory]
    [InlineData(Otter, Walrus)]
    [InlineData(Walrus, Otter)]
    [InlineData(Otter, Otter)]
    public void An_entry_that_a_delete_record_names_is_left_out_whichever_machine_the_record_is_on(string copiedOn, string deletedOn)
    {
        WriteFile(copiedOn, "2026-10-03.jsonl", TextLine("gone", At(3, 9)), TextLine("kept", At(3, 10)));
        WriteFile(deletedOn, "2026-10-04.jsonl", DeleteLine("d1", At(4, 8), "gone"));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["kept"], Ids(load));
    }

    [Fact]
    public void A_delete_record_counts_even_when_it_is_read_before_the_entry_it_names()
    {
        // The record is in an older file than the entry, and on its own line before another one.
        WriteFile(Otter, "2026-10-02.jsonl", DeleteLine("d1", At(2, 8), "gone"));
        WriteFile(Otter, "2026-10-03.jsonl", DeleteLine("d2", At(3, 8), "gone-too"), TextLine("gone", At(3, 9)), TextLine("gone-too", At(3, 10)), TextLine("kept", At(3, 11)));

        Assert.Equal(["kept"], Ids(ClipReader.Load(Clipboard)));
    }

    [Fact]
    public void Every_id_that_a_delete_record_names_is_handed_back_whether_or_not_an_entry_has_it()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("a", At(3, 9)), DeleteLine("d1", At(3, 10), "a"), DeleteLine("d2", At(3, 11), "never-seen"));
        WriteFile(Walrus, "2026-10-03.jsonl", DeleteLine("d3", At(3, 12), "b"));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["a", "b", "never-seen"], load.Deleted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Without_a_delete_or_a_clear_record_nothing_is_deleted_or_cleared()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));

        var load = ClipReader.Load(Clipboard);

        Assert.Empty(load.Deleted);
        Assert.Null(load.ClearedBefore);
    }

    // Clear records.

    [Theory]
    [InlineData(Otter, Walrus)]
    [InlineData(Walrus, Otter)]
    [InlineData(Otter, Otter)]
    public void Entries_older_than_the_time_a_clear_record_gives_are_left_out_whichever_machine_the_record_is_on(string copiedOn, string clearedOn)
    {
        WriteFile(copiedOn, "2026-10-03.jsonl", TextLine("much-older", At(3, 6)), TextLine("a-second-older", At(3, 9, 59, 59)), TextLine("later", At(3, 11)));
        WriteFile(clearedOn, "2026-10-04.jsonl", ClearLine("c1", At(4, 8), At(3, 10)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["later"], Ids(load));
    }

    [Fact]
    public void An_entry_with_exactly_the_time_a_clear_record_gives_is_kept()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("at-that-time", At(3, 10)));
        WriteFile(Walrus, "2026-10-04.jsonl", ClearLine("c1", At(4, 8), At(3, 10)));

        Assert.Equal(["at-that-time"], Ids(ClipReader.Load(Clipboard)));
    }

    [Fact]
    public void An_entry_later_than_the_time_a_clear_record_gives_is_kept_even_when_it_is_older_than_the_record()
    {
        // Copied at eleven on the 3rd. The record was written on the 4th and clears what is older than ten on the 3rd.
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("later", At(3, 11)));
        WriteFile(Walrus, "2026-10-04.jsonl", ClearLine("c1", At(4, 8), At(3, 10)));

        Assert.Equal(["later"], Ids(ClipReader.Load(Clipboard)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Of_two_clear_records_the_later_time_counts_whichever_is_read_first(bool laterTimeIsReadFirst)
    {
        var earlier = At(3, 10);
        var later = At(3, 12);
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("nine", At(3, 9)), TextLine("eleven", At(3, 11)), TextLine("one", At(3, 13)));
        WriteFile(Otter, "2026-10-04.jsonl", ClearLine("c1", At(4, 8), laterTimeIsReadFirst ? later : earlier));
        WriteFile(Walrus, "2026-10-04.jsonl", ClearLine("c2", At(4, 9), laterTimeIsReadFirst ? earlier : later));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(later, load.ClearedBefore);
        Assert.Equal(["one"], Ids(load));
    }

    [Fact]
    public void The_time_a_clear_record_gives_is_handed_back()
    {
        WriteFile(Otter, "2026-10-04.jsonl", ClearLine("c1", At(4, 8), At(3, 10)));

        Assert.Equal(At(3, 10), ClipReader.Load(Clipboard).ClearedBefore);
    }

    // The newest record of each machine.

    [Fact]
    public void The_newest_record_of_each_machine_counts_delete_and_clear_records_too()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), DeleteLine("d1", At(3, 12), "o1"));
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 8)));
        WriteFile(Walrus, "2026-10-04.jsonl", ClearLine("c1", At(4, 7), At(1, 0)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(2, load.NewestRecord.Count);
        Assert.Equal(At(3, 12), load.NewestRecord[Otter]);
        Assert.Equal(At(4, 7), load.NewestRecord[Walrus]);
    }

    [Fact]
    public void The_newest_record_of_a_machine_is_the_latest_by_time_and_not_the_last_one_read()
    {
        // The file of the 2nd is read first, and its first line has the latest time of all.
        WriteFile(Otter, "2026-10-02.jsonl", TextLine("o1", At(5, 9)), TextLine("o2", At(2, 9)));
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o3", At(3, 9)));

        Assert.Equal(At(5, 9), ClipReader.Load(Clipboard).NewestRecord[Otter]);
    }

    [Fact]
    public void A_machines_newest_record_is_found_whatever_the_case_its_name_is_asked_in()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(At(3, 9), load.NewestRecord["otter-pc"]);
        Assert.Equal(At(3, 9), load.NewestRecord["Otter-Pc"]);
    }

    [Fact]
    public void A_machine_without_one_record_that_can_be_read_has_no_newest_record()
    {
        WriteFile(Otter, "2026-10-03.jsonl", "not json", "");
        DeviceDir(Walrus);

        Assert.Empty(ClipReader.Load(Clipboard).NewestRecord);
    }

    // The same id twice.

    [Fact]
    public void An_id_on_two_machines_counts_once_and_the_machine_first_in_the_order_of_names_has_it()
    {
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("same", At(3, 9), "from the walrus"));
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("same", At(3, 10), "from the otter"));

        var entry = Assert.Single(ClipReader.Load(Clipboard).Entries);

        Assert.Equal(Otter, entry.Device);
        Assert.Equal("from the otter", entry.Text);
    }

    [Fact]
    public void An_id_in_two_day_files_of_one_machine_counts_once_and_the_older_file_has_it()
    {
        WriteFile(Otter, "2026-10-04.jsonl", TextLine("same", At(4, 9), "from the 4th"));
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("same", At(3, 9), "from the 3rd"));

        var entry = Assert.Single(ClipReader.Load(Clipboard).Entries);

        Assert.Equal(new DateOnly(2026, 10, 3), entry.Day);
        Assert.Equal("from the 3rd", entry.Text);
    }

    [Fact]
    public void An_id_twice_in_one_file_counts_once_and_the_first_line_has_it()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("same", At(3, 9), "first"), TextLine("same", At(3, 10), "second"), TextLine("other", At(3, 11)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["other", "same"], Ids(load));
        Assert.Equal("first", load.Entries[1].Text);
    }

    // Lines that cannot be read.

    [Fact]
    public void A_line_that_cannot_be_read_is_skipped_and_counted_and_the_lines_after_it_still_load()
    {
        WriteFile(
            Otter,
            "2026-10-03.jsonl",
            TextLine("o1", At(3, 9)),
            "not json",
            TextLine("o2", At(3, 10)),
            """{"id":"o3","ts":"yesterday","kind":"text","text":"walrus"}""",
            TextLine("o4", At(3, 11)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["o4", "o2", "o1"], Ids(load));
        Assert.Equal(2, load.BadLineCount);
        Assert.Equal([2, 4], load.BadLines);
        Assert.Equal(1, load.DayFiles);
        Assert.Equal(0, load.UnreadableFiles);
    }

    [Fact]
    public void Blank_lines_are_not_counted_as_lines_that_cannot_be_read()
    {
        WriteFile(Otter, "2026-10-03.jsonl", "", TextLine("o1", At(3, 9)), "   ", "", TextLine("o2", At(3, 10)), "\t");

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["o2", "o1"], Ids(load));
        Assert.Equal(0, load.BadLineCount);
        Assert.Empty(load.BadLines);
    }

    [Fact]
    public void The_number_of_a_line_that_cannot_be_read_counts_the_blank_lines_before_it()
    {
        WriteFile(Otter, "2026-10-03.jsonl", "", "", "not json", TextLine("o1", At(3, 9)));

        Assert.Equal([3], ClipReader.Load(Clipboard).BadLines);
    }

    [Fact]
    public void More_bad_lines_than_are_listed_are_still_all_counted()
    {
        int bad = ClipReader.MaxBadLinesListed + 5;
        string[] lines = Enumerable.Range(1, bad).Select(number => "nonsense " + number).Append(TextLine("o1", At(3, 9))).ToArray();
        WriteFile(Otter, "2026-10-03.jsonl", lines);

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(bad, load.BadLineCount);
        Assert.Equal(Enumerable.Range(1, ClipReader.MaxBadLinesListed), load.BadLines);
        Assert.Equal(["o1"], Ids(load));
    }

    [Fact]
    public void As_many_bad_lines_as_are_listed_are_all_listed()
    {
        string[] lines = Enumerable.Range(1, ClipReader.MaxBadLinesListed).Select(number => "nonsense " + number).ToArray();
        WriteFile(Otter, "2026-10-03.jsonl", lines);

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(ClipReader.MaxBadLinesListed, load.BadLineCount);
        Assert.Equal(Enumerable.Range(1, ClipReader.MaxBadLinesListed), load.BadLines);
    }

    [Fact]
    public void Bad_lines_in_several_files_are_counted_together()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)), "not json");
        WriteFile(Walrus, "2026-10-03.jsonl", "not json either", TextLine("w1", At(3, 10)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(2, load.BadLineCount);
        Assert.Equal([2, 1], load.BadLines);
        Assert.Equal(["w1", "o1"], Ids(load));
    }

    // Files that are not day files, and files that cannot be opened.

    [Fact]
    public void A_file_that_is_not_a_day_file_is_not_read_whatever_it_holds()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        WriteFile(Otter, "notes.txt", TextLine("n1", At(3, 10)), "not json");
        WriteFile(Otter, "2026-10-03.jsonl.tmp", TextLine("t1", At(3, 11)), DeleteLine("d1", At(3, 12), "o1"));
        WriteFile(Otter, "2026-10-03.conflict-PC-1.jsonl", ClearLine("c1", At(3, 13), At(3, 13)));

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["o1"], Ids(load));
        Assert.Equal(1, load.DayFiles);
        Assert.Equal(0, load.BadLineCount);
        Assert.Empty(load.Deleted);
        Assert.Null(load.ClearedBefore);
        Assert.Equal(At(3, 9), load.NewestRecord[Otter]);
    }

    [Fact]
    public void A_day_file_that_is_not_in_a_machines_folder_is_not_read()
    {
        WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        File.WriteAllText(Path.Combine(Clipboard, "2026-10-03.jsonl"), TextLine("top", At(3, 10)) + "\n");
        string dayFolder = Path.Combine(Clipboard, Otter, "2026-10-03");
        Directory.CreateDirectory(dayFolder);
        File.WriteAllText(Path.Combine(dayFolder, "2026-10-04.jsonl"), TextLine("deep", At(4, 10)) + "\n");

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["o1"], Ids(load));
        Assert.Equal(1, load.DayFiles);
    }

    [Fact]
    public void A_day_file_that_another_program_has_locked_is_left_out_and_counted_and_the_others_still_load()
    {
        string locked = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        WriteFile(Otter, "2026-10-04.jsonl", TextLine("o2", At(4, 9)));
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 10)));
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // No report is asked for here, and nothing is thrown for the want of one.
        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["o2", "w1"], Ids(load));
        Assert.Equal(1, load.UnreadableFiles);
        Assert.Equal(2, load.DayFiles);
        Assert.Equal(0, load.BadLineCount);
    }

    [Fact]
    public void A_locked_day_file_is_reported_once_in_words_that_name_no_file_folder_or_machine()
    {
        string locked = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 10)));
        var reports = new List<(string Words, Exception? Cause)>();
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        ClipReader.Load(Clipboard, (words, cause) => reports.Add((words, cause)));

        var report = Assert.Single(reports);
        Assert.IsAssignableFrom<IOException>(report.Cause);
        Assert.NotEqual("", report.Words.Trim());
        string[] secrets = [_dir.Path, Path.GetFileName(_dir.Path), ClipboardFolderName, Otter, "2026-10-03", "2026", "jsonl", @"\", "/", ":"];
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, report.Words, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Each_locked_day_file_is_reported_in_the_same_words()
    {
        string first = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        string second = WriteFile(Walrus, "2026-10-05.jsonl", TextLine("w1", At(5, 10)));
        var reports = new List<string>();
        using var holdFirst = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var holdSecond = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var load = ClipReader.Load(Clipboard, (words, _) => reports.Add(words));

        Assert.Equal(2, load.UnreadableFiles);
        Assert.Equal(0, load.DayFiles);
        Assert.Equal(2, reports.Count);
        Assert.Equal(reports[0], reports[1]);
    }

    [Fact]
    public void A_day_file_that_was_locked_is_read_by_the_next_load_once_it_is_free()
    {
        string locked = WriteFile(Otter, "2026-10-03.jsonl", TextLine("o1", At(3, 9)));
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(ClipReader.Load(Clipboard).Entries);
        }

        var load = ClipReader.Load(Clipboard);

        Assert.Equal(["o1"], Ids(load));
        Assert.Equal(0, load.UnreadableFiles);
    }

    [Fact]
    public void A_day_file_larger_than_the_limit_is_left_out_and_counted_and_the_others_still_load()
    {
        WriteDayFileOfLength(Otter, "2026-10-03.jsonl", ClipLimits.MaxDayFileBytes + 1, TextLine("o1", At(3, 9)));
        WriteFile(Otter, "2026-10-04.jsonl", TextLine("o2", At(4, 9)));
        WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 10)));
        var reports = new List<(string Words, Exception? Cause)>();

        var load = ClipReader.Load(Clipboard, (words, cause) => reports.Add((words, cause)));

        // Nothing of the large file is kept, not even the good line that stands first in it.
        Assert.Equal(["o2", "w1"], Ids(load));
        Assert.Equal(1, load.UnreadableFiles);
        Assert.Equal(2, load.DayFiles);
        Assert.Equal(0, load.BadLineCount);
        var report = Assert.Single(reports);
        Assert.IsType<IOException>(report.Cause);
        Assert.NotEqual("", report.Words.Trim());
        string[] secrets = [_dir.Path, Path.GetFileName(_dir.Path), ClipboardFolderName, Otter, "2026-10-03", "2026", "jsonl", @"\", "/", ":"];
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, report.Words, StringComparison.OrdinalIgnoreCase));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, report.Cause!.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_day_file_larger_than_the_limit_is_reported_in_the_same_words_as_one_that_is_locked()
    {
        WriteDayFileOfLength(Otter, "2026-10-03.jsonl", ClipLimits.MaxDayFileBytes + 1, TextLine("o1", At(3, 9)));
        string locked = WriteFile(Walrus, "2026-10-03.jsonl", TextLine("w1", At(3, 10)));
        var reports = new List<string>();
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var load = ClipReader.Load(Clipboard, (words, _) => reports.Add(words));

        Assert.Equal(2, load.UnreadableFiles);
        Assert.Equal(2, reports.Count);
        Assert.Equal(reports[0], reports[1]);
    }

    // Loading only reads.

    private static string[] Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                string relative = Path.GetRelativePath(folder, path);
                return Directory.Exists(path)
                    ? "folder " + relative
                    : "file " + relative + " " + File.GetLastWriteTimeUtc(path).Ticks + " " + Convert.ToBase64String(File.ReadAllBytes(path));
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Loading_changes_nothing_on_disk()
    {
        // Two machines. Good lines, a delete record and a clear record that remove entries,
        // lines that cannot be read, a blank line, a file that is not UTF-8, a folder of content
        // for a day, a folder with nothing in it, and files that are not day files.
        string otterDay = Path.Combine(DeviceDir(Otter), "2026-10-03");
        Directory.CreateDirectory(otterDay);
        File.WriteAllText(Path.Combine(otterDay, Hash + ".txt"), "the whole of a long text");
        File.WriteAllBytes(Path.Combine(otterDay, Hash[..63] + "1.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF]);
        Directory.CreateDirectory(Path.Combine(DeviceDir(Otter), "2026-10-02"));

        WriteFile(
            Otter,
            "2026-10-03.jsonl",
            TextLine("o1", At(3, 9)),
            "this line is not a record",
            "",
            TextLine("o2", At(3, 10)),
            DeleteLine("o3", At(3, 11), "o1"));
        WriteFile(Otter, "2026-10-04.jsonl", ClipLine.Write(new EntryRecord("o4", At(4, 9), "otter.exe") { TextFile = Hash + ".txt", Text = "start", TextLength = 24 }));
        WriteContent(Otter, "2026-10-03.conflict-OTHER-PC.jsonl", TextLine("c1", At(3, 12)) + "\n");
        WriteContent(Otter, "notes.txt", "not a day file\n");
        string walrusFile = Path.Combine(DeviceDir(Walrus), "2026-10-03.jsonl");
        File.WriteAllBytes(
            walrusFile,
            [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(TextLine("w1", At(3, 8)) + "\n"), 0xFF, 0xFE, 0x0A, .. Encoding.UTF8.GetBytes(TextLine("w2", At(3, 13)) + "\r\n")]);
        WriteFile(Walrus, "2026-10-05.jsonl", ClearLine("w3", At(5, 9), At(3, 10)), TextLine("w4", At(5, 10)));
        File.WriteAllText(Path.Combine(Clipboard, "readme.txt"), "a stray file in the clipboard folder");

        string[] before = Snapshot(Clipboard);
        var load = ClipReader.Load(Clipboard);
        string[] after = Snapshot(Clipboard);

        // The load did read and merge all of it, so that the check below is not about nothing.
        Assert.Equal(["w4", "o4", "w2", "o2"], Ids(load));
        Assert.Equal(["o1"], load.Deleted.Order(StringComparer.Ordinal));
        Assert.Equal(At(3, 10), load.ClearedBefore);
        Assert.Equal(4, load.DayFiles);
        Assert.Equal(2, load.BadLineCount);
        Assert.Equal(0, load.UnreadableFiles);

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    // The two questions a load answers, asked by themselves.

    [Fact]
    public void An_entry_that_a_delete_record_names_is_removed()
    {
        var deleted = new HashSet<string> { "gone" };

        Assert.True(ClipReader.IsRemoved("gone", At(3, 9), deleted, clearedBefore: null));
        Assert.False(ClipReader.IsRemoved("kept", At(3, 9), deleted, clearedBefore: null));
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public void An_entry_is_removed_by_a_clear_record_only_when_the_record_gives_a_later_time_than_its_own(int hour, bool removed)
    {
        Assert.Equal(removed, ClipReader.IsRemoved("a1", At(3, hour), new HashSet<string>(), clearedBefore: At(3, 10)));
    }

    [Fact]
    public void The_entries_that_are_not_deleted_or_cleared_are_visible_newest_first()
    {
        ClipEntry[] entries =
        [
            EntryOn(Otter, "cleared", At(3, 8)),
            EntryOn(Otter, "mid", At(3, 10)),
            EntryOn(Otter, "deleted", At(3, 12)),
            EntryOn(Walrus, "new", At(3, 11)),
        ];

        var visible = ClipReader.Visible(entries, new HashSet<string> { "deleted" }, clearedBefore: At(3, 9));

        Assert.Equal(["new", "mid"], visible.Select(entry => entry.Id));
    }

    [Fact]
    public void Visible_entries_with_the_same_time_come_by_machine_and_on_one_machine_the_later_one_first()
    {
        ClipEntry[] entries =
        [
            EntryOn(Walrus, "w1", At(3, 9)),
            EntryOn(Otter, "o1", At(3, 9)),
            EntryOn(Walrus, "w2", At(3, 9)),
            EntryOn(Otter, "o2", At(3, 9)),
        ];

        var visible = ClipReader.Visible(entries, new HashSet<string>(), clearedBefore: null);

        Assert.Equal(["o2", "o1", "w2", "w1"], visible.Select(entry => entry.Id));
    }

    [Fact]
    public void The_order_of_the_machines_does_not_depend_on_the_case_of_their_names()
    {
        // By the numbers of the characters "WALRUS-PC" would come before "otter-pc". The otter's
        // entry is given first, so that it is not the order they are given in that puts it first.
        ClipEntry[] entries = [EntryOn("otter-pc", "o1", At(3, 9)), EntryOn(Walrus, "w1", At(3, 9))];

        var visible = ClipReader.Visible(entries, new HashSet<string>(), clearedBefore: null);

        Assert.Equal(["o1", "w1"], visible.Select(entry => entry.Id));
    }
}
