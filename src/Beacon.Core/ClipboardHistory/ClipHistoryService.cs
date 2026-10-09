using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Keeps the clipboard history off the UI thread. Everything that reads or writes the files is
/// put in a line and done by one background task, one piece of work at a time and in the order
/// it was asked for, so the launcher never waits for a disk and two pieces of work never meet.
/// </summary>
/// <remarks>
/// <para>The view reads <see cref="Current"/> and <see cref="IsWriter"/>, which never wait for
/// the store or for the line of work. <see cref="Current"/> is replaced in one step when a piece
/// of work has changed the history, so it sees either the old list or the new one and never a
/// mixture. When the history is configured again, the old list stays in view until the new
/// store has been made and has read the files, and is then replaced in one step.</para>
/// <para>The history is read again once a day, which is also when expired days are removed, and
/// whenever <see cref="Reload"/> is called. While a delete or a clear has not been taken off
/// this machine's disk yet, because a file was open in another program, the owner is told once,
/// and the history is read again after a minute, which tries it again, and after longer waits
/// (2, 4, 8, 16 and then 30 minutes) until it has worked.</para>
/// <para>Closing: <see cref="Drain"/> waits for the line of work to go idle, so that a delete or a
/// clear that is waiting is carried out, and <see cref="Dispose"/> drops what still waits and says
/// how many pieces that was. After <see cref="Dispose"/> nothing is said: no notification is sent
/// and the change callback is not called.</para>
/// </remarks>
public sealed class ClipHistoryService : IDisposable
{
    /// <summary>How often the history is read again by itself, so that expired days go without a restart.</summary>
    public static readonly TimeSpan DailyPeriod = TimeSpan.FromDays(1);

    /// <summary>Shown when an entry could not be deleted.</summary>
    public const string NotDeletedNote = "The clipboard entry could not be deleted. Details are in the log file.";

    /// <summary>Shown when the history was cleared.</summary>
    public const string ClearedNote = "The clipboard history was cleared.";

    /// <summary>Shown when the history could not be cleared.</summary>
    public const string NotClearedNote = "The clipboard history could not be cleared. Details are in the log file.";

    /// <summary>Shown when a change is asked of a copy of Beacon that may not write the history.</summary>
    public const string CannotWriteNote =
        "This copy of Beacon cannot change the clipboard history: another copy is using the data folder, or the folder cannot be written.";

    /// <summary>Shown when the history could not be opened at all: there is none until it has been configured again.</summary>
    public const string NotOpenedNote = "The clipboard history could not be opened. Details are in the log file.";

    /// <summary>Shown when an entry is to be deleted or the history cleared and no history is open, so that nothing was changed.</summary>
    public const string NotOpenNote = "The clipboard history is not open, so nothing was changed.";

    /// <summary>
    /// Shown once per run of Beacon, after a delete or a clear that hid entries of a folder this
    /// machine does not own: those are hidden, but their files are not this machine's to remove.
    /// </summary>
    public const string ForeignFilesNote =
        "Some of what you removed was copied under another machine name. It is hidden, but its files stay on this disk until that machine removes them or they expire.";

    /// <summary>
    /// Shown once per run of Beacon when the history holds a clear, or a record of this machine,
    /// dated more than a day ahead of the clock: a clock that was wrong. Records that far ahead
    /// make copies made now be removed again (see <see cref="ClipStore.ClockSuspect"/>).
    /// </summary>
    public const string ClockSuspectNote =
        "Some clipboard history is dated in the future, so new copies may be removed. Check this machine's clock, and the clock of your other machine.";

    /// <summary>Shown once when something deleted could not be taken off the disk, until it has been.</summary>
    public const string ScrubOwedNote = "Something deleted from the clipboard history could not be taken off the disk yet. Beacon will try again.";

