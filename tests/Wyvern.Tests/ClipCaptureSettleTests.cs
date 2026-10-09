using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>
/// Capture leaves the clipboard alone until the program that is copying has finished with it.
/// A program can put a copy on the clipboard a form at a time, and gives the copy up when it
/// finds the clipboard open in between. The clipboard here is a fake the test fills, and the
/// waiting is a list the test reads: no time passes.
/// </summary>
public sealed class ClipCaptureSettleTests : IDisposable
{
    private const string Device = "OTTER-PC";
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    private readonly TempDir _dir = new();
    private readonly FakeClipboardReader _reader = new();
    private readonly FakeClipboardListener _listener = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly List<string> _reports = [];
    private readonly ClipStore _store;
    private readonly ClipboardOptions _options = ClipboardOptions.From(new WyvernSettings(), Device);
    private TimeSpan _settle = Settle;
    private Action<int>? _onWait;
    private int _ids;

    public ClipCaptureSettleTests() =>
        _store = new ClipStore(_dir.File("clipboard"), Device, 90, new FakeClock(), newId: () => $"id-{++_ids}");

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private ClipCapture NewCapture(Action<Action<ClipStore>> post) => new(
        _reader,
        _listener,
        new FakeImageCodec(),
        post,
        () => true,
        () => _options,
        _dir.File("clipboard-state.json"),
        wait =>
        {
            _waits.Add(wait);
            _onWait?.Invoke(_waits.Count);
        },
        (message, _) => _reports.Add(message),
        settle: () => _settle);

    private ClipCapture Started()
    {
        var capture = NewCapture(work => work(_store));
        capture.Start();
        _waits.Clear();
        return capture;
    }

    private static byte[] Text(string text) => ClipboardBytes.TextBlock(text);

    private void Copy(string text) => _reader.Copy((ClipboardFormat.UnicodeText, Text(text)));

    private TimeSpan Waited => TimeSpan.FromTicks(_waits.Sum(wait => wait.Ticks));

    [Fact]
    public void The_clipboard_is_not_opened_until_it_has_been_left_alone_for_the_settle_time()
    {
        Started();
        int tries = _reader.Tries;
        var triesAtEachWait = new List<int>();
        _onWait = _ => triesAtEachWait.Add(_reader.Tries - tries);

        Copy("an otter");
        _listener.Changed();

        // Four waits of a fortieth of a second each, and not one try at the clipboard during them.
        Assert.Equal([ClipCapture.SettlePoll, ClipCapture.SettlePoll, ClipCapture.SettlePoll, ClipCapture.SettlePoll], _waits);
        Assert.All(triesAtEachWait, during => Assert.Equal(0, during));
        Assert.Equal(1, _reader.Tries - tries);
        Assert.Equal("an otter", Assert.Single(_store.Entries).Text);
    }

