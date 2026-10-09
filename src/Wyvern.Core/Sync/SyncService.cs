using System.Security;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Files;
using Wyvern.Core.Platform;
using Wyvern.Core.Settings;

namespace Wyvern.Core.Sync;

/// <summary>
/// Runs sync in the background: a short while after Wyvern starts, then at every interval, and
/// when the owner asks. Never two runs at once, and never on the calling thread: every method
/// here returns at once.
/// </summary>
/// <remarks>
/// <para>With no sync.json, sync is off: nothing is read but that one file's absence, and no
/// connection is made. A sync.json that cannot be used, one without a confirmed host key, a
/// data folder that holds Wyvern's own files or the SSH key file, a sync key that is missing or
/// cannot be read, a saved passphrase that cannot be read: each stops sync before any
/// connection, and the owner is told once in fixed words. Each of these is looked at again at
/// every run, so putting it right is enough. Nothing that cannot be understood ever loosens
/// anything.</para>
/// <para>A server whose host key is not the pinned one, a sign-in that is refused, and a key
/// file that cannot be used stop sync until the owner acts: Sync Now, Sync Setup, Reload or a
/// restart. Until then no further connection is tried, whatever else a run finds in between: a
/// sync.json that was gone or unusable for a while is said while it lasts, and does not lift
/// the stop. A server that cannot be reached, a server that was reached and could not be used
/// (its folder, a refusal, more than Wyvern reads), clocks that are more than a day apart and
/// a run that ended in something nobody expected are tried again by themselves, after the
/// interval at first and then after longer and longer waits, up to an hour. The tray tells a
/// server that could not be used from one that could not be reached.</para>
/// <para>The owner is told the truth about a run that was asked for, whatever it was. A run
/// that came by the clock says nothing while things are well, and speaks once when sync
/// stops, when the server holds something that cannot be read, when the folder on the server
/// could not be made or listed as the connection was made (a folder that sync.json names
/// wrongly does not come right by itself), and when a run did nothing for one of the two
/// reasons below. It says nothing of a server that cannot be reached, of any other failure of
/// the server, or of a data folder that is not there. What was said once is said again only
/// after a run has completed without it: a server that could not be reached in between does
/// not make it news. The tray icon's text says how things stand at any time
/// (<see cref="Status"/>), and "last synced" moves only when a run did everything there was to
/// do.</para>
/// <para>Two things make a run do nothing, without stopping sync, each with a line of its own
/// in the tray and one line in the log when it begins. A clipboard-folders.json that is there
/// and cannot be understood: with fewer names than there are, this machine's earlier
/// clipboard folders would be taken for another machine's. And a data folder whose id file
/// can be neither read nor made. A file that is only held for a moment is unreadable too, and
/// gives the same failure in both, so a run by the clock says either only when the run before
/// found the same; an owner who asks is answered at once. The next run looks again at both.
/// The id (<see cref="SyncFolderId"/>) ties the record of the last sync
/// to the data folder as it stands on the disk, so that a folder that was emptied or replaced
/// is compared afresh and nothing in it is taken for deleted. A data folder that is not there
/// (a drive that is away) ends the run before any connection is made and before that record
/// is read: the record stays as it is, and serves again when the folder is back.</para>
/// <para>A file that was synced and is no longer in the data folder is taken for one the owner
/// deleted, and its deletion goes to his other machine. A run that would send that for many
/// library files at once sends nothing and changes nothing of the owner's
/// (<see cref="SyncEnd.Held"/>, which says what the look at the folder may still remove), and
/// sync is paused: a line of its own in the tray, one sentence with the count that a run by
/// the clock says once and an owner who asks hears every time, and a line in the log when the
/// pause begins and when the count moves. The files may have gone some other way (a folder
/// deleted by mistake, a cleanup tool, a data folder put back from an older backup), and the
/// owner says which it is with one of two commands, each of which starts one run that answers
/// him as Sync Now does: <see cref="ConfirmDeletions"/>, a run that may send the deletions, and
/// <see cref="RestoreMissing"/>, a run that fetches the files back. What he allows holds for
/// the run it starts and for no other, whether that run completes or not, and is kept in
/// memory only. Wherever these remarks say Sync Now, each of the two commands is the same:
/// the owner acts, so a stop that waited for him is over, and while a run is at work he is
/// told so and the run he asked for, with what he allowed, follows. With one difference:
/// <see cref="ConfirmDeletions"/> answers something that is held and nothing else. While
/// sync is not paused (<see cref="Pause"/>), and while it is paused with nothing held, it
/// starts nothing, is not the owner acting, and says so, so that a deletion cannot be allowed
/// in advance. <see cref="RestoreMissing"/> works at any time: it deletes nothing. A pause is
/// not a failure: the next run comes after the interval. "Last synced" does not move
/// meanwhile.</para>
/// <para>A pause, once begun, lasts until the owner has answered it. The limits decide only
/// whether it begins. It is remembered in a mark in the Wyvern root
/// (<see cref="WyvernPaths.SyncPausedFile"/>), which is read as every run starts, so a restart
/// does not lift it; and while it lasts, a run that would send the deletion of a single
/// library file is held, whatever the count. Otherwise the files the owner said he did not
/// delete would go as deletions as soon as there were few enough of them: after he had put
/// some back by hand, or after a run that fetched them back was cut off. The pause ends, and
/// the mark is removed, in exactly two ways: an ordinary run that finds nothing to delete,
/// which is the files being back, by hand or fetched, and which then goes on as any run does;
/// and a Confirm Sync Deletions run that completed. A Restore Missing Files run does not end
/// it by itself, completed or not: the ordinary run that follows it does. It begins one,
/// when none is begun, as it starts: the owner has said that the missing files are not ones
/// he deleted, and a run that restores and is cut off must leave the rest held. A run that
/// was cut off ends nothing: what it had not fetched or sent is held again. A run that could
/// not look at a part of the library ends nothing either, for it cannot see that nothing is
/// missing. A mark that is there and cannot be read counts as there. When the mark cannot be
/// written, the run that was held is still held and shown as a pause, and the next run counts
/// afresh by the limits, which the log says.</para>
/// <para>That sync is paused and that something is held are told apart (<see cref="SyncPause"/>).
/// After a Restore Missing Files run that completed the files are back and nothing is held,
/// and sync is still paused until the next ordinary run has seen that. The tray says nothing
/// of a pause then (it says how the last run ended), and Confirm Sync Deletions says that
/// nothing is waiting.</para>
/// <para>What the owner does while a run is at work is not lost to that run: after Reload,
/// Sync Setup or Sync Now, the run goes on with what it began with, its end does not make sync
/// wait for the owner again, and another run begins <see cref="StartDelay"/> after it. After
/// Sync Setup or Sync Now that second run is the one the owner asked for: it tells him how it
/// went, and the run that was at work does not announce how it ended.</para>
/// <para>The sync key is read from its file when a run starts and wiped when the run ends.
/// Between runs Wyvern holds no key in memory.</para>
/// <para>Closing: <see cref="Dispose"/> stops the clock, tells a run that is under way to stop
/// between two files, and waits for it; a run that does not end in time has its connection
/// closed under it. A run that only begins as Wyvern closes connects to nothing, and one whose
/// connection was still being made closes it as soon as it is there. An upload that is cut off
/// leaves only a temporary file on the server, never half a blob, and the record of the sync
/// is written before the run ends. After <see cref="Dispose"/> nothing is said to anybody.</para>
/// </remarks>
public sealed class SyncService : IDisposable
{
    /// <summary>How long after Wyvern starts, or after Reload, the first run begins.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10);

