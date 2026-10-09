using System.Globalization;
using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

public class ClipPathsTests
{
    // Every lower-case hex digit, four times over: the 64 characters a SHA-256 is written in.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

    [Fact]
    public void A_time_belongs_to_the_day_it_is_in_utc_and_not_to_the_day_on_its_own_clock()
    {
        // Half past midnight on the 4th, two hours ahead of UTC, is half past ten in the evening of the 3rd.
        var time = new DateTimeOffset(2026, 10, 4, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal(new DateOnly(2026, 10, 3), ClipPaths.DayOf(time));
    }

    [Fact]
    public void A_time_late_in_the_evening_behind_utc_belongs_to_the_next_day()
    {
        var time = new DateTimeOffset(2026, 10, 3, 23, 30, 0, TimeSpan.FromHours(-2));

        Assert.Equal(new DateOnly(2026, 10, 4), ClipPaths.DayOf(time));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(12, 0, 0)]
    [InlineData(23, 59, 59)]
    public void Every_time_of_a_day_in_utc_belongs_to_that_day(int hour, int minute, int second)
    {
        var time = new DateTimeOffset(2026, 10, 3, hour, minute, second, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 3), ClipPaths.DayOf(time));
    }

    [Theory]
    [InlineData(2026, 10, 3, "2026-10-03")]
    [InlineData(2026, 1, 5, "2026-01-05")]
    [InlineData(2025, 12, 31, "2025-12-31")]
    public void A_days_folder_is_named_by_year_month_and_day_with_leading_zeros(int year, int month, int dayOfMonth, string expected)
    {
        Assert.Equal(expected, ClipPaths.DayName(new DateOnly(year, month, dayOfMonth)));
    }

    [Fact]
    public void A_days_file_is_named_as_its_folder_with_jsonl_after_it()
    {
        Assert.Equal("2026-10-03.jsonl", ClipPaths.DayFileName(new DateOnly(2026, 10, 3)));
    }

    [Fact]
    public void A_days_names_are_the_same_whatever_calendar_the_culture_has()
    {
        var thai = new CultureInfo("th-TH");
        var day = new DateOnly(2026, 10, 3);

        // Proves that this culture really counts the years another way, so the checks below cannot pass for nothing.
        Assert.NotEqual("2026", day.ToString("yyyy", thai));

        Assert.Equal("2026-10-03", InCulture(thai, () => ClipPaths.DayName(day)));
        Assert.Equal("2026-10-03.jsonl", InCulture(thai, () => ClipPaths.DayFileName(day)));
    }

    [Theory]
    [InlineData("2026-10-03.jsonl", 2026, 10, 3)]
    [InlineData("2025-12-31.jsonl", 2025, 12, 31)]
    [InlineData("2028-02-29.jsonl", 2028, 2, 29)]
    public void The_name_of_a_days_file_gives_its_day(string fileName, int year, int month, int dayOfMonth)
    {
        Assert.True(ClipPaths.TryParseDayFile(fileName, out var day));
        Assert.Equal(new DateOnly(year, month, dayOfMonth), day);
    }

    [Theory]
    [InlineData("2026-10-03.json")]
    [InlineData("2026-10-03.jsonl.tmp")]
    [InlineData("2026-10-3.jsonl")]
    [InlineData("2026-13-01.jsonl")]
    [InlineData("2026-02-30.jsonl")]
    [InlineData("notes.jsonl")]
    [InlineData("2026-10-03.JSONL")]
    [InlineData("2026-10-03.conflict-PC-1.jsonl")]
    [InlineData("2026-10-03 (1).jsonl")]
    [InlineData("2026-10-03")]
    [InlineData(".jsonl")]
    [InlineData("")]
    [InlineData(" 2026-10-03.jsonl")]
    [InlineData("2026-10-03 .jsonl")]
    [InlineData("2026-10-03.jsonl ")]
    [InlineData("~2026-10-03.jsonl")]
    [InlineData("26-10-03.jsonl")]
    [InlineData("20261003.jsonl")]
    [InlineData("2026_10_03.jsonl")]
    [InlineData("2026-10-03T00.jsonl")]
    [InlineData(@"OTTER-PC\2026-10-03.jsonl")]
    public void Any_other_name_is_not_a_days_file(string fileName)
    {
        Assert.False(ClipPaths.TryParseDayFile(fileName, out _));
    }

