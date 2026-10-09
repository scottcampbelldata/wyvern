using Beacon.Core.ClipboardHistory;
using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The text that stands in the pictures of the clipboard history: read once, kept on this machine, dropped with its entry, and what an entry is found by. The reader is a list the test fills: no picture is looked at.</summary>
public sealed class ClipPictureTextTests : IDisposable
{
    private static readonly string One = new string('1', 64) + ".png";
    private static readonly string Two = new string('2', 64) + ".png";
    private static readonly string Three = new string('3', 64) + ".jpg";

    private readonly TempDir _dir = new();
    private readonly string _file;
    private readonly Reader _reader = new();
    private readonly List<string> _reported = [];
    private int _changed;

    public ClipPictureTextTests() => _file = _dir.File("clipboard-picture-text.json");

    public void Dispose() => _dir.Dispose();

    private sealed class Reader : IPictureTextReader
    {
        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public bool CannotRead { get; set; }

        public Action<string>? Before { get; set; }

        public string? Read(string pictureFile)
        {
            lock (Asked)
            {
                Asked.Add(Path.GetFileName(pictureFile));
            }

            Before?.Invoke(Path.GetFileName(pictureFile));
            if (CannotRead)
            {
                return null;
            }

            return Texts.TryGetValue(Path.GetFileName(pictureFile), out string? text) ? text : throw new InvalidDataException("not a picture");
        }
    }

    private ClipPictureText Index() => new(_file, _reader, (words, _) =>
    {
        lock (_reported)
        {
            _reported.Add(words);
        }
    }, () => Interlocked.Increment(ref _changed));

