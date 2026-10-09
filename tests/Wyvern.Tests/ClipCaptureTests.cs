using System.Text;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>
/// Everything here runs against fakes: a clipboard that exists only in the test, a listener
/// that the test triggers by hand, and a real store in a temporary folder. No test reads the
/// real clipboard or listens to it. What is "copied" is made up.
/// </summary>
public sealed class ClipCaptureTests : IDisposable
{
    private const string Device = "OTTER-PC";

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly FakeClipboardReader _reader = new();
    private readonly FakeClipboardListener _listener = new();
    private readonly FakeImageCodec _codec = new();
    private readonly List<TimeSpan> _waits = [];
    private Action<TimeSpan>? _onWait;
    private readonly List<(string Message, Exception? Error)> _reports = [];
    private readonly ClipStore _store;
    private readonly ClipCapture _capture;
    private ClipboardOptions _options = ClipboardOptions.From(new WyvernSettings(), Device);
    private bool _canWrite = true;
    private int _stateChanges;
    private int _ids;

    public ClipCaptureTests()
    {
        _store = new ClipStore(_dir.File("clipboard"), Device, 90, _clock, newId: () => $"id-{++_ids}");
        _capture = NewCapture(work => work(_store));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private string StateFile => _dir.File("clipboard-state.json");

    private ClipCapture NewCapture(
        Action<Action<ClipStore>> post,
        TimeProvider? time = null,
        Func<string, Stream>? openPicture = null,
        Func<Stream, FileAttributes>? attributesOf = null,
        Func<string, FileAttributes>? attributesOfPath = null) => new(
        _reader,
        _listener,
        _codec,
        post,
        () => _canWrite,
        () => _options,
        StateFile,
        wait =>
        {
            _waits.Add(wait);
            _onWait?.Invoke(wait);
        },
        (message, error) => _reports.Add((message, error)),
        () => _stateChanges++,
        time,
        openPicture ?? OpenPictureInTheTestFolder,
        attributesOf,
        attributesOfPath ?? AttributesOfPath);

    /// <summary>
    /// What opening a picture file is as far as these tests go, unless a test gives its own: a
    /// real file that a test made in its own temporary folder is opened as the app opens one,
    /// and anything else is refused. A path that is not in that folder, a made-up share, pipe or
    /// device path above all, must never reach a real <see cref="FileStream"/>: Windows would
    /// look for the named server on the owner's network.
    /// </summary>
    private Stream OpenPictureInTheTestFolder(string path)
    {
        if (!path.StartsWith(_dir.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A test handed a path that is not in its own folder to the opener. It must give its own opener for a made-up path.");
        }

        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            // A folder, which the app's opener tells as a file that is not there.
            throw new FileNotFoundException();
        }
    }

    /// <summary>
    /// What a path's attributes are as far as these tests go: the real ones for a file that a
    /// test made in its own temporary folder, and "an ordinary file" for any other path. A made-up
    /// share, pipe or device path must never be handed to a real file API: Windows would look for
    /// the named server on the owner's network. A test that gives a made-up path gives its own
    /// functions for the path's attributes and for opening it.
    /// </summary>
    private FileAttributes AttributesOfPath(string path) =>
        path.StartsWith(_dir.Path, StringComparison.OrdinalIgnoreCase) ? File.GetAttributes(path) : FileAttributes.Normal;

    private List<string> Messages => _reports.Select(report => report.Message).ToList();

    private static byte[] Text(string text) => ClipboardBytes.TextBlock(text);

    private static byte[] Ascii(string text) => ClipboardBytes.WithZero(Encoding.ASCII.GetBytes(text));

    /// <summary>A made-up picture of the given size as the clipboard holds one.</summary>
    private static byte[] Dib(int width = 2, int height = 2)
    {
        byte[] pixels = new byte[width * 3 * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        return BmpFile.PackedDib(width, height, pixels);
    }

    /// <summary>What the fake codec gives for that picture: its "PNG" of the .bmp file the capture made of it.</summary>
    private static byte[] PngOf(byte[] dib)
    {
        byte[] bmp = [.. new byte[BmpFile.FileHeaderSize], .. dib];
        Assert.True(BmpFile.TryAddFileHeader(bmp));
        return FakeImageCodec.PngOf(bmp);
    }

    private string WriteFile(string name, params byte[] bytes)
    {
        string file = _dir.File(name);
        File.WriteAllBytes(file, bytes);
        return file;
    }

    /// <summary>Starts capture and makes one copy with these blocks.</summary>
    private void CopyAndTell(params (ClipboardFormat Format, byte[] Bytes)[] blocks)
    {
        if (!_capture.Listening)
        {
            _capture.Start();

            // Starting looks at what is on the clipboard once. What a test asks about is what is asked for from here on.
            _reader.Asked.Clear();
        }

        _reader.Copy(blocks);
        _listener.Changed();
    }

    // Starting, pausing and resuming

    [Fact]
    public void With_no_state_file_capture_starts_and_listens()
    {
        string? note = _capture.Start();

        Assert.Null(note);
        Assert.False(_capture.Paused);
        Assert.True(_capture.Listening);
        Assert.True(_listener.Listening);
        Assert.Equal(ClipCapture.OnTooltip, _capture.Tooltip);
        Assert.Contains("Clipboard capture started", Messages);
        Assert.True(_stateChanges > 0);
    }

    [Fact]
    public void A_state_file_that_says_paused_means_the_listener_is_never_started_and_nothing_is_read()
    {
        ClipboardState.Save(StateFile, paused: true);

        string? note = _capture.Start();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("walrus")));
        _listener.Changed();

        Assert.Null(note);
        Assert.True(_capture.Paused);
        Assert.False(_capture.Listening);
        Assert.Equal(0, _listener.Starts);
        Assert.Equal(0, _reader.Tries);
        Assert.Empty(_store.Entries);
        Assert.Equal(ClipCapture.PausedTooltip, _capture.Tooltip);
        Assert.Contains("Clipboard capture is paused", Messages);
        Assert.DoesNotContain("Clipboard capture started", Messages);
    }

    [Fact]
    public void A_state_file_that_cannot_be_understood_pauses_capture_and_the_owner_is_told()
    {
        File.WriteAllText(StateFile, "{ \"capturePaused\": \"perhaps\" }");

        string? note = _capture.Start();

        Assert.Equal(ClipCapture.StateUnreadableNote, note);
        Assert.True(_capture.Paused);
        Assert.Equal(0, _listener.Starts);
    }

    [Fact]
    public void While_the_history_cannot_be_written_nothing_is_listened_to_and_a_refresh_starts_it_once_it_can()
    {
        _canWrite = false;
        _capture.Start();

        Assert.False(_capture.Listening);
        Assert.Equal(0, _listener.Starts);
        Assert.Equal(ClipCapture.OffTooltip, _capture.Tooltip);

        _canWrite = true;
        _capture.Refresh();
        _capture.Refresh();

        Assert.True(_capture.Listening);
        Assert.Equal(1, _listener.Starts);

        _canWrite = false;
        _capture.Refresh();

        Assert.False(_capture.Listening);
        Assert.False(_listener.Listening);
        Assert.Contains("Clipboard capture stopped", Messages);
    }

    [Fact]
    public void Pause_stops_the_listener_and_is_remembered_in_the_state_file()
    {
        _capture.Start();

        string note = _capture.Pause();

        Assert.Equal(ClipCapture.PausedNote, note);
        Assert.True(_capture.Paused);
        Assert.False(_listener.Listening);
        Assert.Equal(new ClipboardState(Paused: true, Unreadable: false), ClipboardState.Load(StateFile));
        Assert.Equal(ClipCapture.PausedTooltip, _capture.Tooltip);
    }

