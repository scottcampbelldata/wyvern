using Beacon.Core.ClipboardHistory;

namespace Beacon.Core.Sync;

/// <summary>Whose a path is, which decides which way it syncs.</summary>
public enum PathOwner
{
    /// <summary>The library and everything else outside the machines' clipboard folders: synced both ways.</summary>
    Shared,

    /// <summary>A path in a clipboard folder this machine writes. Only this machine uploads it, and sync never changes a file that is there.</summary>
    Own,

    /// <summary>A path in another machine's clipboard folder. It is only ever downloaded.</summary>
    Other,
}

/// <summary>What a run is to do about one path or one blob.</summary>
public enum SyncActionKind
{
    /// <summary>Send the file here to the server.</summary>
    Upload,

    /// <summary>Send the note that the file was deleted here.</summary>
    UploadTombstone,

    /// <summary>Download the blob and open it; <see cref="SyncPlanner.Settle"/> then says what to do with it.</summary>
    Fetch,

    /// <summary>Delete the blob from the server, and forget the path.</summary>
    DeleteRemote,

    /// <summary>Take the path out of the record: nothing is left of it on either side.</summary>
    Forget,

    /// <summary>
    /// Remember the blob as one this machine does not keep, so that it is not downloaded. When
    /// it was remembered as one that could not be used (<see cref="SyncState.Bad"/>), that is
    /// forgotten.
    /// </summary>
    Ignore,
}

/// <summary>One thing a run is to do.</summary>
/// <param name="Blob">The blob's name.</param>
/// <param name="Path">The path, or null for a blob whose path is not known until it has been opened.</param>
/// <param name="Local">How the path stood on this machine when the plan was made.</param>
public sealed record SyncAction(SyncActionKind Kind, string Blob, string? Path, LocalChange Local)
{
    /// <summary>The type's name and the kind, and never the path.</summary>
    public override string ToString() => $"{nameof(SyncAction)} {{ Kind = {Kind} }}";
}

/// <summary>What is to be done with a blob that was downloaded and opened.</summary>
public enum Settlement
{
    /// <summary>Change nothing; note in the record how things stand (the same content on both sides, or deleted on both).</summary>
    Record,

    /// <summary>Write the blob's file here, over a file that is unchanged or where there is none.</summary>
    TakeRemote,

    /// <summary>Write the blob's file here only if there is none: a file of this machine's own clipboard folder that is missing.</summary>
    Restore,

    /// <summary>Delete the file here: it was deleted elsewhere and is unchanged here.</summary>
    DeleteLocal,

    /// <summary>Keep the file here and upload it: an edit wins over a deletion, and this machine's own clipboard file over anything.</summary>
    KeepLocal,

    /// <summary>
    /// Edited on both sides and the server's is newer, or unchanged here while the server
    /// holds other content that is older than the file here: the server's is written here, and
    /// the file that was here is kept beside it as a conflict copy. The copy is made first: the
    /// file here is given the copy's name before the server's version is written, so that the
    /// version that lost is kept whatever becomes of the write.
    /// </summary>
    RemoteWins,

    /// <summary>
    /// Edited on both sides and this one is newer: it is uploaded, and the server's is kept
    /// beside it as a conflict copy. The copy is made first: the server's version is written
    /// beside the file before the file is uploaded over it, so that the version that lost is
    /// kept whatever becomes of the upload.
    /// </summary>
    LocalWins,

    /// <summary>An expired day of another machine's clipboard history: keep nothing, remember the blob.</summary>
    Ignore,

    /// <summary>An expired blob of this machine's own clipboard history: delete it from the server.</summary>
    RemoveRemote,
}

