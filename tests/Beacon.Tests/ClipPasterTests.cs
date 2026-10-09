using System.Collections.Concurrent;
using System.Text;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Pasting an entry of the clipboard history. The entry's files are a few made-up bytes in a
/// temporary folder, and the paste service only writes down what it was handed: no test here
/// touches the real clipboard or presses a key.
/// </summary>
public sealed class ClipPasterTests : IDisposable
{
    /// <summary>Writes down what it was asked to paste, and pastes nothing.</summary>
    private sealed class RecordingPaste : IPasteService
    {
        /// <summary>Every text that was to be pasted and then taken off the clipboard again.</summary>
        public List<string> Texts { get; } = [];

        /// <summary>Every list of blocks that was to be pasted and kept on the clipboard.</summary>
        public List<IReadOnlyList<ClipboardBlock>> Kept { get; } = [];

        /// <summary>When set, pasting blocks throws it.</summary>
        public Exception? Fails { get; set; }

        public void Paste(string text) => Texts.Add(text);

        public void PasteAndKeep(IReadOnlyList<ClipboardBlock> blocks)
        {
            if (Fails is not null)
            {
                throw Fails;
            }

            Kept.Add(blocks);
        }
    }

    /// <summary>A codec that cannot read one picture, and reads every other as <see cref="FakeImageCodec"/> does.</summary>
    private sealed class CodecThatCannotRead(byte[] damaged) : IImageCodec
    {
        public byte[] BmpToPng(byte[] bmp) => FakeImageCodec.PngOf(bmp);

        public byte[] ToDib(byte[] picture) =>
            picture.AsSpan().SequenceEqual(damaged)
                ? throw new InvalidOperationException("made-up words from the picture code")
                : FakeImageCodec.DibOf(picture);
    }

    private static readonly DateTimeOffset Time = new(2026, 10, 3, 12, 34, 56, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 3);

    // A letter that is two bytes as UTF-8 and one in the older encodings, so that a file read
    // as anything but UTF-8 shows. Written as a number so that no tool can change it.
    private const char EAcute = (char)0xE9;

    // Paths that are on no disk. They are only ever given to a paster that asks the test whether
    // a file is there (PasterAsking), so they never reach the file system.
    private const string MadeUpReport = @"C:\made-up\report.docx";
    private const string MadeUpNotes = @"D:\made-up\notes.txt";
    private const string MadeUpFolder = @"C:\made-up\holiday photos";

    // Longer than anything here takes, so that a test that goes wrong fails and does not hang.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // The limit for a check of the files that is meant never to answer in time.
    private static readonly TimeSpan NoTime = TimeSpan.FromMilliseconds(20);

    private readonly TempDir _dir = new();
    private readonly string _history;
    private readonly string _day;
    private readonly RecordingPaste _paste = new();
    private readonly FakeImageCodec _codec = new();
    private readonly List<string> _notes = [];
    private readonly List<(string Message, Exception? Error)> _reports = [];
    private readonly ClipPaster _paster;

    public ClipPasterTests()
    {
        _history = _dir.File("clipboard");
        _day = Path.Combine(_history, "OTTER-PC", "2026-10-03");
        Directory.CreateDirectory(_day);

        // The check of the files is given as long as anything here takes, so that no test
        // depends on a pool thread answering within the 1.5 seconds the app allows. The tests
        // that are about giving up on the check pass a short limit of their own.
        _paster = new ClipPaster(_paste, _codec, _notes.Add, (message, error) => _reports.Add((message, error)), fileCheckLimit: Limit);
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

    /// <summary>
    /// Writes a content file under the name the history gave it when it held other bytes: the
    /// file is there, and what is in it is not what was stored.
    /// </summary>
    /// <returns>That name.</returns>
    private string StoredAs(byte[] named, byte[] holds, string extension)
    {
        string name = ClipPaths.BlobName(named, extension);
        File.WriteAllBytes(Path.Combine(_day, name), holds);
        return name;
    }

    /// <summary>The name of a content file that is not there: the record names it, and the file has gone.</summary>
    private static string Gone(byte[] content, string extension) => ClipPaths.BlobName(content, extension);

    /// <summary>A file with made-up content, outside the history.</summary>
    /// <returns>Its full path.</returns>
    private string OwnersFile(string name)
    {
        string path = _dir.File(name);
        File.WriteAllText(path, "made up");
        return path;
    }

    /// <summary>Pastes the entry and gives the blocks that went to the paste service.</summary>
    private IReadOnlyList<ClipboardBlock> Pasted(ClipEntry entry) => Pasted(_paster, entry);

    private IReadOnlyList<ClipboardBlock> Pasted(ClipPaster paster, ClipEntry entry)
    {
        paster.Paste(entry);
        return Assert.Single(_paste.Kept);
    }

    /// <summary>
    /// A paster that asks the test whether one of the owner's files is there, where the real one
    /// asks the disk. A made-up path given to it never reaches the file system.
    /// </summary>
    /// <param name="limit">How long the whole check may take. Left out, it is longer than any check here takes.</param>
    private ClipPaster PasterAsking(Func<string, bool> exists, TimeSpan? limit = null) =>
        new(_paste, _codec, _notes.Add, (message, error) => _reports.Add((message, error)), exists, limit ?? Limit);

    /// <summary>
    /// A paster whose question about a file is not answered until <paramref name="release"/> is
    /// set, as with a path on a share that cannot be reached, and that waits no longer than
    /// <see cref="NoTime"/> for it. The answer, when it is let through, is that the file is
    /// there. A test sets the event before it ends so that no thread is left waiting; the
    /// event is not disposed, because the check may still be coming out of its wait then.
    /// </summary>
    private ClipPaster PasterLeftWaiting(ManualResetEventSlim release) =>
        PasterAsking(
            _ =>
            {
                release.Wait(Limit);
                return true;
            },
            NoTime);

    private static ClipboardFormat[] Formats(IReadOnlyList<ClipboardBlock> blocks) =>
        [.. blocks.Select(block => block.Format)];

    private static void AssertBlock(ClipboardFormat format, byte[] bytes, ClipboardBlock block)
    {
        Assert.Equal(format, block.Format);
        Assert.Equal(bytes, block.Bytes);
    }

    [Fact]
    public void A_short_text_goes_on_the_clipboard_as_one_block_of_text()
    {
        var blocks = Pasted(Entry() with { Text = "a short note", TextLength = 12 });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("a short note"), Assert.Single(blocks));
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void The_entry_is_pasted_to_stay_on_the_clipboard()
    {
        _paster.Paste(Entry() with { Text = "a short note", TextLength = 12 });

        // The other way of pasting would put back what the clipboard held before.
        Assert.Single(_paste.Kept);
        Assert.Empty(_paste.Texts);
    }

    [Fact]
    public void Asking_what_would_go_on_the_clipboard_pastes_nothing()
    {
        var blocks = _paster.Blocks(Entry() with { Text = "a short note", TextLength = 12 });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("a short note"), Assert.Single(blocks));
        Assert.Empty(_paste.Kept);
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_long_text_is_read_whole_from_its_file()
    {
        const string start = "Dear Otter, the start of a long letter";
        string whole = start + ", and the rest of it, sent from a caf" + EAcute + " by the river.";
        string file = Stored(Encoding.UTF8.GetBytes(whole), ClipPaths.TextExtension);

        var blocks = Pasted(Entry() with { Text = start, TextLength = whole.Length, TextFile = file });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(whole), Assert.Single(blocks));
    }