    [Fact]
    public void A_change_that_was_waiting_its_turn_when_capture_was_paused_is_not_read()
    {
        var waiting = new List<Action<ClipStore>>();
        var capture = NewCapture(waiting.Add);
        capture.Start();

        // The look at the clipboard is the first thing in the line, and runs first.
        RunTheLine(waiting);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("walrus")));
        _listener.Changed();
        Assert.Single(waiting);
        int tries = _reader.Tries;

        capture.Pause();
        waiting[0](_store);

        Assert.Equal(tries, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_change_that_was_waiting_its_turn_when_capture_was_paused_still_does_nothing_after_capture_is_resumed()
    {
        var waiting = new List<Action<ClipStore>>();
        var capture = NewCapture(waiting.Add);
        capture.Start();

        // The look at the clipboard is the first thing in the line, and runs first.
        RunTheLine(waiting);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("walrus")));
        _listener.Changed();
        Assert.Single(waiting);

        capture.Pause();
        capture.Resume();

        // Whatever is on the clipboard by the time the old work runs was not copied for it.
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied after the resume")));
        int tries = _reader.Tries;
        waiting[0](_store);

        Assert.Equal(tries, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_change_that_was_waiting_its_turn_when_listening_stopped_does_nothing_once_it_has_started_again()
    {
        var waiting = new List<Action<ClipStore>>();
        var capture = NewCapture(waiting.Add);
        capture.Start();

        // The look at the clipboard is the first thing in the line, and runs first.
        RunTheLine(waiting);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("walrus")));
        _listener.Changed();
        Assert.Single(waiting);

        _canWrite = false;
        capture.Refresh();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while it could not write")));
        _canWrite = true;
        capture.Refresh();
        int tries = _reader.Tries;
        waiting[0](_store);

        Assert.Equal(tries, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    // Stop: the object is finished, and Wyvern is closing

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_stops_the_listener_and_neither_changes_whether_capture_is_paused_nor_writes_the_state_file(bool paused)
    {
        _capture.Start();
        if (paused)
        {
            _capture.Pause();
        }

        // Given an old date, so that a file that was written again shows.
        bool existed = File.Exists(StateFile);
        byte[] before = existed ? File.ReadAllBytes(StateFile) : [];
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (existed)
        {
            File.SetLastWriteTimeUtc(StateFile, longAgo);
        }

        _capture.Stop();

        Assert.False(_capture.Listening);
        Assert.False(_listener.Listening);
        Assert.Equal(paused, _capture.Paused);
        Assert.Equal(paused ? ClipCapture.PausedTooltip : ClipCapture.OffTooltip, _capture.Tooltip);
        Assert.Equal(existed, File.Exists(StateFile));
        if (existed)
        {
            Assert.Equal(before, File.ReadAllBytes(StateFile));
            Assert.Equal(longAgo, File.GetLastWriteTimeUtc(StateFile));
        }
    }

    [Fact]
    public void Stop_stops_a_listener_that_is_listening_and_says_so_in_the_log()
    {
        _capture.Start();
        Assert.True(_listener.Listening);

        _capture.Stop();

        Assert.False(_listener.Listening);
        Assert.Equal(1, _listener.Stops);
        Assert.Contains("Clipboard capture stopped", Messages);
        Assert.False(_capture.Paused);
    }

    [Fact]
    public void Stop_says_in_the_log_that_listening_stopped_and_does_nothing_more_when_it_is_called_again()
    {
        _capture.Start();
        _capture.Stop();
        int stops = _listener.Stops;

        _capture.Stop();

        Assert.Equal(1, Messages.Count(message => message == "Clipboard capture stopped"));
        Assert.Equal(stops, _listener.Stops);
    }

    [Fact]
    public void Stop_before_capture_ever_started_does_nothing_to_the_listener()
    {
        _capture.Stop();

        Assert.Equal(0, _listener.Stops);
        Assert.Equal(0, _listener.Starts);
        Assert.False(_capture.Paused);
    }

    [Fact]
    public void After_Stop_neither_Start_nor_Resume_nor_Refresh_listens_again()
    {
        _capture.Start();
        _capture.Stop();
        int starts = _listener.Starts;
        int tries = _reader.Tries;

        _capture.Start();
        _capture.Refresh();
        _capture.Resume();
        _capture.Refresh();

        Assert.Equal(starts, _listener.Starts);
        Assert.False(_listener.Listening);
        Assert.False(_capture.Listening);
        Assert.Equal(tries, _reader.Tries);
    }

    [Fact]
    public void After_Stop_a_capture_that_was_waiting_its_turn_reads_nothing_and_records_nothing()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        capture.Start();
        RunTheLine(line);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();
        Assert.Single(line);
        int tries = _reader.Tries;
        int owners = _reader.OwnerAsked;

        capture.Stop();
        RunTheLine(line);

        Assert.Equal(tries, _reader.Tries);
        Assert.Equal(owners, _reader.OwnerAsked);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void After_Stop_the_look_that_was_waiting_its_turn_reads_nothing()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        capture.Start();
        Assert.Single(line);

        capture.Stop();
        RunTheLine(line);

        Assert.Equal(0, _reader.Tries);
        Assert.Equal(0, _reader.OwnerAsked);
    }

    [Fact]
    public void A_reading_that_Stop_finds_waiting_for_a_busy_clipboard_is_not_tried_again()
    {
        _capture.Start();
        int tries = _reader.Tries;
        _reader.Asked.Clear();
        _reader.BusyFor = 3;
        _reader.WhenBusy = _capture.Stop;

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        // The one try that found it busy, and no other.
        Assert.Equal(tries + 1, _reader.Tries);
        Assert.Empty(_store.Entries);
        Assert.Empty(_reader.Asked);
    }

    [Fact]
    public void A_copy_whose_picture_files_are_being_read_when_Stop_comes_reads_no_more_of_them_and_records_nothing()
    {
        var opened = new List<string>();
        ClipCapture? capture = null;
        capture = NewCapture(
            work => work(_store),
            openPicture: path =>
            {
                opened.Add(path);
                capture!.Stop();
                return new PictureStream(1, 2, 3);
            });
        capture.Start();

        _reader.Copy(
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1), PhotoPath(2), PhotoPath(3)])));
        _listener.Changed();

        Assert.Equal([PhotoPath(1)], opened);
        Assert.Empty(_store.Entries);
    }

    // A reading that holds the clipboard open when capture is paused or stopped asks for no further form

    // A file that is not a picture: nothing of it is ever opened.
    private const string NotesPath = @"C:\made-up\notes.txt";

    /// <summary>The five content forms, in the order they are copied out.</summary>
    private static readonly ClipboardFormat[] ContentForms =
    [
        ClipboardFormat.UnicodeText,
        ClipboardFormat.Html,
        ClipboardFormat.Rtf,
        ClipboardFormat.Dib,
        ClipboardFormat.FileDrop,
    ];

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void A_pause_or_a_stop_that_comes_after_a_content_form_was_copied_means_no_further_form_is_asked_for_and_nothing_is_recorded(int after, bool stop)
    {
        _capture.Start();
        _reader.Asked.Clear();
        _reader.Copy(
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.Html, Ascii("<b>an otter</b>")),
            (ClipboardFormat.Rtf, Ascii("{\\rtf1 an otter}")),
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([NotesPath])));
        _reader.WhenCopied = format =>
        {
            if (format == ContentForms[after])
            {
                _reader.WhenCopied = null;
                if (stop)
                {
                    _capture.Stop();
                }
                else
                {
                    _capture.Pause();
                }
            }
        };

        _listener.Changed();

        // The form that was being copied was the last one asked for: the clipboard is still held
        // for the others, and the reading ends there as a stale one does.
        Assert.Equal(ContentForms.Take(after + 1), _reader.Asked);
        Assert.Empty(_store.Entries);
        Assert.Empty(_codec.Bitmaps);
        Assert.Empty(_codec.Pictures);
    }

    [Fact]
    public void A_reading_that_is_not_stopped_asks_for_every_content_form_as_before()
    {
        _capture.Start();
        _reader.Asked.Clear();
        _reader.Copy(
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.Html, Ascii("<b>an otter</b>")),
            (ClipboardFormat.Rtf, Ascii("{\\rtf1 an otter}")),
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([NotesPath])));
        var copied = new List<ClipboardFormat>();
        _reader.WhenCopied = copied.Add;

        _listener.Changed();

        Assert.Equal(ContentForms, _reader.Asked);
        Assert.Equal(ContentForms, copied);
        Assert.Single(_store.Entries);
    }

    // What is on the clipboard when listening starts is not a copy that is told of

    [Fact]
    public void What_was_copied_while_capture_was_paused_is_not_recorded_when_a_format_is_made_late_after_the_resume()
    {
        _capture.Start();
        _capture.Pause();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while paused")));

        _capture.Resume();

        // A program that makes a format only when it is asked: the change number moves and
        // Windows tells of it, though nothing new was copied.
        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void What_is_on_the_clipboard_when_capture_starts_is_not_recorded_when_a_format_is_made_late()
    {
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        _capture.Start();

        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void What_is_on_the_clipboard_when_capture_starts_is_not_recorded_at_all_by_the_look_at_it()
    {
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));

        _capture.Start();

        Assert.Empty(_store.Entries);
        Assert.Equal(1, _reader.Tries);
    }

    [Fact]
    public void What_is_on_the_clipboard_when_capture_is_able_to_write_again_is_not_recorded_when_a_format_is_made_late()
    {
        _canWrite = false;
        _capture.Start();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while it could not write")));
        _canWrite = true;
        _capture.Refresh();

        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    // The look at the clipboard when listening starts is put on the capture line, and runs there

    /// <summary>A capture whose posted work waits in the list until a test runs it: the history's line, by hand.</summary>
    private ClipCapture CaptureWithALine(List<Action<ClipStore>> line) => NewCapture(line.Add);

    private void RunTheLine(List<Action<ClipStore>> line)
    {
        while (line.Count > 0)
        {
            var work = line[0];
            line.RemoveAt(0);
            work(_store);
        }
    }

    [Fact]
    public void Nothing_is_read_on_the_thread_that_starts_capture_and_the_look_is_put_in_line_before_the_listener_is_started()
    {
        var line = new List<Action<ClipStore>>();
        bool? listeningWhenPosted = null;
        var capture = NewCapture(work =>
        {
            listeningWhenPosted ??= _listener.Listening;
            line.Add(work);
        });
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));

        capture.Start();

        Assert.Equal(0, _reader.Tries);
        Assert.Equal(0, _reader.OwnerAsked);
        Assert.Empty(_reader.Asked);
        Assert.Single(line);
        Assert.False(listeningWhenPosted);
        Assert.True(capture.Listening);
        Assert.Empty(_store.Entries);

        RunTheLine(line);

        Assert.Equal(1, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void Nothing_is_read_on_the_thread_that_resumes_capture_either()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        capture.Start();
        RunTheLine(line);
        capture.Pause();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while paused")));
        int tries = _reader.Tries;

        capture.Resume();

        Assert.Equal(tries, _reader.Tries);
        Assert.Single(line);

        RunTheLine(line);

        Assert.Equal(tries + 1, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void Nothing_is_read_on_the_thread_that_refreshes_capture_either_and_the_look_is_put_in_line_before_the_listener_is_started()
    {
        var line = new List<Action<ClipStore>>();
        bool? listeningWhenPosted = null;
        var capture = NewCapture(work =>
        {
            listeningWhenPosted ??= _listener.Listening;
            line.Add(work);
        });

        // Listening was not wanted, because this copy of Wyvern could not write the history.
        _canWrite = false;
        capture.Start();
        Assert.False(capture.Listening);
        Assert.Empty(line);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while it could not write")));
        _canWrite = true;
        int tries = _reader.Tries;

        capture.Refresh();

        Assert.Equal(tries, _reader.Tries);
        Assert.Equal(0, _reader.OwnerAsked);
        Assert.Empty(_reader.Asked);
        Assert.Single(line);
        Assert.False(listeningWhenPosted);
        Assert.True(capture.Listening);
        Assert.Equal(1, _listener.Starts);
        Assert.Empty(_store.Entries);

        // The look runs in its turn, remembers what is there, and records nothing.
        RunTheLine(line);

        Assert.Equal(tries + 1, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void What_was_on_the_clipboard_when_listening_started_is_not_recorded_also_when_a_format_is_made_late_after_it()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        capture.Start();
        RunTheLine(line);

        _reader.Sequence++;
        _listener.Changed();
        RunTheLine(line);

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_copy_made_after_the_look_has_run_is_recorded()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        capture.Start();
        RunTheLine(line);

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();
        RunTheLine(line);

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_copy_made_between_the_start_of_listening_and_the_looks_turn_is_taken_for_what_was_there_and_is_not_recorded()
    {
        // The price of keeping the look off the thread that starts listening, which is accepted:
        // the gap is the line's backlog at that moment, and nothing when it is idle.
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        capture.Start();

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();
        RunTheLine(line);

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void The_look_comes_before_every_capture_the_listener_causes_because_the_line_runs_in_order()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        capture.Start();

        _reader.Sequence++;
        _listener.Changed();
        _reader.Sequence++;
        _listener.Changed();

        // The look, then one capture for each message.
        Assert.Equal(3, line.Count);
        RunTheLine(line);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_look_that_fails_leaves_the_first_change_only_noted()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        var failure = new InvalidOperationException("private words from whatever failed");
        _reader.Broken = failure;
        capture.Start();

        RunTheLine(line);
        _reader.Broken = null;

        Assert.Contains(_reports, report => report.Message == "The clipboard could not be looked at when capture started, so the first change after it is not recorded" && ReferenceEquals(report.Error, failure));

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();
        RunTheLine(line);

        Assert.Empty(_store.Entries);
        Assert.Contains("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", Messages);

        _reader.Copy((ClipboardFormat.UnicodeText, Text("a walrus")));
        _listener.Changed();
        RunTheLine(line);

        Assert.Equal(["a walrus"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_look_that_is_put_off_by_a_busy_clipboard_the_whole_time_leaves_the_first_change_only_noted()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        _reader.BusyFor = ClipCapture.Attempts;
        capture.Start();

        RunTheLine(line);

        Assert.Equal(ClipCapture.Attempts, _reader.Tries);
        Assert.Contains("The clipboard could not be looked at when capture started, so the first change after it is not recorded", Messages);

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();
        RunTheLine(line);

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_pause_between_putting_the_look_in_line_and_its_turn_drops_it_and_it_reads_nothing()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        capture.Start();
        Assert.Single(line);

        capture.Pause();
        RunTheLine(line);

        Assert.Equal(0, _reader.Tries);
    }

    [Fact]
    public void A_look_that_was_dropped_or_failed_leaves_what_is_remembered_as_not_known_so_the_first_change_is_only_noted()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        capture.Start();
        RunTheLine(line);
        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();
        RunTheLine(line);
        Assert.Single(_store.Entries);

        // The first look is put off by a pause that follows at once, and the second one fails.
        capture.Pause();
        capture.Resume();
        capture.Pause();
        capture.Resume();
        Assert.Equal(2, line.Count);
        int tries = _reader.Tries;
        var dropped = line[0];
        line.RemoveAt(0);
        dropped(_store);
        Assert.Equal(tries, _reader.Tries);
        _reader.Broken = new InvalidOperationException("private words from whatever failed");
        RunTheLine(line);
        _reader.Broken = null;

        // Not known: a copy that is told of now is only noted, though what was remembered before the pause was a different copy.
        _reader.Copy((ClipboardFormat.UnicodeText, Text("a walrus")));
        _listener.Changed();
        RunTheLine(line);

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void The_look_records_nothing_whatever_it_finds()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        string photo = WriteFile("holiday.jpg", 10, 20, 30);
        _reader.Copy(
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.Html, Ascii("<b>an otter</b>")),
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([photo])));

        capture.Start();
        RunTheLine(line);

        Assert.Empty(_store.Entries);
        Assert.Empty(_codec.Bitmaps);
        Assert.Empty(_codec.Pictures);
    }

    [Fact]
    public void Nothing_is_read_or_put_in_line_when_listening_starts_while_this_copy_of_Wyvern_cannot_write()
    {
        var line = new List<Action<ClipStore>>();
        var capture = CaptureWithALine(line);
        _canWrite = false;
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));

        capture.Start();

        Assert.False(capture.Listening);
        Assert.Equal(0, _reader.Tries);
        Assert.Empty(line);
        Assert.Equal(0, _listener.Starts);
    }

    // A form that was offered and could not be read at the look leaves what was there not known

    [Fact]
    public void A_form_that_is_offered_but_gives_no_block_at_the_look_leaves_the_first_change_only_noted()
    {
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));

        // The owning program did not answer in time for its HTML.
        _reader.Withhold(ClipboardFormat.Html);
        _capture.Start();
        Assert.Contains("The clipboard could not be read completely when capture started, so the first change after it is not recorded", Messages);

        // Now it can be read: the same copy, with one form more than at the look.
        _reader.Copy(
            (ClipboardFormat.UnicodeText, Text("there before Wyvern started")),
            (ClipboardFormat.Html, Ascii("<b>there before Wyvern started</b>")));
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.Contains("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", Messages);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("rich text")]
    [InlineData("picture")]
    [InlineData("files")]
    public void Any_content_form_that_is_offered_but_gives_no_block_at_the_look_leaves_what_was_there_not_known(string form)
    {
        var format = form switch
        {
            "text" => ClipboardFormat.UnicodeText,
            "html" => ClipboardFormat.Html,
            "rich text" => ClipboardFormat.Rtf,
            "picture" => ClipboardFormat.Dib,
            _ => ClipboardFormat.FileDrop,
        };
        // Something else is there to be read, so that it is the withheld form that is the one that cannot be.
        _reader.Copy(format == ClipboardFormat.UnicodeText
            ? (ClipboardFormat.Rtf, Ascii("{\\rtf1 there before Wyvern started}"))
            : (ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        _reader.Withhold(format);

        _capture.Start();
        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.Contains("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", Messages);
    }

    [Fact]
    public void A_form_that_is_offered_but_over_its_limit_at_the_look_does_not_leave_what_was_there_not_known()
    {
        _reader.Copy(
            (ClipboardFormat.UnicodeText, Text("there before Wyvern started")),
            (ClipboardFormat.FileDrop, new byte[ClipLimits.MaxFileListBytes + 1]));
        _capture.Start();

        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain("The clipboard could not be read completely when capture started, so the first change after it is not recorded", Messages);
        Assert.DoesNotContain("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", Messages);
    }

    [Fact]
    public void A_form_that_is_not_offered_at_all_does_not_leave_what_was_there_not_known()
    {
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        _capture.Start();

        _reader.Sequence++;
        _listener.Changed();

        Assert.DoesNotContain("The clipboard could not be read completely when capture started, so the first change after it is not recorded", Messages);
        Assert.DoesNotContain("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", Messages);
    }

    [Fact]
    public void A_form_that_is_offered_but_gives_no_block_at_an_ordinary_change_is_not_a_reason_to_hold_the_copy_back()
    {
        _capture.Start();

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _reader.Withhold(ClipboardFormat.Html);
        _listener.Changed();

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void The_same_content_copied_anew_after_something_else_has_been_copied_is_recorded()
    {
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        _capture.Start();

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        CopyAndTell((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));

        Assert.Equal(["there before Wyvern started", "an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void The_same_content_copied_anew_straight_after_capture_starts_is_not_recorded_because_it_was_already_there()
    {
        _reader.Copy((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));
        _capture.Start();

        CopyAndTell((ClipboardFormat.UnicodeText, Text("there before Wyvern started")));

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void Content_that_the_rules_refuse_is_not_read_by_the_look_at_the_clipboard_either()
    {
        _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);
        _reader.Copy(
            (ClipboardFormat.KeepOutOfMonitors, []),
            (ClipboardFormat.UnicodeText, Text("a secret")));

        _capture.Start();

        Assert.Equal(1, _reader.Tries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_looked_at_when_capture_starts_means_the_first_change_is_only_noted_and_the_log_says_so()
    {
        _reader.BusyFor = ClipCapture.Attempts;
        _capture.Start();
        Assert.Equal(ClipCapture.Attempts, _reader.Tries);
        Assert.Contains("The clipboard could not be looked at when capture started, so the first change after it is not recorded", Messages);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Empty(_store.Entries);
        Assert.Contains("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", Messages);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a walrus")));

        Assert.Equal(["a walrus"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_clipboard_that_cannot_be_looked_at_when_capture_starts_means_the_first_change_is_not_recorded_when_a_format_is_made_late_either()
    {
        _reader.BusyFor = ClipCapture.Attempts;
        _capture.Start();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_when_capture_starts_is_reported_with_the_failure_and_the_first_change_is_only_noted()
    {
        var failure = new InvalidOperationException("private words from whatever failed");
        _reader.Broken = failure;
        _capture.Start();
        _reader.Broken = null;
        Assert.Contains(_reports, report => report.Message == "The clipboard could not be looked at when capture started, so the first change after it is not recorded" && ReferenceEquals(report.Error, failure));

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void Resume_listens_again_and_does_not_read_what_was_copied_while_paused()
    {
        _capture.Start();
        _capture.Pause();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while paused")));
        _listener.Changed();
        int tries = _reader.Tries;

        string note = _capture.Resume();

        Assert.Equal(ClipCapture.ResumedNote, note);
        Assert.False(_capture.Paused);
        Assert.True(_listener.Listening);
        Assert.Equal(new ClipboardState(Paused: false, Unreadable: false), ClipboardState.Load(StateFile));

        // Resuming looks at the clipboard once, to know what is there already: that is not recorded.
        Assert.Equal(tries + 1, _reader.Tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void Resume_says_so_when_this_copy_of_Wyvern_cannot_record()
    {
        _canWrite = false;
        _capture.Start();
        _capture.Pause();

        Assert.Equal(ClipCapture.ResumedButOffNote, _capture.Resume());
        Assert.Equal(ClipCapture.OffTooltip, _capture.Tooltip);
    }

    [Fact]
    public void A_pause_that_cannot_be_remembered_still_pauses_and_is_reported_and_the_owner_is_told_it_is_not_kept()
    {
        _capture.Start();
        Directory.CreateDirectory(StateFile);

        string note = _capture.Pause();

        Assert.Equal(ClipCapture.PausedUnsavedNote, note);
        Assert.NotEqual(ClipCapture.PausedNote, note);
        Assert.True(_capture.Paused);
        Assert.False(_listener.Listening);
        Assert.Equal(ClipCapture.PausedTooltip, _capture.Tooltip);
        Assert.Contains("Whether clipboard capture is paused could not be remembered for the next start", Messages);
    }

    [Fact]
    public void Saving_is_tried_three_times_with_a_wait_between_the_tries()
    {
        _capture.Start();
        using var held = new FileStream(StateFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        int saves = 0;
        _onWait = _ => saves++;

        _capture.Pause();

        Assert.Equal(TimeSpan.FromMilliseconds(50), ClipCapture.SavePause);
        Assert.Equal(3, ClipCapture.SaveAttempts);
        Assert.Equal([ClipCapture.SavePause, ClipCapture.SavePause], _waits);
        Assert.Equal(2, saves);
        Assert.Single(Messages, "Whether clipboard capture is paused could not be remembered for the next start");
    }

    [Fact]
    public void A_save_that_fails_once_and_then_succeeds_is_a_pause_that_is_remembered()
    {
        _capture.Start();
        var held = new FileStream(StateFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _onWait = _ => held.Dispose();

        string note = _capture.Pause();

        Assert.Equal(ClipCapture.PausedNote, note);
        Assert.Single(_waits);
        Assert.Equal(new ClipboardState(Paused: true, Unreadable: false), ClipboardState.Load(StateFile));
        Assert.DoesNotContain(Messages, message => message.Contains("could not be remembered"));
    }

    [Fact]
    public void A_pause_that_could_not_be_saved_is_saved_at_a_later_refresh_once_it_can_be()
    {
        _capture.Start();
        var held = new FileStream(StateFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _capture.Pause();
        Assert.Equal(new ClipboardState(Paused: true, Unreadable: true), ClipboardState.Load(StateFile));

        _capture.Refresh();
        Assert.Equal(new ClipboardState(Paused: true, Unreadable: true), ClipboardState.Load(StateFile));

        held.Dispose();
        _capture.Refresh();

        Assert.Equal(new ClipboardState(Paused: true, Unreadable: false), ClipboardState.Load(StateFile));
        Assert.True(_capture.Paused);
    }

    [Fact]
    public void A_refresh_that_cannot_save_yet_says_so_once_and_does_not_wait()
    {
        _capture.Start();
        using var held = new FileStream(StateFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _capture.Pause();
        int waits = _waits.Count;
        int told = Messages.Count(message => message.Contains("could not be remembered"));

        _capture.Refresh();
        _capture.Refresh();

        Assert.Equal(waits, _waits.Count);
        Assert.Equal(told, Messages.Count(message => message.Contains("could not be remembered")));
    }

    [Fact]
    public void A_resume_that_could_not_be_saved_has_no_new_sentence_and_is_saved_at_a_later_refresh()
    {
        _capture.Start();
        _capture.Pause();
        var held = new FileStream(StateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        string note = _capture.Resume();

        Assert.Equal(ClipCapture.ResumedNote, note);
        Assert.False(_capture.Paused);
        Assert.True(_capture.Listening);

        held.Dispose();
        _capture.Refresh();

        Assert.Equal(new ClipboardState(Paused: false, Unreadable: false), ClipboardState.Load(StateFile));
    }

    [Fact]
    public void A_start_that_finds_a_pause_that_was_not_finished_being_saved_is_paused_and_the_owner_is_told()
    {
        _capture.Start();
        Directory.CreateDirectory(StateFile);
        _capture.Pause();
        Directory.Delete(StateFile);

        var again = NewCapture(work => work(_store));
        string? note = again.Start();

        // The temporary file that the save left behind says that a save did not finish.
        Assert.Equal(ClipCapture.StateUnreadableNote, note);
        Assert.True(again.Paused);
    }

    [Fact]
    public void Resume_says_the_listener_could_not_be_started_when_that_is_why_nothing_is_listened_to()
    {
        _capture.Start();
        _capture.Pause();
        _listener.StartFails = new InvalidOperationException("private words from whatever failed");

        string note = _capture.Resume();

        Assert.Equal(ClipCapture.ListenerFailedNote, note);
        Assert.NotEqual(ClipCapture.ResumedButOffNote, note);
        Assert.False(_capture.Paused);
        Assert.False(_capture.Listening);
        Assert.Equal(ClipCapture.OffTooltip, _capture.Tooltip);
    }

    [Fact]
    public void Resume_still_says_this_copy_cannot_record_when_that_is_why_nothing_is_listened_to_even_after_a_listener_failed_before()
    {
        _capture.Start();
        _capture.Pause();
        _listener.StartFails = new InvalidOperationException("private words from whatever failed");
        _capture.Resume();
        _capture.Pause();
        _canWrite = false;

        Assert.Equal(ClipCapture.ResumedButOffNote, _capture.Resume());
    }

    [Fact]
    public void Resume_says_it_is_on_again_when_the_listener_that_failed_once_starts_at_a_later_try()
    {
        _capture.Start();
        _capture.Pause();
        _listener.StartFails = new InvalidOperationException("private words from whatever failed");
        _capture.Resume();
        _capture.Pause();
        _listener.StartFails = null;

        Assert.Equal(ClipCapture.ResumedNote, _capture.Resume());
        Assert.True(_capture.Listening);
    }

    [Fact]
    public void A_listener_that_cannot_be_started_is_reported_and_nothing_throws()
    {
        var failure = new InvalidOperationException("private words from whatever failed");
        _listener.StartFails = failure;

        _capture.Start();

        Assert.False(_capture.Listening);
        Assert.Contains(_reports, report => report.Message == "The clipboard listener could not be started or stopped" && ReferenceEquals(report.Error, failure));
    }

    // What is kept

    [Fact]
    public void Text_becomes_one_entry_with_the_program_it_came_from()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a walrus on the ice")));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("a walrus on the ice", entry.Text);
        Assert.Equal("notepad.exe", entry.SourceApp);
        Assert.Null(entry.Html);
        Assert.Null(entry.Image);
        Assert.Empty(entry.Files);
    }

    [Fact]
    public void Every_form_one_copy_offers_goes_into_one_entry()
    {
        byte[] dib = Dib();
        string file = WriteFile("notes.txt", 1, 2, 3);

        CopyAndTell(
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.Html, Ascii("<b>an otter</b>")),
            (ClipboardFormat.Rtf, Ascii("{\\rtf1 an otter}")),
            (ClipboardFormat.Dib, dib),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([file])));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("an otter", entry.Text);
        Assert.Equal(ClipPaths.BlobName(Encoding.ASCII.GetBytes("<b>an otter</b>"), ClipPaths.HtmlExtension), entry.Html);
        Assert.Equal(ClipPaths.BlobName(Encoding.ASCII.GetBytes("{\\rtf1 an otter}"), ClipPaths.RtfExtension), entry.Rtf);
        Assert.Equal(ClipPaths.BlobName(PngOf(dib), ClipPaths.ImageExtension), entry.Image);
        Assert.Equal([file], entry.Files);
        Assert.Equal(PngOf(dib), File.ReadAllBytes(entry.PathOf(entry.Image!)));
        Assert.Equal("<b>an otter</b>", File.ReadAllText(entry.PathOf(entry.Html!)));
    }

    [Fact]
    public void The_picture_is_handed_to_the_codec_as_a_bmp_file()
    {
        CopyAndTell((ClipboardFormat.Dib, Dib(3, 1)));

        byte[] bmp = Assert.Single(_codec.Bitmaps);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(Dib(3, 1), bmp[BmpFile.FileHeaderSize..]);
    }

    [Fact]
    public void A_copied_link_keeps_the_words_it_had_on_the_page()
    {
        CopyAndTell(
            (ClipboardFormat.UnicodeText, Text("https://example.org/walrus")),
            (ClipboardFormat.Html, Ascii("<a href=\"https://example.org/walrus\">All about walruses</a>")));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("https://example.org/walrus", entry.Link);
        Assert.Equal("All about walruses", entry.Label);
    }

    [Fact]
    public void Text_that_is_only_white_space_is_nothing_to_keep_and_is_not_reported()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text(" \r\n\t ")));

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(Messages, message => message.Contains("size limit"));
    }

    [Fact]
    public void The_same_thing_copied_twice_is_stored_once()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Single(_store.Entries);

        // The look when capture started, and the two copies.
        Assert.Equal(3, _reader.Tries);
    }

    [Fact]
    public void The_same_thing_copied_again_straight_after_its_entry_was_deleted_is_not_stored_again()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));
        Assert.Empty(_store.Entries);

        // The program tells of the same copy once more under a new change number.
        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void The_same_thing_copied_anew_straight_after_its_entry_was_deleted_is_not_stored_again_either()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void The_same_thing_is_stored_again_once_something_else_has_been_copied_in_between()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));

        Assert.Equal(["a password"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_copy_that_was_refused_counts_as_something_else_being_copied()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));
        _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a secret")));
        _reader.WhoOwns = new ClipboardOwner("notepad", IsWyvern: false);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));

        Assert.Equal(["a password"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_copy_whose_forms_differ_is_not_the_same_copy_although_its_text_is()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));

        CopyAndTell(
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.Html, Ascii("<b>an otter</b>")));

        Assert.NotNull(Assert.Single(_store.Entries).Html);
    }

    [Fact]
    public void A_format_made_late_does_not_make_the_picture_or_read_the_files_a_second_time()
    {
        string photo = WriteFile("holiday.jpg", 10, 20, 30);
        CopyAndTell(
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([photo])));
        Assert.Single(_codec.Bitmaps);

        // From here on another program has the picture open for writing, so reading it again would fail and be logged.
        using var held = new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _reader.Sequence++;
        _listener.Changed();

        Assert.Single(_codec.Bitmaps);
        Assert.DoesNotContain(Messages, message => message.Contains("pictures"));
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void Nothing_is_logged_for_a_copy_that_is_dropped_as_the_same()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        int reports = _reports.Count;

        _reader.Sequence++;
        _listener.Changed();

        Assert.Equal(reports, _reports.Count);
    }

    // What is never kept

    [Fact]
    public void Content_that_is_marked_to_be_left_alone_is_not_kept_and_is_not_even_read()
    {
        CopyAndTell(
            (ClipboardFormat.KeepOutOfMonitors, []),
            (ClipboardFormat.UnicodeText, Text("a secret")));

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Fact]
    public void The_older_marker_that_says_to_leave_the_content_alone_is_named_as_Windows_programs_set_it()
    {
        Assert.Equal("Clipboard Viewer Ignore", ClipboardFormat.ViewerIgnore.Name);
        Assert.Equal(0u, ClipboardFormat.ViewerIgnore.Id);
        Assert.NotEqual(ClipboardFormat.KeepOutOfMonitors, ClipboardFormat.ViewerIgnore);
    }

    [Fact]
    public void Content_that_has_the_older_marker_alone_is_not_kept_and_no_content_is_asked_for()
    {
        CopyAndTell(
            (ClipboardFormat.ViewerIgnore, []),
            (ClipboardFormat.UnicodeText, Text("a secret")),
            (ClipboardFormat.Html, Ascii("<b>a secret</b>")),
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1)])));

        Assert.Empty(_store.Entries);
        Assert.All(_reader.Asked, format => Assert.True(format == ClipboardFormat.CanIncludeInHistory || format == ClipboardFormat.CanUploadToCloud));
        Assert.Empty(_codec.Bitmaps);
    }

    [Fact]
    public void The_older_marker_is_judged_by_its_presence_alone_whatever_its_block_holds()
    {
        CopyAndTell(
            (ClipboardFormat.ViewerIgnore, [1, 0, 0, 0]),
            (ClipboardFormat.UnicodeText, Text("a secret")));

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Fact]
    public void Content_that_has_the_older_marker_is_not_read_by_the_look_at_the_clipboard_either()
    {
        _reader.Copy(
            (ClipboardFormat.ViewerIgnore, []),
            (ClipboardFormat.UnicodeText, Text("a secret")));

        _capture.Start();

        Assert.Equal(1, _reader.Tries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
        Assert.Empty(_store.Entries);

        // What is on the clipboard is remembered as dealt with: told of again, nothing is looked at.
        int looks = _reader.Looks;
        _listener.Changed();
        Assert.Equal(looks, _reader.Looks);
    }

    [Fact]
    public void A_copy_without_the_older_marker_is_kept_as_before()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void The_history_marker_says_no_when_it_holds_zero(byte value, bool kept)
    {
        CopyAndTell(
            (ClipboardFormat.CanIncludeInHistory, [value, 0, 0, 0]),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(kept, _store.Entries.Count == 1);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void The_cloud_marker_says_no_when_it_holds_zero_because_the_history_is_synced(byte value, bool kept)
    {
        CopyAndTell(
            (ClipboardFormat.CanUploadToCloud, [value, 0, 0, 0]),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(kept, _store.Entries.Count == 1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public void A_marker_that_is_there_but_is_not_one_number_is_taken_to_say_no(int length)
    {
        byte[] marker = new byte[length];
        Array.Fill(marker, (byte)1);

        CopyAndTell(
            (ClipboardFormat.CanIncludeInHistory, marker),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Fact]
    public void What_Wyvern_put_on_the_clipboard_itself_is_not_kept()
    {
        _reader.WhoOwns = new ClipboardOwner(null, IsWyvern: true);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a pasted snippet")));

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Fact]
    public void A_copy_from_an_excluded_program_is_not_kept_and_is_not_even_read()
    {
        _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a secret")));

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Theory]
    [InlineData("the marker that says to leave the content alone")]
    [InlineData("the older marker that says to leave the content alone")]
    [InlineData("the history marker holding zero")]
    [InlineData("the cloud marker holding zero")]
    [InlineData("Wyvern's own write")]
    [InlineData("an excluded program")]
    public void When_a_copy_is_refused_nothing_but_the_two_number_markers_is_asked_for_and_no_picture_is_made_or_opened(string refusal)
    {
        var opened = new List<string>();
        var capture = CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return new PictureStream(1, 2, 3);
        });
        _reader.Asked.Clear();
        var blocks = new List<(ClipboardFormat Format, byte[] Bytes)>
        {
            (ClipboardFormat.UnicodeText, Text("an otter")),
            (ClipboardFormat.Html, Ascii("<b>an otter</b>")),
            (ClipboardFormat.Rtf, Ascii("{\\rtf1 an otter}")),
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1)])),
        };
        switch (refusal)
        {
            case "the marker that says to leave the content alone":
                blocks.Add((ClipboardFormat.KeepOutOfMonitors, []));
                break;
            case "the older marker that says to leave the content alone":
                blocks.Add((ClipboardFormat.ViewerIgnore, []));
                break;
            case "the history marker holding zero":
                blocks.Add((ClipboardFormat.CanIncludeInHistory, [0, 0, 0, 0]));
                break;
            case "the cloud marker holding zero":
                blocks.Add((ClipboardFormat.CanUploadToCloud, [0, 0, 0, 0]));
                break;
            case "Wyvern's own write":
                _reader.WhoOwns = new ClipboardOwner(null, IsWyvern: true);
                break;
            case "an excluded program":
                _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);
                break;
        }

        _reader.Copy([.. blocks]);
        _listener.Changed();

        Assert.True(capture.Listening);
        Assert.Empty(_store.Entries);
        Assert.All(_reader.Asked, format => Assert.True(format == ClipboardFormat.CanIncludeInHistory || format == ClipboardFormat.CanUploadToCloud));
        Assert.Empty(_codec.Bitmaps);
        Assert.Empty(_codec.Pictures);
        Assert.Empty(opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_marker_that_is_there_without_a_block_to_read_says_do_not_keep(bool cloud)
    {
        _capture.Start();
        _reader.Asked.Clear();

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _reader.Withhold(cloud ? ClipboardFormat.CanUploadToCloud : ClipboardFormat.CanIncludeInHistory);
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Fact]
    public void A_program_that_cannot_be_told_is_kept_with_no_source_and_is_not_taken_for_an_excluded_one()
    {
        _reader.WhoOwns = new ClipboardOwner(null, IsWyvern: false);
        _options = ClipboardOptions.From(
            new WyvernSettings { ClipboardExcludedApps = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "notepad", "keepass" }) },
            Device);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("an otter", entry.Text);
        Assert.Null(entry.SourceApp);
    }

    // Who copied is asked while the clipboard is held: it cannot change then

    [Fact]
    public void A_copy_that_lands_while_the_clipboard_is_busy_is_judged_by_the_program_that_made_it()
    {
        _capture.Start();
        _reader.Asked.Clear();
        _reader.BusyFor = 1;
        _reader.WhenBusy = () =>
        {
            // Another program has the clipboard open, and in that moment the password manager copies.
            _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);
            _reader.Copy((ClipboardFormat.UnicodeText, Text("a secret")));
        };

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.DoesNotContain(ClipboardFormat.UnicodeText, _reader.Asked);
    }

    [Fact]
    public void A_copy_by_another_program_that_lands_while_the_clipboard_is_busy_is_not_taken_for_the_one_before_it()
    {
        _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);
        _capture.Start();
        _reader.BusyFor = 1;
        _reader.WhenBusy = () =>
        {
            _reader.WhoOwns = new ClipboardOwner("notepad", IsWyvern: false);
            _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        };

        _reader.Copy((ClipboardFormat.UnicodeText, Text("a secret")));
        _listener.Changed();

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("an otter", entry.Text);
        Assert.Equal("notepad.exe", entry.SourceApp);
    }

    [Fact]
    public void What_Wyvern_pasted_while_the_clipboard_was_busy_is_not_taken_for_a_copy_by_the_program_before_it()
    {
        _capture.Start();
        _reader.BusyFor = 1;
        _reader.WhenBusy = () =>
        {
            _reader.WhoOwns = new ClipboardOwner(null, IsWyvern: true);
            _reader.Copy((ClipboardFormat.UnicodeText, Text("a pasted snippet")));
        };

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void The_owner_is_only_ever_asked_while_the_clipboard_is_held_open()
    {
        _capture.Start();
        _reader.BusyFor = 2;

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        Assert.Single(_store.Entries);
        Assert.True(_reader.OwnerAsked > 0);
        Assert.Equal(0, _reader.OwnerAskedWhileClosed);
    }

    [Fact]
    public void The_settings_in_use_are_asked_for_at_every_change()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("first")));
        _options = ClipboardOptions.From(
            new WyvernSettings { ClipboardExcludedApps = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "notepad" }) },
            Device);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("second")));

        Assert.Equal(["first"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_store_that_may_not_write_is_given_nothing_and_the_clipboard_is_not_read()
    {
        using var second = new ClipStore(_dir.File("clipboard"), Device, 90, _clock);
        Assert.False(second.IsWriter);
        var capture = NewCapture(work => work(second));
        capture.Start();
        int tries = _reader.Tries;

        _reader.Copy((ClipboardFormat.UnicodeText, Text("walrus")));
        _listener.Changed();

        Assert.Equal(tries, _reader.Tries);
    }

    // One copy, told of more than once

    [Fact]
    public void A_second_message_about_the_same_copy_reads_nothing_again()
    {
        CopyAndTell((ClipboardFormat.Dib, Dib()));
        int looks = _reader.Looks;

        _listener.Changed();

        Assert.Equal(looks, _reader.Looks);
        Assert.Single(_codec.Bitmaps);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void A_repeated_message_asks_for_nothing_but_the_change_number_not_even_who_copied()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        int owners = _reader.OwnerAsked;
        int looks = _reader.Looks;
        int tries = _reader.Tries;

        _listener.Changed();

        Assert.Equal(tries + 1, _reader.Tries);
        Assert.Equal(owners, _reader.OwnerAsked);
        Assert.Equal(looks, _reader.Looks);
    }

    [Fact]
    public void A_message_about_a_new_copy_still_asks_who_copied_while_the_clipboard_is_held()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        int owners = _reader.OwnerAsked;

        CopyAndTell((ClipboardFormat.UnicodeText, Text("a walrus")));

        Assert.Equal(owners + 1, _reader.OwnerAsked);
        Assert.Equal(0, _reader.OwnerAskedWhileClosed);
    }

    [Fact]
    public void A_copy_that_was_refused_is_remembered_too_so_its_markers_are_not_looked_at_twice()
    {
        CopyAndTell(
            (ClipboardFormat.KeepOutOfMonitors, []),
            (ClipboardFormat.UnicodeText, Text("a secret")));
        int looks = _reader.Looks;
        Assert.True(looks > 0);

        _listener.Changed();

        Assert.Equal(looks, _reader.Looks);
    }

    [Fact]
    public void The_change_number_remembered_for_a_copy_that_was_refused_is_the_one_read_after_its_markers()
    {
        // Reading a marker moves the change number, as when a program makes its content when it is asked.
        _reader.SequenceStep = 1;
        CopyAndTell(
            (ClipboardFormat.CanIncludeInHistory, [0, 0, 0, 0]),
            (ClipboardFormat.UnicodeText, Text("a secret")));
        _reader.SequenceStep = 0;
        int looks = _reader.Looks;
        int owners = _reader.OwnerAsked;

        // Windows tells of that change once more, under the number it has now: nothing is looked at.
        _listener.Changed();

        Assert.Equal(looks, _reader.Looks);
        Assert.Equal(owners, _reader.OwnerAsked);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void The_change_number_remembered_by_the_look_for_a_copy_that_is_refused_is_the_one_read_after_its_markers()
    {
        _reader.SequenceStep = 1;
        _reader.Copy(
            (ClipboardFormat.CanUploadToCloud, [0, 0, 0, 0]),
            (ClipboardFormat.UnicodeText, Text("a secret")));
        _capture.Start();
        _reader.SequenceStep = 0;
        int looks = _reader.Looks;
        int owners = _reader.OwnerAsked;

        _listener.Changed();

        Assert.Equal(looks, _reader.Looks);
        Assert.Equal(owners, _reader.OwnerAsked);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_change_number_that_moved_while_the_blocks_were_read_is_still_the_same_copy_and_costs_nothing_to_tell_so()
    {
        _reader.SequenceStep = 1;
        CopyAndTell((ClipboardFormat.Dib, Dib()));

        // The program made its content when it was asked, which Windows counts as a change and
        // tells of once more. The number that is remembered is the one after the blocks were
        // read, so this is known to be the same copy without looking at any of it: had the
        // number been taken before the blocks, the fingerprint would still drop it, but only after
        // everything had been read again.
        _reader.SequenceStep = 0;
        int looks = _reader.Looks;
        _listener.Changed();

        Assert.Equal(looks, _reader.Looks);
        Assert.Single(_codec.Bitmaps);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void A_change_number_that_moved_and_a_copy_that_is_not_the_one_before_is_recorded()
    {
        _reader.SequenceStep = 1;
        CopyAndTell((ClipboardFormat.Dib, Dib()));
        _reader.SequenceStep = 0;

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(2, _store.Entries.Count);
    }

    // A read that fails forgets nothing that was remembered

    [Fact]
    public void A_change_at_which_the_reader_fails_does_not_make_capture_forget_what_it_took_at_the_resume()
    {
        _capture.Start();
        _capture.Pause();
        _reader.Copy((ClipboardFormat.UnicodeText, Text("copied while paused")));
        _capture.Resume();
        var failure = new InvalidOperationException("private words from whatever failed");
        _reader.Broken = failure;
        _reader.Sequence++;
        _listener.Changed();
        Assert.Contains(_reports, report => report.Message == "A clipboard change could not be recorded" && ReferenceEquals(report.Error, failure));
        _reader.Broken = null;

        // A format that is made late: the change number moves, nothing new was copied.
        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_change_at_which_the_reader_fails_does_not_bring_back_what_was_deleted()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));
        _reader.Broken = new InvalidOperationException("private words from whatever failed");
        _reader.Sequence++;
        _listener.Changed();
        _reader.Broken = null;

        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_clipboard_that_stays_busy_does_not_make_capture_forget_either()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text("a password")));
        Assert.True(_store.Delete(Assert.Single(_store.Entries).Id));
        _reader.BusyFor = ClipCapture.Attempts;
        _reader.Sequence++;
        _listener.Changed();

        _reader.Sequence++;
        _listener.Changed();

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_copy_that_was_read_and_could_not_be_recorded_is_forgotten_so_that_it_is_tried_again_even_under_the_same_number()
    {
        string dayFile = Path.Combine(_dir.File("clipboard"), Device, ClipPaths.DayFileName(ClipPaths.DayOf(_clock.UtcNow)));
        Directory.CreateDirectory(dayFile);
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));
        Assert.Empty(_store.Entries);
        Directory.Delete(dayFile);

        _listener.Changed();

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    // Limits

    [Fact]
    public void Text_over_its_limit_is_left_out_and_the_rest_of_the_copy_is_kept()
    {
        string tooLong = new('x', ClipLimits.MaxTextBytes + 1);

        CopyAndTell(
            (ClipboardFormat.UnicodeText, Text(tooLong)),
            (ClipboardFormat.Html, Ascii("<p>short</p>")));

        var entry = Assert.Single(_store.Entries);
        Assert.Null(entry.Text);
        Assert.NotNull(entry.Html);
        Assert.Contains("1 parts of what was copied were over their size limit and were left out", Messages);
    }

    [Fact]
    public void Text_of_exactly_the_limit_is_kept()
    {
        CopyAndTell((ClipboardFormat.UnicodeText, Text(new string('x', ClipLimits.MaxTextBytes))));

        Assert.Equal(ClipLimits.MaxTextBytes, Assert.Single(_store.Entries).TextLength);
    }

    [Fact]
    public void Text_is_measured_as_it_is_stored_so_letters_that_take_more_bytes_reach_the_limit_sooner()
    {
        // Each of these letters is two bytes as UTF-8, so this many is one byte over the limit.
        string text = new((char)0xE9, (ClipLimits.MaxTextBytes / 2) + 1);

        CopyAndTell((ClipboardFormat.UnicodeText, Text(text)));

        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void A_copy_with_nothing_within_its_limit_is_not_recorded_and_that_is_counted_in_the_log()
    {
        byte[] hugeHtml = new byte[ClipLimits.MaxTextBytes + 2];
        Array.Fill(hugeHtml, (byte)'x');
        hugeHtml[^1] = 0;

        CopyAndTell((ClipboardFormat.Html, hugeHtml));

        Assert.Empty(_store.Entries);
        Assert.Contains("1 parts of what was copied were over their size limit and were left out", Messages);
        Assert.Contains("A clipboard change was not recorded: nothing in it was within its size limit", Messages);
    }

    [Fact]
    public void A_block_too_large_to_read_at_all_is_counted_as_left_out()
    {
        byte[] huge = new byte[ClipLimits.MaxFileListBytes + 1];

        CopyAndTell(
            (ClipboardFormat.FileDrop, huge),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        var entry = Assert.Single(_store.Entries);
        Assert.Empty(entry.Files);
        Assert.Contains("1 parts of what was copied were over their size limit and were left out", Messages);
    }

    [Fact]
    public void A_picture_over_its_limit_as_PNG_is_left_out_and_the_text_is_kept()
    {
        _codec.PngLength = ClipLimits.MaxImageBytes;

        CopyAndTell(
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        var entry = Assert.Single(_store.Entries);
        Assert.Null(entry.Image);
        Assert.Equal("an otter", entry.Text);
    }

    [Fact]
    public void A_file_list_of_more_than_a_thousand_paths_is_left_out()
    {
        var paths = Enumerable.Range(0, ClipLimits.MaxFiles + 1).Select(i => $@"C:\made-up\file-{i}.txt").ToList();

        CopyAndTell(
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles(paths)),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Empty(Assert.Single(_store.Entries).Files);
    }

    [Fact]
    public void A_file_list_of_exactly_a_thousand_paths_is_kept()
    {
        var paths = Enumerable.Range(0, ClipLimits.MaxFiles).Select(i => $@"C:\made-up\file-{i}.txt").ToList();

        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles(paths)));

        Assert.Equal(ClipLimits.MaxFiles, Assert.Single(_store.Entries).Files.Count);
    }

    // Pictures that were copied as files

    [Fact]
    public void Every_picture_in_a_copied_file_list_is_kept_itself_and_no_other_file_is()
    {
        string photo = WriteFile("holiday.jpg", 10, 20, 30);
        string drawing = WriteFile("Drawing.PNG", 40, 50);
        string notes = WriteFile("notes.txt", 60, 70, 80, 90);

        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([photo, notes, drawing])));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([photo, notes, drawing], entry.Files);
        Assert.Equal(
            [
                new KeptPicture("holiday.jpg", ClipPaths.BlobName([10, 20, 30], ".jpg")),
                new KeptPicture("Drawing.PNG", ClipPaths.BlobName([40, 50], ".png")),
            ],
            entry.Pictures);
        Assert.Equal([10, 20, 30], File.ReadAllBytes(entry.PathOf(entry.Pictures[0].File)));
        Assert.Equal(2, Directory.GetFiles(entry.Folder).Length);
    }

    [Fact]
    public void A_copied_picture_survives_the_file_being_deleted()
    {
        string photo = WriteFile("holiday.jpg", 10, 20, 30);
        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([photo])));

        File.Delete(photo);

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([10, 20, 30], File.ReadAllBytes(entry.PathOf(Assert.Single(entry.Pictures).File)));
    }

    [Fact]
    public void At_most_twenty_pictures_are_kept_and_the_rest_are_listed_by_path_and_counted()
    {
        var photos = Enumerable.Range(0, ClipLimits.MaxPictures + 2).Select(i => WriteFile($"photo-{i}.jpg", (byte)i)).ToList();

        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles(photos)));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal(ClipLimits.MaxPictures + 2, entry.Files.Count);
        Assert.Equal(ClipLimits.MaxPictures, entry.Pictures.Count);
        Assert.Equal("photo-0.jpg", entry.Pictures[0].Name);
        Assert.Contains("2 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_picture_larger_than_the_limit_is_listed_by_path_only()
    {
        string huge = _dir.File("huge.bmp");
        using (var stream = new FileStream(huge, FileMode.CreateNew))
        {
            // The length is set without writing: nothing here reads the file.
            stream.SetLength(ClipLimits.MaxImageBytes + 1L);
        }

        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([huge])));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([huge], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_picture_whose_content_is_not_on_this_disk_is_not_opened_when_it_is_a_real_file()
    {
        // The real attributes of a file this test made in its own folder, read by the real code:
        // nothing is passed in for the path or for the handle. The opener is only a count.
        string cloud = WriteFile("in-the-cloud.jpg", 1, 2, 3);
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        int opened = 0;
        var capture = new ClipCapture(
            _reader,
            _listener,
            _codec,
            work => work(_store),
            () => true,
            () => _options,
            StateFile,
            _ => { },
            (message, error) => _reports.Add((message, error)),
            openPicture: path =>
            {
                opened++;
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            });
        capture.Start();

        CopyFiles(cloud);

        Assert.Equal(0, opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([cloud], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    // Kept pictures: nothing that is not an ordinary file on a disk is read, and one copy cannot stall the line

    private const string PhotoStem = @"C:\made-up\photo";

    private static string PhotoPath(int number, string extension = ".jpg") => $"{PhotoStem}-{number}{extension}";

    /// <summary>A copy of a file list of made-up files, whose content the test gives through the opener.</summary>
    private ClipCapture CapturePicturesFrom(
        Func<string, Stream> openPicture,
        TimeProvider? time = null,
        Func<Stream, FileAttributes>? attributesOf = null,
        Func<string, FileAttributes>? attributesOfPath = null)
    {
        var capture = NewCapture(work => work(_store), time, openPicture, attributesOf, attributesOfPath);
        capture.Start();
        return capture;
    }

    private void CopyFiles(params string[] paths)
    {
        _reader.Copy((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles(paths)));
        _listener.Changed();
    }

    [Fact]
    public void A_picture_is_opened_once_and_its_bytes_are_those_that_were_read_from_what_was_opened()
    {
        var opened = new List<string>();
        var stream = new PictureStream(10, 20, 30);
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return stream;
        });

        CopyFiles(PhotoPath(1));

        Assert.Equal([PhotoPath(1)], opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([10, 20, 30], File.ReadAllBytes(entry.PathOf(Assert.Single(entry.Pictures).File)));
        Assert.True(stream.Closed);
    }

    [Fact]
    public void Once_twenty_pictures_are_kept_the_names_after_them_are_counted_and_not_touched()
    {
        var opened = new List<string>();
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return new PictureStream((byte)opened.Count);
        });

        CopyFiles([.. Enumerable.Range(0, ClipLimits.MaxPictures + 5).Select(i => PhotoPath(i))]);

        Assert.Equal(ClipLimits.MaxPictures, opened.Count);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal(ClipLimits.MaxPictures, entry.Pictures.Count);
        Assert.Equal(ClipLimits.MaxPictures + 5, entry.Files.Count);
        Assert.Contains("5 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void Names_that_are_not_pictures_are_never_opened_and_do_not_count()
    {
        var opened = new List<string>();
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return new PictureStream(1);
        });

        CopyFiles(@"C:\made-up\notes.txt", @"C:\made-up\archive.zip");

        Assert.Empty(opened);
        Assert.DoesNotContain(Messages, message => message.Contains("pictures"));
    }

    [Theory]
    [InlineData(@"\\server\pipe\x.png")]
    [InlineData(@"\\SERVER\PIPE\x.png")]
    [InlineData(@"\\server\Pipe\folder\x.jpg")]
    [InlineData(@"\\server\mailslot\x.png")]
    [InlineData(@"\\server\MailSlot\x.gif")]
    [InlineData("//server/pipe/x.png")]
    [InlineData(@"\\server/pipe\x.png")]
    [InlineData(@"C:\made-up\COM3.png")]
    [InlineData(@"C:\made-up\com1.jpg")]
    [InlineData(@"C:\made-up\COM9.bmp")]
    [InlineData(@"C:\made-up\LPT1.png")]
    [InlineData(@"C:\made-up\lpt9.gif")]
    [InlineData(@"C:\made-up\CON.png")]
    [InlineData(@"C:\made-up\Con.jpeg")]
    [InlineData(@"C:\made-up\PRN.png")]
    [InlineData(@"C:\made-up\AUX.png")]
    [InlineData(@"C:\made-up\NUL.png")]
    [InlineData(@"C:\made-up\nul.PNG")]
    [InlineData(@"C:\made-up\CONIN$.png")]
    [InlineData(@"C:\made-up\CONOUT$.png")]
    [InlineData(@"C:\made-up\conout$.jpg")]
    [InlineData(@"C:\made-up\COM3.tar.png")]
    [InlineData("C:\\made-up\\COM\u00B9.png")]
    [InlineData(@"\\server\share\LPT2.png")]
    [InlineData(@"\\server\pipe.\x.png")]
    [InlineData(@"\\server\pipe \x.png")]
    [InlineData(@"\\server\pipe. .\x.png")]
    [InlineData(@"\\server\Pipe...\x.jpg")]
    [InlineData(@"\\server\mailslot..\x.png")]
    [InlineData(@"\\server\MailSlot. \x.gif")]
    [InlineData(@"\\server\IPC$\x.png")]
    [InlineData(@"\\server\ipc$\x.jpg")]
    [InlineData(@"\\server\Ipc$.\x.png")]
    [InlineData(@"\\server\IPC$ \x.png")]
    [InlineData("//server/PIPE./x.png")]
    [InlineData("//server/ipc$ ./x.png")]
    [InlineData(@"C:\made-up\COM3 .png")]
    [InlineData(@"C:\made-up\CON .jpg")]
    [InlineData(@"C:\made-up\NUL  .png")]
    [InlineData(@"C:\made-up\lpt1 .gif")]
    [InlineData(@"C:\made-up\CONIN$ .png")]
    public void A_name_that_stands_for_a_pipe_or_a_device_is_listed_by_its_path_and_never_opened(string path)
    {
        var opened = new List<string>();
        var asked = new List<string>();
        CapturePicturesFrom(
            name =>
            {
                opened.Add(name);
                return new PictureStream(1);
            },
            attributesOfPath: name =>
            {
                asked.Add(name);
                return FileAttributes.Archive;
            });

        CopyFiles(path);

        // Decided by the text: not even the attributes of the path are asked for.
        Assert.Empty(asked);
        Assert.Empty(opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([path], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Theory]
    [InlineData(@"C:\made-up\COM10.png")]
    [InlineData(@"C:\made-up\COM0.png")]
    [InlineData(@"C:\made-up\console.png")]
    [InlineData(@"C:\made-up\my-com3.png")]
    [InlineData(@"C:\made-up\aux-plug.jpg")]
    [InlineData(@"C:\made-up\pipe\x.png")]
    [InlineData(@"C:\made-up\mailslot.png")]
    [InlineData(@"\\server\pipes\x.png")]
    [InlineData(@"\\server\share\pipe\x.png")]
    [InlineData(@"\\pipe\share\x.png")]
    [InlineData(@"\\server\ipc\x.png")]
    [InlineData(@"\\server\ipc$x\x.png")]
    [InlineData(@"\\server\pipeline\x.png")]
    [InlineData(@"\\server\pipes.\x.png")]
    [InlineData(@"\\server\x-ipc$\x.png")]
    [InlineData(@"C:\made-up\IPC$\x.png")]
    [InlineData(@"C:\made-up\COM3x .png")]
    public void A_name_that_only_looks_like_one_is_opened_as_any_picture_is(string path)
    {
        var opened = new List<string>();
        CapturePicturesFrom(name =>
        {
            opened.Add(name);
            return new PictureStream(1, 2);
        });

        CopyFiles(path);

        Assert.Equal([path], opened);
        Assert.Single(Assert.Single(_store.Entries).Pictures);
    }

    // Windows serves a share path whose server name holds an "@" over HTTP. It is not listed
    // at all, so it is neither opened nor asked about, and nothing of it is kept: a connection
    // that Wyvern must never make. The paths are made up and the opener is the test's own.
    [Theory]
    [InlineData(@"\\host@SSL\DavWWWRoot\x.png")]
    [InlineData(@"\\host@8080\share\x.jpg")]
    [InlineData("//host@SSL/DavWWWRoot/x.gif")]
    [InlineData(@"\\host@SSL@8080\DavWWWRoot\folder\x.png")]
    public void A_share_whose_server_name_holds_an_at_sign_is_not_listed_and_never_asked_about_or_opened(string path)
    {
        var opened = new List<string>();
        var asked = new List<string>();
        CapturePicturesFrom(
            name =>
            {
                opened.Add(name);
                return new PictureStream(1);
            },
            attributesOfPath: name =>
            {
                asked.Add(name);
                return FileAttributes.Archive;
            });

        CopyFiles(path, @"C:\made-up\notes.txt");

        Assert.Empty(asked);
        Assert.Empty(opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([@"C:\made-up\notes.txt"], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.DoesNotContain(Messages, message => message.Contains("pictures"));
    }

    [Fact]
    public void A_picture_that_is_not_a_file_on_a_disk_because_it_cannot_seek_is_closed_without_a_byte_being_read()
    {
        var pipe = new PictureStream(1, 2, 3) { Seekable = false };
        int opened = 0;
        CapturePicturesFrom(_ =>
        {
            opened++;
            return pipe;
        });

        CopyFiles(PhotoPath(1));

        Assert.Equal(1, opened);
        Assert.Equal(0, pipe.BytesRead);
        Assert.True(pipe.Closed);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([PhotoPath(1)], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x40000)]
    [InlineData(0x400000)]
    public void A_picture_whose_content_is_not_on_this_disk_is_closed_without_a_byte_being_read(int attribute)
    {
        // Offline, recall on open and recall on data access: the attribute is asked of what was opened.
        var stream = new PictureStream(1, 2, 3);
        int opened = 0;
        CapturePicturesFrom(
            _ =>
            {
                opened++;
                return stream;
            },
            attributesOf: given =>
            {
                Assert.Same(stream, given);
                return FileAttributes.Archive | (FileAttributes)attribute;
            });

        CopyFiles(PhotoPath(1));

        Assert.Equal(1, opened);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.Closed);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([PhotoPath(1)], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x40000)]
    [InlineData(0x400000)]
    public void A_picture_whose_path_says_its_content_is_not_on_this_disk_is_never_opened(int attribute)
    {
        // Opening a file that is marked recall on open is what makes the sync tool fetch it.
        int opened = 0;
        var asked = new List<string>();
        CapturePicturesFrom(
            _ =>
            {
                opened++;
                return new PictureStream(1, 2, 3);
            },
            attributesOfPath: path =>
            {
                asked.Add(path);
                return FileAttributes.Archive | (FileAttributes)attribute;
            });

        CopyFiles(PhotoPath(1));

        Assert.Equal([PhotoPath(1)], asked);
        Assert.Equal(0, opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal([PhotoPath(1)], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void The_attributes_of_the_path_are_read_before_the_file_is_opened_and_those_of_the_handle_after_it()
    {
        var events = new List<string>();
        var stream = new PictureStream(1, 2, 3);
        CapturePicturesFrom(
            _ =>
            {
                events.Add("open");
                return stream;
            },
            attributesOf: _ =>
            {
                events.Add("handle");
                return FileAttributes.Archive;
            },
            attributesOfPath: _ =>
            {
                events.Add("path");
                return FileAttributes.Archive;
            });

        CopyFiles(PhotoPath(1));

        Assert.Equal(["path", "open", "handle"], events);
        Assert.Single(Assert.Single(_store.Entries).Pictures);
    }

    [Fact]
    public void A_path_whose_attributes_cannot_be_read_is_counted_and_not_opened_and_no_name_is_logged()
    {
        int opened = 0;
        CapturePicturesFrom(
            _ =>
            {
                opened++;
                return new PictureStream(1, 2, 3);
            },
            attributesOfPath: path => path == PhotoPath(2)
                ? throw new IOException("private words from whatever failed")
                : FileAttributes.Archive);

        CopyFiles(PhotoPath(1), PhotoPath(2), PhotoPath(3));

        Assert.Equal(2, opened);
        Assert.Equal(2, Assert.Single(_store.Entries).Pictures.Count);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
        Assert.DoesNotContain(Messages, message => message.Contains("photo", StringComparison.OrdinalIgnoreCase) || message.Contains("private"));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(ArgumentException))]
    public void Whatever_the_attributes_of_a_path_fail_with_the_rest_of_the_copy_is_kept(Type failure)
    {
        int opened = 0;
        CapturePicturesFrom(
            _ =>
            {
                opened++;
                return new PictureStream(1, 2, 3);
            },
            attributesOfPath: _ => throw (Exception)Activator.CreateInstance(failure)!);

        _reader.Copy(
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1)])),
            (ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        Assert.Equal(0, opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal("an otter", entry.Text);
        Assert.Empty(entry.Pictures);
    }

    [Fact]
    public void A_path_that_is_not_there_when_its_attributes_are_asked_is_passed_over_without_a_word_as_when_opening_it_fails()
    {
        int opened = 0;
        CapturePicturesFrom(
            _ =>
            {
                opened++;
                return new PictureStream(1, 2, 3);
            },
            attributesOfPath: _ => throw new FileNotFoundException());

        CopyFiles(PhotoPath(1));

        Assert.Equal(0, opened);
        Assert.Empty(Assert.Single(_store.Entries).Pictures);
        Assert.DoesNotContain(Messages, message => message.Contains("pictures"));
    }

    [Fact]
    public void After_the_attributes_of_one_share_path_could_not_be_read_no_other_share_path_of_that_list_is_touched()
    {
        var asked = new List<string>();
        var opened = new List<string>();
        CapturePicturesFrom(
            path =>
            {
                opened.Add(path);
                return new PictureStream(1, 2, 3);
            },
            attributesOfPath: path =>
            {
                asked.Add(path);
                return path.StartsWith(@"\\", StringComparison.Ordinal)
                    ? throw new IOException("the server could not be reached")
                    : FileAttributes.Archive;
            });

        CopyFiles(@"\\server\share\a.png", @"\\server\share\b.png", @"C:\made-up\c.png", "//other/share/d.png", @"C:\made-up\e.png");

        // Neither the attributes nor the file of a share path are asked for after the first failure.
        Assert.Equal([@"\\server\share\a.png", @"C:\made-up\c.png", @"C:\made-up\e.png"], asked);
        Assert.Equal([@"C:\made-up\c.png", @"C:\made-up\e.png"], opened);
        Assert.Equal(2, Assert.Single(_store.Entries).Pictures.Count);
        Assert.Contains("3 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_share_path_whose_attributes_are_read_without_failing_does_not_stop_the_next_one()
    {
        var asked = new List<string>();
        CapturePicturesFrom(
            _ => new PictureStream(1, 2, 3),
            attributesOfPath: path =>
            {
                asked.Add(path);
                return FileAttributes.Archive;
            });

        CopyFiles(@"\\server\share\a.png", @"\\server\share\b.png");

        Assert.Equal([@"\\server\share\a.png", @"\\server\share\b.png"], asked);
        Assert.Equal(2, Assert.Single(_store.Entries).Pictures.Count);
    }

    [Fact]
    public void The_five_seconds_hold_when_they_are_used_up_by_reading_attributes()
    {
        var time = new SteppedTime();
        var asked = new List<string>();
        int opened = 0;
        CapturePicturesFrom(
            _ =>
            {
                opened++;
                return new PictureStream(1, 2, (byte)opened);
            },
            time,
            attributesOfPath: path =>
            {
                asked.Add(path);

                // A slow server: the answer takes five seconds.
                time.Advance(TimeSpan.FromSeconds(5));
                return FileAttributes.Archive;
            });

        CopyFiles(PhotoPath(1), PhotoPath(2), PhotoPath(3));

        Assert.Equal([PhotoPath(1)], asked);
        Assert.Equal(1, opened);
        Assert.Single(Assert.Single(_store.Entries).Pictures);
        Assert.Contains("2 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void The_attributes_of_a_name_that_is_not_to_be_touched_are_not_asked_for()
    {
        var asked = new List<string>();
        var opened = new List<string>();
        CapturePicturesFrom(
            path =>
            {
                opened.Add(path);
                return new PictureStream(1);
            },
            attributesOfPath: path =>
            {
                asked.Add(path);
                return FileAttributes.Archive;
            });

        // Twenty kept, then the rest; and names that stand for a pipe or a device.
        CopyFiles([@"\\server\pipe\x.png", @"C:\made-up\COM3.png", .. Enumerable.Range(0, ClipLimits.MaxPictures + 3).Select(i => PhotoPath(i))]);

        Assert.Equal(ClipLimits.MaxPictures, asked.Count);
        Assert.Equal(ClipLimits.MaxPictures, opened.Count);
        Assert.DoesNotContain(asked, path => path.Contains("pipe") || path.Contains("COM3"));
    }

    [Fact]
    public void A_picture_with_other_attributes_than_those_that_say_it_is_not_on_the_disk_is_kept()
    {
        CapturePicturesFrom(
            _ => new PictureStream(1, 2, 3),
            attributesOf: _ => FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Compressed);

        CopyFiles(PhotoPath(1));

        Assert.Single(Assert.Single(_store.Entries).Pictures);
    }

    [Fact]
    public void The_length_that_counts_is_the_one_of_what_was_opened_and_a_picture_over_the_limit_is_not_read()
    {
        var huge = new PictureStream(1, 2, 3) { ClaimedLength = ClipLimits.MaxImageBytes + 1L };
        CapturePicturesFrom(_ => huge);

        CopyFiles(PhotoPath(1));

        Assert.Equal(0, huge.BytesRead);
        Assert.True(huge.Closed);
        Assert.Empty(Assert.Single(_store.Entries).Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_picture_that_grew_after_it_was_opened_is_cut_off_and_not_kept()
    {
        // Said to be three bytes long, and then it never ends.
        var growing = new PictureStream(1, 2, 3) { Endless = true, ClaimedLength = 3 };
        CapturePicturesFrom(_ => growing);

        CopyFiles(PhotoPath(1));

        Assert.True(growing.BytesRead <= ClipLimits.MaxImageBytes + 1L);
        Assert.True(growing.Closed);
        Assert.Empty(Assert.Single(_store.Entries).Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_picture_that_grew_after_it_was_opened_and_is_larger_than_the_limit_is_not_read_to_its_end()
    {
        var growing = new PictureStream(1) { Endless = true, ClaimedLength = ClipLimits.MaxImageBytes };
        CapturePicturesFrom(_ => growing);

        CopyFiles(PhotoPath(1));

        // The array is as long as it was said to be, and one byte more is asked for: that is how it is known to have grown.
        Assert.Equal(ClipLimits.MaxImageBytes + 1L, growing.BytesRead);
        Assert.Empty(Assert.Single(_store.Entries).Pictures);
    }

    [Fact]
    public void A_picture_that_is_shorter_than_it_was_when_opened_gives_what_is_left_of_it()
    {
        CapturePicturesFrom(_ => new PictureStream(7, 8) { ClaimedLength = 100 });

        CopyFiles(PhotoPath(1));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([7, 8], File.ReadAllBytes(entry.PathOf(Assert.Single(entry.Pictures).File)));
    }

    [Fact]
    public void The_pictures_of_one_copy_are_kept_up_to_two_hundred_megabytes_by_the_length_of_what_was_opened()
    {
        // Four pictures that are each as long as one may be make exactly what one copy may hold.
        // Their lengths are made up: what they give is a few bytes.
        var streams = new List<PictureStream>();
        CapturePicturesFrom(_ =>
        {
            var stream = streams.Count < 4
                ? new PictureStream((byte)(streams.Count + 1)) { ClaimedLength = ClipLimits.MaxImageBytes }
                : new PictureStream(99) { ClaimedLength = 1 };
            streams.Add(stream);
            return stream;
        });

        CopyFiles(PhotoPath(1), PhotoPath(2), PhotoPath(3), PhotoPath(4), PhotoPath(5));

        Assert.Equal(5, streams.Count);
        Assert.Equal(4, Assert.Single(_store.Entries).Pictures.Count);
        Assert.Equal(0, streams[4].BytesRead);
        Assert.True(streams[4].Closed);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_picture_that_cannot_be_opened_costs_one_count_and_no_name_in_the_log_and_the_others_are_kept()
    {
        var failure = new IOException("private words from whatever failed");
        CapturePicturesFrom(path => path == PhotoPath(2) ? throw failure : new PictureStream(1, 2, 3));

        CopyFiles(PhotoPath(1), PhotoPath(2), PhotoPath(3));

        Assert.Equal(2, Assert.Single(_store.Entries).Pictures.Count);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
        Assert.DoesNotContain(Messages, message => message.Contains("photo", StringComparison.OrdinalIgnoreCase) || message.Contains("private"));
    }

    [Fact]
    public void A_picture_that_cannot_be_read_costs_one_count_and_no_name_in_the_log_and_is_closed()
    {
        var broken = new PictureStream(1, 2, 3) { ReadFails = new IOException("private words from whatever failed") };
        CapturePicturesFrom(path => path == PhotoPath(1) ? broken : new PictureStream(4, 5));

        CopyFiles(PhotoPath(1), PhotoPath(2));

        Assert.True(broken.Closed);
        Assert.Single(Assert.Single(_store.Entries).Pictures);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
        Assert.DoesNotContain(Messages, message => message.Contains("photo", StringComparison.OrdinalIgnoreCase) || message.Contains("private"));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(System.Security.SecurityException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public void Whatever_a_picture_fails_with_the_rest_of_the_copy_is_kept(Type failure)
    {
        CapturePicturesFrom(_ => throw (Exception)Activator.CreateInstance(failure)!);

        _reader.Copy(
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1)])),
            (ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        var entry = Assert.Single(_store.Entries);
        Assert.Equal("an otter", entry.Text);
        Assert.Empty(entry.Pictures);
    }

    [Fact]
    public void Pictures_are_read_for_five_seconds_and_the_names_not_yet_looked_at_are_listed_by_path_only_and_counted()
    {
        var time = new SteppedTime();
        var opened = new List<string>();
        CapturePicturesFrom(
            path =>
            {
                opened.Add(path);

                // A slow disk: this one picture takes five seconds to open.
                time.Advance(TimeSpan.FromSeconds(5));
                return new PictureStream(1, 2, (byte)opened.Count);
            },
            time);

        CopyFiles(PhotoPath(1), PhotoPath(2), PhotoPath(3));

        Assert.Equal([PhotoPath(1)], opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal(3, entry.Files.Count);
        Assert.Single(entry.Pictures);
        Assert.Contains("2 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void Pictures_are_still_read_when_the_five_seconds_have_not_quite_gone_by()
    {
        var time = new SteppedTime();
        var opened = new List<string>();
        CapturePicturesFrom(
            path =>
            {
                opened.Add(path);
                time.Advance(TimeSpan.FromSeconds(2.4));
                return new PictureStream(1, 2, (byte)opened.Count);
            },
            time);

        CopyFiles(PhotoPath(1), PhotoPath(2), PhotoPath(3), PhotoPath(4));

        // 0 s, 2.4 s and 4.8 s are within the time; the fourth name is looked at at 7.2 s.
        Assert.Equal([PhotoPath(1), PhotoPath(2), PhotoPath(3)], opened);
        Assert.Equal(3, Assert.Single(_store.Entries).Pictures.Count);
        Assert.Contains("1 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void The_five_seconds_are_counted_from_the_start_of_each_list_and_not_from_the_start_of_capture()
    {
        var time = new SteppedTime();
        time.Advance(TimeSpan.FromMinutes(10));
        var opened = new List<string>();
        CapturePicturesFrom(
            path =>
            {
                opened.Add(path);
                return new PictureStream(1, 2, (byte)opened.Count);
            },
            time);

        CopyFiles(PhotoPath(1), PhotoPath(2));

        Assert.Equal(2, opened.Count);
    }

    [Fact]
    public void After_the_first_failure_with_a_share_path_no_other_share_path_of_that_list_is_touched()
    {
        var opened = new List<string>();
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return path.StartsWith(@"\\", StringComparison.Ordinal)
                ? throw new IOException("the server could not be reached")
                : new PictureStream(1, 2, 3);
        });

        CopyFiles(@"\\server\share\a.png", @"\\server\share\b.png", @"C:\made-up\c.png", "//other/share/d.png", @"C:\made-up\e.png");

        // The drive paths are not share paths, and are opened; every share path after the first is not.
        Assert.Equal([@"\\server\share\a.png", @"C:\made-up\c.png", @"C:\made-up\e.png"], opened);
        var entry = Assert.Single(_store.Entries);
        Assert.Equal(2, entry.Pictures.Count);
        Assert.Contains("3 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void A_share_path_that_is_opened_without_failing_does_not_stop_the_next_one_being_opened()
    {
        var opened = new List<string>();
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return new PictureStream(1, 2, (byte)opened.Count);
        });

        CopyFiles(@"\\server\share\a.png", @"\\server\share\b.png");

        Assert.Equal(2, opened.Count);
        Assert.Equal(2, Assert.Single(_store.Entries).Pictures.Count);
    }

    [Fact]
    public void A_share_path_that_is_not_there_is_a_failure_of_the_share_too()
    {
        var opened = new List<string>();
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return path.EndsWith("a.png", StringComparison.Ordinal)
                ? throw new FileNotFoundException()
                : new PictureStream(1, 2, 3);
        });

        CopyFiles(@"\\server\share\a.png", @"\\server\share\b.png");

        Assert.Equal([@"\\server\share\a.png"], opened);
    }

    [Fact]
    public void The_failure_of_a_share_path_in_one_list_does_not_count_against_the_next_list()
    {
        bool fail = true;
        var opened = new List<string>();
        CapturePicturesFrom(path =>
        {
            opened.Add(path);
            return fail ? throw new IOException() : new PictureStream(1, 2, 3);
        });
        CopyFiles(@"\\server\share\a.png");
        fail = false;

        CopyFiles(@"\\server\share\b.png");

        Assert.Equal([@"\\server\share\a.png", @"\\server\share\b.png"], opened);
        Assert.Single(_store.Entries, entry => entry.Pictures.Count == 1);
    }

    [Fact]
    public void A_made_up_path_handed_to_the_tests_default_opener_is_refused_and_reaches_no_file_api()
    {
        // The default opener of the tests' helper opens real files only in the test's own folder.
        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1), @"\\server\share\a.png"])));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal(2, entry.Files.Count);
        Assert.Empty(entry.Pictures);
        Assert.Contains("2 pictures in a copied file list were listed by their path only", Messages);
    }

    [Fact]
    public void The_apps_own_defaults_for_the_path_and_for_the_opener_keep_a_real_picture_and_pass_over_a_folder_and_a_missing_file()
    {
        // Nothing is passed in: the real attribute reader and the real opener, on files and a
        // folder that this test made in its own folder.
        string photo = WriteFile("holiday.jpg", 10, 20, 30);
        string folder = _dir.File("album.png");
        Directory.CreateDirectory(folder);
        string gone = _dir.File("gone.gif");
        var capture = new ClipCapture(
            _reader,
            _listener,
            _codec,
            work => work(_store),
            () => true,
            () => _options,
            StateFile,
            _ => { },
            (message, error) => _reports.Add((message, error)));
        capture.Start();

        CopyFiles(photo, folder, gone);

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([photo, folder, gone], entry.Files);
        Assert.Equal([10, 20, 30], File.ReadAllBytes(entry.PathOf(Assert.Single(entry.Pictures).File)));
        Assert.DoesNotContain(Messages, message => message.Contains("pictures"));
    }

    [Fact]
    public void A_picture_that_another_program_has_open_for_writing_is_still_read()
    {
        string photo = WriteFile("being-saved.jpg", 10, 20, 30);
        using var writing = new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([photo])));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([10, 20, 30], File.ReadAllBytes(entry.PathOf(Assert.Single(entry.Pictures).File)));
    }

    [Fact]
    public void The_clipboards_picture_is_let_go_of_before_the_picture_files_are_read()
    {
        var weak = new List<WeakReference>();
        var codec = new ForgetfulCodec(weak);
        var capture = new ClipCapture(
            _reader,
            _listener,
            codec,
            work => work(_store),
            () => true,
            () => _options,
            StateFile,
            _ => { },
            openPicture: _ =>
            {
                // The first picture file is about to be read: the clipboard's picture is not held any more.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.Single(weak);
                Assert.False(weak[0].IsAlive);
                return new PictureStream(1, 2, 3);
            },
            attributesOfPath: AttributesOfPath);
        capture.Start();

        _reader.Copy(
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([PhotoPath(1)])));
        _listener.Changed();

        Assert.Single(Assert.Single(_store.Entries).Pictures);
    }

    [Fact]
    public void A_folder_or_a_missing_file_with_a_picture_name_is_passed_over_without_a_word()
    {
        string folder = _dir.File("album.png");
        Directory.CreateDirectory(folder);
        string gone = _dir.File("gone.jpg");

        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([folder, gone])));

        var entry = Assert.Single(_store.Entries);
        Assert.Equal([folder, gone], entry.Files);
        Assert.Empty(entry.Pictures);
        Assert.DoesNotContain(Messages, message => message.Contains("pictures"));
    }

    // Things that go wrong

    [Fact]
    public void A_clipboard_that_is_busy_for_a_moment_is_tried_again()
    {
        _capture.Start();
        _reader.BusyFor = 3;

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        Assert.Single(_store.Entries);
        Assert.Equal([ClipCapture.RetryPause, ClipCapture.RetryPause, ClipCapture.RetryPause], _waits);
    }

    [Fact]
    public void A_clipboard_that_stays_busy_is_given_up_on_and_the_next_copy_is_still_recorded()
    {
        _capture.Start();
        int tries = _reader.Tries;
        _reader.BusyFor = ClipCapture.Attempts;

        _reader.Copy((ClipboardFormat.UnicodeText, Text("missed")));
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.Equal(ClipCapture.Attempts, _reader.Tries - tries);
        Assert.Equal(ClipCapture.Attempts - 1, _waits.Count);
        Assert.Contains("The clipboard stayed in use by another program, so one change was not recorded", Messages);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_clipboard_that_cannot_be_read_is_reported_and_nothing_throws()
    {
        var failure = new InvalidOperationException("private words from whatever failed");
        _capture.Start();
        _reader.Broken = failure;

        _reader.Copy((ClipboardFormat.UnicodeText, Text("an otter")));
        _listener.Changed();

        Assert.Empty(_store.Entries);
        Assert.Contains(_reports, report => report.Message == "A clipboard change could not be recorded" && ReferenceEquals(report.Error, failure));
    }

    [Fact]
    public void A_picture_that_is_not_one_is_reported_and_the_rest_of_the_copy_is_kept()
    {
        CopyAndTell(
            (ClipboardFormat.Dib, new byte[100]),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        var entry = Assert.Single(_store.Entries);
        Assert.Null(entry.Image);
        Assert.Contains("A picture on the clipboard was in a form that is not read", Messages);
        Assert.Empty(_codec.Bitmaps);
    }

    [Fact]
    public void A_picture_that_cannot_be_turned_into_a_PNG_is_reported_and_the_rest_of_the_copy_is_kept()
    {
        var failure = new NotSupportedException("private words from whatever failed");
        _codec.Fails = failure;

        CopyAndTell(
            (ClipboardFormat.Dib, Dib()),
            (ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal("an otter", Assert.Single(_store.Entries).Text);
        Assert.Contains(_reports, report => report.Message == "A picture on the clipboard could not be turned into a PNG" && ReferenceEquals(report.Error, failure));
    }

    [Fact]
    public void An_entry_that_cannot_be_written_is_reported_and_the_next_copy_is_still_recorded()
    {
        // A folder where today's file should be makes the append fail.
        string dayFile = Path.Combine(_dir.File("clipboard"), Device, ClipPaths.DayFileName(ClipPaths.DayOf(_clock.UtcNow)));
        Directory.CreateDirectory(dayFile);

        CopyAndTell((ClipboardFormat.UnicodeText, Text("lost")));

        Assert.Empty(_store.Entries);
        Assert.Contains(Messages, message => message == "A clipboard change could not be recorded");

        Directory.Delete(dayFile);
        CopyAndTell((ClipboardFormat.UnicodeText, Text("an otter")));

        Assert.Equal(["an otter"], _store.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void Nothing_that_was_copied_and_no_program_name_reaches_the_log()
    {
        string photo = WriteFile("walrus-photo.jpg", 1, 2, 3);
        CopyAndTell((ClipboardFormat.UnicodeText, Text("walrus secret")));
        CopyAndTell((ClipboardFormat.FileDrop, ClipboardBytes.DropFiles([photo])));
        CopyAndTell((ClipboardFormat.Dib, new byte[100]));
        _reader.WhoOwns = new ClipboardOwner("KeePass", IsWyvern: false);
        CopyAndTell((ClipboardFormat.UnicodeText, Text("walrus password")));
        _capture.Pause();

        Assert.NotEmpty(_reports);
        Assert.All(Messages, message =>
        {
            Assert.DoesNotContain("walrus", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notepad", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("keepass", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_dir.Path, message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// What the opener of a picture file gives in a test: a stream that is not a file at all, so
    /// that nothing here opens a pipe, a device or a share. It says what was read from it and
    /// whether it was closed, and it can claim a length other than its content's, not seek,
    /// never end, or fail when read.
    /// </summary>
    private sealed class PictureStream(params byte[] content) : Stream
    {
        private long _position;

        public long? ClaimedLength { get; init; }

        public bool Seekable { get; init; } = true;

        public bool Endless { get; init; }

        public Exception? ReadFails { get; init; }

        public long BytesRead { get; private set; }

        public bool Closed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => Seekable;

        public override bool CanWrite => false;

        public override long Length => Seekable ? ClaimedLength ?? content.Length : throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ReadFails is not null)
            {
                throw ReadFails;
            }

            if (Endless)
            {
                Array.Fill(buffer, (byte)'x', offset, count);
                BytesRead += count;
                return count;
            }

            int given = (int)Math.Min(count, content.Length - _position);
            Array.Copy(content, _position, buffer, offset, given);
            _position += given;
            BytesRead += given;
            return given;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A clock whose time only moves when a test moves it, in the units that <see cref="TimeProvider"/> counts.</summary>
    private sealed class SteppedTime : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount) => _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
    }

    /// <summary>
    /// A picture codec that does not keep what it is given, but only a weak reference to it, so
    /// that a test can see whether anything else still holds it.
    /// </summary>
    private sealed class ForgetfulCodec(List<WeakReference> seen) : IImageCodec
    {
        public byte[] BmpToPng(byte[] bmp)
        {
            seen.Add(new WeakReference(bmp));
            return [0x89, (byte)'P', (byte)'N', (byte)'G'];
        }

        public byte[] ToDib(byte[] picture) => picture;
    }
}
