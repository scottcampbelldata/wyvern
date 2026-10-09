using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Platform;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Decides what of the clipboard is kept. The Windows side only says that the clipboard has
/// changed and copies blocks of bytes when asked; everything else is here: whether to look at
/// all, what must never be kept, what each block means, what is too large, and what goes into
/// the history.
/// </summary>
/// <remarks>
/// <para>While capture is paused, and while this copy of Beacon may not write the history, the
/// listener is stopped: Beacon is then told nothing and reads nothing. Each time listening
/// starts, what is remembered becomes "not known", a look at the clipboard is put in the capture
/// line, and only then is the listener started. The look runs on the line in its turn, before
/// every capture the listener causes, reads through the same rules as any copy, and only
/// remembers what it finds as dealt with: it was not copied while Beacon was listening, and may
/// have been copied while capture was paused. Nothing is read on the thread that starts
/// listening, because the look asks the program that owns the clipboard to make every form it
/// offers, and how long that takes is up to that program. The price is that a copy made between
/// the start of listening and the look's turn is taken for what was there; that gap is the
/// line's backlog, and nothing when it is idle.</para>
/// <para><see cref="Start"/>, <see cref="Pause"/>, <see cref="Resume"/> and <see cref="Refresh"/>
/// are for the UI thread, which is also where the listener calls back. The reading and the
/// storing are done by the work that is handed to <c>post</c>, on the history's background
/// task, one change at a time. Work that was put in the line before a pause, or before
/// listening stopped, does nothing when it runs.</para>
/// <para>What is read is the owner's. None of it, and no program's name, ever goes to
/// <c>report</c>: only fixed words and counts. It is remembered in memory only, as a
/// fingerprint of the last copy that was read, so that the same copy is not worked on or
/// recorded twice in a row. That is so also when its entry has been deleted from the history
/// since; it is forgotten when Beacon closes.</para>
/// </remarks>
public sealed class ClipCapture
{
    /// <summary>How many times one reading of the clipboard is tried when another program has it open.</summary>
    public const int Attempts = 10;

    /// <summary>How long to wait between two tries. Right after a copy several programs look at the clipboard, each for a moment.</summary>
    public static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long the clipboard has to have been left alone before Beacon opens it, unless the
    /// owner has set another time: nothing told of a change, and no window holding it open.
    /// A copy is not always one step. Some programs put it on the clipboard a form at a
    /// time, opening and closing the clipboard for each, and give the whole copy up when
    /// they find the clipboard open in between. A recorder that opened it at the first
    /// word of a change would break copying in such a program.
    /// </summary>
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest such time that is used, whatever is set.</summary>
    public static readonly TimeSpan MaxSettle = TimeSpan.FromSeconds(5);

    /// <summary>How often it is asked, while waiting, whether anything has stirred.</summary>
    public static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(25);

    /// <summary>How long one change is waited for at most. A clipboard that is never left alone that long is read all the same.</summary>
    public static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(10);

    /// <summary>How many times the file that remembers the pause is written when that fails.</summary>
    public const int SaveAttempts = 3;

    /// <summary>How long to wait between two tries at writing that file.</summary>
    public static readonly TimeSpan SavePause = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long the picture files of one copy are read for. Once it has gone by, the names not
    /// yet looked at are listed by their path only. The history has one line, and while one copy
    /// holds it later copies are lost.
    /// </summary>
    public static readonly TimeSpan PicturesTimeLimit = TimeSpan.FromSeconds(5);

    /// <summary>Shown by Pause Clipboard Capture.</summary>
    public const string PausedNote = "Clipboard capture is paused. Nothing you copy is recorded until you run Resume Clipboard Capture.";

    /// <summary>Shown by Pause Clipboard Capture when the pause could not be written to the file that remembers it.</summary>
    public const string PausedUnsavedNote =
        "Clipboard capture is paused for now, but that could not be saved. It will be on again the next time Beacon starts unless you pause it again then.";

    /// <summary>Shown by Resume Clipboard Capture when the listener could not be started.</summary>
    public const string ListenerFailedNote = "Clipboard capture could not be started. The log has the details.";

    /// <summary>Shown by Resume Clipboard Capture.</summary>
    public const string ResumedNote = "Clipboard capture is on again.";

    /// <summary>Shown by Resume Clipboard Capture when this copy of Beacon may not write the history.</summary>
    public const string ResumedButOffNote =
        "Clipboard capture is not paused, but this copy of Beacon cannot record: another copy is using the data folder, or the folder cannot be written.";

    /// <summary>Shown at startup when the file that remembers the pause could not be understood.</summary>
    public const string StateUnreadableNote =
        "Beacon could not read whether clipboard capture was paused, so it is paused. Run Resume Clipboard Capture to turn it on.";

    /// <summary>Shown once when capture is not paused and this copy of Beacon still cannot record.</summary>
    public const string NotRecordingNote =
        "Clipboard history is not being recorded by this copy of Beacon: another copy is using the data folder, or the folder cannot be written. Details are in the log file.";

    /// <summary>The tray icon's text while capture is on.</summary>
    public const string OnTooltip = "Beacon";