    [Fact]
    public void A_long_text_whose_file_has_gone_is_pasted_as_the_start_that_is_in_memory()
    {
        const string start = "Dear Otter, the start of a long letter";
        string whole = start + ", and the rest of it.";
        string file = Gone(Encoding.UTF8.GetBytes(whole), ClipPaths.TextExtension);

        var blocks = Pasted(Entry() with { Text = start, TextLength = whole.Length, TextFile = file });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(start), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_long_text_whose_file_is_empty_is_pasted_as_the_start_that_is_in_memory()
    {
        const string start = "Dear Otter, the start of a long letter";
        string whole = start + ", and the rest of it.";
        string file = StoredAs(Encoding.UTF8.GetBytes(whole), [], ClipPaths.TextExtension);

        var blocks = Pasted(Entry() with { Text = start, TextLength = whole.Length, TextFile = file });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(start), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_long_text_whose_file_holds_less_than_the_start_that_is_in_memory_is_pasted_as_that_start()
    {
        const string start = "Dear Otter, the start of a long letter";
        string whole = start + ", and the rest of it.";
        string file = StoredAs(Encoding.UTF8.GetBytes(whole), Encoding.UTF8.GetBytes("Dear Otter"), ClipPaths.TextExtension);

        var blocks = Pasted(Entry() with { Text = start, TextLength = whole.Length, TextFile = file });

        // The file was cut short. What is in memory is more of the text than the file has.
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(start), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_text_file_is_measured_in_characters_and_not_in_bytes_against_the_start_that_is_in_memory()
    {
        // Four letters of two bytes each: eight bytes, and still shorter than five characters.
        const string start = "abcde";
        string inFile = new(EAcute, 4);
        string file = StoredAs(Encoding.UTF8.GetBytes("abcdef"), Encoding.UTF8.GetBytes(inFile), ClipPaths.TextExtension);

        var blocks = Pasted(Entry() with { Text = start, TextLength = 6, TextFile = file });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(start), Assert.Single(blocks));
    }

    [Fact]
    public void A_text_file_that_holds_exactly_as_much_as_the_start_that_is_in_memory_is_the_one_that_is_pasted()
    {
        // Told apart by their capitals. A file that is as long as the start is a whole file.
        const string start = "dear otter";
        string file = Stored(Encoding.UTF8.GetBytes("DEAR OTTER"), ClipPaths.TextExtension);

        var blocks = Pasted(Entry() with { Text = start, TextLength = start.Length, TextFile = file });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("DEAR OTTER"), Assert.Single(blocks));
    }

    [Fact]
    public void An_empty_html_file_is_left_out_like_one_that_has_gone()
    {
        string html = StoredAs(Encoding.UTF8.GetBytes("<b>made up</b>"), [], ClipPaths.HtmlExtension);

        var blocks = Pasted(Entry() with { Text = "made up", TextLength = 7, Html = html });

        // No block of one zero byte in front of the text, for a program to take as the richest form.
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("made up"), Assert.Single(blocks));
    }

