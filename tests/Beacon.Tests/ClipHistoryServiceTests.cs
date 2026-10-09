using System.Text.Json;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Platform;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>
/// The service does its work on a background task. A test waits for <c>Idle</c>, up to a
/// generous limit, and where the order of things matters it holds a piece of work with an
/// event of its own; nothing depends on how fast the machine is. The daily timer never runs
/// out by itself: a test fires it.
/// </summary>
public sealed class ClipHistoryServiceTests : IDisposable
{
    private const string Device = "OTTER-PC";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly DateOnly Today = new(2026, 10, 3);

    private const string LoadedStart = "Clipboard history loaded";
    private const string DayNotRead = "A day of clipboard history could not be read";
    private const string DayNotRewritten = "A day of clipboard history could not be rewritten";
    private const string DayIsFull = "Today's clipboard history is full";

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly ShakyClock _shaky;
    private readonly ManualTimeProvider _time = new();
    private readonly WatchedTime _watched;
    private readonly List<(string Message, Exception? Error)> _reports = [];
    private readonly List<string> _notes = [];
    private readonly ClipHistoryService _service;
    private int _changes;
    private Hold? _hold;
    private Action<string>? _seen;
    private Action<TimeSpan>? _onPause;

    public ClipHistoryServiceTests()
    {
        _shaky = new ShakyClock(_clock);
        _watched = new WatchedTime(_time);
        _service = new ClipHistoryService(
            _shaky,
            (message, error) =>
            {
                lock (_reports)
                {
                    _reports.Add((message, error));
                }

                Volatile.Read(ref _seen)?.Invoke(message);

                // A piece of work can be held where the store holds its own lock: the store
                // says what it is doing while it is doing it.
                var hold = Volatile.Read(ref _hold);
                if (hold is not null
                    && message.StartsWith(hold.Start, StringComparison.Ordinal)
                    && ReferenceEquals(Interlocked.CompareExchange(ref _hold, null, hold), hold))
                {
                    hold.Entered.Set();
                    hold.Release.Wait(Limit);
                }
            },
            note =>
            {
                lock (_notes)
                {
                    _notes.Add(note);
                }
            },
            () => Interlocked.Increment(ref _changes),
            _watched,
            length => Volatile.Read(ref _onPause)?.Invoke(length));
    }

    public void Dispose()
    {
        _service.Dispose();
        _service.Idle.Wait(Limit);
        _watched.AnyDisposed.Dispose();
        _dir.Dispose();
    }

    /// <summary>
    /// The first thing the store says that starts with <see cref="Start"/> is held until
    /// <see cref="Release"/> is set. The store says it while it holds its own lock, so whatever
    /// the service is doing then is held inside the store.
    /// </summary>
    private sealed class Hold(string start) : IDisposable
    {
        public string Start { get; } = start;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        // Lets go of whatever is still held, so that a test that failed leaves no thread waiting.
        public void Dispose() => Release.Set();
    }

    /// <summary>A clock that can be made to fail, to make the store fail in a way that is not about a file.</summary>
    private sealed class ShakyClock(IClock inner) : IClock
    {
        private volatile bool _fails;

        public bool Fails
        {
            set => _fails = value;
        }

        public DateTimeOffset UtcNow => _fails ? throw new InvalidOperationException("the clock is broken") : inner.UtcNow;
    }

    /// <summary>The manual time provider's timers, which tell a test when one of them has been disposed.</summary>
    private sealed class WatchedTime(ManualTimeProvider inner) : TimeProvider
    {
        public ManualResetEventSlim AnyDisposed { get; } = new();