/// <summary>What <see cref="SyncPlanner.Plan"/> works from.</summary>
/// <remarks>
/// The look at the data folder and the plan must be given the same record: <paramref name="State"/>
/// has to be the very record that <see cref="LocalScan.Run"/> was given when it made
/// <paramref name="Local"/>. A file's <see cref="LocalFile.Change"/> says how the file stands
/// against that record, and <see cref="SyncPlanner.Plan"/> takes it as said without holding it
/// against the record again. A view made against one record and planned against another could
/// call a file unchanged that the record does not know, and a tombstone on the server would
/// then delete it.
/// </remarks>
/// <param name="Local">The data folder as this run found it.</param>
/// <param name="Remote">The server's blobs by name. Only names that are a blob's name.</param>
/// <param name="State">The record of the last sync: the one <paramref name="Local"/> was made against.</param>
/// <param name="BlobName">The name of the blob for a path's key; <see cref="SyncKeys.BlobName"/>.</param>
/// <param name="IsOwnDevice">True for the name of a clipboard folder this machine writes, in any case.</param>
/// <param name="Today">Today's date in UTC, by which a clipboard day is expired or not.</param>
/// <param name="RetentionDays">How many days of clipboard history this machine keeps.</param>
/// <param name="Now">The time, by which a tombstone is old or not.</param>
/// <param name="RestoreMissing">
/// True for the one run that the owner's Restore Missing Files command starts: the server is
/// right where this folder is behind. Two cells of the table for a path outside the clipboard
/// folders then read differently (see the remarks on <see cref="SyncPlanner"/>); every other
/// cell, and every path in a clipboard folder, is as always.
/// </param>
public sealed record PlanInput(
    LocalView Local,
    IReadOnlyDictionary<string, RemoteBlob> Remote,
    SyncState State,
    Func<string, string> BlobName,
    Func<string, bool> IsOwnDevice,
    DateOnly Today,
    int RetentionDays,
    DateTimeOffset Now,
    bool RestoreMissing = false)
{
    /// <summary>The type's name and nothing else.</summary>
    public override string ToString() => nameof(PlanInput);
}

/// <summary>What a plan holds.</summary>
/// <param name="Actions">What to do, in the order of the paths, with the blobs of unknown path last.</param>
/// <param name="StillBad">
/// How many blobs that could not be used before are still on the server, listed exactly as
/// they were then: the blobs of paths this machine has a file or a record for, and blobs that
/// no path here claims, alike. None of them is downloaded again and nothing is uploaded over
/// any of them, so none has an action in <paramref name="Actions"/>. Not counted: such a blob
/// of an expired day of clipboard history that a path here has. The machine that writes the
/// day deletes it from the server (<see cref="SyncActionKind.DeleteRemote"/>), and any other
/// machine, which would not keep the file whatever the blob held, remembers it as one it does
/// not keep (<see cref="SyncActionKind.Ignore"/>).
/// </param>
/// <param name="Unknown">
/// How many paths were left alone because this run could not look at their files. Such a path
/// whose blob is also one that could not be used is counted here and in
/// <paramref name="StillBad"/>. Also counted here: a file that is gone from a clipboard folder
/// this machine writes while the run could not look at the day file of its day, which is what
/// says whether the owner deleted it.
/// </param>
public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions, int StillBad, int Unknown)
{
    /// <summary>The type's name and how many actions it holds.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(SyncPlan)} {{ Actions = {Actions.Count} }}");
}

