using System.Globalization;
using System.Text.Json;
using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

/// <summary>
/// Every line here is written on one line with its escapes spelled out, and the characters a
/// tool could change on the way into this file (half of a surrogate pair, U+FFFD, an emoji, the
/// escape for half a pair) are built from their numbers. What is tested therefore does not
/// depend on how this source file is stored.
/// </summary>
public class ClipLineTests
{
    // Every lower-case hex digit, four times over: the 64 characters a SHA-256 is written in.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private const string Link = "https://example.org/otters";

    private const char HighHalf = (char)0xD83D;
    private const char LowHalf = (char)0xDE00;
    private const char Replacement = (char)0xFFFD;
    private const char EAcute = (char)0xE9;

    private static readonly DateTimeOffset Time = new(2026, 10, 3, 14, 2, 11, TimeSpan.Zero);

    // U+1F600, which takes two UTF-16 characters: the two halves above.
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    // A backslash and a "u": how JSON starts the escape for a character given by its number.
    private static readonly string BackslashU = new(['\\', 'u']);

    /// <summary>Puts in what cannot be written in an attribute: the hash, and the escapes for half a surrogate pair.</summary>
    private static string Fill(string template) => template
        .Replace("<hash>", Hash)
        .Replace("<HASH>", Hash.ToUpperInvariant())
        .Replace("<high>", BackslashU + "d800")
        .Replace("<low>", BackslashU + "dc00");

    /// <summary>
    /// The line of an entry: the id "a1", the time 14:02:11 and the kind "text", and after them
    /// the keys of <paramref name="forms"/>, which is written as a JSON object of its own.
    /// </summary>
    private static string Entry(string forms)
    {
        Assert.StartsWith("{\"", forms, StringComparison.Ordinal);
        return Fill("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text",""" + forms[1..]);
    }

    private static EntryRecord TextEntry(string text) => new("a1", Time, "otter.exe") { Text = text, TextLength = text.Length };

    private static EntryRecord Everything() => new("e1", Time, "explorer.exe")
    {
        Text = Link,
        TextLength = 250_000,
        TextFile = Hash + ".txt",
        Html = Hash + ".html",
        Rtf = Hash + ".rtf",
        Image = Hash + ".png",
        Files = [@"C:\Otters\walrus.png", @"C:\Otters\notes.txt"],
        Pictures = [new KeptPicture("walrus.png", OtherHash + ".png")],
        Label = "Otter pictures",
    };

    private static EntryRecord ReadEntry(string line) => Assert.IsType<EntryRecord>(ClipLine.Parse(line));

    /// <summary>Compares field by field: two records with equal lists in them are not equal as records.</summary>
    private static void AssertSame(EntryRecord expected, ClipRecord? read)
    {
        var actual = Assert.IsType<EntryRecord>(read);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.SourceApp, actual.SourceApp);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.TextLength, actual.TextLength);
        Assert.Equal(expected.TextFile, actual.TextFile);
        Assert.Equal(expected.Html, actual.Html);
        Assert.Equal(expected.Rtf, actual.Rtf);
        Assert.Equal(expected.Image, actual.Image);
        Assert.Equal(expected.Files, actual.Files);
        Assert.Equal(expected.Pictures, actual.Pictures);
        Assert.Equal(expected.Label, actual.Label);
    }

    /// <summary>Runs something while the thread has another culture, and puts the old one back.</summary>
    private static T InCulture<T>(CultureInfo culture, Func<T> act)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // The four shapes the format started with. Another reader may know only these, so each is
    // compared as a whole line.

    [Fact]
    public void A_plain_text_entry_is_written_exactly_as_the_format_started_with()
    {
        var record = new EntryRecord("a1", new DateTimeOffset(2026, 10, 3, 14, 2, 11, TimeSpan.Zero), "chrome.exe") { Text = "hello", TextLength = 5 };

        Assert.Equal(
            """{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","sourceApp":"chrome.exe"}""",
            ClipLine.Write(record));
    }

    [Fact]
    public void A_picture_entry_is_written_exactly_as_the_format_started_with()
    {
        var record = new EntryRecord("a2", new DateTimeOffset(2026, 10, 3, 14, 3, 40, TimeSpan.Zero), "snippingtool.exe") { Image = Hash + ".png" };

        Assert.Equal(
            $$"""{"id":"a2","ts":"2026-10-03T14:03:40Z","kind":"image","image":"{{Hash}}.png","sourceApp":"snippingtool.exe"}""",
            ClipLine.Write(record));
    }

    [Fact]
    public void A_delete_record_is_written_exactly_as_the_format_started_with()
    {
        var record = new DeleteRecord("a3", new DateTimeOffset(2026, 10, 3, 14, 5, 2, TimeSpan.Zero), "a1");

        Assert.Equal(
            """{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":"a1"}""",
            ClipLine.Write(record));
    }

    [Fact]
    public void A_clear_record_is_written_exactly_as_the_format_started_with()
    {
        var moment = new DateTimeOffset(2026, 10, 3, 14, 9, 30, TimeSpan.Zero);
        var record = new ClearRecord("a4", moment, moment);

        Assert.Equal(
            """{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"2026-10-03T14:09:30Z"}""",
            ClipLine.Write(record));
    }

    [Fact]
    public void A_plain_text_line_of_the_first_format_is_read()
    {
        var entry = ReadEntry("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","sourceApp":"chrome.exe"}""");

        Assert.Equal("a1", entry.Id);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, TimeSpan.Zero), entry.Time);
        Assert.Equal("hello", entry.Text);
        Assert.Equal(5, entry.TextLength);
        Assert.Equal("chrome.exe", entry.SourceApp);
        Assert.Null(entry.TextFile);
        Assert.Null(entry.Html);
        Assert.Null(entry.Rtf);
        Assert.Null(entry.Image);
        Assert.Empty(entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Null(entry.Label);
    }

    [Fact]
    public void A_picture_line_of_the_first_format_is_read()
    {
        var entry = ReadEntry($$"""{"id":"a2","ts":"2026-10-03T14:03:40Z","kind":"image","image":"{{Hash}}.png","sourceApp":"snippingtool.exe"}""");

        Assert.Equal("a2", entry.Id);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 3, 40, TimeSpan.Zero), entry.Time);
        Assert.Equal(Hash + ".png", entry.Image);
        Assert.Equal("snippingtool.exe", entry.SourceApp);
        Assert.Null(entry.Text);
        Assert.Equal(0, entry.TextLength);
    }

    [Fact]
    public void A_delete_line_of_the_first_format_is_read()
    {
        var record = ClipLine.Parse("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":"a1"}""");

        Assert.Equal(new DeleteRecord("a3", new DateTimeOffset(2026, 10, 3, 14, 5, 2, TimeSpan.Zero), "a1"), record);
    }

    [Fact]
    public void A_clear_line_of_the_first_format_is_read()
    {
        var record = ClipLine.Parse("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"2026-10-03T14:09:30Z"}""");

        var moment = new DateTimeOffset(2026, 10, 3, 14, 9, 30, TimeSpan.Zero);
        Assert.Equal(new ClearRecord("a4", moment, moment), record);
    }