    private ClipEntry Entry(string id, string? image = null, params string[] kept)
    {
        foreach (string name in kept.Append(image).OfType<string>())
        {
            File.WriteAllText(_dir.File(name), "made-up bytes");
        }

        return new ClipEntry(id, new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero), "OTTER-PC", new DateOnly(2026, 10, 7), _dir.Path)
        {
            Image = image,
            Pictures = kept.Select(name => new KeptPicture("shot " + name[0] + ".jpg", name)).ToList(),
        };
    }

    [Fact]
    public async Task Each_picture_is_read_once_in_the_background_and_its_text_is_there_to_be_found()
    {
        _reader.Texts[One] = "  Invoice 4471\r\nTotal due 120.00  ";
        _reader.Texts[Two] = "";
        var shot = Entry("a", One);
        var blank = Entry("b", Two);
        var text = Entry("c") with { Text = "no picture" };
        var index = Index();
        Assert.Null(index.Of(shot));

        index.Follow([shot, blank, text]);
        await index.Idle;

        Assert.Equal("Invoice 4471\r\nTotal due 120.00", index.Of(shot));
        Assert.Null(index.Of(blank));
        Assert.Null(index.Of(text));
        Assert.Equal(2, index.Count);
        Assert.Equal(1, _changed);
        Assert.Equal(["Text was looked for in 2 pictures of the clipboard history"], _reported);

        // Nothing is read a second time, whether it held text or not.
        index.Follow([shot, blank, text]);
        await index.Idle;
        Assert.Equal(2, _reader.Asked.Count);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public async Task An_entry_with_several_pictures_is_found_by_the_text_of_any_of_them()
    {
        _reader.Texts[One] = "first shot";
        _reader.Texts[Three] = "second shot";
        var entry = Entry("a", One, Three);
        var index = Index();

        index.Follow([entry]);
        await index.Idle;

        Assert.Equal("first shot\nsecond shot", index.Of(entry));
    }

    [Fact]
    public async Task What_was_read_is_kept_between_runs_and_nothing_is_read_again()
    {
        _reader.Texts[One] = "Invoice 4471";
        var shot = Entry("a", One);
        var first = Index();
        first.Follow([shot]);
        await first.Idle;

        var second = Index();

        Assert.Equal("Invoice 4471", second.Of(shot));
        second.Follow([shot]);
        await second.Idle;
        Assert.Single(_reader.Asked);
    }

    [Fact]
    public async Task The_text_of_a_picture_the_history_no_longer_has_is_dropped_at_once_from_memory_and_from_the_file()
    {
        _reader.Texts[One] = "Invoice 4471";
        _reader.Texts[Two] = "Meeting notes";
        var invoice = Entry("a", One);
        var notes = Entry("b", Two);
        var index = Index();
        index.Follow([invoice, notes]);
        await index.Idle;
        Assert.Contains("Invoice 4471", File.ReadAllText(_file));

        index.Follow([notes]);

        Assert.Null(index.Of(invoice));
        Assert.Equal("Meeting notes", index.Of(notes));
        Assert.DoesNotContain("Invoice", File.ReadAllText(_file));
        Assert.Contains("Meeting notes", File.ReadAllText(_file));

        // Cleared: nothing of any picture stays.
        index.Follow([]);
        Assert.Equal(0, index.Count);
        Assert.Equal("{}", File.ReadAllText(_file));
        Assert.Null(Index().Of(notes));
    }

    [Fact]
    public async Task An_entry_deleted_while_its_picture_is_being_read_leaves_no_text_behind()
    {
        _reader.Texts[One] = "Invoice 4471";
        var shot = Entry("a", One);
        var index = Index();
        _reader.Before = _ => index.Follow([]);

        index.Follow([shot]);
        await index.Idle;

        Assert.Null(index.Of(shot));
        Assert.Equal(0, index.Count);
        Assert.False(File.Exists(_file) && File.ReadAllText(_file).Contains("Invoice"));
    }

    [Fact]
    public async Task A_picture_that_cannot_be_read_or_whose_file_is_gone_holds_no_text_and_is_not_tried_again()
    {
        var broken = Entry("a", One);
        var gone = Entry("b", Two);
        File.Delete(_dir.File(Two));
        var index = Index();

        index.Follow([broken, gone]);
        await index.Idle;
        index.Follow([broken, gone]);
        await index.Idle;

        Assert.Null(index.Of(broken));
        Assert.Null(index.Of(gone));
        Assert.Equal([One], _reader.Asked);
        Assert.Equal(2, index.Count);
        Assert.Contains("A picture of the clipboard history could not be read for text", _reported);
    }

    [Fact]
    public async Task On_a_pc_without_a_text_reader_nothing_is_read_and_it_is_said_once()
    {
        _reader.CannotRead = true;
        var index = Index();

        index.Follow([Entry("a", One), Entry("b", Two)]);
        await index.Idle;
        index.Follow([Entry("a", One), Entry("b", Two), Entry("c", Three)]);
        await index.Idle;

        Assert.Single(_reader.Asked);
        Assert.Equal(0, index.Count);
        Assert.Equal(0, _changed);
        Assert.Equal(["Text in pictures is not read on this PC: Windows has no text reader for its language"], _reported);
    }

    [Fact]
    public async Task A_very_long_text_is_kept_up_to_its_bound_and_nothing_that_was_read_is_ever_reported()
    {
        _reader.Texts[One] = new string('w', ClipPictureText.MaxTextLength + 500);
        _reader.Texts[Two] = "Secret plan";
        var index = Index();
        var longOne = Entry("a", One);

        index.Follow([longOne, Entry("b", Two)]);
        await index.Idle;

        Assert.Equal(ClipPictureText.MaxTextLength, index.Of(longOne)!.Length);
        Assert.DoesNotContain(_reported, words => words.Contains("Secret", StringComparison.OrdinalIgnoreCase) || words.Contains("www", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public async Task A_file_that_cannot_be_read_costs_a_second_reading_and_nothing_more(string content)
    {
        File.WriteAllText(_file, content);
        _reader.Texts[One] = "Invoice 4471";
        var shot = Entry("a", One);
        var index = Index();

        index.Follow([shot]);
        await index.Idle;

        Assert.Equal("Invoice 4471", index.Of(shot));
        Assert.Equal("Invoice 4471", Index().Of(shot));
    }

    [Fact]
    public async Task A_picture_copied_while_others_are_being_read_is_read_as_well()
    {
        _reader.Texts[One] = "first";
        _reader.Texts[Two] = "second";
        var first = Entry("a", One);
        var second = Entry("b", Two);
        var index = Index();
        bool added = false;
        _reader.Before = _ =>
        {
            if (!added)
            {
                added = true;
                index.Follow([first, second]);
            }
        };

        index.Follow([first]);
        await index.Idle;

        Assert.Equal("first", index.Of(first));
        Assert.Equal("second", index.Of(second));
    }

    private ClipboardViewModel View(IReadOnlyList<ClipEntry> history, ClipPictureText index, ClipboardActions? actions = null) =>
        new(() => history, new FakeClock(), actions, TimeZoneInfo.Utc, pictureText: index.Of);

    [Fact]
    public async Task A_screenshot_is_found_in_the_history_by_a_word_that_stands_in_it()
    {
        _reader.Texts[One] = "Invoice 4471\nTotal   due 120.00";
        _reader.Texts[Two] = "Holiday photo";
        var history = new List<ClipEntry> { Entry("invoice", One), Entry("photo", Two), Entry("text") with { Text = "an invoice in words", TextLength = 19 } };
        var index = Index();
        index.Follow(history);
        await index.Idle;
        var view = View(history, index);
        view.Open();
        Assert.Equal(3, view.Rows.Count);

        view.Filter = "invoice";
        Assert.Equal(["invoice", "text"], view.Rows.Select(row => row.Entry.Id));

        // The words are found across a line break and a run of spaces, as in a text.
        view.Filter = "4471 total due";
        Assert.Equal(["invoice"], view.Rows.Select(row => row.Entry.Id));

        view.Filter = "HOLIDAY";
        Assert.Equal(["photo"], view.Rows.Select(row => row.Entry.Id));

        view.Filter = "heron";
        Assert.Empty(view.Rows);
    }

    [Fact]
    public async Task The_selected_picture_says_how_many_words_were_read_in_it_and_one_without_text_says_nothing()
    {
        _reader.Texts[One] = "Invoice 4471 total due";
        _reader.Texts[Two] = "";
        var history = new List<ClipEntry> { Entry("invoice", One), Entry("blank", Two) };
        var index = Index();
        index.Follow(history);
        await index.Idle;
        var view = View(history, index);
        view.Open();

        Assert.Contains(view.Info, line => line.Label == "Words in Picture" && line.Value == "4");
        Assert.Equal("Invoice 4471 total due", view.PictureText(history[0]));

        view.SelectedIndex = 1;
        Assert.DoesNotContain(view.Info, line => line.Label == "Words in Picture");
        Assert.Null(view.PictureText(history[1]));
    }

    [Fact]
    public async Task The_text_of_the_selected_picture_can_be_copied_and_the_window_hides_first()
    {
        _reader.Texts[One] = "Invoice 4471";
        _reader.Texts[Two] = "";
        var history = new List<ClipEntry> { Entry("invoice", One), Entry("blank", Two), Entry("text") with { Text = "words", TextLength = 5 } };
        var index = Index();
        index.Follow(history);
        await index.Idle;
        var done = new List<string>();
        var view = View(history, index, ClipboardActions.None with { CopyText = text => done.Add("copied " + text) });
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var launcher = new LauncherViewModel(new SearchEngine([], usage), usage, clipboard: view);
        launcher.Dismissed += (_, _) => done.Add("dismissed");

        // With the history closed nothing is copied.
        launcher.CopySelectedClipPictureText();
        Assert.Empty(done);

        launcher.OpenClipboard();
        launcher.CopySelectedClipPictureText();
        Assert.Equal(["dismissed", "copied Invoice 4471"], done);
        Assert.False(view.IsOpen);

        // A picture without text, and an entry that is no picture, have nothing to copy.
        done.Clear();
        launcher.OpenClipboard();
        launcher.MoveSelection(1);
        launcher.CopySelectedClipPictureText();
        launcher.MoveSelection(1);
        launcher.CopySelectedClipPictureText();
        Assert.Empty(done);
        Assert.True(view.IsOpen);
    }

    [Fact]
    public void Without_a_reader_of_pictures_the_history_is_found_by_what_it_was_found_by_before()
    {
        var history = new List<ClipEntry> { Entry("shot", One), Entry("text") with { Text = "an invoice", TextLength = 10 } };
        var view = new ClipboardViewModel(() => history, new FakeClock(), zone: TimeZoneInfo.Utc);
        view.Open();

        view.Filter = "invoice";

        Assert.Equal(["text"], view.Rows.Select(row => row.Entry.Id));
        Assert.Null(view.PictureText(history[0]));
    }
}
