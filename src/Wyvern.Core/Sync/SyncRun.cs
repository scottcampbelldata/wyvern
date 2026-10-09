using System.Globalization;
using System.Security.Cryptography;
using Wyvern.Core.Platform;

namespace Wyvern.Core.Sync;

/// <summary>How a sync run ended.</summary>
public enum SyncEnd
{
    /// <summary>Every action was tried. <see cref="SyncResult.LeftForLater"/> and <see cref="SyncResult.BadBlobs"/> say what it could not do.</summary>
    Completed,

    /// <summary>Wyvern is closing: the run stopped between two files.</summary>
    Stopped,

    /// <summary>The server could not be used; <see cref="SyncResult.Failure"/> says how. What was done before is recorded.</summary>
    Failed,

    /// <summary>The data folder is not there or cannot be listed. Nothing was done: files that cannot be seen are not files that were deleted.</summary>
    NoDataFolder,

    /// <summary>
    /// This machine's clock and the server's are more than a day apart. Nothing was written to
    /// the server. When the run's plan held only downloads, those that came before the first
    /// one that led to a write were carried out, and are recorded.
    /// </summary>
    ClockWrong,

    /// <summary>
    /// The brake on mass deletion held the run: its plan held deletion notes for library files
    /// (<see cref="SyncResult.HeldDeletions"/>) that are not sent without the owner's word,
    /// because they are many, or because sync was paused already
    /// (<see cref="SyncRunContext.Paused"/>) and then one is enough. Nothing was written to
    /// the server, nothing was downloaded, no file of the owner's was changed, and the record
    /// of the last sync was not written. The server was asked for its listing and for nothing
    /// else. One thing in the data folder can have gone all the same: the look at the folder,
    /// which comes before the plan, removes sync's own leftover temporary files, those that a
    /// write of its own left behind more than an hour ago when it was cut off
    /// (<see cref="SyncFiles.RemoveLeftover"/>), in a held run as in any other. Sync is paused
    /// from here on, which is for whoever started the run to remember.
    /// </summary>
    Held,
}

/// <summary>What a sync run did. Counts only: no path and no name.</summary>
public sealed class SyncResult
{
    public SyncEnd End { get; internal set; }

    /// <summary>Why the server could not be used, when <see cref="End"/> is <see cref="SyncEnd.Failed"/>.</summary>
    public RemoteFailure? Failure { get; internal set; }

    public int Uploaded { get; internal set; }

    public int Downloaded { get; internal set; }

    public int DeletedHere { get; internal set; }

    public int TombstonesSent { get; internal set; }

    /// <summary>
    /// When <see cref="End"/> is <see cref="SyncEnd.Held"/>: how many library files that were
    /// synced are no longer in the data folder, which is how many deletion notes the run would
    /// have sent. Never zero then, and it can be one: while sync is paused a run is held for a
    /// single file. Otherwise zero.
    /// </summary>
    public int HeldDeletions { get; internal set; }

    /// <summary>
    /// True when the run made its plan, the plan held no deletion note for a library file, and
    /// the run could look at every library path that the record holds as live: nothing that
    /// was synced is missing here in a way this run, or one like it, would send as a deletion.
    /// This is what ends a pause (see <see cref="SyncRunContext.Paused"/>). False for a run
    /// that ended before its plan was made, for one whose plan held such a note, whether the
    /// run was held or sent it, and for one that could not look at a part of the library, where
    /// a file that is missing is not seen to be missing.
    /// </summary>
    public bool NothingToHold { get; internal set; }

    /// <summary>
    /// Conflict copies this run made. A version that is already kept, in a conflict copy of the
    /// same file with the same content that this run leaves as it is, is not kept a second time
    /// and is not counted again.
    /// </summary>
    public int ConflictCopies { get; internal set; }

    public int RemovedFromServer { get; internal set; }

    /// <summary>Blobs on the server that cannot be used: those found in this run and those known from before that are still there.</summary>
    public int BadBlobs { get; internal set; }

    /// <summary>
    /// Files that could not be synced this time and are tried again by the next run: held,
    /// edited meanwhile, not looked at, refused by the server, gone from the server between
    /// its listing and the download, or in a blob that this machine failed to decrypt. Also
    /// counted here: a leftover of an upload that the server would not delete.
    /// </summary>
    public int LeftForLater { get; internal set; }

    /// <summary>Files over the size limit, which are not synced.</summary>
    public int TooLarge { get; internal set; }

    /// <summary>
    /// Folders and files in the data folder that could not be looked at this time
    /// (<see cref="LocalView.Unreadable"/>). What is in or behind them was not synced, whether
    /// or not the record knows any of it, so the run is not <see cref="Clean"/>.
    /// </summary>
    public int Unreadable { get; internal set; }

    /// <summary>
    /// Links in the data folder, which are never followed (<see cref="LocalView.Links"/>). They
    /// stay links, so they are told, and a link is no reason in itself to call the run
    /// incomplete. A blob on the server whose path lies behind a link is another matter: it is
    /// counted in <see cref="LeftForLater"/> at every run, by design, so a run that finds one is
    /// not <see cref="Clean"/> for as long as the path is behind a link.
    /// </summary>
    public int Links { get; internal set; }

    /// <summary>Files in the data folder whose names cannot be synced (<see cref="LocalView.BadNames"/>). Told, and no reason to call the run incomplete.</summary>
    public int BadNames { get; internal set; }

