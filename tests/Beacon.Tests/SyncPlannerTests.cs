using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>
/// The table that decides what a sync run does. Nothing here touches a disk or a server: the
/// three views are made up in memory. The tests follow the table row by row, then the
/// three rules for clipboard folders, then what must never produce an action.
/// </summary>
public class SyncPlannerTests
{
    private const string Doc = "library/snippets/sig.md";
    private const string Mine = "clipboard/OTTER-PC/2026-10-01/aaaa.png";
    private const string MineDay = "clipboard/OTTER-PC/2026-10-01.jsonl";
    private const string Theirs = "clipboard/SEAL-PC/2026-10-01/bbbb.png";
    private const string HashOne = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string HashTwo = "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateTimeOffset SyncedAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly SyncState _state = new("scope");
    private readonly List<LocalFile> _files = [];
    private readonly List<string> _unknown = [];
    private readonly List<string> _unknownFolders = [];
    private readonly Dictionary<string, RemoteBlob> _remote = new(StringComparer.Ordinal);

    // A stand-in for the keyed name of a blob: the same for the same key, 64 hex digits.
    private static string BlobOf(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SyncPath.Key(path))));

    private static bool IsOwn(string device) => device.Equals("OTTER-PC", StringComparison.OrdinalIgnoreCase);

    // A file is here, standing against the record as said, and last written when said.
    private void Here(string path, LocalChange change, DateTimeOffset? written = null) => _files.Add(new LocalFile(path, new LocalStat(7, written ?? Now), change));

    // The record knows the path, as it was after the last sync.
    private void Synced(string path, SyncedKind kind = SyncedKind.Live, DateTimeOffset? at = null) =>
        _state.Set(new SyncedFile(path, BlobOf(path), kind, kind == SyncedKind.Live ? HashOne : "", kind == SyncedKind.Live ? 7 : 0, at ?? SyncedAt, 200, SyncedAt));

    // The server lists the path's blob: as the record has it, or changed since.
    private void There(string path, bool changed = false) =>
        _remote[BlobOf(path)] = new RemoteBlob(BlobOf(path), changed ? 201 : 200, changed ? SyncedAt.AddHours(1) : SyncedAt);

    private SyncPlan Plan(int retentionDays = 90, bool restore = false) => SyncPlanner.Plan(new PlanInput(
        new LocalView(_files, _unknown, _unknownFolders), _remote, _state, key => BlobOf(key), IsOwn, Today, retentionDays, Now, restore));

    private void AssertOne(SyncActionKind kind, string? path, LocalChange local, int retentionDays = 90)
    {
        var action = Assert.Single(Plan(retentionDays).Actions);
        Assert.Equal(kind, action.Kind);
        Assert.Equal(path, action.Path);
        Assert.Equal(local, action.Local);
        if (path is not null)
        {
            Assert.Equal(BlobOf(path), action.Blob);
        }
    }

    private void AssertNothing(int retentionDays = 90) => Assert.Empty(Plan(retentionDays).Actions);

    private static BlobHeader Content(string sha256 = HashTwo, DateTimeOffset? mtime = null, string path = Doc) => new(path, mtime ?? Now, Deleted: false, sha256);

    private static BlobHeader Tombstone(string path = Doc) => new(path, Now, Deleted: true, "");

    // ---- The table, for a path that syncs both ways ----

    [Fact]
    public void Row_1_changed_here_and_unchanged_there_is_uploaded()
    {
        Here(Doc, LocalChange.Changed);
        Synced(Doc);
        There(Doc);

        AssertOne(SyncActionKind.Upload, Doc, LocalChange.Changed);
    }

    [Fact]
    public void Row_2_unchanged_here_and_changed_there_is_downloaded()
    {
        Here(Doc, LocalChange.Unchanged);
        Synced(Doc);
        There(Doc, changed: true);

        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.Unchanged);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, expired: false, LocalChange.Unchanged, Content()));
    }

    [Fact]
    public void Row_2_an_unchanged_file_is_not_replaced_unseen_by_a_version_that_is_older_than_it()
    {
        // The file here was last written at Now and has content one; the server holds content two.
        static Settlement Settle(BlobHeader there, string? hashHere = HashOne) =>
            SyncPlanner.Settle(PathOwner.Shared, expired: false, LocalChange.Unchanged, there, hashHere, Now);

        // Written before the file here was: the file here is kept as a conflict copy first, then the server's is taken.
        Assert.Equal(Settlement.RemoteWins, Settle(Content(HashTwo, Now.AddTicks(-1))));
        Assert.Equal(Settlement.RemoteWins, Settle(Content(HashTwo, Now.AddYears(-1))));

        // Without the hash of the file here it cannot be told that the content is the same, so it is taken for other content.
        Assert.Equal(Settlement.RemoteWins, Settle(Content(HashTwo, Now.AddTicks(-1)), hashHere: null));
        Assert.Equal(Settlement.RemoteWins, Settle(Content(HashOne, Now.AddTicks(-1)), hashHere: null));

        // Written at the same time or later: taken, as an edit made on the other machine is.
        Assert.Equal(Settlement.TakeRemote, Settle(Content(HashTwo, Now)));
        Assert.Equal(Settlement.TakeRemote, Settle(Content(HashTwo, Now.AddTicks(1))));

        // The same content under an earlier time is nothing to keep a copy of.
        Assert.Equal(Settlement.TakeRemote, Settle(Content(HashOne, Now.AddYears(-1))));
        Assert.Equal(Settlement.TakeRemote, Settle(Content(HashOne.ToUpperInvariant(), Now.AddYears(-1))));

        // A tombstone still deletes an unchanged file, whatever the times.
        Assert.Equal(Settlement.DeleteLocal, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Unchanged, Tombstone(), HashOne, Now.AddYears(1)));
    }

    [Fact]
    public void Only_an_unchanged_file_outside_the_clipboard_folders_is_held_against_the_time_of_the_servers_version()
    {
        var older = Now.AddYears(-1);

        // Nothing is here to keep: the server's version is taken whatever its time.
        foreach (var local in new[] { LocalChange.Absent, LocalChange.Deleted, LocalChange.Gone })
        {
            Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, false, local, Content(HashTwo, older), HashOne, Now));
        }

        // Clipboard folders have one writer each, and go by that alone.
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Unchanged, Content(HashTwo, older, Theirs), HashOne, Now));
        Assert.Equal(Settlement.KeepLocal, SyncPlanner.Settle(PathOwner.Own, false, LocalChange.Unchanged, Content(HashTwo, older, Mine), HashOne, Now));
    }

    [Fact]
    public void Row_3_new_here_and_absent_there_is_uploaded()
    {
        Here(Doc, LocalChange.New);

        AssertOne(SyncActionKind.Upload, Doc, LocalChange.New);
    }

    [Fact]
    public void Row_4_absent_here_and_never_synced_and_present_there_is_downloaded_to_learn_its_path()
    {
        There(Doc);

        // The path is not known until the blob has been opened.
        var action = Assert.Single(Plan().Actions);
        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Doc), null, LocalChange.Absent), action);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, expired: false, LocalChange.Absent, Content()));
    }

    [Fact]
    public void Row_5_deleted_here_and_unchanged_there_uploads_a_tombstone()
    {
        Synced(Doc);
        There(Doc);

        AssertOne(SyncActionKind.UploadTombstone, Doc, LocalChange.Deleted);
    }

    [Fact]
    public void Row_6_unchanged_here_and_a_tombstone_there_is_deleted_here()
    {
        Here(Doc, LocalChange.Unchanged);
        Synced(Doc);
        There(Doc, changed: true);

        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.Unchanged);
        Assert.Equal(Settlement.DeleteLocal, SyncPlanner.Settle(PathOwner.Shared, expired: false, LocalChange.Unchanged, Tombstone()));
    }

    [Theory]
    [InlineData(LocalChange.Changed)]
    [InlineData(LocalChange.New)]
    public void Row_7_changed_on_both_sides_the_newer_one_wins_and_the_other_is_kept(LocalChange local)
    {
        Here(Doc, local);
        if (local == LocalChange.Changed)
        {
            Synced(Doc);
        }

        There(Doc, changed: true);

        AssertOne(SyncActionKind.Fetch, Doc, local);
        Assert.Equal(Settlement.RemoteWins, SyncPlanner.Settle(PathOwner.Shared, false, local, Content(mtime: Now), HashOne, Now.AddTicks(-1)));
        Assert.Equal(Settlement.LocalWins, SyncPlanner.Settle(PathOwner.Shared, false, local, Content(mtime: Now), HashOne, Now.AddTicks(1)));
    }

    [Fact]
    public void Row_7_at_the_very_same_time_both_machines_pick_the_same_winner()
    {
        // This machine holds content one and sees two on the server; the other machine the reverse.
        var here = SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashTwo, Now), HashOne, Now);
        var there = SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashOne, Now), HashTwo, Now);

        Assert.Equal(Settlement.RemoteWins, here);
        Assert.Equal(Settlement.LocalWins, there);
    }

    [Theory]
    [InlineData(LocalChange.Changed)]
    [InlineData(LocalChange.New)]
    public void Row_7_the_same_content_on_both_sides_is_no_conflict(LocalChange local)
    {
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, local, Content(HashOne, Now.AddDays(3)), HashOne, Now));
    }

    [Fact]
    public void Row_8_deleted_here_and_changed_there_the_edit_wins_and_the_file_is_restored()
    {
        Synced(Doc);
        There(Doc, changed: true);

        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.Deleted);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Deleted, Content()));
    }

    [Theory]
    [InlineData(LocalChange.Changed)]
    [InlineData(LocalChange.New)]
    public void Row_8_changed_here_and_deleted_there_the_edit_wins_and_the_file_is_kept(LocalChange local)
    {
        Assert.Equal(Settlement.KeepLocal, SyncPlanner.Settle(PathOwner.Shared, false, local, Tombstone(), HashOne, Now.AddYears(-1)));
    }

    // ---- What the table leaves out ----

    [Fact]
    public void Unchanged_on_both_sides_is_nothing_to_do()
    {
        Here(Doc, LocalChange.Unchanged);
        Synced(Doc);
        There(Doc);

        AssertNothing();
    }

    [Fact]
    public void Deleted_on_both_sides_is_only_noted()
    {
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Deleted, Tombstone()));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Gone, Tombstone()));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Absent, Tombstone()));
    }

    [Fact]
    public void A_file_whose_blob_has_gone_from_the_server_is_uploaded_again_and_never_deleted()
    {
        Here(Doc, LocalChange.Unchanged);
        Synced(Doc);

        AssertOne(SyncActionKind.Upload, Doc, LocalChange.Unchanged);
    }

    [Fact]
    public void A_path_that_is_gone_on_both_sides_is_forgotten()
    {
        Synced(Doc);

        AssertOne(SyncActionKind.Forget, Doc, LocalChange.Deleted);
    }

    [Fact]
    public void A_tombstone_that_was_synced_is_left_alone_while_it_is_young()
    {
        Synced(Doc, SyncedKind.Tombstone, at: Now.AddDays(-180));
        There(Doc);

        AssertNothing();
    }

    [Fact]
    public void A_tombstone_older_than_180_days_is_removed_from_the_server()
    {
        Synced(Doc, SyncedKind.Tombstone, at: Now.AddDays(-180).AddSeconds(-1));
        There(Doc);

        AssertOne(SyncActionKind.DeleteRemote, Doc, LocalChange.Gone);
    }

    [Fact]
    public void A_tombstone_whose_blob_changed_is_opened_again_and_a_file_in_it_is_taken()
    {
        Synced(Doc, SyncedKind.Tombstone);
        There(Doc, changed: true);

        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.Gone);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Gone, Content()));
    }

    [Fact]
    public void An_old_tombstone_whose_blob_changed_is_opened_and_not_deleted_from_the_server()
    {
        // Over 180 days old, but what the server holds now is not that tombstone any more: it
        // may be the file, made again on the other machine. It is fetched, never deleted unseen.
        Synced(Doc, SyncedKind.Tombstone, at: Now.AddDays(-400));
        There(Doc, changed: true);

        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.Gone);
    }

    [Fact]
    public void A_tombstone_whose_blob_has_gone_is_forgotten()
    {
        Synced(Doc, SyncedKind.Tombstone);

        AssertOne(SyncActionKind.Forget, Doc, LocalChange.Gone);
    }

    [Fact]
    public void A_record_that_says_ignored_for_a_path_outside_the_clipboard_folders_counts_as_no_record()
    {
        // A path outside the clipboard folders is recorded as ignored when its blob was opened
        // while the run could not look at the path (behind a link, say). Once the path can be
        // looked at, it is treated as one the record does not know.
        Synced(Doc, SyncedKind.Ignored);
        There(Doc);

        // No file here: the blob is opened, as a blob that no path claims is. It is not left unopened for good.
        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.Absent);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Absent, Content()));

        // A file here: it is new against a blob nobody has compared it with, so the blob is opened first and not uploaded over.
        Here(Doc, LocalChange.New);
        AssertOne(SyncActionKind.Fetch, Doc, LocalChange.New);

        // No blob: the file is uploaded; and with no file either, the record goes.
        _remote.Clear();
        AssertOne(SyncActionKind.Upload, Doc, LocalChange.New);
        _files.Clear();
        AssertOne(SyncActionKind.Forget, Doc, LocalChange.Absent);
    }

    [Fact]
    public void The_hash_of_the_file_here_is_compared_whatever_the_case_of_its_hex_digits()
    {
        const string lower = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        string upper = lower.ToUpperInvariant();

        // The same content is the same content, and no conflict.
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(lower, Now.AddDays(3)), upper, Now));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Changed, Content(lower, path: Theirs), upper, Now));

        // At the very same time the greater hash wins, by its value: a capital B is greater
        // than a small a, though it sorts before it as a character.
        string a = new('a', 64);
        string b = new('b', 64);
        Assert.Equal(Settlement.LocalWins, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(a, Now), b.ToUpperInvariant(), Now));
        Assert.Equal(Settlement.RemoteWins, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(b, Now), a.ToUpperInvariant(), Now));
    }

    [Fact]
    public void A_blob_at_a_path_the_run_could_not_look_at_and_the_record_does_not_know_is_opened_as_one_of_unknown_path()
    {
        // No file is in the view and no record names the blob, so the plan cannot tell whose
        // it is. Settle would answer as for a path with nothing here. It must not be asked:
        // the caller holds the opened blob's path against LocalView.Knows first.
        There(Doc);
        _unknown.Add(Doc);

        var plan = Plan();

        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Doc), null, LocalChange.Absent), Assert.Single(plan.Actions));
        Assert.Equal(0, plan.Unknown);
        Assert.False(new LocalView(_files, _unknown, _unknownFolders).Knows(SyncPath.Key(Doc)));
    }

    [Fact]
    public void A_file_made_again_where_one_was_deleted_is_uploaded_over_the_tombstone()
    {
        Here(Doc, LocalChange.New);
        Synced(Doc, SyncedKind.Tombstone);
        There(Doc);

        AssertOne(SyncActionKind.Upload, Doc, LocalChange.New);
    }

    // ---- Clipboard rule 1: one writer for each machine's folder ----

    [Theory]
    [InlineData(LocalChange.New, false)]
    [InlineData(LocalChange.Changed, false)]
    [InlineData(LocalChange.Changed, true)]     // even when the blob changed there, it is not fetched
    [InlineData(LocalChange.Unchanged, true)]   // somebody else wrote the blob: ours is sent again
    public void A_file_in_this_machines_folder_is_uploaded_and_never_fetched(LocalChange local, bool changedThere)
    {
        Here(Mine, local);
        if (local != LocalChange.New)
        {
            Synced(Mine);
            There(Mine, changedThere);
        }

        AssertOne(SyncActionKind.Upload, Mine, local);
    }

    // ---- Clipboard rule 1, for a file that is gone here: who removed it ----
    //
    // Deleting an entry (a scrub) writes the day's day file again and never removes it. So a
    // file of this machine's own history that is gone here was deleted by the owner only when
    // the day file of its day is still here. With the day file gone too, something else took
    // the day away (retention under a wrong date, say), and that is no deletion to pass on.

    [Theory]
    [InlineData(LocalChange.Unchanged, false)]
    [InlineData(LocalChange.Changed, false)]    // as a scrub leaves it: written again
    [InlineData(LocalChange.Changed, true)]     // whatever the server holds for the content file
    [InlineData(LocalChange.New, false)]
    public void A_content_file_gone_from_this_machines_folder_while_the_day_file_of_its_day_is_here_is_tombstoned(LocalChange dayFile, bool changedThere)
    {
        Synced(Mine);
        There(Mine, changedThere);
        Here(MineDay, dayFile);
        if (dayFile != LocalChange.New)
        {
            Synced(MineDay);
            There(MineDay);
        }

        var action = Assert.Single(Plan().Actions, action => action.Path == Mine);

        Assert.Equal(SyncActionKind.UploadTombstone, action.Kind);
        Assert.Equal(LocalChange.Deleted, action.Local);
        Assert.Equal(BlobOf(Mine), action.Blob);
    }

    [Theory]
    [InlineData(true, false)]       // the day file was synced, and is gone as well
    [InlineData(true, true)]
    [InlineData(false, false)]      // no day file here, and the record never knew one
    [InlineData(false, true)]
    public void A_content_file_gone_from_this_machines_folder_with_the_day_file_of_its_day_gone_too_is_fetched_and_never_tombstoned(bool dayFileWasSynced, bool changedThere)
    {
        Synced(Mine);
        There(Mine, changedThere);
        if (dayFileWasSynced)
        {
            Synced(MineDay);
            There(MineDay);
        }

        var plan = Plan();

        var action = Assert.Single(plan.Actions, action => action.Path == Mine);
        Assert.Equal(SyncActionKind.Fetch, action.Kind);
        Assert.Equal(LocalChange.Deleted, action.Local);
        Assert.Equal(BlobOf(Mine), action.Blob);
        Assert.All(plan.Actions, each => Assert.Equal(SyncActionKind.Fetch, each.Kind));

        // What the blob holds is then written here again; a tombstone in it is only noted.
        Assert.Equal(Settlement.Restore, SyncPlanner.Settle(PathOwner.Own, false, LocalChange.Deleted, Content(path: Mine)));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Own, false, LocalChange.Deleted, Tombstone(Mine)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]       // a content file of the day is still here
    public void A_day_file_gone_from_this_machines_folder_is_fetched_and_never_tombstoned(bool contentStillHere, bool changedThere)
    {
        Synced(MineDay);
        There(MineDay, changedThere);
        if (contentStillHere)
        {
            Here(Mine, LocalChange.Unchanged);
            Synced(Mine);
            There(Mine);
        }

        AssertOne(SyncActionKind.Fetch, MineDay, LocalChange.Deleted);
        Assert.Equal(Settlement.Restore, SyncPlanner.Settle(PathOwner.Own, false, LocalChange.Deleted, Content(path: MineDay)));
    }

    [Theory]
    [InlineData("a content file, the day file here")]
    [InlineData("a content file, the day file gone")]
    [InlineData("the day file")]
    public void A_file_gone_from_an_expired_day_of_this_machines_folder_has_its_blob_deleted_outright_whatever_became_of_the_day_file(string which)
    {
        string gone = which == "the day file" ? MineDay : Mine;
        Synced(gone);
        There(gone);
        if (which == "a content file, the day file here")
        {
            Here(MineDay, LocalChange.New);
        }

        var action = Assert.Single(Plan(retentionDays: 1).Actions, action => action.Path == gone);

        Assert.Equal(SyncActionKind.DeleteRemote, action.Kind);
        Assert.Equal(LocalChange.Deleted, action.Local);
    }

    [Theory]
    [InlineData("clipboard/OTTER-PC/notes.txt")]                // in the machine's folder, and no day's file
    [InlineData("clipboard/OTTER-PC/2026-10-01.jsonl.bak")]     // not a day file's name
    [InlineData("clipboard/OTTER-PC/attachments/x.png")]        // in a folder that is no day's
    public void A_file_gone_from_this_machines_folder_that_belongs_to_no_day_is_tombstoned(string path)
    {
        Synced(path);
        There(path);

        AssertOne(SyncActionKind.UploadTombstone, path, LocalChange.Deleted);
    }

    [Fact]
    public void A_content_file_gone_from_this_machines_folder_is_left_for_later_when_the_day_file_of_its_day_could_not_be_looked_at()
    {
        // Whether the day file is here is what says who removed the content file. A day file
        // the run could not look at says neither, so the content file is neither tombstoned
        // nor made again by this run, and is counted with what was left alone.
        Synced(Mine);
        There(Mine);
        _unknown.Add(MineDay);

        var plan = Plan();

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Unknown);
        Assert.Equal(0, plan.StillBad);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("here")]
    [InlineData("not looked at")]
    public void A_file_gone_from_this_machines_folder_whose_blob_has_gone_too_is_forgotten_whatever_became_of_the_day_file(string dayFile)
    {
        Synced(Mine);
        if (dayFile == "here")
        {
            Here(MineDay, LocalChange.New);
        }
        else if (dayFile == "not looked at")
        {
            _unknown.Add(MineDay);
        }

        var plan = Plan();

        var action = Assert.Single(plan.Actions, action => action.Path == Mine);
        Assert.Equal(SyncActionKind.Forget, action.Kind);
        Assert.Equal(0, plan.Unknown);
    }

    [Theory]
    [InlineData(null, SyncActionKind.Forget)]               // the blob has gone: nothing is left of the path
    [InlineData(false, null)]                               // the tombstone this machine sent is still there
    [InlineData(true, SyncActionKind.UploadTombstone)]      // something else is there now: the tombstone is sent again
    public void A_file_this_machine_deleted_from_its_own_folder_stays_deleted_on_the_server(bool? changedThere, SyncActionKind? expected)
    {
        Synced(Mine, SyncedKind.Tombstone);
        if (changedThere is { } changed)
        {
            There(Mine, changed);
        }

        var actions = Plan().Actions;

        Assert.Equal(expected is null ? [] : [expected.Value], actions.Select(action => action.Kind));
        Assert.All(actions, action => Assert.Equal(LocalChange.Gone, action.Local));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_made_again_in_this_machines_own_folder_is_uploaded_whatever_the_server_holds(bool? changedThere)
    {
        Here(Mine, LocalChange.New);
        Synced(Mine, SyncedKind.Tombstone);
        if (changedThere is { } changed)
        {
            There(Mine, changed);
        }

        AssertOne(SyncActionKind.Upload, Mine, LocalChange.New);
    }

    [Fact]
    public void A_file_that_is_here_in_this_machines_folder_is_never_changed_by_what_the_server_holds()
    {
        foreach (var local in new[] { LocalChange.New, LocalChange.Unchanged, LocalChange.Changed })
        {
            Assert.Equal(Settlement.KeepLocal, SyncPlanner.Settle(PathOwner.Own, false, local, Content(path: Mine), HashOne, Now.AddYears(-1)));
            Assert.Equal(Settlement.KeepLocal, SyncPlanner.Settle(PathOwner.Own, false, local, Tombstone(Mine), HashOne, Now.AddYears(-1)));
        }
    }

    [Fact]
    public void A_file_of_this_machines_folder_that_is_missing_here_is_made_again_from_the_server()
    {
        There(Mine);

        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Mine), null, LocalChange.Absent), Assert.Single(Plan().Actions));
        Assert.Equal(Settlement.Restore, SyncPlanner.Settle(PathOwner.Own, false, LocalChange.Absent, Content(path: Mine)));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Own, false, LocalChange.Absent, Tombstone(Mine)));
    }

    [Theory]
    [InlineData(LocalChange.Changed)]   // a copy that was synced and then altered here; the blob is as the record has it
    [InlineData(LocalChange.New)]       // a file made here that the record does not know, where that machine's blob is
    public void A_copy_of_another_machines_file_that_was_altered_here_is_downloaded_again(LocalChange local)
    {
        Here(Theirs, local);
        if (local == LocalChange.Changed)
        {
            Synced(Theirs);
        }

        There(Theirs);

        // The blob that is opened holds that machine's file, which replaces what is here.
        AssertOne(SyncActionKind.Fetch, Theirs, local);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Other, false, local, Content(path: Theirs), HashOne, Now.AddYears(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_made_here_in_another_machines_folder_where_that_machine_deleted_one_is_deleted_here(bool changedThere)
    {
        // The record knows the path as deleted by that machine. The blob that is opened is its
        // tombstone, and what that machine says about its folder goes, whatever was made here.
        Here(Theirs, LocalChange.New);
        Synced(Theirs, SyncedKind.Tombstone);
        There(Theirs, changedThere);

        AssertOne(SyncActionKind.Fetch, Theirs, LocalChange.New);
        Assert.Equal(Settlement.DeleteLocal, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.New, Tombstone(Theirs), HashOne, Now.AddYears(1)));
    }

    [Fact]
    public void A_copy_of_another_machines_file_stays_here_when_its_blob_has_gone_from_the_server()
    {
        // Nothing is downloaded, nothing is ever uploaded, and the copy is not deleted and not
        // forgotten: it stays until this machine's own retention removes it.
        Here(Theirs, LocalChange.Unchanged);
        Synced(Theirs);

        AssertNothing();
    }

    [Fact]
    public void A_copy_of_another_machines_file_that_is_missing_here_is_downloaded_again()
    {
        Synced(Theirs);
        There(Theirs);

        AssertOne(SyncActionKind.Fetch, Theirs, LocalChange.Deleted);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Deleted, Content(path: Theirs)));
    }

    [Fact]
    public void A_file_in_another_machines_folder_is_never_uploaded()
    {
        Here(Theirs, LocalChange.New);
        Here("clipboard/SEAL-PC/2026-10-02.jsonl", LocalChange.Changed);
        Synced("clipboard/SEAL-PC/2026-10-02.jsonl");

        // Not on the server, or gone from it: nothing is sent in either case.
        AssertNothing();
    }

    [Fact]
    public void A_file_missing_from_another_machines_folder_is_never_tombstoned()
    {
        Synced(Theirs);

        AssertOne(SyncActionKind.Forget, Theirs, LocalChange.Deleted);
    }

    [Fact]
    public void What_another_machine_changed_or_deleted_in_its_folder_is_taken_as_it_is()
    {
        Here(Theirs, LocalChange.Unchanged);
        Synced(Theirs);
        There(Theirs, changed: true);

        AssertOne(SyncActionKind.Fetch, Theirs, LocalChange.Unchanged);
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Unchanged, Content(path: Theirs)));
        Assert.Equal(Settlement.DeleteLocal, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Unchanged, Tombstone(Theirs)));
        Assert.Equal(Settlement.DeleteLocal, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Changed, Tombstone(Theirs), HashOne, Now));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Deleted, Tombstone(Theirs)));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Other, false, LocalChange.Changed, Content(HashOne, path: Theirs), HashOne, Now));
    }

    [Fact]
    public void A_folder_this_machine_wrote_under_an_earlier_name_is_still_its_own()
    {
        const string earlier = "clipboard/OLD-OTTER/2026-10-01.jsonl";
        Here(earlier, LocalChange.Changed);
        Synced(earlier);
        There(earlier);
        var view = new LocalView(_files);

        var plan = SyncPlanner.Plan(new PlanInput(view, _remote, _state, BlobOf, device => device is "OTTER-PC" or "OLD-OTTER", Today, 90, Now));

        Assert.Equal(SyncActionKind.Upload, Assert.Single(plan.Actions).Kind);
        Assert.Equal(PathOwner.Own, SyncPlanner.OwnerOf(earlier, device => device is "OTTER-PC" or "OLD-OTTER"));
        Assert.Equal(PathOwner.Other, SyncPlanner.OwnerOf(earlier, IsOwn));
        Assert.Equal(PathOwner.Shared, SyncPlanner.OwnerOf(Doc, IsOwn));
        Assert.Equal(PathOwner.Shared, SyncPlanner.OwnerOf("clipboard/readme.txt", IsOwn));
    }

    // ---- Clipboard rule 2: expired days are ignored ----

    [Fact]
    public void A_day_is_expired_when_it_is_more_than_the_kept_days_before_today()
    {
        Assert.False(SyncPlanner.IsExpired("clipboard/SEAL-PC/2026-09-03.jsonl", Today, 30));
        Assert.True(SyncPlanner.IsExpired("clipboard/SEAL-PC/2026-09-02.jsonl", Today, 30));
        Assert.True(SyncPlanner.IsExpired("clipboard/SEAL-PC/2026-09-02/x.png", Today, 30));
        Assert.False(SyncPlanner.IsExpired("clipboard/SEAL-PC/notes.txt", Today, 1));
        Assert.False(SyncPlanner.IsExpired("library/2020-01-01.jsonl", Today, 1));
    }

    [Fact]
    public void An_expired_day_of_another_machine_is_not_downloaded_and_its_blob_is_remembered()
    {
        Synced(Theirs);
        There(Theirs, changed: true);

        AssertOne(SyncActionKind.Ignore, Theirs, LocalChange.Deleted, retentionDays: 1);
        Assert.Equal(Settlement.Ignore, SyncPlanner.Settle(PathOwner.Other, expired: true, LocalChange.Absent, Content(path: Theirs)));
    }

    [Fact]
    public void A_remembered_expired_blob_is_left_alone_until_it_goes_from_the_server()
    {
        Synced(Theirs, SyncedKind.Ignored);
        There(Theirs);
        AssertNothing(retentionDays: 1);

        _remote.Clear();
        AssertOne(SyncActionKind.Forget, Theirs, LocalChange.Absent, retentionDays: 1);
    }

    [Fact]
    public void An_expired_day_of_another_machine_is_never_tombstoned_or_uploaded()
    {
        Synced(Theirs);
        Here("clipboard/SEAL-PC/2026-10-01/cccc.png", LocalChange.New);

        // Missing here and gone from the server: forgotten. Here and not on the server: left alone.
        AssertOne(SyncActionKind.Forget, Theirs, LocalChange.Deleted, retentionDays: 1);
    }

    [Fact]
    public void A_remembered_blob_whose_day_is_kept_again_is_downloaded()
    {
        Synced(Theirs, SyncedKind.Ignored);
        There(Theirs);

        AssertOne(SyncActionKind.Fetch, Theirs, LocalChange.Absent, retentionDays: 90);
    }

    // ---- Clipboard rule 3: the owner deletes its expired blobs outright ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_expired_blob_of_this_machine_is_deleted_from_the_server_with_no_tombstone(bool stillHere)
    {
        if (stillHere)
        {
            Here(Mine, LocalChange.Changed);
        }

        Synced(Mine);
        There(Mine);

        AssertOne(SyncActionKind.DeleteRemote, Mine, stillHere ? LocalChange.Changed : LocalChange.Deleted, retentionDays: 1);
        Assert.Equal(Settlement.RemoveRemote, SyncPlanner.Settle(PathOwner.Own, expired: true, LocalChange.Absent, Content(path: Mine)));
    }

    [Fact]
    public void An_expired_file_of_this_machine_is_not_uploaded_and_is_forgotten_once_its_blob_has_gone()
    {
        Here("clipboard/OTTER-PC/2026-10-01/new.png", LocalChange.New);
        Synced(Mine);

        AssertOne(SyncActionKind.Forget, Mine, LocalChange.Deleted, retentionDays: 1);
    }

    // ---- What must never produce an action ----

    [Fact]
    public void A_path_the_run_could_not_look_at_gets_no_action_and_is_never_taken_for_deleted()
    {
        Synced(Doc);
        There(Doc, changed: true);
        Synced("library/scripts/deploy.ps1");
        There("library/scripts/deploy.ps1");
        _unknown.Add(Doc);
        _unknownFolders.Add("library/scripts");

        var plan = Plan();

        Assert.Empty(plan.Actions);
        Assert.Equal(2, plan.Unknown);
    }

    [Fact]
    public void A_blob_that_could_not_be_used_is_not_fetched_again_while_it_is_the_same()
    {
        There(Doc);
        _state.SetBad(_remote[BlobOf(Doc)]);

        var plan = Plan();

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.StillBad);
    }

    [Fact]
    public void A_blob_that_could_not_be_used_is_fetched_again_once_it_has_changed()
    {
        There(Doc);
        _state.SetBad(_remote[BlobOf(Doc)]);
        There(Doc, changed: true);

        var plan = Plan();

        Assert.Equal(SyncActionKind.Fetch, Assert.Single(plan.Actions).Kind);
        Assert.Equal(0, plan.StillBad);
    }

    // The blob of a path is remembered as one that could not be used, exactly as the server lists it now.
    private void Unusable(string path) => _state.SetBad(_remote[BlobOf(path)]);

    [Theory]
    // A path that syncs both ways. The blob is listed otherwise than the record has it, as it is when it was fetched and found bad.
    [InlineData(Doc, LocalChange.Absent, SyncedKind.Live, true)]            // deleted here: no tombstone goes over it, and it is not fetched
    [InlineData(Doc, LocalChange.Absent, SyncedKind.Tombstone, true)]
    [InlineData(Doc, LocalChange.Unchanged, SyncedKind.Live, true)]
    [InlineData(Doc, LocalChange.Changed, SyncedKind.Live, true)]           // changed here: not fetched, and never uploaded over it
    [InlineData(Doc, LocalChange.New, null, true)]
    [InlineData(Doc, LocalChange.New, SyncedKind.Tombstone, true)]
    // The same when the blob is listed as the record has it.
    [InlineData(Doc, LocalChange.Absent, SyncedKind.Live, false)]
    [InlineData(Doc, LocalChange.Changed, SyncedKind.Live, false)]
    [InlineData(Doc, LocalChange.New, SyncedKind.Tombstone, false)]
    // A folder this machine writes: nothing is uploaded over it either.
    [InlineData(Mine, LocalChange.New, null, true)]
    [InlineData(Mine, LocalChange.Absent, SyncedKind.Live, true)]
    [InlineData(Mine, LocalChange.Absent, SyncedKind.Live, false)]
    [InlineData(Mine, LocalChange.Absent, SyncedKind.Tombstone, true)]
    [InlineData(Mine, LocalChange.Absent, SyncedKind.Ignored, true)]
    [InlineData(Mine, LocalChange.Unchanged, SyncedKind.Live, true)]
    [InlineData(Mine, LocalChange.Changed, SyncedKind.Live, true)]
    [InlineData(Mine, LocalChange.Changed, SyncedKind.Live, false)]
    // Another machine's folder.
    [InlineData(Theirs, LocalChange.Absent, SyncedKind.Live, true)]
    [InlineData(Theirs, LocalChange.Absent, SyncedKind.Live, false)]
    [InlineData(Theirs, LocalChange.Absent, SyncedKind.Tombstone, true)]
    [InlineData(Theirs, LocalChange.Unchanged, SyncedKind.Live, true)]
    [InlineData(Theirs, LocalChange.Changed, SyncedKind.Live, true)]
    [InlineData(Theirs, LocalChange.Changed, SyncedKind.Live, false)]
    [InlineData(Theirs, LocalChange.New, null, true)]
    public void A_path_whose_blob_could_not_be_used_gets_no_action_while_the_server_lists_the_blob_the_same(string path, LocalChange here, SyncedKind? record, bool listedOtherwiseThanTheRecord)
    {
        if (record is { } kind)
        {
            Synced(path, kind);
        }

        if (here != LocalChange.Absent)
        {
            Here(path, here);
        }

        There(path, listedOtherwiseThanTheRecord);
        Unusable(path);

        var plan = Plan();

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.StillBad);
    }

    [Fact]
    public void A_path_whose_blob_could_not_be_used_is_fetched_again_once_the_server_lists_the_blob_differently()
    {
        Here(Doc, LocalChange.Changed);
        Synced(Doc);
        There(Doc, changed: true);
        Unusable(Doc);
        _remote[BlobOf(Doc)] = new RemoteBlob(BlobOf(Doc), 202, SyncedAt.AddHours(2));

        var plan = Plan();

        Assert.Equal(SyncActionKind.Fetch, Assert.Single(plan.Actions).Kind);
        Assert.Equal(0, plan.StillBad);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_expired_blob_of_this_machine_that_could_not_be_used_is_still_deleted_from_the_server_outright(bool stillHere)
    {
        if (stillHere)
        {
            Here(Mine, LocalChange.Changed);
        }

        Synced(Mine);
        There(Mine, changed: true);
        Unusable(Mine);

        var plan = Plan(retentionDays: 1);

        Assert.Equal(SyncActionKind.DeleteRemote, Assert.Single(plan.Actions).Kind);
        Assert.Equal(0, plan.StillBad);
    }

    [Theory]
    [InlineData(null, SyncedKind.Live, true)]                       // synced while the day was kept, the blob then replaced by one that could not be used
    [InlineData(null, SyncedKind.Live, false)]
    [InlineData(null, SyncedKind.Tombstone, true)]
    [InlineData(null, SyncedKind.Ignored, true)]
    [InlineData(null, SyncedKind.Ignored, false)]                   // already remembered as not kept, under this very listing
    [InlineData(LocalChange.Unchanged, SyncedKind.Live, true)]      // the copy here has not gone yet
    [InlineData(LocalChange.New, null, true)]
    public void An_expired_day_of_another_machine_whose_blob_could_not_be_used_is_ignored_and_no_longer_counted(LocalChange? here, SyncedKind? record, bool listedOtherwiseThanTheRecord)
    {
        // This machine would not keep the file whatever the blob held. So the blob is
        // remembered as one that is not kept, as any expired day of another machine is, and
        // sync is not shown as degraded for it until the other machine deletes it.
        if (record is { } kind)
        {
            Synced(Theirs, kind);
        }

        if (here is { } change)
        {
            Here(Theirs, change);
        }

        There(Theirs, listedOtherwiseThanTheRecord);
        Unusable(Theirs);

        var plan = Plan(retentionDays: 1);

        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncActionKind.Ignore, action.Kind);
        Assert.Equal(Theirs, action.Path);
        Assert.Equal(BlobOf(Theirs), action.Blob);
        Assert.Equal(0, plan.StillBad);
        Assert.Equal(0, plan.Unknown);
    }

    [Theory]
    [InlineData(200, 1)]    // the same size at another time, as when the same content was uploaded again
    [InlineData(201, 0)]    // another size at the same time
    public void A_blob_that_could_not_be_used_is_looked_at_afresh_as_soon_as_its_size_or_its_time_alone_is_listed_differently(long size, int hoursLater)
    {
        Here(Doc, LocalChange.Changed);
        Synced(Doc);
        There(Doc);
        Unusable(Doc);
        Assert.Equal(1, Plan().StillBad);
        _remote[BlobOf(Doc)] = new RemoteBlob(BlobOf(Doc), size, SyncedAt.AddHours(hoursLater));

        var plan = Plan();

        Assert.Equal(SyncActionKind.Fetch, Assert.Single(plan.Actions).Kind);
        Assert.Equal(0, plan.StillBad);

        // And for a blob that no path here claims.
        _files.Clear();
        _state.Remove(SyncPath.Key(Doc));
        var stray = Plan();

        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Doc), null, LocalChange.Absent), Assert.Single(stray.Actions));
        Assert.Equal(0, stray.StillBad);
    }

    [Fact]
    public void A_path_the_run_could_not_look_at_whose_blob_could_not_be_used_is_counted_as_both()
    {
        Synced(Doc);
        There(Doc, changed: true);
        Unusable(Doc);
        _unknown.Add(Doc);

        var plan = Plan();

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Unknown);
        Assert.Equal(1, plan.StillBad);
    }

    [Fact]
    public void Every_cell_of_the_table_for_a_blob_that_could_not_be_used_holds_nothing_but_what_is_done_about_an_expired_day()
    {
        // The whole column of the table for a blob that is remembered as unusable and listed as
        // it was then: five kinds of path, five ways the path can stand here, five things the
        // record can say about it, and the blob listed as the record has it or otherwise.
        (string Path, int RetentionDays, string Name)[] kinds =
        [
            (Doc, 90, "shared"), (Mine, 90, "own"), (Mine, 1, "own, expired"), (Theirs, 90, "other"), (Theirs, 1, "other, expired"),
        ];
        string[] heres = ["no file", "unchanged", "changed", "new", "unknown"];
        string[] records = ["none", "live", "tombstone", "old tombstone", "ignored"];
        var wrong = new List<string>();
        int cells = 0;
        foreach (var (path, retentionDays, kind) in kinds)
        {
            foreach (string here in heres)
            {
                foreach (string record in records)
                {
                    foreach (bool listedOtherwise in new[] { false, true })
                    {
                        _files.Clear();
                        _unknown.Clear();
                        _remote.Clear();
                        foreach (string key in _state.Files.Keys.ToList())
                        {
                            _state.Remove(key);
                        }

                        foreach (string name in _state.Bad.Keys.ToList())
                        {
                            _state.RemoveBad(name);
                        }

                        switch (here)
                        {
                            case "unchanged": Here(path, LocalChange.Unchanged); break;
                            case "changed": Here(path, LocalChange.Changed); break;
                            case "new": Here(path, LocalChange.New); break;
                            case "unknown": _unknown.Add(path); break;
                        }

                        switch (record)
                        {
                            case "live": Synced(path); break;
                            case "tombstone": Synced(path, SyncedKind.Tombstone); break;
                            case "old tombstone": Synced(path, SyncedKind.Tombstone, at: Now.AddDays(-400)); break;
                            case "ignored": Synced(path, SyncedKind.Ignored); break;
                        }

                        There(path, listedOtherwise);
                        Unusable(path);

                        var plan = Plan(retentionDays);

                        // A path here has the blob when there is a file or a record for it. Then
                        // the machine that writes an expired day deletes the blob outright, and
                        // the other machine remembers it as one it does not keep; neither
                        // counts it any longer. Every other cell holds nothing, and counts it.
                        bool claimed = record != "none" || here is not ("no file" or "unknown");
                        string want = here == "unknown" && record != "none" ? "nothing, still bad 1, unknown 1"
                            : claimed && kind == "own, expired" ? "DeleteRemote, still bad 0, unknown 0"
                            : claimed && kind == "other, expired" ? "Ignore, still bad 0, unknown 0"
                            : "nothing, still bad 1, unknown 0";
                        string actions = plan.Actions.Count == 0 ? "nothing" : string.Join("+", plan.Actions.Select(action => action.Kind));
                        string got = FormattableString.Invariant($"{actions}, still bad {plan.StillBad}, unknown {plan.Unknown}");
                        if (got != want)
                        {
                            wrong.Add($"{kind} / {here} / {record} / listed otherwise {listedOtherwise}: {got}");
                        }

                        cells++;
                    }
                }
            }
        }

        Assert.Empty(wrong);
        Assert.Equal(250, cells);
    }

    [Fact]
    public void Actions_come_in_the_order_of_the_paths_and_blobs_of_unknown_path_come_last()
    {
        Here("library/b.md", LocalChange.New);
        Here("library/a.md", LocalChange.New);
        There("library/unknown.md");
        Synced("library/c.md");
        There("library/c.md");

        var actions = Plan().Actions;

        Assert.Equal(["library/a.md", "library/b.md", "library/c.md", null], actions.Select(action => action.Path));
        Assert.Equal(
            [SyncActionKind.Upload, SyncActionKind.Upload, SyncActionKind.UploadTombstone, SyncActionKind.Fetch],
            actions.Select(action => action.Kind));
    }

    [Fact]
    public void A_file_spelt_in_other_case_than_the_record_is_the_same_path()
    {
        _files.Add(new LocalFile("Library/Snippets/SIG.md", new LocalStat(7, Now), LocalChange.Unchanged));
        Synced(Doc);
        There(Doc);

        AssertNothing();
    }

    [Fact]
    public void An_action_and_a_plan_print_nothing_of_a_path()
    {
        Here(Doc, LocalChange.New);

        var plan = Plan();

        Assert.Equal("SyncPlan { Actions = 1 }", plan.ToString());
        Assert.Equal("SyncAction { Kind = Upload }", plan.Actions[0].ToString());
    }

    // ---- Restore Missing Files: for one run the server is right where this folder is behind ----

    [Fact]
    public void Restoring_a_library_file_that_is_gone_here_is_fetched_and_no_deletion_note_is_planned_for_it()
    {
        Synced(Doc);
        There(Doc);

        // As always: gone here and unchanged there, its deletion goes to the server.
        AssertOne(SyncActionKind.UploadTombstone, Doc, LocalChange.Deleted);

        // Restoring: the blob is opened in place of that.
        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Doc), Doc, LocalChange.Deleted), Assert.Single(Plan(restore: true).Actions));

        // What the blob holds is then written here. A deletion note in it is only recorded: the file stays deleted.
        Assert.Equal(Settlement.TakeRemote, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Deleted, Content()));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Deleted, Tombstone()));
    }

    [Fact]
    public void Restoring_a_library_file_that_is_gone_here_and_that_the_server_holds_nothing_for_is_forgotten()
    {
        Synced(Doc);

        Assert.Equal(new SyncAction(SyncActionKind.Forget, BlobOf(Doc), Doc, LocalChange.Deleted), Assert.Single(Plan(restore: true).Actions));
    }

    [Fact]
    public void Restoring_a_library_file_that_was_changed_here_and_is_older_than_the_record_is_fetched_and_not_uploaded()
    {
        // The mark of a folder put back from a backup: the content is not the record's, and the
        // file was last written before the version that was synced.
        var older = SyncedAt.AddTicks(-1);
        Here(Doc, LocalChange.Changed, older);
        Synced(Doc);
        There(Doc);

        // As always: changed here and unchanged there, it is uploaded over the server's version.
        AssertOne(SyncActionKind.Upload, Doc, LocalChange.Changed);

        // Restoring: the blob is opened in place of that.
        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Doc), Doc, LocalChange.Changed), Assert.Single(Plan(restore: true).Actions));

        // And it is settled by the times, like any two versions of one file. The server holds
        // the version that was synced, which is the newer: it is taken, and the file here is
        // kept beside it as a conflict copy.
        Assert.Equal(Settlement.RemoteWins, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashOne, SyncedAt), HashTwo, older));

        // Should the server's version be the older one after all, the file here wins and the
        // server's is kept as the copy; and the same content on both sides is no conflict.
        Assert.Equal(Settlement.LocalWins, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashOne, older.AddDays(-1)), HashTwo, older));
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashTwo, SyncedAt), HashTwo, older));
    }

    [Theory]
    [InlineData(-864_000_000_000)]  // written a day before the version the record holds: a backup put back with its file times
    [InlineData(-1)]
    [InlineData(0)]                 // written at the very time the record holds
    [InlineData(1)]                 // written after it: an edit, or a backup put back by a tool that stamps files with the present time
    [InlineData(864_000_000_000)]
    public void Restoring_a_library_file_that_was_changed_here_is_fetched_whatever_its_time_and_both_versions_are_kept(long ticksAfter)
    {
        // The time of a file says nothing about where it came from: a zip, a copy tool or a
        // cloud download stamps an old version with the present time. So in the run that
        // restores, every file that was changed here while its blob is as the record has it is
        // held against the server's version, and is not uploaded over it.
        var written = SyncedAt.AddTicks(ticksAfter);
        Here(Doc, LocalChange.Changed, written);
        Synced(Doc);
        There(Doc);

        AssertOne(SyncActionKind.Upload, Doc, LocalChange.Changed);
        Assert.Equal(new SyncAction(SyncActionKind.Fetch, BlobOf(Doc), Doc, LocalChange.Changed), Assert.Single(Plan(restore: true).Actions));

        // Settled by the times, like an edit on both sides. Whichever wins, the other is kept
        // beside it as a conflict copy: the server's version is never lost unseen.
        var settled = SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashOne, SyncedAt), HashTwo, written);
        Assert.Equal(ticksAfter < 0 ? Settlement.RemoteWins : Settlement.LocalWins, settled);

        // The same content on both sides is no conflict, whatever the times.
        Assert.Equal(Settlement.Record, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Content(HashTwo, SyncedAt), HashTwo, written));
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void Restoring_a_changed_file_is_as_always_when_the_blob_is_gone_or_has_changed(int daysAfter)
    {
        Here(Doc, LocalChange.Changed, SyncedAt.AddDays(daysAfter));
        Synced(Doc);

        // Nothing on the server to be right: the file is sent, as a file whose blob has gone always is.
        Assert.Equal(SyncActionKind.Upload, Assert.Single(Plan().Actions).Kind);
        Assert.Equal(SyncActionKind.Upload, Assert.Single(Plan(restore: true).Actions).Kind);

        // Another version on the server: opened and settled, as always. A deletion note there
        // loses to the file here, in the run that restores as in any other.
        There(Doc, changed: true);
        Assert.Equal(SyncActionKind.Fetch, Assert.Single(Plan().Actions).Kind);
        Assert.Equal(SyncActionKind.Fetch, Assert.Single(Plan(restore: true).Actions).Kind);
        Assert.Equal(Settlement.KeepLocal, SyncPlanner.Settle(PathOwner.Shared, false, LocalChange.Changed, Tombstone(), HashTwo, SyncedAt.AddDays(daysAfter)));
    }

    [Fact]
    public void Restoring_a_file_the_record_does_not_know_is_as_always()
    {
        // A new file is no version of anything the server holds for the record.
        Here(Doc, LocalChange.New);

        Assert.Equal(SyncActionKind.Upload, Assert.Single(Plan(restore: true).Actions).Kind);

        // With a blob there it is opened, in any run.
        There(Doc);
        Assert.Equal(SyncActionKind.Fetch, Assert.Single(Plan().Actions).Kind);
        Assert.Equal(SyncActionKind.Fetch, Assert.Single(Plan(restore: true).Actions).Kind);
    }

    [Fact]
    public void Restoring_changes_two_cells_of_the_table_whatever_the_files_time_and_no_other()
    {
        // Every cell of the table with the flag and without it: five kinds of path, six ways
        // the path can stand here, five things the record can say about it, and the blob not
        // there, listed as the record has it, or listed otherwise.
        (string Path, int RetentionDays, string Name)[] kinds =
        [
            (Doc, 90, "shared"), (Mine, 90, "own"), (Mine, 1, "own, expired"), (Theirs, 90, "other"), (Theirs, 1, "other, expired"),
        ];
        string[] heres = ["no file", "unchanged", "changed", "changed, older than the record", "new", "unknown"];
        string[] records = ["none", "live", "tombstone", "old tombstone", "ignored"];
        string[] theres = ["no blob", "as the record has it", "listed otherwise"];
        var differ = new List<string>();
        int cells = 0;
        foreach (var (path, retentionDays, kind) in kinds)
        {
            foreach (string here in heres)
            {
                foreach (string record in records)
                {
                    foreach (string there in theres)
                    {
                        _files.Clear();
                        _unknown.Clear();
                        _remote.Clear();
                        foreach (string key in _state.Files.Keys.ToList())
                        {
                            _state.Remove(key);
                        }

                        switch (here)
                        {
                            case "unchanged": Here(path, LocalChange.Unchanged); break;
                            case "changed": Here(path, LocalChange.Changed); break;
                            case "changed, older than the record": Here(path, LocalChange.Changed, Now.AddDays(-500)); break;
                            case "new": Here(path, LocalChange.New); break;
                            case "unknown": _unknown.Add(path); break;
                        }

                        switch (record)
                        {
                            case "live": Synced(path); break;
                            case "tombstone": Synced(path, SyncedKind.Tombstone); break;
                            case "old tombstone": Synced(path, SyncedKind.Tombstone, at: Now.AddDays(-400)); break;
                            case "ignored": Synced(path, SyncedKind.Ignored); break;
                        }

                        if (there != "no blob")
                        {
                            There(path, changed: there == "listed otherwise");
                        }

                        var always = Plan(retentionDays);
                        var restoring = Plan(retentionDays, restore: true);

                        if (!always.Actions.SequenceEqual(restoring.Actions) || always.StillBad != restoring.StillBad || always.Unknown != restoring.Unknown)
                        {
                            differ.Add($"{kind} / {here} / {record} / {there}: {Kinds(always)} becomes {Kinds(restoring)}");
                        }

                        cells++;
                    }
                }
            }
        }

        // Two cells: a file that is gone here, and a file that was changed here, each with its
        // blob listed as the record has it. The second is in the list twice because the test
        // plans it with two times, the present one and one long before the record's: the
        // file's time makes no difference.
        Assert.Equal(
            [
                "shared / no file / live / as the record has it: UploadTombstone becomes Fetch",
                "shared / changed / live / as the record has it: Upload becomes Fetch",
                "shared / changed, older than the record / live / as the record has it: Upload becomes Fetch",
            ],
            differ);
        Assert.Equal(450, cells);

        static string Kinds(SyncPlan plan) => plan.Actions.Count == 0 ? "nothing" : string.Join("+", plan.Actions.Select(action => action.Kind));
    }

    [Fact]
    public void Restoring_does_not_touch_what_was_deleted_from_this_machines_own_clipboard_folder()
    {
        // Delete and Clear in the clipboard history are the owner's own doing: the day file is
        // still here, and the deletion of the content goes out in a restoring run as in any other.
        Synced(Mine);
        There(Mine);
        Here(MineDay, LocalChange.Changed);
        Synced(MineDay);
        There(MineDay);

        var action = Assert.Single(Plan(restore: true).Actions, action => action.Path == Mine);

        Assert.Equal(SyncActionKind.UploadTombstone, action.Kind);
    }

    // ---- The brake on mass deletion: what it counts ----

    [Fact]
    public void The_numbers_of_the_brake_are_twenty_five_a_quarter_and_two()
    {
        Assert.Equal(20, SyncLimits.MassDeletionCount);
        Assert.Equal(5, SyncLimits.MassDeletionFloor);
        Assert.Equal(4, SyncLimits.MassDeletionShare);
        Assert.Equal(2, SyncLimits.MassDeletionWholeLibrary);
    }

    // In every case here the files that are missing are the ones a deletion is planned for,
    // and some of the library is still here, so only the count and the share decide.
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 100, false)]
    [InlineData(4, 5, false)]           // fewer than five do not begin a pause by their share, though they are four of five
    [InlineData(4, 10, false)]
    [InlineData(5, 6, true)]
    [InlineData(5, 12, true)]
    [InlineData(5, 19, true)]
    [InlineData(5, 20, false)]          // a quarter exactly is not more than a quarter
    [InlineData(6, 24, false)]
    [InlineData(7, 24, true)]
    [InlineData(19, 76, false)]
    [InlineData(19, 75, true)]
    [InlineData(20, 80, false)]
    [InlineData(20, 79, true)]
    [InlineData(20, 200, false)]        // twenty is not more than twenty
    [InlineData(21, 200, true)]
    [InlineData(21, 1_000_000, true)]   // more than twenty are always held
    [InlineData(30, 100, true)]
    public void Deletions_are_held_when_they_are_more_than_twenty_or_at_least_five_and_more_than_a_quarter_of_the_live_library_files(int deletions, int live, bool held)
    {
        Assert.Equal(held, SyncPlanner.IsMassDeletion(deletions, live, missing: deletions));
    }

    [Theory]
    [InlineData(1, 1, 1, false)]        // one file is no library: deleting the only file is deleting a file
    [InlineData(2, 2, 2, true)]         // from two on, a library that is gone altogether is held
    [InlineData(3, 3, 3, true)]
    [InlineData(4, 4, 4, true)]
    [InlineData(1, 2, 1, false)]        // one of two is still here
    [InlineData(3, 4, 3, false)]        // one of four is still here
    [InlineData(4, 5, 4, false)]
    [InlineData(1, 2, 2, true)]         // both are gone, and a deletion is planned for one of them only (the other's blob changed)
    [InlineData(3, 4, 4, true)]
    [InlineData(0, 2, 2, false)]        // both are gone and no deletion is planned: there is nothing to hold
    [InlineData(0, 4, 4, false)]
    [InlineData(0, 0, 0, false)]
    public void Deletions_are_held_when_every_live_library_file_the_run_could_look_at_is_gone_and_there_are_at_least_two(int deletions, int live, int missing, bool held)
    {
        Assert.Equal(held, SyncPlanner.IsMassDeletion(deletions, live, missing));
    }

    [Fact]
    public void The_brake_counts_the_deletion_notes_for_library_files_and_nothing_else()
    {
        // Three library files that are gone here, with their blobs as the record has them.
        foreach (string gone in new[] { "library/a.md", "library/scripts/b.cmd", "library/snippets/c.md" })
        {
            Synced(gone);
            There(gone);
        }

        // Not counted: a deletion in this machine's own clipboard folder (the day file is here,
        // so the owner deleted it), a file in a clipboard folder that belongs to no day, ...
        Synced(Mine);
        There(Mine);
        Here(MineDay, LocalChange.New);
        Synced("clipboard/OTTER-PC/notes.txt");
        There("clipboard/OTTER-PC/notes.txt");

        // ... an old tombstone that is taken off the server, a path that is only forgotten, a
        // file gone here that was edited there (it is fetched), and whatever is uploaded.
        Synced("library/old.md", SyncedKind.Tombstone, at: Now.AddDays(-400));
        There("library/old.md");
        Synced("library/forgotten.md");
        Synced("library/edited-there.md");
        There("library/edited-there.md", changed: true);
        Here("library/new.md", LocalChange.New);

        var plan = Plan();

        Assert.Equal(5, plan.Actions.Count(action => action.Kind == SyncActionKind.UploadTombstone));
        Assert.Contains(plan.Actions, action => action.Kind == SyncActionKind.DeleteRemote);
        Assert.Contains(plan.Actions, action => action.Kind == SyncActionKind.Forget);
        Assert.Equal(3, SyncPlanner.SharedDeletions(plan));

        // A restoring run plans no deletion note for a library file at all.
        Assert.Equal(0, SyncPlanner.SharedDeletions(Plan(restore: true)));
    }

    [Fact]
    public void The_brake_holds_the_deletions_against_the_live_library_files_the_run_could_look_at()
    {
        Synced("library/a.md");
        Here("library/a.md", LocalChange.Unchanged);
        Synced("library/snippets/b.md");
        Here("library/snippets/b.md", LocalChange.Changed);
        Synced("readme.txt");                                       // outside the clipboard folders, so shared; and not here
        Synced("clipboard/note.txt");                               // directly under clipboard: in no machine's folder, so shared; and not here
        Synced("library/deleted.md", SyncedKind.Tombstone);         // not live
        Synced("library/behind-a-link.md", SyncedKind.Ignored);     // not live
        Synced(Mine);                                               // this machine's clipboard folder
        Synced(MineDay);
        Synced(Theirs);                                             // another machine's
        Here("library/new.md", LocalChange.New);                    // not in the record

        // Four live library files, all of which the run could look at; two of them are not here.
        var all = new LocalView(_files);
        Assert.Equal(4, SyncPlanner.LiveShared(_state, all));
        Assert.Equal(2, SyncPlanner.MissingShared(_state, all));
        Assert.Equal(0, SyncPlanner.UnseenShared(_state, all));

        // Sixty more behind a folder that could not be listed, and one that could not be read.
        // They are not files the run saw to be here, so they are not what the deletions are a
        // share of; and they are not seen to be missing either.
        for (int number = 0; number < 60; number++)
        {
            Synced(FormattableString.Invariant($"library/locked/note-{number}.md"));
        }

        Synced("library/held.md");
        _unknownFolders.Add("library/locked");
        _unknown.Add("library/held.md");
        var some = new LocalView(_files, _unknown, _unknownFolders);

        Assert.Equal(4, SyncPlanner.LiveShared(_state, some));
        Assert.Equal(2, SyncPlanner.MissingShared(_state, some));
        Assert.Equal(61, SyncPlanner.UnseenShared(_state, some));
        Assert.Equal(65, SyncPlanner.LiveShared(_state, all));
        Assert.Equal(0, SyncPlanner.LiveShared(new SyncState("scope"), some));
        Assert.Equal(0, SyncPlanner.MissingShared(new SyncState("scope"), some));
    }

    [Fact]
    public void The_live_library_files_a_run_could_not_look_at_are_counted_so_that_such_a_run_does_not_end_a_pause()
    {
        Synced("library/a.md");
        Here("library/a.md", LocalChange.Unchanged);
        Synced("library/gone.md");                                  // missing, and seen to be missing
        Synced("library/locked/b.md");                              // behind a folder that could not be listed
        Synced("library/locked/deeper/c.md");
        Synced("library/too-large.md");                             // a file that could not be looked at
        Synced("library/locked/deleted.md", SyncedKind.Tombstone);  // not live
        Synced("library/locked/ignored.md", SyncedKind.Ignored);    // not live
        Synced(Mine);                                               // a clipboard folder, which is not the library
        _unknownFolders.Add("library/locked");
        _unknown.Add("library/too-large.md");
        _unknown.Add(Mine);

        var view = new LocalView(_files, _unknown, _unknownFolders);

        Assert.Equal(3, SyncPlanner.UnseenShared(_state, view));
        Assert.Equal(0, SyncPlanner.UnseenShared(_state, new LocalView(_files)));
        Assert.Equal(0, SyncPlanner.UnseenShared(new SyncState("scope"), view));
    }
}