    /// <summary>The tray icon's text while capture is paused.</summary>
    public const string PausedTooltip = "Beacon (clipboard capture paused)";

    /// <summary>The tray icon's text while capture is not paused and still not running.</summary>
    public const string OffTooltip = "Beacon (clipboard capture off)";

    // A marker holds one 32-bit number. A block much larger than that is not a marker Beacon
    // understands, and is taken to say "no".
    private const int MaxMarkerBytes = 64;

    // FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS:
    // the file's content is not on this disk, and reading it would make a sync tool fetch it.
    private const int NotOnThisDisk = 0x1000 | 0x40000 | 0x400000;

    private readonly IClipboardReader _reader;
    private readonly IClipboardListener _listener;
    private readonly IImageCodec _codec;
    private readonly Action<Action<ClipStore>> _post;
    private readonly Func<bool> _canWrite;
    private readonly Func<ClipboardOptions> _options;
    private readonly string _stateFile;
    private readonly Action<TimeSpan> _wait;
    private readonly Action<string, Exception?> _report;
    private readonly Action _stateChanged;
    private readonly TimeProvider _time;
    private readonly Func<string, Stream> _openPicture;
    private readonly Func<Stream, FileAttributes> _attributesOf;
    private readonly Func<string, FileAttributes> _attributesOfPath;
    private readonly Func<TimeSpan> _settle;

    // How many changes the listener has told of, counted on the UI thread, and the count as
    // it stood when the clipboard was last left alone long enough to be read. Only the
    // history's background task reads and writes the second.
    private long _changes;
    private long _answered;
    private volatile bool _paused;

    // True from Stop on: Beacon is closing and nothing is listened to again.
    private volatile bool _stopped;
    private bool _listening;

    // True when whether capture is paused could not be written to its file, and is still to be.
    private bool _unsaved;

    // True when listening is wanted and starting the listener failed.
    private bool _listenerFailed;

    // Moved on by every pause, and by every time listening stops. Work that was put in the line
    // carries the value of the moment, and does nothing when it finds the value has moved on:
    // it is about a time when capture was not running as it is now.
    private int _epoch;

    // What is remembered of the last copy that was read, in memory only and never written
    // anywhere: the clipboard's change number as it stood after the reading, and a fingerprint
    // of the blocks that were read. "Known" is false until capture has looked at the clipboard
    // once, and again whenever that look failed. Read and written by the one background task and
    // by the UI thread when listening starts, so under a lock.
    private readonly Lock _seen = new();
    private bool _known;
    private uint? _lastSequence;
    private byte[]? _lastPrint;

    /// <param name="post">Puts work in the history's line; <see cref="ClipHistoryService.Post"/>. A test runs it at once.</param>
    /// <param name="canWrite">True when this copy of Beacon may write the history; <see cref="ClipHistoryService.IsWriter"/>.</param>
    /// <param name="options">The clipboard settings in use, read again for every change.</param>
    /// <param name="stateFile">The file that remembers whether capture is paused.</param>
    /// <param name="wait">Waits for the given time between two tries. <c>Thread.Sleep</c> in the app; a test passes its own.</param>
    /// <param name="report">Receives fixed words and counts for the log, and an exception that is logged by type only.</param>
    /// <param name="stateChanged">Called on the UI thread when <see cref="Tooltip"/> may have changed.</param>
    /// <param name="time">Tells how long the picture files of one copy have been read. Left out everywhere except in tests: the system's.</param>
    /// <param name="openPicture">Opens a picture file that was copied, for reading, and answers the stream or throws. Left out everywhere except in tests: the file, opened so that a program that has it open for writing does not stop the reading.</param>
    /// <param name="attributesOf">Says what attributes what was opened has. Left out everywhere except in tests: those of the file's handle.</param>
    /// <param name="attributesOfPath">Says what attributes a path has, before anything is opened, or throws. Left out everywhere except in tests: those the file system gives for the path.</param>
    /// <param name="settle">
    /// Gives how long the clipboard has to have been left alone before it is opened for a
    /// change; see <see cref="DefaultSettle"/>. Asked again for every change. Left out, or
    /// zero, the clipboard is opened as soon as the change has its turn.
    /// </param>
    public ClipCapture(
        IClipboardReader reader,
        IClipboardListener listener,
        IImageCodec codec,
        Action<Action<ClipStore>> post,
        Func<bool> canWrite,
        Func<ClipboardOptions> options,
        string stateFile,
        Action<TimeSpan> wait,
        Action<string, Exception?>? report = null,
        Action? stateChanged = null,
        TimeProvider? time = null,
        Func<string, Stream>? openPicture = null,
        Func<Stream, FileAttributes>? attributesOf = null,
        Func<string, FileAttributes>? attributesOfPath = null,
        Func<TimeSpan>? settle = null)
    {
        _settle = settle ?? (() => TimeSpan.Zero);
        _attributesOfPath = attributesOfPath ?? AttributesOfPath;
        _time = time ?? TimeProvider.System;
        _openPicture = openPicture ?? OpenPictureFile;
        _attributesOf = attributesOf ?? AttributesOfFile;
        _reader = reader;
        _listener = listener;
        _codec = codec;
        _post = post;
        _canWrite = canWrite;
        _options = options;
        _stateFile = stateFile;
        _wait = wait;
        _report = report ?? ((_, _) => { });
        _stateChanged = stateChanged ?? (() => { });
    }

