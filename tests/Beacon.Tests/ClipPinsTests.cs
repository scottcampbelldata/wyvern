using Beacon.Core.ClipboardHistory;

namespace Beacon.Tests;

/// <summary>The pinned entries of the clipboard history: copies kept in a folder of their own until they are unpinned.</summary>
public sealed class ClipPinsTests : IDisposable
{
    private static readonly DateTimeOffset Copied = new(2026, 10, 3, 11, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 3);

    private readonly TempDir _dir = new();
    private readonly List<(string Words, Exception? Error)> _reported = [];
    private readonly string _day;
    private readonly string _pinsFolder;

    public ClipPinsTests()
    {
        _day = Path.Combine(_dir.Path, "clipboard", "OTTER-PC", "2026-10-03");
        Directory.CreateDirectory(_day);
        _pinsFolder = Path.Combine(_dir.Path, "clipboard-pins");
    }

    public void Dispose() => _dir.Dispose();

    private ClipPins Pins() => new(_pinsFolder, (words, error) => _reported.Add((words, error)));

    // The name of a file of content: sixty-four of one hexadecimal digit and an extension.
    private static string Blob(char digit, string extension) => new string(digit, 64) + extension;

    private ClipEntry Clip(string id) => new(id, Copied, "OTTER-PC", Day, _day);

    private ClipEntry TextClip(string id, string text) => Clip(id) with { Text = text, TextLength = text.Length, SourceApp = "notepad.exe" };

    // Writes a file of content into the day's folder and gives its name.
    private string Content(char digit, string extension, string holds)
    {
        string name = Blob(digit, extension);
        File.WriteAllText(Path.Combine(_day, name), holds);
        return name;
    }

    private string[] PinFiles() =>
        Directory.Exists(_pinsFolder) ? Directory.GetFiles(_pinsFolder).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray() : [];

    [Fact]
    public void Nothing_is_pinned_to_begin_with_and_no_folder_is_made_for_that()
    {
        var pins = Pins();

        Assert.Empty(pins.All);
        Assert.False(pins.IsPinned("a"));
        Assert.False(Directory.Exists(_pinsFolder));
        Assert.Empty(_reported);
    }

    [Fact]
    public void A_pinned_text_is_listed_and_is_still_there_when_the_list_is_read_afresh()
    {
        var pins = Pins();

        Assert.Equal(PinResult.Pinned, pins.Pin(TextClip("a", "harbour seal")));

        foreach (var read in new[] { pins, Pins() })
        {
            var pinned = Assert.Single(read.All);
            Assert.True(read.IsPinned("a"));
            Assert.Equal("a", pinned.Id);
            Assert.Equal("harbour seal", pinned.Text);
            Assert.Equal(12, pinned.TextLength);
            Assert.Equal(Copied, pinned.Time);
            Assert.Equal("OTTER-PC", pinned.Device);
            Assert.Equal(Day, pinned.Day);
            Assert.Equal("notepad.exe", pinned.SourceApp);

            // Its files are looked for in the pins' folder from now on.
            Assert.Equal(_pinsFolder, pinned.Folder);
        }

        Assert.Equal([ClipPins.ListName], PinFiles());
        Assert.Empty(_reported);
    }

    [Fact]
    public void The_files_that_hold_an_entry_s_content_are_copied_and_the_history_keeps_its_own()
    {
        string whole = Content('1', ClipPaths.TextExtension, "the whole of a long text");
        string html = Content('2', ClipPaths.HtmlExtension, "<b>seal</b>");
        string rtf = Content('3', ClipPaths.RtfExtension, "{\\rtf1 seal}");
        string image = Content('4', ClipPaths.ImageExtension, "picture bytes");
        string kept = Content('5', ".jpg", "kept picture bytes");
        var entry = Clip("a") with
        {
            Text = "the whole",
            TextLength = 24,
            TextFile = whole,
            Html = html,
            Rtf = rtf,
            Image = image,
            Files = [@"C:\harbour\seal.jpg"],
            Pictures = [new KeptPicture("seal.jpg", kept)],
        };

        Assert.Equal(PinResult.Pinned, Pins().Pin(entry));

        Assert.Equal(new[] { whole, html, rtf, image, kept, ClipPins.ListName }.Order(StringComparer.Ordinal), PinFiles());
        Assert.Equal("picture bytes", File.ReadAllText(Path.Combine(_pinsFolder, image)));
        Assert.Equal("kept picture bytes", File.ReadAllText(Path.Combine(_pinsFolder, kept)));
        Assert.Equal("picture bytes", File.ReadAllText(Path.Combine(_day, image)));

        var pinned = Assert.Single(Pins().All);
        Assert.Equal(whole, pinned.TextFile);
        Assert.Equal(html, pinned.Html);
        Assert.Equal(rtf, pinned.Rtf);
        Assert.Equal(image, pinned.Image);
        Assert.Equal([@"C:\harbour\seal.jpg"], pinned.Files);
        Assert.Equal([new KeptPicture("seal.jpg", kept)], pinned.Pictures);
        Assert.Equal(24, pinned.TextLength);
        Assert.Equal(Path.Combine(_pinsFolder, image), pinned.PathOf(pinned.Image!));
    }

