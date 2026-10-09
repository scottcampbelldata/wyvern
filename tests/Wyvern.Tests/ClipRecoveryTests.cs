using System.Globalization;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;

namespace Wyvern.Tests;

/// <summary>
/// Saving a copy of an entry's pictures. The Pictures folder is a folder in a temporary folder,
/// the launcher only writes down what it was asked to show, and the picture files are a few
/// made-up bytes that nothing decodes.
/// </summary>
public sealed class ClipRecoveryTests : IDisposable
{
    // In a zone thirteen hours ahead this is 01:34:56 on the 4th. The local day is not the UTC
    // day, so a name made from the wrong one shows.
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 12, 34, 56, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 3);

    // A zone of the test's own, so that the name does not depend on where the machine is.
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Thirteen ahead", TimeSpan.FromHours(13), "Thirteen ahead", "Thirteen ahead");

    private const string ClipboardCopy = "Clipboard 2026-10-04 01.34.56";

    private readonly TempDir _dir = new();
    private readonly string _history;
    private readonly string _day;
    private readonly string _pictures;
    private readonly string _recovered;
    private readonly FakeProcessLauncher _launcher = new();
    private readonly List<string> _notes = [];
    private readonly ClipRecovery _recovery;

    public ClipRecoveryTests()
    {
        _history = _dir.File("clipboard");
        _day = Path.Combine(_history, "OTTER-PC", "2026-10-03");
        Directory.CreateDirectory(_day);

        // The Pictures folder is there, as it is on the owner's machine. The folder for the
        // copies inside it is not, as before the first picture is saved.
        _pictures = Path.Combine(_dir.Path, "Pictures");
        Directory.CreateDirectory(_pictures);
        _recovered = Path.Combine(_pictures, "Wyvern recovered");
        _recovery = new ClipRecovery(() => _pictures, _launcher, _notes.Add, Zone);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>An entry that holds nothing yet. Each test says what it holds.</summary>
    private ClipEntry Entry() => new("id", Time, "OTTER-PC", Day, _day);

    /// <summary>Writes a content file into the day's folder under the name the history gives it.</summary>
    /// <returns>That name.</returns>
    private string Stored(byte[] content, string extension)
    {
        string name = ClipPaths.BlobName(content, extension);
        File.WriteAllBytes(Path.Combine(_day, name), content);
        return name;
    }

    /// <summary>The name of a content file that is not there: the record names it, and the file has not arrived.</summary>
    private static string Gone(byte[] content, string extension) => ClipPaths.BlobName(content, extension);

    /// <summary>The full path of a copy with this name in the folder the copies go into.</summary>
    private string Copy(string name) => Path.Combine(_recovered, name);

    /// <summary>Every file in the temporary folder that is not one of the history's own, in order of name.</summary>
    private string[] Written() =>
        [
            .. Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(_history + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>Saves an entry that holds one kept picture with this name, stored under this extension.</summary>
    /// <returns>The file name of the copy. It has to be in the folder the copies go into, and the only file that was written anywhere.</returns>
    private string SavedAs(string name, string extension = ".png")
    {
        var saved = _recovery.Save(Entry() with { Pictures = [new KeptPicture(name, Stored([7, 7, 7], extension))] });

        string copy = Assert.Single(saved);
        Assert.Equal(_recovered, Path.GetDirectoryName(copy));
        Assert.Equal([copy], Written());
        return Path.GetFileName(copy);
    }

    [Fact]
    public void A_clipboard_picture_is_saved_under_the_local_date_and_time_it_was_copied()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        var saved = _recovery.Save(Entry() with { Image = Stored(png, ClipPaths.ImageExtension) });

        string copy = Path.Combine(_pictures, "Wyvern recovered", "Clipboard 2026-10-04 01.34.56.png");
        Assert.Equal([copy], saved);
        Assert.Equal(png, File.ReadAllBytes(copy));
        Assert.Empty(_notes);
    }

    [Fact]
    public void The_folder_for_the_copies_is_made_when_it_is_missing()
    {
        Assert.False(Directory.Exists(_recovered));

        _recovery.Save(Entry() with { Image = Stored([1, 2, 3], ClipPaths.ImageExtension) });

        Assert.True(Directory.Exists(_recovered));
    }

    [Fact]
    public void The_saved_copy_is_shown_in_explorer()
    {
        var saved = _recovery.Save(Entry() with { Image = Stored([1, 2, 3], ClipPaths.ImageExtension) });

        Assert.Equal(["reveal " + Copy(ClipboardCopy + ".png")], _launcher.Calls);
        Assert.Equal([Copy(ClipboardCopy + ".png")], saved);
    }

    [Fact]
    public void The_name_is_made_in_the_given_time_zone_however_the_time_was_written()
    {
        // The same moment, written as a time five hours behind UTC.
        var entry = new ClipEntry("id", new DateTimeOffset(2026, 10, 3, 7, 34, 56, TimeSpan.FromHours(-5)), "OTTER-PC", Day, _day)
        {
            Image = Stored([1, 2, 3], ClipPaths.ImageExtension),
        };

        var saved = _recovery.Save(entry);

        Assert.Equal([Copy(ClipboardCopy + ".png")], saved);
    }

    [Fact]
    public void The_name_does_not_depend_on_the_language_of_the_machine()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Saudi Arabia's default calendar is not the Gregorian one.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            var saved = _recovery.Save(Entry() with { Image = Stored([1, 2, 3], ClipPaths.ImageExtension) });

            Assert.Equal([Copy(ClipboardCopy + ".png")], saved);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Saving_the_same_entry_again_gives_each_further_copy_a_number()
    {
        var entry = Entry() with { Image = Stored([1, 2, 3], ClipPaths.ImageExtension) };

        var first = _recovery.Save(entry);
        var second = _recovery.Save(entry);
        var third = _recovery.Save(entry);

        Assert.Equal([Copy(ClipboardCopy + ".png")], first);
        Assert.Equal([Copy(ClipboardCopy + "-2.png")], second);
        Assert.Equal([Copy(ClipboardCopy + "-3.png")], third);
        Assert.Equal(["reveal " + first[0], "reveal " + second[0], "reveal " + third[0]], _launcher.Calls);
    }

    [Fact]
    public void A_picture_copied_in_the_same_second_does_not_overwrite_the_one_saved_before()
    {
        byte[] one = [1, 1, 1];
        byte[] other = [2, 2, 2, 2];
        _recovery.Save(Entry() with { Image = Stored(one, ClipPaths.ImageExtension) });

        _recovery.Save(Entry() with { Image = Stored(other, ClipPaths.ImageExtension) });

        Assert.Equal(one, File.ReadAllBytes(Copy(ClipboardCopy + ".png")));
        Assert.Equal(other, File.ReadAllBytes(Copy(ClipboardCopy + "-2.png")));
    }

    [Fact]
    public void A_kept_picture_is_saved_under_the_name_its_file_had()
    {
        byte[] photo = [0xFF, 0xD8, 7, 7, 7];

        var saved = _recovery.Save(Entry() with { Pictures = [new KeptPicture("holiday.jpg", Stored(photo, ".jpg"))] });

        Assert.Equal([Copy("holiday.jpg")], saved);
        Assert.Equal(photo, File.ReadAllBytes(Copy("holiday.jpg")));
        Assert.Equal(["reveal " + Copy("holiday.jpg")], _launcher.Calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Two_kept_pictures_with_the_same_name_are_both_saved()
    {
        byte[] beach = [1, 1, 1];
        byte[] hills = [2, 2, 2, 2];
        var entry = Entry() with
        {
            Pictures =
            [
                new KeptPicture("holiday.jpg", Stored(beach, ".jpg")),
                new KeptPicture("holiday.jpg", Stored(hills, ".jpg")),
            ],
        };

        var saved = _recovery.Save(entry);

        Assert.Equal([Copy("holiday.jpg"), Copy("holiday-2.jpg")], saved);
        Assert.Equal(beach, File.ReadAllBytes(Copy("holiday.jpg")));
        Assert.Equal(hills, File.ReadAllBytes(Copy("holiday-2.jpg")));
    }

    [Fact]
    public void A_file_already_in_the_folder_is_never_overwritten()
    {
        byte[] theOwners = [5, 5, 5, 5, 5];
        byte[] photo = [0xFF, 0xD8, 7, 7, 7];
        Directory.CreateDirectory(_recovered);
        File.WriteAllBytes(Copy("holiday.jpg"), theOwners);

        var saved = _recovery.Save(Entry() with { Pictures = [new KeptPicture("holiday.jpg", Stored(photo, ".jpg"))] });

        Assert.Equal([Copy("holiday-2.jpg")], saved);
        Assert.Equal(theOwners, File.ReadAllBytes(Copy("holiday.jpg")));
        Assert.Equal(photo, File.ReadAllBytes(Copy("holiday-2.jpg")));
    }

    [Theory]
    [InlineData("photo.exe", ".jpg", "photo.jpg")]
    [InlineData("photo.png", ".gif", "photo.gif")]
    [InlineData("photo.JPG", ".jpeg", "photo.jpeg")]
    [InlineData("photo", ".bmp", "photo.bmp")]
    public void The_extension_is_that_of_the_stored_file_whatever_the_name_says(string name, string extension, string expected)
    {
        Assert.Equal(expected, SavedAs(name, extension));
    }

    [Theory]
    [InlineData(@"..\..\evil.jpg", ".jpg", "evil.jpg")]
    [InlineData(@"C:\x\y.png", ".png", "y.png")]
    [InlineData("a/b/c.gif", ".gif", "c.gif")]
    [InlineData(@"\\server\share\z.bmp", ".bmp", "z.bmp")]
    public void A_name_with_folders_in_it_is_saved_under_its_last_part_only(string name, string extension, string expected)
    {
        // The copy is in the folder every copy goes into, and nothing was written anywhere else.
        Assert.Equal(expected, SavedAs(name, extension));
    }

    [Theory]
    [InlineData("what<now.png")]
    [InlineData("what>now.png")]
    [InlineData("what:now.png")]
    [InlineData("what\"now.png")]
    [InlineData("what|now.png")]
    [InlineData("what?now.png")]
    [InlineData("what*now.png")]
    public void A_character_windows_refuses_in_a_name_becomes_an_underscore(string name)
    {
        Assert.Equal("what_now.png", SavedAs(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_with_nothing_in_it_becomes_picture(string name)
    {
        Assert.Equal("picture.jpg", SavedAs(name, ".jpg"));
    }

    [Fact]
    public void A_name_that_is_only_an_extension_becomes_picture()
    {
        Assert.Equal("picture.jpg", SavedAs(".jpg", ".jpg"));
    }

    [Theory]
    [InlineData("nul.png", "_nul.png")]
    [InlineData("CON.png", "_CON.png")]
    [InlineData("prn.png", "_prn.png")]
    [InlineData("aux", "_aux.png")]
    [InlineData("com1.png", "_com1.png")]
    [InlineData("LPT9.png", "_LPT9.png")]
    [InlineData("nul.tar.png", "_nul.tar.png")]
    public void A_name_windows_keeps_for_a_device_gets_an_underscore_in_front(string name, string expected)
    {
        Assert.Equal(expected, SavedAs(name));
    }

    [Fact]
    public void Characters_that_show_nothing_are_taken_out_of_the_name()
    {
        // U+202E turns the text after it around, so that this name reads "photoexe.jpg" on
        // screen. Written as a number so that no tool can change it.
        string name = "photo" + (char)0x202E + "gpj.exe";

        Assert.Equal("photogpj.jpg", SavedAs(name, ".jpg"));
    }

    [Fact]
    public void A_long_name_is_kept_whole()
    {
        // Telephones and screenshot tools give pictures names of this length every day.
        const string name = "Screenshot 2026-10-03 at 12.34.56 from the second monitor.png";

        Assert.Equal(name, SavedAs(name));
    }

    [Fact]
    public void A_folder_that_has_the_name_counts_as_a_name_that_is_taken()
    {
        Directory.CreateDirectory(Copy("holiday.jpg"));

        Assert.Equal("holiday-2.jpg", SavedAs("holiday.jpg", ".jpg"));
    }

    [Fact]
    public void An_entry_with_both_kinds_saves_them_all_the_clipboard_picture_first_and_shows_the_first()
    {
        var entry = Entry() with
        {
            Image = Stored([1, 1, 1], ClipPaths.ImageExtension),
            Pictures =
            [
                new KeptPicture("holiday.jpg", Stored([2, 2, 2], ".jpg")),
                new KeptPicture("beach.gif", Stored([3, 3, 3], ".gif")),
            ],
        };

        var saved = _recovery.Save(entry);

        Assert.Equal([Copy(ClipboardCopy + ".png"), Copy("holiday.jpg"), Copy("beach.gif")], saved);
        Assert.Equal([1, 1, 1], File.ReadAllBytes(saved[0]));
        Assert.Equal([2, 2, 2], File.ReadAllBytes(saved[1]));
        Assert.Equal([3, 3, 3], File.ReadAllBytes(saved[2]));
        Assert.Equal(["reveal " + Copy(ClipboardCopy + ".png")], _launcher.Calls);
    }

    [Fact]
    public void An_entry_without_pictures_is_left_alone()
    {
        var entry = Entry() with { Text = "no picture here", TextLength = 15, Files = [_dir.File("report.docx")] };

        var saved = _recovery.Save(entry);

        Assert.Empty(saved);
        Assert.False(Directory.Exists(_recovered));
        Assert.Empty(_launcher.Calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_clipboard_picture_that_is_not_on_this_machine_saves_nothing_and_the_owner_is_told()
    {
        var saved = _recovery.Save(Entry() with { Image = Gone([1, 2, 3], ClipPaths.ImageExtension) });

        Assert.Empty(saved);
        Assert.Empty(Written());
        Assert.Equal([ClipRecovery.NothingSavedNote], _notes);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public void Kept_pictures_that_are_not_on_this_machine_save_nothing_and_the_owner_is_told()
    {
        var entry = Entry() with
        {
            Pictures =
            [
                new KeptPicture("holiday.jpg", Gone([2, 2, 2], ".jpg")),
                new KeptPicture("beach.gif", Gone([3, 3, 3], ".gif")),
            ],
        };

        var saved = _recovery.Save(entry);

        Assert.Empty(saved);
        Assert.Empty(Written());
        Assert.Equal([ClipRecovery.NothingSavedNote], _notes);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public void A_picture_that_is_missing_among_several_does_not_stop_the_others()
    {
        var entry = Entry() with
        {
            Image = Gone([1, 1, 1], ClipPaths.ImageExtension),
            Pictures =
            [
                new KeptPicture("holiday.jpg", Stored([2, 2, 2], ".jpg")),
                new KeptPicture("lost.png", Gone([3, 3, 3], ".png")),
                new KeptPicture("beach.gif", Stored([4, 4, 4], ".gif")),
            ],
        };

        var saved = _recovery.Save(entry);

        Assert.Equal([Copy("holiday.jpg"), Copy("beach.gif")], saved);
        Assert.Equal(["reveal " + Copy("holiday.jpg")], _launcher.Calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_clipboard_picture_under_a_name_the_history_does_not_give_a_picture_is_not_read_and_not_saved()
    {
        // The file is there, two folders up from the day's folder. A name in a record can come
        // from another machine, and it must not be able to point at it.
        File.WriteAllBytes(Path.Combine(_history, "x.png"), [9, 9, 9]);
        var entry = Entry() with { Image = @"..\..\x.png" };
        Assert.True(File.Exists(entry.PathOf(@"..\..\x.png")));

        var saved = _recovery.Save(entry);

        Assert.Empty(saved);
        Assert.Empty(Written());
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public void A_kept_picture_under_a_name_the_history_does_not_give_a_picture_is_not_read_and_not_saved()
    {
        File.WriteAllBytes(Path.Combine(_history, "x.png"), [9, 9, 9]);
        var entry = Entry() with { Pictures = [new KeptPicture("x.png", @"..\..\x.png")] };
        Assert.True(File.Exists(entry.PathOf(@"..\..\x.png")));

        var saved = _recovery.Save(entry);

        Assert.Empty(saved);
        Assert.Empty(Written());
        Assert.Empty(_launcher.Calls);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".rtf")]
    public void A_kept_picture_that_names_a_file_of_text_is_not_saved(string extension)
    {
        // The file is one of the history's own and is where the name says, but it is what was
        // copied as text. A record that calls it a picture must not get it out into Pictures.
        string text = Stored([(byte)'m', (byte)'a', (byte)'d', (byte)'e', (byte)' ', (byte)'u', (byte)'p'], extension);

        var saved = _recovery.Save(Entry() with { Pictures = [new KeptPicture("notes.jpg", text)] });

        Assert.Empty(saved);
        Assert.Empty(Written());
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_fails_the_saving_and_nothing_is_shown()
    {
        // A file stands where the folder for the copies has to be.
        File.WriteAllText(_recovered, "in the way");

        Assert.ThrowsAny<IOException>(() => _recovery.Save(Entry() with { Image = Stored([1, 2, 3], ClipPaths.ImageExtension) }));

        Assert.Empty(_launcher.Calls);
        Assert.Empty(_notes);
    }

    // The copies are on disk when Explorer is asked to show the first, so a failure to show it
    // is not a failure to save: the owner would be told nothing was saved, and the copies are there.

    /// <summary>A launcher whose Explorer cannot be started.</summary>
    private sealed class LauncherThatCannotReveal(Exception failure) : IProcessLauncher
    {
        public int Asked { get; private set; }

        public void LaunchApp(string appId) { }

        public void OpenPath(string path) { }

        public void RevealInFolder(string path)
        {
            Asked++;
            throw failure;
        }

        public void OpenLink(string link) { }

        public void EditFile(string path) { }
    }

    public static IEnumerable<object[]> FailuresToReveal() =>
    [
        [new InvalidOperationException("made-up words from the launcher")],
        [new System.ComponentModel.Win32Exception(2, "made-up words from Windows")],
        [new IOException("made-up words from the disk")],
        [new UnauthorizedAccessException("made-up words from the disk")],
        [new FileNotFoundException("made-up words from the disk")],
    ];

    [Theory]
    [MemberData(nameof(FailuresToReveal))]
    public void A_failure_to_show_the_saved_copy_in_explorer_does_not_make_the_save_fail(Exception failure)
    {
        var launcher = new LauncherThatCannotReveal(failure);
        var recovery = new ClipRecovery(() => _pictures, launcher, _notes.Add, Zone);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        var saved = recovery.Save(Entry() with { Image = Stored(png, ClipPaths.ImageExtension) });

        Assert.Equal(1, launcher.Asked);
        Assert.Equal([Copy(ClipboardCopy + ".png")], saved);
        Assert.Equal(png, File.ReadAllBytes(saved[0]));
        Assert.Equal(saved, Written());

        // Nothing is said to the owner: the copies are where they were meant to be.
        Assert.Empty(_notes);
    }

    [Fact]
    public void Every_picture_of_an_entry_is_saved_before_explorer_is_asked_and_a_failure_of_explorer_loses_none()
    {
        var launcher = new LauncherThatCannotReveal(new InvalidOperationException("made-up words from the launcher"));
        var recovery = new ClipRecovery(() => _pictures, launcher, _notes.Add, Zone);
        var entry = Entry() with
        {
            Image = Stored([1, 1, 1], ClipPaths.ImageExtension),
            Pictures = [new KeptPicture("holiday.jpg", Stored([2, 2, 2], ".jpg"))],
        };

        var saved = recovery.Save(entry);

        Assert.Equal([Copy(ClipboardCopy + ".png"), Copy("holiday.jpg")], saved);
        Assert.Equal(saved, Written());
    }

    // A Pictures folder that Windows could not name comes as blank text or as a path that is
    // not full, and Directory.CreateDirectory would make "Wyvern recovered" wherever Wyvern was
    // started from. Each of these entries has a picture that is not on this machine, so that a
    // refusal missing here would write nothing either, and the test would show it by the
    // exception that is not thrown and not by a folder that nobody wants.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pictures")]
    [InlineData(@"made-up\Pictures")]
    [InlineData(@"..\made-up\Pictures")]
    [InlineData(@"\made-up\Pictures")]
    [InlineData(@"C:made-up\Pictures")]
    public void A_pictures_folder_that_is_blank_or_not_a_full_path_is_refused_before_anything_is_written(string folder)
    {
        var recovery = new ClipRecovery(() => folder, _launcher, _notes.Add, Zone);

        var refused = Assert.Throws<IOException>(() => recovery.Save(Entry() with { Image = Gone([1, 2, 3], ClipPaths.ImageExtension) }));

        Assert.Equal(ClipRecovery.NoPicturesFolderMessage, refused.Message);
        Assert.Empty(Written());
        Assert.Empty(_launcher.Calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void The_words_that_refuse_a_pictures_folder_name_no_folder()
    {
        var recovery = new ClipRecovery(() => "made-up-folder-name", _launcher, _notes.Add, Zone);

        var refused = Assert.Throws<IOException>(() => recovery.Save(Entry() with { Image = Gone([1, 2, 3], ClipPaths.ImageExtension) }));

        Assert.DoesNotContain("made-up-folder-name", refused.Message);
    }

    [Fact]
    public void A_pictures_folder_that_is_not_asked_for_is_not_refused_for_an_entry_without_pictures()
    {
        var recovery = new ClipRecovery(() => "", _launcher, _notes.Add, Zone);

        Assert.Empty(recovery.Save(Entry() with { Text = "no picture here", TextLength = 15 }));
        Assert.Empty(_launcher.Calls);
    }

    // The name a picture is saved under comes from a record, which another machine may have
    // written. Whatever it holds, the copy is one file in the folder for copies, under a name
    // Windows can make, with the extension of the stored file.

    [Fact]
    public void A_name_of_five_thousand_characters_is_cut_to_the_longest_name_and_the_copy_is_saved()
    {
        string name = new string('a', 5000) + ".png";

        string saved = SavedAs(name);

        Assert.Equal(new string('a', ClipRecovery.MaxNameLength) + ".png", saved);
    }

    [Fact]
    public void A_character_of_two_positions_that_the_cut_would_fall_inside_is_dropped_whole()
    {
        // The emoji takes two positions, and only one is left at the cut.
        string name = new string('a', ClipRecovery.MaxNameLength - 1) + char.ConvertFromUtf32(0x1F600) + new string('b', 5000);

        string saved = SavedAs(name, ".jpg");

        Assert.Equal(new string('a', ClipRecovery.MaxNameLength - 1) + ".jpg", saved);
    }

    [Theory]
    [InlineData("x.png:stream", ".jpg", "x.jpg")]
    [InlineData("x.png:stream", ".png", "x.png")]
    [InlineData("x:stream.png", ".png", "x_stream.png")]
    [InlineData("x.png:stream:$DATA", ".gif", "x.gif")]
    public void The_name_of_an_alternate_data_stream_is_never_written_and_never_makes_a_stream(string name, string extension, string expected)
    {
        // A colon in a name written to NTFS makes a stream of a file. The copy is the only file
        // there is, in the folder for copies, and has no colon in its name.
        string saved = SavedAs(name, extension);

        Assert.Equal(expected, saved);
        Assert.DoesNotContain(':', saved);
    }

    [Theory]
    [InlineData("a.png.", ".png", "a.png.png")]
    [InlineData("a.", ".jpg", "a.jpg")]
    [InlineData("a..", ".jpg", "a_.jpg")]
    [InlineData("a. .", ".gif", "a_.gif")]
    [InlineData("a ", ".png", "a.png")]
    public void A_name_that_ends_in_a_dot_or_a_space_gets_the_extension_of_the_stored_file_and_ends_in_neither(string name, string extension, string expected)
    {
        string saved = SavedAs(name, extension);

        Assert.Equal(expected, saved);

        // Windows drops a dot or a space that ends a name, so a name that ended in one would be
        // another name than it was made as.
        string stem = saved[..^extension.Length];
        Assert.False(stem.EndsWith('.') || stem.EndsWith(' '));
    }

    [Theory]
    [InlineData("photo.jpg.lnk", ".jpg", "photo.jpg.jpg")]
    [InlineData("photo.jpg.lnk", ".png", "photo.jpg.png")]
    [InlineData("photo.png.exe", ".png", "photo.png.png")]
    [InlineData("photo.jpg.url", ".gif", "photo.jpg.gif")]
    [InlineData("photo.png.scr", ".bmp", "photo.png.bmp")]
    public void A_name_with_a_picture_extension_and_then_another_cannot_make_a_copy_that_is_a_shortcut_or_a_program(string name, string extension, string expected)
    {
        // What the name says its last extension is does not count: the copy has the extension of
        // the picture file that is stored, so it is never a .lnk, a .url or a program.
        string saved = SavedAs(name, extension);

        Assert.Equal(expected, saved);
        Assert.Equal(extension, Path.GetExtension(saved));
    }

    // The search for a name that is free stops at a thousand. Far more copies of one name than
    // anyone makes, and a folder in which every one is taken fails and writes nothing.
    [Fact]
    public void When_every_one_of_a_thousand_names_is_taken_the_save_fails_and_writes_nothing_more()
    {
        Directory.CreateDirectory(_recovered);
        File.WriteAllBytes(Copy("holiday.jpg"), [5]);
        for (int number = 2; number <= 1000; number++)
        {
            File.WriteAllBytes(Copy($"holiday-{number.ToString(CultureInfo.InvariantCulture)}.jpg"), [5]);
        }

        var entry = Entry() with { Pictures = [new KeptPicture("holiday.jpg", Stored([7, 7, 7], ".jpg"))] };

        var refused = Assert.Throws<IOException>(() => _recovery.Save(entry));

        Assert.Equal("Every name for the recovered picture is already taken.", refused.Message);
        Assert.Equal(1000, Directory.GetFiles(_recovered).Length);
        Assert.All(Directory.GetFiles(_recovered), file => Assert.Equal([5], File.ReadAllBytes(file)));
        Assert.Empty(_launcher.Calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void When_nine_hundred_and_ninety_nine_names_are_taken_the_thousandth_is_still_used()
    {
        Directory.CreateDirectory(_recovered);
        File.WriteAllBytes(Copy("holiday.jpg"), [5]);
        for (int number = 2; number <= 999; number++)
        {
            File.WriteAllBytes(Copy($"holiday-{number.ToString(CultureInfo.InvariantCulture)}.jpg"), [5]);
        }

        var saved = _recovery.Save(Entry() with { Pictures = [new KeptPicture("holiday.jpg", Stored([7, 7, 7], ".jpg"))] });

        Assert.Equal([Copy("holiday-1000.jpg")], saved);
        Assert.Equal([7, 7, 7], File.ReadAllBytes(saved[0]));
    }

    [Fact]
    public void A_copy_that_cannot_be_finished_leaves_no_half_picture_behind()
    {
        string image = Stored([1, 2, 3], ClipPaths.ImageExtension);

        // Another program has the picture's file open and shares it with nobody.
        using (new FileStream(Path.Combine(_day, image), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => _recovery.Save(Entry() with { Image = image }));
        }

        Assert.Empty(Written());
        Assert.Empty(_launcher.Calls);
    }
}