    /// <summary>True while the owner has capture paused.</summary>
    public bool Paused => _paused;

    /// <summary>True while Beacon is being told of clipboard changes.</summary>
    public bool Listening => _listening;

    /// <summary>What the tray icon says about capture. Fixed words.</summary>
    public string Tooltip => _paused ? PausedTooltip : _listening ? OnTooltip : OffTooltip;

    /// <summary>
    /// Call once at startup. Reads whether capture was paused when Beacon last ran; nothing is
    /// listened to before that is known.
    /// </summary>
    /// <returns>A sentence to show the owner, or null when there is nothing to say.</returns>
    public string? Start()
    {
        var state = ClipboardState.Load(_stateFile);
        _paused = state.Paused;
        if (_paused)
        {
            Report("Clipboard capture is paused", null);
        }

        Refresh();
        StateChanged();
        return state.Unreadable ? StateUnreadableNote : null;
    }

    /// <summary>Stops recording until <see cref="Resume"/>, and remembers that across restarts.</summary>
    /// <returns>The sentence to show the owner.</returns>
    public string Pause()
    {
        _paused = true;
        Interlocked.Increment(ref _epoch);

        // The listener is stopped before the file is written, which can take a moment.
        RefreshListening();
        bool saved = Remember();
        Report("Clipboard capture is paused", null);
        StateChanged();
        return saved ? PausedNote : PausedUnsavedNote;
    }

    /// <summary>
    /// Records again. What was copied while capture was paused is not recorded: it is looked at
    /// once, on the capture line, to be remembered as dealt with. Nothing is read on the calling
    /// thread.
    /// </summary>
    /// <returns>The sentence to show the owner.</returns>
    public string Resume()
    {
        _paused = false;

        // When this cannot be saved the next start is paused, which is the safe side. It is
        // tried again at every refresh.
        Remember();
        RefreshListening();
        StateChanged();
        return _listening ? ResumedNote : _listenerFailed ? ListenerFailedNote : ResumedButOffNote;
    }

    /// <summary>
    /// Starts or stops listening so that it matches how things stand: listening exactly when
    /// capture is not paused and this copy of Beacon may write the history. Also tries once
    /// more to save whether capture is paused, when that could not be saved before. Call after
    /// the history has been configured or has changed.
    /// </summary>
    public void Refresh()
    {
        RetrySave();
        RefreshListening();
    }

