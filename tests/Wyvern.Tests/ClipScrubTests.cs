using System.Diagnostics;
using System.Text;
using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Tests;

public sealed class ClipScrubTests : IDisposable
{
    // A made-up machine, so that no test depends on the name of the one it runs on.
    private const string Machine = "OTTER-PC";

    private const string Garbage = "{ this is not a record";
    private const string DayNotRewritten = "A day of clipboard history could not be rewritten";
    private const string ContentNotDeleted = "Clipboard content that is no longer in the history could not be deleted";
    private const string TemporaryNotDeleted = "A temporary file that an earlier rewrite left behind could not be deleted";
    private const string FolderIsALink = "This machine's clipboard folder is a link and is left alone";
    private const string DayFolderIsALink = "A day's folder in the clipboard history is a link and is left alone";

    private static readonly DateOnly Day = new(2026, 10, 3);
    private static readonly DateOnly DayBefore = new(2026, 10, 2);
    private static readonly DateOnly TwoDaysBefore = new(2026, 10, 1);

    // Older than anything a test does, so that a file that was written again shows.
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly HashSet<string> NothingDeleted = [];

    private readonly TempDir _dir = new();
    private readonly List<(string Text, Exception? Cause)> _reports = [];
    private readonly List<string> _links = [];
    private readonly List<TimeSpan> _pauses = [];

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

    private string DeviceDir => Path.Combine(_dir.Path, "clipboard", Machine);

    private string DayFile(DateOnly day) => Path.Combine(DeviceDir, ClipPaths.DayFileName(day));

    private string DayFolder(DateOnly day) => Path.Combine(DeviceDir, ClipPaths.DayName(day));