    [Theory]
    [InlineData("2026-10-03", 2026, 10, 3)]
    [InlineData("2025-12-31", 2025, 12, 31)]
    [InlineData("2028-02-29", 2028, 2, 29)]
    public void The_name_of_a_days_folder_gives_its_day(string name, int year, int month, int dayOfMonth)
    {
        Assert.True(ClipPaths.TryParseDayFolder(name, out var day));
        Assert.Equal(new DateOnly(year, month, dayOfMonth), day);
    }

    [Theory]
    [InlineData("2026-10-3")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("2026-10-03.jsonl")]
    [InlineData("2026-10-03.conflict-PC-1")]
    [InlineData("2026-10-03 (1)")]
    [InlineData("notes")]
    [InlineData("")]
    [InlineData(" 2026-10-03")]
    [InlineData("2026-10-03 ")]
    [InlineData("26-10-03")]
    [InlineData("20261003")]
    [InlineData("2026_10_03")]
    [InlineData(@"OTTER-PC\2026-10-03")]
    public void Any_other_name_is_not_a_days_folder(string name)
    {
        Assert.False(ClipPaths.TryParseDayFolder(name, out _));
    }

    [Fact]
    public void A_days_name_is_read_the_same_whatever_calendar_the_culture_has()
    {
        var thai = new CultureInfo("th-TH");

        var folderDay = InCulture(thai, () => ClipPaths.TryParseDayFolder("2026-10-03", out var day) ? day : (DateOnly?)null);
        var fileDay = InCulture(thai, () => ClipPaths.TryParseDayFile("2026-10-03.jsonl", out var day) ? day : (DateOnly?)null);

        Assert.Equal(new DateOnly(2026, 10, 3), folderDay);
        Assert.Equal(new DateOnly(2026, 10, 3), fileDay);
    }

    [Theory]
    [InlineData(2026, 10, 3)]
    [InlineData(2026, 1, 1)]
    [InlineData(2027, 12, 31)]
    public void The_names_made_for_a_day_are_read_back_as_that_day(int year, int month, int dayOfMonth)
    {
        var day = new DateOnly(year, month, dayOfMonth);

        Assert.True(ClipPaths.TryParseDayFolder(ClipPaths.DayName(day), out var fromFolder));
        Assert.True(ClipPaths.TryParseDayFile(ClipPaths.DayFileName(day), out var fromFile));
        Assert.Equal(day, fromFolder);
        Assert.Equal(day, fromFile);
    }

    [Fact]
    public void The_extensions_of_the_content_files_are_fixed()
    {
        Assert.Equal(".txt", ClipPaths.TextExtension);
        Assert.Equal(".html", ClipPaths.HtmlExtension);
        Assert.Equal(".rtf", ClipPaths.RtfExtension);
        Assert.Equal(".png", ClipPaths.ImageExtension);
    }

    [Fact]
    public void Five_kinds_of_file_count_as_a_picture()
    {
        Assert.Equal([".png", ".jpg", ".jpeg", ".gif", ".bmp"], ClipPaths.PictureExtensions);
    }