    /// <summary>
    /// For when Beacon closes: the object is finished. Moves the pause counter on, so that work
    /// that was put in the line and has not run, work that is running (it reads no more of the
    /// clipboard or of picture files, and records nothing) and the look at the clipboard do
    /// nothing more, and stops the listener if it is listening. It does not change whether
    /// capture is paused, and does not write the file that remembers that. Afterwards
    /// <see cref="Start"/>, <see cref="Resume"/> and <see cref="Refresh"/> do not listen again.
    /// For the UI thread, like the other members that start and stop listening.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        Interlocked.Increment(ref _epoch);
        RefreshListening();
    }

    private void RefreshListening()
    {
        bool wanted = !_stopped && !_paused && CanWrite();
        if (!wanted)
        {
            // Not listening is then as it should be, and nothing failed.
            _listenerFailed = false;
        }

        if (wanted == _listening)
        {
            return;
        }

        try
        {
            if (wanted)
            {
                // In this order, and it matters. First what is remembered is "not known", so that
                // nothing of an earlier time is taken for what is on the clipboard now. Then the
                // look at the clipboard is put in the capture line, before the listener is started:
                // the line runs in order, so the look comes before every capture that the listener
                // causes. Then the listener is started. Nothing is read on this thread: the look
                // asks the program that owns the clipboard to make every form it offers, and how
                // long that takes is up to that program.
                int epoch;
                lock (_seen)
                {
                    _known = false;
                    _lastPrint = null;
                    _lastSequence = null;
                    epoch = Volatile.Read(ref _epoch);
                }

                PostTheLook(epoch);
                _listener.Start(ClipboardChanged);
                _listening = true;
                _listenerFailed = false;
                Report("Clipboard capture started", null);
            }
            else
            {
                _listening = false;
                Interlocked.Increment(ref _epoch);
                _listener.Stop();
                Report("Clipboard capture stopped", null);
            }
        }
        catch (Exception ex)
        {
            // Told apart from a copy of Beacon that may not write, which is not a failure.
            _listenerFailed = wanted;
            Report("The clipboard listener could not be started or stopped", ex);
        }

        StateChanged();
    }

    // Called by the listener on the UI thread, straight from a window message: it must be quick
    // and must not throw. All it does is put the reading in the line.
    private void ClipboardChanged()
    {
        try
        {
            if (!_paused && !_stopped)
            {
                int epoch = Volatile.Read(ref _epoch);
                Interlocked.Increment(ref _changes);
                _post(store => Capture(store, epoch));
            }
        }
        catch (Exception ex)
        {
            Report("A clipboard change could not be put in line to be recorded", ex);
        }
    }

    // Puts the look at the clipboard in the capture line. If that cannot be done, or the work is
    // dropped by the line, what is remembered stays "not known".
    private void PostTheLook(int epoch)
    {
        try
        {
            _post(_ => Look(epoch));
        }
        catch (Exception ex)
        {
            Report("The look at the clipboard when capture started could not be put in line, so the first change after it is not recorded", ex);
        }
    }

    // Runs on the history's background task, in its turn: looks once at what is on the clipboard
    // when listening started, and remembers it as dealt with, through the same reading and the
    // same rules as any copy. It only remembers (the change number and the fingerprint) and
    // records nothing, whatever it finds: what is there was not copied while Beacon was
    // listening, and may have been copied while capture was paused. Without this, the first
    // message that is not about a new copy (a program that makes a format only when it is asked
    // for tells of a change when it is asked) would record it. Content that the rules refuse is
    // not read here either. When this does not happen (the work is dropped because capture was
    // paused since, or the clipboard cannot be read, or a form that is offered cannot be read)
    // what is remembered stays "not known", and the first change that is told of is only noted.
    private void Look(int epoch)
    {
        try
        {
            if (_paused || epoch != Volatile.Read(ref _epoch))
            {
                return;
            }

            var options = _options();
            if (ReadWithRetry(open => Take(open, epoch, options, atStart: true), epoch) is not { } offered)
            {
                return;
            }

            if (offered.Unreadable)
            {
                // A form that is offered and cannot be read now may be readable later, and then
                // the same copy would look like another one.
                Report("The clipboard could not be read completely when capture started, so the first change after it is not recorded", null);
                return;
            }

            Note(epoch, offered.Sequence, Fingerprint(offered));
        }
        catch (Exception ex)
        {
            Report("The clipboard could not be looked at when capture started, so the first change after it is not recorded", ex is ClipboardBusyException ? null : ex);
        }
    }

    // Runs on the history's background task.
    private void Capture(ClipStore store, int epoch)
    {
        // True from the moment this change's own content has been read and noted. Only then is
        // there anything of this change to forget when something fails after it.
        bool noted = false;
        try
        {
            // Asked again here: capture may have been paused, or listening stopped and started
            // again, or the folder lost, while this waited its turn.
            if (_paused || epoch != Volatile.Read(ref _epoch) || !store.IsWriter)
            {
                return;
            }

            // Not before the program that is copying has finished with the clipboard.
            if (!Settled(epoch))
            {
                return;
            }

            var options = _options();
            if (ReadWithRetry(open => Take(open, epoch, options, atStart: false), epoch) is not { } offered)
            {
                return;
            }

            // The first thing done with what was read: a copy that is the one before is dropped
            // here, without the picture being made again or any picture file being read again.
            var seen = Note(epoch, offered.Sequence, Fingerprint(offered));
            noted = true;
            switch (seen)
            {
                case Seen.Same:
                case Seen.Stale:
                    return;
                case Seen.Unknown:
                    Report("The first clipboard change after capture started was not recorded, because what was on the clipboard before it is not known", null);
                    return;
            }

            var draft = Draft(offered, epoch, out int leftOut);

            // Capture was paused or stopped while the picture files were read: nothing is recorded
            // of a copy that was being worked on then.
            if (epoch != Volatile.Read(ref _epoch))
            {
                return;
            }

            if (leftOut > 0)
            {
                Report($"{leftOut} parts of what was copied were over their size limit and were left out", null);
            }

            if (draft.IsEmpty)
            {
                if (leftOut > 0)
                {
                    Report("A clipboard change was not recorded: nothing in it was within its size limit", null);
                }

                return;
            }

            store.Add(draft);
        }
        catch (ClipboardBusyException)
        {
            Report("The clipboard stayed in use by another program, so one change was not recorded", null);
        }
        catch (Exception ex)
        {
            // Nobody is above this on a background task, and one copy that cannot be recorded
            // must not stop the next. Nor must it stop the owner from copying it once more, so
            // when it was read and noted it is forgotten. A failure before that, at the reader or
            // anywhere else, has nothing of this change to forget, and what was remembered of the
            // copy before it, which can be what the look at the start took, stays.
            if (noted)
            {
                ForgetLast();
            }

            Report("A clipboard change could not be recorded", ex);
        }
    }

    // Runs on the history's background task, before the clipboard is opened for a change.
    // Waits until the clipboard has been left alone for the settle time: nothing more was told
    // of it, and no window was found holding it open. Whatever stirs meanwhile starts the wait
    // again, so the reading that follows is of the copy as its program left it, and that one
    // reading answers every change told of until then. Nothing is opened while waiting.
    //
    // False when there is nothing to read after all: capture was paused or stopped meanwhile,
    // or a reading made since this change was told of has answered it already.
    private bool Settled(int epoch)
    {
        TimeSpan settle = _settle();
        if (settle <= TimeSpan.Zero)
        {
            return true;
        }

        if (settle > MaxSettle)
        {
            settle = MaxSettle;
        }

        long told = Interlocked.Read(ref _changes);
        if (told == _answered)
        {
            return false;
        }

        TimeSpan quiet = TimeSpan.Zero;
        TimeSpan waited = TimeSpan.Zero;
        while (quiet < settle && waited < SettleLimit)
        {
            TimeSpan step = settle - quiet < SettlePoll ? settle - quiet : SettlePoll;
            Wait(step);
            waited += step;
            if (Moved(epoch))
            {
                return false;
            }

            long now = Interlocked.Read(ref _changes);
            if (now != told || InUse())
            {
                // Somebody is still at it: the wait starts again.
                told = now;
                quiet = TimeSpan.Zero;
            }
            else
            {
                quiet += step;
            }
        }

        _answered = told;
        return true;
    }

    private bool InUse()
    {
        try
        {
            return _reader.InUse();
        }
        catch (Exception)
        {
            // What cannot be asked is taken for free: the opening itself says otherwise if it is not.
            return false;
        }
    }

    // Tries again while another program has the clipboard open, unless capture was paused or
    // stopped meanwhile: the clipboard is not opened again then, and the answer is "nothing".
    private T? ReadWithRetry<T>(Func<IOpenClipboard, T> read, int epoch)
    {
        for (int tries = 1; ; tries++)
        {
            if (tries > 1 && epoch != Volatile.Read(ref _epoch))
            {
                return default;
            }

            try
            {
                return _reader.Read(read);
            }
            catch (ClipboardBusyException) when (tries < Attempts)
            {
                Wait(RetryPause);
            }
        }
    }

    // Runs while the clipboard is held open, so it only looks and copies. First the things that
    // decide whether anything may be kept; the content itself is not read when the answer is no.
    private Offered? Take(IOpenClipboard open, int epoch, ClipboardOptions options, bool atStart)
    {
        // Asked again here, with the clipboard held: what is read below cannot have been copied
        // after capture was paused, because nothing can be copied while it is held.
        if (_paused || epoch != Volatile.Read(ref _epoch))
        {
            return null;
        }

        uint sequence = open.Sequence;
        if (!atStart)
        {
            lock (_seen)
            {
                if (_known && sequence == _lastSequence)
                {
                    // Windows often tells of one copy more than once. This one has been dealt with,
                    // and nothing but the change number has been asked for.
                    return null;
                }
            }
        }

        // Asked here, with the clipboard held, and not before it was opened: while another program
        // has it open a copy can land, and it is that copy that is read below. Nobody can change
        // who owns it until it is closed. A process lookup costs, so it is only made for a change
        // that is new.
        var owner = _reader.Owner();

        var verdict = CaptureRules.Judge(
            owner,
            // Either of the two formats that say "leave this alone", by its presence alone: the
            // older one is the only one that several password managers set.
            open.Has(ClipboardFormat.KeepOutOfMonitors) || open.Has(ClipboardFormat.ViewerIgnore),
            Marker(open, ClipboardFormat.CanIncludeInHistory),
            Marker(open, ClipboardFormat.CanUploadToCloud),
            options.Excludes);
        if (verdict != CaptureVerdict.Keep)
        {
            // What is on the clipboard now is not what was read before, whatever that was. The
            // change number that is remembered is the one read now, after the markers: a program
            // that makes its content when it is asked changes it while they are read, and the
            // message that follows is about this same copy.
            lock (_seen)
            {
                if (epoch == _epoch)
                {
                    _known = true;
                    _lastSequence = open.Sequence;
                    _lastPrint = null;
                }
            }

            return null;
        }

        // Before each form is copied, the counter is compared with the one this work carries:
        // capture may have been paused or stopped while the clipboard was held (a program that
        // makes its content only when it is asked can take a long time over one form). When it
        // has moved, no further form is asked for, and the reading ends here and is thrown away,
        // as a stale reading is at the top.
        if (Moved(epoch))
        {
            return null;
        }

        var text = Copy(open, ClipboardFormat.UnicodeText, (2 * ClipLimits.MaxTextBytes) + ClipLimits.BlockSlack);
        if (Moved(epoch))
        {
            return null;
        }

        var html = Copy(open, ClipboardFormat.Html, ClipLimits.MaxTextBytes + ClipLimits.BlockSlack);
        if (Moved(epoch))
        {
            return null;
        }

        var rtf = Copy(open, ClipboardFormat.Rtf, ClipLimits.MaxTextBytes + ClipLimits.BlockSlack);
        if (Moved(epoch))
        {
            return null;
        }

        var bitmap = Copy(open, ClipboardFormat.Dib, ClipLimits.MaxBitmapBytes, BmpFile.FileHeaderSize);
        if (Moved(epoch))
        {
            return null;
        }

        var files = Copy(open, ClipboardFormat.FileDrop, ClipLimits.MaxFileListBytes);
        if (Moved(epoch))
        {
            return null;
        }

        // At the look only: a form that is offered, gave no block, and was not over its limit
        // could not be read, which can be because its program did not answer in time.
        bool unreadable = atStart
            && (Unread(open, ClipboardFormat.UnicodeText, text)
                || Unread(open, ClipboardFormat.Html, html)
                || Unread(open, ClipboardFormat.Rtf, rtf)
                || Unread(open, ClipboardFormat.Dib, bitmap)
                || Unread(open, ClipboardFormat.FileDrop, files));

        // Read last: a program that makes its content only when it is asked for changes the
        // number while the blocks above are read, and the message that follows is about this
        // same copy.
        return new Offered(owner, open.Sequence, text, html, rtf, bitmap, files, unreadable);
    }

    // True when capture was paused, or stopped, or stopped listening since this work was put in
    // line: the work carries the counter of its time.
    private bool Moved(int epoch) => _paused || epoch != Volatile.Read(ref _epoch);

    private static bool Unread(IOpenClipboard open, ClipboardFormat format, Block block) =>
        block.Bytes is null && !block.TooLarge && open.Has(format);

    private static Block Copy(IOpenClipboard open, ClipboardFormat format, int maxBytes, int reserve = 0)
    {
        byte[]? bytes = open.Bytes(format, maxBytes, out bool tooLarge, reserve);
        return new Block(bytes, tooLarge);
    }

    // A SHA-256 over the blocks as they were read: for each form that was there, which form it
    // is, its length and its bytes (a block that was too large to read is only said to be that).
    // Taken before anything is made of the blocks, so that a copy that has been dealt with
    // costs no more than the reading and this.
    private static byte[] Fingerprint(Offered offered)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, 1, offered.Text, 0);
        Append(hash, 2, offered.Html, 0);
        Append(hash, 3, offered.Rtf, 0);

        // The picture's block has room in front of it for the file header that is put there later.
        Append(hash, 4, offered.Bitmap, BmpFile.FileHeaderSize);
        Append(hash, 5, offered.Files, 0);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, byte form, Block block, int skip)
    {
        Span<byte> header = stackalloc byte[9];
        header[0] = form;
        if (block.TooLarge)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header[1..], -1);
            hash.AppendData(header);
        }
        else if (block.Bytes is { } bytes)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header[1..], bytes.Length - skip);
            hash.AppendData(header);
            hash.AppendData(bytes.AsSpan(skip));
        }
    }

    private enum Seen
    {
        /// <summary>Not the copy that was read last.</summary>
        New,

        /// <summary>The copy that was read last: the same blocks, whatever the change number says.</summary>
        Same,

        /// <summary>It is not known what was on the clipboard before this: capture could not look when it started.</summary>
        Unknown,

        /// <summary>Capture was paused, or stopped listening, since this was put in line.</summary>
        Stale,
    }

    // Compares what was read with what was read last, and remembers it from now on, whatever
    // the answer. A pause since the work was put in line is told here too, under the lock that
    // the look at the start of listening takes: work of the time before the pause must not
    // overwrite what that look remembered.
    private Seen Note(int epoch, uint sequence, byte[] print)
    {
        lock (_seen)
        {
            if (epoch != Volatile.Read(ref _epoch))
            {
                return Seen.Stale;
            }

            var seen = !_known ? Seen.Unknown
                : _lastPrint is not null && _lastPrint.AsSpan().SequenceEqual(print) ? Seen.Same
                : Seen.New;
            _known = true;
            _lastSequence = sequence;
            _lastPrint = print;
            return seen;
        }
    }

    // A copy that was read and noted and then could not be recorded must not stop the owner
    // from copying it again, nor a message that tells of it once more from being looked at: its
    // fingerprint and its change number are forgotten.
    private void ForgetLast()
    {
        lock (_seen)
        {
            _lastPrint = null;
            _lastSequence = null;
        }
    }

    // A marker that is there but is not a block Beacon can read is taken to say "no".
    private static byte[]? Marker(IOpenClipboard open, ClipboardFormat format)
    {
        if (!open.Has(format))
        {
            return null;
        }

        return open.Bytes(format, MaxMarkerBytes, out _) ?? [];
    }

    // Runs after the clipboard has been closed: this is where the slow work is done.
    private ClipDraft Draft(Offered offered, int epoch, out int leftOut)
    {
        leftOut = offered.TooLarge;

        string? text = offered.Text.Bytes is null ? null : ClipboardBytes.Text(offered.Text.Bytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Nothing that can be seen is nothing to keep.
            text = null;
        }
        else if (Encoding.UTF8.GetByteCount(text) > ClipLimits.MaxTextBytes)
        {
            text = null;
            leftOut++;
        }

        byte[]? html = Within(offered.Html.Bytes, ref leftOut);
        byte[]? rtf = Within(offered.Rtf.Bytes, ref leftOut);
        byte[]? png = Png(offered, ref leftOut);

        IReadOnlyList<string> files = offered.Files.Bytes is null ? [] : ClipboardBytes.FileList(offered.Files.Bytes) ?? [];
        if (files.Count > ClipLimits.MaxFiles)
        {
            files = [];
            leftOut++;
        }

        return new ClipDraft(ClipboardOptions.ProgramName(offered.Owner.Program))
        {
            Text = text,
            Html = html,
            Rtf = rtf,
            ImagePng = png,
            Files = files,
            Pictures = Pictures(files, epoch),
            Label = html is not null && ClipLinks.Address(text) is { } address ? ClipLinks.Label(html, address) : null,
        };
    }

    // HTML and rich text are kept byte for byte, up to the zero byte that ends them.
    private static byte[]? Within(byte[]? block, ref int leftOut)
    {
        if (block is null)
        {
            return null;
        }

        byte[] content = ClipboardBytes.UpToZero(block);
        if (content.Length == 0)
        {
            return null;
        }

        if (content.Length > ClipLimits.MaxTextBytes)
        {
            leftOut++;
            return null;
        }

        return content;
    }

    // The picture is taken out of what was offered here, and not held by anything of the caller
    // when this returns: it can be a quarter of a gigabyte, and it is not wanted while the
    // picture files that were copied are read.
    private byte[]? Png(Offered offered, ref int leftOut)
    {
        if (offered.TakeBitmap() is not { } bitmap)
        {
            return null;
        }

        if (!BmpFile.TryAddFileHeader(bitmap))
        {
            Report("A picture on the clipboard was in a form that is not read", null);
            return null;
        }

        byte[] png;
        try
        {
            png = _codec.BmpToPng(bitmap);
        }
        catch (Exception ex)
        {
            // The rest of what was copied is still kept.
            Report("A picture on the clipboard could not be turned into a PNG", ex);
            return null;
        }

        if (png.Length > ClipLimits.MaxImageBytes)
        {
            leftOut++;
            return null;
        }

        return png;
    }

    // The pictures among the copied files, with their content. A path means nothing once the
    // file is deleted, or on the owner's other machine, so a picture is kept itself. Only files
    // whose name ends like a picture are opened; no other copied file is ever read.
    //
    // The paths are the owner's copy and could name anything: a pipe of another program, a
    // device, a share on a machine that does not answer. A read of one of those waits, and this
    // runs on the history's one line, so while it waits later copies are lost and deleting
    // waits too. So, for each name with a picture's extension, in this order, and the first
    // that applies decides: once twenty are kept nothing more is touched; a time limit holds
    // for the whole list; a name that stands for a pipe or a device is not touched; after one
    // share path has failed no other share path of the list is touched; and what is left has
    // the attributes of its path read, and is not opened at all when they say its content is
    // not on this disk; only then is it opened, once, and the same attributes, whether it can
    // seek and its length are asked of what was opened. A failure to read the attributes is a
    // failure to open, also for the share rule.
    private IReadOnlyList<DraftPicture> Pictures(IReadOnlyList<string> files, int epoch)
    {
        var kept = new List<DraftPicture>();
        long total = 0;
        int notKept = 0;
        long began = _time.GetTimestamp();
        bool shareFailed = false;
        foreach (string path in files)
        {
            if (ClipPaths.PictureExtensionOf(path) is not { } extension)
            {
                continue;
            }

            // Paused or stopped meanwhile: no further file is touched, and the caller drops the copy.
            if (epoch != Volatile.Read(ref _epoch))
            {
                return kept;
            }

            bool onShare = IsSharePath(path);
            if (kept.Count >= ClipLimits.MaxPictures
                || _time.GetElapsedTime(began) >= PicturesTimeLimit
                || ClipboardBytes.StandsForPipeOrDevice(path)
                || (onShare && shareFailed))
            {
                notKept++;
                continue;
            }

            try
            {
                byte[]? bytes = ReadPicture(path, total, out long length);
                if (bytes is null)
                {
                    notKept++;
                    continue;
                }

                kept.Add(new DraftPicture(Path.GetFileName(path), extension, bytes));
                total += length;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A folder with a name like a picture's, or a file that has already gone. Nothing
                // to say, but a share that has lost its file may have lost more.
                shareFailed |= onShare;
            }
            catch (Exception)
            {
                // Whatever it was, this one picture is not kept and the rest of the copy is.
                shareFailed |= onShare;
                notKept++;
            }
        }

        if (notKept > 0)
        {
            Report($"{notKept} pictures in a copied file list were listed by their path only", null);
        }

        return kept;
    }

    // Reads the attributes of the path, opens the file once if they allow, and asks the rest of
    // what was opened. Null when it is not to be kept. The length that counts, for the limit of one picture and of all of them, is the one
    // of what was opened, and is given back for the caller to add up.
    private byte[]? ReadPicture(string path, long total, out long length)
    {
        length = 0;

        // First the attributes of the path, and only then the file is opened: a file whose
        // content is not on this disk is not opened at all, because opening one that is marked
        // recall on open is what makes a sync tool fetch it from its server. A path whose
        // attributes cannot be read fails as an open does.
        if (((int)_attributesOfPath(path) & NotOnThisDisk) != 0)
        {
            return null;
        }

        using Stream stream = _openPicture(path);

        // A pipe or a device is not a file on a disk, and a read from one can wait for ever.
        if (!stream.CanSeek)
        {
            return null;
        }

        // The same attributes again, of what was opened: a link's own attributes can differ from
        // those of its target, and the file may by then be another one. Nothing is read.
        if (((int)_attributesOf(stream) & NotOnThisDisk) != 0)
        {
            return null;
        }

        length = stream.Length;
        if (length > ClipLimits.MaxImageBytes || total + length > ClipLimits.MaxPicturesBytes)
        {
            return null;
        }

        byte[] bytes = new byte[length];
        int filled = 0;
        while (filled < bytes.Length)
        {
            int read = stream.Read(bytes, filled, bytes.Length - filled);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        if (filled == bytes.Length)
        {
            // One byte more than it was said to have is enough to know that the file has grown
            // since it was opened. A file that is still being written is not one to keep, and
            // none of what follows is read.
            if (stream.ReadByte() >= 0)
            {
                return null;
            }
        }
        else
        {
            // It has become shorter: what is left of it is what there is.
            Array.Resize(ref bytes, filled);
        }

        return bytes;
    }

    private static bool IsSeparator(char character) => character is '\\' or '/';

    // Two separators in front: a share, which is on another machine.
    private static bool IsSharePath(string path) => path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    // Opened so that a program which has the picture open, even for writing, does not stop the
    // reading. A folder with a picture's name is told as a file that is not there.
    private static Stream OpenPictureFile(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            throw new FileNotFoundException();
        }
    }

    // Asked of the handle that was opened as well as of the path: a link's own attributes can
    // differ from those of what it points to, and the file may by then be another one.
    private static FileAttributes AttributesOfFile(Stream stream) =>
        stream is FileStream file ? File.GetAttributes(file.SafeFileHandle) : 0;

    // Asked of the path before anything is opened, because opening is what makes a sync tool
    // fetch a file that is not on this disk. Throws when the file is not there or cannot be
    // reached, which is handled as a failure to open it.
    private static FileAttributes AttributesOfPath(string path) => File.GetAttributes(path);

    private bool CanWrite()
    {
        try
        {
            return _canWrite();
        }
        catch (Exception ex)
        {
            Report("Whether the clipboard history can be written could not be told", ex);
            return false;
        }
    }

    // Writes whether capture is paused, trying SaveAttempts times with a wait between the tries:
    // a sharing violation can be passing, such as a scanner that is looking at the file.
    // Capture is paused or resumed for this run whatever the answer.
    private bool Remember()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ClipboardState.Save(_stateFile, _paused);
                _unsaved = false;
                return true;
            }
            catch (Exception ex)
            {
                if (attempt < SaveAttempts)
                {
                    Wait(SavePause);
                    continue;
                }

                _unsaved = true;
                Report("Whether clipboard capture is paused could not be remembered for the next start", ex);
                return false;
            }
        }
    }

    // One more try, at a refresh, for a pause or resume that could not be saved. It does not
    // wait: a refresh is on the UI thread, and the owner has been told already.
    private void RetrySave()
    {
        if (!_unsaved)
        {
            return;
        }

        try
        {
            ClipboardState.Save(_stateFile, _paused);
        }
        catch (Exception)
        {
            return;
        }

        _unsaved = false;
        Report("Whether clipboard capture is paused could be remembered after all", null);
    }

    private void Wait(TimeSpan time)
    {
        try
        {
            _wait(time);
        }
        catch (Exception)
        {
            // A wait that fails is a wait that is over.
        }
    }

    // The callbacks are the caller's code and can fail. That must not turn into an exception
    // out of a window message or a background task.
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

    private void StateChanged()
    {
        try
        {
            _stateChanged();
        }
        catch (Exception)
        {
            // The tray icon's text is a courtesy.
        }
    }

    /// <summary>One block as the clipboard held it, or null when it was not there, or that it was too large to be read.</summary>
    private readonly record struct Block(byte[]? Bytes, bool TooLarge);

    /// <summary>Who copied, the blocks one reading of the clipboard gave, and how many were too large to read.</summary>
    private sealed class Offered
    {
        // Held in a field of its own, and not in a Block that is kept: that is how it is let go of.
        private byte[]? _bitmap;
        private readonly bool _bitmapTooLarge;

        public Offered(ClipboardOwner owner, uint sequence, Block text, Block html, Block rtf, Block bitmap, Block files, bool unreadable = false)
        {
            Owner = owner;
            Sequence = sequence;
            Unreadable = unreadable;
            Text = text;
            Html = html;
            Rtf = rtf;
            Files = files;
            _bitmap = bitmap.Bytes;
            _bitmapTooLarge = bitmap.TooLarge;
            TooLarge = new[] { text, html, rtf, bitmap, files }.Count(block => block.TooLarge);
        }

        public ClipboardOwner Owner { get; }

        /// <summary>The clipboard's change number after the blocks were read: a program that makes its content when asked changes it meanwhile.</summary>
        public uint Sequence { get; }

        /// <summary>At the look only: a form was offered that gave no block although it was not over its limit.</summary>
        public bool Unreadable { get; }

        public Block Text { get; }

        public Block Html { get; }

        public Block Rtf { get; }

        public Block Files { get; }

        /// <summary>The picture's block, until <see cref="TakeBitmap"/> hands it over.</summary>
        public Block Bitmap => new(_bitmap, _bitmapTooLarge);

        public int TooLarge { get; }

        /// <summary>
        /// The clipboard's picture, which is handed over once and then no longer held here: it can
        /// be a quarter of a gigabyte, and it is not wanted while the picture files that were
        /// copied are read.
        /// </summary>
        public byte[]? TakeBitmap()
        {
            byte[]? bytes = _bitmap;
            _bitmap = null;
            return bytes;
        }
    }
}