    [Fact]
    public void A_settle_time_that_is_not_a_whole_number_of_looks_is_waited_out_exactly()
    {
        _settle = TimeSpan.FromMilliseconds(60);
        Started();

        Copy("an otter");
        _listener.Changed();

        Assert.Equal([ClipCapture.SettlePoll, ClipCapture.SettlePoll, TimeSpan.FromMilliseconds(10)], _waits);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void A_program_that_puts_its_copy_there_in_steps_is_waited_for_and_read_once_when_it_has_finished()
    {
        // The history's line by hand, as the app has it: each message puts one piece of work in it.
        var line = new List<Action<ClipStore>>();
        var capture = NewCapture(line.Add);
        capture.Start();
        line[0](_store);
        line.Clear();
        _waits.Clear();
        int tries = _reader.Tries;

        // The first step, and Windows tells of it.
        _reader.Copy((ClipboardFormat.UnicodeText, Text("half a copy")));
        _listener.Changed();

        // While Wyvern waits, the program comes back twice more: the second step after two
        // waits, the third after five. Each is told of, and each puts its own work in the line.
        _onWait = count =>
        {
            if (count == 2)
            {
                _reader.Copy((ClipboardFormat.UnicodeText, Text("most of a copy")));
                _listener.Changed();
            }
            else if (count == 5)
            {
                _reader.Copy((ClipboardFormat.UnicodeText, Text("the whole copy")));
                _listener.Changed();
            }
        };

        while (line.Count > 0)
        {
            var work = line[0];
            line.RemoveAt(0);
            work(_store);
        }

        // Read once, after the last step had been left alone for the whole settle time: the
        // two waits before the second step and the three before the third did not count.
        Assert.Equal(1, _reader.Tries - tries);
        Assert.Equal("the whole copy", Assert.Single(_store.Entries).Text);
        Assert.Equal(2 + 3 + 4, _waits.Count);
    }

    [Fact]
    public void A_window_that_still_has_the_clipboard_open_starts_the_wait_again()
    {
        Started();
        int tries = _reader.Tries;
        _reader.InUseFor = 3;

        Copy("an otter");
        _listener.Changed();

        // Three looks found it open; the four after them found it left alone.
        Assert.Equal(7, _waits.Count);
        Assert.Equal(7, _reader.InUseAsked);
        Assert.Equal(1, _reader.Tries - tries);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void A_clipboard_that_is_never_left_alone_is_read_all_the_same_once_the_limit_is_reached()
    {
        Started();
        _reader.InUseFor = int.MaxValue;

        Copy("an otter");
        _listener.Changed();

        Assert.Equal(ClipCapture.SettleLimit, Waited);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void Capture_that_is_paused_while_it_waits_reads_nothing()
    {
        var capture = Started();
        int tries = _reader.Tries;
        _onWait = count =>
        {
            if (count == 2)
            {
                capture.Pause();
            }
        };

        Copy("copied just before the pause");
        _listener.Changed();

        Assert.Equal(0, _reader.Tries - tries);
        Assert.Empty(_store.Entries);
    }

    [Fact]
    public void One_copy_after_another_is_each_waited_for_and_each_recorded()
    {
        Started();

        Copy("first");
        _listener.Changed();
        Copy("second");
        _listener.Changed();

        Assert.Equal(["second", "first"], _store.Entries.Select(entry => entry.Text));
        Assert.Equal(8, _waits.Count);
    }

    [Fact]
    public void Without_a_settle_time_the_clipboard_is_opened_at_once_as_before()
    {
        _settle = TimeSpan.Zero;
        Started();

        Copy("an otter");
        _listener.Changed();

        Assert.Empty(_waits);
        Assert.Equal(0, _reader.InUseAsked);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void A_settle_time_longer_than_the_longest_is_cut_to_it()
    {
        _settle = TimeSpan.FromMinutes(10);
        Started();

        Copy("an otter");
        _listener.Changed();

        Assert.Equal(ClipCapture.MaxSettle, Waited);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_asked_whether_it_is_open_is_taken_for_free()
    {
        Started();
        _reader.InUseBroken = new InvalidOperationException("no");

        Copy("an otter");
        _listener.Changed();

        Assert.Equal(4, _waits.Count);
        Assert.Single(_store.Entries);
        Assert.DoesNotContain(_reports, words => words.Contains("could not", StringComparison.Ordinal));
    }

    [Fact]
    public void After_the_wait_a_clipboard_that_is_busy_is_still_tried_again()
    {
        Started();
        _reader.BusyFor = 2;

        Copy("an otter");
        _listener.Changed();

        Assert.Equal(
            [ClipCapture.SettlePoll, ClipCapture.SettlePoll, ClipCapture.SettlePoll, ClipCapture.SettlePoll, ClipCapture.RetryPause, ClipCapture.RetryPause],
            _waits);
        Assert.Single(_store.Entries);
    }

    [Fact]
    public void The_settle_time_is_half_a_second_unless_another_is_set_and_a_reader_that_cannot_tell_says_not_in_use()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), ClipCapture.DefaultSettle);
        Assert.True(ClipCapture.MaxSettle < ClipCapture.SettleLimit);

        IClipboardReader plain = new CannotTell();
        Assert.False(plain.InUse());
    }

    private sealed class CannotTell : IClipboardReader
    {
        public ClipboardOwner Owner() => new("notepad", IsWyvern: false);

        public T Read<T>(Func<IOpenClipboard, T> read) => throw new ClipboardBusyException();
    }
}