    /// <summary>The longest wait between two tries at a server that cannot be reached.</summary>
    public static readonly TimeSpan MaxBackOff = TimeSpan.FromHours(1);

    /// <summary>How long closing waits for a run to stop between two files, and then once more after its connection was closed.</summary>
    public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

    public const string NotSetUpNote = "Sync is not set up. Run Sync Setup.";
    public const string AlreadyRunningNote = "Sync is already running.";
    public const string NoDataFolderNote = "Sync cannot run: Wyvern has no data folder in use.";
    public const string DoneNote = "Sync finished.";
    public const string DegradedNote = "Sync finished, but some files on the server could not be read and were skipped. The log file names them.";
    public const string IncompleteNote = "Sync finished, but some files could not be synced this time. Wyvern will try again.";
    public const string DataFolderNote = "Sync did nothing: the data folder could not be read.";
    public const string UnreachableNote = "Sync could not reach the server. Wyvern will try again.";
    public const string ServerNote = "Sync could not use the server. Details are in the log file. Wyvern will try again.";
    public const string ServerFolderNote = "Sync could not use the server: the folder on it could not be made or listed. Check the folder in Sync Setup, and that the folder it is in exists. Wyvern will try again.";
    public const string HostKeyNote = "Sync is stopped and nothing was sent: the server's host key is not the one you confirmed. If you changed the server, run Sync Setup and confirm the new key.";
    public const string RefusedNote = "Sync is stopped: the server refused the sign-in. Check the user name and the key file in Sync Setup.";
    public const string KeyFileNote = "Sync is stopped: the SSH key file could not be read, or its passphrase is missing or wrong. It must be on a drive of this machine, kept on this disk (not an online-only file), and not reached through a link. Run Sync Setup.";
    public const string UnfinishedNote = "Sync Setup was not finished: the server's key has not been confirmed. Run Sync Setup.";
    public const string NoKeyNote = "Sync is stopped: this machine has no sync key. Run Sync Setup.";
    public const string KeyUnreadableNote = "Sync is stopped: the sync key on this machine cannot be read. Run Sync Setup and enter the key again.";
    public const string PassphraseUnreadableNote = "Sync is stopped: the saved passphrase of the SSH key file cannot be read on this machine. Run Sync Setup and enter the passphrase again.";
    public const string ClockNote = "Sync is stopped: this machine's clock and the server's are more than a day apart. Check the date and time.";
    public const string OwnFilesNote = "Sync is stopped: the data folder holds Wyvern's own files. Choose another data folder in the settings.";
    public const string KeyFileInDataFolderNote = "Sync is stopped: the SSH key file is inside the data folder, where it would be synced. Keep the key file somewhere else and run Sync Setup.";
    public const string UnexpectedNote = "Sync failed in a way Wyvern did not expect. Details are in the log file. Wyvern will try again.";
    public const string ClipboardFoldersNote = "Sync did nothing: clipboard-folders.json cannot be understood. Correct it, or delete it only if this PC has always had its present name.";
    public const string FolderIdNote = "Sync did nothing: Wyvern could not read or write its id file in the data folder.";
    public const string NotPausedNote = "Sync is not paused. There is nothing to confirm.";
    public const string NothingWaitingNote = "Nothing is waiting to be confirmed.";
    public const string ConfigNoteEnd = " Sync is off until it is put right.";

    // The log line for a data folder that is not there. The same words as a run's own scan has
    // for a folder that went away after this class had looked.
    private const string NoDataFolderLine = "Sync did nothing because the data folder could not be listed";

    /// <summary>
    /// Shown after any run that kept two versions of a file: a copy is kept when both machines
    /// edited it, and also when the server's version is older than the file here, so the
    /// sentence does not say how it came about. The launcher does not list a conflict copy, so
    /// this is how the owner learns of one.
    /// </summary>
    public static string ConflictNote(int copies) => copies == 1
        ? "Sync kept two versions of a file. The other version is beside it, with \"conflict\" in its name."
        : FormattableString.Invariant($"Sync kept two versions of {copies} files. The other versions are beside them, with \"conflict\" in their names.");

    /// <summary>
    /// Shown when the brake on mass deletion held a run: library files that were synced are no
    /// longer in the data folder, and their deletions are not sent without the owner's word. It
    /// names the two commands by their titles (<see cref="SyncCommands"/>). The count is the
    /// only thing in it that is the owner's. A pause begins with many files, but while it
    /// lasts a run is held for a single one, so the sentence is true of one file too.
    /// </summary>
    public static string MissingFilesNote(int missing) => missing == 1
        ? "Sync is paused and nothing was sent: 1 file that was synced is no longer in the data folder. If you deleted, moved or renamed it, run Confirm Sync Deletions. If you did not, run Restore Missing Files to get it back from the server."
        : FormattableString.Invariant(
            $"Sync is paused and nothing was sent: {missing} files that were synced are no longer in the data folder. If you deleted, moved or renamed them, run Confirm Sync Deletions. If you did not, run Restore Missing Files to get them back from the server.");

    // What is remembered as said when MissingFilesNote was said by the clock, whatever its
    // count: the sentence without it. It is never shown.
    private const string MissingFilesSaid = "Sync is paused: files that were synced are no longer in the data folder.";

    /// <summary>
    /// What the mark of a paused sync holds (<see cref="WyvernPaths.SyncPausedFile"/>): one
    /// fixed line for an owner who finds the file. Nothing reads it back: that the mark is
    /// there is all it says.
    /// </summary>
    public const string PausedMarkText = "Wyvern keeps this file while sync is paused, and removes it when the pause is over. To end the pause, run Confirm Sync Deletions or Restore Missing Files in Wyvern.\n";

    // The two log lines for a mark that could not be written or removed.
    private const string MarkNotWrittenLine = "Sync could not write the mark that keeps it paused, so the next run counts the missing files afresh";
    private const string MarkNotRemovedLine = "Sync could not remove the mark that keeps it paused, so it stays paused";

    private readonly WyvernPaths _paths;
    private readonly IRemoteConnector _connector;
    private readonly KeyStore _keyStore;
    private readonly IClock _clock;
    private readonly Action<string, Exception?> _report;
    private readonly Action<string> _notify;
    private readonly Action _statusChanged;
    private readonly Action _clipboardChanged;
    private readonly TimeProvider _time;
    private readonly Func<Action, Task> _start;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();

