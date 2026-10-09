using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Tests;

public class ClipEntryTests
{
    // Every lower-case hex digit, four times over: the 64 characters a SHA-256 is written in.
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private const string Device = "OTTER-PC";
    private const string Folder = @"C:\Wyvern\clipboard\OTTER-PC\2026-10-03";
    private const string Link = "https://example.org/otters";

    // U+202E turns the text after it around, and U+200B is a space with no width. Neither shows.
    private const char TurnsTextAround = (char)0x202E;
    private const char ZeroWidthSpace = (char)0x200B;
    private const char Ellipsis = (char)0x2026;

    private static readonly DateOnly Day = new(2026, 10, 3);
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 14, 2, 11, TimeSpan.Zero);

    // U+1F600, which takes two UTF-16 characters.
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static EntryRecord TextRecord(string text, string? sourceApp = "otter.exe") =>
        new("a1", Time, sourceApp) { Text = text, TextLength = text.Length };

    /// <summary>A record with every form, each of which can be changed by itself.</summary>
    private static EntryRecord Full() => new("a1", Time, "otter.exe")
    {
        Text = "walrus",
        TextLength = 250_000,
        TextFile = Hash + ".txt",
        Html = Hash + ".html",
        Rtf = Hash + ".rtf",
        Image = Hash + ".png",
        Files = [@"C:\Otters\walrus.png", @"C:\Otters\otter.jpg", @"C:\Otters\notes.txt"],
        Pictures = [new KeptPicture("walrus.png", Hash + ".png"), new KeptPicture("otter.jpg", Hash + ".jpg")],
    };

    private static ClipEntry From(EntryRecord record) => ClipEntry.From(record, Device, Day, Folder);

    [Fact]
    public void An_entry_takes_its_id_and_time_from_the_record_and_is_told_its_machine_day_and_folder()
    {
        var entry = ClipEntry.From(TextRecord("walrus"), "WALRUS-PC", new DateOnly(2026, 10, 4), @"D:\Elsewhere\WALRUS-PC\2026-10-04");

        Assert.Equal("a1", entry.Id);
        Assert.Equal(Time, entry.Time);
        Assert.Equal("WALRUS-PC", entry.Device);
        Assert.Equal(new DateOnly(2026, 10, 4), entry.Day);
        Assert.Equal(@"D:\Elsewhere\WALRUS-PC\2026-10-04", entry.Folder);
    }

    [Fact]
    public void An_entry_takes_every_form_of_its_record()
    {
        var record = Full();

        var entry = From(record);

        Assert.Equal("walrus", entry.Text);
        Assert.Equal(250_000, entry.TextLength);
        Assert.Equal(Hash + ".txt", entry.TextFile);
        Assert.Equal(Hash + ".html", entry.Html);
        Assert.Equal(Hash + ".rtf", entry.Rtf);
        Assert.Equal(Hash + ".png", entry.Image);
        Assert.Equal(record.Files, entry.Files);
        Assert.Equal(record.Pictures, entry.Pictures);
        Assert.Equal("otter.exe", entry.SourceApp);
    }

    [Fact]
    public void An_entry_without_text_has_no_text_and_a_length_of_nothing()
    {
        var entry = From(new EntryRecord("a1", Time, null) { Image = Hash + ".png" });

        Assert.Null(entry.Text);
        Assert.Equal(0, entry.TextLength);
    }

    // How much text is held.

    [Fact]
    public void A_text_as_long_as_the_limit_is_kept_whole()
    {
        string text = new('w', ClipEntry.MaxTextStart);

        Assert.Equal(text, From(TextRecord(text)).Text);
    }

    [Fact]
    public void A_longer_text_is_cut_to_its_first_characters_up_to_the_limit()
    {
        string text = "start" + new string('w', ClipEntry.MaxTextStart) + "end";

        var entry = From(TextRecord(text));

        Assert.Equal(text[..ClipEntry.MaxTextStart], entry.Text);
    }

    [Fact]
    public void A_text_is_never_cut_between_the_two_halves_of_a_character()
    {
        // The emoji's first half is the last character that fits, so the emoji is left out whole.
        string text = new string('w', ClipEntry.MaxTextStart - 1) + Emoji + "tail";

        var entry = From(TextRecord(text));

        Assert.Equal(new string('w', ClipEntry.MaxTextStart - 1), entry.Text);
    }

    [Fact]
    public void A_character_of_two_halves_that_ends_at_the_limit_is_kept()
    {
        string text = new string('w', ClipEntry.MaxTextStart - 2) + Emoji + "tail";

        var entry = From(TextRecord(text));

        Assert.Equal(new string('w', ClipEntry.MaxTextStart - 2) + Emoji, entry.Text);
    }

    [Fact]
    public void The_length_of_the_whole_text_is_the_records_when_the_record_gives_more_than_is_kept()
    {
        var entry = From(new EntryRecord("a1", Time, null) { Text = "walrus", TextLength = 250_000, TextFile = Hash + ".txt" });

        Assert.Equal(250_000, entry.TextLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void The_length_of_the_whole_text_is_that_of_the_kept_text_when_the_record_gives_less(int written)
    {
        var entry = From(new EntryRecord("a1", Time, null) { Text = "walrus", TextLength = written });

        Assert.Equal(6, entry.TextLength);
    }

    [Fact]
    public void A_long_text_keeps_its_whole_length_when_its_start_is_all_that_is_held()
    {
        string text = new('w', ClipEntry.MaxTextStart + 5);

        var entry = From(TextRecord(text));

        Assert.Equal(ClipEntry.MaxTextStart, entry.Text?.Length);
        Assert.Equal(ClipEntry.MaxTextStart + 5, entry.TextLength);
    }

    [Fact]
    public void The_start_of_a_short_text_is_the_text()
    {
        Assert.Equal("walrus", ClipEntry.Start("walrus"));
        Assert.Equal("", ClipEntry.Start(""));
    }

    [Fact]
    public void The_start_of_a_text_at_the_limit_is_the_text()
    {
        string text = new string('w', ClipEntry.MaxTextStart - 3) + "end";

        Assert.Equal(text, ClipEntry.Start(text));
    }

    [Fact]
    public void The_start_of_a_longer_text_is_its_first_characters_up_to_the_limit()
    {
        string text = "start" + new string('w', ClipEntry.MaxTextStart) + "end";

        string start = ClipEntry.Start(text);

        Assert.Equal(ClipEntry.MaxTextStart, start.Length);
        Assert.Equal(text[..ClipEntry.MaxTextStart], start);
    }

    [Fact]
    public void The_start_never_ends_on_the_first_half_of_a_character()
    {
        string text = new string('w', ClipEntry.MaxTextStart - 1) + Emoji;

        string start = ClipEntry.Start(text);

        Assert.Equal(new string('w', ClipEntry.MaxTextStart - 1), start);
    }

    [Fact]
    public void The_start_keeps_a_character_of_two_halves_that_just_fits()
    {
        string text = new string('w', ClipEntry.MaxTextStart - 2) + Emoji + "w";

        string start = ClipEntry.Start(text);

        Assert.Equal(new string('w', ClipEntry.MaxTextStart - 2) + Emoji, start);
    }

    // How many paths and pictures are held.

    [Fact]
    public void More_copied_files_than_the_limit_are_cut_to_the_first_ones()
    {
        var paths = Enumerable.Range(0, ClipLimits.MaxFiles + 1).Select(i => $@"C:\Otters\otter-{i}.txt").ToList();

        var entry = From(new EntryRecord("a1", Time, null) { Files = paths });

        Assert.Equal(ClipLimits.MaxFiles, entry.Files.Count);
        Assert.Equal(paths.Take(ClipLimits.MaxFiles), entry.Files);
    }

    [Fact]
    public void As_many_copied_files_as_the_limit_are_all_kept()
    {
        var paths = Enumerable.Range(0, ClipLimits.MaxFiles).Select(i => $@"C:\Otters\otter-{i}.txt").ToList();

        var entry = From(new EntryRecord("a1", Time, null) { Files = paths });

        Assert.Equal(paths, entry.Files);
    }

    [Fact]
    public void More_kept_pictures_than_the_limit_are_cut_to_the_first_ones()
    {
        var pictures = Enumerable.Range(0, ClipLimits.MaxPictures + 1).Select(i => new KeptPicture($"otter-{i}.png", Hash + ".png")).ToList();

        var entry = From(new EntryRecord("a1", Time, null) { Files = [@"C:\Otters\otter-0.png"], Pictures = pictures });

        Assert.Equal(ClipLimits.MaxPictures, entry.Pictures.Count);
        Assert.Equal(pictures.Take(ClipLimits.MaxPictures), entry.Pictures);
    }

    [Fact]
    public void As_many_kept_pictures_as_the_limit_are_all_kept()
    {
        var pictures = Enumerable.Range(0, ClipLimits.MaxPictures).Select(i => new KeptPicture($"otter-{i}.png", Hash + ".png")).ToList();

        var entry = From(new EntryRecord("a1", Time, null) { Files = [@"C:\Otters\otter-0.png"], Pictures = pictures });

        Assert.Equal(pictures, entry.Pictures);
    }

    // The name of the program, which is shown.

    [Fact]
    public void Characters_that_show_nothing_are_taken_out_of_the_programs_name()
    {
        var entry = From(TextRecord("walrus", "chro" + TurnsTextAround + "me" + ZeroWidthSpace + ".exe"));

        Assert.Equal("chrome.exe", entry.SourceApp);
    }

    [Fact]
    public void White_space_around_the_programs_name_is_dropped()
    {
        Assert.Equal("chrome.exe", From(TextRecord("walrus", "  chrome.exe \t")).SourceApp);
    }

    [Fact]
    public void White_space_that_is_left_at_the_ends_once_the_invisible_characters_are_out_is_dropped_too()
    {
        var entry = From(TextRecord("walrus", TurnsTextAround + " chrome.exe " + ZeroWidthSpace));

        Assert.Equal("chrome.exe", entry.SourceApp);
    }

    [Fact]
    public void A_programs_name_as_long_as_the_limit_is_kept_whole()
    {
        string name = new('x', ClipEntry.MaxSourceAppLength);

        Assert.Equal(name, From(TextRecord("walrus", name)).SourceApp);
    }

    [Fact]
    public void A_longer_programs_name_is_shortened_to_the_limit_and_ends_in_an_ellipsis()
    {
        var entry = From(TextRecord("walrus", new string('x', ClipEntry.MaxSourceAppLength + 1)));

        Assert.Equal(new string('x', ClipEntry.MaxSourceAppLength - 1) + Ellipsis, entry.SourceApp);
    }

    [Fact]
    public void A_programs_name_is_never_cut_between_the_two_halves_of_a_character()
    {
        // Only the first half of the emoji would fit before the ellipsis.
        var entry = From(TextRecord("walrus", new string('x', ClipEntry.MaxSourceAppLength - 2) + Emoji + "tail"));

        Assert.Equal(new string('x', ClipEntry.MaxSourceAppLength - 2) + Ellipsis, entry.SourceApp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void No_program_or_a_blank_name_means_that_the_program_is_not_known(string? sourceApp)
    {
        Assert.Null(From(TextRecord("walrus", sourceApp)).SourceApp);
    }

    [Fact]
    public void A_programs_name_with_nothing_to_show_in_it_means_that_the_program_is_not_known()
    {
        var entry = From(TextRecord("walrus", " " + TurnsTextAround + ZeroWidthSpace + " "));

        Assert.Null(entry.SourceApp);
    }

    // A link and its label.

    [Theory]
    [InlineData("https://example.org/otters", "https://example.org/otters")]
    [InlineData("http://example.org", "http://example.org")]
    [InlineData("https://example.org/otters?kind=sea&page=2#walrus", "https://example.org/otters?kind=sea&page=2#walrus")]
    [InlineData("  https://example.org/otters  ", "https://example.org/otters")]
    [InlineData("https://example.org/otters\r\n", "https://example.org/otters")]
    public void A_text_that_is_one_web_address_is_a_link(string text, string link)
    {
        var entry = From(TextRecord(text));

        Assert.Equal(link, entry.Link);
        Assert.Equal(text, entry.Text);
    }

    [Theory]
    [InlineData("walrus")]
    [InlineData("example.org")]
    [InlineData("see https://example.org/otters")]
    [InlineData("https://example.org/otters and more")]
    [InlineData("https://example.org/otters\nhttps://example.org/walrus")]
    [InlineData("ftp://example.org/otters")]
    [InlineData("")]
    public void A_text_that_is_anything_else_is_not_a_link(string text)
    {
        Assert.Null(From(TextRecord(text)).Link);
    }

    [Fact]
    public void An_entry_without_text_is_not_a_link()
    {
        Assert.Null(From(new EntryRecord("a1", Time, null) { Image = Hash + ".png", Label = "Otter pictures" }).Link);
    }

    [Fact]
    public void A_text_whose_whole_is_in_a_file_is_not_a_link_even_when_its_start_looks_like_one()
    {
        var record = new EntryRecord("a1", Time, null) { Text = Link, TextLength = 250_000, TextFile = Hash + ".txt", Label = "Otter pictures" };

        var entry = From(record);

        Assert.Null(entry.Link);
        Assert.Null(entry.Label);
    }

    [Fact]
    public void The_label_of_a_link_is_kept()
    {
        var entry = From(TextRecord(Link) with { Label = "Otter pictures" });

        Assert.Equal(Link, entry.Link);
        Assert.Equal("Otter pictures", entry.Label);
    }

    [Fact]
    public void A_link_without_a_label_has_none()
    {
        Assert.Null(From(TextRecord(Link)).Label);
    }

    [Fact]
    public void A_label_beside_text_that_is_not_a_link_is_dropped()
    {
        Assert.Null(From(TextRecord("walrus") with { Label = "Otter pictures" }).Label);
    }

    [Fact]
    public void Characters_that_show_nothing_and_white_space_at_the_ends_are_taken_out_of_a_label()
    {
        var entry = From(TextRecord(Link) with { Label = "  Otter" + TurnsTextAround + " pict" + ZeroWidthSpace + "ures  " });

        Assert.Equal("Otter pictures", entry.Label);
    }

    [Fact]
    public void A_label_as_long_as_the_limit_is_kept_whole()
    {
        string label = new('x', ClipLinks.MaxLabelLength);

        Assert.Equal(label, From(TextRecord(Link) with { Label = label }).Label);
    }

    [Fact]
    public void A_longer_label_is_shortened_to_the_limit_and_ends_in_an_ellipsis()
    {
        var entry = From(TextRecord(Link) with { Label = new string('x', ClipLinks.MaxLabelLength + 1) });

        Assert.Equal(new string('x', ClipLinks.MaxLabelLength - 1) + Ellipsis, entry.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_label_means_no_label(string label)
    {
        Assert.Null(From(TextRecord(Link) with { Label = label }).Label);
    }

    [Fact]
    public void A_label_with_nothing_to_show_in_it_means_no_label()
    {
        Assert.Null(From(TextRecord(Link) with { Label = " " + TurnsTextAround + " " }).Label);
    }

    // A record can come from another machine, so the label in it is judged as capture judges
    // the words of a link: by the one rule, on the same words (see ClipLinksTests.LabelCases).
    // A label that is not fit to show is no label, and the address stands alone.
    [Theory]
    [MemberData(nameof(ClipLinksTests.LabelCases), MemberType = typeof(ClipLinksTests))]
    public void The_label_of_a_record_is_judged_by_the_same_rule_as_the_words_of_a_link_at_capture(string written, string? shown)
    {
        var entry = From(TextRecord(Link) with { Label = written });

        Assert.Equal(shown, entry.Label);
        Assert.Equal(Link, entry.Link);
    }

    [Fact]
    public void A_label_that_is_another_address_is_dropped_and_the_link_is_kept()
    {
        var entry = From(TextRecord(Link) with { Label = "https://bank.example/login" });

        Assert.Null(entry.Label);
        Assert.Equal(Link, entry.Link);
    }

    [Fact]
    public void A_label_is_made_one_line_like_the_words_of_a_link_at_capture()
    {
        // Taking out the control characters alone would join the words either side of the tab.
        Assert.Equal("Otter pictures", From(TextRecord(Link) with { Label = "Otter\tpictures" }).Label);
    }

    [Theory]
    [InlineData(0x3164)]
    [InlineData(0xFE0F)]
    [InlineData(0xE000)]
    [InlineData(0x2065)]
    [InlineData(0xF0000)]
    public void A_character_that_shows_nothing_but_is_not_a_control_or_format_character_is_taken_out_of_the_programs_name_too(int code)
    {
        var entry = From(TextRecord("walrus", "chro" + char.ConvertFromUtf32(code) + "me.exe"));

        Assert.Equal("chrome.exe", entry.SourceApp);
    }

    [Fact]
    public void A_programs_name_with_only_characters_that_show_nothing_means_that_the_program_is_not_known_whichever_they_are()
    {
        Assert.Null(From(TextRecord("walrus", char.ConvertFromUtf32(0x3164) + " " + char.ConvertFromUtf32(0xE000))).SourceApp);
    }

    // What an entry says about itself.

    [Fact]
    public void An_entrys_item_id_is_its_id_after_the_word_clipboard_and_a_colon()
    {
        var entry = From(new EntryRecord("3f2c9d1e-walrus", Time, null) { Text = "walrus", TextLength = 6 });

        Assert.Equal("clipboard:3f2c9d1e-walrus", entry.ItemId);
        Assert.Equal("clipboard:", ClipEntry.IdPrefix);
    }

    [Fact]
    public void An_entry_with_a_picture_from_the_clipboard_has_a_picture()
    {
        Assert.True(From(new EntryRecord("a1", Time, null) { Image = Hash + ".png" }).HasPicture);
    }

    [Fact]
    public void An_entry_with_a_picture_kept_from_a_copied_file_has_a_picture()
    {
        var record = new EntryRecord("a1", Time, null)
        {
            Files = [@"C:\Otters\walrus.png"],
            Pictures = [new KeptPicture("walrus.png", Hash + ".png")],
        };

        Assert.True(From(record).HasPicture);
    }

    [Fact]
    public void An_entry_with_neither_has_no_picture()
    {
        Assert.False(From(TextRecord("walrus")).HasPicture);
        Assert.False(From(new EntryRecord("a1", Time, null) { Files = [@"C:\Otters\walrus.png"] }).HasPicture);
        Assert.False(From(new EntryRecord("a1", Time, null) { Html = Hash + ".html", Rtf = Hash + ".rtf" }).HasPicture);
    }

    [Fact]
    public void The_path_of_a_file_the_entry_names_is_in_the_entrys_folder()
    {
        var entry = From(new EntryRecord("a1", Time, null) { Image = Hash + ".png" });

        Assert.Equal(Folder + @"\" + Hash + ".png", entry.PathOf(Hash + ".png"));
    }

    // Whether two entries hold the same content.

    [Fact]
    public void Two_entries_made_from_equal_records_hold_the_same_content()
    {
        // Each call makes lists of its own, so the lists are compared by what is in them.
        Assert.True(From(Full()).SameContentAs(From(Full())));
    }

    [Fact]
    public void Where_and_when_it_was_copied_and_from_which_program_does_not_count()
    {
        var first = ClipEntry.From(Full(), "OTTER-PC", new DateOnly(2026, 10, 3), @"C:\Wyvern\clipboard\OTTER-PC\2026-10-03");
        var second = ClipEntry.From(
            Full() with { Id = "b2", Time = Time.AddDays(1), SourceApp = "walrus.exe" },
            "WALRUS-PC",
            new DateOnly(2026, 10, 4),
            @"C:\Wyvern\clipboard\WALRUS-PC\2026-10-04");

        Assert.True(first.SameContentAs(second));
        Assert.True(second.SameContentAs(first));
    }

    [Fact]
    public void The_label_of_a_link_does_not_count()
    {
        var first = From(TextRecord(Link) with { Label = "Otter pictures" });
        var second = From(TextRecord(Link) with { Label = "Walrus pictures" });

        // Proves that the two really differ in their labels, so the check below cannot pass for nothing.
        Assert.NotEqual(first.Label, second.Label);

        Assert.True(first.SameContentAs(second));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("text length")]
    [InlineData("text file")]
    [InlineData("no text file")]
    [InlineData("html")]
    [InlineData("no html")]
    [InlineData("rich text")]
    [InlineData("no rich text")]
    [InlineData("picture")]
    [InlineData("no picture")]
    [InlineData("one file")]
    [InlineData("one file fewer")]
    [InlineData("order of the files")]
    [InlineData("no files")]
    [InlineData("name of a kept picture")]
    [InlineData("file of a kept picture")]
    [InlineData("one kept picture fewer")]
    [InlineData("order of the kept pictures")]
    public void Entries_that_differ_in_one_form_do_not_hold_the_same_content(string difference)
    {
        var record = Full();
        var changed = difference switch
        {
            // The same number of characters, so that the length stays what it was.
            "text" => record with { Text = "otters" },
            "text length" => record with { TextLength = 250_001 },
            "text file" => record with { TextFile = OtherHash + ".txt" },
            "no text file" => record with { TextFile = null },
            "html" => record with { Html = OtherHash + ".html" },
            "no html" => record with { Html = null },
            "rich text" => record with { Rtf = OtherHash + ".rtf" },
            "no rich text" => record with { Rtf = null },
            "picture" => record with { Image = OtherHash + ".png" },
            "no picture" => record with { Image = null },
            "one file" => record with { Files = [@"C:\Otters\walrus.png", @"C:\Otters\otter.jpg", @"C:\Otters\other.txt"] },
            "one file fewer" => record with { Files = [@"C:\Otters\walrus.png", @"C:\Otters\otter.jpg"] },
            "order of the files" => record with { Files = [@"C:\Otters\otter.jpg", @"C:\Otters\walrus.png", @"C:\Otters\notes.txt"] },
            "no files" => record with { Files = [] },
            "name of a kept picture" => record with { Pictures = [new KeptPicture("seal.png", Hash + ".png"), new KeptPicture("otter.jpg", Hash + ".jpg")] },
            "file of a kept picture" => record with { Pictures = [new KeptPicture("walrus.png", OtherHash + ".png"), new KeptPicture("otter.jpg", Hash + ".jpg")] },
            "one kept picture fewer" => record with { Pictures = [new KeptPicture("walrus.png", Hash + ".png")] },
            "order of the kept pictures" => record with { Pictures = [new KeptPicture("otter.jpg", Hash + ".jpg"), new KeptPicture("walrus.png", Hash + ".png")] },
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };

        Assert.False(From(record).SameContentAs(From(changed)));
        Assert.False(From(changed).SameContentAs(From(record)));
    }
}