    [Fact]
    public void An_empty_rich_text_file_is_left_out_like_one_that_has_gone()
    {
        string rtf = StoredAs(Encoding.ASCII.GetBytes(@"{\rtf1 made up}"), [], ClipPaths.RtfExtension);

        var blocks = Pasted(Entry() with { Text = "made up", TextLength = 7, Rtf = rtf });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("made up"), Assert.Single(blocks));
    }

    [Fact]
    public void An_empty_picture_file_is_left_out_and_is_not_handed_to_the_codec()
    {
        string image = StoredAs([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], [], ClipPaths.ImageExtension);

        var blocks = Pasted(Entry() with { Text = "the caption", TextLength = 11, Image = image });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the caption"), Assert.Single(blocks));
        Assert.Empty(_codec.Pictures);
        Assert.Empty(_reports);
    }

    [Fact]
    public void An_entry_whose_only_content_file_is_empty_pastes_nothing_and_the_owner_is_told()
    {
        _paster.Paste(Entry() with { Html = StoredAs(Encoding.UTF8.GetBytes("<b>made up</b>"), [], ClipPaths.HtmlExtension) });

        Assert.Empty(_paste.Kept);
        Assert.Equal([ClipPaster.NothingLeftNote], _notes);
    }

    [Fact]
    public void Html_goes_on_the_clipboard_as_the_bytes_of_its_file_and_one_zero_byte()
    {
        byte[] html = Encoding.UTF8.GetBytes("Version:0.9\r\nStartHTML:0000000105\r\n<html><body><b>made up</b></body></html>");

        var blocks = Pasted(Entry() with { Html = Stored(html, ClipPaths.HtmlExtension) });

        AssertBlock(ClipboardFormat.Html, [.. html, 0], Assert.Single(blocks));
    }

    [Fact]
    public void Rich_text_goes_on_the_clipboard_as_the_bytes_of_its_file_and_one_zero_byte()
    {
        byte[] rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b made up}}");

        var blocks = Pasted(Entry() with { Rtf = Stored(rtf, ClipPaths.RtfExtension) });

        AssertBlock(ClipboardFormat.Rtf, [.. rtf, 0], Assert.Single(blocks));
    }

    [Fact]
    public void A_picture_goes_on_the_clipboard_as_png_and_as_the_plain_picture()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

        var blocks = Pasted(Entry() with { Image = Stored(png, ClipPaths.ImageExtension) });

        // The PNG is the file as it is; the plain picture is what the codec made of that file.
        Assert.Equal([ClipboardFormat.Png, ClipboardFormat.Dib], Formats(blocks));
        Assert.Equal(png, blocks[0].Bytes);
        Assert.Equal(FakeImageCodec.DibOf(png), blocks[1].Bytes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void Every_form_goes_on_the_clipboard_together_the_richest_first()
    {
        var entry = Entry() with
        {
            Text = "made up",
            TextLength = 7,
            Files = [OwnersFile("report.docx")],
            Image = Stored([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], ClipPaths.ImageExtension),
            Html = Stored(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension),
            Rtf = Stored(Encoding.ASCII.GetBytes(@"{\rtf1 made up}"), ClipPaths.RtfExtension),
        };

        var blocks = Pasted(entry);

        Assert.Equal(
            [
                ClipboardFormat.Rtf,
                ClipboardFormat.Html,
                ClipboardFormat.Png,
                ClipboardFormat.Dib,
                ClipboardFormat.FileDrop,
                ClipboardFormat.DropEffect,
                ClipboardFormat.UnicodeText,
            ],
            Formats(blocks));
    }

    [Fact]
    public void A_file_list_holds_only_the_paths_that_still_exist()
    {
        string report = OwnersFile("report.docx");
        string deleted = OwnersFile("deleted.txt");
        string folder = _dir.File("holiday photos");
        Directory.CreateDirectory(folder);
        File.Delete(deleted);

        var blocks = Pasted(Entry() with { Files = [report, deleted, folder] });

        Assert.Equal([ClipboardFormat.FileDrop, ClipboardFormat.DropEffect], Formats(blocks));
        Assert.Equal([report, folder], ClipboardBytes.FileList(blocks[0].Bytes));
    }

    [Fact]
    public void A_file_list_is_followed_by_the_mark_that_says_the_files_are_to_be_copied()
    {
        var blocks = Pasted(Entry() with { Files = [OwnersFile("report.docx")] });

        Assert.Equal(2, blocks.Count);
        AssertBlock(ClipboardFormat.DropEffect, ClipboardBytes.CopyEffect(), blocks[1]);
    }

    [Fact]
    public void With_nothing_said_the_check_of_the_files_may_take_a_second_and_a_half()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), ClipPaster.FileCheckLimit);
    }

    [Fact]
    public void Each_file_is_asked_about_once_and_the_list_holds_those_that_are_there()
    {
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return path != MadeUpNotes;
        });

        var blocks = Pasted(paster, Entry() with { Files = [MadeUpReport, MadeUpNotes, MadeUpFolder] });

        Assert.Equal([ClipboardFormat.FileDrop, ClipboardFormat.DropEffect], Formats(blocks));
        Assert.Equal(ClipboardBytes.DropFiles([MadeUpReport, MadeUpFolder]), blocks[0].Bytes);
        Assert.Equal([MadeUpReport, MadeUpNotes, MadeUpFolder], asked);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Fact]
    public void The_files_are_asked_about_on_another_thread_than_the_one_that_pastes()
    {
        int pasting = Environment.CurrentManagedThreadId;
        var threads = new ConcurrentQueue<int>();
        var paster = PasterAsking(_ =>
        {
            threads.Enqueue(Environment.CurrentManagedThreadId);
            return true;
        });

        paster.Paste(Entry() with { Files = [MadeUpReport, MadeUpNotes] });

        // A share that cannot be reached keeps whoever asks waiting, and that must not be the
        // thread the launcher's window lives on.
        Assert.Equal(2, threads.Count);
        Assert.DoesNotContain(pasting, threads);
    }

    [Fact]
    public void A_file_that_cannot_be_asked_about_is_left_out_and_the_others_are_listed()
    {
        var paster = PasterAsking(path =>
            path == MadeUpNotes ? throw new InvalidOperationException("made-up words from the check") : true);

        var blocks = Pasted(paster, Entry() with { Files = [MadeUpReport, MadeUpNotes, MadeUpFolder] });

        Assert.Equal(ClipboardBytes.DropFiles([MadeUpReport, MadeUpFolder]), blocks[0].Bytes);
        Assert.Empty(_notes);
    }

    [Fact]
    public void When_the_files_do_not_answer_in_time_the_text_of_the_entry_is_pasted()
    {
        var release = new ManualResetEventSlim();
        var paster = PasterLeftWaiting(release);

        try
        {
            var entry = Entry() with
            {
                Text = "the names of two files",
                TextLength = 22,
                Files = [MadeUpReport, MadeUpNotes],
            };

            // The paste comes back although nobody has answered: nothing lets the check go on
            // until the lines below have run.
            var blocks = Pasted(paster, entry);

            AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the names of two files"), Assert.Single(blocks));
            Assert.Empty(_notes);
            Assert.Empty(_reports);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void When_the_files_do_not_answer_in_time_and_there_is_nothing_else_the_owner_is_told_so_and_not_that_they_are_gone()
    {
        var release = new ManualResetEventSlim();
        var paster = PasterLeftWaiting(release);

        try
        {
            paster.Paste(Entry() with { Files = [MadeUpReport, MadeUpNotes] });

            // They may be only slow: the owner is not told they are gone.
            Assert.Empty(_paste.Kept);
            Assert.Equal([ClipPaster.FilesNotCheckedNote], _notes);
            Assert.NotEqual(ClipPaster.FilesGoneNote, ClipPaster.FilesNotCheckedNote);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void The_note_for_files_that_could_not_be_checked_in_time_is_this_sentence()
    {
        Assert.Equal("The files of this entry could not be checked in time, so they were left out.", ClipPaster.FilesNotCheckedNote);
    }

    [Fact]
    public void Files_that_do_not_answer_in_time_are_not_told_as_gone_when_the_other_forms_of_the_entry_cannot_be_pasted_either()
    {
        var release = new ManualResetEventSlim();
        var paster = PasterLeftWaiting(release);

        try
        {
            // The entry's own picture has gone, and no kept picture is there to take its place.
            paster.Paste(Entry() with
            {
                Image = Gone([1, 2, 3], ClipPaths.ImageExtension),
                Files = [MadeUpReport],
            });

            Assert.Empty(_paste.Kept);
            Assert.Equal([ClipPaster.FilesNotCheckedNote], _notes);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void An_entry_that_is_nothing_but_files_the_check_of_which_answered_and_found_none_is_still_told_as_gone()
    {
        var paster = PasterAsking(_ => false);

        paster.Paste(Entry() with { Files = [MadeUpReport, MadeUpNotes] });

        Assert.Empty(_paste.Kept);
        Assert.Equal([ClipPaster.FilesGoneNote], _notes);
    }

    [Fact]
    public void Nothing_that_can_be_pasted_is_told_as_a_check_that_ran_out_of_time_when_it_did_not_ask_about_a_file()
    {
        // No path of the entry is one that is asked about, so there was no check to run out of time.
        var release = new ManualResetEventSlim();
        var paster = PasterLeftWaiting(release);

        try
        {
            paster.Paste(Entry() with { Files = [@"\\.\pipe\x.png", "notes.txt"] });

            Assert.Equal([ClipPaster.FilesGoneNote], _notes);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void When_the_files_do_not_answer_in_time_a_kept_picture_is_pasted_as_a_picture()
    {
        byte[] kept = [2, 2, 2];
        var release = new ManualResetEventSlim();
        var paster = PasterLeftWaiting(release);

        try
        {
            var entry = Entry() with
            {
                Files = [@"C:\made-up\photo.png"],
                Pictures = [new KeptPicture("photo.png", Stored(kept, ".png"))],
            };

            var blocks = Pasted(paster, entry);

            AssertBlock(ClipboardFormat.Dib, FakeImageCodec.DibOf(kept), Assert.Single(blocks));
            Assert.Empty(_notes);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_check_that_was_given_up_on_asks_about_no_more_files_and_its_late_answer_changes_nothing()
    {
        var release = new ManualResetEventSlim();
        var inTheAir = new ManualResetEventSlim();
        var asked = new ConcurrentQueue<string>();
        Task? check = null;
        var paster = new ClipPaster(
            _paste,
            _codec,
            _notes.Add,
            (message, error) => _reports.Add((message, error)),
            path =>
            {
                asked.Enqueue(path);

                // Only the first question is held, as a path on a share that cannot be reached
                // holds it. A check that went on once it had its answer would ask the others.
                if (asked.Count == 1)
                {
                    inTheAir.Set();
                    release.Wait(Limit);
                }

                return true;
            },
            NoTime,
            task =>
            {
                // Called on the thread that pastes, with the check started and before the paste
                // begins to count its limit. The paste is held here until the first question is
                // in the air, which is what the check is waited for to have started. So the
                // limit that follows can only run out on a question that is being held, however
                // long a pool thread took to start: no time that passes decides this test.
                check = task;
                inTheAir.Wait(Limit);
            });

        IReadOnlyList<ClipboardBlock> blocks;
        try
        {
            var entry = Entry() with
            {
                Text = "the names of three files",
                TextLength = 24,
                Files = [MadeUpReport, MadeUpNotes, MadeUpFolder],
            };
            blocks = Pasted(paster, entry);
        }
        finally
        {
            release.Set();
        }

        // The first file is now said to be there, too late. The check is waited for until it
        // has ended, and only then are the questions counted: there is no wait that has to
        // run out for nothing to have happened. It has asked the one question it was in the
        // middle of, and none after it.
        Assert.True(inTheAir.IsSet, "Timed out waiting for the check to ask its first question");
        Assert.NotNull(check);
        await check.WaitAsync(Limit);
        Assert.Equal([MadeUpReport], asked);
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the names of three files"), Assert.Single(blocks));
        Assert.Same(blocks, Assert.Single(_paste.Kept));
        Assert.Empty(_notes);
        Assert.Empty(_reports);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData(@"made-up\notes.txt")]
    [InlineData(@".\notes.txt")]
    [InlineData(@"..\made-up\notes.txt")]
    [InlineData(@"\made-up\notes.txt")]
    [InlineData("/made-up/notes.txt")]
    [InlineData("C:notes.txt")]
    public void A_relative_path_is_left_out_of_the_file_list_and_never_asked_about(string relative)
    {
        // A relative path means a different file depending on where the program happens to be
        // running. The check here says that whatever it is asked about is there, so nothing but
        // the form of the path can keep it out of the list, wherever the tests run from and
        // whichever drive the temporary folder is on.
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return true;
        });

        var blocks = Pasted(paster, Entry() with { Files = [relative, MadeUpReport] });

        // The block is compared byte for byte: read back as a file list it would lose a path
        // that is not an ordinary one, whether or not the paster had left it out.
        Assert.Equal(ClipboardBytes.DropFiles([MadeUpReport]), blocks[0].Bytes);
        Assert.Equal([MadeUpReport], asked);
    }

    [Theory]
    [InlineData(@"\\.\pipe\x.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\x.png")]
    [InlineData(@"\\?\C:\x.png")]
    [InlineData(@"\\?\UNC\server\share\x.png")]
    [InlineData(@"\\.")]
    [InlineData(@"\\?")]
    [InlineData(@"//./pipe/x.png")]
    [InlineData(@"//?/C:/x.png")]
    [InlineData(@"\\./pipe\made-up.png")]
    [InlineData(@"\\?/C:\made-up\a.png")]
    [InlineData(@"\\?/GLOBALROOT\Device\made-up.png")]
    [InlineData(@"\\./PhysicalDrive0\made-up.png")]
    [InlineData(@"\/.\pipe\x.png")]
    [InlineData(@"/\?\C:\x.png")]
    [InlineData(@"//?/UNC/server/share/x.png")]
    [InlineData(@"\\.//pipe\x.png")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"\\server/")]
    [InlineData(@"\\server\/x.png")]
    [InlineData(@"//server//x.png")]
    [InlineData(@"\\\share\x.png")]
    [InlineData(@"///share/x.png")]
    [InlineData(@"1:\x.png")]
    [InlineData(@"C:")]
    [InlineData(@"C:x.png")]
    [InlineData(@"CC:\x.png")]
    [InlineData(@"C|\x.png")]
    [InlineData(@":\x.png")]
    public void A_device_path_an_extended_path_and_any_other_form_is_never_asked_about_and_never_listed(string name)
    {
        // The forms that capture leaves out of a file list. A record can come from another
        // machine, or from anything that can write a file, and name one all the same.
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return true;
        });

        var blocks = Pasted(paster, Entry() with { Files = [@"C:\one.txt", name, @"D:\three.txt"] });

        // Not even asked about: the question alone makes Windows go and look for the thing.
        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], asked);
        Assert.Equal(ClipboardBytes.DropFiles([@"C:\one.txt", @"D:\three.txt"]), blocks[0].Bytes);
    }

    [Theory]
    [InlineData(@"C:\x.png")]
    [InlineData(@"c:\x.png")]
    [InlineData(@"Z:\")]
    [InlineData("C:/x.png")]
    [InlineData(@"\\server\share\x.png")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\10.0.0.5\share\folder\x.png")]
    [InlineData(@"\\server.example.org\share$\x.png")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData(@"//server/share/a.png")]
    [InlineData(@"\\server/share\a.png")]
    [InlineData(@"/\server\share\a.png")]
    public void A_drive_path_and_a_share_path_are_asked_about_and_listed(string name)
    {
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return true;
        });

        var blocks = Pasted(paster, Entry() with { Files = [name] });

        Assert.Equal([name], asked);
        Assert.Equal(ClipboardBytes.DropFiles([name]), blocks[0].Bytes);
    }

    // The names capture never opens, because they stand for a pipe or a device and a read of
    // one can wait for ever, and a name of that kind in a record is never asked about either:
    // the question alone makes Windows go and look for the thing. Whatever the file is called,
    // a picture or not.
    [Theory]
    [InlineData(@"\\server\pipe\x.png")]
    [InlineData(@"\\SERVER\PIPE\report.docx")]
    [InlineData(@"\\server\mailslot\x.png")]
    [InlineData("//server/pipe/x.png")]
    [InlineData(@"\\server\IPC$\x.png")]
    [InlineData(@"\\server\pipe.\x.png")]
    [InlineData(@"\\server\pipe \x.png")]
    [InlineData(@"C:\made-up\COM3.png")]
    [InlineData(@"C:\made-up\NUL.txt")]
    [InlineData(@"C:\made-up\CON")]
    [InlineData(@"C:\made-up\lpt1 .gif")]
    [InlineData(@"C:\made-up\CONIN$.png")]
    [InlineData("C:\\made-up\\COM\u00B9.png")]
    [InlineData(@"\\server\share\LPT2.png")]
    public void A_name_that_stands_for_a_pipe_or_a_device_is_never_asked_about_and_never_listed(string name)
    {
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return true;
        });

        var blocks = Pasted(paster, Entry() with { Files = [@"C:\one.txt", name, @"D:\three.txt"] });

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], asked);
        Assert.Equal(ClipboardBytes.DropFiles([@"C:\one.txt", @"D:\three.txt"]), blocks[0].Bytes);
    }

    [Theory]
    [InlineData(@"C:\made-up\COM10.png")]
    [InlineData(@"C:\made-up\console.png")]
    [InlineData(@"C:\made-up\my-com3.png")]
    [InlineData(@"C:\made-up\pipe\x.png")]
    [InlineData(@"\\server\pipes\x.png")]
    [InlineData(@"\\server\share\pipe\x.png")]
    [InlineData(@"\\server\pipeline\x.png")]
    [InlineData(@"C:\made-up\IPC$\x.png")]
    public void A_name_that_only_looks_like_one_is_asked_about_and_listed_like_any_other(string name)
    {
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return true;
        });

        var blocks = Pasted(paster, Entry() with { Files = [name] });

        Assert.Equal([name], asked);
        Assert.Equal(ClipboardBytes.DropFiles([name]), blocks[0].Bytes);
    }

    // Windows serves a share path whose server name holds an "@" over HTTP, through the web
    // client service. Asking whether one exists is a connection, and putting its path back on
    // the clipboard hands it to a program that will open it.
    [Theory]
    [InlineData(@"\\host@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\host@8080\share\x.png")]
    [InlineData(@"\\host@SSL@8080\DavWWWRoot\folder\x.png")]
    [InlineData("//host@SSL/DavWWWRoot/x.png")]
    [InlineData(@"\\host@SSL/DavWWWRoot\x.png")]
    [InlineData(@"\\10.0.0.5@8080\share")]
    [InlineData(@"\\host@\share\x.png")]
    public void A_share_whose_server_name_holds_an_at_sign_is_never_asked_about_and_never_put_back(string name)
    {
        var asked = new ConcurrentQueue<string>();
        var paster = PasterAsking(path =>
        {
            asked.Enqueue(path);
            return true;
        });

        var blocks = Pasted(paster, Entry() with { Files = [@"C:\one.txt", name, @"D:\three.txt"] });

        Assert.Equal([@"C:\one.txt", @"D:\three.txt"], asked);
        Assert.Equal(ClipboardBytes.DropFiles([@"C:\one.txt", @"D:\three.txt"]), blocks[0].Bytes);
    }

    [Fact]
    public void Files_that_are_all_names_of_pipes_devices_or_shares_with_an_at_sign_are_gone_without_a_single_question()
    {
        int asked = 0;
        var paster = PasterAsking(_ =>
        {
            Interlocked.Increment(ref asked);
            return true;
        });

        paster.Paste(Entry() with { Files = [@"\\server\pipe\x.png", @"C:\made-up\NUL.txt", @"\\host@SSL\DavWWWRoot\x.png"] });

        Assert.Empty(_paste.Kept);
        Assert.Equal([ClipPaster.FilesGoneNote], _notes);
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    [Fact]
    public void Files_that_are_all_named_in_a_form_that_is_not_listed_are_gone_without_a_single_question()
    {
        int asked = 0;
        var paster = PasterAsking(_ =>
        {
            Interlocked.Increment(ref asked);
            return true;
        });

        paster.Paste(Entry() with { Files = [@"\\.\pipe\x.png", @"\\?\C:\x.png", "notes.txt"] });

        Assert.Empty(_paste.Kept);
        Assert.Equal([ClipPaster.FilesGoneNote], _notes);
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    [Fact]
    public void When_the_files_have_all_gone_the_text_of_the_entry_is_still_pasted()
    {
        var entry = Entry() with
        {
            Text = "the names of two files",
            TextLength = 22,
            Files = [_dir.File("gone.docx"), _dir.File("gone too.txt")],
        };

        var blocks = Pasted(entry);

        // No file list at all: an empty one would paste nothing where the text can be pasted.
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the names of two files"), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void When_the_files_have_all_gone_the_first_kept_picture_that_is_still_there_is_pasted_as_a_picture()
    {
        byte[] first = [1, 1, 1];
        byte[] second = [2, 2, 2];
        byte[] third = [3, 3, 3];
        var entry = Entry() with
        {
            Files = [_dir.File("first.jpg"), _dir.File("second.png"), _dir.File("third.gif")],
            Pictures =
            [
                new KeptPicture("first.jpg", Gone(first, ".jpg")),
                new KeptPicture("second.png", Stored(second, ".png")),
                new KeptPicture("third.gif", Stored(third, ".gif")),
            ],
        };

        var blocks = Pasted(entry);

        AssertBlock(ClipboardFormat.Dib, FakeImageCodec.DibOf(second), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void While_one_of_the_files_still_exists_no_kept_picture_is_pasted_in_its_place()
    {
        string still = OwnersFile("still here.jpg");
        var entry = Entry() with
        {
            Files = [_dir.File("gone.jpg"), still],
            Pictures = [new KeptPicture("gone.jpg", Stored([2, 2, 2], ".jpg"))],
        };

        var blocks = Pasted(entry);

        Assert.Equal([ClipboardFormat.FileDrop, ClipboardFormat.DropEffect], Formats(blocks));
        Assert.Equal([still], ClipboardBytes.FileList(blocks[0].Bytes));
        Assert.Empty(_codec.Pictures);
    }

    [Fact]
    public void An_entry_with_a_picture_of_its_own_does_not_use_the_kept_pictures()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        var entry = Entry() with
        {
            Image = Stored(png, ClipPaths.ImageExtension),
            Files = [_dir.File("gone.jpg")],
            Pictures = [new KeptPicture("gone.jpg", Stored([2, 2, 2], ".jpg"))],
        };

        var blocks = Pasted(entry);

        // One plain picture, and it is the entry's own: the kept one was never handed to the codec.
        Assert.Equal([ClipboardFormat.Png, ClipboardFormat.Dib], Formats(blocks));
        Assert.Equal(FakeImageCodec.DibOf(png), blocks[1].Bytes);
        Assert.Equal(png, Assert.Single(_codec.Pictures));
    }

    [Fact]
    public void An_entry_whose_own_picture_has_gone_uses_a_kept_picture_when_the_files_have_gone_too()
    {
        byte[] kept = [2, 2, 2];
        var entry = Entry() with
        {
            Image = Gone([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], ClipPaths.ImageExtension),
            Files = [_dir.File("gone.jpg")],
            Pictures = [new KeptPicture("gone.jpg", Stored(kept, ".jpg"))],
        };

        var blocks = Pasted(entry);

        // The record names a picture of the entry's own, but none went on the clipboard, and a
        // kept picture is on disk: that is pasted, and the owner is not told the files are gone.
        AssertBlock(ClipboardFormat.Dib, FakeImageCodec.DibOf(kept), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void An_entry_whose_own_picture_cannot_be_read_uses_a_kept_picture_when_the_files_have_gone()
    {
        byte[] damaged = [9, 9, 9];
        byte[] kept = [2, 2, 2];
        var paster = new ClipPaster(
            _paste,
            new CodecThatCannotRead(damaged),
            _notes.Add,
            (message, error) => _reports.Add((message, error)));
        var entry = Entry() with
        {
            Image = Stored(damaged, ClipPaths.ImageExtension),
            Files = [_dir.File("gone.jpg")],
            Pictures = [new KeptPicture("gone.jpg", Stored(kept, ".jpg"))],
        };

        var blocks = Pasted(paster, entry);

        AssertBlock(ClipboardFormat.Dib, FakeImageCodec.DibOf(kept), Assert.Single(blocks));
        Assert.Empty(_notes);
        Assert.Equal("A picture of a clipboard entry could not be read, so it is pasted without it", Assert.Single(_reports).Message);
    }

    [Fact]
    public void Files_that_have_all_gone_and_nothing_else_paste_nothing_and_the_owner_is_told()
    {
        _paster.Paste(Entry() with { Files = [_dir.File("gone.docx"), _dir.File("gone too.txt")] });

        Assert.Empty(_paste.Kept);
        Assert.Empty(_paste.Texts);
        Assert.Equal([ClipPaster.FilesGoneNote], _notes);
    }

    [Fact]
    public void An_entry_whose_only_content_file_has_gone_pastes_nothing_and_the_owner_is_told()
    {
        _paster.Paste(Entry() with { Html = Gone(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension) });

        Assert.Empty(_paste.Kept);
        Assert.Empty(_paste.Texts);
        Assert.Equal([ClipPaster.NothingLeftNote], _notes);
    }

    [Fact]
    public void A_picture_the_codec_cannot_read_is_left_out_and_the_rest_is_pasted()
    {
        _codec.Fails = new InvalidOperationException("made-up words from the picture code");
        var entry = Entry() with
        {
            Text = "the caption",
            TextLength = 11,
            Image = Stored([9, 9, 9], ClipPaths.ImageExtension),
        };

        var blocks = Pasted(entry);

        // Neither form of the picture: a PNG that cannot be read is no use to a program either.
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the caption"), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_picture_the_codec_cannot_read_is_reported_once_without_a_file_name_or_a_path()
    {
        _codec.Fails = new InvalidOperationException("made-up words from the picture code");
        string image = Stored([9, 9, 9], ClipPaths.ImageExtension);

        _paster.Paste(Entry() with { Text = "the caption", TextLength = 11, Image = image });

        var report = Assert.Single(_reports);
        Assert.Equal("A picture of a clipboard entry could not be read, so it is pasted without it", report.Message);
        Assert.Same(_codec.Fails, report.Error);
        Assert.DoesNotContain(image, report.Message);
        Assert.DoesNotContain(_dir.Path, report.Message);
    }

    [Fact]
    public void A_content_file_larger_than_its_limit_is_left_out()
    {
        byte[] html = new byte[ClipLimits.MaxTextBytes + 1];
        Array.Fill(html, (byte)'x');

        var blocks = Pasted(Entry() with { Text = "made up", TextLength = 7, Html = Stored(html, ClipPaths.HtmlExtension) });

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("made up"), Assert.Single(blocks));
    }

    [Fact]
    public void A_content_file_exactly_at_its_limit_is_still_pasted()
    {
        byte[] html = new byte[ClipLimits.MaxTextBytes];
        Array.Fill(html, (byte)'x');

        var blocks = Pasted(Entry() with { Html = Stored(html, ClipPaths.HtmlExtension) });

        var block = Assert.Single(blocks);
        Assert.Equal(ClipboardFormat.Html, block.Format);
        Assert.Equal(ClipLimits.MaxTextBytes + 1, block.Bytes.Length);
    }

    [Fact]
    public void A_content_file_that_cannot_be_read_is_left_out_and_the_rest_is_pasted()
    {
        string html = Stored(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension);
        IReadOnlyList<ClipboardBlock> blocks;

        // Another program has the file open and shares it with nobody.
        using (new FileStream(Path.Combine(_day, html), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            blocks = Pasted(Entry() with { Text = "made up", TextLength = 7, Html = html });
        }

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("made up"), Assert.Single(blocks));
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_content_file_that_cannot_be_read_is_reported_in_fixed_words_without_a_file_name_or_a_path()
    {
        string html = Stored(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension);

        // Another program has the file open and shares it with nobody.
        using (new FileStream(Path.Combine(_day, html), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _paster.Paste(Entry() with { Text = "made up", TextLength = 7, Html = html });
        }

        // The file is there and the paste went without it: unlike a file that has gone, that
        // is worth a line in the log. The exception goes along as it is; the words are fixed.
        var report = Assert.Single(_reports);
        Assert.Equal("A file of a clipboard entry could not be read, so it is pasted without it", report.Message);
        Assert.IsAssignableFrom<IOException>(report.Error);
        Assert.DoesNotContain(html, report.Message);
        Assert.DoesNotContain(_dir.Path, report.Message);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Several_content_files_that_cannot_be_read_are_reported_once_for_the_paste()
    {
        const string start = "Dear Otter, the start of a long letter";
        string whole = start + ", and the rest of it.";
        string text = Stored(Encoding.UTF8.GetBytes(whole), ClipPaths.TextExtension);
        string html = Stored(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension);
        string rtf = Stored(Encoding.ASCII.GetBytes(@"{\rtf1 made up}"), ClipPaths.RtfExtension);
        string kept = Stored([2, 2, 2], ".jpg");
        var entry = Entry() with
        {
            Text = start,
            TextLength = whole.Length,
            TextFile = text,
            Html = html,
            Rtf = rtf,
            Files = [_dir.File("gone.jpg")],
            Pictures = [new KeptPicture("gone.jpg", kept)],
        };
        IReadOnlyList<ClipboardBlock> blocks;

        using (new FileStream(Path.Combine(_day, text), FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(Path.Combine(_day, html), FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(Path.Combine(_day, rtf), FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(Path.Combine(_day, kept), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            blocks = Pasted(entry);
        }

        // Four files could not be read. One line says so, and the start of the text is pasted.
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock(start), Assert.Single(blocks));
        Assert.Equal("A file of a clipboard entry could not be read, so it is pasted without it", Assert.Single(_reports).Message);
    }

    [Fact]
    public void Each_paste_reports_a_file_that_cannot_be_read_anew()
    {
        string html = Stored(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension);
        var entry = Entry() with { Text = "made up", TextLength = 7, Html = html };

        using (new FileStream(Path.Combine(_day, html), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _paster.Paste(entry);
            _paster.Paste(entry);
        }

        // Once for each paste, not once for good.
        Assert.Equal(2, _paste.Kept.Count);
        Assert.Equal(2, _reports.Count);
    }

    [Fact]
    public void A_content_file_that_has_gone_is_not_reported()
    {
        string html = Gone(Encoding.UTF8.GetBytes("<b>made up</b>"), ClipPaths.HtmlExtension);

        var blocks = Pasted(Entry() with { Text = "made up", TextLength = 7, Html = html });

        // Files go when the history is cleared or grows old. That is nothing to write down.
        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("made up"), Assert.Single(blocks));
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_name_that_is_not_one_of_the_names_the_history_gives_its_files_is_never_read()
    {
        // The files are there, two folders up from the day's folder. A name in a record can
        // come from another machine, and it must not be able to point at them.
        foreach (string outside in new[] { "outside.rtf", "outside.html", "outside.png", "outside.txt" })
        {
            File.WriteAllText(Path.Combine(_history, outside), "outside the day's folder");
        }

        var entry = Entry() with
        {
            Text = "the start",
            TextLength = 9,
            TextFile = @"..\..\outside.txt",
            Rtf = @"..\..\outside.rtf",
            Html = @"..\..\outside.html",
            Image = @"..\..\outside.png",
        };
        Assert.True(File.Exists(entry.PathOf(@"..\..\outside.html")));

        var blocks = Pasted(entry);

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the start"), Assert.Single(blocks));
        Assert.Empty(_codec.Pictures);
    }

    [Theory]
    [InlineData("rich text", ".png")]
    [InlineData("rich text", ".html")]
    [InlineData("rich text", ".txt")]
    [InlineData("html", ".rtf")]
    [InlineData("html", ".txt")]
    [InlineData("html", ".png")]
    [InlineData("picture", ".jpg")]
    [InlineData("picture", ".gif")]
    [InlineData("picture", ".bmp")]
    [InlineData("picture", ".txt")]
    [InlineData("whole text", ".html")]
    [InlineData("whole text", ".rtf")]
    [InlineData("whole text", ".png")]
    public void A_content_file_of_another_kind_than_its_form_is_never_read(string form, string extension)
    {
        // The file is there, in the day's folder, under a name the history does give its files:
        // only not to the files of this form. A record can come from another machine, and its
        // rich text must not be able to name a picture, nor its picture a text.
        string file = Stored(Encoding.UTF8.GetBytes("made-up bytes, more of them than the start has"), extension);
        var entry = Entry() with { Text = "the start", TextLength = 9 };
        entry = form switch
        {
            "rich text" => entry with { Rtf = file },
            "html" => entry with { Html = file },
            "picture" => entry with { Image = file },
            _ => entry with { TextFile = file },
        };

        var blocks = Pasted(entry);

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the start"), Assert.Single(blocks));
        Assert.Empty(_codec.Pictures);
        Assert.Empty(_reports);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".html")]
    [InlineData(".rtf")]
    public void A_kept_picture_under_the_name_of_a_file_that_is_no_picture_is_never_read(string extension)
    {
        var entry = Entry() with
        {
            Text = "the names of the files",
            TextLength = 22,
            Files = [_dir.File("gone.png")],
            Pictures = [new KeptPicture("gone.png", Stored([2, 2, 2], extension))],
        };

        var blocks = Pasted(entry);

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the names of the files"), Assert.Single(blocks));
        Assert.Empty(_codec.Pictures);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".bmp")]
    public void A_kept_picture_of_every_kind_that_is_kept_can_be_pasted(string extension)
    {
        byte[] kept = [2, 2, 2];
        var entry = Entry() with
        {
            Files = [_dir.File("gone" + extension)],
            Pictures = [new KeptPicture("gone" + extension, Stored(kept, extension))],
        };

        var blocks = Pasted(entry);

        AssertBlock(ClipboardFormat.Dib, FakeImageCodec.DibOf(kept), Assert.Single(blocks));
    }

    [Fact]
    public void A_kept_picture_under_a_name_the_history_does_not_give_its_files_is_never_read()
    {
        File.WriteAllText(Path.Combine(_history, "outside.png"), "outside the day's folder");
        var entry = Entry() with
        {
            Text = "the names of the files",
            TextLength = 22,
            Files = [_dir.File("gone.png")],
            Pictures = [new KeptPicture("gone.png", @"..\..\outside.png")],
        };
        Assert.True(File.Exists(entry.PathOf(@"..\..\outside.png")));

        var blocks = Pasted(entry);

        AssertBlock(ClipboardFormat.UnicodeText, ClipboardBytes.TextBlock("the names of the files"), Assert.Single(blocks));
        Assert.Empty(_codec.Pictures);
    }

    [Fact]
    public void A_busy_clipboard_comes_out_of_the_paste()
    {
        _paste.Fails = new ClipboardBusyException();

        var thrown = Assert.Throws<ClipboardBusyException>(() => _paster.Paste(Entry() with { Text = "a short note", TextLength = 12 }));

        Assert.Same(_paste.Fails, thrown);
        Assert.Empty(_notes);
    }
}