    /// <summary>True when a file under the clipboard folder was written or deleted here, so that the history has to be read again.</summary>
    public bool ClipboardChanged { get; internal set; }

    /// <summary>True when the record of the sync could not be written at the end. The next run then finds what this one did by comparing content.</summary>
    public bool StateNotSaved { get; internal set; }

    /// <summary>
    /// True when the run did everything there was to do: completed, with nothing left over, no
    /// blob it could not use, and no part of the data folder it could not look at.
    /// </summary>
    public bool Clean => End == SyncEnd.Completed && LeftForLater == 0 && BadBlobs == 0 && Unreadable == 0 && !StateNotSaved;
}

/// <summary>What a sync run works with.</summary>
/// <param name="DataRoot">The data folder.</param>
/// <param name="StateFile">Where the record of the sync is written.</param>
/// <param name="State">The record, already read.</param>
/// <param name="Store">The connection to the server's folder.</param>
/// <param name="Keys">The keys of this run.</param>
/// <param name="Device">This machine's name in clipboard history, which a conflict copy carries.</param>
/// <param name="IsOwnDevice">True for the name of a clipboard folder this machine writes.</param>
/// <param name="RetentionDays">How many days of clipboard history this machine keeps.</param>
/// <param name="Report">Receives fixed words, counts and blob names for the log, and an exception that is logged by type only.</param>
/// <param name="Cancelled">True once Wyvern is closing. Asked between two files.</param>
/// <param name="Pause">What waits between two tries at replacing a file that is held. Null means <see cref="Thread.Sleep(TimeSpan)"/>.</param>
/// <param name="AllowDeletions">
/// True for the one run that the owner's Confirm Sync Deletions command starts: the brake on
/// mass deletion does not hold this run, which may send any number of deletion notes.
/// </param>
/// <param name="RestoreMissing">
/// True for the one run that the owner's Restore Missing Files command starts: the run plans
/// with <see cref="PlanInput.RestoreMissing"/>, so a library file that is missing here is
/// fetched from the server and no deletion note is sent for it, and a library file that was
/// changed here is held against the server's version, whatever its time, with both versions
/// kept, and is not uploaded over it unseen.
/// </param>
/// <param name="Paused">
/// True while sync is paused: an earlier run was held by the brake on mass deletion, and the
/// owner has not answered it yet. Whoever starts the run remembers that, and says it here. A
/// run that is told so is held for a single deletion note for a library file, whatever the
/// count: the limits decide only whether a pause begins. A run cannot tell by itself that
/// sync is paused, and neither begins nor ends a pause: it reports that it was held
/// (<see cref="SyncEnd.Held"/>) or that nothing is left to hold
/// (<see cref="SyncResult.NothingToHold"/>).
/// </param>
public sealed record SyncRunContext(
    string DataRoot,
    string StateFile,
    SyncState State,
    IRemoteStore Store,
    SyncKeys Keys,
    string Device,
    Func<string, bool> IsOwnDevice,
    int RetentionDays,
    IClock Clock,
    Action<string, Exception?> Report,
    Func<bool> Cancelled,
    Action<TimeSpan>? Pause = null,
    bool AllowDeletions = false,
    bool RestoreMissing = false,
    bool Paused = false)
{
    /// <summary>The type's name and nothing else.</summary>
    public override string ToString() => nameof(SyncRunContext);
}