    // How long after a delete that is still on the disk the history is read again, which tries the
    // scrub again: a minute at first, and longer after each try that fails, up to half an hour.
    // After a try that works, or after the scrub was made good by a write, it starts again at a minute.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(16),
        TimeSpan.FromMinutes(30),
    ];

    // How long after a history could not be opened it is tried again with the same arguments, for
    // each of the three tries.
    private static readonly TimeSpan[] ReopenDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
    ];

    private static readonly IReadOnlyList<ClipEntry> Nothing = [];

    private readonly IClock _clock;
    private readonly Action<TimeSpan>? _pause;
    private readonly string? _foldersFile;
    private readonly Action<string, Exception?> _report;
    private readonly Action<string> _notify;
    private readonly Action _changed;
    private readonly TimeProvider _time;
    private readonly ITimer _daily;
    private readonly Lock _gate = new();
    private readonly Queue<Action> _work = new();

    // The store that work is given. None from the moment the earlier store is let go of until
    // the new one has been made and has read the files, and none when that failed.
    private ClipStore? _store;

    // The store that a configure is loading, which is not yet the one in use. Dispose has to be
    // able to let go of it.
    private ClipStore? _building;

    // The store whose list Current shows, and what IsWriter says. They are the ones of the
    // store in use, and while a new store is being made they are the ones of the store it
    // replaces, so that a reload does not show an empty history or a copy that cannot write.
    private ClipStore? _shown;
    private bool _shownWriter;

    // The timer that tries a scrub that is owed again, whether it is waiting, and which of the
    // waits it is at.
    private ITimer? _retry;
    private bool _retryArmed;
    private int _retryStep;

    // Moved on by every configure: tries at opening that were made for an earlier one are of no use.
    // The arguments of the configure that failed, which are tried again, how many tries have been
    // made, and the timer for the next one.
    private int _generation;
    private (string Folder, string Device, int Days)? _reopenArgs;
    private int _reopenTries;
    private ITimer? _reopen;

    private TaskCompletionSource? _idle;
    private bool _working;
    private bool _disposed;

    // Only the one background task reads and writes this.
    private bool _owedTold;
    private bool _foreignTold;
    private bool _clockTold;

    /// <param name="report">
    /// Receives progress and problems for the log: fixed words and counts, and an exception that
    /// is logged by type only. A callback that throws is ignored.
    /// </param>
    /// <param name="notify">Receives one of this class's fixed sentences for a notification, on a background thread. A callback that throws is ignored. Never called after <see cref="Dispose"/>.</param>
    /// <param name="changed">Called on a background thread after <see cref="Current"/> or <see cref="IsWriter"/> may have changed. A callback that throws is ignored. Never called after <see cref="Dispose"/>.</param>
    /// <param name="time">Supplies the daily timer and the retry timer. Left out everywhere except in tests.</param>
    /// <param name="pause">What the store waits with between two tries at a file that is held. Left out everywhere except in tests: <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    /// <param name="foldersFile">
    /// The file that remembers which clipboard folders this machine has written
    /// (<see cref="ClipFolders"/>). The name of a folder is added to it when the folder has been
    /// taken for writing, and the folders of the earlier names in it stay this machine's to
    /// scrub. Left out, no name is remembered and only the folder of the present name is scrubbed.
    /// </param>
    public ClipHistoryService(
        IClock clock,
        Action<string, Exception?>? report = null,
        Action<string>? notify = null,
        Action? changed = null,
        TimeProvider? time = null,
        Action<TimeSpan>? pause = null,
        string? foldersFile = null)
    {
        _clock = clock;
        _pause = pause;
        _foldersFile = foldersFile;
        _report = report ?? ((_, _) => { });
        _notify = notify ?? (_ => { });
        _changed = changed ?? (() => { });
        _time = time ?? TimeProvider.System;
        _daily = _time.CreateTimer(_ => OnDay(), null, DailyPeriod, DailyPeriod);
    }

    /// <summary>
    /// The history, newest first. Empty until the first load has finished, and when the history
    /// could not be opened. Safe to read from any thread; never waits.
    /// </summary>
    public IReadOnlyList<ClipEntry> Current => Volatile.Read(ref _shown)?.Entries ?? Nothing;

    /// <summary>
    /// True when the history in use can be written by this copy of Beacon. False before
    /// <see cref="Configure"/> has done its work for the first time, when the history could not
    /// be opened, and after <see cref="Dispose"/>. Safe to read from any thread; never waits.
    /// </summary>
    public bool IsWriter => !Volatile.Read(ref _disposed) && Volatile.Read(ref _shownWriter);

    /// <summary>Completes when no work is running or waiting.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle?.Task ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Starts using the history in this clipboard folder, for this machine's name and this
    /// number of days: lets go of the folder used before, takes the new one, and reads it.
    /// Returns at once. Calling it again, with the same values or others, reads afresh; this
    /// is what the Reload command does. <see cref="Current"/> keeps the list it had until the
    /// new one has been read, and then shows the new one. When the history cannot be opened the
    /// owner is told once, and the same arguments are tried again after 1, 2 and 4 minutes, with
    /// nothing said unless it works, in which case the history is simply there (and
    /// <c>changed</c> is called). Calling this again, or <see cref="Dispose"/>, cancels those tries.
    /// </summary>
    public void Configure(string clipboardDir, ClipboardOptions options)
    {
        string device = options.DeviceName;
        int retentionDays = options.RetentionDays;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Tries at opening that an earlier configure left to be made are cancelled at once.
            generation = ++_generation;
            _reopenArgs = null;
            _reopen?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Enqueue(() => Open(clipboardDir, device, retentionDays, generation, tries: 0));
    }

    /// <summary>Reads every machine's files again and scrubs this machine's own. Returns at once. For use after files have arrived from another machine.</summary>
    public void Reload() => Post(store => store.Load());

    /// <summary>
    /// Puts a piece of work in the line. It is given the store in use, on the background task,
    /// and is not run at all when there is none.
    /// </summary>
    public void Post(Action<ClipStore> work) => Enqueue(() =>
    {
        if (Volatile.Read(ref _store) is { } store)
        {
            work(store);
        }
    });

    // Work that the owner asked for, which must not be dropped without a word: with no store in
    // use, he is told that nothing was changed.
    private void Ask(Action<ClipStore> work) => Enqueue(() =>
    {
        if (Volatile.Read(ref _store) is { } store)
        {
            work(store);
        }
        else
        {
            Notify(NotOpenNote);
        }
    });

    /// <summary>
    /// Deletes one entry. Returns at once; when it cannot be done, <c>notify</c> is told: also when
    /// no history is open, which is said as <see cref="NotOpenNote"/>.
    /// </summary>
    public void Delete(string id) => Ask(store =>
    {
        try
        {
            if (store.Delete(id))
            {
                TellWhereFilesStay(store);
            }
            else if (!store.IsWriter)
            {
                Notify(CannotWriteNote);
            }
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the owner is told: a row may be gone from view already.
            Report("A clipboard entry could not be deleted", ex);
            Notify(NotDeletedNote);
        }
    });

    /// <summary>Clears the history. Returns at once; <c>notify</c> is told how it went, also when no history is open (<see cref="NotOpenNote"/>).</summary>
    public void Clear() => Ask(store =>
    {
        try
        {
            bool cleared = store.Clear();
            Notify(cleared ? ClearedNote : CannotWriteNote);
            if (cleared)
            {
                TellWhereFilesStay(store);
            }
        }
        catch (Exception ex)
        {
            Report("The clipboard history could not be cleared", ex);
            Notify(NotClearedNote);
        }
    });

    /// <summary>
    /// For when Beacon closes, before <see cref="Dispose"/>: waits, up to <paramref name="limit"/>,
    /// for the line of work to go idle. Work that is waiting, such as a delete or a clear the owner
    /// asked for, is run and not dropped, as long as it starts within the limit: this only
    /// waits, it changes nothing about how the work is done. Called from the UI thread while the
    /// line may be running: it takes no lock the line needs. The limit is counted by the time
    /// provider given to the constructor. A limit of zero or less only asks.
    /// </summary>
    /// <returns>True when the line is idle, false when the limit ran out first. The work that is still waiting is dropped by <see cref="Dispose"/>.</returns>
    public bool Drain(TimeSpan limit)
    {
        var idle = Volatile.Read(ref _idle)?.Task ?? Task.CompletedTask;
        if (idle.IsCompleted)
        {
            return true;
        }

        if (limit <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            idle.WaitAsync(limit, _time).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stops the timers, drops the work that is waiting (and says in the log how many pieces
    /// that was, when it was any), and lets go of the folder once the work that is running has
    /// finished. A delete or a clear that is waiting is dropped too: call <see cref="Drain"/> first.
    /// </summary>
    public void Dispose()
    {
        ClipStore? active;
        ClipStore? building;
        ITimer? retry;
        ITimer? reopen;
        int dropped;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dropped = _work.Count;
            _work.Clear();
            _reopenArgs = null;
            active = _store;
            building = _building;
            retry = _retry;
            reopen = _reopen;
        }

        if (dropped > 0)
        {
            Report($"Pieces of work on the clipboard history that were still waiting when Beacon closed were dropped: {dropped}", null);
        }

        Stop(_daily);
        Stop(retry);
        Stop(reopen);

        // Waits for a piece of work that is using the store, which holds the store's own lock.
        active?.Dispose();
        building?.Dispose();
    }

    private static void Stop(ITimer? timer)
    {
        try
        {
            timer?.Dispose();
        }
        catch (Exception)
        {
            // Left to be cleaned up with the process.
        }
    }

    // Called on a pool thread by the timer.
    private void OnDay()
    {
        try
        {
            Reload();
        }
        catch (Exception ex)
        {
            // Nobody is above this on a pool thread: an escaped exception would end the app.
            Report("The daily reading of the clipboard history could not be started", ex);
        }
    }

    // Called on a pool thread by the retry timer: the history is read again in its turn, if a
    // scrub is still owed by then.
    private void OnRetry()
    {
        try
        {
            Enqueue(() =>
            {
                lock (_gate)
                {
                    _retryArmed = false;
                }

                if (Volatile.Read(ref _store) is { IsWriter: true, ScrubOwed: true } store)
                {
                    store.Load();
                }
            });
        }
        catch (Exception ex)
        {
            Report("The retry of the clipboard history could not be started", ex);
        }
    }

    private void Enqueue(Action work)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _work.Enqueue(work);
            if (!_working)
            {
                _working = true;
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task.Run(DoWork);
            }
        }
    }

    // On the background task. The store that was in use goes first, the new one is made and
    // reads the files, and only then is it the one in use and the one that is shown. When it
    // cannot be made or read, the arguments are kept and tried again later: "tries" is how many
    // times that has been done already for this configure, and only the first failure is told to
    // the owner.
    private void Open(string clipboardDir, string device, int retentionDays, int generation, int tries)
    {
        ClipStore? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // From here until a new store is in use there is none, also when making it fails.
            previous = _store;
            _store = null;
        }

        // The earlier store goes first: while it holds the folder, a new one for the same
        // folder would find it taken.
        previous?.Dispose();

        ClipStore store;
        try
        {
            // The folders of the names this machine had before stay its own to scrub.
            IReadOnlyList<string> earlier = _foldersFile is null ? [] : ClipFolders.Load(_foldersFile);
            store = new ClipStore(clipboardDir, device, retentionDays, _clock, Report, null, _pause, earlier);
        }
        catch (Exception ex)
        {
            NotOpened(null, ex, quiet: tries > 0);
            TryAgainToOpen(clipboardDir, device, retentionDays, generation, tries);
            return;
        }

        bool wanted;
        lock (_gate)
        {
            wanted = !_disposed;
            if (wanted)
            {
                _building = store;
            }
        }

        if (wanted)
        {
            try
            {
                store.Load();
            }
            catch (Exception ex)
            {
                NotOpened(store, ex, quiet: tries > 0);
                TryAgainToOpen(clipboardDir, device, retentionDays, generation, tries);
                return;
            }
        }

        lock (_gate)
        {
            _building = null;
            wanted = !_disposed;
            if (wanted)
            {
                _store = store;
                Volatile.Write(ref _shown, store);
                Volatile.Write(ref _shownWriter, store.IsWriter);
            }
        }

        if (!wanted)
        {
            // Dispose was called while the folder was being taken or read. It must not stay held.
            store.Dispose();
        }
        else if (_foldersFile is not null && store.IsWriter)
        {
            // The name is remembered once its folder has really been taken for writing, so that
            // the folder stays this machine's after the name has been changed.
            ClipFolders.Remember(_foldersFile, device, Report);
        }
    }

    // There is no history in use: the folder is let go of, nothing is shown, and the owner is told,
    // unless this was one of the later tries, which say nothing: the owner has been told.
    private void NotOpened(ClipStore? store, Exception error, bool quiet)
    {
        Report("The clipboard history could not be opened", error);
        try
        {
            store?.Dispose();
        }
        catch (Exception)
        {
            // It is no longer in use, which is what counts.
        }

        lock (_gate)
        {
            _building = null;
            _store = null;
            Volatile.Write(ref _shown, null);
            Volatile.Write(ref _shownWriter, false);
        }

        if (!quiet)
        {
            Notify(NotOpenedNote);
        }
    }

    // The history could not be opened: keeps the arguments and arms the timer for the next try,
    // unless the three tries have been made, or another configure has come since (it has its own
    // tries), or Beacon is closing. "tries" is how many have been made.
    private void TryAgainToOpen(string clipboardDir, string device, int retentionDays, int generation, int tries)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || tries >= ReopenDelays.Length)
            {
                return;
            }

            _reopenArgs = (clipboardDir, device, retentionDays);
            _reopenTries = tries;
            _reopen ??= _time.CreateTimer(_ => OnReopen(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _reopen.Change(ReopenDelays[tries], Timeout.InfiniteTimeSpan);
        }
    }

    // Called on a pool thread by the timer: the try is put in the line, in its turn.
    private void OnReopen()
    {
        try
        {
            (string Folder, string Device, int Days) args;
            int generation;
            int tries;
            lock (_gate)
            {
                if (_disposed || _reopenArgs is not { } kept)
                {
                    return;
                }

                args = kept;
                generation = _generation;
                tries = _reopenTries + 1;
            }

            Enqueue(() => Reopen(args, generation, tries));
        }
        catch (Exception ex)
        {
            Report("The new try at opening the clipboard history could not be started", ex);
        }
    }

    // On the background task. Does nothing when a configure has come since the try was put in the
    // line, or the tries were cancelled: those arguments are not wanted any more.
    private void Reopen((string Folder, string Device, int Days) args, int generation, int tries)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _reopenArgs is null)
            {
                return;
            }

            _reopenArgs = null;
        }

        Open(args.Folder, args.Device, args.Days, generation, tries);
    }

    // The one place that touches the files, on one thread at a time.
    private void DoWork()
    {
        while (true)
        {
            Action work;
            lock (_gate)
            {
                if (!_work.TryDequeue(out work!))
                {
                    _working = false;
                    _idle?.TrySetResult();
                    return;
                }
            }

            var shown = Volatile.Read(ref _shown);
            var before = Current;
            bool writer = IsWriter;
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // Nobody is above this on a pool thread: an escaped exception would end the app.
                Report("A piece of work on the clipboard history failed", ex);
            }

            try
            {
                AfterWork(shown, before, writer);
            }
            catch (Exception ex)
            {
                // The worker must not end with work still waiting.
                Report("A piece of work on the clipboard history failed", ex);
            }
        }
    }

    private void AfterWork(ClipStore? shownBefore, IReadOnlyList<ClipEntry> listBefore, bool writerBefore)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        // Told only when something a reader can see is different: another store, another
        // list, or a store that may now write or may no longer.
        if (!ReferenceEquals(shownBefore, Volatile.Read(ref _shown)) || !ReferenceEquals(listBefore, Current) || writerBefore != IsWriter)
        {
            Changed();
        }

        // A history that is dated in the future is said once per run, when a load has found it.
        if (!_clockTold && Volatile.Read(ref _store) is { ClockSuspect: true })
        {
            _clockTold = true;
            Notify(ClockSuspectNote);
        }

        // Something that was deleted and is still on the disk is said when it becomes so, not
        // again while it stays so, and is tried again until it has worked.
        bool owed = Volatile.Read(ref _store) is { IsWriter: true, ScrubOwed: true };
        if (owed && !_owedTold)
        {
            Notify(ScrubOwedNote);
        }

        _owedTold = owed;
        TryAgainLater(owed);
    }

    // One timer, made when it is first needed, that is armed while a scrub is owed and not
    // already waiting, and stopped when nothing is owed any more.
    private void TryAgainLater(bool owed)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (owed && !_retryArmed)
            {
                // Each wait is longer than the one before, up to the last.
                _retry ??= _time.CreateTimer(_ => OnRetry(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _retry.Change(RetryDelays[_retryStep], Timeout.InfiniteTimeSpan);
                _retryStep = Math.Min(_retryStep + 1, RetryDelays.Length - 1);
                _retryArmed = true;
            }
            else if (!owed)
            {
                if (_retryArmed)
                {
                    _retry?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _retryArmed = false;
                }

                // It has worked, or a write made it good: the next time starts at a minute again.
                _retryStep = 0;
            }
        }
    }

    // After a delete or a clear that was done: when it hid entries of a folder this machine does
    // not own, whose files stay where they are, the owner is told, once per run of Beacon.
    private void TellWhereFilesStay(ClipStore store)
    {
        if (!_foreignTold && store.LastChangeHidForeignEntries)
        {
            _foreignTold = true;
            Notify(ForeignFilesNote);
        }
    }

    // The callbacks are the caller's code. If one threw inside the worker, the worker would end
    // with work still waiting.
    private void Report(string message, Exception? error)
    {
        try
        {
            _report(message, error);
        }
        catch (Exception)
        {
            // Nothing more can be done about a log that is broken.
        }
    }

    // Nothing is said once Dispose has been called: a note about a folder that this copy let
    // go of itself would be wrong, and the view it would tell is going away.
    private void Notify(string note)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        try
        {
            _notify(note);
        }
        catch (Exception)
        {
            // A notification is a courtesy; the log already has the failure.
        }
    }

    private void Changed()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        try
        {
            _changed();
        }
        catch (Exception)
        {
            // Whoever listens will see the history the next time it looks.
        }
    }
}
