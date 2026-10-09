using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Launcher;
using Wyvern.Core.Platform;

namespace Wyvern.Tests;

/// <summary>
/// A record prints all of its members by default, and a record that holds what the owner copied,
/// or names a program or a file, would print that wherever it is formatted into a string (an
/// assertion message, a debugger, a log line somebody adds later). Each of those types prints a
/// short fixed form instead: its name and at most counts. Everything here is made up.
/// </summary>
public class ClipRecordToStringTests
{
    // Made up, and each one distinct, so that any of them in a printed record is seen.
    private const string Secret = "the-secret-text-of-the-walrus";
    private const string Path1 = @"C:\Otters\private-tusks.png";
    private const string Path2 = @"D:\Seals\private-flippers.docx";
    private const string Name1 = "private-tusks.png";
    private const string Program = "private-otter.exe";
    private const string Label = "the-private-label-of-a-link";
    private const string Id = "id-0f3a9c11-private";
    private const string Folder = @"C:\Wyvern\clipboard\PRIVATE-PC\2026-10-03";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string RawLine = """{"id":"id-0f3a9c11-private","ts":"2026-10-03T14:02:11Z","kind":"text","text":"the-secret-text-of-the-walrus"}""";

    private static readonly DateTimeOffset Time = new(2026, 10, 3, 14, 2, 11, TimeSpan.Zero);

    private static readonly string[] Everything =
    [
        Secret, Path1, Path2, Name1, "private-flippers", Program, "private-otter", Label, Id, Folder, "PRIVATE-PC", Hash, RawLine,
        "private", "walrus", "otter", "tusks",
    ];

    private static void AssertPrintsNone(string printed)
    {
        foreach (string text in Everything)
        {
            Assert.DoesNotContain(text, printed, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ClipEntry MadeUpEntry() =>
        new(Id, Time, "PRIVATE-PC", new DateOnly(2026, 10, 3), Folder)
        {
            SourceApp = Program,
            Text = Secret,
            TextLength = Secret.Length,
            Html = Hash + ".html",
            Link = "https://example.org/private",
            Label = Label,
            Files = [Path1, Path2],
            Pictures = [new KeptPicture(Name1, Hash + ".png")],
        };

    private static EntryRecord MadeUpRecord() =>
        new(Id, Time, Program)
        {
            Text = Secret,
            TextLength = Secret.Length,
            Html = Hash + ".html",
            Label = Label,
            Files = [Path1, Path2],
            Pictures = [new KeptPicture(Name1, Hash + ".png")],
        };

    [Fact]
    public void A_row_prints_its_name_only()
    {
        var row = new ClipRow(MadeUpEntry(), Secret, Label, "Link  HTML", Program, "14:02");

        string printed = row.ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClipRow", printed);
    }

    [Fact]
    public void An_entry_prints_its_name_and_how_many_files_and_pictures_it_has()
    {
        string printed = MadeUpEntry().ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClipEntry { Files = 2, Pictures = 1 }", printed);
    }

    [Fact]
    public void An_entry_with_nothing_but_its_names_prints_counts_of_nothing()
    {
        string printed = new ClipEntry("id", Time, "PRIVATE-PC", new DateOnly(2026, 10, 3), Folder).ToString();

        Assert.Equal("ClipEntry { Files = 0, Pictures = 0 }", printed);
    }

    [Fact]
    public void A_line_of_a_day_file_prints_its_name_and_its_number_and_not_the_line()
    {
        var line = new ClipFileLine(12, RawLine, MadeUpRecord());

        string printed = line.ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClipFileLine { Number = 12 }", printed);
    }

    [Fact]
    public void A_line_of_a_day_file_that_cannot_be_read_prints_the_same_way()
    {
        string printed = new ClipFileLine(3, Secret, null).ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClipFileLine { Number = 3 }", printed);
    }

    [Fact]
    public void The_record_of_an_entry_prints_its_name_and_how_many_files_and_pictures_it_has()
    {
        string printed = MadeUpRecord().ToString();

        AssertPrintsNone(printed);
        Assert.Equal("EntryRecord { Files = 2, Pictures = 1 }", printed);
    }

    [Fact]
    public void The_record_of_a_delete_prints_its_name_only()
    {
        string printed = new DeleteRecord(Id, Time, "target-" + Id).ToString();

        AssertPrintsNone(printed);
        Assert.DoesNotContain("target-", printed);
        Assert.Equal("DeleteRecord", printed);
    }

    [Fact]
    public void The_record_of_a_clear_prints_its_name_only()
    {
        string printed = new ClearRecord(Id, Time, Time.AddDays(-1)).ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClearRecord", printed);
    }

    [Fact]
    public void Any_record_of_a_line_prints_no_more_than_its_own_name_through_the_base_type()
    {
        ClipRecord[] records = [MadeUpRecord(), new DeleteRecord(Id, Time, Id), new ClearRecord(Id, Time, Time)];

        foreach (var record in records)
        {
            AssertPrintsNone(record.ToString());
            AssertPrintsNone($"{record}");
        }
    }

    [Fact]
    public void A_kept_picture_prints_its_name_only()
    {
        string printed = new KeptPicture(Name1, Hash + ".png").ToString();

        AssertPrintsNone(printed);
        Assert.Equal("KeptPicture", printed);
    }

    [Fact]
    public void A_draft_prints_its_name_and_how_many_files_and_pictures_it_has()
    {
        var draft = new ClipDraft(Program)
        {
            Text = Secret,
            Html = System.Text.Encoding.UTF8.GetBytes(Secret),
            Rtf = System.Text.Encoding.UTF8.GetBytes(Secret),
            ImagePng = System.Text.Encoding.UTF8.GetBytes(Secret),
            Label = Label,
            Files = [Path1, Path2],
            Pictures = [new DraftPicture(Name1, ".png", System.Text.Encoding.UTF8.GetBytes(Secret))],
        };

        string printed = draft.ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClipDraft { Files = 2, Pictures = 1 }", printed);
    }

    [Fact]
    public void A_picture_of_a_draft_prints_its_name_and_its_size_and_not_its_file_name_or_bytes()
    {
        string printed = new DraftPicture(Name1, ".png", System.Text.Encoding.UTF8.GetBytes(Secret)).ToString();

        AssertPrintsNone(printed);
        Assert.Equal($"DraftPicture {{ Bytes = {Secret.Length} }}", printed);
    }

    [Fact]
    public void The_owner_of_the_clipboard_prints_its_name_only()
    {
        string printed = new ClipboardOwner(Program, false).ToString();

        AssertPrintsNone(printed);
        Assert.Equal("ClipboardOwner", printed);
    }

    [Fact]
    public void A_block_of_the_clipboard_prints_its_name_and_its_size_and_not_its_bytes_or_its_format()
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(Secret);
        var block = new ClipboardBlock(ClipboardFormat.Html, bytes);

        string printed = block.ToString();

        AssertPrintsNone(printed);
        Assert.DoesNotContain("HTML", printed);
        Assert.Equal($"ClipboardBlock {{ Bytes = {Secret.Length} }}", printed);
    }
}