/// <summary>
/// Carries one sync run out: lists the server, looks at the data folder, asks
/// <see cref="SyncPlanner"/> what to do, and does it one file at a time. Call it on a
/// background thread: every step waits for the disk or the server.
/// </summary>
/// <remarks>
/// <para>A path is entered in the record only after its file has been synced, so a run that is
/// cut off leaves a record of exactly what was done, and the next run does the rest. The record
/// is written at the end of the run, when the run is cut off, and every thirty seconds in
/// between; what a power cut loses of it is found again by the next run, which sees the same
/// content on both sides.</para>
/// <para>Nothing here overwrites or deletes a file that is not as this run saw it: an edit made
/// while the run was at work is left for the next run. A blob that does not authenticate or
/// does not hold what a blob holds is skipped, named in the log, remembered so that it is not
/// downloaded again and nothing is uploaded over it, and never written to disk. That is said
/// of a blob only for what the blob is: a failure of this machine's own while it opens one is
/// not remembered, and the blob is opened again by the next run. Before the run
/// writes anything to the server it holds this machine's clock against the server's, because
/// the times it writes decide which of two edits wins on the other machine and when history
/// expires. That holds for a write the plan held and for one that a download led to; the
/// server is asked the time once in a run, and not at all by a run that writes nothing.</para>
/// <para>A version that loses a conflict is kept as a conflict copy before the winner is
/// written, and is kept once: when a conflict copy of the file with the same content is already
/// there, and this run's plan holds nothing for that copy but, at most, its upload, that one is
/// the copy. A file that is unchanged here is kept the same way before a version from the
/// server that is older than it is written over it.</para>
/// <para>A file that was synced and is no longer in the data folder is taken for one the owner
/// deleted. A run whose plan holds a deletion note for many library files at once
/// (<see cref="SyncPlanner.IsMassDeletion"/>) does none of the above: it ends as
/// <see cref="SyncEnd.Held"/> once the plan is made, having asked the server for its listing
/// and for nothing else, and having changed no file of the owner's and not written the record.
/// (The look at the data folder comes before the plan, and it may have removed sync's own
/// leftover temporary files, as it does in every run.) The files may
/// have gone some other way, and the owner says which it is, with one of two commands that
/// each start one run: a run that may send the deletions
/// (<see cref="SyncRunContext.AllowDeletions"/>), or a run that fetches the files back
/// (<see cref="SyncRunContext.RestoreMissing"/>). Sync is paused from the run that was held
/// until the owner has answered, and a run that is told so
/// (<see cref="SyncRunContext.Paused"/>) is held for a single such deletion note: some of the
/// files being back does not make the others files that the owner deleted. A run says when
/// nothing is left to hold (<see cref="SyncResult.NothingToHold"/>). Deletions in this
/// machine's own clipboard folders are not counted and are never held, paused or not, and
/// neither are deletions that arrive from the other machine.</para>
/// <para>Nothing but <see cref="RemoteStoreException"/> (which ends the run as failed, or as
/// stopped when Wyvern is closing) and the keys being wiped under the run (which ends it as
/// stopped) is expected here, and neither leaves <see cref="Execute"/>.</para>
/// </remarks>
public static class SyncRun
{
    /// <summary>How often the record is written while a run is at work.</summary>
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);

    // How many names are tried for a conflict copy before the file is left for the next run.
    private const int ConflictNames = 20;

    // After this many actions in a row that the server refused, the run gives up.
    private const int MaxServerRefusals = 3;

    // The mark and the time in the name of a conflict copy, as SyncPath.ConflictCopy writes
    // them. They are used only to take a name apart; whether it is a copy's name is decided by
    // SyncPath.ConflictCopy making that name again (see IsCopyOf).
    private const string ConflictMark = ".conflict-";
    private const string ConflictTimeFormat = "yyyyMMdd-HHmmss";

    public static SyncResult Execute(SyncRunContext context) => new Runner(context).Run();

    private sealed class Runner(SyncRunContext context)
    {
        private readonly SyncResult _result = new();
        private readonly SyncState _state = context.State;
        private Dictionary<string, RemoteBlob> _blobs = [];
        private LocalView _view = new();

        // What this run's plan holds for each path it names, by the path's key.
        private readonly Dictionary<string, SyncActionKind> _planned = new(StringComparer.Ordinal);
        private DateTimeOffset _started;
        private DateTimeOffset _savedAt;
        private bool _saveFailureTold;

        // The server's time, once it has been asked, and whether this machine's clock is within
        // a day of it. It is asked at most once in a run.
        private DateTimeOffset? _serverNow;
        private bool _clocksAgree;

        // Set when a download led to a write and the clocks turned out to be apart: the run ends.
        private bool _clockWrong;

        public SyncResult Run()
        {
            _started = context.Clock.UtcNow;
            _savedAt = _started;
            try
            {
                Work();
            }
            catch (RemoteStoreException ex)
            {
                if (context.Cancelled())
                {
                    _result.End = SyncEnd.Stopped;
                }
                else
                {
                    _result.End = SyncEnd.Failed;
                    _result.Failure = ex.Failure;
                    context.Report($"Sync could not use the server ({ex.Failure}, {ex.Cause ?? "no detail"})", null);
                }
            }
            catch (ObjectDisposedException)
            {
                // A guard, not something the service brings about: it disposes the keys on the
                // run's own thread, after Execute has returned, also when Wyvern closes while a
                // run is at work. Should the keys ever be wiped under a run all the same,
                // whatever it was doing (naming a blob, sealing a file, opening a blob) stops
                // there. It is not the server that failed, and the blob in hand is not
                // remembered as one that could not be used: that would be this machine's
                // failure and not the blob's. Nothing was recorded for it, so the next run
                // takes it up again.
                _result.End = SyncEnd.Stopped;
            }
            finally
            {
                // A run that the brake held changes nothing of the owner's, and that holds for
                // the record on the disk as well: it stays byte for byte what the last run
                // wrote. All that such a run has noted in the record in memory is what it saw
                // before the plan was made (a file written again with the same content, a blob
                // that could not be used and is gone from the listing), and the next run sees
                // the same again. (The one thing a held run may have removed from the data
                // folder is a leftover temporary file of sync's own, which the look at the
                // folder removes in every run.)
                if (_result.End != SyncEnd.Held)
                {
                    Save(always: true);
                }
            }

            return _result;
        }

        private void Work()
        {
            var listing = context.Store.List();
            var temporaries = new List<RemoteBlob>();
            bool leftoverDue = false;
            int strangers = 0;
            foreach (var entry in listing)
            {
                if (BlobCodec.IsBlobName(entry.Name))
                {
                    _blobs[entry.Name] = entry;
                }
                else if (!BlobCodec.IsTemporaryName(entry.Name))
                {
                    strangers++;
                }
                else
                {
                    // What an upload left behind, or one that is under way on the other machine.
                    // Which it is, is decided further down by the server's own clock, which
                    // stamped it. This machine's clock only says whether that is worth asking.
                    temporaries.Add(entry);
                    leftoverDue |= _started - entry.ModifiedUtc > SyncLimits.TemporaryBlobAge;
                }
            }

            if (strangers > 0)
            {
                context.Report(Invariant($"The server's folder holds files that are not Wyvern's, which are left alone: {strangers}"), null);
            }

            if (LocalScan.Run(context.DataRoot, _state, _started) is not { } view)
            {
                _result.End = SyncEnd.NoDataFolder;
                context.Report("Sync did nothing because the data folder could not be listed", null);
                return;
            }

            _view = view;
            _result.TooLarge = view.TooLarge;
            _result.Unreadable = view.Unreadable;
            _result.Links = view.Links;
            _result.BadNames = view.BadNames;
            if (view.Unreadable > 0)
            {
                // Whether or not the record knows anything behind them, a part of the folder was
                // not seen: the result is not clean for it.
                context.Report(Invariant($"The data folder holds folders or files that could not be read, which are tried again by the next run: {view.Unreadable}"), null);
            }

            if (view.Links > 0)
            {
                context.Report(Invariant($"The data folder holds links, which are not followed: {view.Links}"), null);
            }

            if (view.BadNames > 0)
            {
                context.Report(Invariant($"The data folder holds files whose names cannot be synced, which are left alone: {view.BadNames}"), null);
            }

            foreach (var file in view.Files.Values.Where(file => file.Touched))
            {
                // Written again with the same content: nothing to sync. The record takes the
                // size and time the file has now, so that the next run takes it as unchanged
                // from those alone and does not read it again.
                if (_state.Find(SyncPath.Key(file.Path)) is { } synced)
                {
                    _state.Set(synced with { Size = file.Stat.Size, MtimeUtc = file.Stat.MtimeUtc });
                }
            }

            // Looking at the folder can take long: it reads every file whose time moved. A
            // stop that was asked for meanwhile is heeded here, before the server is asked
            // for anything that writes to it.
            if (context.Cancelled())
            {
                _result.End = SyncEnd.Stopped;
                return;
            }

            // A blob that could not be used is remembered for as long as the server lists it
            // exactly as it did then, whether or not a path here has it. One that is gone from
            // the listing, or is listed differently, is forgotten and looked at afresh.
            foreach (var bad in _state.Bad.Values.Where(bad => !_blobs.TryGetValue(bad.Name, out var listed) || listed != bad).ToList())
            {
                _state.RemoveBad(bad.Name);
            }

            // An entry that says only that a blob is not kept (see Remember) says nothing once
            // the server no longer lists that blob. For a path this run can look at, the plan
            // forgets it. For one it cannot look at, the plan holds nothing at all, and the
            // entry would be counted as left for later at every run for good, for a blob that
            // is not there. It goes here: that touches no file and no blob.
            foreach (string gone in _state.Files
                .Where(entry => entry.Value.Kind == SyncedKind.Ignored && !_blobs.ContainsKey(entry.Value.Blob) && !view.Knows(entry.Key))
                .Select(entry => entry.Key)
                .ToList())
            {
                _state.Remove(gone);
            }

            var plan = SyncPlanner.Plan(new PlanInput(
                view,
                _blobs,
                _state,
                context.Keys.BlobName,
                context.IsOwnDevice,
                DateOnly.FromDateTime(_started.UtcDateTime),
                context.RetentionDays,
                _started,
                context.RestoreMissing));
            _result.BadBlobs = plan.StillBad;
            _result.LeftForLater = plan.Unknown;

            // The brake on mass deletion. A file that was synced and is no longer here is taken
            // for one the owner deleted, and the plan holds a deletion note for it. When it
            // holds that for many library files at once, the files may have gone some other
            // way: a folder deleted by mistake, a cleanup tool, a data folder put back from an
            // older backup. Such a run does nothing at all. It ends here, before the server is
            // asked the time, before anything is uploaded (the older versions of a folder that
            // was put back must not go up either), deleted on the server or downloaded, and
            // before any file of the owner's is changed. The record is not written either (see
            // Run): what this run noted in it so far, from looking at the folder and the
            // listing, is found again by the next run. What has happened by now, in this run
            // as in any other, is that the look at the folder (LocalScan) removed sync's own
            // leftover temporary files, if there were any: never a file of the owner's.
            //
            // The limits decide only whether a pause begins. Once sync is paused, which this
            // run is told (the service keeps a mark of it on the disk), a run is held for a
            // single deletion note for a library file: the count falling under the limits,
            // because some of the files are back or a run that restored them was cut off, does
            // not make the rest of them files the owner deleted. Not held: the one run that
            // the owner has allowed to send the deletions. The one run that restores holds no
            // deletion note for a library file in its plan, so there is nothing to hold it for.
            int deletions = SyncPlanner.SharedDeletions(plan);
            if (deletions > 0
                && !context.AllowDeletions
                && (context.Paused || SyncPlanner.IsMassDeletion(deletions, SyncPlanner.LiveShared(_state, view), SyncPlanner.MissingShared(_state, view))))
            {
                _result.End = SyncEnd.Held;
                _result.HeldDeletions = deletions;
                return;
            }

            // What ends a pause: a plan with no deletion note for a library file, made by a run
            // that could look at every library path the record holds as live. A file that is
            // missing behind a folder that could not be listed has no action in the plan at
            // all, and that is not the same as the file being back.
            _result.NothingToHold = deletions == 0 && SyncPlanner.UnseenShared(_state, view) == 0;

            foreach (var action in plan.Actions)
            {
                if (action.Path is { } planned)
                {
                    _planned[SyncPath.Key(planned)] = action.Kind;
                }
            }

            // Everything this run would write to the server carries or depends on this machine's
            // time, so the two clocks are held against each other first. When the plan itself
            // holds a write, or a leftover looks due, that is done here, before anything is
            // downloaded either. A plan of downloads only does not ask: the server is asked
            // when, and if, one of the downloads leads to a write (see MayWrite).
            bool writes = leftoverDue
                || plan.Actions.Any(action => action.Kind is SyncActionKind.Upload or SyncActionKind.UploadTombstone or SyncActionKind.DeleteRemote);
            if (writes && !ClocksAgree())
            {
                _result.End = SyncEnd.ClockWrong;
                context.Report("Sync wrote nothing because this machine's clock and the server's are more than a day apart", null);
                return;
            }

            if (_serverNow is { } serverNow)
            {
                // A leftover is as old as the server's clock says, by which its time was given.
                // By this machine's clock, which may be most of a day off, an upload that is
                // still under way on the other machine could look a day old.
                foreach (var leftover in temporaries.Where(entry => serverNow - entry.ModifiedUtc > SyncLimits.TemporaryBlobAge))
                {
                    try
                    {
                        context.Store.Delete(leftover.Name);
                        _result.RemovedFromServer++;
                    }
                    catch (RemoteStoreException ex) when (ex.Failure is RemoteFailure.Missing or RemoteFailure.Folder or RemoteFailure.Server)
                    {
                        // The server will not let go of it. That is counted, and the next run
                        // tries again; it is no reason not to sync the owner's files.
                        _result.LeftForLater++;
                    }
                }
            }

            int refusals = 0;
            foreach (var action in plan.Actions)
            {
                if (context.Cancelled())
                {
                    _result.End = SyncEnd.Stopped;
                    return;
                }

                try
                {
                    Do(action);
                    refusals = 0;
                }
                catch (RemoteStoreException ex) when (ex.Failure is RemoteFailure.Missing or RemoteFailure.Folder or RemoteFailure.Server)
                {
                    // The server would not do this one thing. The others are still tried, until
                    // it has refused that many in a row: then it is the server and not the
                    // thing, and the run ends as failed.
                    refusals++;
                    if (refusals >= MaxServerRefusals)
                    {
                        throw;
                    }

                    _result.LeftForLater++;
                }

                if (_clockWrong)
                {
                    // Found out before a write that a download led to. What was downloaded and
                    // written here before that stands, and is recorded.
                    _result.End = SyncEnd.ClockWrong;
                    context.Report("Sync stopped before it wrote to the server because this machine's clock and the server's are more than a day apart", null);
                    return;
                }

                Save(always: false);
            }

            _result.End = SyncEnd.Completed;
            var took = context.Clock.UtcNow - _started;
            context.Report(
                Invariant($"Sync finished in {(long)took.TotalSeconds} s: {_result.Uploaded} sent, {_result.Downloaded} fetched, {_result.DeletedHere} deleted here, ")
                + Invariant($"{_result.TombstonesSent} deletions sent, {_result.ConflictCopies} conflict copies kept, {_result.RemovedFromServer} removed from the server, ")
                + Invariant($"{_result.BadBlobs} blobs that could not be used, {_result.LeftForLater} left for the next run, {_result.TooLarge} over the size limit and not synced"),
                null);
        }

        // True when this machine's clock is within a day of the server's. The server is asked
        // the first time this is called in a run, and its answer is kept for the rest of it.
        private bool ClocksAgree()
        {
            if (_serverNow is null)
            {
                var serverNow = context.Store.Now();
                _clocksAgree = (serverNow - context.Clock.UtcNow).Duration() <= SyncLimits.ClockTolerance;
                _serverNow = serverNow;
            }

            return _clocksAgree;
        }

        // Asked before every write to the server that a download led to: an upload of the file
        // here, or a delete of an expired blob. Such a write is decided by this machine's
        // times as much as one the plan held (which of two edits is the newer, what has
        // expired), and the plan's own check did not cover it when the plan held downloads
        // only. False ends the run before that write.
        private bool MayWrite()
        {
            if (ClocksAgree())
            {
                return true;
            }

            _clockWrong = true;
            return false;
        }

        private void Do(SyncAction action)
        {
            switch (action.Kind)
            {
                case SyncActionKind.Upload:
                    Upload(action.Path!, action.Blob);
                    break;

                case SyncActionKind.UploadTombstone:
                    UploadTombstone(action.Path!, action.Blob);
                    break;

                case SyncActionKind.Fetch:
                    Fetch(action);
                    break;

                case SyncActionKind.DeleteRemote:
                    context.Store.Delete(action.Blob);
                    _state.Remove(SyncPath.Key(action.Path!));
                    _state.RemoveBad(action.Blob);
                    _result.RemovedFromServer++;
                    break;

                case SyncActionKind.Forget:
                    _state.Remove(SyncPath.Key(action.Path!));
                    break;

                case SyncActionKind.Ignore:
                    // An expired day of another machine. Should the blob be remembered as one
                    // that could not be used, that goes: this machine would not keep the file
                    // whatever the blob held, so there is nothing here to hold against sync.
                    Remember(action.Path!, _blobs[action.Blob]);
                    _state.RemoveBad(action.Blob);
                    break;
            }
        }

        // The file as it is at this moment goes to the server, and the record takes what was sent.
        private void Upload(string path, string blob)
        {
            if (SyncFiles.Read(context.DataRoot, path, out var stat) is not { } content)
            {
                // Gone, held or grown too large since the run looked. The next run sees which.
                _result.LeftForLater++;
                return;
            }

            string sha256 = BlobCodec.Hash(content);
            var stored = context.Store.Upload(blob, BlobCodec.Seal(context.Keys, blob, new BlobHeader(path, stat.MtimeUtc, Deleted: false, sha256), content));
            _state.Set(new SyncedFile(path, blob, SyncedKind.Live, sha256, stat.Size, stat.MtimeUtc, stored.Size, stored.ModifiedUtc));
            _result.Uploaded++;
        }

        private void UploadTombstone(string path, string blob)
        {
            if (SyncFiles.Stat(context.DataRoot, path) is not null)
            {
                // The file is back. It is not deleted after all.
                _result.LeftForLater++;
                return;
            }

            var noticed = context.Clock.UtcNow;
            var stored = context.Store.Upload(blob, BlobCodec.Seal(context.Keys, blob, new BlobHeader(path, noticed, Deleted: true, ""), []));
            _state.Set(new SyncedFile(path, blob, SyncedKind.Tombstone, "", 0, noticed, stored.Size, stored.ModifiedUtc));
            _result.TombstonesSent++;
        }

        // Downloads a blob and opens it. Null when there is nothing to go on with: the blob has
        // gone since the listing (left for later), this machine failed to decrypt it (left for
        // later), or it cannot be used (skipped and remembered). Only what the blob itself is
        // makes it one that cannot be used: larger than any blob Wyvern makes, or what
        // BlobCodec.Open reports (not authentic, not a blob, bad content).
        private OpenedBlob? Open(RemoteBlob listed)
        {
            byte[] bytes;
            try
            {
                bytes = context.Store.Download(listed.Name, SyncLimits.MaxBlobBytes);
            }
            catch (RemoteStoreException ex) when (ex.Failure == RemoteFailure.TooMuch)
            {
                // Larger than any blob Wyvern makes.
                Bad(listed, "too large");
                return null;
            }
            catch (RemoteStoreException ex) when (ex.Failure == RemoteFailure.Missing)
            {
                // Gone since the server was listed: the other machine replaced or removed it at
                // this moment. The server did what it was asked, so this is not one of the
                // refusals that end a run. The next run sees what is there then.
                _result.LeftForLater++;
                return null;
            }

            // BlobCodec.Open decrypts the array where it lies. When it gives no blob back, the
            // array holds whatever came of that (for a blob that authenticated and was refused
            // for what it holds, the plain bytes), and it goes no further than this method: it
            // is not kept, not hashed and not sent anywhere.
            try
            {
                if (BlobCodec.Open(context.Keys, listed.Name, bytes, out var problem) is { } opened)
                {
                    return opened;
                }

                Bad(listed, problem.ToString());
            }
            catch (CryptographicException)
            {
                // Not a tag that does not match, which BlobCodec.Open answers itself, but the
                // decryption failing in some other way. That is a failure of this machine's
                // own, and says nothing about the blob. "Could not be used" is a property of
                // the blob, and a blob that is remembered as such is not looked at again while
                // the server lists it the same; this one may be good. It is not remembered: its
                // path is left for later, and the next run opens the blob again.
                _result.LeftForLater++;
            }

            return null;
        }

        private void Fetch(SyncAction action)
        {
            var listed = _blobs[action.Blob];
            if (Open(listed) is not { } opened)
            {
                return;
            }

            var header = opened.Header;
            string key = SyncPath.Key(header.Path);
            if (!_view.Knows(key))
            {
                // A blob that no path here had, and its path turns out to be one this run
                // could not look at: behind a link, at a file that is too large, at a key two
                // files share. Nothing is done about it. So that the blob is not downloaded
                // again at every run, up to its full size each time, only to be left alone
                // again, it is entered in the record as one this machine does not keep, as the
                // server lists it now. The path then has a record, the planner counts it as
                // unknown at every run without asking for its blob, and once the path can be
                // looked at, a record that says this counts as no record at all.
                Remember(header.Path, listed);
                _result.LeftForLater++;
                return;
            }

            _view.Files.TryGetValue(key, out var file);
            var synced = _state.Find(key);
            string path = file?.Path ?? synced?.Path ?? header.Path;
            var local = file?.Change ?? synced?.Kind switch
            {
                SyncedKind.Live => LocalChange.Deleted,
                SyncedKind.Tombstone => LocalChange.Gone,
                _ => LocalChange.Absent,
            };
            var owner = SyncPlanner.OwnerOf(path, context.IsOwnDevice);
            bool expired = SyncPlanner.IsExpired(path, DateOnly.FromDateTime(_started.UtcDateTime), context.RetentionDays);

            // An edit here is judged by its content, which is read now.
            string? localSha256 = null;
            LocalStat localStat = file?.Stat ?? default;
            if (local is LocalChange.New or LocalChange.Changed && owner != PathOwner.Own && !expired)
            {
                if (SyncFiles.Read(context.DataRoot, path, out localStat) is not { } mine)
                {
                    _result.LeftForLater++;
                    return;
                }

                localSha256 = BlobCodec.Hash(mine);
            }
            else if (local == LocalChange.Unchanged && owner == PathOwner.Shared)
            {
                // Unchanged since it was last synced, so its content is what the record has.
                // The planner holds it against the blob's, with the file's time against the
                // blob's: a version that is older than the file here does not replace it unseen.
                localSha256 = synced?.Sha256;
            }

            var settlement = SyncPlanner.Settle(owner, expired, local, header, localSha256, localStat.MtimeUtc);
            if (settlement is Settlement.KeepLocal or Settlement.LocalWins or Settlement.RemoveRemote && !MayWrite())
            {
                // Before the conflict copy as well: with a wrong clock it is not known that the
                // file here is the newer one, and nothing is made for an upload that will not follow.
                return;
            }

            switch (settlement)
            {
                case Settlement.Record when header.Deleted:
                    _state.Set(new SyncedFile(path, action.Blob, SyncedKind.Tombstone, "", 0, header.MtimeUtc, listed.Size, listed.ModifiedUtc));
                    break;

                case Settlement.Record:
                    _state.Set(new SyncedFile(path, action.Blob, SyncedKind.Live, header.Sha256, localStat.Size, localStat.MtimeUtc, listed.Size, listed.ModifiedUtc));
                    break;

                case Settlement.TakeRemote:
                    Take(path, opened, listed, expected: file?.Stat);
                    break;

                case Settlement.Restore:
                    Take(path, opened, listed, expected: null);
                    break;

                case Settlement.DeleteLocal:
                    if (Check(SyncFiles.Delete(context.DataRoot, path, file!.Stat)))
                    {
                        _state.Set(new SyncedFile(path, action.Blob, SyncedKind.Tombstone, "", 0, header.MtimeUtc, listed.Size, listed.ModifiedUtc));
                        _result.DeletedHere++;
                        Changed(path);
                    }

                    break;

                case Settlement.KeepLocal:
                    Upload(path, action.Blob);
                    break;

                case Settlement.RemoteWins:
                    // The file here was edited and the server's is the newer edit, or it is
                    // unchanged and the server holds an older version of other content. Either
                    // way the server's version is taken, and what is here is kept first.
                    if (localSha256 is not null && HasCopy(path, localStat.Size, localSha256))
                    {
                        // What is here is kept already, in a conflict copy with the same bytes
                        // (the owner put an earlier loser back, say). No second copy is made:
                        // the server's version is written over the file, if it still stands as
                        // it was read.
                        Take(path, opened, listed, expected: localStat);
                    }
                    else if (ConflictName(path, name => SyncFiles.Move(context.DataRoot, path, name, localStat)))
                    {
                        // The file that was here is renamed first, so it is kept whatever happens next.
                        _result.ConflictCopies++;
                        Take(path, opened, listed, expected: null);
                    }

                    break;

                case Settlement.LocalWins:
                    if (HasCopy(path, opened.Content.Length, header.Sha256))
                    {
                        // The server's version is kept already: an earlier run saved it and was
                        // then cut off, or the server refused the upload. That copy is the
                        // copy. A second one at every run, each of them later sent to the other
                        // machine, would be the same bytes again without end.
                        Upload(path, action.Blob);
                    }
                    else if (ConflictName(path, name => SyncFiles.Write(context.DataRoot, name, opened.Content.Span, header.MtimeUtc, null, context.Clock.UtcNow, out _, context.Pause)))
                    {
                        // The server's version is saved beside the file before the file replaces it there.
                        _result.ConflictCopies++;
                        Upload(path, action.Blob);
                    }

                    break;

                case Settlement.Ignore:
                    Remember(path, listed);
                    break;

                case Settlement.RemoveRemote:
                    context.Store.Delete(action.Blob);
                    _state.Remove(key);
                    _result.RemovedFromServer++;
                    break;
            }
        }

        // Writes the blob's file here and records it as synced.
        private void Take(string path, OpenedBlob opened, RemoteBlob listed, LocalStat? expected)
        {
            var outcome = SyncFiles.Write(context.DataRoot, path, opened.Content.Span, opened.Header.MtimeUtc, expected, context.Clock.UtcNow, out var written, context.Pause);
            if (Check(outcome))
            {
                _state.Set(new SyncedFile(path, listed.Name, SyncedKind.Live, opened.Header.Sha256, written.Size, written.MtimeUtc, listed.Size, listed.ModifiedUtc));
                _result.Downloaded++;
                Changed(path);
            }
        }

        // Tries the names a conflict copy can have until one is free.
        private bool ConflictName(string path, Func<string, FileOutcome> keep)
        {
            var now = context.Clock.UtcNow;
            for (int number = 1; number <= ConflictNames; number++)
            {
                string? copy = SyncPath.ConflictCopy(path, context.Device, now, number);
                if (copy is null)
                {
                    // No name fits this path: left for later, as when Windows refuses the name.
                    break;
                }

                var outcome = keep(copy);
                if (outcome == FileOutcome.Done)
                {
                    Changed(path);
                    return true;
                }

                if (outcome != FileOutcome.Changed || SyncFiles.Stat(context.DataRoot, copy) is null)
                {
                    // Not a name that is taken: the file itself changed, or Windows refused.
                    break;
                }
            }

            _result.LeftForLater++;
            return false;
        }

        // True when the folder of a path already holds a conflict copy of that path with exactly
        // these bytes, which this run leaves as it is: a file this run's look at the folder
        // found, whose name is a conflict copy's name for this path, of this size (which costs
        // nothing to hold against it) and, read now, of this hash. Whichever machine made it
        // and whenever, it is the copy.
        //
        // A copy that this run's plan is to do something else with is not taken, whether that
        // has been done yet or not: the other machine may have deleted it or written it anew,
        // and this run then takes that over, so that the version it was relied on to keep would
        // be on neither machine. The one thing the plan may hold for the copy's own path is its
        // upload, which leaves the file here as it is. For any other, a new copy is made.
        private bool HasCopy(string path, long size, string sha256)
        {
            string folder = SyncPath.Key(path[..(path.LastIndexOf('/') + 1)]);
            foreach (var (key, file) in _view.Files)
            {
                if (file.Stat.Size == size
                    && key.Length > folder.Length
                    && key.StartsWith(folder, StringComparison.Ordinal)
                    && key.IndexOf('/', folder.Length) < 0
                    && (!_planned.TryGetValue(key, out var planned) || planned == SyncActionKind.Upload)
                    && IsCopyOf(path, file.Path)
                    && SyncFiles.Read(context.DataRoot, file.Path, out _) is { } content
                    && content.Length == size
                    && BlobCodec.Hash(content) == sha256)
                {
                    return true;
                }
            }

            return false;
        }

        // True when the name of a file is one that SyncPath.ConflictCopy gives for a path in the
        // same folder: for some machine's name, some time, and some number. A copy's name is the
        // stem of the file's name (cut short when it is long), the mark, the machine's name,
        // the time, a number when the name was taken, and the file's extension (none when that
        // is too long to be one). Rather than repeat here how the stem is cut and when the
        // extension is kept, every way the name can be read as machine, time and number is
        // given back to SyncPath.ConflictCopy, and the name is a copy's when one of them makes
        // that very name again.
        private static bool IsCopyOf(string path, string other)
        {
            int slash = path.LastIndexOf('/');
            string folder = path[..(slash + 1)];
            string name = other[(other.LastIndexOf('/') + 1)..];
            if (!SyncPath.IsConflictCopy(name))
            {
                return false;
            }

            string extension = Path.GetExtension(path[(slash + 1)..]);
            foreach (string ending in extension.Length > 0 ? new[] { extension, "" } : new[] { "" })
            {
                if (!name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Without a number at the end, and with one.
                string front = name[..^ending.Length];
                int dash = front.LastIndexOf('-');
                if (Makes(front, 1)
                    || (dash > 0
                        && int.TryParse(front.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                        && number > 1
                        && Makes(front[..dash], number)))
                {
                    return true;
                }
            }

            return false;

            // The front is a stem, the mark, a machine's name, "-" and a time.
            bool Makes(string front, int number)
            {
                if (front.Length <= ConflictTimeFormat.Length + 1
                    || front[^(ConflictTimeFormat.Length + 1)] != '-'
                    || !DateTime.TryParseExact(
                        front[^ConflictTimeFormat.Length..],
                        ConflictTimeFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var time))
                {
                    return false;
                }

                // The machine's name starts after the mark. A stem or a machine's name can hold
                // the mark's letters too, so every place they stand at is tried.
                string before = front[..^(ConflictTimeFormat.Length + 1)];
                for (int mark = before.IndexOf(ConflictMark, StringComparison.Ordinal); mark >= 0; mark = before.IndexOf(ConflictMark, mark + 1, StringComparison.Ordinal))
                {
                    string device = before[(mark + ConflictMark.Length)..];
                    if (device.Length > 0
                        && string.Equals(SyncPath.ConflictCopy(path, device, new DateTimeOffset(time, TimeSpan.Zero), number), folder + name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // Enters a path's blob in the record as one this machine does not keep, as the server
        // lists it: an expired day of another machine, or a blob whose path could not be looked at.
        private void Remember(string path, RemoteBlob listed) =>
            _state.Set(new SyncedFile(path, listed.Name, SyncedKind.Ignored, "", 0, default, listed.Size, listed.ModifiedUtc));

        private bool Check(FileOutcome outcome)
        {
            if (outcome == FileOutcome.Done)
            {
                return true;
            }

            _result.LeftForLater++;
            return false;
        }

        private void Changed(string path)
        {
            if (SyncPath.Clipboard(path) is not null)
            {
                _result.ClipboardChanged = true;
            }
        }

        // A blob that cannot be used: skipped, named in the log by its keyed name, and remembered
        // as the server lists it so that it is not downloaded again until it changes.
        private void Bad(RemoteBlob listed, string why)
        {
            _state.SetBad(listed);
            _result.BadBlobs++;
            context.Report($"A blob could not be used and was skipped ({why}): {listed.Name}", null);
        }

        private void Save(bool always)
        {
            var now = context.Clock.UtcNow;
            if (!_state.Dirty || (!always && now - _savedAt < SaveEvery && now >= _savedAt))
            {
                return;
            }

            _savedAt = now;
            try
            {
                _state.Save(context.StateFile);
                _saveFailureTold = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _result.StateNotSaved = always;
                if (!_saveFailureTold)
                {
                    _saveFailureTold = true;
                    context.Report("The record of the sync could not be written", ex);
                }
            }
        }

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