    // Everything from here to the status is read and written under the lock only.
    private DataPaths? _data;
    private ClipboardOptions _clipboard = ClipboardOptions.Default;
    private TimeSpan _interval = SyncOptions.Default.Interval;
    private Task _run = Task.CompletedTask;
    private IRemoteStore? _connection;
    private bool _running;
    private bool _disposed;

    // What the runs so far have left behind. A run's end changes these in one place, Settle;
    // besides that only the three ways the owner acts touch them (Configure, Sync Now and
    // SetupChanged).
    //
    // The stop that waits for the owner, kept as the stop it is and apart from the status,
    // which other checks write over. None when sync is not stopped in that way.
    private SyncStop _latched;

    // How many runs in a row could not use the server or ended in something nobody expected.
    private int _failures;

    // The sentences a run by the clock has said and that no run has completed without since.
    private readonly HashSet<string> _told = new(StringComparer.Ordinal);

    // True when the owner acted (Reload, Sync Setup, Sync Now) while a run was at work. That run
    // began before the owner acted, so its end neither stops sync until the owner acts again nor
    // counts as a failure, and the next run comes after StartDelay, not a whole interval later.
    private bool _again;

    // True when the owner asked for a run (Sync Now, Sync Setup; not Reload) while one was at
    // work, until a run has begun that answers him. The run that was at work says nothing by
    // the clock when it ends: what it found is not what he asked about. The next run to begin,
    // which the clock starts, takes this with it and answers as the run he asked for.
    private bool _owed;

    // What the owner said with Confirm Sync Deletions or Restore Missing Files while a run was
    // at work, until the next run begins: that run takes it, and it holds for that run alone.
    // A later one of the two takes the earlier one's place. It is kept nowhere but here, so
    // it does not outlast Wyvern, and a command that starts no run leaves nothing here.
    private OwnersWord _word;

    private SyncStatus _status = SyncStatus.Off;
    private volatile bool _closing;

    // Whether sync is paused, as the runs of this service have found it, and the count of the
    // last run that was held. Written by the one run at a time only (Once and KeepPause); read
    // from any thread, by the command that asks before it confirms.
    private SyncPause _pause = SyncPause.None;

    // True once a run has read the mark. Written by a run only, after it has set _pause by
    // what it read, so that whoever sees it true sees what that run found.
    private bool _markRead;

    // Used by the one run at a time only.
    private bool _offLogged;

    // The count in the last log line about a paused sync, so that the line is written when the
    // pause begins and when the count moves, and not at every run. Used by the one run at a
    // time only.
    private int _pausedLogged;

    // The log line about the mark of a paused sync that was written last, until the mark has
    // been written or removed again: a mark that cannot be written or removed is logged when
    // that begins, and not at every run. Used by the one run at a time only.
    private string? _markProblem;