    // Writing a record and reading the line gives the record back, for every kind.

    [Fact]
    public void A_text_entry_comes_back_as_it_was_written()
    {
        var record = TextEntry("walrus");

        string line = ClipLine.Write(record);

        Assert.Contains("\"kind\":\"text\"", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void An_entry_whose_text_is_in_a_file_comes_back_with_the_file_and_the_whole_length()
    {
        var record = new EntryRecord("a1", Time, "otter.exe") { Text = "the start of a long walrus", TextLength = 250_000, TextFile = Hash + ".txt" };

        string line = ClipLine.Write(record);

        Assert.Contains("\"kind\":\"text\"", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void A_picture_entry_comes_back_as_it_was_written()
    {
        var record = new EntryRecord("a1", Time, "otter.exe") { Image = Hash + ".png" };

        string line = ClipLine.Write(record);

        Assert.Contains("\"kind\":\"image\"", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void A_files_entry_comes_back_with_its_paths_and_kept_pictures_in_order()
    {
        var record = new EntryRecord("a1", Time, "explorer.exe")
        {
            Files = [@"C:\Otters\walrus.png", @"C:\Otters\notes.txt", @"C:\Otters\otter.JPG"],
            Pictures = [new KeptPicture("walrus.png", Hash + ".png"), new KeptPicture("otter.JPG", OtherHash + ".jpg")],
        };

        string line = ClipLine.Write(record);

        Assert.Contains("\"kind\":\"files\"", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void An_html_entry_comes_back_as_it_was_written()
    {
        var record = new EntryRecord("a1", Time, null) { Html = Hash + ".html" };

        string line = ClipLine.Write(record);

        Assert.Contains("\"kind\":\"html\"", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void A_rich_text_entry_comes_back_as_it_was_written()
    {
        var record = new EntryRecord("a1", Time, null) { Rtf = Hash + ".rtf" };

        string line = ClipLine.Write(record);

        Assert.Contains("\"kind\":\"rtf\"", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void An_entry_with_every_form_at_once_comes_back_with_all_of_them()
    {
        var record = Everything();

        AssertSame(record, ClipLine.Parse(ClipLine.Write(record)));
    }

    [Fact]
    public void A_delete_record_comes_back_as_it_was_written()
    {
        var record = new DeleteRecord("d1", Time, "a1");

        Assert.Equal(record, ClipLine.Parse(ClipLine.Write(record)));
    }

    [Fact]
    public void A_clear_record_comes_back_as_it_was_written()
    {
        var record = new ClearRecord("c1", Time, new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(record, ClipLine.Parse(ClipLine.Write(record)));
    }

    [Fact]
    public void The_keys_of_an_entry_come_in_a_fixed_order()
    {
        string line = ClipLine.Write(Everything());

        using var document = JsonDocument.Parse(line);
        Assert.Equal(
            ["id", "ts", "kind", "text", "textFile", "textLength", "html", "rtf", "image", "files", "pictures", "label", "sourceApp"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void An_entry_with_every_form_is_one_object_with_a_key_for_each()
    {
        Assert.Equal(
            $$"""{"id":"e1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"https://example.org/otters","textFile":"{{Hash}}.txt","textLength":250000,"html":"{{Hash}}.html","rtf":"{{Hash}}.rtf","image":"{{Hash}}.png","files":["C:\\Otters\\walrus.png","C:\\Otters\\notes.txt"],"pictures":[{"name":"walrus.png","file":"{{OtherHash}}.png"}],"label":"Otter pictures","sourceApp":"explorer.exe"}""",
            ClipLine.Write(Everything()));
    }

    [Fact]
    public void The_whole_length_of_a_text_is_written_only_when_the_text_is_in_a_file()
    {
        Assert.DoesNotContain("textLength", ClipLine.Write(TextEntry("walrus")), StringComparison.Ordinal);
        Assert.Contains(
            "\"textLength\":250000",
            ClipLine.Write(new EntryRecord("a1", Time, null) { Text = "walrus", TextLength = 250_000, TextFile = Hash + ".txt" }),
            StringComparison.Ordinal);
    }

    // The word in "kind".

    [Fact]
    public void An_entry_with_text_is_text_whatever_else_it_holds()
    {
        Assert.Equal("text", ClipLine.Kind(Everything()));
    }

    [Fact]
    public void An_entry_whose_text_is_empty_is_still_text()
    {
        Assert.Equal("text", ClipLine.Kind(new EntryRecord("a1", Time, null) { Text = "", Image = Hash + ".png" }));
    }

    [Fact]
    public void An_entry_that_has_only_the_file_of_its_text_is_text()
    {
        Assert.Equal("text", ClipLine.Kind(new EntryRecord("a1", Time, null) { TextFile = Hash + ".txt", TextLength = 250_000, Image = Hash + ".png" }));
    }

    [Fact]
    public void Without_text_an_entry_with_a_picture_is_an_image()
    {
        var record = new EntryRecord("a1", Time, null)
        {
            Image = Hash + ".png",
            Files = [@"C:\Otters\walrus.png"],
            Html = Hash + ".html",
            Rtf = Hash + ".rtf",
        };

        Assert.Equal("image", ClipLine.Kind(record));
    }

    [Fact]
    public void Without_text_or_a_picture_an_entry_with_copied_files_is_files()
    {
        var record = new EntryRecord("a1", Time, null)
        {
            Files = [@"C:\Otters\walrus.png"],
            Html = Hash + ".html",
            Rtf = Hash + ".rtf",
        };

        Assert.Equal("files", ClipLine.Kind(record));
    }

    [Fact]
    public void Html_comes_before_rich_text()
    {
        Assert.Equal("html", ClipLine.Kind(new EntryRecord("a1", Time, null) { Html = Hash + ".html", Rtf = Hash + ".rtf" }));
    }

    [Fact]
    public void An_entry_with_nothing_but_rich_text_is_rtf()
    {
        Assert.Equal("rtf", ClipLine.Kind(new EntryRecord("a1", Time, null) { Rtf = Hash + ".rtf" }));
    }

    [Fact]
    public void A_delete_record_is_delete_and_a_clear_record_is_clear()
    {
        Assert.Equal("delete", ClipLine.Kind(new DeleteRecord("d1", Time, "a1")));
        Assert.Equal("clear", ClipLine.Kind(new ClearRecord("c1", Time, Time)));
    }

    // What is escaped and what is not.

    [Fact]
    public void A_letter_outside_ascii_is_written_as_itself()
    {
        string text = "caf" + EAcute;

        string line = ClipLine.Write(TextEntry(text));

        Assert.Contains("\"text\":\"" + text + "\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain(BackslashU, line, StringComparison.Ordinal);
    }

    // A character outside the basic plane is written as two escapes of six characters: the
    // JSON writer of .NET never writes one as itself. It comes back whole (the next test).
    [Fact]
    public void An_emoji_is_written_as_two_escapes()
    {
        string line = ClipLine.Write(TextEntry("walrus " + Emoji));

        Assert.Contains("\"text\":\"walrus " + BackslashU + "D83D" + BackslashU + "DE00\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_emoji_comes_back_whole_however_it_is_written()
    {
        string text = "walrus " + Emoji + " otter " + Emoji + Emoji;

        Assert.Equal(text, ReadEntry(ClipLine.Write(TextEntry(text))).Text);
    }

    [Fact]
    public void A_letter_outside_ascii_and_an_emoji_that_stand_in_a_line_as_themselves_are_read()
    {
        string text = "caf" + EAcute + " " + Emoji;

        Assert.Equal(text, ReadEntry(Entry("{\"text\":\"" + text + "\"}")).Text);
    }

    [Fact]
    public void Characters_that_only_matter_on_a_web_page_are_written_as_themselves()
    {
        string line = ClipLine.Write(TextEntry("<a href='otter'>walrus & otter + 1</a>"));

        Assert.Equal(
            """{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"<a href='otter'>walrus & otter + 1</a>","sourceApp":"otter.exe"}""",
            line);
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped()
    {
        string line = ClipLine.Write(TextEntry("say \"walrus\" in C:\\Otters"));

        Assert.Equal(
            """{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"say \"walrus\" in C:\\Otters","sourceApp":"otter.exe"}""",
            line);
    }

    [Fact]
    public void Line_breaks_and_tabs_are_escaped()
    {
        string line = ClipLine.Write(TextEntry("walrus\r\notter\nseal\ttab"));

        Assert.Equal(
            """{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"walrus\r\notter\nseal\ttab","sourceApp":"otter.exe"}""",
            line);
    }

    [Fact]
    public void Text_with_line_breaks_is_written_on_one_line_and_comes_back_whole()
    {
        string text = "walrus\r\notter\nseal\rend\n";

        string line = ClipLine.Write(TextEntry(text));

        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.Equal(text, ReadEntry(line).Text);
    }

    [Fact]
    public void Every_text_of_a_record_is_kept_on_the_one_line()
    {
        var record = new EntryRecord("a\n1", Time, "otter\r\n.exe")
        {
            Text = Link,
            TextLength = Link.Length,
            Files = ["C:\\Otters\\wal\nrus.png"],
            Pictures = [new KeptPicture("wal\nrus.png", Hash + ".png")],
            Label = "Otter\r\npictures",
        };

        string line = ClipLine.Write(record);

        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        AssertSame(record, ClipLine.Parse(line));
    }

    [Fact]
    public void No_character_that_some_reader_takes_for_a_line_break_is_written_as_itself()
    {
        // A vertical tab, a form feed, the three separators below U+0020, NEL, and the line and
        // the paragraph separator. This code breaks lines only at CR and LF, but a file of
        // records may be read by something that breaks them at any of these.
        char[] breaks = [(char)0x0B, (char)0x0C, (char)0x1C, (char)0x1D, (char)0x1E, (char)0x85, (char)0x2028, (char)0x2029];
        string text = "walrus" + string.Concat(breaks.Select(c => c + "otter"));

        string line = ClipLine.Write(TextEntry(text));

        Assert.All(breaks, c => Assert.DoesNotContain(c.ToString(), line, StringComparison.Ordinal));
        Assert.Equal(text, ReadEntry(line).Text);
    }

    [Fact]
    public void A_record_is_one_object_with_nothing_before_or_after_it()
    {
        string line = ClipLine.Write(new DeleteRecord("d1", Time, "a1"));

        Assert.StartsWith("{", line, StringComparison.Ordinal);
        Assert.EndsWith("}", line, StringComparison.Ordinal);
    }

    // Times.

    [Fact]
    public void A_time_is_written_in_utc_whatever_offset_it_has()
    {
        var time = new DateTimeOffset(2026, 10, 3, 16, 2, 11, TimeSpan.FromHours(2));

        Assert.Equal("2026-10-03T14:02:11Z", ClipLine.FormatTime(time));
    }

    [Fact]
    public void A_time_whose_date_is_another_one_in_utc_is_written_with_the_utc_date()
    {
        var time = new DateTimeOffset(2026, 10, 4, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal("2026-10-03T22:30:00Z", ClipLine.FormatTime(time));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(999)]
    public void A_fraction_of_a_second_is_dropped_and_never_rounded_up(int milliseconds)
    {
        var time = new DateTimeOffset(2026, 10, 3, 14, 2, 11, milliseconds, TimeSpan.Zero);

        Assert.Equal("2026-10-03T14:02:11Z", ClipLine.FormatTime(time));
    }

    [Fact]
    public void A_time_is_written_the_same_whatever_calendar_the_culture_has()
    {
        var thai = new CultureInfo("th-TH");

        // Proves that this culture really counts the years another way, so the check below cannot pass for nothing.
        Assert.NotEqual("2026", Time.ToString("yyyy", thai));

        Assert.Equal("2026-10-03T14:02:11Z", InCulture(thai, () => ClipLine.FormatTime(Time)));
    }

    [Fact]
    public void The_time_of_a_record_and_the_time_a_clear_record_gives_are_both_written_in_utc_in_whole_seconds()
    {
        // 16:02:11 and three quarters, two hours ahead of UTC; and nine in the morning and a half
        // second, five hours behind it.
        var written = new DateTimeOffset(2026, 10, 3, 16, 2, 11, 750, TimeSpan.FromHours(2));
        var before = new DateTimeOffset(2026, 10, 3, 9, 0, 0, 500, TimeSpan.FromHours(-5));

        Assert.Equal(
            """{"id":"c1","ts":"2026-10-03T14:02:11Z","kind":"clear","before":"2026-10-03T14:00:00Z"}""",
            ClipLine.Write(new ClearRecord("c1", written, before)));
    }

    [Fact]
    public void A_time_is_read_the_same_whatever_calendar_the_culture_has()
    {
        var thai = new CultureInfo("th-TH");

        var record = InCulture(thai, () => ClipLine.Parse("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":"a1"}"""));

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 5, 2, TimeSpan.Zero), Assert.IsType<DeleteRecord>(record).Time);
    }

    // Half of a surrogate pair in what is written.

    [Fact]
    public void Text_that_holds_half_a_surrogate_pair_is_written_with_a_replacement_character_in_its_place()
    {
        string line = ClipLine.Write(TextEntry("wal" + HighHalf + "rus"));

        Assert.Equal("wal" + Replacement + "rus", ReadEntry(line).Text);
    }

    [Fact]
    public void A_second_half_with_no_first_half_before_it_is_replaced_too()
    {
        string line = ClipLine.Write(TextEntry("wal" + LowHalf + "rus" + LowHalf + HighHalf));

        Assert.Equal("wal" + Replacement + "rus" + Replacement + Replacement, ReadEntry(line).Text);
    }

    [Fact]
    public void A_whole_pair_beside_half_a_pair_is_kept()
    {
        string line = ClipLine.Write(TextEntry(Emoji + HighHalf + Emoji));

        Assert.Equal(Emoji + Replacement + Emoji, ReadEntry(line).Text);
    }

    [Fact]
    public void Every_other_text_of_an_entry_that_holds_half_a_pair_is_written_without_throwing()
    {
        var record = new EntryRecord("a" + HighHalf, Time, "otter" + HighHalf + ".exe")
        {
            Text = Link,
            TextLength = Link.Length,
            Files = [@"C:\Otters\wal" + HighHalf + "rus.png"],
            Pictures = [new KeptPicture("wal" + HighHalf + "rus.png", Hash + ".png")],
            Label = "Otter" + HighHalf,
        };

        var read = ReadEntry(ClipLine.Write(record));

        Assert.Equal("a" + Replacement, read.Id);
        Assert.Equal("otter" + Replacement + ".exe", read.SourceApp);
        Assert.Equal([@"C:\Otters\wal" + Replacement + "rus.png"], read.Files);
        Assert.Equal([new KeptPicture("wal" + Replacement + "rus.png", Hash + ".png")], read.Pictures);
        Assert.Equal("Otter" + Replacement, read.Label);
    }

    [Fact]
    public void A_delete_record_whose_target_holds_half_a_pair_is_written_without_throwing()
    {
        var read = Assert.IsType<DeleteRecord>(ClipLine.Parse(ClipLine.Write(new DeleteRecord("d1", Time, "a" + HighHalf))));

        Assert.Equal("a" + Replacement, read.Target);
    }

    // Lines that are not read.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("walrus: otter")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"walrus\"")]
    [InlineData("{}")]
    [InlineData("{ }")]
    [InlineData("[]")]
    [InlineData("""[{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}]""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}{"id":"a2","ts":"2026-10-03T14:02:12Z","kind":"text","text":"again"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"},""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}x""")]
    [InlineData("""{'id':'a1','ts':'2026-10-03T14:02:11Z','kind':'text','text':'hello'}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello",}""")]
    public void A_line_that_is_not_one_json_object_with_the_expected_keys_is_not_read(string line)
    {
        Assert.Null(ClipLine.Parse(line));
    }

    [Fact]
    public void A_line_cut_off_anywhere_is_not_read()
    {
        string line = ClipLine.Write(Everything());

        for (int length = 0; length < line.Length; length++)
        {
            if (ClipLine.Parse(line[..length]) is not null)
            {
                Assert.Fail($"The line was read when cut to its first {length} characters.");
            }
        }
    }

    [Fact]
    public void A_line_nested_deeper_than_json_is_read_here_is_not_read_and_nothing_is_thrown()
    {
        string line = Entry("{\"text\":\"hello\",\"walrus\":" + new string('[', 200) + new string(']', 200) + "}");

        Assert.Null(ClipLine.Parse(line));
    }

    [Theory]
    [InlineData("""{"ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    [InlineData("""{"id":"","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    [InlineData("""{"id":null,"ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    [InlineData("""{"id":7,"ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    [InlineData("""{"id":["a1"],"ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    [InlineData("""{"ID":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    public void A_line_without_an_id_that_is_text_is_not_read(string line)
    {
        Assert.Null(ClipLine.Parse(line));
    }

    [Fact]
    public void An_id_may_be_100_characters_long()
    {
        string id = new('a', 100);

        var record = ClipLine.Parse(ClipLine.Write(new DeleteRecord(id, Time, "a1")));

        Assert.Equal(id, Assert.IsType<DeleteRecord>(record).Id);
    }

    [Fact]
    public void A_line_whose_id_is_101_characters_long_is_not_read()
    {
        Assert.Null(ClipLine.Parse(ClipLine.Write(new DeleteRecord(new string('a', 101), Time, "a1"))));
        Assert.Null(ClipLine.Parse(ClipLine.Write(new EntryRecord(new string('a', 101), Time, null) { Text = "hello", TextLength = 5 })));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("")]
    [InlineData("2026-10-03")]
    [InlineData("14:02:11")]
    [InlineData("2026-10-03 14:02:11")]
    [InlineData("2026-10-03 14:02:11Z")]
    [InlineData("2026-13-01T14:02:11Z")]
    [InlineData("2026-02-30T14:02:11Z")]
    [InlineData("2026-10-03T25:02:11Z")]
    [InlineData("2026-10-03T14:02Z")]
    [InlineData("03/10/2026 14:02:11")]
    [InlineData(" 2026-10-03T14:02:11Z")]
    [InlineData("2026-10-03T14:02:11Z ")]
    [InlineData("2026-10-03T14:02:11Z and more")]
    public void A_line_whose_time_is_not_written_as_a_date_and_a_time_of_day_is_not_read(string written)
    {
        Assert.Null(ClipLine.Parse($$"""{"id":"a1","ts":"{{written}}","kind":"text","text":"hello"}"""));
    }

    [Theory]
    [InlineData("1759500131")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("""["2026-10-03T14:02:11Z"]""")]
    [InlineData("""{"at":"2026-10-03T14:02:11Z"}""")]
    public void A_line_whose_time_is_not_text_is_not_read(string value)
    {
        Assert.Null(ClipLine.Parse($$"""{"id":"a1","ts":{{value}},"kind":"text","text":"hello"}"""));
    }

    [Fact]
    public void A_line_without_a_time_is_not_read()
    {
        Assert.Null(ClipLine.Parse("""{"id":"a1","kind":"text","text":"hello"}"""));
    }

    // Each row has everything any kind of record needs, so only the word in "kind" is at fault.
    [Theory]
    [InlineData("\"video\"")]
    [InlineData("\"\"")]
    [InlineData("\"Text\"")]
    [InlineData("\"DELETE\"")]
    [InlineData("\"text \"")]
    [InlineData("\"entry\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("[\"text\"]")]
    public void A_line_of_a_kind_this_code_does_not_know_is_not_read(string value)
    {
        Assert.Null(ClipLine.Parse($$"""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":{{value}},"text":"hello","target":"a0","before":"2026-10-03T14:00:00Z"}"""));
    }

    [Fact]
    public void A_line_without_a_kind_is_not_read()
    {
        Assert.Null(ClipLine.Parse("""{"id":"a1","ts":"2026-10-03T14:02:11Z","text":"hello","target":"a0","before":"2026-10-03T14:00:00Z"}"""));
    }

    [Theory]
    [InlineData("""{"text":7}""")]
    [InlineData("""{"text":true}""")]
    [InlineData("""{"text":["hello"]}""")]
    [InlineData("""{"text":{"value":"hello"}}""")]
    [InlineData("""{"text":7,"image":"<hash>.png"}""")]
    public void An_entry_whose_text_is_not_text_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","sourceApp":7}""")]
    [InlineData("""{"text":"hello","sourceApp":["chrome.exe"]}""")]
    [InlineData("""{"text":"hello","sourceApp":false}""")]
    [InlineData("""{"text":"hello","label":7}""")]
    [InlineData("""{"text":"hello","label":{"words":"Otters"}}""")]
    public void An_entry_whose_source_program_or_label_is_not_text_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","textLength":"5"}""")]
    [InlineData("""{"text":"hello","textLength":"many"}""")]
    [InlineData("""{"text":"hello","textLength":true}""")]
    [InlineData("""{"text":"hello","textLength":[5]}""")]
    public void An_entry_whose_text_length_is_not_a_number_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","files":"C:\\Otters\\walrus.png"}""")]
    [InlineData("""{"text":"hello","files":7}""")]
    [InlineData("""{"text":"hello","files":{"first":"C:\\Otters\\walrus.png"}}""")]
    [InlineData("""{"text":"hello","files":["C:\\Otters\\walrus.png",7]}""")]
    [InlineData("""{"text":"hello","files":[7]}""")]
    [InlineData("""{"text":"hello","files":[["C:\\Otters\\walrus.png"]]}""")]
    [InlineData("""{"text":"hello","files":[{"path":"C:\\Otters\\walrus.png"}]}""")]
    public void An_entry_whose_files_are_not_a_list_of_texts_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","pictures":"walrus.png"}""")]
    [InlineData("""{"text":"hello","pictures":7}""")]
    [InlineData("""{"text":"hello","pictures":{"name":"walrus.png","file":"<hash>.png"}}""")]
    [InlineData("""{"text":"hello","pictures":["walrus.png"]}""")]
    [InlineData("""{"text":"hello","pictures":[["walrus.png","<hash>.png"]]}""")]
    [InlineData("""{"text":"hello","pictures":[{}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"file":"<hash>.png"}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":"walrus.png"}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":"","file":"<hash>.png"}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":null,"file":"<hash>.png"}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":7,"file":"<hash>.png"}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":"walrus.png","file":null}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":"walrus.png","file":7}]}""")]
    [InlineData("""{"text":"hello","pictures":[{"name":"walrus.png","file":"<hash>.png"},{"name":"otter.png"}]}""")]
    public void An_entry_whose_pictures_are_not_a_list_with_a_name_and_a_file_each_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"sourceApp":"otter.exe"}""")]
    [InlineData("""{"label":"Otter pictures","sourceApp":"otter.exe"}""")]
    [InlineData("""{"text":null}""")]
    [InlineData("""{"textLength":5}""")]
    [InlineData("""{"files":[]}""")]
    [InlineData("""{"files":["",""]}""")]
    [InlineData("""{"pictures":[]}""")]
    [InlineData("""{"text":null,"textFile":null,"html":null,"rtf":null,"image":null,"files":null,"pictures":null}""")]
    public void An_entry_with_no_form_at_all_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image")]
    [InlineData("files")]
    [InlineData("html")]
    [InlineData("rtf")]
    public void A_line_with_the_kind_of_an_entry_and_nothing_else_is_not_read(string kind)
    {
        Assert.Null(ClipLine.Parse($$"""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"{{kind}}"}"""));
    }

    [Theory]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete"}""")]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":""}""")]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":null}""")]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":7}""")]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":["a1"]}""")]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","text":"a1"}""")]
    public void A_delete_record_without_a_target_is_not_read(string line)
    {
        Assert.Null(ClipLine.Parse(line));
    }

    [Fact]
    public void A_target_may_be_100_characters_long()
    {
        string target = new('a', 100);

        var record = ClipLine.Parse(ClipLine.Write(new DeleteRecord("d1", Time, target)));

        Assert.Equal(target, Assert.IsType<DeleteRecord>(record).Target);
    }

    [Fact]
    public void A_delete_record_whose_target_is_101_characters_long_is_not_read()
    {
        Assert.Null(ClipLine.Parse(ClipLine.Write(new DeleteRecord("d1", Time, new string('a', 101)))));
    }

    [Theory]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear"}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":null}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":""}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"soon"}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"2026-10-03"}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"2026-10-03 14:09:30"}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":1759500570}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","target":"2026-10-03T14:09:30Z"}""")]
    public void A_clear_record_without_a_time_to_clear_before_is_not_read(string line)
    {
        Assert.Null(ClipLine.Parse(line));
    }

    // Half of a surrogate pair written as an escape is valid JSON that cannot be read as text.
    // "<high>" and "<low>" stand for the six characters of such an escape.
    [Theory]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"wal<high>rus"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"wal<low>rus"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"wal<low><high>rus"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"walrus<high>"}""")]
    [InlineData("""{"id":"a<high>","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")]
    [InlineData("""{"id":"a1","ts":"<high>","kind":"text","text":"hello"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"<high>","text":"hello"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","sourceApp":"otter<high>.exe"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","label":"Otter<high>"}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"files","files":["C:\\Otters\\wal<high>rus.png"]}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"files","files":["C:\\Otters\\walrus.png"],"pictures":[{"name":"wal<high>rus.png","file":"<hash>.png"}]}""")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","image":"<high>"}""")]
    [InlineData("""{"id":"a3","ts":"2026-10-03T14:05:02Z","kind":"delete","target":"a<high>"}""")]
    [InlineData("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"<high>"}""")]
    public void A_line_with_half_a_surrogate_pair_written_as_an_escape_is_not_read_and_nothing_is_thrown(string template)
    {
        Assert.Null(ClipLine.Parse(Fill(template)));
    }