/// <summary>
/// Decides what a sync run does, and touches nothing: no file and no server. A run compares
/// three views of every path, which are how it stands here now, how the server lists its blob
/// now, and what the record of the last sync says, and this is the table that turns them into
/// actions.
/// </summary>
/// <remarks>
/// <para>The decision has two halves, because a blob says what it holds only once it has been
/// downloaded and opened. <see cref="Plan"/> works from what can be seen without that: whether
/// the blob changed. Where the answer depends on what the blob holds, it asks for the blob
/// (<see cref="SyncActionKind.Fetch"/>), and <see cref="Settle"/> finishes the decision from
/// the blob's header.</para>
/// <para>For the library, and everything else outside the machines' clipboard folders:</para>
/// <list type="bullet">
/// <item>changed or new here, the blob unchanged or not there: upload;</item>
/// <item>unchanged here, the blob changed: download, or delete here when the blob is a tombstone. A download never replaces a newer file unseen: when the blob holds other content that was written before the file here was, the file here is kept as a conflict copy first;</item>
/// <item>not here and never synced, a blob there: download;</item>
/// <item>deleted here, the blob unchanged: upload a tombstone;</item>
/// <item>changed on both sides: the newer one wins and the other is kept as a conflict copy, unless both hold the same content;</item>
/// <item>deleted on one side and edited on the other: the edit wins;</item>
/// <item>a tombstone older than 180 days is removed from the server.</item>
/// </list>
/// <para>In the one run that the owner's Restore Missing Files command starts
/// (<see cref="PlanInput.RestoreMissing"/>), the server is right where this folder is behind,
/// and two of those cells read differently, both for a blob that the server lists as the
/// record has it:</para>
/// <list type="bullet">
/// <item>deleted here: the blob is downloaded and the file written again, in place of a tombstone being uploaded (a blob that turns out to be a tombstone is recorded, and the file stays deleted);</item>
/// <item>changed here, whatever the file's time: the blob is downloaded and the two are settled by their times like an edit on both sides, in place of the file being uploaded over the server's version. Whichever wins, the other is kept beside it as a conflict copy. The file's time is not asked whether this is an edit or an older version that was put back, because a backup that is put back by a tool that stamps files with the present time looks like an edit by it.</item>
/// </list>
/// <para>How many tombstones a plan holds for the library is what the brake on mass deletion
/// counts (<see cref="SharedDeletions"/>, <see cref="IsMassDeletion"/>): a run that would send
/// many at once sends nothing until the owner has said which it is.</para>
/// <para>For clipboard folders, which each have one writer:</para>
/// <list type="bullet">
/// <item>a path in a folder this machine writes is only ever uploaded from here, and no file that is here is changed by sync; a file of it that is missing here and that the record does not know is made again from the server;</item>
/// <item>a file in such a folder that was synced and is gone here is tombstoned only when the owner deleted it, which shows in the day file of its day still being here: deleting an entry writes the day file again and never removes it. When the day file is gone as well, or is itself the file that is gone, nothing the owner did removed it (retention on a day when this machine's date was far ahead does that), and it is made again from the server. A file that belongs to no day is tombstoned;</item>
/// <item>a path in another machine's folder is only ever downloaded: a copy that is missing or altered here is fetched again, and it is never uploaded or tombstoned from here;</item>
/// <item>an expired day is not downloaded and not tombstoned, and the machine that writes the folder deletes its expired blobs from the server outright.</item>
/// </list>
/// <para>A path this run could not look at (<see cref="LocalView.Knows"/>) gets no action of
/// any kind.</para>
/// <para>A blob that could not be used (<see cref="SyncState.Bad"/>) gets no action either
/// while the server lists it as it did then, whatever stands here at its path: it is not
/// downloaded again, no file and no tombstone is uploaded over it, and it is counted
/// (<see cref="SyncPlan.StillBad"/>). The exception is an expired day of clipboard history,
/// which goes by the rule above whether or not its blob could be used: the machine that writes
/// the folder deletes the blob outright, and any other machine ignores it and forgets that it
/// could not be used. What makes a blob one that could not be used is the blob: it does not
/// authenticate, it is not a blob, it does not hold what a blob holds, or it is larger than any
/// blob Beacon makes. A failure of this machine's own while it opened a blob is not remembered.</para>
/// </remarks>
public static class SyncPlanner
{
    /// <summary>The first half: what to do for every path, as far as it can be told without opening a blob.</summary>
    public static SyncPlan Plan(PlanInput input)
    {
        var actions = new List<SyncAction>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        int unknown = 0;
        int stillBad = 0;
        var keys = new SortedSet<string>(input.Local.Files.Keys, StringComparer.Ordinal);
        keys.UnionWith(input.State.Files.Keys);
        foreach (string key in keys)
        {
            input.Local.Files.TryGetValue(key, out var file);
            var synced = input.State.Find(key);
            string path = synced?.Path ?? file!.Path;
            string blob = synced?.Blob ?? input.BlobName(key);
            claimed.Add(blob);
            input.Remote.TryGetValue(blob, out var remote);
            bool unusable = remote is not null && StillUnusable(remote, input.State);
            if (!input.Local.Knows(key))
            {
                unknown++;
                if (unusable)
                {
                    stillBad++;
                }

                continue;
            }

            var local = LocalOf(file, synced);
            var owner = OwnerOf(path, input.IsOwnDevice);
            bool expired = IsExpired(path, input.Today, input.RetentionDays);
            if (unusable && expired && owner == PathOwner.Other)
            {
                // The blob could not be used, and it is an expired day of another machine's
                // history: this machine would not keep the file whatever the blob held. It is
                // remembered as a blob that is not kept, as every such blob is, and the run
                // forgets that it could not be used. It is not counted: sync is not degraded
                // here for a file this machine has no use for, until the other machine deletes
                // the blob.
                actions.Add(new SyncAction(SyncActionKind.Ignore, blob, path, local));
                continue;
            }

            if (unusable && !(owner == PathOwner.Own && expired))
            {
                // The blob could not be used, and the server lists it as it did then. Whatever
                // stands here, it is not downloaded again, and nothing is sent over it: it may
                // be a version of the file that was damaged on the way. What still happens to
                // such a blob is what happens to an expired day: the other machine's is
                // ignored (above), and the machine whose expired history it is deletes it, as
                // it deletes every expired blob of its own.
                stillBad++;
                continue;
            }

            // A file of this machine's own history that was synced and is gone here, in a day
            // that has not expired, with its blob still on the server: whether the owner
            // deleted it is told by the day file of its day (see WhyMissing).
            var missing = Missing.DeletedByOwner;
            if (owner == PathOwner.Own && !expired && local == LocalChange.Deleted && remote is not null)
            {
                missing = WhyMissing(path, key, input.Local);
                if (missing == Missing.CannotTell)
                {
                    // The day file could not be looked at, so it says neither that the owner
                    // deleted this file nor that he did not. No tombstone goes out for it and
                    // it is not made again either: it is left as it is, and counted.
                    unknown++;
                    continue;
                }
            }

            var kind = owner switch
            {
                PathOwner.Own => ForOwn(local, remote, synced, expired, missing == Missing.DeletedByOwner),
                PathOwner.Other => ForOther(local, remote, synced, expired),
                _ => ForShared(local, remote, synced, input.Now, input.RestoreMissing),
            };
            if (kind is { } action)
            {
                actions.Add(new SyncAction(action, blob, path, local));
            }
        }

        // A blob that belongs to no path this machine knows is opened to learn its path.
        foreach (var remote in input.Remote.Values.OrderBy(blob => blob.Name, StringComparer.Ordinal))
        {
            if (claimed.Contains(remote.Name))
            {
                continue;
            }

            if (StillUnusable(remote, input.State))
            {
                stillBad++;
            }
            else
            {
                actions.Add(new SyncAction(SyncActionKind.Fetch, remote.Name, null, LocalChange.Absent));
            }
        }

        return new SyncPlan(actions, stillBad, unknown);
    }