    [Fact]
    public void A_pinned_copy_outlives_the_day_it_was_taken_from()
    {
        string image = Content('4', ClipPaths.ImageExtension, "picture bytes");
        Pins().Pin(Clip("a") with { Image = image });

        Directory.Delete(Path.Combine(_dir.Path, "clipboard"), recursive: true);

        var pinned = Assert.Single(Pins().All);
        Assert.Equal("picture bytes", File.ReadAllText(pinned.PathOf(pinned.Image!)));
    }

    [Fact]
    public void An_entry_is_pinned_once()
    {
        var pins = Pins();
        pins.Pin(TextClip("a", "seal"));

        Assert.Equal(PinResult.AlreadyPinned, pins.Pin(TextClip("a", "seal")));

        Assert.Single(pins.All);
        Assert.Single(File.ReadAllLines(Path.Combine(_pinsFolder, ClipPins.ListName)));
    }

    [Fact]
    public void The_entry_pinned_last_comes_first()
    {
        var pins = Pins();
        pins.Pin(TextClip("a", "one"));
        pins.Pin(TextClip("b", "two"));
        pins.Pin(TextClip("c", "three"));

        Assert.Equal(["c", "b", "a"], pins.All.Select(entry => entry.Id));
        Assert.Equal(["c", "b", "a"], Pins().All.Select(entry => entry.Id));
    }

    [Fact]
    public void An_entry_whose_content_has_gone_is_not_pinned_and_nothing_is_written()
    {
        string image = Content('4', ClipPaths.ImageExtension, "picture bytes");
        var entry = Clip("a") with { Image = image, Html = Blob('2', ClipPaths.HtmlExtension) };

        var pins = Pins();
        Assert.Equal(PinResult.ContentGone, pins.Pin(entry));

        Assert.Empty(pins.All);
        Assert.False(Directory.Exists(_pinsFolder));
    }

    [Fact]
    public void No_more_than_the_most_can_be_pinned()
    {
        var pins = Pins();
        for (int i = 0; i < ClipPins.MaxPins; i++)
        {
            Assert.Equal(PinResult.Pinned, pins.Pin(TextClip("id-" + i, "text " + i)));
        }

        Assert.Equal(PinResult.TooMany, pins.Pin(TextClip("one-more", "text")));

        Assert.Equal(ClipPins.MaxPins, pins.All.Count);
        Assert.False(pins.IsPinned("one-more"));
        Assert.Equal(ClipPins.MaxPins, Pins().All.Count);
    }

    [Fact]
    public void A_copy_that_fails_leaves_nothing_behind()
    {
        string image = Content('4', ClipPaths.ImageExtension, "picture bytes");
        string html = Content('9', ClipPaths.HtmlExtension, "<b>seal</b>");
        var entry = Clip("a") with { Image = image, Html = html };
        var pins = Pins();

        // Another program has one of the files to itself.
        using (new FileStream(Path.Combine(_day, image), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => pins.Pin(entry));
        }

        Assert.Empty(pins.All);
        Assert.Empty(PinFiles());

        // And once the file is free, the entry is pinned.
        Assert.Equal(PinResult.Pinned, pins.Pin(entry));
        Assert.Equal(new[] { image, html, ClipPins.ListName }.Order(StringComparer.Ordinal), PinFiles());
    }

    [Fact]
    public void Unpinning_takes_the_entry_out_and_removes_the_files_no_other_pin_names()
    {
        string shared = Content('4', ClipPaths.ImageExtension, "picture bytes");
        string own = Content('2', ClipPaths.HtmlExtension, "<b>seal</b>");
        var pins = Pins();
        pins.Pin(Clip("a") with { Image = shared, Html = own });
        pins.Pin(Clip("b") with { Image = shared });

        Assert.True(pins.Unpin("a"));

        Assert.Equal(["b"], pins.All.Select(entry => entry.Id));
        Assert.Equal(["b"], Pins().All.Select(entry => entry.Id));
        Assert.Equal(new[] { shared, ClipPins.ListName }.Order(StringComparer.Ordinal), PinFiles());

        Assert.True(pins.Unpin("b"));

        Assert.Empty(pins.All);
        Assert.Equal([ClipPins.ListName], PinFiles());
        Assert.Empty(Pins().All);
        Assert.Empty(_reported);
    }