        /// <summary>Gets one more permit each time a timer is made, so that a test can wait for the thread it started to have made its own.</summary>
        public SemaphoreSlim Made { get; } = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new WatchedTimer(inner.CreateTimer(callback, state, dueTime, period), AnyDisposed);
            Made.Release();
            return timer;
        }
    }

    private sealed class WatchedTimer(ITimer inner, ManualResetEventSlim disposed) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            inner.Dispose();
            disposed.Set();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Holds the next thing the store says that starts with these words.</summary>
    private Hold HoldAt(string start = LoadedStart)
    {
        var hold = new Hold(start);
        Volatile.Write(ref _hold, hold);
        return hold;
    }

    private string ClipboardDir => _dir.File("clipboard");

    private int Changes => Volatile.Read(ref _changes);

    private List<string> Reports()
    {
        lock (_reports)
        {
            return _reports.Select(report => report.Message).ToList();
        }
    }

    private List<(string Message, Exception? Error)> ReportsWithErrors()
    {
        lock (_reports)
        {
            return _reports.ToList();
        }
    }

    private int Loads() => Reports().Count(report => report.StartsWith(LoadedStart, StringComparison.Ordinal));

    private List<string> Notes()
    {
        lock (_notes)
        {
            return _notes.ToList();
        }
    }

    private static ClipboardOptions Options(string device = Device, int days = 90) => ClipboardOptions.From(
        new BeaconSettings
        {
            DeviceName = JsonSerializer.SerializeToElement(device),
            ClipboardRetentionDays = JsonSerializer.SerializeToElement(days),
        },
        "UNUSED-PC");

    private Task Idle() => _service.Idle.WaitAsync(Limit);

    private async Task Configure(string? clipboardDir = null)
    {
        _service.Configure(clipboardDir ?? ClipboardDir, Options());
        await Idle();
    }

    /// <summary>Writes a day file as another machine would have, straight to disk.</summary>
    private void WriteDay(string device, DateOnly day, params ClipRecord[] records)
    {
        string folder = Path.Combine(ClipboardDir, device);
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, ClipPaths.DayFileName(day)), records.Select(ClipLine.Write));
    }

    private static EntryRecord TextRecord(string id, string text, int hour = 9, int daysAgo = 0) =>
        new(id, new DateTimeOffset(2026, 10, 3 - daysAgo, hour, 0, 0, TimeSpan.Zero), "notepad.exe") { Text = text, TextLength = text.Length };

    private string DayFile(DateOnly day, string device = Device) => Path.Combine(ClipboardDir, device, ClipPaths.DayFileName(day));

    /// <summary>Holds a file open the way a virus scanner does: everyone else may read, write and delete, and nobody can replace it.</summary>
    private static FileStream HoldShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Makes a file longer without writing anything: what the file system gives a file that is made longer is zeros that are never stored.</summary>
    private static void GrowTo(string file, long length)
    {
        using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private async Task<ClipEntry> Add(string text)
    {
        ClipEntry? added = null;
        _service.Post(store => added = store.Add(new ClipDraft("notepad.exe") { Text = text }));
        await Idle();
        return added!;
    }

    [Fact]
    public async Task Before_it_is_configured_there_is_no_history_and_posted_work_is_not_run()
    {
        bool ran = false;

        _service.Post(_ => ran = true);
        await Idle();

        Assert.Empty(_service.Current);
        Assert.False(_service.IsWriter);
        Assert.False(ran);
        Assert.Equal(0, Changes);
    }

    [Fact]
    public async Task Configure_takes_the_folder_reads_what_is_there_and_says_that_things_changed()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));

        await Configure();

        Assert.True(_service.IsWriter);
        Assert.Equal(["from the other machine"], _service.Current.Select(entry => entry.Text));
        Assert.True(Changes >= 1);
        Assert.Contains(Reports(), report => report.StartsWith("Clipboard history loaded: 1 entries from 1 day files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Work_is_done_in_the_background_one_piece_at_a_time_in_the_order_it_was_asked_for()
    {
        await Configure();
        var order = new List<int>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        _service.Post(_ =>
        {
            lock (order)
            {
                order.Add(1);
            }

            started.Set();
            release.Wait(Limit);
        });
        for (int i = 2; i <= 5; i++)
        {
            int number = i;
            _service.Post(_ =>
            {
                lock (order)
                {
                    order.Add(number);
                }
            });
        }

        // The first piece is running and held, and this thread was not kept waiting for it.
        Assert.True(started.Wait(Limit));
        Assert.False(_service.Idle.IsCompleted);
        lock (order)
        {
            Assert.Equal([1], order);
        }

        release.Set();
        await Idle();

        Assert.Equal([1, 2, 3, 4, 5], order);
    }

    [Fact]
    public async Task Work_that_changes_the_history_is_told_and_work_that_does_not_is_not()
    {
        await Configure();
        int before = Changes;

        _service.Post(_ => { });
        await Idle();
        Assert.Equal(before, Changes);

        await Add("an otter");

        Assert.Equal(before + 1, Changes);
        Assert.Equal(["an otter"], _service.Current.Select(entry => entry.Text));
    }

    [Fact]
    public async Task A_piece_of_work_that_throws_is_reported_and_the_next_one_still_runs()
    {
        await Configure();

        _service.Post(_ => throw new InvalidOperationException("private words from whatever failed"));
        await Add("an otter");

        Assert.Contains("A piece of work on the clipboard history failed", Reports());
        Assert.Single(_service.Current);
        Assert.All(Reports(), report => Assert.DoesNotContain("private words", report));
    }

    [Fact]
    public async Task Delete_takes_the_entry_out_and_says_nothing()
    {
        await Configure();
        var kept = await Add("kept");
        var gone = await Add("gone");

        _service.Delete(gone.Id);
        await Idle();

        Assert.Equal([kept.Id], _service.Current.Select(entry => entry.Id));
        Assert.Empty(Notes());
    }

    [Fact]
    public async Task Deleting_an_entry_that_is_not_there_says_nothing()
    {
        await Configure();

        _service.Delete("no-such-id");
        await Idle();

        Assert.Empty(Notes());
    }

    [Fact]
    public async Task A_delete_that_cannot_be_written_is_reported_and_the_owner_is_told()
    {
        await Configure();
        var entry = await Add("an otter");

        // Tomorrow's file cannot be made: a folder is in its place. The entry's own day is today.
        _clock.Advance(TimeSpan.FromDays(1));
        Directory.CreateDirectory(Path.Combine(ClipboardDir, Device, ClipPaths.DayFileName(Today.AddDays(1))));
        _service.Delete(entry.Id);
        await Idle();

        Assert.Equal([ClipHistoryService.NotDeletedNote], Notes());
        Assert.Contains("A clipboard entry could not be deleted", Reports());
        Assert.Single(_service.Current);
    }

    [Fact]
    public async Task Clear_empties_the_history_and_says_so()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await Configure();
        await Add("an otter");

        _service.Clear();
        await Idle();

        // The entry of the other machine is hidden and its files are not this machine's to remove,
        // which is said too (once per run).
        Assert.Empty(_service.Current);
        Assert.Equal([ClipHistoryService.ClearedNote, ClipHistoryService.ForeignFilesNote], Notes());
    }

    [Fact]
    public async Task A_clear_that_cannot_be_written_is_reported_and_the_owner_is_told()
    {
        await Configure();
        await Add("an otter");
        _clock.Advance(TimeSpan.FromDays(1));
        Directory.CreateDirectory(Path.Combine(ClipboardDir, Device, ClipPaths.DayFileName(Today.AddDays(1))));

        _service.Clear();
        await Idle();

        Assert.Equal([ClipHistoryService.NotClearedNote], Notes());
        Assert.Contains("The clipboard history could not be cleared", Reports());
        Assert.Single(_service.Current);
    }

    [Fact]
    public async Task A_copy_of_Beacon_that_does_not_hold_the_folder_reads_the_history_and_says_why_it_cannot_change_it()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        using var other = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(other.IsWriter);

        await Configure();
        _service.Delete("w1");
        _service.Clear();
        await Idle();

        Assert.False(_service.IsWriter);
        Assert.Equal(["from the other machine"], _service.Current.Select(entry => entry.Text));
        Assert.Equal([ClipHistoryService.CannotWriteNote, ClipHistoryService.CannotWriteNote], Notes());
    }

    [Fact]
    public async Task Reload_reads_what_another_machine_has_written_since()
    {
        await Configure();
        Assert.Empty(_service.Current);
        int before = Changes;

        WriteDay("WALRUS-PC", Today, TextRecord("w1", "arrived by sync"));
        _service.Reload();
        await Idle();

        Assert.Equal(["arrived by sync"], _service.Current.Select(entry => entry.Text));
        Assert.Equal(before + 1, Changes);
    }

    [Fact]
    public async Task Once_a_day_the_history_is_read_again_which_removes_expired_days()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "old"));
        await Configure();
        var timer = Assert.Single(_time.Timers);
        Assert.Equal(ClipHistoryService.DailyPeriod, timer.Due);
        Assert.Single(_service.Current);

        _clock.Advance(TimeSpan.FromDays(100));
        timer.Fire();
        await Idle();

        Assert.Empty(_service.Current);
        Assert.False(File.Exists(Path.Combine(ClipboardDir, "WALRUS-PC", ClipPaths.DayFileName(Today))));
    }

    [Fact]
    public async Task Configuring_again_for_the_same_folder_lets_go_of_it_first_and_takes_it_again()
    {
        await Configure();
        await Add("an otter");

        await Configure();

        Assert.True(_service.IsWriter);
        Assert.Equal(["an otter"], _service.Current.Select(entry => entry.Text));
    }

    [Fact]
    public async Task Configuring_for_another_folder_lets_go_of_the_first()
    {
        await Configure();
        await Add("in the first folder");

        await Configure(_dir.File("elsewhere"));

        Assert.True(_service.IsWriter);
        Assert.Empty(_service.Current);
        using var first = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(first.IsWriter);
    }

    [Fact]
    public async Task Dispose_lets_go_of_the_folder_stops_the_timer_and_later_calls_do_nothing()
    {
        await Configure();
        bool ran = false;

        _service.Dispose();
        _service.Dispose();
        _service.Post(_ => ran = true);
        _service.Configure(ClipboardDir, Options());
        _service.Reload();
        _service.Delete("anything");
        _service.Clear();
        await Idle();

        Assert.False(ran);
        Assert.False(_service.IsWriter);
        Assert.True(Assert.Single(_time.Timers).Disposed);
        using var next = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(next.IsWriter);
    }

    [Fact]
    public async Task Callbacks_that_throw_do_not_stop_the_work()
    {
        using var service = new ClipHistoryService(
            _clock,
            (_, _) => throw new InvalidOperationException("the log is broken"),
            _ => throw new InvalidOperationException("the tray is broken"),
            () => throw new InvalidOperationException("the view is broken"),
            _time);

        service.Configure(ClipboardDir, Options());
        service.Post(store => store.Add(new ClipDraft(null) { Text = "an otter" }));
        service.Post(_ => throw new InvalidOperationException("the work is broken"));
        service.Clear();
        await service.Idle.WaitAsync(Limit);

        Assert.True(service.IsWriter);
        Assert.Empty(service.Current);
    }

    // ---- Reading never waits for the disk

    [Fact]
    public async Task IsWriter_and_Current_answer_while_the_background_task_is_inside_the_store()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await Configure();
        using var hold = HoldAt();

        _service.Reload();
        Assert.True(hold.Entered.Wait(Limit));
        try
        {
            // The store is saying that it has loaded while it holds its own lock. Asked from
            // another thread, so that a wait for that lock is a failure of this test (it times
            // out) and not a hang of it.
            var answers = Task.Run(() => (_service.IsWriter, _service.Current.Count));
            Assert.Equal((true, 1), await answers.WaitAsync(Limit));
            Assert.False(_service.Idle.IsCompleted);
        }
        finally
        {
            hold.Release.Set();
        }

        await Idle();
    }

    // ---- A reload does not show an empty history

    [Fact]
    public async Task A_reload_keeps_showing_the_old_history_until_the_new_one_is_ready()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "first"));
        WriteDay("WALRUS-PC", Today.AddDays(-1), TextRecord("w0", "second", daysAgo: 1));
        await Configure();
        var old = _service.Current;
        Assert.Equal(["first", "second"], old.Select(entry => entry.Text));
        int before = Changes;

        // The older day cannot be read this time. The store says so before it has anything to show.
        using var locked = new FileStream(DayFile(Today.AddDays(-1), "WALRUS-PC"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var hold = HoldAt(DayNotRead);
        _service.Configure(ClipboardDir, Options());
        Assert.True(hold.Entered.Wait(Limit));
        try
        {
            Assert.Same(old, _service.Current);
            Assert.True(_service.IsWriter);
            Assert.Equal(before, Changes);
        }
        finally
        {
            hold.Release.Set();
        }

        await Idle();

        Assert.Equal(["first"], _service.Current.Select(entry => entry.Text));
        Assert.True(_service.IsWriter);
        Assert.Equal(before + 1, Changes);
    }

    [Fact]
    public async Task The_first_configure_goes_from_nothing_to_the_loaded_history_in_one_change()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "first"));
        using var hold = HoldAt();

        _service.Configure(ClipboardDir, Options());
        Assert.True(hold.Entered.Wait(Limit));
        try
        {
            Assert.Empty(_service.Current);
            Assert.False(_service.IsWriter);
            Assert.Equal(0, Changes);
        }
        finally
        {
            hold.Release.Set();
        }

        await Idle();

        Assert.Equal(["first"], _service.Current.Select(entry => entry.Text));
        Assert.True(_service.IsWriter);
        Assert.Equal(1, Changes);
    }

    // ---- Nothing is said after Dispose

    [Fact]
    public async Task Nothing_is_said_about_a_delete_that_was_in_flight_when_Dispose_came()
    {
        await Configure();
        var entry = await Add("an otter");
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);
        int before = Changes;

        // The store says that the day is full, and while it is saying so the service is
        // disposed: the delete then finds the store it was given already let go of, which looks
        // like a copy of Beacon that does not hold the folder.
        _seen = message =>
        {
            if (message == DayIsFull)
            {
                _service.Dispose();
            }
        };
        _service.Delete(entry.Id);
        await Idle();

        Assert.Empty(Notes());
        Assert.Equal(before, Changes);
        Assert.False(_service.IsWriter);
    }

    [Fact]
    public async Task Nothing_is_said_about_a_clear_that_was_in_flight_when_Dispose_came()
    {
        await Configure();
        await Add("an otter");
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);
        int before = Changes;
        _seen = message =>
        {
            if (message == DayIsFull)
            {
                _service.Dispose();
            }
        };

        _service.Clear();
        await Idle();

        Assert.Empty(Notes());
        Assert.Equal(before, Changes);
    }

    [Fact]
    public async Task A_change_made_by_a_piece_that_was_running_when_Dispose_came_is_not_announced()
    {
        await Configure();
        int before = Changes;

        _service.Post(store =>
        {
            store.Add(new ClipDraft("notepad.exe") { Text = "an otter" });
            _service.Dispose();
        });
        await Idle();

        Assert.Equal(before, Changes);
        Assert.Empty(Notes());
        Assert.False(_service.IsWriter);
    }

    // ---- A store that cannot be built leaves no store

    [Fact]
    public async Task A_store_that_cannot_be_built_leaves_no_store_and_the_owner_is_told()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await Configure();
        int before = Changes;

        // A path with a character that no path can have: the store cannot even be made.
        _service.Configure(ClipboardDir + "\0", Options());
        await Idle();
        bool ran = false;
        _service.Post(_ => ran = true);
        await Idle();

        Assert.Empty(_service.Current);
        Assert.False(_service.IsWriter);
        Assert.False(ran);
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
        Assert.Equal(before + 1, Changes);
        var failure = Assert.Single(ReportsWithErrors(), report => report.Message == "The clipboard history could not be opened");
        Assert.NotNull(failure.Error);
        Assert.DoesNotContain("A piece of work on the clipboard history failed", Reports());

        // The folder of the store that was let go of is free, and the service opens again when asked.
        using (var other = new ClipStore(ClipboardDir, Device, 90, _clock))
        {
            Assert.True(other.IsWriter);
        }

        await Configure();
        Assert.True(_service.IsWriter);
        Assert.Equal(["from the other machine"], _service.Current.Select(entry => entry.Text));
    }

    [Fact]
    public async Task A_store_whose_load_throws_is_let_go_of_and_leaves_no_store()
    {
        await Configure();
        await Add("an otter");
        int before = Changes;

        // The load asks the clock for the day, which is not about a file.
        _shaky.Fails = true;
        _service.Configure(ClipboardDir, Options());
        await Idle();
        _shaky.Fails = false;
        bool ran = false;
        _service.Post(_ => ran = true);
        await Idle();

        Assert.Empty(_service.Current);
        Assert.False(_service.IsWriter);
        Assert.False(ran);
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
        Assert.Equal(before + 1, Changes);
        var failure = Assert.Single(ReportsWithErrors(), report => report.Message == "The clipboard history could not be opened");
        Assert.IsType<InvalidOperationException>(failure.Error);
        using (var other = new ClipStore(ClipboardDir, Device, 90, _clock))
        {
            Assert.True(other.IsWriter);
        }

        await Configure();
        Assert.True(_service.IsWriter);
        Assert.Equal(["an otter"], _service.Current.Select(entry => entry.Text));
    }

    // ---- A delete or a clear that fails in any way tells the owner

    [Fact]
    public async Task A_delete_that_fails_in_any_way_is_reported_with_its_exception_and_the_owner_is_told()
    {
        await Configure();
        var entry = await Add("an otter");

        // The store asks the clock for the time of the delete record, which is not about a file.
        _shaky.Fails = true;
        _service.Delete(entry.Id);
        await Idle();
        _shaky.Fails = false;

        Assert.Equal([ClipHistoryService.NotDeletedNote], Notes());
        var failure = Assert.Single(ReportsWithErrors(), report => report.Message == "A clipboard entry could not be deleted");
        Assert.IsType<InvalidOperationException>(failure.Error);
        Assert.DoesNotContain("A piece of work on the clipboard history failed", Reports());
        Assert.Single(_service.Current);
    }

    [Fact]
    public async Task A_clear_that_fails_in_any_way_is_reported_with_its_exception_and_the_owner_is_told()
    {
        await Configure();
        await Add("an otter");

        _shaky.Fails = true;
        _service.Clear();
        await Idle();
        _shaky.Fails = false;

        Assert.Equal([ClipHistoryService.NotClearedNote], Notes());
        var failure = Assert.Single(ReportsWithErrors(), report => report.Message == "The clipboard history could not be cleared");
        Assert.IsType<InvalidOperationException>(failure.Error);
        Assert.DoesNotContain("A piece of work on the clipboard history failed", Reports());
        Assert.Single(_service.Current);
    }

    // ---- Work and Dispose, in order

    [Fact]
    public async Task Work_posted_before_a_configure_runs_against_the_old_store_and_work_posted_after_it_against_the_new_one()
    {
        await Configure();
        var devices = new List<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        _service.Post(store =>
        {
            lock (devices)
            {
                devices.Add(store.Device);
            }

            started.Set();
            release.Wait(Limit);
        });
        _service.Configure(ClipboardDir, Options("SEAL-PC"));
        _service.Post(store =>
        {
            lock (devices)
            {
                devices.Add(store.Device);
            }
        });
        Assert.True(started.Wait(Limit));
        release.Set();
        await Idle();

        Assert.Equal([Device, "SEAL-PC"], devices);
    }

    [Fact]
    public async Task The_daily_reading_runs_in_its_turn_after_the_work_that_is_waiting_and_once()
    {
        await Configure();
        var timer = Assert.Single(_time.Timers);
        var order = new List<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _seen = message =>
        {
            if (message.StartsWith(LoadedStart, StringComparison.Ordinal))
            {
                lock (order)
                {
                    order.Add("reading");
                }
            }
        };

        _service.Post(_ =>
        {
            lock (order)
            {
                order.Add("1");
            }

            started.Set();
            release.Wait(Limit);
        });
        foreach (string name in new[] { "2", "3" })
        {
            _service.Post(_ =>
            {
                lock (order)
                {
                    order.Add(name);
                }
            });
        }

        Assert.True(started.Wait(Limit));

        // The day is over while those wait, and more work comes after it.
        timer.Fire();
        _service.Post(_ =>
        {
            lock (order)
            {
                order.Add("4");
            }
        });
        release.Set();
        await Idle();

        Assert.Equal(["1", "2", "3", "reading", "4"], order);
    }

    [Fact]
    public async Task Dispose_waits_for_the_piece_that_is_running_drops_the_pieces_that_wait_and_a_second_Dispose_does_nothing()
    {
        await Configure();
        int before = Changes;
        var order = new List<string>();
        bool returned = false;
        using var hold = HoldAt();

        // The piece that runs is inside the store, which holds its own lock, and two wait.
        _service.Post(store =>
        {
            store.Load();
            Volatile.Write(ref returned, true);
        });
        foreach (string name in new[] { "2", "3" })
        {
            _service.Post(_ =>
            {
                lock (order)
                {
                    order.Add(name);
                }
            });
        }

        Assert.True(hold.Entered.Wait(Limit));
        var disposing = Task.Run(_service.Dispose);

        // The timer is let go of after the waiting pieces are dropped and before the wait for the store.
        Assert.True(_watched.AnyDisposed.Wait(Limit));
        Assert.False(disposing.IsCompleted);
        hold.Release.Set();
        await disposing.WaitAsync(Limit);
        await Idle();

        Assert.True(Volatile.Read(ref returned));
        Assert.Empty(order);

        // What was dropped is said, once, with its count, and the second Dispose does not say it again.
        Assert.Single(Reports(), report => report == DroppedStart + 2);
        Assert.False(_service.IsWriter);
        using (var next = new ClipStore(ClipboardDir, Device, 90, _clock))
        {
            Assert.True(next.IsWriter);
        }

        _service.Dispose();
        await Idle();

        Assert.Equal(before, Changes);
        Assert.Empty(Notes());
        Assert.True(Assert.Single(_time.Timers).Disposed);
        Assert.Single(Reports(), report => report == DroppedStart + 2);
    }

    [Fact]
    public async Task A_Dispose_that_comes_while_a_configure_is_loading_lets_go_of_the_new_store()
    {
        using var hold = HoldAt();

        _service.Configure(ClipboardDir, Options());
        Assert.True(hold.Entered.Wait(Limit));
        var disposing = Task.Run(_service.Dispose);
        Assert.True(_watched.AnyDisposed.Wait(Limit));
        Assert.False(disposing.IsCompleted);
        hold.Release.Set();
        await disposing.WaitAsync(Limit);
        await Idle();

        Assert.False(_service.IsWriter);
        Assert.Empty(_service.Current);
        Assert.Equal(0, Changes);
        using var next = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(next.IsWriter);
    }

    [Fact]
    public async Task A_Dispose_that_comes_while_a_configure_is_taking_the_folder_closes_the_new_store_on_the_spot()
    {
        // Another copy holds the folder at first and lets go during the first pause of the new
        // store, and the service is disposed during that same pause: the store that takes the
        // folder next is one nobody is waiting for.
        string lockFile = Path.Combine(ClipboardDir, Device, ClipStore.LockFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using var holder = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        _onPause = _ =>
        {
            holder.Dispose();
            _service.Dispose();
        };

        _service.Configure(ClipboardDir, Options());
        await Idle();

        Assert.False(_service.IsWriter);
        Assert.Equal(0, Changes);
        using var next = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(next.IsWriter);
    }

    // ---- A delete whose files are still on disk is said, and tried again

    [Fact]
    public async Task A_delete_whose_files_are_still_on_disk_is_said_once_and_tried_again_after_a_minute_and_then_after_longer_waits_until_it_works()
    {
        await Configure();
        var entry = await Add("walrus");
        Assert.Single(_time.Timers);
        using var held = HoldShared(DayFile(Today));

        _service.Delete(entry.Id);
        await Idle();

        // Gone from the list, still on the disk, said once, and a retry is waiting.
        Assert.Equal([ClipHistoryService.ScrubOwedNote], Notes());
        Assert.Empty(_service.Current);
        Assert.Contains("walrus", File.ReadAllText(DayFile(Today)));
        Assert.Equal(2, _time.Timers.Count);
        var retry = _time.Timers[1];
        Assert.Equal(TimeSpan.FromMinutes(1), retry.Due);

        // A minute later, with the file still held: it was tried, nothing more is said, and it is waiting again, for twice as long.
        int loads = Loads();
        retry.Fire();
        await Idle();
        Assert.Equal(loads + 1, Loads());
        Assert.Equal([ClipHistoryService.ScrubOwedNote], Notes());
        Assert.Equal(TimeSpan.FromMinutes(2), retry.Due);
        Assert.Contains("walrus", File.ReadAllText(DayFile(Today)));

        // Let go of, and a minute later: the line is gone, and no retry is waiting.
        held.Dispose();
        retry.Fire();
        await Idle();
        Assert.Equal(loads + 2, Loads());
        Assert.DoesNotContain("walrus", File.ReadAllText(DayFile(Today)));
        Assert.Equal([ClipHistoryService.ScrubOwedNote], Notes());
        Assert.Null(retry.Due);

        // Nothing more is tried, and no timer is made for it.
        retry.Fire();
        await Idle();
        Assert.Equal(loads + 2, Loads());
        Assert.Null(retry.Due);
        Assert.Equal(2, _time.Timers.Count);
    }

    [Fact]
    public async Task A_scrub_that_the_next_write_makes_good_stops_the_retries_and_a_new_failure_is_said_again()
    {
        await Configure();
        var walrus = await Add("walrus");
        using var held = HoldShared(DayFile(Today));
        _service.Delete(walrus.Id);
        await Idle();
        Assert.Equal([ClipHistoryService.ScrubOwedNote], Notes());
        var retry = _time.Timers[1];
        Assert.Equal(TimeSpan.FromMinutes(1), retry.Due);

        // The next copy ends with a scrub that works, and nothing is owed any more.
        held.Dispose();
        var seal = await Add("seal");
        Assert.Null(retry.Due);
        Assert.Equal([ClipHistoryService.ScrubOwedNote], Notes());

        // Something that becomes owed again is said again, and uses the same timer.
        using var again = HoldShared(DayFile(Today));
        _service.Delete(seal.Id);
        await Idle();
        Assert.Equal([ClipHistoryService.ScrubOwedNote, ClipHistoryService.ScrubOwedNote], Notes());
        Assert.Equal(TimeSpan.FromMinutes(1), retry.Due);
        Assert.Equal(2, _time.Timers.Count);
    }

    [Fact]
    public async Task A_copy_that_cannot_write_says_nothing_about_a_scrub_and_waits_for_no_retry()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        using var other = new ClipStore(ClipboardDir, Device, 90, _clock);
        await Configure();

        _service.Delete("w1");
        await Idle();

        Assert.False(_service.IsWriter);
        Assert.Equal([ClipHistoryService.CannotWriteNote], Notes());
        Assert.Single(_time.Timers);
    }

    [Fact]
    public async Task Nothing_is_said_or_waited_for_about_a_scrub_after_Dispose()
    {
        await Configure();
        var entry = await Add("walrus");
        using var held = HoldShared(DayFile(Today));
        int before = Changes;

        // The store says that it could not rewrite the day, and while it is saying so the service is disposed.
        _seen = message =>
        {
            if (message == DayNotRewritten)
            {
                _service.Dispose();
            }
        };
        _service.Delete(entry.Id);
        await Idle();

        Assert.Empty(Notes());
        Assert.Single(_time.Timers);
        Assert.Equal(before, Changes);
    }

    [Fact]
    public async Task Dispose_stops_the_retry_and_a_firing_that_was_already_on_its_way_does_nothing()
    {
        await Configure();
        var entry = await Add("walrus");
        using var held = HoldShared(DayFile(Today));
        _service.Delete(entry.Id);
        await Idle();
        var retry = _time.Timers[1];
        Assert.Equal(TimeSpan.FromMinutes(1), retry.Due);
        int loads = Loads();

        _service.Dispose();
        retry.Fire();
        await Idle();

        Assert.True(retry.Disposed);
        Assert.Null(retry.Due);
        Assert.Equal(loads, Loads());
    }

    // ---- Closing: what the owner asked for is carried out first, and what is dropped is counted

    private const string DroppedStart = "Pieces of work on the clipboard history that were still waiting when Beacon closed were dropped: ";

    /// <summary>Starts a drain on a thread of its own, after taking the permits that earlier timers left, so that <see cref="WaitingTimerOf"/> sees only its own.</summary>
    private Task<bool> StartDrain(TimeSpan limit)
    {
        while (_watched.Made.Wait(0))
        {
        }

        return Task.Run(() => _service.Drain(limit));
    }

    /// <summary>Waits until the drain has made its timer, which is when it is waiting for the line, and gives that timer. Fails when the drain was over before it waited.</summary>
    private async Task<ManualTimer> WaitingTimerOf(Task<bool> drain)
    {
        var made = _watched.Made.WaitAsync(Limit);
        Assert.Same(made, await Task.WhenAny(made, drain));
        Assert.True(await made);
        return _time.Timers[^1];
    }

    /// <summary>Runs a piece that holds the line until the returned event is set, and gives once it is running.</summary>
    private ManualResetEventSlim HoldTheLine()
    {
        var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        _service.Post(_ =>
        {
            started.Set();
            release.Wait(Limit);
        });
        Assert.True(started.Wait(Limit));
        return release;
    }

    [Fact]
    public async Task A_delete_that_waits_behind_a_piece_that_is_held_is_carried_out_when_Drain_comes_before_Dispose()
    {
        await Configure();
        await Add("kept");
        var gone = await Add("gone");
        using var release = HoldTheLine();
        _service.Delete(gone.Id);

        var drain = StartDrain(Limit);
        await WaitingTimerOf(drain);
        release.Set();
        Assert.True(await drain.WaitAsync(Limit));
        _service.Dispose();

        // On the disk: the record, and the line it took out.
        var load = ClipReader.Load(ClipboardDir);
        Assert.Contains(gone.Id, load.Deleted);
        Assert.Equal(["kept"], load.Entries.Select(entry => entry.Text));
        Assert.DoesNotContain("gone", File.ReadAllText(DayFile(Today)));
        Assert.DoesNotContain(Reports(), report => report.StartsWith(DroppedStart, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clear_that_waits_behind_a_piece_that_is_held_is_carried_out_when_Drain_comes_before_Dispose()
    {
        await Configure();
        await Add("an otter");
        using var release = HoldTheLine();
        _service.Clear();

        var drain = StartDrain(Limit);
        await WaitingTimerOf(drain);
        release.Set();
        Assert.True(await drain.WaitAsync(Limit));
        _service.Dispose();

        var load = ClipReader.Load(ClipboardDir);
        Assert.NotNull(load.ClearedBefore);
        Assert.Empty(load.Entries);
        Assert.DoesNotContain("an otter", File.ReadAllText(DayFile(Today)));
        Assert.Equal([ClipHistoryService.ClearedNote], Notes());
    }

    [Fact]
    public async Task Drain_answers_false_at_its_limit_when_the_line_stays_busy_and_the_work_that_was_waiting_is_not_lost_by_it()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await Configure();
        using var hold = HoldAt();
        _service.Reload();
        Assert.True(hold.Entered.Wait(Limit));
        var waiting = new List<int>();
        _service.Post(_ => waiting.Add(1));

        // The piece that runs is inside the store, which holds its own lock: the drain must not need it.
        var drain = StartDrain(TimeSpan.FromSeconds(5));
        var timer = await WaitingTimerOf(drain);
        Assert.Equal(TimeSpan.FromSeconds(5), timer.Due);
        Assert.False(drain.IsCompleted);
        timer.Fire();

        Assert.False(await drain.WaitAsync(Limit));
        Assert.False(_service.Idle.IsCompleted);
        Assert.Empty(waiting);

        // It only waited: once the line is let go, the piece that was waiting still runs.
        hold.Release.Set();
        await Idle();
        Assert.Equal([1], waiting);
    }

    [Fact]
    public async Task Drain_answers_true_at_once_and_makes_no_timer_when_the_line_is_idle()
    {
        await Configure();
        int timers = _time.Timers.Count;

        Assert.True(_service.Drain(TimeSpan.FromSeconds(5)));
        Assert.True(_service.Drain(TimeSpan.Zero));
        Assert.True(_service.Drain(TimeSpan.FromSeconds(-1)));

        Assert.Equal(timers, _time.Timers.Count);
    }

    [Fact]
    public async Task Drain_with_no_time_to_wait_answers_false_at_once_while_the_line_is_busy()
    {
        await Configure();
        using var release = HoldTheLine();
        int timers = _time.Timers.Count;

        Assert.False(_service.Drain(TimeSpan.Zero));
        Assert.False(_service.Drain(TimeSpan.FromSeconds(-1)));

        Assert.Equal(timers, _time.Timers.Count);
        release.Set();
        await Idle();
    }

    [Fact]
    public async Task Drain_answers_true_when_the_line_goes_idle_before_its_limit()
    {
        await Configure();
        using var release = HoldTheLine();

        var drain = StartDrain(TimeSpan.FromSeconds(5));
        var timer = await WaitingTimerOf(drain);
        release.Set();

        Assert.True(await drain.WaitAsync(Limit));

        // The timer was let go of, not left to fire later. The wait inside Drain answers first
        // and lets go of its timer straight afterwards, on the thread that finished the line's
        // work. So this waits for the timer to have been let go of: looking once, at the very
        // moment Drain has answered, can come a few instructions too early.
        Assert.True(SpinWait.SpinUntil(() => timer.Disposed, Limit), "The drain's timer was not let go of.");
    }

    [Fact]
    public async Task Dispose_says_how_many_pieces_of_work_it_dropped()
    {
        await Configure();
        using var release = HoldTheLine();
        _service.Post(_ => { });
        _service.Delete("an id");
        _service.Reload();

        _service.Dispose();
        release.Set();
        await Idle();

        Assert.Single(Reports(), report => report == DroppedStart + 3);
        Assert.Empty(Notes());
    }

    [Fact]
    public async Task Dispose_with_nothing_waiting_says_nothing_about_dropped_work()
    {
        await Configure();
        await Add("an otter");

        _service.Dispose();
        await Idle();

        Assert.DoesNotContain(Reports(), report => report.StartsWith(DroppedStart, StringComparison.Ordinal));
    }

    // ---- With no history in use, and with a full day, the owner is told

    [Fact]
    public void The_sentence_for_a_history_that_is_not_open_is_fixed()
    {
        Assert.Equal("The clipboard history is not open, so nothing was changed.", ClipHistoryService.NotOpenNote);
    }

    [Fact]
    public async Task A_delete_with_no_history_open_tells_the_owner_and_so_does_a_clear()
    {
        _service.Delete("an id");
        await Idle();
        Assert.Equal([ClipHistoryService.NotOpenNote], Notes());

        _service.Clear();
        await Idle();
        Assert.Equal([ClipHistoryService.NotOpenNote, ClipHistoryService.NotOpenNote], Notes());
        Assert.Equal(0, Changes);
    }

    [Fact]
    public async Task A_delete_and_a_clear_after_the_history_could_not_be_opened_tell_the_owner_it_is_not_open()
    {
        await Configure();
        await Add("an otter");
        _service.Configure(ClipboardDir + "\0", Options());
        await Idle();
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());

        _service.Delete("an id");
        _service.Clear();
        await Idle();

        Assert.Equal([ClipHistoryService.NotOpenedNote, ClipHistoryService.NotOpenNote, ClipHistoryService.NotOpenNote], Notes());
    }

    [Fact]
    public async Task A_delete_that_is_queued_before_the_first_configure_is_not_told_as_not_open_because_the_configure_runs_first()
    {
        _service.Configure(ClipboardDir, Options());
        _service.Delete("an id that is not there");
        await Idle();

        Assert.Empty(Notes());
        Assert.True(_service.IsWriter);
    }

    [Fact]
    public async Task A_delete_after_Dispose_says_nothing_not_even_that_the_history_is_not_open()
    {
        _service.Dispose();

        _service.Delete("an id");
        _service.Clear();
        await Idle();

        Assert.Empty(Notes());
    }

    [Fact]
    public async Task A_delete_that_the_store_refuses_because_the_day_is_full_tells_the_owner_with_the_failure_in_the_log()
    {
        await Configure();
        var entry = await Add("an otter");
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        _service.Delete(entry.Id);
        await Idle();

        Assert.Equal([ClipHistoryService.NotDeletedNote], Notes());
        var failure = Assert.Single(ReportsWithErrors(), report => report.Message == "A clipboard entry could not be deleted");
        Assert.IsType<IOException>(failure.Error);
        Assert.Single(_service.Current);
    }

    [Fact]
    public async Task A_clear_that_the_store_refuses_because_the_day_is_full_tells_the_owner_it_could_not_be_cleared()
    {
        await Configure();
        await Add("an otter");
        GrowTo(DayFile(Today), ClipLimits.MaxDayFileBytes - 10);

        _service.Clear();
        await Idle();

        Assert.Equal([ClipHistoryService.NotClearedNote], Notes());
        var failure = Assert.Single(ReportsWithErrors(), report => report.Message == "The clipboard history could not be cleared");
        Assert.IsType<IOException>(failure.Error);
        Assert.Single(_service.Current);
    }

    // ---- What another machine's folder keeps on this disk is said once per run

    [Fact]
    public void The_sentence_for_hidden_files_of_another_machine_is_fixed()
    {
        Assert.Equal(
            "Some of what you removed was copied under another machine name. It is hidden, but its files stay on this disk until that machine removes them or they expire.",
            ClipHistoryService.ForeignFilesNote);
    }

    [Fact]
    public async Task A_delete_of_an_entry_of_another_machine_says_where_its_files_stay_once_per_run()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "first"), TextRecord("w2", "second", hour: 10));
        await Configure();

        _service.Delete("w1");
        await Idle();
        Assert.Equal([ClipHistoryService.ForeignFilesNote], Notes());

        _service.Delete("w2");
        await Idle();

        Assert.Empty(_service.Current);
        Assert.Equal([ClipHistoryService.ForeignFilesNote], Notes());
    }

    [Fact]
    public async Task A_clear_that_hides_an_entry_of_another_machine_says_so_after_it_says_that_it_cleared_and_once_per_run()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await Configure();
        await Add("an otter");

        _service.Clear();
        await Idle();
        Assert.Equal([ClipHistoryService.ClearedNote, ClipHistoryService.ForeignFilesNote], Notes());

        _service.Clear();
        await Idle();

        Assert.Equal([ClipHistoryService.ClearedNote, ClipHistoryService.ForeignFilesNote, ClipHistoryService.ClearedNote], Notes());
    }

    [Fact]
    public async Task A_delete_and_then_a_clear_say_it_once_between_them()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "first"), TextRecord("w2", "second", hour: 10));
        await Configure();

        _service.Delete("w1");
        _service.Clear();
        await Idle();

        Assert.Equal([ClipHistoryService.ForeignFilesNote, ClipHistoryService.ClearedNote], Notes());
    }

    [Fact]
    public async Task Deleting_and_clearing_what_this_machine_wrote_does_not_say_it()
    {
        await Configure();
        var gone = await Add("gone");
        await Add("an otter");

        _service.Delete(gone.Id);
        _service.Clear();
        await Idle();

        Assert.Equal([ClipHistoryService.ClearedNote], Notes());
    }

    [Fact]
    public async Task A_delete_that_could_not_be_done_does_not_say_where_files_stay()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        using var other = new ClipStore(ClipboardDir, Device, 90, _clock);
        await Configure();

        _service.Delete("w1");
        await Idle();

        Assert.Equal([ClipHistoryService.CannotWriteNote], Notes());
    }

    // ---- A history dated in the future is said once per run

    private static ClearRecord ClearAt(string id, DateTimeOffset time) => new(id, time, time);

    [Fact]
    public void The_sentence_for_a_history_dated_in_the_future_is_fixed()
    {
        Assert.Equal(
            "Some clipboard history is dated in the future, so new copies may be removed. Check this machine's clock, and the clock of your other machine.",
            ClipHistoryService.ClockSuspectNote);
    }

    [Fact]
    public async Task A_history_dated_more_than_a_day_ahead_of_the_clock_tells_the_owner_once_per_run()
    {
        WriteDay("WALRUS-PC", Today, ClearAt("w-c", _clock.UtcNow.AddDays(3)));

        await Configure();
        Assert.Equal([ClipHistoryService.ClockSuspectNote], Notes());

        _service.Reload();
        await Idle();
        await Configure();

        Assert.Equal([ClipHistoryService.ClockSuspectNote], Notes());
    }

    [Fact]
    public async Task A_history_dated_ahead_of_the_clock_that_a_reload_finds_later_is_told_then()
    {
        await Configure();
        Assert.Empty(Notes());

        WriteDay("WALRUS-PC", Today, ClearAt("w-c", _clock.UtcNow.AddDays(3)));
        _service.Reload();
        await Idle();

        Assert.Equal([ClipHistoryService.ClockSuspectNote], Notes());
    }

    [Fact]
    public async Task A_history_that_is_not_ahead_of_the_clock_tells_nothing()
    {
        WriteDay("WALRUS-PC", Today, ClearAt("w-c", _clock.UtcNow.AddHours(-1)), TextRecord("w-1", "an otter", hour: 13));

        await Configure();

        Assert.Empty(Notes());
    }

    [Fact]
    public async Task Nothing_is_told_about_the_clock_after_Dispose()
    {
        WriteDay("WALRUS-PC", Today, ClearAt("w-c", _clock.UtcNow.AddDays(3)));
        using var hold = HoldAt();
        _service.Configure(ClipboardDir, Options());
        Assert.True(hold.Entered.Wait(Limit));
        var disposing = Task.Run(_service.Dispose);
        Assert.True(_watched.AnyDisposed.Wait(Limit));
        hold.Release.Set();
        await disposing.WaitAsync(Limit);
        await Idle();

        Assert.Empty(Notes());
    }

    // ---- The retry of a scrub that is owed backs off

    [Fact]
    public async Task The_retry_of_a_scrub_that_is_owed_backs_off_from_a_minute_to_half_an_hour_and_starts_again_at_a_minute_after_it_has_worked()
    {
        await Configure();
        var entry = await Add("walrus");
        using var held = HoldShared(DayFile(Today));
        _service.Delete(entry.Id);
        await Idle();
        var retry = _time.Timers[1];

        foreach (int minutes in new[] { 1, 2, 4, 8, 16, 30, 30, 30 })
        {
            Assert.Equal(TimeSpan.FromMinutes(minutes), retry.Due);
            retry.Fire();
            await Idle();
        }

        Assert.Equal(TimeSpan.FromMinutes(30), retry.Due);

        // It works at last, and the next time something is owed the waits start again at a minute.
        held.Dispose();
        retry.Fire();
        await Idle();
        Assert.Null(retry.Due);
        var seal = await Add("seal");
        using var again = HoldShared(DayFile(Today));
        _service.Delete(seal.Id);
        await Idle();

        Assert.Equal(TimeSpan.FromMinutes(1), retry.Due);
        Assert.Equal(2, _time.Timers.Count);
    }

    [Fact]
    public async Task A_scrub_that_a_write_makes_good_in_between_starts_the_waits_again_at_a_minute()
    {
        await Configure();
        var walrus = await Add("walrus");
        using var held = HoldShared(DayFile(Today));
        _service.Delete(walrus.Id);
        await Idle();
        var retry = _time.Timers[1];
        retry.Fire();
        await Idle();
        retry.Fire();
        await Idle();
        Assert.Equal(TimeSpan.FromMinutes(4), retry.Due);

        held.Dispose();
        var seal = await Add("seal");
        Assert.Null(retry.Due);
        using var again = HoldShared(DayFile(Today));
        _service.Delete(seal.Id);
        await Idle();

        Assert.Equal(TimeSpan.FromMinutes(1), retry.Due);
    }

    // ---- A history that could not be opened is tried again, three times

    private int OpenFailures() => Reports().Count(report => report == "The clipboard history could not be opened");

    /// <summary>Configures while the clock is broken, so that the load throws, and waits for the work to be done.</summary>
    private async Task ConfigureWhileItCannotBeOpened()
    {
        _shaky.Fails = true;
        _service.Configure(ClipboardDir, Options());
        await Idle();
    }

    [Fact]
    public async Task A_history_that_could_not_be_opened_is_tried_again_after_1_2_and_4_minutes_then_given_up_and_the_owner_is_not_told_again()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await ConfigureWhileItCannotBeOpened();
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
        Assert.Equal(2, _time.Timers.Count);
        var timer = _time.Timers[1];
        Assert.Equal(TimeSpan.FromMinutes(1), timer.Due);

        foreach (var expected in new TimeSpan?[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), null })
        {
            timer.Fire();
            await Idle();
            Assert.Equal(expected, timer.Due);
        }

        // The first failure and the three tries, in the log; the owner was told once.
        Assert.Equal(4, OpenFailures());
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
        Assert.False(_service.IsWriter);

        // Nothing more is tried, and no other timer is made.
        timer.Fire();
        await Idle();
        Assert.Equal(4, OpenFailures());
        Assert.Equal(2, _time.Timers.Count);
    }

    [Fact]
    public async Task A_try_that_works_leaves_the_history_simply_there_and_says_nothing_more()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await ConfigureWhileItCannotBeOpened();
        var timer = _time.Timers[1];
        int changes = Changes;

        timer.Fire();
        await Idle();
        Assert.Equal(TimeSpan.FromMinutes(2), timer.Due);
        Assert.Equal(changes, Changes);

        _shaky.Fails = false;
        timer.Fire();
        await Idle();

        Assert.True(_service.IsWriter);
        Assert.Equal(["from the other machine"], _service.Current.Select(entry => entry.Text));
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
        Assert.Null(timer.Due);
        Assert.Equal(changes + 1, Changes);

        // Nothing is tried again, and the history works.
        int loads = Loads();
        timer.Fire();
        await Idle();
        Assert.Equal(loads, Loads());
        var added = await Add("an otter");
        Assert.NotNull(added);
    }

    [Fact]
    public async Task A_store_that_cannot_even_be_made_is_tried_again_too()
    {
        // A path with a character that no path can have: the same arguments are tried again, and fail again, quietly.
        _service.Configure(ClipboardDir + "\0", Options());
        await Idle();
        var timer = _time.Timers[1];

        timer.Fire();
        await Idle();

        Assert.Equal(TimeSpan.FromMinutes(2), timer.Due);
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
        Assert.Equal(2, OpenFailures());
    }

    [Fact]
    public async Task A_new_Configure_cancels_the_tries_and_a_try_that_was_on_its_way_does_nothing()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await ConfigureWhileItCannotBeOpened();
        var timer = _time.Timers[1];
        timer.Fire();
        await Idle();
        Assert.Equal(TimeSpan.FromMinutes(2), timer.Due);

        _shaky.Fails = false;
        _service.Configure(ClipboardDir, Options());

        // Cancelled at once, before the configure has run.
        Assert.Null(timer.Due);
        await Idle();
        Assert.True(_service.IsWriter);
        int loads = Loads();
        int failures = OpenFailures();

        timer.Fire();
        await Idle();

        Assert.Equal(loads, Loads());
        Assert.Equal(failures, OpenFailures());
        Assert.Equal([ClipHistoryService.NotOpenedNote], Notes());
    }

    [Fact]
    public async Task A_new_Configure_that_fails_too_starts_its_own_tries_at_a_minute()
    {
        await ConfigureWhileItCannotBeOpened();
        var timer = _time.Timers[1];
        timer.Fire();
        await Idle();
        Assert.Equal(TimeSpan.FromMinutes(2), timer.Due);

        _service.Configure(ClipboardDir, Options("SEAL-PC"));
        await Idle();

        Assert.Equal(TimeSpan.FromMinutes(1), timer.Due);
        Assert.Equal(2, _time.Timers.Count);
        Assert.Equal([ClipHistoryService.NotOpenedNote, ClipHistoryService.NotOpenedNote], Notes());
    }

    [Fact]
    public async Task A_try_uses_the_arguments_of_the_configure_that_failed_and_not_those_of_an_earlier_one()
    {
        await Configure();
        await Add("in the first folder");
        _shaky.Fails = true;
        _service.Configure(_dir.File("elsewhere"), Options("SEAL-PC"));
        await Idle();
        var timer = _time.Timers[1];

        _shaky.Fails = false;
        timer.Fire();
        await Idle();

        Assert.True(_service.IsWriter);
        Assert.Empty(_service.Current);
        Assert.True(Directory.Exists(Path.Combine(_dir.File("elsewhere"), "SEAL-PC")));
        Assert.False(File.Exists(Path.Combine(ClipboardDir, "SEAL-PC", ClipPaths.DayFileName(Today))));
    }

    [Fact]
    public async Task Dispose_cancels_the_tries_and_a_try_that_was_on_its_way_does_nothing()
    {
        await ConfigureWhileItCannotBeOpened();
        var timer = _time.Timers[1];
        Assert.Equal(TimeSpan.FromMinutes(1), timer.Due);
        _shaky.Fails = false;

        _service.Dispose();
        Assert.True(timer.Disposed);
        Assert.Null(timer.Due);
        timer.Fire();
        await Idle();

        Assert.False(_service.IsWriter);
        Assert.Empty(_service.Current);
        Assert.Equal(0, Loads());
        using var next = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(next.IsWriter);
    }

    [Fact]
    public async Task A_try_that_is_loading_when_Dispose_is_called_lets_go_of_the_folder()
    {
        WriteDay("WALRUS-PC", Today, TextRecord("w1", "from the other machine"));
        await ConfigureWhileItCannotBeOpened();
        var timer = _time.Timers[1];
        _shaky.Fails = false;
        using var hold = HoldAt();

        timer.Fire();
        Assert.True(hold.Entered.Wait(Limit));
        var disposing = Task.Run(_service.Dispose);
        Assert.True(_watched.AnyDisposed.Wait(Limit));
        hold.Release.Set();
        await disposing.WaitAsync(Limit);
        await Idle();

        Assert.False(_service.IsWriter);
        Assert.Empty(_service.Current);
        using var next = new ClipStore(ClipboardDir, Device, 90, _clock);
        Assert.True(next.IsWriter);
    }
}