    /// <summary>The second half: what to do with a blob now that its header has been read.</summary>
    /// <remarks>
    /// This may only be asked about a path the run could look at. It cannot be told that a path
    /// is unknown, and would answer as for a path with nothing here: take the file, make it
    /// again, or delete the blob. For a path that <see cref="Plan"/> itself asked the blob for,
    /// <see cref="Plan"/> has seen to that. For a blob whose path <see cref="Plan"/> did not
    /// know (a <see cref="SyncActionKind.Fetch"/> with no path), the path is learnt only by
    /// opening the blob, and the caller must ask <see cref="LocalView.Knows"/> about it before
    /// it calls this. A path that is unknown gets no action here either: this is then not
    /// called, and the blob is left for the next run.
    /// </remarks>
    /// <param name="expired">True when the path is an expired day of clipboard history.</param>
    /// <param name="local">How the path stands here.</param>
    /// <param name="localSha256">
    /// The hash of the file here, as hex digits in either case. Needed when
    /// <paramref name="local"/> is new or changed. For an unchanged file outside the clipboard
    /// folders it is the hash the record has for it, by which a blob that holds the same
    /// content is told from one that holds other content; left out, the blob is taken to hold
    /// other content. Otherwise ignored.
    /// </param>
    /// <param name="localMtimeUtc">
    /// When the file here was last written. Needed when <paramref name="local"/> is new or
    /// changed, and for an unchanged file outside the clipboard folders; left out, no blob is
    /// older than the file here.
    /// </param>
    public static Settlement Settle(PathOwner owner, bool expired, LocalChange local, BlobHeader remote, string? localSha256 = null, DateTimeOffset localMtimeUtc = default)
    {
        bool here = local is LocalChange.New or LocalChange.Unchanged or LocalChange.Changed;
        bool edited = local is LocalChange.New or LocalChange.Changed;

        // A hash is the number its digits stand for: the same content is the same content
        // whether the caller wrote its hash in small letters, as a blob's header does, or in
        // capitals. The order of two hashes is taken the same way, so that both machines pick
        // the same winner.
        bool sameContent = string.Equals(localSha256, remote.Sha256, StringComparison.OrdinalIgnoreCase);
        switch (owner)
        {
            case PathOwner.Own:
                // This machine is the one writer. What is here is never changed by sync.
                return expired ? Settlement.RemoveRemote
                    : here ? Settlement.KeepLocal
                    : remote.Deleted ? Settlement.Record
                    : Settlement.Restore;

            case PathOwner.Other:
                // The other machine is the one writer. What it says wins, whatever is here.
                return expired ? Settlement.Ignore
                    : remote.Deleted ? (here ? Settlement.DeleteLocal : Settlement.Record)
                    : edited && sameContent ? Settlement.Record
                    : Settlement.TakeRemote;

            default:
                if (remote.Deleted)
                {
                    // Deleted there. Unchanged here: it goes. Edited here: the edit wins.
                    return local == LocalChange.Unchanged ? Settlement.DeleteLocal
                        : edited ? Settlement.KeepLocal
                        : Settlement.Record;
                }

                if (!edited)
                {
                    // Not here at all, or deleted here while it was edited there: the edit wins
                    // and is taken. Unchanged here: the server's version is the other machine's
                    // edit and is taken as well. But a file here is never replaced unseen by a
                    // version that was written before it: that is not an edit made after this
                    // one, it is an older version that came to be on the server some other way
                    // (the blob was lost or removed there, and a machine that never had the
                    // newer text sent its own). The server's version is still taken, and the
                    // file that was here is kept beside it as a conflict copy first.
                    return local == LocalChange.Unchanged && !sameContent && remote.MtimeUtc < localMtimeUtc
                        ? Settlement.RemoteWins
                        : Settlement.TakeRemote;
                }

                if (sameContent)
                {
                    return Settlement.Record;
                }

                // Edited on both sides. The newer one wins; at the very same time, the one whose
                // hash is greater, so that both machines pick the same winner.
                int newer = remote.MtimeUtc.CompareTo(localMtimeUtc);
                if (newer == 0)
                {
                    newer = string.Compare(remote.Sha256, localSha256, StringComparison.OrdinalIgnoreCase);
                }

                return newer > 0 ? Settlement.RemoteWins : Settlement.LocalWins;
        }
    }