    /// <param name="connector">Makes connections. The app passes <see cref="SftpRemoteStore.Connector"/>.</param>
    /// <param name="report">Receives fixed words, counts and blob names for the log, and an exception that is logged by type only. A callback that throws is ignored.</param>
    /// <param name="notify">
    /// Receives one of this class's fixed sentences for a notification. What a run has to say
    /// comes on the run's background thread. What <see cref="SyncNow"/> and
    /// <see cref="SetupChanged"/> are answered with at once (a run is already at work, Wyvern
    /// has no data folder, the run could not be started) comes on the thread that called them,
    /// before the call returns. A callback that throws is ignored. Never called after
    /// <see cref="Dispose"/>.
    /// </param>
    /// <param name="statusChanged">Called on a background thread after <see cref="Status"/> has changed. Never called after <see cref="Dispose"/>.</param>
    /// <param name="clipboardChanged">Called on a background thread after a run that wrote or deleted a file under the clipboard folder, so that the history is read again.</param>
    /// <param name="time">Supplies the timer and counts the wait at closing. Left out everywhere except in tests.</param>
    /// <param name="start">Starts a run on a background thread. Left out everywhere except in tests: <see cref="Task.Run(Action)"/>.</param>
    public SyncService(
        WyvernPaths paths,
        IRemoteConnector connector,
        IKeyProtector protector,
        IClock clock,
        Action<string, Exception?>? report = null,
        Action<string>? notify = null,
        Action? statusChanged = null,
        Action? clipboardChanged = null,
        TimeProvider? time = null,
        Func<Action, Task>? start = null)
    {
        _paths = paths;
        _connector = connector;
        _keyStore = new KeyStore(paths.KeyFile, paths.PassphraseFile, protector);
        _clock = clock;
        _report = report ?? ((_, _) => { });
        _notify = notify ?? (_ => { });
        _statusChanged = statusChanged ?? (() => { });
        _clipboardChanged = clipboardChanged ?? (() => { });
        _time = time ?? TimeProvider.System;
        _start = start ?? Task.Run;
        _timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>How sync stands. Safe to read from any thread; never waits.</summary>
    public SyncStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Whether sync is paused by the brake on mass deletion, and how many deletions the last
    /// run held, as far as the runs of this service have seen. Safe to read from any thread;
    /// never waits. The mark on the disk is read as a run starts. Until a run has got that
    /// far (ten seconds after Wyvern starts, and for as long as sync.json cannot be used) this
    /// looks at the mark itself, once for each time it is asked: one check whether it is
    /// there, which is all <see cref="PauseMarked"/> does, so that the command that answers
    /// the pause does not say "Sync is not paused" of a pause that is there. After that it
    /// reads nothing, and says what the runs have seen. A pause found in this way is paused,
    /// with the count not known, as after a restart.
    /// </summary>
    public SyncPause Pause
    {
        get
        {
            var seen = Volatile.Read(ref _pause);
            if (seen.Paused || Volatile.Read(ref _markRead))
            {
                return seen;
            }

            return PauseMarked() ? new SyncPause(true) : seen;
        }
    }

    /// <summary>Completes when no run is under way.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _run;
            }
        }
    }

    /// <summary>
    /// Says which data folder to sync, what this machine is called and how often to run, and
    /// starts the clock: a run begins after <see cref="StartDelay"/>. Called at startup and by
    /// Reload, which also lets a sync that was stopped try once more. While a run is at work,
    /// that run goes on with what it began with, and the run with the new settings begins
    /// <see cref="StartDelay"/> after it has ended.
    /// </summary>
    public void Configure(DataPaths data, ClipboardOptions clipboard, SyncOptions options)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _data = data;
            _clipboard = clipboard;
            _interval = options.Interval;
            _latched = SyncStop.None;
            _failures = 0;
            if (_running)
            {
                // The run that is at work sets the clock when it ends, and is told here how.
                _again = true;
            }
            else
            {
                _timer.Change(StartDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// What the Sync Now command does: a run now, and one sentence that says how it went. While
    /// a run is at work the sentence says so, another run begins <see cref="StartDelay"/> after
    /// that one has ended, and that run says how it went as the one that was asked for. The run
    /// that was at work does not announce how it ended, unless it was asked for too.
    /// </summary>
    public void SyncNow() => Begin(asked: true);

    /// <summary>
    /// What the Confirm Sync Deletions command does once the owner has typed its word: a run
    /// now, as <see cref="SyncNow"/> starts one, which the brake on mass deletion does not
    /// hold. The owner says with it that he deleted, moved or renamed the files that are
    /// missing from the data folder, and that one run may send any number of deletions. Its
    /// answer is the sentence for how the run went. The word holds for the run it starts and
    /// for no other, whether that run completes or not: when the run completed, the pause is
    /// over; when it was cut off, the pause stays, what it had not sent is held again, and he
    /// confirms again. While a run is at work the answer says so, and the word holds for the
    /// run that follows.
    /// </summary>
    /// <remarks>
    /// It answers something that is held, and works only then (<see cref="Pause"/>). While
    /// sync is not paused it starts nothing, is not the owner acting on anything, and says
    /// <see cref="NotPausedNote"/>: a deletion cannot be allowed in advance. The same while
    /// sync is paused with nothing held, which is after a Restore Missing Files run that
    /// completed, until the next ordinary run has ended the pause: nothing is missing, so there
    /// is nothing to confirm, and <see cref="NothingWaitingNote"/> says so. For the same reason
    /// a word that was given while a run was at work allows nothing when that run has ended the
    /// pause, or left nothing held: the run that follows is then an ordinary one.
    /// </remarks>
    public void ConfirmDeletions()
    {
        var pause = Pause;
        if (!pause.Paused)
        {
            Notify(NotPausedNote);
            return;
        }

        if (pause.NothingHeld)
        {
            Notify(NothingWaitingNote);
            return;
        }

        Begin(asked: true, OwnersWord.ConfirmDeletions);
    }

    /// <summary>
    /// What the Restore Missing Files command does: a run now, as <see cref="SyncNow"/> starts
    /// one, for which the server is right where the data folder is behind. A library file that
    /// was synced and is missing here is fetched back, where its deletion would be sent, and a
    /// library file that was changed here is settled against the server's version by their
    /// times, whatever its own time, where it would be uploaded over it: one wins and the
    /// other is kept beside it as a conflict copy (<see cref="PlanInput.RestoreMissing"/>).
    /// The word holds for the run it starts and for no other, whether that run completes or
    /// not. While a run is at work the answer says so, and the word holds for the run that
    /// follows. It works at any time, paused or not: it deletes nothing. It begins a pause when
    /// none is begun, as it starts, so that a run that is cut off leaves the rest held; and it
    /// does not end a pause by itself: the ordinary run that follows it does, when it finds
    /// nothing to delete.
    /// </summary>
    public void RestoreMissing() => Begin(asked: true, OwnersWord.RestoreMissing);

    /// <summary>
    /// For when Sync Setup has saved: whatever stopped sync is forgotten, and a run begins now.
    /// While a run is at work it is answered as <see cref="SyncNow"/> is, and the run with the
    /// new setup follows in the same way: the owner hears how that one went, and not how the
    /// run with the old setup ended.
    /// </summary>
    public void SetupChanged()
    {
        lock (_gate)
        {
            _told.Clear();
        }

        Begin(asked: true);
    }

    /// <summary>
    /// Stops the clock and the run that is under way, and waits for it as the remarks say.
    /// </summary>
    public void Dispose()
    {
        Task run;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _closing = true;
            run = _run;
        }

        try
        {
            _timer.Dispose();
        }
        catch (Exception)
        {
            // Left to be cleaned up with the process.
        }

        if (Wait(run))
        {
            return;
        }

        // The run is inside one long call to the server. Closing the connection ends that call;
        // the run then records what it had done and ends.
        IRemoteStore? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        try
        {
            connection?.Dispose();
        }
        catch (Exception)
        {
            // It is going away in any case.
        }

        if (!Wait(run))
        {
            Report("Sync was still at work when Wyvern closed, and the wait for it ran out", null);
        }
    }

    private bool Wait(Task run)
    {
        if (run.IsCompleted)
        {
            return true;
        }

        try
        {
            run.WaitAsync(CloseWait, _time).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            // The run itself never throws; whatever this was, it is over.
            return true;
        }
    }

    // Called on a pool thread by the timer.
    private void OnTimer()
    {
        try
        {
            Begin(asked: false);
        }
        catch (Exception ex)
        {
            // Nobody is above this on a pool thread: an escaped exception would end the app.
            Report("A sync run could not be started", ex);
        }
    }

    // What the owner allows one run, with one of the two commands that answer a paused sync.
    private enum OwnersWord
    {
        // An ordinary run.
        None,

        // Confirm Sync Deletions: the brake on mass deletion does not hold the run.
        ConfirmDeletions,

        // Restore Missing Files: the run fetches what is missing here, and sends no deletion for it.
        RestoreMissing,
    }

    private void Begin(bool asked, OwnersWord word = OwnersWord.None)
    {
        string? refusal = null;
        Exception? notStarted = null;

        // True when whoever asked for this run is to hear how it went, or that there is none.
        bool answers = asked;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (asked)
            {
                // The owner acts: a stop that waited for that is over.
                _latched = SyncStop.None;
            }

            if (_running)
            {
                refusal = AlreadyRunningNote;

                // What was asked for is not lost: the run that is at work is told to let
                // another follow it shortly, and that one answers the owner.
                _again |= asked;
                _owed |= asked;

                // The run that is at work began without the owner's word, and goes on without
                // it. The word is kept for the run that follows, and the later of two words
                // takes the earlier one's place: he cannot mean both.
                if (word != OwnersWord.None)
                {
                    _word = word;
                }
            }
            else if (_data is not { } data)
            {
                // No run begins, so the word that came with this call goes nowhere: a run that
                // sends deletions must not begin, some time later, on a word nobody remembers.
                refusal = NoDataFolderNote;
            }
            else
            {
                // The run the owner asked for while another was at work: the clock starts it,
                // and it answers him all the same. Whichever run begins next is that run.
                answers |= _owed;
                _owed = false;

                // The owner's word for one run: the one that came with this call, or else the
                // one he gave while the run before was at work. It is taken out here, before
                // the run is started, so it is used up whether the run completes, fails, or
                // cannot be started at all.
                var allowed = word != OwnersWord.None ? word : _word;
                _word = OwnersWord.None;
                var clipboard = _clipboard;
                _running = true;
                try
                {
                    bool answered = answers;
                    _run = _start(() => Run(data, clipboard, answered, allowed));
                }
                catch (Exception ex)
                {
                    // No run began, so none will end and set the clock. Later runs can start,
                    // and the clock goes on.
                    notStarted = ex;
                    _running = false;
                    _timer.Change(_interval, Timeout.InfiniteTimeSpan);
                }
            }
        }

        // Said after the lock is let go of: whoever listens is not this class's code.
        if (notStarted is not null)
        {
            Report("A sync run could not be started", notStarted);
            refusal = UnexpectedNote;
        }

        if (refusal is not null && answers)
        {
            Notify(refusal);
        }
    }

    // One run, on a background thread, from sync.json to the next turn of the clock. "Asked" is
    // true for a run whose end the owner is told whatever it was: one he started, and the one
    // that follows what he asked for while another run was at work. "Allowed" is what the owner
    // allowed this one run with Confirm Sync Deletions or Restore Missing Files, if he did.
    private void Run(DataPaths data, ClipboardOptions clipboard, bool asked, OwnersWord allowed)
    {
        Outcome? outcome = null;
        try
        {
            // A run that begins as Wyvern closes reads nothing and connects to nothing.
            outcome = _closing ? null : Once(data, clipboard, Status, allowed);
        }
        catch (Exception ex)
        {
            // Whatever it was, the run is over and the clock goes on. It is not passed over in
            // silence: sync is shown as incomplete, an owner who asked is told where to look,
            // and the next run waits as it does for a server that cannot be reached.
            Report("A sync run failed", ex);
            outcome = new Outcome(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, Status.LastSynced), UnexpectedNote) { Failure = true };
        }
        finally
        {
            Settle(outcome, asked);
        }
    }

    /// <summary>
    /// How one run ended, as the run itself knows it: how sync stands after it and the sentence
    /// for that. What it does to the stop that waits for the owner, to the wait before the next
    /// run and to what a run by the clock says is decided in one place, <see cref="Settle"/>.
    /// </summary>
    private sealed record Outcome(SyncStatus Status, string Note)
    {
        /// <summary>Not <see cref="SyncStop.None"/> when the run failed in a way that stops sync until the owner acts.</summary>
        public SyncStop Latch { get; init; }

        /// <summary>True when the server could not be used, the clocks are apart or the run ended in something nobody expected: the next run waits longer.</summary>
        public bool Failure { get; init; }

        /// <summary>True when the run went through to its end. Only such a run shows that what was said before is over.</summary>
        public bool Completed { get; init; }

        /// <summary>
        /// True for what a run by the clock says too, once: sync is stopped, the server holds
        /// something that cannot be read, or the status names a reason of its own (the run did
        /// nothing, or the folder on the server could not be used as the connection was made).
        /// </summary>
        public bool Announced => Status.Condition is SyncCondition.Stopped or SyncCondition.Degraded || Status.Stop != SyncStop.None;

        /// <summary>
        /// True when a run by the clock keeps what would be <see cref="Announced"/> to itself
        /// this once, and it does not count as said: it may be over by the next run. An owner
        /// who asked is answered all the same, and then it does count as said.
        /// </summary>
        public bool NotYet { get; init; }

        /// <summary>
        /// What is remembered as said once a run by the clock has said <see cref="Note"/>,
        /// when that is not the sentence itself: fixed words for a sentence that carries a
        /// count, so that it is said once, and not once more for every count.
        /// </summary>
        public string? SaidAs { get; init; }

        /// <summary>What is remembered as said: <see cref="SaidAs"/>, or the sentence.</summary>
        public string Said => SaidAs ?? Note;

        /// <summary>The type's name, the condition and the reason.</summary>
        public override string ToString() => $"{nameof(Outcome)} {{ Condition = {Status.Condition}, Stop = {Status.Stop} }}";
    }

    // The one place where a run's end changes what the service remembers: the stop that waits
    // for the owner, the count of failures, what has been said, the status, and the clock for
    // the next run, all under the lock. The sentence and the word that the status changed go
    // out after the lock is let go of: whoever listens is not this class's code.
    //
    // A null outcome is a run that ended because Wyvern is closing: nothing changes.
    private void Settle(Outcome? outcome, bool asked)
    {
        string? say = null;
        bool changed = false;
        lock (_gate)
        {
            if (outcome is not null && !_closing)
            {
                // What Reload, Sync Setup or Sync Now set back while this run was at work stays
                // set back: the run began before the owner acted, so it is not this run that
                // makes sync wait for the owner again, or wait longer.
                if (outcome.Latch != SyncStop.None && !_again)
                {
                    _latched = outcome.Latch;
                }

                if (!outcome.Failure)
                {
                    _failures = 0;
                }
                else if (!_again)
                {
                    _failures++;
                }

                // An owner who asked is always answered. A run that came by the clock says what
                // is worth saying once: again only after a run has completed without it. It says
                // nothing when the owner asked for a run while it was at work: he acted after it
                // had begun, the run that follows answers him, and what this one found is not
                // counted as said. Nor is what a run by the clock keeps to itself for once.
                bool silent = !asked && (_owed || outcome.NotYet);
                bool news = !silent && outcome.Announced && _told.Add(outcome.Said);
                if (outcome.Completed)
                {
                    _told.RemoveWhere(told => !outcome.Announced || told != outcome.Said);
                }

                if (outcome.Note.Length > 0 && (asked || news))
                {
                    say = outcome.Note;
                }

                if (outcome.Status != _status)
                {
                    Volatile.Write(ref _status, outcome.Status);
                    changed = true;
                }
            }

            // The interval is read here, under the lock: Reload may have changed it meanwhile.
            var next = _again ? StartDelay : BackOff(_interval, _failures);
            _again = false;
            _running = false;
            _connection = null;
            if (!_disposed)
            {
                _timer.Change(next, Timeout.InfiniteTimeSpan);
            }
        }

        if (say is not null)
        {
            Notify(say);
        }

        if (changed)
        {
            Changed();
        }
    }

    // Shows a status while a run is still at work. How the run ends is written by Settle.
    private void Show(SyncStatus status)
    {
        lock (_gate)
        {
            if (_closing || status == _status)
            {
                return;
            }

            Volatile.Write(ref _status, status);
        }

        Changed();
    }

    /// <summary>The wait before the next run: the interval, doubled for each run in a row that could not use the server, and never more than <see cref="MaxBackOff"/> unless the interval itself is longer.</summary>
    public static TimeSpan BackOff(TimeSpan interval, int failures)
    {
        if (failures <= 1)
        {
            return interval;
        }

        double factor = Math.Pow(2, Math.Min(failures - 1, 16));
        var wait = TimeSpan.FromTicks((long)Math.Min(interval.Ticks * factor, MaxBackOff.Ticks));
        return wait > interval ? wait : interval;
    }

    // Does the run, and says how it ended. Null when it ended because Wyvern is closing.
    private Outcome? Once(DataPaths data, ClipboardOptions clipboard, SyncStatus before, OwnersWord allowed)
    {
        var loaded = SyncConfig.Load(_paths.SyncFile);
        switch (loaded.State)
        {
            case SyncConfigState.Missing:
                if (!_offLogged)
                {
                    _offLogged = true;
                    Report(FormattableString.Invariant($"Sync is not set up, so no connection is made. Connections opened by this run of Wyvern: {SftpRemoteStore.ConnectionsOpened}"), null);
                }

                return new Outcome(SyncStatus.Off, NotSetUpNote);

            case SyncConfigState.Unreadable:
                return Stopped(SyncStop.ConfigUnusable, loaded.Problem + ConfigNoteEnd, before, loaded.Cause);

            case SyncConfigState.Unpinned:
                return Stopped(SyncStop.SetupUnfinished, UnfinishedNote, before);
        }

        _offLogged = false;
        var config = loaded.Config!;

        // Whether sync is paused: an earlier run was held, and the owner has not answered it
        // yet. That is remembered in a mark on the disk, beside sync.json, so that a restart
        // does not lift a pause, and it is read here, as the run starts. The run is told
        // (SyncRunContext.Paused), and what it reports decides whether the mark is written,
        // stays or goes (KeepPause). What the mark says is known from here on, also when this
        // run goes no further than the next check or never reaches the server.
        bool marked = PauseMarked();

        // A run that restores begins a pause when none is begun, and does it here, as it
        // starts, before it asks anything of the server or touches a file: the owner has said
        // that the missing files are not ones he deleted, and nothing that cuts the run off
        // (the connection, Wyvern closing, the power) may leave them to the next ordinary run,
        // which goes by the limits and would send what is left as deletions. If the mark
        // cannot be written the run goes on all the same, and the pause is not begun (logged).
        if (allowed == OwnersWord.RestoreMissing && !marked && WriteMark())
        {
            marked = true;
        }

        if (marked && !Volatile.Read(ref _pause).Paused)
        {
            Volatile.Write(ref _pause, new SyncPause(true));
        }

        Volatile.Write(ref _markRead, true);

        // Stopped until the owner acts. Nothing is connected to meanwhile, whatever the status
        // said before this run: a sync.json that was gone or unusable for a while was said
        // while it lasted, and has lifted nothing.
        SyncStop latched;
        lock (_gate)
        {
            latched = _latched;
        }

        if (latched != SyncStop.None)
        {
            return Stopped(latched, NoteFor(latched), before);
        }

        // The owner chooses the data folder. One that is the Wyvern root, or that the root lies
        // under, would send the sync key, sync.json, settings.json and the log to the server and
        // write the other machine's over them. Looked at again at every run, so a corrected
        // setting and a Reload are enough.
        if (Holds(data.Root, _paths.Root))
        {
            return Stopped(SyncStop.DataFolderHoldsWyvern, OwnFilesNote, before);
        }

        // The SSH key file must not lie in the data folder either: it would be synced. Whether
        // it does is asked by making its path full, and that asks the disk when the path's
        // text holds a "~": Windows then looks for the long form of what may be a short name,
        // and reads the disk along the path to find it. On a network drive that contacts the
        // share. On a drive of this machine it does too, where the path passes through a
        // folder that is a link to a share. Either would be a connection made here and at
        // every run, before the connection has refused the key file. So the question is asked
        // only of a path whose text holds no "~" and that is on a drive of this machine, both
        // of which are told from the text and from what Windows knows of its own drive
        // letters. A key file whose path holds a "~" is not looked at here at all, in the
        // data folder or out of it: the connection, which looks at every folder on the way
        // without following a link, refuses or accepts it by its own rules. (For a path with
        // no "~", whether a folder on the way is a link is not asked here either. Making such
        // a path full reads nothing, and the connection asks, folder by folder.)
        if (!config.PrivateKeyPath.Contains('~')
            && SftpRemoteStore.IsOnDriveOfThisMachine(config.PrivateKeyPath)
            && Holds(data.Root, config.PrivateKeyPath))
        {
            return Stopped(SyncStop.KeyFileInDataFolder, KeyFileInDataFolderNote, before);
        }

        // Which clipboard folders are this machine's own: the present name always, and the
        // earlier names in clipboard-folders.json. With a list that is there and cannot be
        // understood the run does nothing at all: with fewer names than there are, sync would
        // take this machine's earlier folders for another machine's and write the server's
        // older files over them. Not a stop that waits for the owner: a file that is only held
        // for a moment is unreadable too, so the next run looks again. For the same reason a
        // run by the clock says it only when the run before found the same: the sentence tells
        // the owner to correct the file, and a file that was only held needs no correcting. The
        // tray and the log have it at once.
        if (ClipFolders.TryLoad(_paths.ClipboardFoldersFile, out var earlier) == ClipFoldersState.Unreadable)
        {
            bool second = before.Stop == SyncStop.ClipboardFoldersUnreadable;
            if (!second)
            {
                Report("Sync did nothing because clipboard-folders.json could not be understood", null);
            }

            return new Outcome(new SyncStatus(SyncCondition.Incomplete, SyncStop.ClipboardFoldersUnreadable, before.LastSynced), ClipboardFoldersNote) { NotYet = !second };
        }

        var own = new HashSet<string>(earlier, StringComparer.OrdinalIgnoreCase) { clipboard.DeviceName };

        // A data folder that is not there ends the run here, in the words a run always had for
        // it: before any connection is made, and before the record of the last sync is read.
        // Were it looked for only after the connection, a drive that is reconnected while the
        // run connects would be worked from an empty record, which would then take the place
        // of the real one.
        if (!Directory.Exists(data.Root))
        {
            Report(NoDataFolderLine, null);
            return new Outcome(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, before.LastSynced), DataFolderNote);
        }

        // The record of the last sync is only used for the data folder it was made for, as that
        // folder stands on the disk: the folder carries an id, and the id is part of the scope.
        // A folder whose id can be neither read nor made is left alone until it can: shown in a
        // line of its own, logged when it begins and not at every run (for a data folder that
        // may not be written stays that way), and said by the clock once, at the second run in
        // a row that finds it so. As with the clipboard folders list above, a file that is only
        // held for a moment gives the same failure, so the first run keeps the sentence to
        // itself, and an owner who asked is answered at once.
        if (!SyncFolderId.TryGet(data.Root, out string folderId))
        {
            bool second = before.Stop == SyncStop.FolderIdUnusable;
            if (!second)
            {
                Report("Sync did nothing because the id file of the data folder could not be read or made", null);
            }

            return new Outcome(new SyncStatus(SyncCondition.Incomplete, SyncStop.FolderIdUnusable, before.LastSynced), FolderIdNote) { NotYet = !second };
        }

        var keyState = _keyStore.ReadMaster(out byte[] master, out var keyCause);
        if (keyState != SecretState.Read)
        {
            return keyState == SecretState.Missing
                ? Stopped(SyncStop.KeyMissing, NoKeyNote, before)
                : Stopped(SyncStop.KeyUnreadable, KeyUnreadableNote, before, keyCause);
        }

        SyncKeys keys;
        try
        {
            keys = SyncKeys.FromMaster(master);
        }
        finally
        {
            SyncKeys.Wipe(master);
        }

        using (keys)
        {
            if (_keyStore.ReadPassphrase(out string? passphrase, out var passphraseCause) == SecretState.Unreadable)
            {
                return Stopped(SyncStop.PassphraseUnreadable, PassphraseUnreadableNote, before, passphraseCause);
            }

            // From here the run goes to the server. Set up, and no run has ended since sync was
            // off: the tray says "has not run yet" until this one has. Only here, so that a run
            // that ends above, stopped or with nothing done, shows how it ended and nothing in
            // between.
            if (before.Condition == SyncCondition.Off)
            {
                Show(new SyncStatus(SyncCondition.Waiting));
            }

            IRemoteStore store;
            try
            {
                store = _connector.Open(config, passphrase);
            }
            catch (RemoteStoreException ex)
            {
                Report($"Sync could not connect ({ex.Failure}, {ex.Cause ?? "no detail"})", null);
                return Failed(ex.Failure, before, connecting: true);
            }

            using (store)
            {
                // Closing closes the connection of a run that does not end in time. One that was
                // still being made when Wyvern began to close is closed here, at once, and the
                // server is asked nothing.
                bool closing;
                lock (_gate)
                {
                    _connection = store;
                    closing = _disposed;
                }

                if (closing)
                {
                    return null;
                }

                // "Another scope" covers a data folder that was emptied or replaced, which has
                // another id or none. It does not cover a data folder put back from a backup
                // that holds the id file: that folder has the same id, and the record is used
                // against it as it is.
                string scope = SyncState.ScopeOf(config.RemoteId(keys.Check()), data.Root, folderId);
                var state = SyncState.Load(_paths.SyncStateFile, scope, out var how, out int skipped);
                if (how is SyncStateLoad.OtherScope or SyncStateLoad.Unreadable)
                {
                    Report(
                        how == SyncStateLoad.OtherScope
                            ? "The record of the last sync was made for another server, sync key or data folder, or for this data folder before it was emptied or replaced, so every file is compared afresh"
                            : "The record of the last sync could not be read, so every file is compared afresh",
                        null);
                }

                if (skipped > 0)
                {
                    Report(FormattableString.Invariant($"Entries of the record of the last sync that could not be understood were left out: {skipped}"), null);
                }

                // The owner's word to send the deletions answers something that is held, and
                // allows nothing without one. He could only give it while that was so; but a
                // word given while a run was at work waited for this run, and that run may
                // have ended the pause meanwhile, or left nothing held (a Restore Missing
                // Files run that completed). Paused is asked of what this service knows and
                // not of the mark alone: a pause whose mark could not be written was shown and
                // said to him all the same, and he answers it.
                var pause = Pause;
                bool confirmed = allowed == OwnersWord.ConfirmDeletions && pause.Paused && !pause.NothingHeld;
                bool restoring = allowed == OwnersWord.RestoreMissing;
                var result = SyncRun.Execute(new SyncRunContext(
                    data.Root,
                    _paths.SyncStateFile,
                    state,
                    store,
                    keys,
                    clipboard.DeviceName,
                    own.Contains,
                    clipboard.RetentionDays,
                    _clock,
                    Report,
                    () => _closing,
                    AllowDeletions: confirmed,
                    RestoreMissing: restoring,
                    Paused: marked));

                // Also for a run that Wyvern's closing cut off: a pause that began is
                // remembered, and one that such a run did not end stays.
                KeepPause(result, restoring, confirmed, marked);

                if (result.ClipboardChanged && !_closing)
                {
                    ClipboardChanged();
                }

                if (result.ConflictCopies > 0)
                {
                    Notify(ConflictNote(result.ConflictCopies));
                }

                return After(result, before);
            }
        }
    }

    // Begins, keeps or ends the pause after a run, by what the run reports. This is the one
    // place where the mark of a paused sync is removed, and where it is written for a run that
    // was held. The one other place that writes it is Once, for a run that restores, as that
    // run starts.
    //
    // A run that was held: sync is paused, and the mark is written when it is not there. When
    // it cannot be written, the run was held all the same and is shown and said as a pause,
    // but nothing on the disk says so: the next run finds no mark and counts afresh by the
    // limits, as every run did before a pause was remembered. That is logged.
    //
    // The pause ends, and the mark goes, in exactly two ways. An ordinary run, which is any run
    // but one that restores, whose plan left nothing to hold (SyncResult.NothingToHold): the
    // files are back, or were fetched back, and that run has gone on as any run does. And a run
    // that the owner allowed to send the deletions, when it completed. A run that restores
    // never ends the pause, completed or not: its plan holds no deletion whatever is missing,
    // so it shows nothing; the ordinary run that follows it does. And it has begun the pause
    // itself, when none was begun, as it started (Once): so one that is cut off leaves the
    // rest held, and one that completes leaves the pause for that ordinary run to end. A run
    // that was cut off, by Wyvern closing or by the server, ends nothing either: what it had
    // not fetched or sent is held again, and the owner answers again.
    //
    // What this service says of the pause (Pause) follows from the same, and tells two things
    // apart: that sync is paused, which is that the mark is there, and that something is held,
    // which is that files the owner may not have deleted are missing and wait for his answer.
    // A pause begins with something held, but it can outlast it: a run that restores and
    // completes has the files back, and the mark stays until the ordinary run after it has
    // seen that. So, after a run that was held: paused, with the run's count. Once the pause
    // is over: not paused. And after any other run that got to the server, paused for as long
    // as the mark is there, with no count, since that run counted nothing that is held; and
    // told apart there, by whether the run showed that nothing is held. It shows that when it
    // went through its plan, found no deletion note for a library file, and could look at the
    // whole library (SyncResult.NothingToHold), and, for a run that restores, which has no
    // deletion note in its plan whatever is missing, when it also completed and left nothing
    // for later: every file it was to fetch is back. Anything less, a run that was cut off,
    // that could not look at the whole library or that left a file for later, shows nothing
    // of the kind, and the pause is taken to hold something, so that the owner can answer it.
    // "Restoring" and "confirmed" say which kind of run it was, and "marked" whether the mark
    // was there when it started or was written by it as it started.
    private void KeepPause(SyncResult result, bool restoring, bool confirmed, bool marked)
    {
        if (result.End == SyncEnd.Held)
        {
            if (!marked)
            {
                WriteMark();
            }

            Volatile.Write(ref _pause, new SyncPause(true, result.HeldDeletions));
            return;
        }

        bool over = (!restoring && result.NothingToHold) || (confirmed && result.End == SyncEnd.Completed);

        // A pause that is over and whose mark will not go is still a pause, with nothing held.
        bool stuck = marked && over && !RemoveMark();
        bool still = marked && (!over || stuck);
        bool restored = restoring && result.End == SyncEnd.Completed && result.NothingToHold && result.LeftForLater == 0;
        Volatile.Write(ref _pause, !still ? SyncPause.None : new SyncPause(true, NothingHeld: stuck || restored));
    }

    // True when sync is paused: the mark is there. Whatever is there under its name is the
    // mark, a file or anything else, whatever it holds. A mark that cannot be asked about
    // counts as there: nothing that cannot be understood ever loosens anything. Only "there is
    // nothing of that name" says that sync is not paused.
    private bool PauseMarked()
    {
        try
        {
            File.GetAttributes(_paths.SyncPausedFile);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            return true;
        }
    }

    // False when the mark could not be written: it is not there, and logged once.
    private bool WriteMark()
    {
        try
        {
            File.WriteAllText(_paths.SyncPausedFile, PausedMarkText);
            _markProblem = null;
            return true;
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            MarkProblem(MarkNotWrittenLine, ex);
            return false;
        }
    }

    // False when the mark is still there: sync stays paused, and the next run that finds
    // nothing to hold tries again.
    private bool RemoveMark()
    {
        try
        {
            File.Delete(_paths.SyncPausedFile);
            _markProblem = null;
            return true;
        }
        catch (Exception ex) when (SyncFiles.IsFileFailure(ex))
        {
            MarkProblem(MarkNotRemovedLine, ex);
            return false;
        }
    }

    // Logged when it begins, and not again at every run for as long as it lasts.
    private void MarkProblem(string line, Exception cause)
    {
        if (_markProblem != line)
        {
            _markProblem = line;
            Report(line, cause);
        }
    }

    private Outcome? After(SyncResult result, SyncStatus before)
    {
        var last = before.LastSynced;
        switch (result.End)
        {
            case SyncEnd.Failed:
                return Failed(result.Failure ?? RemoteFailure.Server, before, connecting: false);

            case SyncEnd.ClockWrong:
                // Not a stop that waits for the owner: a clock is put right without telling Wyvern.
                return new Outcome(new SyncStatus(SyncCondition.Stopped, SyncStop.ClockWrong, last), ClockNote) { Failure = true };

            case SyncEnd.NoDataFolder:
                return new Outcome(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, last), DataFolderNote);

            case SyncEnd.Held:
                // The brake on mass deletion held the run, which sent nothing and changed
                // nothing of the owner's. Sync is shown as paused, in a line of its own, and
                // "last synced" stays where it was. Not a failure: the server was used, and
                // the next run comes after the interval. Not a stop that is latched either:
                // the pause is kept by its mark on the disk (KeepPause), every run looks
                // again, and sync goes on by itself once a run finds nothing to delete. A run
                // by the clock says it once, however the count moves meanwhile, and an owner
                // who asks is told every time, with the count as it is then. The log has a
                // line when the pause begins and when the count moves.
                if (before.Stop != SyncStop.DeletionsHeld || result.HeldDeletions != _pausedLogged)
                {
                    _pausedLogged = result.HeldDeletions;
                    Report(FormattableString.Invariant($"Sync is paused and nothing was sent because files that were synced are no longer in the data folder: {result.HeldDeletions}"), null);
                }

                return new Outcome(new SyncStatus(SyncCondition.Stopped, SyncStop.DeletionsHeld, last), MissingFilesNote(result.HeldDeletions)) { SaidAs = MissingFilesSaid };

            case SyncEnd.Stopped:
                return null;
        }

        // The run went through. "Last synced" moves only when it did everything there was to
        // do: nothing left for the next run, nothing in the data folder that could not be
        // looked at (whether or not the record knows anything behind it), and the record of it
        // saved. A blob that cannot be read stays on the server until someone removes it, so it
        // must not hide any of these.
        bool everything = result.LeftForLater == 0 && result.Unreadable == 0 && !result.StateNotSaved;
        var synced = everything ? _clock.UtcNow : last;
        if (result.BadBlobs > 0)
        {
            return new Outcome(new SyncStatus(SyncCondition.Degraded, SyncStop.None, synced), DegradedNote) { Completed = true };
        }

        return everything
            ? new Outcome(new SyncStatus(SyncCondition.Ok, SyncStop.None, synced), DoneNote) { Completed = true }
            : new Outcome(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, synced), IncompleteNote) { Completed = true };
    }

    // How a run ended that could not use the server. Three kinds stop sync until the owner acts.
    // Every other kind is tried again, after longer and longer waits: a server that could not be
    // reached, and a server that was reached and could not be used, which the tray tells apart.
    private static Outcome Failed(RemoteFailure failure, SyncStatus before, bool connecting)
    {
        var last = before.LastSynced;
        var stop = StopFor(failure);
        if (stop != SyncStop.None)
        {
            return new Outcome(new SyncStatus(SyncCondition.Stopped, stop, last), NoteFor(stop)) { Latch = stop };
        }

        if (failure == RemoteFailure.Unreachable)
        {
            return new Outcome(new SyncStatus(SyncCondition.Unreachable, SyncStop.None, last), UnreachableNote) { Failure = true };
        }

        // The folder, as the connection was made: it is not there and could not be made, or it
        // may not be listed. That is what a folder named wrongly in sync.json looks like, and it
        // does not come right by itself, so the status names it and a run by the clock says it
        // once. Not a stop: the owner may put it right on the server and tell Wyvern nothing.
        if (failure == RemoteFailure.Folder && connecting)
        {
            return new Outcome(new SyncStatus(SyncCondition.ServerUnusable, SyncStop.ServerFolder, last), ServerFolderNote) { Failure = true };
        }

        // The folder in the middle of a run, a refusal, more than Wyvern reads, a blob that is gone.
        return new Outcome(new SyncStatus(SyncCondition.ServerUnusable, SyncStop.None, last), ServerNote) { Failure = true };
    }

    // The failures that stop sync until the owner acts, each as the stop it is.
    private static SyncStop StopFor(RemoteFailure failure) => failure switch
    {
        RemoteFailure.HostKeyChanged => SyncStop.HostKeyChanged,
        RemoteFailure.Refused => SyncStop.Refused,
        RemoteFailure.KeyFile => SyncStop.KeyFile,
        _ => SyncStop.None,
    };

    // The sentence of each stop that waits for the owner. No other stop is ever latched.
    private static string NoteFor(SyncStop latched) => latched switch
    {
        SyncStop.HostKeyChanged => HostKeyNote,
        SyncStop.Refused => RefusedNote,
        _ => KeyFileNote,
    };

    private Outcome Stopped(SyncStop stop, string note, SyncStatus before, Exception? cause = null)
    {
        if (before.Stop != stop)
        {
            Report($"Sync is stopped ({stop})", cause);
        }

        return new Outcome(new SyncStatus(SyncCondition.Stopped, stop, before.LastSynced), note);
    }

    // True when the path is the folder itself or lies inside it: both made full, without a
    // separator at the end, and compared without regard to case. Making a path full goes by
    // its text, with one exception: for a path that holds a "~" Windows is asked for the long
    // form of the short name, and reads the disk along that path to find it. That is why the
    // key file's path is given to this only when its text holds no "~" and it is on a drive of
    // this machine; the data folder and the Wyvern root are read in any case. It does not see
    // through a link, or through a second spelling of the same folder that is not a short name.
    private static bool Holds(string folder, string path)
    {
        string outer = Full(folder);
        return outer.Length > 0 && FilePaths.IsUnder(Full(path), outer);
    }

    private static string Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            // Not a path Windows can make sense of. It is compared as it was written.
            return Path.TrimEndingDirectorySeparator(path);
        }
    }

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

    private void Notify(string note)
    {
        if (_closing)
        {
            return;
        }

        try
        {
            _notify(note);
        }
        catch (Exception)
        {
            // A notification is a courtesy; the tray icon's text says the same.
        }
    }

    private void Changed()
    {
        if (_closing)
        {
            return;
        }

        try
        {
            _statusChanged();
        }
        catch (Exception)
        {
            // Whoever listens sees the status the next time it looks.
        }
    }

    private void ClipboardChanged()
    {
        try
        {
            _clipboardChanged();
        }
        catch (Exception)
        {
            // The history is read again once a day in any case.
        }
    }
}