    [Fact]
    public void Unpinning_what_is_not_pinned_changes_nothing()
    {
        var pins = Pins();
        pins.Pin(TextClip("a", "seal"));

        Assert.False(pins.Unpin("b"));

        Assert.Equal(["a"], pins.All.Select(entry => entry.Id));
    }

    [Fact]
    public void A_file_of_an_unpinned_entry_that_cannot_be_removed_is_reported_in_fixed_words_and_the_entry_is_unpinned_all_the_same()
    {
        string image = Content('4', ClipPaths.ImageExtension, "picture bytes");
        var pins = Pins();
        pins.Pin(Clip("a") with { Image = image });

        using (new FileStream(Path.Combine(_pinsFolder, image), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(pins.Unpin("a"));
        }

        Assert.Empty(pins.All);
        var (words, error) = Assert.Single(_reported);
        Assert.Equal("A file of an unpinned clipboard entry could not be removed", words);
        Assert.Null(error);
    }

    [Fact]
    public void Lines_that_are_not_understood_are_left_out_and_counted_and_the_rest_is_read()
    {
        var pins = Pins();
        pins.Pin(TextClip("a", "one"));
        pins.Pin(TextClip("b", "two"));
        string list = Path.Combine(_pinsFolder, ClipPins.ListName);
        string[] good = File.ReadAllLines(list);
        string outside = "{\"device\":\"OTTER-PC\",\"entry\":{\"id\":\"x\",\"ts\":\"2026-10-03T11:30:00Z\",\"kind\":\"image\",\"image\":\"..\\\\..\\\\settings.png\"}}";
        File.WriteAllLines(
            list,
            [
                good[0],
                "not json",
                "[1, 2]",
                "{\"device\":7,\"entry\":{}}",
                "{\"device\":\"OTTER-PC\"}",
                "{\"device\":\"OTTER-PC\",\"entry\":{\"id\":\"d\",\"ts\":\"2026-10-03T11:30:00Z\",\"kind\":\"delete\",\"target\":\"a\"}}",
                outside,
                "",
                good[1],

                // The same entry a second time.
                good[1],
            ]);

        var read = Pins();

        Assert.Equal(["b", "a"], read.All.Select(entry => entry.Id));
        var (words, error) = Assert.Single(_reported);
        Assert.Equal("Lines of the list of pinned clipboard entries were not understood and are left out: 7", words);
        Assert.Null(error);
    }

    [Fact]
    public void A_line_whose_device_name_is_too_long_is_left_out()
    {
        Directory.CreateDirectory(_pinsFolder);
        string device = new('n', ClipPins.MaxDeviceLength + 1);
        File.WriteAllText(
            Path.Combine(_pinsFolder, ClipPins.ListName),
            "{\"device\":\"" + device + "\",\"entry\":{\"id\":\"a\",\"ts\":\"2026-10-03T11:30:00Z\",\"kind\":\"text\",\"text\":\"seal\"}}\n");

        Assert.Empty(Pins().All);
    }

    [Fact]
    public void What_is_pinned_after_lines_were_left_out_is_written_with_what_was_read()
    {
        var pins = Pins();
        pins.Pin(TextClip("a", "one"));
        string list = Path.Combine(_pinsFolder, ClipPins.ListName);
        File.AppendAllText(list, "not json\n");

        var read = Pins();
        read.Pin(TextClip("b", "two"));

        Assert.Equal(2, File.ReadAllLines(list).Length);
        Assert.Equal(["b", "a"], Pins().All.Select(entry => entry.Id));
    }

    [Fact]
    public void Text_of_any_kind_comes_back_as_it_was()
    {
        string text = "line one\r\n\ttabbed \"quoted\" back\\slash " + char.ConvertFromUtf32(0x1F9AD) + " <tag> & more";
        Pins().Pin(TextClip("a", text) with { SourceApp = null });

        var pinned = Assert.Single(Pins().All);
        Assert.Equal(text, pinned.Text);
        Assert.Null(pinned.SourceApp);
    }

    [Fact]
    public void A_copied_address_is_still_a_link_when_it_is_read_afresh()
    {
        Pins().Pin(TextClip("a", "https://example.com/seals") with { Link = "https://example.com/seals", Label = "Seals" });

        var pinned = Assert.Single(Pins().All);
        Assert.Equal("https://example.com/seals", pinned.Link);
        Assert.Equal("Seals", pinned.Label);
    }
}