    /// <summary>
    /// How many deletion notes a plan holds for paths outside the clipboard folders: the
    /// library files that this run would have the owner's other machine delete. This is what
    /// the brake on mass deletion counts. Not counted: a deletion in a clipboard folder (those
    /// come from Delete and Clear in Beacon itself), an old tombstone that is removed from the
    /// server, and a path that is only forgotten.
    /// </summary>
    public static int SharedDeletions(SyncPlan plan) =>
        plan.Actions.Count(action => action.Kind == SyncActionKind.UploadTombstone && action.Path is { } path && SyncPath.Clipboard(path) is null);

    /// <summary>
    /// How many paths outside the clipboard folders the record holds as live and this run
    /// could look at (<see cref="LocalView.Knows"/>): the library files there were at the last
    /// sync, as far as the run can say whether they are still here. The deletions of a run are
    /// held against these. A file behind a folder that could not be listed is not one of
    /// them: the run did not see it here, so it must not make the missing ones a smaller share.
    /// </summary>
    public static int LiveShared(SyncState state, LocalView view) =>
        state.Files.Count(entry => IsLiveShared(entry.Value) && view.Knows(entry.Key));

    /// <summary>
    /// How many of the library files that <see cref="LiveShared"/> counts are not here any
    /// more. When that is all of them, the library is gone altogether. This is not the count of
    /// the deletions a plan holds (<see cref="SharedDeletions"/>), which can be lower: for a
    /// file that is gone here and was edited or deleted on the other machine meanwhile, or
    /// whose blob has gone from the server, the plan holds no deletion note.
    /// </summary>
    public static int MissingShared(SyncState state, LocalView view) =>
        state.Files.Count(entry => IsLiveShared(entry.Value) && view.Knows(entry.Key) && !view.Files.ContainsKey(entry.Key));