    [Fact]
    public void A_key_whose_name_is_half_a_surrogate_pair_written_as_an_escape_does_not_make_reading_throw()
    {
        string line = Fill("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","<high>":1}""");

        Assert.Null(Record.Exception(() => ClipLine.Parse(line)));
    }

    [Fact]
    public void A_whole_pair_written_as_two_escapes_is_read()
    {
        string line = Entry("{\"text\":\"walrus " + BackslashU + "d83d" + BackslashU + "de00\"}");

        Assert.Equal("walrus " + Emoji, ReadEntry(line).Text);
    }

    // A line read from a file never holds half a pair as a character (ClipReaderTests shows
    // that), so only a direct call can meet this. It must still not throw.
    [Fact]
    public void A_line_that_holds_half_a_surrogate_pair_as_a_character_is_not_read_and_nothing_is_thrown()
    {
        string line = Entry("{\"text\":\"wal" + HighHalf + "rus\"}");

        Assert.Null(ClipLine.Parse(line));
    }

    // A file name in a line is joined to a folder to make a path, so nothing but a hash and
    // the right extension is ever accepted. Each row also holds good text: the whole line is
    // refused, not only the name.

    [Theory]
    [InlineData("""{"text":"hello","image":"..\\..\\x.png"}""")]
    [InlineData("""{"text":"hello","image":"<hash>.txt"}""")]
    [InlineData("""{"text":"hello","image":"<hash>.jpg"}""")]
    [InlineData("""{"text":"hello","image":"<HASH>.png"}""")]
    [InlineData("""{"text":"hello","image":"<hash>.PNG"}""")]
    [InlineData("""{"text":"hello","image":"<hash>"}""")]
    [InlineData("""{"text":"hello","image":""}""")]
    [InlineData("""{"text":"hello","image":"walrus.png"}""")]
    [InlineData("""{"text":"hello","image":"x<hash>.png"}""")]
    [InlineData("""{"text":"hello","image":"sub/<hash>.png"}""")]
    [InlineData("""{"text":"hello","image":"..\\<hash>.png"}""")]
    [InlineData("""{"text":"hello","image":"../<hash>.png"}""")]
    [InlineData("""{"text":"hello","image":"C:\\Otters\\<hash>.png"}""")]
    [InlineData("""{"text":"hello","image":"\\\\walrus\\share\\<hash>.png"}""")]
    [InlineData("""{"text":"hello","image":"<hash>.png."}""")]
    [InlineData("""{"text":"hello","image":"<hash>.png "}""")]
    [InlineData("""{"text":"hello","image":"<hash>.png:walrus"}""")]
    [InlineData("""{"text":"hello","image":7}""")]
    [InlineData("""{"text":"hello","image":["<hash>.png"]}""")]
    public void An_entry_whose_picture_is_not_named_by_a_hash_and_png_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","html":"<hash>.png"}""")]
    [InlineData("""{"text":"hello","html":"<hash>.htm"}""")]
    [InlineData("""{"text":"hello","html":"<hash>.txt"}""")]
    [InlineData("""{"text":"hello","html":"<HASH>.html"}""")]
    [InlineData("""{"text":"hello","html":"..\\x.html"}""")]
    [InlineData("""{"text":"hello","html":"C:\\Otters\\<hash>.html"}""")]
    [InlineData("""{"text":"hello","html":"<p>walrus</p>"}""")]
    [InlineData("""{"text":"hello","html":7}""")]
    public void An_entry_whose_html_is_not_named_by_a_hash_and_html_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","rtf":"<hash>.txt"}""")]
    [InlineData("""{"text":"hello","rtf":"<hash>.html"}""")]
    [InlineData("""{"text":"hello","rtf":"<HASH>.rtf"}""")]
    [InlineData("""{"text":"hello","rtf":"..\\x.rtf"}""")]
    [InlineData("""{"text":"hello","rtf":"C:\\Otters\\<hash>.rtf"}""")]
    [InlineData("""{"text":"hello","rtf":7}""")]
    public void An_entry_whose_rich_text_is_not_named_by_a_hash_and_rtf_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""{"text":"hello","textFile":"C:\\x.txt"}""")]
    [InlineData("""{"text":"hello","textFile":"<hash>.html"}""")]
    [InlineData("""{"text":"hello","textFile":"<hash>.png"}""")]
    [InlineData("""{"text":"hello","textFile":"<HASH>.txt"}""")]
    [InlineData("""{"text":"hello","textFile":"<hash>.TXT"}""")]
    [InlineData("""{"text":"hello","textFile":"..\\<hash>.txt"}""")]
    [InlineData("""{"text":"hello","textFile":"notes.txt"}""")]
    [InlineData("""{"text":"hello","textFile":""}""")]
    [InlineData("""{"text":"hello","textFile":7}""")]
    public void An_entry_whose_text_file_is_not_named_by_a_hash_and_txt_is_not_read(string forms)
    {
        Assert.Null(ClipLine.Parse(Entry(forms)));
    }

    [Theory]
    [InlineData("""..\\..\\walrus.png""")]
    [InlineData("""walrus.png""")]
    [InlineData("""<hash>.txt""")]
    [InlineData("""<hash>.html""")]
    [InlineData("""<hash>.rtf""")]
    [InlineData("""<hash>.webp""")]
    [InlineData("""<hash>""")]
    [InlineData("""<HASH>.png""")]
    [InlineData("""<hash>.PNG""")]
    [InlineData("""C:\\Otters\\<hash>.png""")]
    [InlineData("""..\\<hash>.png""")]
    [InlineData("""sub/<hash>.png""")]
    [InlineData("""<hash>.png.""")]
    [InlineData("")]
    public void An_entry_with_a_kept_picture_whose_file_is_not_a_hash_and_a_picture_extension_is_not_read(string file)
    {
        string forms = """{"text":"hello","files":["C:\\Otters\\walrus.png"],"pictures":[{"name":"walrus.png","file":"<file>"}]}""";

        Assert.Null(ClipLine.Parse(Entry(forms.Replace("<file>", file))));
    }

    [Fact]
    public void An_entry_whose_files_are_each_named_by_a_hash_and_the_right_extension_is_read()
    {
        var entry = ReadEntry(Entry("""{"text":"hello","textFile":"<hash>.txt","html":"<hash>.html","rtf":"<hash>.rtf","image":"<hash>.png"}"""));

        Assert.Equal(Hash + ".txt", entry.TextFile);
        Assert.Equal(Hash + ".html", entry.Html);
        Assert.Equal(Hash + ".rtf", entry.Rtf);
        Assert.Equal(Hash + ".png", entry.Image);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_kept_picture_may_have_any_of_the_picture_extensions(string extension)
    {
        string forms = """{"files":["C:\\Otters\\walrus<ext>"],"pictures":[{"name":"walrus<ext>","file":"<hash><ext>"}]}""";

        var entry = ReadEntry(Entry(forms.Replace("<ext>", extension)));

        Assert.Equal([new KeptPicture("walrus" + extension, Hash + extension)], entry.Pictures);
    }

    // What is accepted.

    [Fact]
    public void A_time_with_a_fraction_of_a_second_is_read_and_the_fraction_is_kept()
    {
        var record = ClipLine.Parse("""{"id":"a3","ts":"2026-10-03T14:02:11.250Z","kind":"delete","target":"a1"}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, 250, TimeSpan.Zero), Assert.IsType<DeleteRecord>(record).Time);
    }

    [Theory]
    [InlineData("2026-10-03T14:02:11.5Z", 500)]
    [InlineData("2026-10-03T14:02:11.25Z", 250)]
    [InlineData("2026-10-03T14:02:11.007Z", 7)]
    [InlineData("2026-10-03T14:02:11.2500000Z", 250)]
    public void A_fraction_may_have_from_one_to_seven_digits(string written, int milliseconds)
    {
        var record = ClipLine.Parse($$"""{"id":"a3","ts":"{{written}}","kind":"delete","target":"a1"}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, milliseconds, TimeSpan.Zero), Assert.IsType<DeleteRecord>(record).Time);
    }

    [Theory]
    [InlineData("2026-10-03T16:02:11+02:00")]
    [InlineData("2026-10-03T08:32:11-05:30")]
    [InlineData("2026-10-04T00:02:11+10:00")]
    [InlineData("2026-10-03T14:02:11+00:00")]
    public void A_time_with_an_offset_is_read_as_the_same_moment_in_utc(string written)
    {
        var record = ClipLine.Parse($$"""{"id":"a3","ts":"{{written}}","kind":"delete","target":"a1"}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, TimeSpan.Zero), Assert.IsType<DeleteRecord>(record).Time);
    }

    [Fact]
    public void A_time_with_both_a_fraction_and_an_offset_is_read()
    {
        var record = ClipLine.Parse("""{"id":"a3","ts":"2026-10-03T16:02:11.250+02:00","kind":"delete","target":"a1"}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, 250, TimeSpan.Zero), Assert.IsType<DeleteRecord>(record).Time);
    }

    [Fact]
    public void The_time_a_clear_record_gives_may_have_a_fraction_and_an_offset_too()
    {
        var record = ClipLine.Parse("""{"id":"a4","ts":"2026-10-03T14:09:30Z","kind":"clear","before":"2026-10-03T16:02:11.250+02:00"}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 3, 14, 2, 11, 250, TimeSpan.Zero), Assert.IsType<ClearRecord>(record).Before);
    }

    [Fact]
    public void Keys_this_code_does_not_know_are_passed_over()
    {
        var entry = ReadEntry("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello","sourceApp":"chrome.exe","walrus":{"deep":[1,2,{"otter":null}]},"pinned":true,"copies":3}""");

        AssertSame(new EntryRecord("a1", Time, "chrome.exe") { Text = "hello", TextLength = 5 }, entry);
    }

    [Fact]
    public void Keys_this_code_does_not_know_are_passed_over_in_a_delete_and_a_clear_record()
    {
        Assert.Equal(
            new DeleteRecord("a3", Time, "a1"),
            ClipLine.Parse("""{"id":"a3","ts":"2026-10-03T14:02:11Z","kind":"delete","target":"a1","reason":"walrus"}"""));
        Assert.Equal(
            new ClearRecord("a4", Time, Time),
            ClipLine.Parse("""{"id":"a4","ts":"2026-10-03T14:02:11Z","kind":"clear","before":"2026-10-03T14:02:11Z","reason":"walrus"}"""));
    }

    [Fact]
    public void Keys_may_come_in_any_order()
    {
        var entry = ReadEntry($$"""{"sourceApp":"explorer.exe","label":"Otter pictures","pictures":[{"file":"{{OtherHash}}.png","name":"walrus.png"}],"files":["C:\\Otters\\walrus.png","C:\\Otters\\notes.txt"],"image":"{{Hash}}.png","rtf":"{{Hash}}.rtf","html":"{{Hash}}.html","textLength":250000,"textFile":"{{Hash}}.txt","text":"https://example.org/otters","kind":"text","ts":"2026-10-03T14:02:11Z","id":"e1"}""");

        AssertSame(Everything(), entry);
    }

    [Fact]
    public void Keys_may_come_in_any_order_in_a_delete_and_a_clear_record()
    {
        Assert.Equal(
            new DeleteRecord("a3", Time, "a1"),
            ClipLine.Parse("""{"target":"a1","kind":"delete","ts":"2026-10-03T14:02:11Z","id":"a3"}"""));
        Assert.Equal(
            new ClearRecord("a4", Time, Time),
            ClipLine.Parse("""{"before":"2026-10-03T14:02:11Z","kind":"clear","ts":"2026-10-03T14:02:11Z","id":"a4"}"""));
    }

    [Fact]
    public void A_line_with_spaces_between_its_parts_is_read()
    {
        var entry = ReadEntry("""  { "id" : "a1" , "ts" : "2026-10-03T14:02:11Z" , "kind" : "text" , "text" : "hello" }  """);

        Assert.Equal("hello", entry.Text);
    }

    [Fact]
    public void An_optional_key_written_as_null_means_the_same_as_no_key()
    {
        var entry = ReadEntry(Entry("""{"text":"hello","textFile":null,"textLength":null,"html":null,"rtf":null,"image":null,"files":null,"pictures":null,"label":null,"sourceApp":null}"""));

        AssertSame(new EntryRecord("a1", Time, null) { Text = "hello", TextLength = 5 }, entry);
    }

    [Fact]
    public void Text_written_as_null_beside_another_form_means_no_text()
    {
        var entry = ReadEntry(Entry("""{"text":null,"image":"<hash>.png"}"""));

        Assert.Null(entry.Text);
        Assert.Equal(Hash + ".png", entry.Image);
    }

    [Fact]
    public void An_entry_with_only_the_file_of_its_text_has_empty_text_and_the_length_that_is_written()
    {
        var entry = ReadEntry(Entry("""{"textFile":"<hash>.txt","textLength":250000}"""));

        Assert.Equal("", entry.Text);
        Assert.Equal(Hash + ".txt", entry.TextFile);
        Assert.Equal(250_000, entry.TextLength);
        Assert.Equal("text", ClipLine.Kind(entry));
    }

    [Fact]
    public void Without_a_text_length_the_length_is_that_of_the_text_in_the_line()
    {
        Assert.Equal(6, ReadEntry(Entry("""{"text":"walrus"}""")).TextLength);
    }

    [Fact]
    public void A_text_length_larger_than_the_text_in_the_line_is_used()
    {
        var entry = ReadEntry(Entry("""{"text":"walrus","textFile":"<hash>.txt","textLength":250000}"""));

        Assert.Equal(250_000, entry.TextLength);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    [InlineData("6.5")]
    [InlineData("1e400")]
    public void A_text_length_that_cannot_be_right_gives_way_to_the_length_of_the_text_in_the_line(string written)
    {
        string forms = """{"text":"walrus","textFile":"<hash>.txt","textLength":<length>}""";

        var entry = ReadEntry(Entry(forms.Replace("<length>", written)));

        Assert.Equal(6, entry.TextLength);
    }

    [Fact]
    public void Empty_paths_in_the_files_are_dropped_and_the_others_keep_their_order()
    {
        var entry = ReadEntry(Entry("""{"files":["","C:\\Otters\\walrus.png","","C:\\Otters\\notes.txt",""]}"""));

        Assert.Equal([@"C:\Otters\walrus.png", @"C:\Otters\notes.txt"], entry.Files);
    }

    [Fact]
    public void The_line_that_the_refused_lines_above_add_one_key_to_is_read()
    {
        AssertSame(new EntryRecord("a1", Time, null) { Text = "hello", TextLength = 5 }, ClipLine.Parse(Entry("""{"text":"hello"}""")));
    }

    // The files a record names.

    [Fact]
    public void A_record_names_its_text_file_html_rich_text_picture_and_every_kept_picture()
    {
        var record = Everything() with
        {
            Pictures = [new KeptPicture("walrus.png", OtherHash + ".png"), new KeptPicture("otter.jpg", OtherHash + ".jpg")],
        };

        Assert.Equal(
            [Hash + ".txt", Hash + ".html", Hash + ".rtf", Hash + ".png", OtherHash + ".png", OtherHash + ".jpg"],
            record.FileNames());
    }

    [Fact]
    public void A_short_text_entry_names_no_file()
    {
        Assert.Empty(TextEntry("walrus").FileNames());
    }

    [Fact]
    public void A_record_names_only_the_files_it_has()
    {
        Assert.Equal([Hash + ".png"], new EntryRecord("a1", Time, null) { Image = Hash + ".png" }.FileNames());
        Assert.Equal([Hash + ".html"], new EntryRecord("a1", Time, null) { Text = "walrus", Html = Hash + ".html" }.FileNames());
    }

    [Fact]
    public void The_paths_of_copied_files_are_not_files_of_the_record()
    {
        var record = new EntryRecord("a1", Time, null) { Files = [@"C:\Otters\walrus.png", @"C:\Otters\notes.txt"] };

        Assert.Empty(record.FileNames());
    }

    // A key that is there twice. A JSON object may hold a key twice and nothing forbids a line
    // from doing so. Which of the two a line means is for the reader to decide, and these rows
    // pin what it decides: the last one counts. What must hold whichever it is: the value that
    // is checked is the value that is used, so a bad file name cannot be hidden behind a good
    // one, or the other way round.

    [Theory]
    [InlineData("""{"text":"hello","image":"<hash>.png","image":"x<hash>.png"}""", false)]
    [InlineData("""{"text":"hello","image":"x<hash>.png","image":"<hash>.png"}""", true)]
    [InlineData("""{"text":"hello","image":"<hash>.png","image":"..\\<hash>.png"}""", false)]
    [InlineData("""{"text":"hello","image":"..\\<hash>.png","image":"<hash>.png"}""", true)]
    [InlineData("""{"text":"hello","image":"<hash>.png","image":"C:\\Otters\\<hash>.png"}""", false)]
    [InlineData("""{"text":"hello","image":"C:\\Otters\\<hash>.png","image":"<hash>.png"}""", true)]
    [InlineData("""{"text":"hello","image":"<hash>.png","image":"x<hash>.png","image":"<hash>.png"}""", true)]
    [InlineData("""{"text":"hello","image":"x<hash>.png","image":"<hash>.png","image":"x<hash>.png"}""", false)]
    [InlineData("""{"text":"hello","image":"<hash>.png","image":7}""", false)]
    [InlineData("""{"text":"hello","image":7,"image":"<hash>.png"}""", true)]
    [InlineData("""{"text":"hello","image":"x<hash>.png","image":null}""", true)]
    public void A_picture_name_that_is_there_twice_is_the_last_one_and_a_name_that_is_not_a_hash_and_png_is_never_held(string forms, bool read)
    {
        var record = ClipLine.Parse(Entry(forms));

        Assert.Equal(read, record is not null);
        if (record is EntryRecord entry)
        {
            // Whenever the line is read, what the record holds has the shape that is let into a path.
            Assert.True(entry.Image is null || ClipPaths.IsBlobName(entry.Image, ClipPaths.ImageExtension));
            Assert.True(entry.Image is null || entry.Image == Hash + ".png");
            Assert.All(entry.FileNames(), name => Assert.True(ClipPaths.IsBlobName(name)));
        }
    }

    [Fact]
    public void A_picture_name_beside_a_null_one_for_the_same_key_is_the_last_of_the_two()
    {
        Assert.Null(ReadEntry(Entry("""{"text":"hello","image":"<hash>.png","image":null}""")).Image);
        Assert.Equal(Hash + ".png", ReadEntry(Entry("""{"text":"hello","image":null,"image":"<hash>.png"}""")).Image);
    }

    [Theory]
    [InlineData("""{"text":"hello","html":"<hash>.html","html":"x<hash>.html"}""", false)]
    [InlineData("""{"text":"hello","html":"x<hash>.html","html":"<hash>.html"}""", true)]
    [InlineData("""{"text":"hello","rtf":"<hash>.rtf","rtf":"..\\<hash>.rtf"}""", false)]
    [InlineData("""{"text":"hello","rtf":"..\\<hash>.rtf","rtf":"<hash>.rtf"}""", true)]
    [InlineData("""{"text":"hello","textFile":"<hash>.txt","textFile":"x<hash>.txt"}""", false)]
    [InlineData("""{"text":"hello","textFile":"x<hash>.txt","textFile":"<hash>.txt"}""", true)]
    [InlineData("""{"text":"hello","files":["C:\\a.png"],"pictures":[{"name":"a.png","file":"<hash>.png"}],"pictures":[{"name":"a.png","file":"x<hash>.png"}]}""", false)]
    [InlineData("""{"text":"hello","files":["C:\\a.png"],"pictures":[{"name":"a.png","file":"x<hash>.png"}],"pictures":[{"name":"a.png","file":"<hash>.png"}]}""", true)]
    [InlineData("""{"text":"hello","files":["C:\\a.png"],"pictures":[{"name":"a.png","file":"<hash>.png","file":"x<hash>.png"}]}""", false)]
    [InlineData("""{"text":"hello","files":["C:\\a.png"],"pictures":[{"name":"a.png","file":"x<hash>.png","file":"<hash>.png"}]}""", true)]
    public void Any_other_name_of_a_file_that_is_there_twice_is_the_last_one_and_a_bad_name_is_never_held(string forms, bool read)
    {
        var record = ClipLine.Parse(Entry(forms));

        Assert.Equal(read, record is not null);
        if (record is EntryRecord entry)
        {
            Assert.Contains(entry.FileNames(), name => name.StartsWith(Hash, StringComparison.Ordinal));
            Assert.All(entry.FileNames(), name => Assert.True(ClipPaths.IsBlobName(name)));
        }
    }

    [Theory]
    [InlineData("""{"id":"a1","id":"a2","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""", "a2")]
    [InlineData("""{"id":"a2","id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""", "a1")]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","id":"a2","kind":"text","text":"hello"}""", "a2")]
    [InlineData("""{"id":"a1","id":"","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""", null)]
    [InlineData("""{"id":"","id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""", "a1")]
    [InlineData("""{"id":"a1","id":7,"ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""", null)]
    [InlineData("""{"id":"a1","id":null,"ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""", null)]
    public void An_id_that_is_there_twice_is_the_last_one_and_the_line_is_read_only_when_that_one_is_an_id(string line, string? id)
    {
        var record = ClipLine.Parse(line);

        Assert.Equal(id, record?.Id);
    }

    [Fact]
    public void An_id_that_is_there_twice_is_too_long_when_the_last_one_is()
    {
        string tooLong = new('x', ClipLine.MaxIdLength + 1);
        string maximum = new('y', ClipLine.MaxIdLength);

        Assert.Null(ClipLine.Parse($$"""{"id":"a1","id":"{{tooLong}}","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}"""));
        Assert.Equal(maximum, ClipLine.Parse($$"""{"id":"{{tooLong}}","id":"{{maximum}}","ts":"2026-10-03T14:02:11Z","kind":"text","text":"hello"}""")?.Id);
    }

    [Fact]
    public void A_kind_that_is_there_twice_is_the_last_one()
    {
        Assert.Equal(
            new DeleteRecord("a1", Time, "a0"),
            ClipLine.Parse("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","kind":"delete","target":"a0","text":"hello"}"""));
        Assert.Equal(
            "hello",
            Assert.IsType<EntryRecord>(ClipLine.Parse("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"delete","kind":"text","target":"a0","text":"hello"}""")).Text);
        Assert.Equal(
            new ClearRecord("a1", Time, Time),
            ClipLine.Parse("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"delete","kind":"clear","before":"2026-10-03T14:02:11Z"}"""));
    }

    [Theory]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"walrus","kind":"text","text":"hello"}""", true)]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","kind":"walrus","text":"hello"}""", false)]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","kind":7,"text":"hello"}""", false)]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":"text","kind":null,"text":"hello"}""", false)]
    [InlineData("""{"id":"a1","ts":"2026-10-03T14:02:11Z","kind":null,"kind":"text","text":"hello"}""", true)]
    public void A_kind_that_is_there_twice_is_read_only_when_the_last_one_is_one_this_code_knows(string line, bool read)
    {
        Assert.Equal(read, ClipLine.Parse(line) is not null);
    }
}