    private static DateTimeOffset On(DateOnly day, int hour, int minute = 0, int second = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute, second)), TimeSpan.Zero);

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0) => On(Day, hour, minute, second);

    private static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.Ordinal);

    private static string Entry(string id, DateTimeOffset time, string text = "walrus") =>
        ClipLine.Write(new EntryRecord(id, time, null) { Text = text, TextLength = text.Length });

    /// <summary>The line of an entry that has no text, only HTML in the file of this name.</summary>
    private static string EntryWith(string id, DateTimeOffset time, string html) =>
        ClipLine.Write(new EntryRecord(id, time, null) { Html = html });

    private static string Deletion(string id, DateTimeOffset time, string target) =>
        ClipLine.Write(new DeleteRecord(id, time, target));

    private static string Clearing(string id, DateTimeOffset time) =>
        ClipLine.Write(new ClearRecord(id, time, time));

    /// <summary>Writes a day file the way the store does: UTF-8, a line feed after every line.</summary>
    private string WriteDay(DateOnly day, params string[] lines) =>
        WriteDayText(day, string.Concat(lines.Select(line => line + "\n")));

    /// <summary>Writes a day file with exactly this text and gives it an old date.</summary>
    private string WriteDayText(DateOnly day, string text)
    {
        Directory.CreateDirectory(DeviceDir);
        string file = DayFile(day);
        File.WriteAllText(file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetLastWriteTimeUtc(file, LongAgo);
        return file;
    }

    /// <summary>Puts a file of content into a day's folder under the name the store would give it.</summary>
    /// <returns>The file's name.</returns>
    private string Content(DateOnly day, string content, string extension = ClipPaths.HtmlExtension)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        string name = ClipPaths.BlobName(bytes, extension);
        Directory.CreateDirectory(DayFolder(day));
        File.WriteAllBytes(Path.Combine(DayFolder(day), name), bytes);
        return name;
    }

    /// <summary>Puts a file with any name into a day's folder and gives it an old date.</summary>
    /// <returns>The file's full path.</returns>
    private string InFolder(DateOnly day, string name)
    {
        Directory.CreateDirectory(DayFolder(day));
        string file = Path.Combine(DayFolder(day), name);
        File.WriteAllText(file, "otter");
        File.SetLastWriteTimeUtc(file, LongAgo);
        return file;
    }

    private bool HasContent(DateOnly day, string name) => File.Exists(Path.Combine(DayFolder(day), name));

    /// <summary>Holds a file open the way a program does that lets nobody else in.</summary>
    private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.None);

    /// <summary>Holds a file open the way a virus scanner or an indexer does: everyone else may read, write and delete.</summary>
    private static FileStream HoldShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private void Report(string text, Exception? cause) => _reports.Add((text, cause));

    /// <summary>Stands in for sleeping: no test waits.</summary>
    private void Pause(TimeSpan length) => _pauses.Add(length);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

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
    /// The target of a junction: a folder that holds everything the scrub would touch if it
    /// followed the link, at its top level (a day file, content named by no record, and a
    /// leftover temporary file of each) and in a folder named for a day.
    /// </summary>
    /// <returns>Every file in it, as full paths.</returns>
    private string[] MakeJunctionTarget(string name)
    {
        string target = Path.Combine(_dir.Path, name);
        string dayFolder = Path.Combine(target, ClipPaths.DayName(Day));
        Directory.CreateDirectory(dayFolder);
        byte[] bytes = Encoding.UTF8.GetBytes("<b>otter</b>");
        string blob = ClipPaths.BlobName(bytes, ClipPaths.HtmlExtension);
        string[] files =
        [
            Path.Combine(target, ClipPaths.DayFileName(Day)),
            Path.Combine(target, ClipPaths.DayFileName(Day) + ClipScrub.TemporaryExtension),
            Path.Combine(target, blob),
            Path.Combine(target, blob + ClipScrub.TemporaryExtension),
            Path.Combine(dayFolder, blob),
            Path.Combine(dayFolder, blob + ClipScrub.TemporaryExtension),
        ];
        File.WriteAllText(files[0], Entry("a", At(10)) + "\n");
        File.WriteAllText(files[1], Entry("gone", At(9)) + "\n");
        foreach (string file in files.Skip(2))
        {
            File.WriteAllBytes(file, bytes);
        }

        foreach (string file in files)
        {
            File.SetLastWriteTimeUtc(file, LongAgo);
        }

        return files;
    }

    /// <summary>Every file and folder under a folder with its bytes and its date, so that a change of any kind shows.</summary>
    private static string[] Snapshot(string folder) =>
        Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? path + "|" + Convert.ToHexString(File.ReadAllBytes(path)) + "|" + File.GetLastWriteTimeUtc(path).Ticks
                : path + "|folder")
            .ToArray();

    [Fact]
    public void A_deleted_entry_is_taken_out_of_its_day_file_and_the_other_lines_stay_as_they_were()
    {
        // Written by hand in a form the store would not write: a rewrite that read the records
        // and wrote them out again would change these two lines.
        const string first = """{ "kind": "text", "id": "a", "ts": "2026-10-03T10:00:00+00:00", "text": "walrus", "somethingNew": 1 }""";
        const string third = """{"sourceApp":"notepad.exe","text":"otter","kind":"text","ts":"2026-10-03T10:00:02.5Z","id":"c"}""";
        Assert.IsType<EntryRecord>(ClipLine.Parse(first));
        Assert.IsType<EntryRecord>(ClipLine.Parse(third));
        string file = WriteDay(Day, first, Entry("b", At(10, 0, 1)), third);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("b"), clearedBefore: null);

        Assert.Equal(first + "\n" + third + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void A_day_with_nothing_deleted_or_cleared_is_not_touched()
    {
        // Line ends, and a last line without one, that a rewrite would not leave as they are.
        string file = WriteDayText(Day, Entry("a", At(10)) + "\r\n" + Entry("b", At(11)));
        byte[] before = File.ReadAllBytes(file);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("some-other-id"), At(9));

        Assert.Equal(ScrubResult.Nothing, result);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void An_entry_older_than_the_clear_goes_and_one_at_exactly_that_time_or_later_stays()
    {
        string same = Entry("b", At(10));
        string later = Entry("c", At(10, 0, 1));
        string file = WriteDay(Day, Entry("a", At(9, 59, 59)), same, later);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(10));

        Assert.Equal(same + "\n" + later + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void Delete_and_clear_records_stay_under_a_clear_that_is_later_than_they_are()
    {
        string deletion = Deletion("d", At(9), "elsewhere");
        string clearing = Clearing("c", At(9, 30));
        string file = WriteDay(Day, deletion, Entry("a", At(9, 10)), clearing);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(11));

        Assert.Equal(deletion + "\n" + clearing + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void Delete_and_clear_records_stay_when_a_delete_record_names_their_own_ids()
    {
        string deletion = Deletion("d", At(9), "elsewhere");
        string clearing = Clearing("c", At(9, 30));
        string file = WriteDay(Day, deletion, clearing, Entry("a", At(10)));

        ClipScrub.RunDay(DeviceDir, Day, Ids("a", "c", "d"), clearedBefore: null);

        Assert.Equal(deletion + "\n" + clearing + "\n", File.ReadAllText(file));
    }

    [Fact]
    public void A_day_that_holds_only_delete_and_clear_records_is_not_rewritten_by_a_later_clear()
    {
        string file = WriteDay(DayBefore, Deletion("d", On(DayBefore, 9), "elsewhere"), Clearing("c", On(DayBefore, 10)));

        var result = ClipScrub.RunDay(DeviceDir, DayBefore, Ids("c", "d"), At(11));

        Assert.Equal(ScrubResult.Nothing, result);
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_line_that_cannot_be_read_stays_when_nothing_is_cleared()
    {
        string kept = Entry("b", At(11));
        string file = WriteDay(Day, Garbage, Entry("a", At(10)), kept);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.Equal(Garbage + "\n" + kept + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Theory]
    [InlineData(12, 0, 0)]
    [InlineData(23, 59, 59)]
    public void A_last_line_that_cannot_be_read_stays_when_the_clear_is_earlier_than_the_end_of_its_day(int hour, int minute, int second)
    {
        // Nothing after the line says when it was written, so it may be later than the clear.
        string file = WriteDay(Day, Entry("a", At(9)), Garbage);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(hour, minute, second));

        Assert.Equal(Garbage + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void A_line_that_cannot_be_read_goes_when_the_clear_record_itself_follows_it()
    {
        // The usual case: the clear the owner asked for is the next line of the same file.
        string clearing = Clearing("c", At(12));
        string file = WriteDay(Day, Garbage, clearing);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(12));

        // The line was not an entry, so the day is rewritten and no entry is counted.
        Assert.Equal(clearing + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 0, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void A_line_that_cannot_be_read_goes_when_the_record_after_it_is_older_than_the_clear()
    {
        string deletion = Deletion("d", At(11), "elsewhere");
        string file = WriteDay(Day, Garbage, Garbage, deletion);

        ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(12));

        Assert.Equal(deletion + "\n", File.ReadAllText(file));
    }

    [Fact]
    public void A_line_that_cannot_be_read_stays_when_the_record_after_it_is_later_than_the_clear()
    {
        string later = Entry("b", At(13));
        string file = WriteDay(Day, Entry("a", At(9)), Garbage, later);

        ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(12));

        Assert.Equal(Garbage + "\n" + later + "\n", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void A_line_that_cannot_be_read_goes_when_its_whole_day_is_before_the_clear(int hourOfTheNextDay)
    {
        // The first row is a clear at the very moment the day ends.
        string deletion = Deletion("d", On(DayBefore, 9), "elsewhere");
        string file = WriteDay(DayBefore, deletion, Garbage);

        var result = ClipScrub.RunDay(DeviceDir, DayBefore, NothingDeleted, At(hourOfTheNextDay));

        Assert.Equal(deletion + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 0, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void Blank_lines_alone_are_never_a_reason_to_rewrite()
    {
        // Under a clear that covers the whole day, which would take a line that cannot be read.
        string file = WriteDayText(DayBefore, "\n" + Deletion("d", On(DayBefore, 9), "elsewhere") + "\n\n   \n\t\n");

        var result = ClipScrub.RunDay(DeviceDir, DayBefore, NothingDeleted, At(12));

        Assert.Equal(ScrubResult.Nothing, result);
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Blank_lines_are_dropped_when_the_file_is_rewritten_for_another_reason()
    {
        string kept = Entry("b", At(11));
        string file = WriteDayText(Day, "\n" + Entry("a", At(10)) + "\n\n   \n" + kept + "\n\t\n\n");

        ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.Equal(kept + "\n", File.ReadAllText(file));
    }

    [Fact]
    public void The_rewritten_file_has_line_feeds_a_last_line_break_and_no_byte_order_mark()
    {
        // The first text is outside ASCII, so the bytes also show that the file is still UTF-8.
        string first = Entry("a", At(10), "w" + (char)0xE5 + "lrus");
        string third = Entry("c", At(10, 0, 2), "otter");
        Assert.Contains((char)0xE5, first);
        Directory.CreateDirectory(DeviceDir);
        string file = DayFile(Day);
        File.WriteAllText(
            file,
            first + "\r\n" + Entry("b", At(10, 0, 1)) + "\r\n" + third,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(file)[0]);

        ClipScrub.RunDay(DeviceDir, Day, Ids("b"), clearedBefore: null);

        Assert.Equal(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(first + "\n" + third + "\n"),
            File.ReadAllBytes(file));
    }

    [Fact]
    public void No_temporary_file_is_left_after_a_rewrite()
    {
        string file = WriteDay(Day, Entry("a", At(10)), Entry("b", At(11)));

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.Equal(1, result.DaysRewritten);
        Assert.Equal(file, Assert.Single(Directory.GetFileSystemEntries(DeviceDir)));
    }

    [Fact]
    public void The_content_of_a_removed_entry_is_deleted()
    {
        string gone = Content(Day, "<b>walrus</b>");
        string kept = Content(Day, "<b>otter</b>");
        WriteDay(Day, EntryWith("a", At(10), gone), EntryWith("b", At(11), kept));

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.False(HasContent(Day, gone));
        Assert.True(HasContent(Day, kept));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void Content_that_another_entry_of_the_same_day_still_names_is_kept()
    {
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html), EntryWith("b", At(11), html));

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.True(HasContent(Day, html));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void Every_file_that_an_entry_which_stays_names_is_kept()
    {
        string text = Content(Day, "the whole of a long text", ClipPaths.TextExtension);
        string html = Content(Day, "<b>walrus</b>");
        string rtf = Content(Day, "{rtf walrus}", ClipPaths.RtfExtension);
        string image = Content(Day, "a picture of a walrus", ClipPaths.ImageExtension);
        string[] pictures = ClipPaths.PictureExtensions.Select(extension => Content(Day, "a picture file " + extension, extension)).ToArray();
        var staying = new EntryRecord("a", At(10), null)
        {
            Text = "the whole",
            TextLength = 24,
            TextFile = text,
            Html = html,
            Rtf = rtf,
            Image = image,
            Files = [@"C:\made-up\otter.png"],
            Pictures = pictures.Select(name => new KeptPicture("otter" + Path.GetExtension(name), name)).ToList(),
        };

        // A second entry goes, so the day is rewritten as well as its folder gone through.
        WriteDay(Day, ClipLine.Write(staying), Entry("b", At(11)));

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("b"), clearedBefore: null);

        string[] named = [text, html, rtf, image, .. pictures];
        Assert.Equal(9, named.Length);
        Assert.All(named, name => Assert.True(HasContent(Day, name)));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void Content_that_no_record_names_is_deleted_even_when_the_day_file_stays_as_it_is()
    {
        string file = WriteDay(Day, Entry("a", At(10)));
        string leftOver = Content(Day, "<b>left over</b>");

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.False(HasContent(Day, leftOver));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void A_temporary_file_in_the_day_folder_is_deleted()
    {
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html));
        string temporary = InFolder(Day, ClipPaths.BlobName([1, 2, 3], ClipPaths.ImageExtension) + ClipScrub.TemporaryExtension);

        ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.False(File.Exists(temporary));
        Assert.True(HasContent(Day, html));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.pdf")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde.txt")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.txt")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("notes.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.pdf.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde.txt.tmp")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.txt.tmp")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.html.tmp.tmp")]
    public void A_file_that_the_store_would_not_have_written_is_never_touched_and_its_folder_stays(string name)
    {
        // Besides the plain name: a hash with an extension that is not one of ours, a hash that
        // is one digit short, one in capitals, and one with no extension.
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html));
        string file = InFolder(Day, name);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), At(23));

        // What is ours in the folder went. The folder stays for the one file that is not.
        Assert.Equal(file, Assert.Single(Directory.GetFileSystemEntries(DayFolder(Day))));
        Assert.Equal("otter", File.ReadAllText(file));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void A_day_folder_left_empty_is_removed()
    {
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html));

        ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.False(Directory.Exists(DayFolder(Day)));
    }

    [Fact]
    public void A_day_folder_whose_day_file_has_gone_loses_its_content()
    {
        Content(Day, "<b>walrus</b>");
        Content(Day, "a picture of a walrus", ClipPaths.ImageExtension);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.False(Directory.Exists(DayFolder(Day)));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 2, Failures: 0), result);
    }

    [Fact]
    public void A_day_with_neither_a_file_nor_a_folder_gives_all_zeros()
    {
        Directory.CreateDirectory(DeviceDir);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), At(12), Report);

        Assert.Equal(new ScrubResult(0, 0, 0, 0), result);
        Assert.Empty(_reports);
    }

    [Fact]
    public void Run_scrubs_every_day_in_the_folder_and_adds_the_results()
    {
        string first = Content(TwoDaysBefore, "<b>one</b>");
        string oldest = WriteDay(TwoDaysBefore, EntryWith("a", On(TwoDaysBefore, 10), first), Entry("b", On(TwoDaysBefore, 11)));
        string second = Content(DayBefore, "<b>two</b>");
        Content(DayBefore, "<b>left over</b>");
        string kept = Entry("d", On(DayBefore, 11));
        string middle = WriteDay(DayBefore, EntryWith("c", On(DayBefore, 10), second), kept);
        string newest = WriteDay(Day, Entry("e", At(13)));

        // The clear takes both entries of the oldest day; the delete takes one of the day after.
        var result = ClipScrub.Run(DeviceDir, Ids("c"), On(TwoDaysBefore, 23));

        Assert.Equal(new ScrubResult(DaysRewritten: 2, EntriesRemoved: 3, FilesRemoved: 3, Failures: 0), result);
        Assert.Equal("", File.ReadAllText(oldest));
        Assert.Equal(kept + "\n", File.ReadAllText(middle));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(newest));
        Assert.Empty(Directory.GetDirectories(DeviceDir));
    }

    [Fact]
    public void Run_scrubs_a_day_that_has_a_folder_and_no_file()
    {
        Content(DayBefore, "<b>walrus</b>");
        WriteDay(Day, Entry("a", At(10)));

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null);

        Assert.False(Directory.Exists(DayFolder(DayBefore)));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void Content_is_kept_only_by_a_record_of_its_own_day()
    {
        string html = Content(DayBefore, "<b>walrus</b>");
        Content(Day, "<b>walrus</b>");
        WriteDay(DayBefore, EntryWith("a", On(DayBefore, 10), html));
        WriteDay(Day, Entry("b", At(10)));

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null);

        Assert.True(HasContent(DayBefore, html));
        Assert.False(HasContent(Day, html));
        Assert.Equal(1, result.FilesRemoved);
    }

    [Fact]
    public void A_temporary_file_ends_in_tmp()
    {
        Assert.Equal(".tmp", ClipScrub.TemporaryExtension);
    }

    [Fact]
    public void Run_removes_a_temporary_file_that_an_interrupted_rewrite_left_beside_the_day_files()
    {
        string file = WriteDay(Day, Entry("a", At(10)));
        string temporary = file + ClipScrub.TemporaryExtension;
        File.WriteAllText(temporary, Entry("gone", At(9)) + "\n");

        ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null);

        Assert.False(File.Exists(temporary));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
    }

    [Theory]
    [InlineData("keep.tmpx")]
    [InlineData("tmp")]
    [InlineData("notes.txt")]
    [InlineData("2026-10-03.jsonl.bak")]
    [InlineData("draft.tmp")]
    [InlineData("2026-10-03.tmp")]
    [InlineData("2026-1-3.jsonl.tmp")]
    [InlineData("2026-10-03.jsonl.tmp.tmp")]
    public void Run_leaves_a_file_beside_the_day_files_alone_unless_it_is_the_temporary_file_of_a_day(string name)
    {
        WriteDay(Day, Entry("a", At(10)));
        string file = Path.Combine(DeviceDir, name);
        File.WriteAllText(file, "otter");
        File.SetLastWriteTimeUtc(file, LongAgo);

        var result = ClipScrub.Run(DeviceDir, Ids("a"), At(23));

        Assert.Equal("otter", File.ReadAllText(file));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public void Run_leaves_a_folder_that_is_not_named_for_a_day_alone()
    {
        string folder = Path.Combine(DeviceDir, "backup");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, ClipPaths.BlobName([1, 2, 3], ClipPaths.HtmlExtension));
        File.WriteAllText(file, "otter");

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, At(23));

        Assert.True(File.Exists(file));
        Assert.Equal(ScrubResult.Nothing, result);
    }

    [Fact]
    public void A_machine_folder_that_is_not_there_gives_all_zeros_and_is_not_made()
    {
        var result = ClipScrub.Run(DeviceDir, Ids("a"), At(12), Report);

        Assert.Equal(new ScrubResult(0, 0, 0, 0), result);
        Assert.False(Directory.Exists(DeviceDir));
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_day_file_that_is_locked_is_a_failure_and_nothing_of_that_day_is_deleted()
    {
        string html = Content(Day, "<b>walrus</b>");
        string leftOver = Content(Day, "<b>left over</b>");
        string file = WriteDay(Day, EntryWith("a", At(10), html));
        byte[] before = File.ReadAllBytes(file);

        ScrubResult result;
        using (Hold(file))
        {
            result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report);
        }

        // The entry is still on disk, so its files must be too. Which files the day names is
        // not known, so not even the one no record names is taken.
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        Assert.True(HasContent(Day, html));
        Assert.True(HasContent(Day, leftOver));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    /// <summary>A day file that is one byte longer than the largest that is read. Its length is set and nothing is written: what the file system gives such a file is zeros that nobody here reads.</summary>
    private string TooLargeDayFile(DateOnly day)
    {
        Directory.CreateDirectory(DeviceDir);
        string file = DayFile(day);
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ClipLimits.MaxDayFileBytes + 1);
        }

        File.SetLastWriteTimeUtc(file, LongAgo);
        return file;
    }

    [Fact]
    public void A_day_file_over_the_largest_that_is_read_is_left_exactly_as_it_is_none_of_its_content_is_deleted_and_it_is_one_failure()
    {
        string html = Content(Day, "<b>walrus</b>");
        string leftOver = Content(Day, "<b>left over</b>");
        string file = TooLargeDayFile(Day);

        // A delete and a clear that would otherwise take lines out, and content that no line names.
        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), At(23), Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(ClipLimits.MaxDayFileBytes + 1, new FileInfo(file).Length);
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.True(HasContent(Day, html));
        Assert.True(HasContent(Day, leftOver));
        Assert.Equal(file, Assert.Single(Directory.GetFiles(DeviceDir)));
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(DayNotRewritten, text);
        Assert.IsAssignableFrom<IOException>(cause);
        Assert.Empty(_pauses);
    }

    [Fact]
    public void A_scrub_of_the_whole_folder_leaves_a_day_file_over_the_limit_and_its_content_alone_and_still_scrubs_the_other_days()
    {
        string html = Content(Day, "<b>walrus</b>");
        string file = TooLargeDayFile(Day);
        string kept = Entry("b", On(DayBefore, 11));
        string dayBefore = WriteDay(DayBefore, Entry("a", On(DayBefore, 10)), kept);

        var result = ClipScrub.Run(DeviceDir, Ids("a"), clearedBefore: null, Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(kept + "\n", File.ReadAllText(dayBefore));
        Assert.Equal(ClipLimits.MaxDayFileBytes + 1, new FileInfo(file).Length);
        Assert.True(HasContent(Day, html));
        Assert.Equal(DayNotRewritten, Assert.Single(_reports).Text);
    }

    [Fact]
    public void A_day_that_could_not_be_rewritten_is_reported_in_fixed_words_without_a_name_or_a_path()
    {
        string file = WriteDay(Day, Entry("a", At(10)));

        using (Hold(file))
        {
            ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report);
        }

        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(DayNotRewritten, text);
        Assert.IsAssignableFrom<IOException>(cause);
    }

    [Fact]
    public void A_failure_is_counted_also_when_nobody_listens_for_the_report()
    {
        string file = WriteDay(Day, Entry("a", At(10)));

        ScrubResult result;
        using (Hold(file))
        {
            result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);
        }

        Assert.Equal(1, result.Failures);
    }

    [Fact]
    public void The_other_days_are_still_scrubbed_when_one_day_file_is_locked()
    {
        // The locked day is the earlier one, so it is met first.
        string locked = WriteDay(DayBefore, Entry("a", On(DayBefore, 10)));
        string kept = Entry("c", At(11));
        string open = WriteDay(Day, Entry("b", At(10)), kept);

        ScrubResult result;
        using (Hold(locked))
        {
            result = ClipScrub.Run(DeviceDir, Ids("a", "b"), clearedBefore: null, Report);
        }

        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(kept + "\n", File.ReadAllText(open));
        Assert.Equal(DayNotRewritten, Assert.Single(_reports).Text);
    }

    [Fact]
    public void A_day_that_could_not_be_rewritten_is_rewritten_by_the_next_scrub()
    {
        string html = Content(Day, "<b>walrus</b>");
        string file = WriteDay(Day, EntryWith("a", At(10), html));
        using (Hold(file))
        {
            ClipScrub.Run(DeviceDir, Ids("a"), clearedBefore: null, Report);
        }

        var result = ClipScrub.Run(DeviceDir, Ids("a"), clearedBefore: null, Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
        Assert.Equal("", File.ReadAllText(file));
        Assert.False(Directory.Exists(DayFolder(Day)));
    }

    [Fact]
    public void A_day_file_that_cannot_be_replaced_is_left_as_it_was_and_no_temporary_file_stays_behind()
    {
        string html = Content(Day, "<b>walrus</b>");
        string file = WriteDay(Day, EntryWith("a", At(10), html), Entry("b", At(11)));
        byte[] before = File.ReadAllBytes(file);

        // Held open by a program that lets everybody else read, write and delete, so the file
        // can be read and could even be written in place, and cannot be replaced: the rewrite
        // gets as far as its last step and fails there. (An earlier form of this test held the
        // file with a mode that shut out writers too, which a write in place would have failed
        // on in the same way, so it could not tell the two apart.) The file is as it was only
        // because the new lines were written elsewhere and never took its place.
        ScrubResult result;
        using (HoldShared(file))
        {
            result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report, Pause);
        }

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(file, Assert.Single(Directory.GetFiles(DeviceDir)));
        Assert.True(HasContent(Day, html));
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(DayNotRewritten, text);
        Assert.True(cause is IOException or UnauthorizedAccessException);

        // Five tries, with a pause of 50, 100, 200 and 400 milliseconds between them.
        Assert.Equal([Ms(50), Ms(100), Ms(200), Ms(400)], _pauses);
    }

    [Fact]
    public void A_day_file_that_another_program_lets_go_of_during_the_pauses_is_replaced_and_is_no_failure()
    {
        string html = Content(Day, "<b>walrus</b>");
        string kept = Entry("b", At(11));
        string file = WriteDay(Day, EntryWith("a", At(10), html), kept);
        var holder = HoldShared(file);

        ScrubResult result;
        try
        {
            result = ClipScrub.RunDay(
                DeviceDir,
                Day,
                Ids("a"),
                clearedBefore: null,
                Report,
                length =>
                {
                    _pauses.Add(length);
                    if (_pauses.Count == 2)
                    {
                        holder.Dispose();
                    }
                });
        }
        finally
        {
            holder.Dispose();
        }

        // Tried at once, after 50 ms, and after another 100 ms, when it went through.
        Assert.Equal([Ms(50), Ms(100)], _pauses);
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
        Assert.Equal(kept + "\n", File.ReadAllText(file));
        Assert.Empty(_reports);
        Assert.Equal(file, Assert.Single(Directory.GetFiles(DeviceDir)));
    }

    [Fact]
    public void No_pause_is_made_when_the_first_try_replaces_the_file()
    {
        WriteDay(Day, Entry("a", At(10)), Entry("b", At(11)));

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report, Pause);

        Assert.Equal(1, result.DaysRewritten);
        Assert.Empty(_pauses);
    }

    [Fact]
    public void Run_hands_the_pause_on_to_every_day()
    {
        string locked = WriteDay(DayBefore, Entry("a", On(DayBefore, 10)));
        using (HoldShared(locked))
        {
            var result = ClipScrub.Run(DeviceDir, Ids("a"), clearedBefore: null, Report, Pause);

            Assert.Equal(1, result.Failures);
        }

        Assert.Equal(4, _pauses.Count);
    }

    [Fact]
    public void Content_that_cannot_be_deleted_is_a_failure_reported_in_fixed_words()
    {
        string html = Content(Day, "<b>walrus</b>");
        string kept = Entry("b", At(11));
        string file = WriteDay(Day, EntryWith("a", At(10), html), kept);

        ScrubResult result;
        using (Hold(Path.Combine(DayFolder(Day), html)))
        {
            result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report);
        }

        // The day was rewritten before its folder was gone through, and that stands.
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(kept + "\n", File.ReadAllText(file));
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(ContentNotDeleted, text);
        Assert.IsAssignableFrom<IOException>(cause);
    }

    [Fact]
    public void Content_that_could_not_be_deleted_is_deleted_by_the_next_scrub()
    {
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html));
        using (Hold(Path.Combine(DayFolder(Day), html)))
        {
            ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report);
        }

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
        Assert.False(Directory.Exists(DayFolder(Day)));
    }

    // ---- A line this build cannot read: what it names stays (item A), a delete reaches it (item B)

    /// <summary>A line of a kind this build does not know, as a newer build might write it, naming a file of HTML content.</summary>
    private static string Unknown(string id, string contentName) =>
        "{ \"id\": \"" + id + "\", \"ts\": \"2026-10-03T10:00:00Z\", \"kind\": \"hologram\", \"html\": \"" + contentName + "\" }";

    [Fact]
    public void Content_that_a_line_of_an_unknown_kind_names_is_kept_with_the_line()
    {
        string html = Content(Day, "<b>walrus</b>");
        string leftOver = Content(Day, "<b>left over</b>");
        string line = Unknown("x", html);
        Assert.Null(ClipLine.Parse(line));
        string file = WriteDay(Day, line);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.True(HasContent(Day, html));
        Assert.False(HasContent(Day, leftOver));
        Assert.Equal(line + "\n", File.ReadAllText(file));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void Content_that_a_line_which_is_not_JSON_at_all_holds_the_name_of_is_kept()
    {
        string html = Content(Day, "<b>walrus</b>");
        string text = Content(Day, "the whole of a long text", ClipPaths.TextExtension);
        string picture = Content(Day, "a picture", ".jpeg");
        WriteDay(Day, "copied, see " + html + ", and " + text + ";" + picture + ".");

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.True(HasContent(Day, html));
        Assert.True(HasContent(Day, text));
        Assert.True(HasContent(Day, picture));
        Assert.Equal(ScrubResult.Nothing, result);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("Zq9")]
    [InlineData("0123456789abcdef")]
    [InlineData("the-file-is-")]
    public void A_content_name_that_stands_glued_to_letters_or_digits_in_front_of_it_is_still_named_by_the_line_that_holds_it(string glued)
    {
        string html = Content(Day, "<b>walrus</b>");
        string leftOver = Content(Day, "<b>left over</b>");
        string line = "copied, see " + glued + html + " and no more";
        string file = WriteDay(Day, line);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.True(HasContent(Day, html));
        Assert.False(HasContent(Day, leftOver));
        Assert.Equal(line + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void Content_names_that_run_into_each_other_are_each_named_by_the_line_that_holds_them()
    {
        string html = Content(Day, "<b>walrus</b>");
        string text = Content(Day, "the whole of a long text", ClipPaths.TextExtension);
        string picture = Content(Day, "a picture", ".jpeg");
        string leftOver = Content(Day, "<b>left over</b>");
        WriteDay(Day, html + text + picture + "tail");

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.True(HasContent(Day, html));
        Assert.True(HasContent(Day, text));
        Assert.True(HasContent(Day, picture));
        Assert.False(HasContent(Day, leftOver));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void A_name_that_has_the_extension_of_a_picture_after_a_longer_run_of_digits_is_named_too()
    {
        string picture = Content(Day, "a picture", ".gif");
        WriteDay(Day, "{\"id\":\"1234567890\"," + picture + "}");

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.True(HasContent(Day, picture));
        Assert.Equal(ScrubResult.Nothing, result);
    }

    [Theory]
    [InlineData(".html")]
    [InlineData(".png")]
    public void A_part_of_a_name_or_a_name_in_other_letters_does_not_name_a_file(string extension)
    {
        string name = Content(Day, "<b>walrus</b>", extension);
        string hash = name[..64];

        // 63 digits, the hash in capitals, and the hash with a letter that is not a hex digit: none is the name.
        string[] notNames = [hash[1..] + extension, hash.ToUpperInvariant() + extension, "g" + hash[1..] + extension];
        WriteDay(Day, string.Join(" ", notNames));

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null);

        Assert.False(HasContent(Day, name));
        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void Content_that_a_line_of_an_unknown_kind_names_goes_when_a_clear_covers_the_line()
    {
        string html = Content(Day, "<b>walrus</b>");
        string clearing = Clearing("c", At(12));
        string file = WriteDay(Day, Unknown("x", html), clearing);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, At(12));

        Assert.Equal(clearing + "\n", File.ReadAllText(file));
        Assert.False(Directory.Exists(DayFolder(Day)));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 0, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void A_line_this_build_cannot_read_goes_when_a_delete_names_its_id_and_its_content_goes_with_it()
    {
        string html = Content(Day, "<b>walrus</b>");
        string kept = Entry("b", At(11));
        string file = WriteDay(Day, Unknown("x", html), kept);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("x"), clearedBefore: null);

        Assert.Equal(kept + "\n", File.ReadAllText(file));
        Assert.False(Directory.Exists(DayFolder(Day)));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
    }

    [Fact]
    public void The_content_of_a_line_that_a_delete_takes_stays_when_another_line_that_stays_names_it()
    {
        string html = Content(Day, "<b>walrus</b>");
        string kept = EntryWith("b", At(11), html);
        string file = WriteDay(Day, Unknown("x", html), kept);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("x"), clearedBefore: null);

        Assert.Equal(kept + "\n", File.ReadAllText(file));
        Assert.True(HasContent(Day, html));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void A_line_this_build_cannot_read_stays_with_its_content_when_no_delete_names_its_id()
    {
        string html = Content(Day, "<b>walrus</b>");
        string line = Unknown("x", html);
        string file = WriteDay(Day, Entry("a", At(10)), line);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a", "y"), clearedBefore: null);

        Assert.Equal(line + "\n", File.ReadAllText(file));
        Assert.True(HasContent(Day, html));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Fact]
    public void The_id_of_a_line_this_build_cannot_read_is_read_as_JSON_reads_text()
    {
        // "\u0078" is an "x" written as an escape.
        string line = "{ \"id\": \"\\u0078\", \"kind\": \"hologram\" }";
        Assert.Null(ClipLine.Parse(line));
        string file = WriteDay(Day, line, Entry("a", At(10)));

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("x"), clearedBefore: null);

        Assert.Equal(Entry("a", At(10)) + "\n", File.ReadAllText(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 0), result);
    }

    [Theory]
    [InlineData("""["x"]""")]
    [InlineData("\"x\"")]
    [InlineData("""{ "id": 5, "kind": "hologram" }""")]
    [InlineData("""{ "id": ["x"], "kind": "hologram" }""")]
    [InlineData("""{ "id": null, "kind": "hologram" }""")]
    [InlineData("""{ "Id": "x", "kind": "hologram" }""")]
    [InlineData("""{ "identity": "x" }""")]
    [InlineData("""{ "nested": { "id": "x" } }""")]
    [InlineData("""{ "id": "x", "kind": "hologram" """)]
    [InlineData("x")]
    [InlineData("""{ "id": "\ud800", "kind": "hologram" }""")]
    public void A_line_that_is_not_an_object_with_a_text_id_stays_even_when_a_delete_names_a_like_id(string line)
    {
        // Every row cannot be read as a record, and each is something that could go wrong in a
        // lenient reading: no object, an id that is no text, a key in capitals, an id one level
        // down, a line cut short, half of a surrogate pair written as an escape.
        Assert.Null(ClipLine.Parse(line));
        string file = WriteDay(Day, line);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("x", "5"), clearedBefore: null);

        Assert.Equal(line + "\n", File.ReadAllText(file));
        Assert.Equal(ScrubResult.Nothing, result);
    }

    [Fact]
    public void A_line_nested_too_deep_to_read_stays_and_does_not_make_the_scrub_throw()
    {
        string line = "{ \"id\": \"x\", \"deep\": " + new string('[', 10_000) + new string(']', 10_000) + " }";
        string file = WriteDay(Day, line);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("x"), clearedBefore: null);

        Assert.Equal(line + "\n", File.ReadAllText(file));
        Assert.Equal(ScrubResult.Nothing, result);
    }

    // ---- Links are not followed (item D)

    [Fact]
    public void A_machine_folder_that_is_a_link_is_left_alone_and_is_one_failure()
    {
        string[] files = MakeJunctionTarget("elsewhere");
        string elsewhere = Path.Combine(_dir.Path, "elsewhere");
        Junction(DeviceDir, elsewhere);
        string[] before = Snapshot(elsewhere);

        var result = ClipScrub.Run(DeviceDir, Ids("a"), At(23), Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(FolderIsALink, text);
        Assert.Null(cause);
        Assert.All(files, file => Assert.True(File.Exists(file)));
        Assert.Equal(before, Snapshot(elsewhere));
        Assert.True(Directory.Exists(DeviceDir));
    }

    [Fact]
    public void A_single_day_of_a_machine_folder_that_is_a_link_is_left_alone_too()
    {
        MakeJunctionTarget("elsewhere");
        string elsewhere = Path.Combine(_dir.Path, "elsewhere");
        Junction(DeviceDir, elsewhere);
        string[] before = Snapshot(elsewhere);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), At(23), Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(FolderIsALink, Assert.Single(_reports).Text);
        Assert.Equal(before, Snapshot(elsewhere));
    }

    [Fact]
    public void A_day_folder_that_is_a_link_is_left_alone_but_the_day_file_beside_it_is_rewritten_and_the_link_is_one_failure_in_fixed_words()
    {
        string[] files = MakeJunctionTarget("elsewhere");
        string elsewhere = Path.Combine(_dir.Path, "elsewhere");
        string kept = Entry("b", At(11));
        string dayFile = WriteDay(Day, Entry("a", At(10)), kept);
        Junction(DayFolder(Day), elsewhere);
        string[] before = Snapshot(elsewhere);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null, Report);

        // What a delete would have left behind the link is not passed over in silence.
        Assert.Equal(kept + "\n", File.ReadAllText(dayFile));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 1), result);
        Assert.True(Directory.Exists(DayFolder(Day)));
        Assert.All(files, file => Assert.True(File.Exists(file)));
        Assert.Equal(before, Snapshot(elsewhere));
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(DayFolderIsALink, text);
        Assert.Null(cause);
    }

    [Fact]
    public void A_day_folder_that_is_a_link_is_left_alone_by_a_scrub_of_the_whole_machine_folder_and_is_one_failure()
    {
        MakeJunctionTarget("elsewhere");
        string elsewhere = Path.Combine(_dir.Path, "elsewhere");
        WriteDay(Day, Entry("a", At(10)));
        Junction(DayFolder(Day), elsewhere);
        string[] before = Snapshot(elsewhere);

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        Assert.True(Directory.Exists(DayFolder(Day)));
        Assert.Equal(before, Snapshot(elsewhere));
        Assert.Equal(DayFolderIsALink, Assert.Single(_reports).Text);
    }

    [Fact]
    public void Several_day_folders_that_are_links_are_each_a_failure_and_are_told_once_for_the_run()
    {
        MakeJunctionTarget("elsewhere");
        MakeJunctionTarget("elsewhere-too");
        WriteDay(Day, Entry("a", At(10)));
        WriteDay(DayBefore, Entry("b", On(DayBefore, 10)));
        Junction(DayFolder(Day), Path.Combine(_dir.Path, "elsewhere"));
        Junction(DayFolder(DayBefore), Path.Combine(_dir.Path, "elsewhere-too"));

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);

        Assert.Equal(2, result.Failures);
        Assert.Equal(DayFolderIsALink, Assert.Single(_reports).Text);
    }

    [Fact]
    public void A_day_folder_that_is_a_link_and_has_no_day_file_beside_it_is_one_failure_too()
    {
        MakeJunctionTarget("elsewhere");
        Junction(DayFolder(Day), Path.Combine(_dir.Path, "elsewhere"));

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);

        Assert.Equal(new ScrubResult(0, 0, 0, 1), result);
        Assert.Equal(DayFolderIsALink, Assert.Single(_reports).Text);
    }

    [Fact]
    public void A_day_folder_that_is_a_link_is_one_failure_also_when_nobody_listens_for_the_report()
    {
        MakeJunctionTarget("elsewhere");
        WriteDay(Day, Entry("a", At(10)));
        Junction(DayFolder(Day), Path.Combine(_dir.Path, "elsewhere"));

        Assert.Equal(1, ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null).Failures);
    }

    // ---- Only the temporary files this code writes are removed (item E)

    [Fact]
    public void A_temporary_file_named_for_content_beside_the_day_files_is_not_ours_and_stays()
    {
        WriteDay(Day, Entry("a", At(10)));
        string file = Path.Combine(DeviceDir, ClipPaths.BlobName([1, 2, 3], ClipPaths.HtmlExtension) + ClipScrub.TemporaryExtension);
        File.WriteAllText(file, "otter");
        File.SetLastWriteTimeUtc(file, LongAgo);

        var result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);

        Assert.Equal("otter", File.ReadAllText(file));
        Assert.Equal(ScrubResult.Nothing, result);
    }

    [Fact]
    public void A_temporary_file_named_for_a_day_file_in_a_day_folder_is_not_ours_and_stays_with_its_folder()
    {
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html));
        string file = InFolder(Day, ClipPaths.DayFileName(Day) + ClipScrub.TemporaryExtension);

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.Equal(file, Assert.Single(Directory.GetFileSystemEntries(DayFolder(Day))));
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
    }

    // ---- One file that cannot be deleted does not stop the rest (item F)

    [Fact]
    public void Content_files_that_cannot_be_deleted_do_not_stop_the_others_and_are_told_once()
    {
        // A directory listing on NTFS comes in the order of the names, so the first two of the
        // three are the ones that are met first, and the ones that are held.
        string[] names = new[] { Content(Day, "<b>one</b>"), Content(Day, "<b>two</b>"), Content(Day, "<b>three</b>") }
            .Order(StringComparer.Ordinal)
            .ToArray();
        WriteDay(Day, Entry("a", At(10)));

        ScrubResult result;
        using (Hold(Path.Combine(DayFolder(Day), names[0])))
        using (Hold(Path.Combine(DayFolder(Day), names[1])))
        {
            result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, clearedBefore: null, Report);
        }

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 1, Failures: 2), result);
        Assert.True(HasContent(Day, names[0]));
        Assert.True(HasContent(Day, names[1]));
        Assert.False(HasContent(Day, names[2]));
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(ContentNotDeleted, text);
        Assert.IsAssignableFrom<IOException>(cause);
    }

    [Fact]
    public void Run_tells_each_kind_of_failure_once_however_many_days_it_met_it_on()
    {
        string first = Content(DayBefore, "<b>one</b>");
        string second = Content(Day, "<b>two</b>");
        WriteDay(DayBefore, Entry("a", On(DayBefore, 10)));
        WriteDay(Day, Entry("b", At(10)));

        ScrubResult result;
        using (Hold(Path.Combine(DayFolder(DayBefore), first)))
        using (Hold(Path.Combine(DayFolder(Day), second)))
        {
            result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);
        }

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 2), result);
        Assert.Equal(ContentNotDeleted, Assert.Single(_reports).Text);
    }

    // ---- A leftover temporary file that cannot be removed is counted (item G)

    [Fact]
    public void A_leftover_temporary_file_that_cannot_be_removed_is_a_failure_told_once_and_is_removed_by_the_next_scrub()
    {
        string file = WriteDay(Day, Entry("a", At(10)));
        string temporary = file + ClipScrub.TemporaryExtension;
        File.WriteAllText(temporary, Entry("gone", At(9)) + "\n");

        ScrubResult result;
        using (Hold(temporary))
        {
            result = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);
        }

        Assert.Equal(new ScrubResult(DaysRewritten: 0, EntriesRemoved: 0, FilesRemoved: 0, Failures: 1), result);
        var (text, cause) = Assert.Single(_reports);
        Assert.Equal(TemporaryNotDeleted, text);
        Assert.True(cause is IOException or UnauthorizedAccessException);
        Assert.True(File.Exists(temporary));

        var next = ClipScrub.Run(DeviceDir, NothingDeleted, clearedBefore: null, Report);

        Assert.Equal(ScrubResult.Nothing, next);
        Assert.False(File.Exists(temporary));
        Assert.Single(_reports);
    }

    [Fact]
    public void A_leftover_temporary_file_that_cannot_be_removed_does_not_stop_the_days_from_being_scrubbed()
    {
        string earlier = WriteDay(DayBefore, Entry("a", On(DayBefore, 10)));
        string temporary = earlier + ClipScrub.TemporaryExtension;
        File.WriteAllText(temporary, Entry("gone", On(DayBefore, 9)) + "\n");
        string kept = Entry("c", At(11));
        string file = WriteDay(Day, Entry("b", At(10)), kept);

        ScrubResult result;
        using (Hold(temporary))
        {
            result = ClipScrub.Run(DeviceDir, Ids("b"), clearedBefore: null, Report);
        }

        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 0, Failures: 1), result);
        Assert.Equal(kept + "\n", File.ReadAllText(file));
    }

    // ---- Tests the review found missing (item I)

    [Fact]
    public void The_folder_of_another_machine_is_not_touched_even_when_a_delete_names_one_of_its_entries()
    {
        string other = Path.Combine(_dir.Path, "clipboard", "WALRUS-PC");
        string otherDayFolder = Path.Combine(other, ClipPaths.DayName(Day));
        Directory.CreateDirectory(otherDayFolder);
        byte[] bytes = Encoding.UTF8.GetBytes("<b>otter</b>");
        string[] files =
        [
            Path.Combine(other, ClipPaths.DayFileName(Day)),
            Path.Combine(other, ClipPaths.DayFileName(Day) + ClipScrub.TemporaryExtension),
            Path.Combine(otherDayFolder, ClipPaths.BlobName(bytes, ClipPaths.HtmlExtension)),
        ];
        File.WriteAllText(files[0], Entry("o", At(10)) + "\n" + Entry("p", At(11)) + "\n");
        File.WriteAllText(files[1], Entry("gone", At(9)) + "\n");
        File.WriteAllBytes(files[2], bytes);
        foreach (string file in files)
        {
            File.SetLastWriteTimeUtc(file, LongAgo);
        }

        WriteDay(Day, Entry("a", At(10)), Entry("b", At(11)));
        string[] before = Snapshot(other);

        // The delete names an entry of the other machine, and the clear covers all of them.
        var result = ClipScrub.Run(DeviceDir, Ids("a", "o"), At(23));

        Assert.Equal(1, result.DaysRewritten);
        Assert.Equal(before, Snapshot(other));
    }

    [Fact]
    public void A_folder_inside_a_day_folder_is_left_and_so_is_the_day_folder()
    {
        string html = Content(Day, "<b>walrus</b>");
        WriteDay(Day, EntryWith("a", At(10), html));
        string inner = Path.Combine(DayFolder(Day), "inner");
        Directory.CreateDirectory(inner);
        string inside = Path.Combine(inner, ClipPaths.BlobName([1, 2, 3], ClipPaths.HtmlExtension));
        File.WriteAllText(inside, "otter");

        var result = ClipScrub.RunDay(DeviceDir, Day, Ids("a"), clearedBefore: null);

        Assert.True(File.Exists(inside));
        Assert.True(Directory.Exists(DayFolder(Day)));
        Assert.False(HasContent(Day, html));
        Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 1, FilesRemoved: 1, Failures: 0), result);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(0, 23, 59, 59, false)]
    [InlineData(1, 0, 0, 0, true)]
    [InlineData(1, 12, 0, 0, true)]
    public void A_day_file_of_only_lines_that_cannot_be_read_is_rewritten_only_when_a_clear_reaches_the_end_of_its_day(
        int daysAfter, int hour, int minute, int second, bool rewritten)
    {
        // The row at the start of the day after is a clear at the very moment the day ends.
        string text = Garbage + "\n" + Garbage + " again\n";
        string file = WriteDayText(Day, text);

        var result = ClipScrub.RunDay(DeviceDir, Day, NothingDeleted, On(Day.AddDays(daysAfter), hour, minute, second));

        if (rewritten)
        {
            Assert.Equal("", File.ReadAllText(file));
            Assert.Equal(new ScrubResult(DaysRewritten: 1, EntriesRemoved: 0, FilesRemoved: 0, Failures: 0), result);
        }
        else
        {
            Assert.Equal(text, File.ReadAllText(file));
            Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(file));
            Assert.Equal(ScrubResult.Nothing, result);
        }
    }

    [Fact]
    public void Results_are_added_count_by_count()
    {
        var sum = new ScrubResult(1, 2, 3, 4).Plus(new ScrubResult(10, 20, 30, 40));

        Assert.Equal(new ScrubResult(DaysRewritten: 11, EntriesRemoved: 22, FilesRemoved: 33, Failures: 44), sum);
        Assert.Equal(new ScrubResult(0, 0, 0, 0), ScrubResult.Nothing);
    }
}