    /// <summary>
    /// How many paths outside the clipboard folders the record holds as live and this run
    /// could not look at (<see cref="LocalView.Knows"/>): library files behind a folder that
    /// could not be listed or is a link, or that could not be read. Whether such a file is
    /// still here is not known, and a plan holds nothing for it. So a run with any of these
    /// has not shown that no library file is missing, and does not end a pause.
    /// </summary>
    public static int UnseenShared(SyncState state, LocalView view) =>
        state.Files.Count(entry => IsLiveShared(entry.Value) && !view.Knows(entry.Key));

    /// <summary>
    /// True when the deletions of library files that a run's plan holds are a mass deletion,
    /// which is not sent without the owner's word, and with which a pause begins: more than
    /// <see cref="SyncLimits.MassDeletionCount"/> of them; or at least
    /// <see cref="SyncLimits.MassDeletionFloor"/> and more than one in
    /// <see cref="SyncLimits.MassDeletionShare"/> of the live library files the run could look
    /// at; or any at all, when every live library file the run could look at is gone and
    /// there were at least <see cref="SyncLimits.MassDeletionWholeLibrary"/> of them. With no
    /// deletion in the plan there is nothing to hold, whatever is missing.
    /// </summary>
    /// <param name="deletions">What <see cref="SharedDeletions"/> gives for the run's plan.</param>
    /// <param name="live">What <see cref="LiveShared"/> gives for the record the plan was made from and the run's look at the folder.</param>
    /// <param name="missing">What <see cref="MissingShared"/> gives for the same two.</param>
    public static bool IsMassDeletion(int deletions, int live, int missing) =>
        deletions > SyncLimits.MassDeletionCount
        || (deletions >= SyncLimits.MassDeletionFloor && (long)deletions * SyncLimits.MassDeletionShare > live)
        || (deletions > 0 && live >= SyncLimits.MassDeletionWholeLibrary && missing >= live);

    /// <summary>Whose a path is.</summary>
    public static PathOwner OwnerOf(string path, Func<string, bool> isOwnDevice) =>
        SyncPath.Clipboard(path) is not { } clipboard ? PathOwner.Shared
            : isOwnDevice(clipboard.Device) ? PathOwner.Own
            : PathOwner.Other;

    /// <summary>
    /// True when a path is a day of clipboard history that is older than this machine keeps,
    /// by the one rule for that (<see cref="ClipRetention.IsExpired"/>). Nothing else expires.
    /// </summary>
    public static bool IsExpired(string path, DateOnly today, int retentionDays) =>
        SyncPath.Clipboard(path) is { Day: { } day } && ClipRetention.IsExpired(day, today, retentionDays);

    private static LocalChange LocalOf(LocalFile? file, SyncedFile? synced) =>
        file is not null ? file.Change
            : synced is null ? LocalChange.Absent
            : synced.Kind switch
            {
                SyncedKind.Live => LocalChange.Deleted,
                SyncedKind.Tombstone => LocalChange.Gone,
                _ => LocalChange.Absent,
            };