    [Fact]
    public void A_content_file_is_named_by_the_sha_256_of_its_bytes_in_lower_case_hex_and_its_extension()
    {
        // The three bytes of "abc", whose SHA-256 is printed in the standard itself.
        byte[] content = [0x61, 0x62, 0x63];

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad.txt",
            ClipPaths.BlobName(content, ".txt"));
    }

    [Fact]
    public void Content_of_no_bytes_has_a_name_too()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855.png",
            ClipPaths.BlobName([], ".png"));
    }

    [Fact]
    public void The_same_bytes_always_get_the_same_name_and_other_bytes_another()
    {
        byte[] walrus = [1, 2, 3, 4];
        byte[] sameWalrus = [1, 2, 3, 4];
        byte[] otter = [1, 2, 3, 5];

        Assert.Equal(ClipPaths.BlobName(walrus, ".png"), ClipPaths.BlobName(sameWalrus, ".png"));
        Assert.NotEqual(ClipPaths.BlobName(walrus, ".png"), ClipPaths.BlobName(otter, ".png"));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".rtf")]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_name_made_for_content_is_accepted_as_one(string extension)
    {
        byte[] content = [0x77, 0x61, 0x6C, 0x72, 0x75, 0x73];

        string name = ClipPaths.BlobName(content, extension);

        Assert.Equal(64 + extension.Length, name.Length);
        Assert.True(ClipPaths.IsBlobName(name, extension));
        Assert.True(ClipPaths.IsBlobName(name));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".rtf")]
    [InlineData(".png")]
    [InlineData(".jpeg")]
    public void Sixty_four_lower_case_hex_digits_and_the_extension_are_a_content_file_name(string extension)
    {
        Assert.True(ClipPaths.IsBlobName(Hash + extension, extension));
    }

    [Fact]
    public void No_name_at_all_is_not_a_content_file_name()
    {
        Assert.False(ClipPaths.IsBlobName(null, ".txt"));
        Assert.False(ClipPaths.IsBlobName(null));
        Assert.False(ClipPaths.IsPictureName(null));
    }

    [Fact]
    public void Upper_case_hex_is_refused()
    {
        Assert.False(ClipPaths.IsBlobName(Hash.ToUpperInvariant() + ".txt", ".txt"));

        // One upper-case digit is enough.
        Assert.False(ClipPaths.IsBlobName(Hash[..10] + "A" + Hash[11..] + ".txt", ".txt"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(128)]
    public void A_hash_that_is_not_64_digits_long_is_refused(int digits)
    {
        // The same digit 64 times is accepted, so the rows are refused for their length alone.
        Assert.True(ClipPaths.IsBlobName(new string('a', 64) + ".txt", ".txt"));

        Assert.False(ClipPaths.IsBlobName(new string('a', digits) + ".txt", ".txt"));
    }

    [Theory]
    [InlineData(Hash + ".png", ".txt")]
    [InlineData(Hash + ".txt", ".png")]
    [InlineData(Hash + ".txt", ".html")]
    [InlineData(Hash + ".htm", ".html")]
    [InlineData(Hash + ".jpg", ".jpeg")]
    [InlineData(Hash + ".jpeg", ".jpg")]
    [InlineData(Hash + ".TXT", ".txt")]
    [InlineData(Hash + ".Png", ".png")]
    [InlineData(Hash, ".txt")]
    [InlineData(Hash + "txt", ".txt")]
    [InlineData(Hash + ".txt.bak", ".txt")]
    [InlineData(Hash + ".bak.txt", ".txt")]
    [InlineData(Hash + ".txt.", ".txt")]
    [InlineData(Hash + ".txt ", ".txt")]
    [InlineData(Hash + ".txt:walrus", ".txt")]
    public void A_name_that_does_not_end_in_exactly_the_extension_asked_for_is_refused(string name, string extension)
    {
        Assert.False(ClipPaths.IsBlobName(name, extension));
    }

    [Theory]
    [InlineData("x" + Hash + ".txt")]
    [InlineData(" " + Hash + ".txt")]
    [InlineData("." + Hash + ".txt")]
    [InlineData(@"..\" + Hash + ".txt")]
    [InlineData("../" + Hash + ".txt")]
    [InlineData(@"2026-10-03\" + Hash + ".txt")]
    [InlineData("sub/" + Hash + ".txt")]
    [InlineData(@"C:\Otters\" + Hash + ".txt")]
    [InlineData("C:" + Hash + ".txt")]
    [InlineData(@"\\walrus\share\" + Hash + ".txt")]
    public void A_name_with_anything_in_front_of_the_hash_is_refused(string name)
    {
        Assert.False(ClipPaths.IsBlobName(name, ".txt"));
        Assert.False(ClipPaths.IsBlobName(name));
    }

    [Fact]
    public void A_name_that_would_leave_the_folder_is_refused_even_when_it_is_as_long_as_a_real_one()
    {
        // Each is 64 characters and the extension, so only what the characters are can refuse it.
        string[] names =
        [
            "../" + Hash[3..] + ".txt",
            @"..\" + Hash[3..] + ".txt",
            Hash[..31] + ".." + Hash[33..] + ".txt",
            Hash[..31] + "/" + Hash[32..] + ".txt",
            Hash[..31] + @"\" + Hash[32..] + ".txt",
            "C:" + Hash[2..] + ".txt",
            Hash[..62] + ".." + ".txt",
        ];

        Assert.All(names, name =>
        {
            Assert.Equal(Hash.Length + ".txt".Length, name.Length);
            Assert.False(ClipPaths.IsBlobName(name, ".txt"));
            Assert.False(ClipPaths.IsBlobName(name));
        });
    }

    [Fact]
    public void A_character_that_is_not_a_lower_case_hex_digit_is_refused_wherever_in_the_hash_it_stands()
    {
        // A dot, both separators, a colon, a space, a zero character, letters just past the hex
        // digits in both cases, and a digit that is not one of the ten ASCII ones (U+FF11).
        char[] others = ['.', '/', '\\', ':', ' ', '\0', 'g', 'G', 'A', 'F', 'z', '-', '_', (char)0xFF11];

        for (int at = 0; at < Hash.Length; at++)
        {
            foreach (char other in others)
            {
                string name = Hash[..at] + other + Hash[(at + 1)..] + ".png";

                Assert.False(ClipPaths.IsBlobName(name, ".png"), $"Accepted with character {(int)other} at {at}.");
            }
        }
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_hash_and_a_picture_extension_are_the_name_of_a_kept_picture(string extension)
    {
        Assert.True(ClipPaths.IsPictureName(Hash + extension));
    }

    [Theory]
    [InlineData(Hash + ".txt")]
    [InlineData(Hash + ".html")]
    [InlineData(Hash + ".rtf")]
    [InlineData(Hash + ".PNG")]
    [InlineData(Hash + ".Jpg")]
    [InlineData(Hash + ".webp")]
    [InlineData(Hash + ".tif")]
    [InlineData(Hash + ".svg")]
    [InlineData(Hash + ".ico")]
    [InlineData(Hash + ".png.txt")]
    [InlineData(Hash + ".png.")]
    [InlineData(Hash)]
    [InlineData("walrus.png")]
    [InlineData(@"..\" + Hash + ".png")]
    [InlineData("")]
    public void Nothing_else_is_the_name_of_a_kept_picture(string name)
    {
        Assert.False(ClipPaths.IsPictureName(name));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".rtf")]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_hash_and_any_of_the_extensions_in_use_are_the_name_of_a_content_file(string extension)
    {
        Assert.True(ClipPaths.IsBlobName(Hash + extension));
    }

    [Theory]
    [InlineData(Hash + ".pdf")]
    [InlineData(Hash + ".htm")]
    [InlineData(Hash + ".json")]
    [InlineData(Hash + ".jsonl")]
    [InlineData(Hash + ".exe")]
    [InlineData(Hash + ".webp")]
    [InlineData(Hash + ".TXT")]
    [InlineData(Hash + ".Html")]
    [InlineData(Hash + ".txt.tmp")]
    [InlineData(Hash + ".")]
    [InlineData(Hash)]
    [InlineData("walrus.txt")]
    [InlineData("")]
    public void A_hash_with_any_other_extension_is_not_the_name_of_a_content_file(string name)
    {
        Assert.False(ClipPaths.IsBlobName(name));
    }

    // None of these files exists: only the name is looked at.
    [Theory]
    [InlineData(@"C:\Otters\walrus.png", ".png")]
    [InlineData(@"C:\Otters\walrus.PNG", ".png")]
    [InlineData("walrus.JpG", ".jpg")]
    [InlineData("walrus.JPEG", ".jpeg")]
    [InlineData("walrus.Gif", ".gif")]
    [InlineData("walrus.BMP", ".bmp")]
    [InlineData("C:/Otters/walrus.jpg", ".jpg")]
    [InlineData(@"C:\Otters\notes.txt.png", ".png")]
    [InlineData(@"\\walrus\share\otter.gif", ".gif")]
    public void A_picture_files_extension_is_given_in_lower_case_whatever_case_it_is_written_in(string path, string expected)
    {
        Assert.Equal(expected, ClipPaths.PictureExtensionOf(path));
    }

    [Theory]
    [InlineData(@"C:\Otters\notes.txt")]
    [InlineData(@"C:\Otters\walrus.pdf")]
    [InlineData(@"C:\Otters\walrus")]
    [InlineData(@"C:\Otters\walrus.png.txt")]
    [InlineData(@"C:\Otters.png\walrus")]
    [InlineData(@"C:\Otters.png\notes.txt")]
    [InlineData(@"C:\Otters\walrus.webp")]
    [InlineData(@"C:\Otters\walrus.pngs")]
    [InlineData(@"C:\Otters\walrus.")]
    [InlineData("png")]
    [InlineData("")]
    public void A_file_that_is_not_one_of_the_picture_kinds_has_no_picture_extension(string path)
    {
        Assert.Null(ClipPaths.PictureExtensionOf(path));
    }

    [Fact]
    public void A_picture_extension_in_upper_case_is_known_under_a_culture_with_other_rules_for_case()
    {
        var turkish = new CultureInfo("tr-TR");

        // Proves that this culture really lowers an "I" to another letter, so the check below cannot pass for nothing.
        Assert.NotEqual(".gif", ".GIF".ToLower(turkish));

        Assert.Equal(".gif", InCulture(turkish, () => ClipPaths.PictureExtensionOf(@"C:\Otters\WALRUS.GIF")));
    }
}