    // True for an entry of the record that is a library file as it was last synced: live, and
    // outside the clipboard folders.
    private static bool IsLiveShared(SyncedFile synced) => synced.Kind == SyncedKind.Live && SyncPath.Clipboard(synced.Path) is null;

    // True when the blob could not be used before and the server lists it exactly as it did then.
    private static bool StillUnusable(RemoteBlob remote, SyncState state) =>
        state.Bad.TryGetValue(remote.Name, out var bad) && bad == remote;

    // True when the server lists the blob as the record has it.
    private static bool Same(RemoteBlob remote, SyncedFile? synced) =>
        synced is not null && remote.Size == synced.RemoteSize && remote.ModifiedUtc == synced.RemoteMtimeUtc;

    // "Restoring" is the one run of Restore Missing Files. It changes two cells, both for a
    // path the record holds as live whose blob is on the server as the record has it: a file
    // that is gone here is fetched where its deletion would be sent, and a file that was
    // changed here is fetched where it would be uploaded over the server's version.
    private static SyncActionKind? ForShared(LocalChange local, RemoteBlob? remote, SyncedFile? synced, DateTimeOffset now, bool restoring)
    {
        // A record that says ignored is made for an expired day in another machine's clipboard
        // folder, and for a blob whose path the run could not look at when it opened the blob
        // (which is then counted as unknown for as long as that lasts, before it comes here).
        // For a path here that the run can look at, such a record says nothing the path can go
        // by, and the path is treated as one the record does not know: a blob that is there is
        // opened, never left unopened for good and never uploaded over unseen.
        bool recorded = synced is not null and not { Kind: SyncedKind.Ignored };
        bool changedThere = remote is not null && !(recorded && Same(remote, synced));
        switch (local)
        {
            case LocalChange.New or LocalChange.Changed:
                // In the run that restores, a file that was changed here is not uploaded over
                // the server's version unseen, whatever its time. Its time does not say
                // whether it is an edit or an older version that took the newer one's place:
                // a backup that is put back by a tool that stamps files with the present time
                // (a zip, a copy tool, a cloud download) is all "changed, and newer". So the
                // blob is opened, and Settle decides by the times as for any two versions of a
                // file: one wins, and the other is kept beside it as a conflict copy. Both
                // texts are kept either way. A real edit made just before the command gets a
                // copy of the server's version beside it, which is the price. With no blob
                // there, nothing on the server can be right, and the file is sent as always.
                bool heldAgainstTheServers = restoring && local == LocalChange.Changed && synced is { Kind: SyncedKind.Live } && remote is not null;
                return changedThere || heldAgainstTheServers ? SyncActionKind.Fetch : SyncActionKind.Upload;

            case LocalChange.Unchanged:
                // A blob that has gone from the server is sent again: the server's loss is not a deletion.
                return remote is null ? SyncActionKind.Upload
                    : changedThere ? SyncActionKind.Fetch
                    : null;

            case LocalChange.Deleted:
                // In the run that restores, no deletion is sent for a file that is gone here:
                // the blob is opened, and Settle has the file written again (TakeRemote), or
                // records a deletion note that the blob turns out to hold. With no blob there,
                // the path is forgotten as always.
                return remote is null ? SyncActionKind.Forget
                    : changedThere || restoring ? SyncActionKind.Fetch
                    : SyncActionKind.UploadTombstone;

            case LocalChange.Gone:
                return remote is null ? SyncActionKind.Forget
                    : changedThere ? SyncActionKind.Fetch
                    : now - synced!.MtimeUtc > TimeSpan.FromDays(SyncLimits.TombstoneDays) ? SyncActionKind.DeleteRemote
                    : null;

            default:
                // Nothing here and nothing known: only a record of a kind this path cannot
                // have (ignored). As for a path with no record, a blob that is there is opened;
                // with none there, nothing is left of the path but that record, which goes.
                return remote is not null ? SyncActionKind.Fetch
                    : synced is not null ? SyncActionKind.Forget
                    : null;
        }
    }

    // What the day file says about a file of this machine's own clipboard history that the
    // record knows as synced and that is not here any more.
    private enum Missing
    {
        // The owner deleted it, or nothing says otherwise: its deletion is passed on.
        DeletedByOwner,

        // Nothing this machine does to delete an entry removed it: it is made again from the server.
        NotDeletedByOwner,

        // The day file that would say which could not be looked at.
        CannotTell,
    }

    // Why a file of this machine's own history is gone. Deleting an entry, or clearing the
    // history, writes the day file of the entry's day again and removes the entry's content
    // (ClipScrub); it never removes a day file. So the owner deleted a content file only when
    // the day file of its day is still here. When the day file is gone as well, or is itself
    // the file that is gone, something else took the day away, such as retention on a day
    // when this machine's date was far ahead, and the owner did not delete anything. A file
    // that belongs to no day has no day file to go by, and counts as deleted by the owner.
    private static Missing WhyMissing(string path, string key, LocalView view)
    {
        if (SyncPath.Clipboard(path) is not { Day: { } day })
        {
            return Missing.DeletedByOwner;
        }

        // The machine's folder is the path up to its second slash; the day file lies in it,
        // under the name ClipPaths gives the day's file.
        int machineFolder = path.IndexOf('/', path.IndexOf('/') + 1);
        string dayFile = SyncPath.Key(path[..(machineFolder + 1)] + ClipPaths.DayFileName(day));
        return dayFile == key ? Missing.NotDeletedByOwner
            : view.Files.ContainsKey(dayFile) ? Missing.DeletedByOwner
            : view.Knows(dayFile) ? Missing.NotDeletedByOwner
            : Missing.CannotTell;
    }

    private static SyncActionKind? ForOwn(LocalChange local, RemoteBlob? remote, SyncedFile? synced, bool expired, bool deletedByOwner)
    {
        if (expired)
        {
            // Deleted from the server outright, with no tombstone. The copy here goes by retention.
            return remote is not null ? SyncActionKind.DeleteRemote
                : synced is not null ? SyncActionKind.Forget
                : null;
        }

        bool sameThere = remote is not null && Same(remote, synced);
        switch (local)
        {
            case LocalChange.New or LocalChange.Changed:
                return SyncActionKind.Upload;

            case LocalChange.Unchanged:
                return sameThere ? null : SyncActionKind.Upload;

            case LocalChange.Deleted:
                // Deleted by the owner: the deletion goes to the server. Gone by anything else:
                // the blob is opened, and Settle then has the file made again (Restore), since
                // nothing is here.
                return remote is null ? SyncActionKind.Forget
                    : deletedByOwner ? SyncActionKind.UploadTombstone
                    : SyncActionKind.Fetch;

            case LocalChange.Gone:
                return remote is null ? SyncActionKind.Forget
                    : sameThere ? null
                    : SyncActionKind.UploadTombstone;

            default:
                // A record that says this machine does not keep the path: from when the folder
                // was not its own, or from a run that could not look at the path.
                return remote is not null ? SyncActionKind.Fetch
                    : synced is not null ? SyncActionKind.Forget
                    : null;
        }
    }

    private static SyncActionKind? ForOther(LocalChange local, RemoteBlob? remote, SyncedFile? synced, bool expired)
    {
        if (remote is null)
        {
            // Nothing to download, and nothing is ever uploaded. A copy that is here stays until it expires.
            return synced is not null && local is LocalChange.Absent or LocalChange.Deleted or LocalChange.Gone ? SyncActionKind.Forget : null;
        }

        bool sameThere = Same(remote, synced);
        if (expired)
        {
            // Not downloaded and not tombstoned. The blob is remembered so that it is not asked for.
            return synced is { Kind: SyncedKind.Ignored } && sameThere ? null : SyncActionKind.Ignore;
        }

        if (!sameThere)
        {
            return SyncActionKind.Fetch;
        }

        // The blob is as the record has it. A copy that is missing or altered here is fetched again.
        return local switch
        {
            LocalChange.Unchanged or LocalChange.Gone => null,
            _ => SyncActionKind.Fetch,
        };
    }
}
