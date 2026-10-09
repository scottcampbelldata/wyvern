using System.Text;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Platform;
using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>
/// Whole sync runs: two made-up machines, each with a throwaway data folder, and one server
/// that lives in memory. Nothing here opens a connection. Time is a clock the test moves.
/// </summary>
public sealed class SyncRunTests : IDisposable
{
    private const string Sig = "library/snippets/sig.md";
    private const string Links = "library/quicklinks.json";

    private static readonly byte[] Master = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new() { UtcNow = Start };
    private readonly MemoryRemoteStore _server = new() { Clock = Start };
    private readonly Machine _otter;
    private readonly Machine _seal;

    public SyncRunTests()
    {
        _otter = new Machine("OTTER-PC", this);
        _seal = new Machine("SEAL-PC", this);
    }

    public void Dispose()
    {
        _otter.Dispose();
        _seal.Dispose();
    }

    // One of the owner's two machines: a data folder, a record of its last sync and a log.
    private sealed class Machine(string device, SyncRunTests test) : IDisposable
    {
        public string Device { get; } = device;

        public SyncSandbox Box { get; } = new();

        public List<string> Log { get; } = [];

        public int RetentionDays { get; set; } = 90;

        public Func<bool> Cancelled { get; set; } = () => false;

        /// <summary>This machine's own clock, for a test in which it is wrong. Otherwise the clock all machines share.</summary>
        public IClock? Clock { get; set; }

        /// <summary>The names of the clipboard folders this machine writes. Its own name unless a test says otherwise.</summary>
        public List<string> Devices { get; } = [device];

        public string StateFile => Box.RootFile("sync-state.json");

        public SyncState State() => SyncState.Load(StateFile, "scope-" + Device, out _, out _);

        /// <summary>The keys of the run that is under way or was the last, for a test that wipes them under a run.</summary>
        public SyncKeys? Keys { get; private set; }

        /// <summary>
        /// What this machine's last run asked of the server, in order and without the blobs'
        /// names: "list", "now", "download", "upload", "delete". A call that failed is in it
        /// too, with how it failed, as in "upload (Folder)": the server's own list of calls
        /// leaves those out.
        /// </summary>
        public List<string> Asked { get; } = [];

        /// <param name="confirm">The one run of Confirm Sync Deletions: the brake on mass deletion does not hold it.</param>
        /// <param name="restore">The one run of Restore Missing Files.</param>
        /// <param name="paused">Sync is paused, as the service would tell the run from its mark on the disk.</param>
        public SyncResult Sync(IRemoteStore? store = null, bool confirm = false, bool restore = false, bool paused = false)
        {
            using var keys = SyncKeys.FromMaster(Master);
            Keys = keys;
            Asked.Clear();
            return SyncRun.Execute(new SyncRunContext(
                Box.Data,
                StateFile,
                State(),
                new Asking(store ?? test._server, Asked),
                keys,
                Device,
                name => Devices.Contains(name, StringComparer.OrdinalIgnoreCase),
                RetentionDays,
                Clock ?? test._clock,
                (text, _) => Log.Add(text),
                () => Cancelled(),
                _ => { },
                AllowDeletions: confirm,
                RestoreMissing: restore,
                Paused: paused));
        }

        public void Dispose() => Box.Dispose();
    }

    // Stands between a run and the server and writes down what the run asks for.
    private sealed class Asking(IRemoteStore store, List<string> asked) : IRemoteStore
    {
        public IReadOnlyList<RemoteBlob> List() => Ask("list", store.List);

        public byte[] Download(string name, long maxBytes) => Ask("download", () => store.Download(name, maxBytes));

        public RemoteBlob Upload(string name, byte[] content) => Ask("upload", () => store.Upload(name, content));

        public void Delete(string name) => Ask("delete", () =>
        {
            store.Delete(name);
            return true;
        });

        public DateTimeOffset Now() => Ask("now", store.Now);

        public void Dispose()
        {
        }

        private T Ask<T>(string call, Func<T> work)
        {
            int at = asked.Count;
            asked.Add(call);
            try
            {
                return work();
            }
            catch (RemoteStoreException ex)
            {
                asked[at] = $"{call} ({ex.Failure})";
                throw;
            }
        }
    }

    private static string BlobOf(string path)
    {
        using var keys = SyncKeys.FromMaster(Master);
        return keys.BlobName(SyncPath.Key(path));
    }

    // Time passes on both machines and on the server.
    private void Pass(TimeSpan time)
    {
        _clock.Advance(time);
        _server.Clock += time;
    }

    private static void AssertClean(SyncResult result)
    {
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.True(result.Clean, $"left for later {result.LeftForLater}, bad blobs {result.BadBlobs}");
    }

    private void BothSynced(string path, string text)
    {
        _otter.Box.Write(path, text, Start.AddHours(-2));
        AssertClean(_otter.Sync());
        AssertClean(_seal.Sync());
        Assert.Equal(text, _seal.Box.Read(path));
    }

    // ---- The ordinary course ----

    [Fact]
    public void The_first_run_uploads_every_file_under_names_and_bytes_that_say_nothing()
    {
        // Every word that is looked for is long enough that bytes that are as good as random
        // cannot hold it by chance: six letters and more, in a few hundred bytes.
        const string note = "library/snippets/signature.md";
        _otter.Box.Write(note, "walrus signature", Start.AddHours(-2));
        _otter.Box.Write(Links, "[ \"walrus link\" ]", Start.AddHours(-3));

        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(new[] { BlobOf(Links), BlobOf(note) }.Order(), _server.Names.Order());
        Assert.All(_server.Names, name => Assert.True(BlobCodec.IsBlobName(name)));
        string everything = string.Concat(_server.Names.Select(name => name + Encoding.Latin1.GetString(_server.Get(name)!)));
        Assert.DoesNotContain("walrus", everything);
        Assert.DoesNotContain("signature", everything);
        Assert.DoesNotContain("snippets", everything);
        Assert.DoesNotContain("quicklinks", everything);
        Assert.DoesNotContain("library", everything);
    }

    [Fact]
    public void A_run_with_nothing_to_do_only_lists_the_server()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        _server.Calls.Clear();

        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(0, result.Uploaded + result.Downloaded);
    }

    [Fact]
    public void The_other_machine_gets_the_file_with_its_content_and_its_time()
    {
        var written = Start.AddHours(-2).AddTicks(1234567);
        _otter.Box.Write(Sig, "walrus", written);
        _otter.Sync();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("walrus", _seal.Box.Read(Sig));
        Assert.Equal(written, _seal.Box.Mtime(Sig));
        Assert.False(result.ClipboardChanged);
        Assert.Equal(["list"], Calls(() => _seal.Sync()));
    }

    [Fact]
    public void An_edit_goes_across_and_nothing_bounces_back()
    {
        BothSynced(Sig, "first");
        _seal.Box.Write(Sig, "second, longer", Start.AddHours(-1));

        AssertClean(_seal.Sync());
        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("second, longer", _otter.Box.Read(Sig));
        Assert.Equal(["list"], Calls(() => _seal.Sync()));
        Assert.Equal(["list"], Calls(() => _otter.Sync()));
        Assert.Equal([Sig], _otter.Box.Files());
    }

    [Fact]
    public void A_delete_goes_across_as_a_tombstone_and_the_content_leaves_the_server()
    {
        BothSynced(Sig, new string('w', 5000));
        int before = _server.Get(BlobOf(Sig))!.Length;
        _otter.Box.Delete(Sig);

        var sent = _otter.Sync();
        var taken = _seal.Sync();

        AssertClean(sent);
        Assert.Equal(1, sent.TombstonesSent);
        Assert.True(before > 5000);
        Assert.True(_server.Get(BlobOf(Sig))!.Length < 300);
        AssertClean(taken);
        Assert.Equal(1, taken.DeletedHere);
        Assert.False(_seal.Box.Exists(Sig));
        Assert.Equal(SyncedKind.Tombstone, _seal.State().Find(Sig)!.Kind);
        Assert.Equal(["list"], Calls(() => _otter.Sync()));
        Assert.Equal(["list"], Calls(() => _seal.Sync()));
    }

    [Fact]
    public void A_file_written_again_with_the_same_content_is_not_sent_again()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-1));

        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
        Assert.Equal(Start.AddHours(-1), _otter.State().Find(Sig)!.MtimeUtc);
    }

    [Fact]
    public void A_file_renamed_only_in_its_case_is_the_same_file()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        File.Move(_otter.Box.Full(Sig), _otter.Box.Full("library/snippets/SIG.md"));

        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
    }

    // ---- Edited on both sides ----

    [Fact]
    public void Edited_on_both_sides_the_newer_one_wins_and_the_other_is_kept_as_a_conflict_copy()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's edit", Start.AddMinutes(-30));
        _seal.Box.Write(Sig, "the seal's later edit", Start.AddMinutes(-10));
        _otter.Sync();

        // The seal's is newer: it stays, the otter's version is saved beside it, and it is sent.
        var onSeal = _seal.Sync();

        AssertClean(onSeal);
        Assert.Equal(1, onSeal.ConflictCopies);
        Assert.Equal("the seal's later edit", _seal.Box.Read(Sig));
        Assert.Equal("the otter's edit", _seal.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));

        // The otter's was older: the seal's replaces it there, and no second copy is made.
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_seal.Sync());
        var onOtter = _otter.Sync();

        AssertClean(onOtter);
        Assert.Equal(0, onOtter.ConflictCopies);
        Assert.Equal("the seal's later edit", _otter.Box.Read(Sig));
        Assert.Equal("the otter's edit", _otter.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
        Assert.Equal(_seal.Box.Files(), _otter.Box.Files());
    }

    [Fact]
    public void Edited_on_both_sides_and_the_server_holds_the_newer_one_the_file_here_is_renamed_and_the_newer_one_written()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(Sig, "the seal's edit", Start.AddMinutes(-30));
        _otter.Sync();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("the otter's later edit", _seal.Box.Read(Sig));
        Assert.Equal("the seal's edit", _seal.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
        Assert.Equal(Start.AddMinutes(-30), _seal.Box.Mtime("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
    }

    [Fact]
    public void A_conflict_copy_never_takes_a_name_that_is_in_use()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(Sig, "the seal's edit", Start.AddMinutes(-30));
        _seal.Box.Write("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md", "an earlier copy");
        _otter.Sync();

        _seal.Sync();

        Assert.Equal("an earlier copy", _seal.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
        Assert.Equal("the seal's edit", _seal.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000-2.md"));
    }

    [Fact]
    public void A_conflict_on_a_path_that_no_copy_name_fits_is_left_for_later_with_nothing_overwritten()
    {
        // 370 characters of folders: a copy's own part is 36, so no copy of this file is a path of 400 or less.
        string path = string.Concat(Enumerable.Repeat("abcdefghi/", 37)) + "a.md";
        BothSynced(path, "first");
        _otter.Box.Write(path, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(path, "the seal's edit", Start.AddMinutes(-30));
        _otter.Sync();

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal(0, result.ConflictCopies + result.Downloaded + result.Uploaded);
        Assert.Equal("the seal's edit", _seal.Box.Read(path));
        Assert.Equal([path], _seal.Box.Files());
    }

    // ---- A conflict copy is made once ----

    private const string Script = "library/scripts/run.bat";

    private static RemoteStoreException? RefuseUploads(string call) =>
        call.StartsWith("upload", StringComparison.Ordinal) ? new RemoteStoreException(RemoteFailure.Folder, "SftpPermissionDeniedException") : null;

    // The conflict copies in a machine's data folder, as paths.
    private static List<string> Copies(Machine machine) =>
        [.. machine.Box.Files().Where(file => SyncPath.IsConflictCopy(Path.GetFileName(file)))];

    // Both machines hold the file and each has edited it; the otter's edit is on the server and
    // the seal's is the newer one, so on the seal the file here wins and the server's is kept as a copy.
    private void TheSealsEditIsNewerThanTheOneOnTheServer()
    {
        BothSynced(Script, "first");
        _otter.Box.Write(Script, "the otter's edit", Start.AddMinutes(-30));
        _seal.Box.Write(Script, "the seal's later edit", Start.AddMinutes(-10));
        _otter.Sync();
    }

    [Fact]
    public void With_uploads_refused_six_runs_leave_one_conflict_copy_and_not_six()
    {
        TheSealsEditIsNewerThanTheOneOnTheServer();
        _server.Fault = RefuseUploads;

        for (int run = 1; run <= 6; run++)
        {
            var result = _seal.Sync();

            Assert.Equal(SyncEnd.Completed, result.End);
            Assert.False(result.Clean);
            Assert.Equal(0, result.Uploaded);

            // The copy is made, and counted, by the first run only.
            Assert.Equal(run == 1 ? 1 : 0, result.ConflictCopies);
            Assert.Single(Copies(_seal));

            // The first run's plan holds the download alone, so the time is asked before the
            // upload it leads to. From the second run on the copy is a new file to send: the
            // time is asked up front, and both uploads are refused.
            string[] asked = run == 1
                ? ["list", "download", "now", "upload (Folder)"]
                : ["list", "now", "download", "upload (Folder)", "upload (Folder)"];
            Assert.Equal(asked, _seal.Asked);
            Pass(TimeSpan.FromMinutes(5));
        }

        string copy = Assert.Single(Copies(_seal));
        Assert.Equal("library/scripts/run.conflict-SEAL-PC-20261003-120000.bat", copy);
        Assert.Equal("the otter's edit", _seal.Box.Read(copy));
        Assert.Equal("the seal's later edit", _seal.Box.Read(Script));

        // Once the server takes uploads again, the file and its one copy go up and across.
        _server.Fault = null;
        var sent = _seal.Sync();
        AssertClean(sent);
        Assert.Equal(2, sent.Uploaded);
        Assert.Equal(0, sent.ConflictCopies);
        Assert.Equal(["list", "now", "download", "upload", "upload"], _seal.Asked);
        AssertClean(_otter.Sync());
        Assert.Equal([Script, copy], _otter.Box.Files());
        Assert.Equal("the seal's later edit", _otter.Box.Read(Script));
        Assert.Equal("the otter's edit", _otter.Box.Read(copy));
    }

    [Fact]
    public void A_run_cut_off_between_the_conflict_copy_and_the_upload_leaves_one_copy_after_the_next_run()
    {
        TheSealsEditIsNewerThanTheOneOnTheServer();
        _server.Fault = call => call.StartsWith("upload", StringComparison.Ordinal) ? new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10054") : null;
        _server.Calls.Clear();

        var cut = _seal.Sync();

        Assert.Equal(SyncEnd.Failed, cut.End);
        Assert.Equal(1, cut.ConflictCopies);
        Assert.Equal(["list", "download " + BlobOf(Script), "now"], _server.Calls);
        Assert.Equal(["list", "download", "now", "upload (Unreachable)"], _seal.Asked);
        string copy = Assert.Single(Copies(_seal));

        Pass(TimeSpan.FromMinutes(5));
        _server.Fault = null;
        _server.Calls.Clear();
        var next = _seal.Sync();

        AssertClean(next);
        Assert.Equal(0, next.ConflictCopies);
        Assert.Equal(2, next.Uploaded);
        Assert.Equal([copy], Copies(_seal));
        Assert.Equal("the otter's edit", _seal.Box.Read(copy));
        Assert.Equal("the seal's later edit", _seal.Box.Read(Script));
        Assert.Equal(
            ["list", "now", "download " + BlobOf(Script), "upload " + BlobOf(Script), "upload " + BlobOf(copy)],
            _server.Calls);
    }

    [Fact]
    public void Two_different_versions_from_the_server_are_each_kept_as_a_copy()
    {
        TheSealsEditIsNewerThanTheOneOnTheServer();
        _server.Fault = RefuseUploads;
        _seal.Sync();
        Assert.Equal(["list", "download", "now", "upload (Folder)"], _seal.Asked);

        // The otter sends another version, still older than the seal's. It is not the one the
        // seal has a copy of, so it is kept as well.
        _server.Fault = null;
        _otter.Box.Write(Script, "the otter's second edit", Start.AddMinutes(-20));
        _otter.Sync();
        Pass(TimeSpan.FromMinutes(5));
        _server.Fault = RefuseUploads;

        var result = _seal.Sync();

        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(["list", "now", "download", "upload (Folder)", "upload (Folder)"], _seal.Asked);
        Assert.Equal(
            ["library/scripts/run.conflict-SEAL-PC-20261003-120000.bat", "library/scripts/run.conflict-SEAL-PC-20261003-120500.bat"],
            Copies(_seal));
        Assert.Equal("the otter's edit", _seal.Box.Read("library/scripts/run.conflict-SEAL-PC-20261003-120000.bat"));
        Assert.Equal("the otter's second edit", _seal.Box.Read("library/scripts/run.conflict-SEAL-PC-20261003-120500.bat"));
        Assert.Equal("the seal's later edit", _seal.Box.Read(Script));
    }

    [Fact]
    public void A_copy_made_on_another_machine_or_at_another_time_counts_and_a_file_that_is_not_a_copy_of_this_one_does_not()
    {
        TheSealsEditIsNewerThanTheOneOnTheServer();

        // The same bytes under the name of a copy of another file, and under a name that is no copy's.
        _seal.Box.Write("library/scripts/run2.conflict-SEAL-PC-20260101-000000.bat", "the otter's edit");
        _seal.Box.Write("library/scripts/run.bat.bak", "the otter's edit");
        _seal.Box.Write("library/run.conflict-SEAL-PC-20260101-000000.bat", "the otter's edit");
        var first = _seal.Sync();

        // The three other files are new and are sent as files, one of them before the script
        // in the order of the paths; the script's own upload follows its download.
        Assert.Equal(1, first.ConflictCopies);
        Assert.Equal(["list", "now", "upload", "download", "upload", "upload", "upload"], _seal.Asked);
        Assert.Equal(4, _seal.Box.Files().Count(file => _seal.Box.Read(file) == "the otter's edit"));
        File.Delete(_seal.Box.Full("library/scripts/run.conflict-SEAL-PC-20261003-120000.bat"));

        // A copy of this file that the otter made months ago, numbered because the name was
        // taken, holding these very bytes: it is the copy. (It is also a file like any other,
        // and is sent as one.)
        TheServerHoldsTheOttersEditAgainAndTheSealsIsNewer();
        _seal.Box.Write("library/scripts/run.conflict-OTTER-PC-20260415-093000-2.bat", "the otter's edit");
        var second = _seal.Sync();

        AssertClean(second);
        Assert.Equal(0, second.ConflictCopies);
        Assert.Equal(2, second.Uploaded);
        Assert.Equal(["list", "now", "download", "upload", "upload"], _seal.Asked);
        Assert.DoesNotContain(Copies(_seal), copy => copy.Contains("SEAL-PC-2026100", StringComparison.Ordinal));
        Assert.Equal("the seal's still later edit", _seal.Box.Read(Script));

        void TheServerHoldsTheOttersEditAgainAndTheSealsIsNewer()
        {
            _otter.Sync();
            _otter.Box.Write(Script, "the otter's edit", Start.AddMinutes(-30));
            Pass(TimeSpan.FromMinutes(5));
            _otter.Sync();
            _seal.Box.Write(Script, "the seal's still later edit", Start.AddMinutes(-2));
        }
    }

    [Theory]
    [InlineData("library/scripts/Makefile")]                                        // no extension
    [InlineData("library/scripts/run.an-extension-too-long-to-be-one")]             // the copy keeps no extension
    [InlineData("library/scripts/run.conflict-OTTER-PC-20260101-000000.bat")]       // itself a conflict copy
    [InlineData("library/scripts/{150}.bat")]                                       // the copy's stem is cut to 120
    [InlineData("library/scripts/run.tar.gz")]
    public void The_copy_is_found_again_whatever_shape_the_files_name_has(string shape)
    {
        string path = shape.Replace("{150}", new string('n', 150));
        BothSynced(path, "first");
        _otter.Box.Write(path, "the otter's edit", Start.AddMinutes(-30));
        _seal.Box.Write(path, "the seal's later edit", Start.AddMinutes(-10));
        _otter.Sync();
        _server.Fault = RefuseUploads;

        Assert.Equal(1, _seal.Sync().ConflictCopies);
        Assert.Equal(["list", "download", "now", "upload (Folder)"], _seal.Asked);
        for (int run = 2; run <= 3; run++)
        {
            Pass(TimeSpan.FromMinutes(5));
            Assert.Equal(0, _seal.Sync().ConflictCopies);

            // The file's download and upload, and the upload of its copy as a new file, in
            // the order of the two paths, which depends on the shape of the name.
            Assert.Equal(["list", "now"], _seal.Asked.Take(2));
            Assert.Equal(["download", "upload (Folder)", "upload (Folder)"], _seal.Asked.Skip(2).Order(StringComparer.Ordinal));
        }

        // The file and its one copy.
        Assert.Equal(2, _seal.Box.Files().Count);
        Assert.Equal(["the otter's edit", "the seal's later edit"], _seal.Box.Files().Select(file => _seal.Box.Read(file)).Order());
    }

    [Fact]
    public void The_file_here_is_not_kept_twice_when_the_server_wins_and_a_copy_of_it_is_already_there()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(Sig, "the seal's edit", Start.AddMinutes(-30));
        _otter.Sync();
        Assert.Equal(1, _seal.Sync().ConflictCopies);
        Assert.Equal(["list", "download"], _seal.Asked);
        const string copy = "library/snippets/sig.conflict-SEAL-PC-20261003-120000.md";

        // The owner puts the seal's version back in place of the winner, and the otter edits again.
        _seal.Box.Write(Sig, "the seal's edit", Start.AddMinutes(-8));
        _otter.Box.Write(Sig, "the otter's third edit", Start.AddMinutes(-5));
        Pass(TimeSpan.FromMinutes(5));
        _otter.Sync();

        var result = _seal.Sync();

        // The server's is newer and is written here. What it replaces is already kept, in the
        // copy that holds the same bytes, so no second copy is made. (The copy itself is a
        // new file, and is sent as one before the download, in the order of the paths.)
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(["list", "now", "upload", "download"], _seal.Asked);
        Assert.Equal("the otter's third edit", _seal.Box.Read(Sig));
        Assert.Equal([copy], Copies(_seal));
        Assert.Equal("the seal's edit", _seal.Box.Read(copy));
    }

    // ---- A copy that is there counts as the copy only when this run leaves it alone ----

    // The files in a machine's data folder that hold exactly this text.
    private static List<string> Holding(Machine machine, string text) =>
        [.. machine.Box.Files().Where(file => machine.Box.Read(file) == text)];

    [Theory]
    [InlineData("library/scripts/run.bat")]     // sorts before its copies, so the file is dealt with first
    [InlineData("library/scripts/Makefile")]    // the same, with no extension
    [InlineData("library/snippets/sig.md")]     // sorts after its copies, so the copy is dealt with first
    public void When_the_server_wins_a_copy_that_this_run_is_about_to_delete_is_not_taken_for_the_copy_of_the_file_here(string path)
    {
        // An earlier conflict left a copy of the seal's version on both machines.
        BothSynced(path, "first");
        _otter.Box.Write(path, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(path, "the seal's edit", Start.AddMinutes(-30));
        _otter.Sync();
        Assert.Equal(1, _seal.Sync().ConflictCopies);
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_seal.Sync());
        AssertClean(_otter.Sync());
        string copy = Assert.Single(Copies(_seal));
        Assert.Equal("the seal's edit", _otter.Box.Read(copy));

        // On the otter the owner clears the old copy away and edits the file again.
        _otter.Box.Delete(copy);
        _otter.Box.Write(path, "the otter's third edit", Start.AddMinutes(-2));
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_otter.Sync());

        // On the seal, which has not synced since, he puts his own version back in the file:
        // the very bytes of the copy, and older than the otter's third edit.
        _seal.Box.Write(path, "the seal's edit", Start.AddMinutes(-8));

        var result = _seal.Sync();

        // The server's version is written over the file, and the old copy goes on the otter's
        // tombstone. The old copy is therefore not what keeps the seal's version: a new one is.
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.DeletedHere);
        Assert.Equal("the otter's third edit", _seal.Box.Read(path));
        Assert.False(_seal.Box.Exists(copy));
        string kept = Assert.Single(Holding(_seal, "the seal's edit"));
        Assert.Equal([kept], Copies(_seal));

        // And the version that lost reaches the other machine too.
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_seal.Sync());
        AssertClean(_otter.Sync());
        Assert.Equal([kept], Holding(_otter, "the seal's edit"));
        Assert.Equal(_seal.Box.Files(), _otter.Box.Files());
    }

    [Theory]
    [InlineData("library/scripts/run.bat")]
    [InlineData("library/scripts/Makefile")]
    [InlineData("library/snippets/sig.md")]
    public void When_the_file_here_wins_a_copy_that_this_run_is_about_to_delete_is_not_taken_for_the_copy_of_the_servers_version(string path)
    {
        // Both edited the file; the otter's edit is on the server and the seal's is the newer.
        BothSynced(path, "first");
        _otter.Box.Write(path, "the otter's edit", Start.AddMinutes(-30));
        _seal.Box.Write(path, "the seal's later edit", Start.AddMinutes(-10));
        _otter.Sync();

        // The seal keeps the server's version as a copy, and the server refuses every upload.
        _server.Fault = RefuseUploads;
        Assert.Equal(1, _seal.Sync().ConflictCopies);
        string copy = Assert.Single(Copies(_seal));

        // At the next run the copy goes up, and the file's own upload is still refused.
        Pass(TimeSpan.FromMinutes(5));
        _server.Fault = call => call == "upload " + BlobOf(path) ? new RemoteStoreException(RemoteFailure.Folder, "SftpPermissionDeniedException") : null;
        var second = _seal.Sync();
        Assert.Equal(0, second.ConflictCopies);
        Assert.Equal(1, second.Uploaded);
        _server.Fault = null;

        // The otter takes the copy, which holds what its own file holds, and the owner deletes it there.
        AssertClean(_otter.Sync());
        Assert.Equal("the otter's edit", _otter.Box.Read(copy));
        Assert.Equal("the otter's edit", _otter.Box.Read(path));
        _otter.Box.Delete(copy);
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_otter.Sync());

        var result = _seal.Sync();

        // The seal's file goes up over the otter's version, and the old copy goes on the
        // otter's tombstone. The old copy is therefore not what keeps the otter's version.
        AssertClean(result);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.DeletedHere);
        Assert.False(_seal.Box.Exists(copy));
        Assert.Equal("the seal's later edit", _seal.Box.Read(path));
        string kept = Assert.Single(Holding(_seal, "the otter's edit"));
        Assert.Equal([kept], Copies(_seal));

        // Once both have synced, the otter's version is still on both machines.
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_seal.Sync());
        AssertClean(_otter.Sync());
        Assert.Equal("the seal's later edit", _otter.Box.Read(path));
        Assert.Equal([kept], Holding(_otter, "the otter's edit"));
        Assert.Equal(_seal.Box.Files(), _otter.Box.Files());
    }

    [Fact]
    public void When_the_server_wins_a_file_with_a_copys_name_and_size_and_other_bytes_is_not_taken_for_the_copy()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(Sig, "the seal's edit", Start.AddMinutes(-30));
        _otter.Sync();

        // A conflict copy of this file from another time, as long as the seal's edit to the byte, and not it.
        const string other = "library/snippets/sig.conflict-OTTER-PC-20260415-093000.md";
        _seal.Box.Write(other, "THE SEAL'S EDIT");
        Assert.Equal("the seal's edit".Length, "THE SEAL'S EDIT".Length);

        var result = _seal.Sync();

        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal("the otter's later edit", _seal.Box.Read(Sig));
        Assert.Equal("THE SEAL'S EDIT", _seal.Box.Read(other));
        Assert.Equal("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md", Assert.Single(Holding(_seal, "the seal's edit")));
    }

    [Fact]
    public void When_the_file_here_wins_a_file_with_a_copys_name_and_size_and_other_bytes_is_not_taken_for_the_copy()
    {
        TheSealsEditIsNewerThanTheOneOnTheServer();

        // A conflict copy of this file from another time, as long as the otter's edit to the byte, and not it.
        const string other = "library/scripts/run.conflict-OTTER-PC-20260415-093000.bat";
        _seal.Box.Write(other, "THE OTTER'S EDIT");
        Assert.Equal("the otter's edit".Length, "THE OTTER'S EDIT".Length);

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal("the seal's later edit", _seal.Box.Read(Script));
        Assert.Equal("THE OTTER'S EDIT", _seal.Box.Read(other));
        Assert.Equal("library/scripts/run.conflict-SEAL-PC-20261003-120000.bat", Assert.Single(Holding(_seal, "the otter's edit")));
    }

    [Fact]
    public void The_same_edit_on_both_sides_is_no_conflict()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the same edit", Start.AddMinutes(-30));
        _seal.Box.Write(Sig, "the same edit", Start.AddMinutes(-10));
        _otter.Sync();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(0, result.ConflictCopies + result.Uploaded + result.Downloaded);
        Assert.Equal([Sig], _seal.Box.Files());
    }

    [Fact]
    public void Deleted_here_and_edited_there_the_edit_wins_and_the_file_comes_back()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "edited", Start.AddMinutes(-10));
        _seal.Box.Delete(Sig);
        _otter.Sync();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal("edited", _seal.Box.Read(Sig));
        Assert.Equal(0, result.TombstonesSent);
    }

    [Fact]
    public void Edited_here_and_deleted_there_the_edit_wins_and_goes_back_to_the_server()
    {
        BothSynced(Sig, "first");
        _otter.Box.Delete(Sig);
        _seal.Box.Write(Sig, "edited", Start.AddMinutes(-10));
        _otter.Sync();

        var result = _seal.Sync();
        _otter.Sync();

        AssertClean(result);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal("edited", _seal.Box.Read(Sig));
        Assert.Equal("edited", _otter.Box.Read(Sig));
    }

    // ---- What the server holds is not trusted ----

    [Fact]
    public void A_blob_that_fails_authentication_is_skipped_named_in_the_log_and_never_written_to_disk()
    {
        BothSynced(Sig, "the true text");
        _otter.Box.Write(Sig, "a newer text", Start.AddMinutes(-10));
        _otter.Sync();
        byte[] tampered = _server.Get(BlobOf(Sig))!;
        tampered[^20] ^= 0x01;
        _server.Put(BlobOf(Sig), tampered);

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(1, result.BadBlobs);
        Assert.False(result.Clean);
        Assert.Equal("the true text", _seal.Box.Read(Sig));
        Assert.Equal([Sig], _seal.Box.Files());
        Assert.Contains($"A blob could not be used and was skipped (NotAuthentic): {BlobOf(Sig)}", _seal.Log);

        // The path is in this machine's record, and the blob is remembered beside it: a second
        // and a third run over the same listing download nothing and say nothing more about
        // it, and still count it, so that sync stays flagged.
        Assert.Equal(SyncedKind.Live, _seal.State().Find(Sig)!.Kind);
        Assert.Equal([BlobOf(Sig)], _seal.State().Bad.Keys);
        for (int run = 2; run <= 3; run++)
        {
            Pass(TimeSpan.FromMinutes(5));
            _server.Calls.Clear();
            var again = _seal.Sync();

            Assert.Equal(["list"], _server.Calls);
            Assert.Equal(SyncEnd.Completed, again.End);
            Assert.Equal(1, again.BadBlobs);
            Assert.False(again.Clean);
        }

        Assert.Single(_seal.Log, line => line.StartsWith("A blob could not be used", StringComparison.Ordinal));
        Assert.Equal("the true text", _seal.Box.Read(Sig));

        // The owner takes the bad file off the server and the machine that has the newer text
        // sends it again. The server now lists the blob differently, so it is fetched.
        _server.Remove(BlobOf(Sig));
        AssertClean(_otter.Sync());
        var healed = _seal.Sync();

        AssertClean(healed);
        Assert.Equal(1, healed.Downloaded);
        Assert.Equal("a newer text", _seal.Box.Read(Sig));
        Assert.Empty(_seal.State().Bad);
    }

    [Fact]
    public void A_file_changed_here_is_not_uploaded_over_a_blob_that_is_remembered_as_one_that_could_not_be_used()
    {
        BothSynced(Sig, "the true text");
        _otter.Box.Write(Sig, "a newer text", Start.AddMinutes(-10));
        _otter.Sync();
        byte[] tampered = _server.Get(BlobOf(Sig))!;
        tampered[^20] ^= 0x01;
        _server.Put(BlobOf(Sig), tampered);
        _seal.Sync();

        // The file is edited here. The blob may be a newer version that was damaged on the way,
        // so it is neither fetched again nor replaced.
        _seal.Box.Write(Sig, "the seal's own edit, made later", Start.AddMinutes(-5));
        _server.Calls.Clear();
        var result = _seal.Sync();

        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, result.BadBlobs);
        Assert.False(result.Clean);
        Assert.Equal(tampered, _server.Get(BlobOf(Sig)));
        Assert.Equal("the seal's own edit, made later", _seal.Box.Read(Sig));
        Assert.Single(_seal.Log, line => line.StartsWith("A blob could not be used", StringComparison.Ordinal));

        // Once the server lists the blob differently it is fetched, and the two edits are both kept.
        _server.Remove(BlobOf(Sig));
        AssertClean(_otter.Sync());
        var healed = _seal.Sync();

        AssertClean(healed);
        Assert.Equal(1, healed.ConflictCopies);
        Assert.Equal("the seal's own edit, made later", _seal.Box.Read(Sig));
        Assert.Equal("a newer text", _seal.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
        Assert.Empty(_seal.State().Bad);
    }

    // Both machines hold the true text. The otter then sends a newer text, and the blob is
    // damaged on the server before the seal has taken it: the seal remembers it as one that
    // could not be used, and still holds the true text, unchanged since its own last sync.
    private void TheSealRemembersTheBlobOfTheNewerTextAsUnusable()
    {
        BothSynced(Sig, "the true text");
        _otter.Box.Write(Sig, "a newer text", Start.AddMinutes(-10));
        _otter.Sync();
        byte[] tampered = _server.Get(BlobOf(Sig))!;
        tampered[^20] ^= 0x01;
        _server.Put(BlobOf(Sig), tampered);
        Assert.Equal(1, _seal.Sync().BadBlobs);
        Assert.Equal([BlobOf(Sig)], _seal.State().Bad.Keys);
    }

    [Fact]
    public void A_file_written_again_with_the_same_content_keeps_the_memory_that_its_blob_could_not_be_used()
    {
        TheSealRemembersTheBlobOfTheNewerTextAsUnusable();

        // Written again with the same content: the record takes the file's new time, and that
        // update of the path's entry must not drop what is remembered about its blob.
        _seal.Box.Write(Sig, "the true text", Start.AddMinutes(-5));
        _server.Calls.Clear();

        var result = _seal.Sync();

        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(1, result.BadBlobs);
        Assert.False(result.Clean);
        Assert.Equal(Start.AddMinutes(-5), _seal.State().Find(Sig)!.MtimeUtc);
        Assert.Equal([BlobOf(Sig)], _seal.State().Bad.Keys);

        // The run after that still asks for nothing and says nothing more.
        Assert.Equal(["list"], Calls(() => Assert.Equal(1, _seal.Sync().BadBlobs)));
        Assert.Single(_seal.Log, line => line.StartsWith("A blob could not be used", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]      // the machine that never got the newer text syncs first
    [InlineData(false)]     // the machine that wrote the newer text syncs first
    public void After_an_unusable_blob_is_taken_off_the_server_the_newer_text_is_on_both_machines_whichever_syncs_first(bool theSealFirst)
    {
        TheSealRemembersTheBlobOfTheNewerTextAsUnusable();

        // The owner releases the file the documented way: the blob is deleted on the server.
        _server.Remove(BlobOf(Sig));
        var (first, second) = theSealFirst ? (_seal, _otter) : (_otter, _seal);

        var sent = first.Sync();
        var taken = second.Sync();

        AssertClean(sent);
        Assert.Equal(1, sent.Uploaded);
        AssertClean(taken);
        Assert.Equal(1, taken.Downloaded);
        if (theSealFirst)
        {
            // The seal sent the older text. The otter's file is unchanged since its own last
            // sync, and newer than what the server now holds: it is not replaced unseen. It is
            // kept beside the file as a conflict copy, and only then is the server's taken.
            Assert.Equal(1, taken.ConflictCopies);
            Assert.Equal("the true text", _otter.Box.Read(Sig));
            Assert.Equal(["library/snippets/sig.conflict-OTTER-PC-20261003-120000.md"], Holding(_otter, "a newer text"));
        }
        else
        {
            // The otter sent the newer text, which the seal takes over its older file. No copy.
            Assert.Equal(0, taken.ConflictCopies);
            Assert.Equal("a newer text", _seal.Box.Read(Sig));
            Assert.Equal([Sig], _seal.Box.Files());
        }

        // Once everything has gone across, the newer text is on both machines.
        Pass(TimeSpan.FromMinutes(5));
        AssertClean(_otter.Sync());
        AssertClean(_seal.Sync());
        Assert.NotEmpty(Holding(_otter, "a newer text"));
        Assert.Equal(Holding(_otter, "a newer text"), Holding(_seal, "a newer text"));
        Assert.Equal(_otter.Box.Files(), _seal.Box.Files());
        Assert.Empty(_seal.State().Bad);
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
        Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
    }

    [Fact]
    public void An_unchanged_file_is_replaced_by_a_newer_edit_from_the_server_and_no_copy_is_made()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's edit, made later", Start.AddMinutes(-10));
        AssertClean(_otter.Sync());

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Equal(["list", "download"], _seal.Asked);
        Assert.Equal("the otter's edit, made later", _seal.Box.Read(Sig));
        Assert.Equal([Sig], _seal.Box.Files());
    }

    [Fact]
    public void An_unchanged_file_is_kept_as_a_conflict_copy_when_the_server_holds_other_content_that_is_older_than_it()
    {
        // The otter puts an old version of the file back, with the old time it had.
        BothSynced(Sig, "the text both have");
        _otter.Box.Write(Sig, "a version from last week", Start.AddDays(-7));
        AssertClean(_otter.Sync());

        var result = _seal.Sync();

        // On the seal the file is unchanged, and newer than what the server holds now. The
        // server's version is taken, as the other machine's change, and what was here is kept.
        AssertClean(result);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(["list", "download"], _seal.Asked);
        Assert.Equal("a version from last week", _seal.Box.Read(Sig));
        Assert.Equal("the text both have", _seal.Box.Read("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
        Assert.Equal(Start.AddHours(-2), _seal.Box.Mtime("library/snippets/sig.conflict-SEAL-PC-20261003-120000.md"));
    }

    [Fact]
    public void An_unchanged_file_is_not_copied_when_the_server_holds_the_same_content_under_an_older_time()
    {
        // The seal's file is written again with the same content, so it has a later time than
        // the otter's. The blob then goes from the server, and the otter sends the same
        // content again under its earlier time.
        BothSynced(Sig, "the text both have");
        _seal.Box.Write(Sig, "the text both have", Start.AddMinutes(-5));
        AssertClean(_seal.Sync());
        _server.Remove(BlobOf(Sig));
        AssertClean(_otter.Sync());

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(0, result.ConflictCopies);
        Assert.Equal("the text both have", _seal.Box.Read(Sig));
        Assert.Equal([Sig], _seal.Box.Files());
        Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
    }

    [Fact]
    public void A_remembered_unusable_blob_of_the_other_machines_day_is_forgotten_and_no_longer_counted_once_the_day_has_expired_here()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        _otter.Box.Write(day, "lines\n", Start.AddHours(-1));
        _otter.Sync();
        _seal.RetentionDays = 30;
        AssertClean(_seal.Sync());

        // The otter adds to the day, and what it sends is damaged on the server.
        _otter.Box.Write(day, "lines\nmore lines\n", Start.AddMinutes(-10));
        _otter.Sync();
        byte[] tampered = _server.Get(BlobOf(day))!;
        tampered[^20] ^= 0x01;
        _server.Put(BlobOf(day), tampered);

        // While the seal keeps the day, the blob that cannot be used is counted against sync.
        var bad = _seal.Sync();
        Assert.Equal(1, bad.BadBlobs);
        Assert.False(bad.Clean);
        Assert.Equal([BlobOf(day)], _seal.State().Bad.Keys);
        Assert.Equal("lines\n", _seal.Box.Read(day));

        // Thirty-one days on, the seal's retention has removed its copy, and the seal would
        // not keep that day whatever the blob held. The blob is remembered as one the seal does
        // not keep, and no longer as one it could not use.
        Pass(TimeSpan.FromDays(31));
        _seal.Box.Delete(day);
        _server.Calls.Clear();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(0, result.BadBlobs);
        Assert.Equal(["list"], _server.Calls);
        Assert.Empty(_seal.State().Bad);
        Assert.Equal(SyncedKind.Ignored, _seal.State().Find(SyncPath.Key(day))!.Kind);
        Assert.Equal(tampered, _server.Get(BlobOf(day)));
        Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
    }

    [Fact]
    public void A_file_in_this_machines_own_folder_is_not_uploaded_over_a_blob_that_could_not_be_used()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        byte[] unreadable = Encoding.UTF8.GetBytes("BCN1 and then nonsense that is long enough to be a blob at all");
        _server.Put(BlobOf(day), unreadable);
        _otter.Sync();
        _otter.Box.Write(day, "the otter's own lines\n", Start.AddHours(-1));
        _server.Calls.Clear();

        var result = _otter.Sync();

        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, result.BadBlobs);
        Assert.Equal(unreadable, _server.Get(BlobOf(day)));
        Assert.Equal("the otter's own lines\n", _otter.Box.Read(day));
    }

    [Fact]
    public void A_blob_that_could_not_be_used_is_not_downloaded_again_until_it_changes()
    {
        _server.Put(BlobOf(Sig), Encoding.UTF8.GetBytes("BCN1 and then nonsense that is long enough to be a blob at all"));
        _seal.Sync();
        _server.Calls.Clear();

        var again = _seal.Sync();

        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(1, again.BadBlobs);
        Assert.False(again.Clean);

        // The owner takes the bad file off the server, and the machine that has the file sends
        // it: the blob has changed, and is taken.
        _server.Remove(BlobOf(Sig));
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-1));
        _otter.Sync();
        var healed = _seal.Sync();

        AssertClean(healed);
        Assert.Equal("walrus", _seal.Box.Read(Sig));
        Assert.Empty(_seal.State().Bad);
    }

    [Fact]
    public void A_file_is_never_uploaded_over_a_blob_that_cannot_be_read()
    {
        // It may be a newer version of the file that was damaged on the way, so it is not replaced.
        byte[] unreadable = Encoding.UTF8.GetBytes("BCN1 and then nonsense that is long enough to be a blob at all");
        _server.Put(BlobOf(Sig), unreadable);
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-1));

        var result = _otter.Sync();

        Assert.Equal(1, result.BadBlobs);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(unreadable, _server.Get(BlobOf(Sig)));
        Assert.Equal("walrus", _otter.Box.Read(Sig));
    }

    [Fact]
    public void A_blob_made_with_another_key_writes_nothing_and_what_is_not_a_blob_is_left_alone()
    {
        using var other = SyncKeys.FromMaster([.. Enumerable.Repeat((byte)9, 32)]);
        string name = other.BlobName(SyncPath.Key(Sig));
        _server.Put(name, BlobCodec.Seal(other, name, new BlobHeader(Sig, Start, false, BlobCodec.Hash([1])), [1]));
        _server.Put("notes-of-the-server-owner.txt", [1, 2, 3]);
        _server.Put("../../outside", [1, 2, 3]);

        var result = _seal.Sync();

        Assert.Equal(1, result.BadBlobs);
        Assert.Empty(_seal.Box.Files());
        Assert.Equal(["../../outside", name, "notes-of-the-server-owner.txt"], _server.Names);
        Assert.Contains("The server's folder holds files that are not Beacon's, which are left alone: 2", _seal.Log);
        Assert.DoesNotContain(_seal.Log, line => line.Contains("notes") || line.Contains("outside"));
    }

    [Fact]
    public void A_blob_larger_than_any_blob_Beacon_makes_is_not_read_and_counts_as_one_that_cannot_be_used()
    {
        _server.Put(BlobOf(Sig), new byte[SyncLimits.MaxBlobBytes + 1]);

        var result = _seal.Sync();

        Assert.Equal(1, result.BadBlobs);
        Assert.Empty(_seal.Box.Files());
        Assert.Contains($"A blob could not be used and was skipped (too large): {BlobOf(Sig)}", _seal.Log);
    }

    [Fact]
    public void A_leftover_of_an_upload_is_removed_from_the_server_once_it_is_a_day_old()
    {
        string old = BlobCodec.TemporaryName(BlobOf(Sig));
        string young = BlobCodec.TemporaryName(BlobOf(Links));
        _server.Put(old, [1], Start.AddDays(-1).AddSeconds(-1));
        _server.Put(young, [1], Start.AddHours(-23));

        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal([young], _server.Names);
        Assert.Equal(1, result.RemovedFromServer);
    }

    // ---- A run that is cut off ----

    [Fact]
    public void A_run_that_loses_the_server_records_what_it_did_and_the_next_run_does_the_rest()
    {
        foreach (string name in new[] { "a", "b", "c", "d" })
        {
            _otter.Box.Write($"library/{name}.md", name, Start.AddHours(-2));
        }

        _server.Fault = call => call.StartsWith("upload", StringComparison.Ordinal) && _server.Count("upload") == 2
            ? new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060")
            : null;

        var cut = _otter.Sync();

        Assert.Equal(SyncEnd.Failed, cut.End);
        Assert.Equal(RemoteFailure.Unreachable, cut.Failure);
        Assert.Equal(2, cut.Uploaded);
        Assert.Equal(["library/a.md", "library/b.md"], _otter.State().Files.Keys.Order());
        Assert.Contains("Sync could not use the server (Unreachable, SocketException 10060)", _otter.Log);

        _server.Fault = null;
        _server.Calls.Clear();
        var resumed = _otter.Sync();

        AssertClean(resumed);
        Assert.Equal(2, resumed.Uploaded);
        Assert.Equal(["list", "now", "upload " + BlobOf("library/c.md"), "upload " + BlobOf("library/d.md")], _server.Calls);
    }

    [Fact]
    public void A_run_whose_record_was_never_written_is_made_good_by_comparing_content()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Box.Write(Links, "[]", Start.AddHours(-2));
        _otter.Sync();
        File.Delete(_otter.StateFile);

        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal(0, result.Uploaded + result.Downloaded + result.ConflictCopies);
        Assert.Equal(2, _otter.State().Files.Count);
        Assert.Equal([Links, Sig], _otter.Box.Files());
        Assert.Equal(["list"], Calls(() => _otter.Sync()));
    }

    [Fact]
    public void The_record_is_written_while_a_long_run_is_at_work()
    {
        foreach (string name in new[] { "a", "b", "c" })
        {
            _otter.Box.Write($"library/{name}.md", name, Start.AddHours(-2));
        }

        // Each upload takes twenty seconds. The record is written once thirty have gone by.
        var recorded = new List<int>();
        _server.Uploaded = _ => _clock.Advance(TimeSpan.FromSeconds(20));
        _server.Fault = call =>
        {
            if (call.StartsWith("upload", StringComparison.Ordinal))
            {
                recorded.Add(_otter.State().Files.Count);
            }

            return null;
        };

        _otter.Sync();

        // Before the first upload nothing, before the second still nothing (20 s), before the third two files (40 s).
        Assert.Equal([0, 0, 2], recorded);
    }

    [Fact]
    public void A_run_that_is_told_to_stop_stops_between_two_files_and_records_what_it_did()
    {
        _otter.Box.Write("library/a.md", "a", Start.AddHours(-2));
        _otter.Box.Write("library/b.md", "b", Start.AddHours(-2));
        _otter.Cancelled = () => _server.Count("upload") >= 1;

        var stopped = _otter.Sync();

        Assert.Equal(SyncEnd.Stopped, stopped.End);
        Assert.Equal(1, stopped.Uploaded);
        Assert.Single(_otter.State().Files);
        Assert.DoesNotContain(_otter.Log, line => line.StartsWith("Sync finished", StringComparison.Ordinal));
    }

    [Fact]
    public void A_connection_closed_under_a_run_that_was_told_to_stop_is_a_stop_and_not_a_failure()
    {
        _otter.Box.Write("library/a.md", "a", Start.AddHours(-2));
        bool closing = false;
        _otter.Cancelled = () => closing;
        _server.Fault = call =>
        {
            if (!call.StartsWith("upload", StringComparison.Ordinal))
            {
                return null;
            }

            closing = true;
            return new RemoteStoreException(RemoteFailure.Unreachable, "closed");
        };

        var result = _otter.Sync();

        Assert.Equal(SyncEnd.Stopped, result.End);
        Assert.Null(result.Failure);
        Assert.DoesNotContain(_otter.Log, line => line.StartsWith("Sync could not use the server", StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_that_refuses_one_file_is_asked_for_the_others_and_one_that_refuses_three_in_a_row_ends_the_run()
    {
        foreach (string name in new[] { "a", "b", "c", "d", "e" })
        {
            _otter.Box.Write($"library/{name}.md", name, Start.AddHours(-2));
        }

        _server.Fault = call => call == "upload " + BlobOf("library/b.md") ? new RemoteStoreException(RemoteFailure.Server, "SshException") : null;
        var one = _otter.Sync();

        Assert.Equal(SyncEnd.Completed, one.End);
        Assert.Equal(4, one.Uploaded);
        Assert.Equal(1, one.LeftForLater);
        Assert.False(one.Clean);

        _otter.Box.Write("library/a.md", "a2", Start.AddHours(-1));
        _otter.Box.Write("library/c.md", "c2", Start.AddHours(-1));
        _server.Fault = call => call.StartsWith("upload", StringComparison.Ordinal) ? new RemoteStoreException(RemoteFailure.Folder, "SftpPermissionDeniedException") : null;
        var all = _otter.Sync();

        Assert.Equal(SyncEnd.Failed, all.End);
        Assert.Equal(RemoteFailure.Folder, all.Failure);
        Assert.Equal(2, all.LeftForLater);
    }

    [Fact]
    public void A_record_that_cannot_be_written_is_said_and_the_run_is_not_called_clean()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        Directory.CreateDirectory(_otter.StateFile + ".tmp");

        var result = _otter.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.True(result.StateNotSaved);
        Assert.False(result.Clean);
        Assert.Contains("The record of the sync could not be written", _otter.Log);
    }

    // ---- What must never be taken for a deletion ----

    [Fact]
    public void A_data_folder_that_is_not_there_makes_the_run_do_nothing_at_all()
    {
        BothSynced(Sig, "walrus");
        Directory.Move(_otter.Box.Data, _otter.Box.Data + "-moved");
        _server.Calls.Clear();

        var result = _otter.Sync();

        Assert.Equal(SyncEnd.NoDataFolder, result.End);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(0, result.TombstonesSent);
        Assert.Single(_otter.State().Files);
        Directory.Move(_otter.Box.Data + "-moved", _otter.Box.Data);
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
    }

    [Fact]
    public void A_data_folder_that_goes_away_while_a_run_is_at_work_does_not_get_its_files_recorded_as_deleted()
    {
        BothSynced(Sig, "walrus");
        _otter.Box.Delete(Sig);
        AssertClean(_otter.Sync());

        // The seal's data folder is taken away after the run has looked at it, as when its
        // drive is unplugged. The run is then told to delete a file there on the otter's
        // tombstone, and Windows says "path not found" for it.
        string away = _seal.Box.Data + "-away";
        _server.Fault = call =>
        {
            if (call.StartsWith("download", StringComparison.Ordinal) && Directory.Exists(_seal.Box.Data))
            {
                Directory.Move(_seal.Box.Data, away);
            }

            return null;
        };
        SyncResult result;
        try
        {
            result = _seal.Sync();
        }
        finally
        {
            _server.Fault = null;
            if (Directory.Exists(away))
            {
                Directory.Move(away, _seal.Box.Data);
            }
        }

        // The file was not deleted, and is not recorded as deleted: it is left for later.
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(0, result.DeletedHere);
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal(SyncedKind.Live, _seal.State().Find(Sig)!.Kind);
        Assert.Equal("walrus", _seal.Box.Read(Sig));

        // With the folder back, the next run finds the file as it was and carries the deletion out.
        var next = _seal.Sync();

        AssertClean(next);
        Assert.Equal(1, next.DeletedHere);
        Assert.False(_seal.Box.Exists(Sig));
        Assert.Equal(SyncedKind.Tombstone, _seal.State().Find(Sig)!.Kind);
    }

    [Fact]
    public void A_folder_that_became_a_link_is_not_followed_and_its_files_are_not_reported_deleted()
    {
        _otter.Box.Write("library/scripts/deploy.ps1", "walrus", Start.AddHours(-2));
        _otter.Sync();
        string elsewhere = Path.Combine(_otter.Box.Root, "elsewhere");
        Directory.Move(_otter.Box.Full("library/scripts"), elsewhere);
        _otter.Box.Junction(_otter.Box.Full("library/scripts"), elsewhere);
        _server.Calls.Clear();

        var result = _otter.Sync();

        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(0, result.TombstonesSent);
        Assert.Equal(1, result.LeftForLater);
        Assert.False(result.Clean);
    }

    [Fact]
    public void A_file_that_grew_over_the_limit_is_not_synced_and_not_reported_deleted()
    {
        _otter.Box.Write("library/video.bin", "small", Start.AddHours(-2));
        _otter.Sync();
        using (var grown = new FileStream(_otter.Box.Full("library/video.bin"), FileMode.Open))
        {
            grown.SetLength(SyncLimits.MaxFileBytes + 1);
        }

        _server.Calls.Clear();
        var result = _otter.Sync();

        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(1, result.TooLarge);
        Assert.Equal(1, result.LeftForLater);
        Assert.Contains(_otter.Log, line => line.EndsWith("1 over the size limit and not synced", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_edited_while_the_run_was_at_work_is_not_overwritten()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "from the otter", Start.AddMinutes(-20));
        _otter.Sync();
        _server.Fault = call =>
        {
            if (call.StartsWith("download", StringComparison.Ordinal))
            {
                _seal.Box.Write(Sig, "typed on the seal this very moment", Start.AddMinutes(-1));
            }

            return null;
        };

        var result = _seal.Sync();

        Assert.Equal("typed on the seal this very moment", _seal.Box.Read(Sig));
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal([Sig], _seal.Box.Files());

        // The next run sees an edit on both sides and keeps both.
        _server.Fault = null;
        var next = _seal.Sync();
        Assert.Equal(1, next.ConflictCopies);
        Assert.Equal("typed on the seal this very moment", _seal.Box.Read(Sig));
    }

    [Fact]
    public void A_file_that_came_back_before_its_tombstone_was_sent_is_not_tombstoned()
    {
        _otter.Box.Write("library/a.md", "a", Start.AddHours(-2));
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        _otter.Box.Delete(Sig);
        _otter.Box.Write("library/a.md", "a2", Start.AddHours(-1));
        _server.Uploaded = _ => _otter.Box.Write(Sig, "back again", Start.AddMinutes(-1));

        var result = _otter.Sync();

        Assert.Equal(0, result.TombstonesSent);
        Assert.Equal(1, result.LeftForLater);
    }

    // ---- The clocks ----

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void A_machine_whose_clock_is_more_than_a_day_from_the_servers_writes_nothing_there(int days)
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _server.Clock = Start.AddDays(days);

        var result = _otter.Sync();

        Assert.Equal(SyncEnd.ClockWrong, result.End);
        Assert.Equal(["list", "now"], _server.Calls);
        Assert.Empty(_server.Names);
        Assert.Contains("Sync wrote nothing because this machine's clock and the server's are more than a day apart", _otter.Log);
    }

    [Fact]
    public void A_clock_that_is_hours_off_is_not_a_reason_to_stop()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _server.Clock = Start.AddHours(23);

        AssertClean(_otter.Sync());
    }

    [Fact]
    public void A_run_that_only_fetches_does_not_ask_the_server_the_time()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        _server.Calls.Clear();

        _seal.Sync();

        Assert.DoesNotContain("now", _server.Calls);
    }

    [Fact]
    public void A_run_that_only_downloads_asks_nothing_and_takes_the_files_even_when_the_clocks_disagree()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Box.Write(Links, "[]", Start.AddHours(-2));
        _otter.Sync();
        _seal.Clock = new FakeClock { UtcNow = Start.AddDays(3) };
        _server.Calls.Clear();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(["list", "download", "download"], _seal.Asked);
        Assert.Equal("list", _server.Calls[0]);
        Assert.Equal(new[] { "download " + BlobOf(Links), "download " + BlobOf(Sig) }.Order(), _server.Calls.Skip(1).Order());
        Assert.Equal("walrus", _seal.Box.Read(Sig));
    }

    [Fact]
    public void A_machine_whose_clock_is_days_ahead_does_not_win_a_conflict_by_its_wrong_time()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's edit", Start.AddMinutes(-10));
        _otter.Sync();
        byte[] before = _server.Get(BlobOf(Sig))!;

        // The seal's clock is three days ahead, and so is the time of what was typed on it.
        _seal.Clock = new FakeClock { UtcNow = Start.AddDays(3) };
        _seal.Box.Write(Sig, "the seal's edit, stamped with a wrong time", Start.AddDays(3).AddMinutes(-1));
        _server.Calls.Clear();

        var result = _seal.Sync();

        // The plan held only a download, so the time was not asked up front. It is asked
        // before the upload that the download led to, and the run ends there.
        Assert.Equal(SyncEnd.ClockWrong, result.End);
        Assert.Equal(["list", "download " + BlobOf(Sig), "now"], _server.Calls);
        Assert.Equal(0, result.Uploaded + result.ConflictCopies);
        Assert.Equal(before, _server.Get(BlobOf(Sig)));
        Assert.Equal("the seal's edit, stamped with a wrong time", _seal.Box.Read(Sig));
        Assert.Equal([Sig], _seal.Box.Files());
        Assert.Contains("Sync stopped before it wrote to the server because this machine's clock and the server's are more than a day apart", _seal.Log);
    }

    [Fact]
    public void A_machine_set_up_afresh_with_its_date_far_ahead_does_not_delete_its_own_history_from_the_server()
    {
        const string day = "clipboard/OTTER-PC/2026-10-02.jsonl";
        _otter.Box.Write(day, "lines\n", Start.AddDays(-1));
        _otter.Sync();

        // The same machine set up afresh, with its date 200 days ahead: by that date its own
        // history has expired, and an expired blob of its own is one it deletes.
        var fresh = new Machine("OTTER-PC", this) { Clock = new FakeClock { UtcNow = Start.AddDays(200) } };
        _server.Calls.Clear();

        var result = fresh.Sync();

        Assert.Equal(SyncEnd.ClockWrong, result.End);
        Assert.Equal(["list", "download " + BlobOf(day), "now"], _server.Calls);
        Assert.Equal(0, result.RemovedFromServer);
        Assert.Equal([BlobOf(day)], _server.Names);
        fresh.Dispose();
    }

    [Fact]
    public void The_server_is_asked_the_time_once_however_many_downloads_end_in_an_upload()
    {
        // Two files edited here and deleted there: each is downloaded, found to be a tombstone,
        // and sent back. Nothing in the plan is a write, so the time is asked when the first
        // download leads to one, and not again. (The two are the otter's whole library, so
        // their deletions go out with the owner's word: a library that is gone altogether is
        // held without it.)
        BothSynced(Sig, "first");
        BothSynced(Links, "[]");
        _otter.Box.Delete(Sig);
        _otter.Box.Delete(Links);
        Assert.Equal(2, _otter.Sync(confirm: true).TombstonesSent);
        _seal.Box.Write(Sig, "edited on the seal", Start.AddMinutes(-10));
        _seal.Box.Write(Links, "[ \"edited on the seal\" ]", Start.AddMinutes(-10));
        _server.Calls.Clear();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(
            ["list", "download " + BlobOf(Links), "now", "upload " + BlobOf(Links), "download " + BlobOf(Sig), "upload " + BlobOf(Sig)],
            _server.Calls);
    }

    [Fact]
    public void The_server_is_asked_the_time_once_when_the_plan_holds_a_write_and_a_download_ends_in_another()
    {
        BothSynced(Sig, "first");
        _otter.Box.Delete(Sig);
        _otter.Sync();
        _seal.Box.Write(Sig, "edited on the seal", Start.AddMinutes(-10));
        _seal.Box.Write("library/a-new-file.md", "new", Start.AddMinutes(-10));
        _server.Calls.Clear();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(
            ["list", "now", "upload " + BlobOf("library/a-new-file.md"), "download " + BlobOf(Sig), "upload " + BlobOf(Sig)],
            _server.Calls);
    }

    [Fact]
    public void A_leftover_is_as_old_as_the_servers_clock_says_and_not_as_this_machines_clock_says()
    {
        // This machine's clock is two hours ahead of the server's, which is within what is
        // allowed. By this machine's clock the temporary file is 25 hours old; by the server's,
        // whose clock stamped it, it is 23: an upload that may still be under way elsewhere.
        string temporary = BlobCodec.TemporaryName(BlobOf(Sig));
        _server.Put(temporary, [1], Start.AddHours(-23));
        _otter.Clock = new FakeClock { UtcNow = Start.AddHours(2) };

        var kept = _otter.Sync();

        AssertClean(kept);
        Assert.Equal([temporary], _server.Names);
        Assert.Equal(0, kept.RemovedFromServer);
        Assert.Equal(["list", "now"], _otter.Asked);
        Assert.Equal(1, _server.Count("now"));
        Assert.Equal(0, _server.Count("delete"));

        // And the other way round: two hours behind, with a file to send. By this machine's
        // clock the temporary file is 23 hours old, by the server's 25, so it goes.
        _server.Clock = Start.AddHours(2);
        _otter.Clock = new FakeClock { UtcNow = Start };
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-3));
        _server.Calls.Clear();

        var removed = _otter.Sync();

        AssertClean(removed);
        Assert.Equal([BlobOf(Sig)], _server.Names);
        Assert.Equal(1, removed.RemovedFromServer);
        Assert.Equal(["list", "now", "delete " + temporary, "upload " + BlobOf(Sig)], _server.Calls);
    }

    // ---- Tombstones ----

    [Fact]
    public void A_tombstone_older_than_180_days_is_removed_from_the_server_and_forgotten()
    {
        BothSynced(Sig, "walrus");
        _otter.Box.Delete(Sig);
        _otter.Sync();
        _seal.Sync();

        Pass(TimeSpan.FromDays(180));
        Assert.Equal(["list"], Calls(() => _otter.Sync()));

        Pass(TimeSpan.FromSeconds(2));
        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal(1, result.RemovedFromServer);
        Assert.Empty(_server.Names);
        Assert.Empty(_otter.State().Files);

        // The other machine finds the blob gone and forgets the path as well.
        AssertClean(_seal.Sync());
        Assert.Empty(_seal.State().Files);
        Assert.False(_seal.Box.Exists(Sig));
    }

    // ---- Clipboard folders ----

    [Fact]
    public void Clipboard_history_goes_from_the_machine_that_wrote_it_to_the_other_and_the_history_is_told()
    {
        _otter.Box.Write("clipboard/OTTER-PC/2026-10-03.jsonl", "{\"id\":\"1\"}\n", Start.AddHours(-1));
        _otter.Box.Write("clipboard/OTTER-PC/2026-10-03/abc.png", "png", Start.AddHours(-1));
        _otter.Box.Write("clipboard/OTTER-PC/.~lock", "", Start);
        _otter.Box.Write("clipboard/OTTER-PC/2026-10-03.jsonl.tmp", "half", Start);

        var sent = _otter.Sync();
        var taken = _seal.Sync();

        Assert.Equal(2, sent.Uploaded);
        Assert.False(sent.ClipboardChanged);
        AssertClean(taken);
        Assert.True(taken.ClipboardChanged);
        Assert.Equal(["clipboard/OTTER-PC/2026-10-03.jsonl", "clipboard/OTTER-PC/2026-10-03/abc.png"], _seal.Box.Files());
    }

    [Fact]
    public void A_copy_of_the_other_machines_history_that_was_altered_or_removed_here_is_fetched_again_and_never_sent()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        const string picture = "clipboard/OTTER-PC/2026-10-03/abc.png";
        _otter.Box.Write(day, "{\"id\":\"1\"}\n", Start.AddHours(-1));
        _otter.Box.Write(picture, "png", Start.AddHours(-1));
        _otter.Sync();
        _seal.Sync();
        _seal.Box.Write(day, "altered on the seal, and longer", Start);
        _seal.Box.Delete(picture);
        _seal.Box.Write("clipboard/OTTER-PC/2026-10-03/made-here.png", "never the seal's to send", Start);
        _server.Calls.Clear();

        var result = _seal.Sync();

        AssertClean(result);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(0, result.Uploaded + result.TombstonesSent + result.ConflictCopies);
        Assert.Equal("{\"id\":\"1\"}\n", _seal.Box.Read(day));
        Assert.Equal("png", _seal.Box.Read(picture));
        Assert.DoesNotContain(_server.Calls, call => call.StartsWith("upload", StringComparison.Ordinal));
        Assert.Equal(2, _server.Names.Count);
    }

    [Fact]
    public void A_file_this_machine_scrubbed_from_its_own_folder_is_tombstoned_and_goes_from_the_other_machine()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        const string picture = "clipboard/OTTER-PC/2026-10-03/abc.png";
        _otter.Box.Write(day, "{\"id\":\"1\"}\n{\"id\":\"2\"}\n", Start.AddHours(-1));
        _otter.Box.Write(picture, new string('p', 4000), Start.AddHours(-1));
        _otter.Sync();
        _seal.Sync();

        // The owner deletes the entry. A scrub writes the day file again without it, and
        // removes the content the entry had. The day file itself is never removed by a scrub.
        _otter.Box.Write(day, "{\"id\":\"2\"}\n{\"delete\":\"1\"}\n", Start.AddMinutes(-1));
        _otter.Box.Delete(picture);

        var sent = _otter.Sync();
        var taken = _seal.Sync();

        AssertClean(sent);
        Assert.Equal(1, sent.TombstonesSent);
        Assert.Equal(1, sent.Uploaded);
        Assert.Equal(0, sent.Downloaded);
        Assert.True(_server.Get(BlobOf(picture))!.Length < 300);
        Assert.False(_otter.Box.Exists(picture));
        AssertClean(taken);
        Assert.Equal(1, taken.DeletedHere);
        Assert.True(taken.ClipboardChanged);
        Assert.False(_seal.Box.Exists(picture));
        Assert.Equal("{\"id\":\"2\"}\n{\"delete\":\"1\"}\n", _seal.Box.Read(day));

        // And it stays deleted: neither machine has anything more to do about it.
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
        Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
    }

    [Fact]
    public void History_that_retention_removed_under_a_date_far_ahead_is_not_deleted_from_the_server_and_the_other_machine_and_comes_back()
    {
        const string day = "clipboard/OTTER-PC/2026-10-02.jsonl";
        string picture = "clipboard/OTTER-PC/2026-10-02/" + ClipPaths.BlobName("png"u8, ClipPaths.ImageExtension);
        _otter.Box.Write(day, "{\"id\":\"1\"}\n", Start.AddDays(-1));
        _otter.Box.Write(picture, "png", Start.AddDays(-1));
        AssertClean(_otter.Sync());
        AssertClean(_seal.Sync());
        Assert.Equal([day, picture], _seal.Box.Files());
        byte[] dayBlob = _server.Get(BlobOf(day))!;
        byte[] pictureBlob = _server.Get(BlobOf(picture))!;

        // The otter's clock goes 200 days ahead, and retention, which goes by this machine's
        // clock, removes what has expired by that date: the day file and the day's content.
        var ahead = new FakeClock { UtcNow = Start.AddDays(200) };
        _otter.Clock = ahead;
        int removed = ClipRetention.Run(Path.Combine(_otter.Box.Data, "clipboard"), _otter.RetentionDays, DateOnly.FromDateTime(ahead.UtcNow.UtcDateTime));
        Assert.Equal(2, removed);
        Assert.Empty(_otter.Box.Files());

        // While the clock is wrong, nothing is written to the server.
        var wrong = _otter.Sync();

        Assert.Equal(SyncEnd.ClockWrong, wrong.End);
        Assert.Equal(["list", "now"], _otter.Asked);
        Assert.Equal(dayBlob, _server.Get(BlobOf(day)));
        Assert.Equal(pictureBlob, _server.Get(BlobOf(picture)));

        // The clock is put right. The files are gone here and their days have not expired. The
        // owner did not delete them: a scrub would have left the day file. No tombstone goes
        // out, and both files are made again from the server.
        _otter.Clock = null;
        var right = _otter.Sync();

        AssertClean(right);
        Assert.Equal(0, right.TombstonesSent + right.RemovedFromServer + right.Uploaded);
        Assert.Equal(2, right.Downloaded);
        Assert.True(right.ClipboardChanged);
        Assert.Equal(["list", "download", "download"], _otter.Asked);
        Assert.Equal("{\"id\":\"1\"}\n", _otter.Box.Read(day));
        Assert.Equal("png", _otter.Box.Read(picture));
        Assert.Equal(dayBlob, _server.Get(BlobOf(day)));
        Assert.Equal(pictureBlob, _server.Get(BlobOf(picture)));

        // The other machine still has both and is told to delete nothing.
        var onSeal = _seal.Sync();

        AssertClean(onSeal);
        Assert.Equal(0, onSeal.DeletedHere);
        Assert.Equal([day, picture], _seal.Box.Files());
        Assert.Equal("png", _seal.Box.Read(picture));
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
    }

    [Fact]
    public void A_file_in_this_machines_own_folder_is_never_changed_by_what_the_server_holds()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        _otter.Box.Write(day, "the otter's own lines\n", Start.AddHours(-1));
        _otter.Sync();

        // Something else writes the same path's blob, later and with other content: a second
        // machine that was given this machine's name.
        var impostor = new Machine("OTTER-PC", this);
        impostor.Box.Write(day, "lines of a machine with the same name\n", Start.AddMinutes(-1));
        Pass(TimeSpan.FromMinutes(1));
        impostor.Sync();
        impostor.Dispose();

        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal("the otter's own lines\n", _otter.Box.Read(day));
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.Downloaded + result.ConflictCopies);
        Assert.Equal([day], _otter.Box.Files());
    }

    [Fact]
    public void A_file_of_this_machines_own_folder_that_is_missing_here_is_made_again_from_the_server()
    {
        const string day = "clipboard/OTTER-PC/2026-10-02.jsonl";
        _otter.Box.Write(day, "lines\n", Start.AddDays(-1));
        _otter.Sync();

        // The same machine set up afresh: an empty data folder and no record.
        var fresh = new Machine("OTTER-PC", this);
        var result = fresh.Sync();

        AssertClean(result);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("lines\n", fresh.Box.Read(day));
        Assert.True(result.ClipboardChanged);
        fresh.Dispose();
    }

    [Fact]
    public void An_expired_day_of_the_other_machine_is_opened_once_to_learn_what_it_is_and_then_left_on_the_server()
    {
        const string day = "clipboard/OTTER-PC/2026-09-01.jsonl";
        _otter.Box.Write(day, "old lines\n", Start.AddDays(-32));
        _otter.Sync();
        _seal.RetentionDays = 30;

        var first = _seal.Sync();
        var second = Calls(() => AssertClean(_seal.Sync()));

        AssertClean(first);
        Assert.Equal(0, first.Downloaded);
        Assert.Empty(_seal.Box.Files());
        Assert.Equal(SyncedKind.Ignored, _seal.State().Find(SyncPath.Key(day))!.Kind);
        Assert.Equal(["list"], second);
        Assert.Single(_server.Names);
    }

    [Fact]
    public void A_day_that_expires_here_is_dropped_here_and_stays_on_the_server_for_its_owner()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        _otter.Box.Write(day, "lines\n", Start.AddHours(-1));
        _otter.Sync();
        _seal.RetentionDays = 30;
        _seal.Sync();

        // Thirty-one days on, the seal's retention has removed its copy. Nothing is sent about it.
        Pass(TimeSpan.FromDays(31));
        _seal.Box.Delete(day);
        var result = Calls(() => AssertClean(_seal.Sync()));

        Assert.Equal(["list"], result);
        Assert.Single(_server.Names);
        Assert.Equal(SyncedKind.Ignored, _seal.State().Find(SyncPath.Key(day))!.Kind);
    }

    [Fact]
    public void The_machine_that_wrote_a_day_deletes_its_expired_blobs_from_the_server_outright()
    {
        const string day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        const string picture = "clipboard/OTTER-PC/2026-10-03/abc.png";
        _otter.Box.Write(day, "lines\n", Start.AddHours(-1));
        _otter.Box.Write(picture, "png", Start.AddHours(-1));
        _otter.Sync();
        _seal.Sync();
        _otter.Box.Delete(picture);
        _otter.Sync();

        Pass(TimeSpan.FromDays(91));
        var result = _otter.Sync();

        // The day file and the picture's tombstone both go, and no tombstone is written for the day file.
        AssertClean(result);
        Assert.Equal(2, result.RemovedFromServer);
        Assert.Equal(0, result.TombstonesSent + result.Uploaded);
        Assert.Empty(_server.Names);
        Assert.Empty(_otter.State().Files);

        // The other machine keeps its copy until its own retention removes it, and sends nothing.
        var onSeal = Calls(() => AssertClean(_seal.Sync()));
        Assert.Equal(["list"], onSeal);
        Assert.True(_seal.Box.Exists(day));
    }

    [Fact]
    public void A_folder_this_machine_wrote_under_an_earlier_name_is_still_its_own_to_send()
    {
        const string day = "clipboard/OLD-OTTER/2026-10-03.jsonl";
        _otter.Devices.Add("OLD-OTTER");
        _otter.Box.Write(day, "lines\n", Start.AddHours(-1));
        _otter.Sync();
        _otter.Box.Write(day, "lines after a scrub\n", Start);

        var result = _otter.Sync();

        Assert.Equal(1, result.Uploaded);
        AssertClean(_seal.Sync());
        Assert.Equal("lines after a scrub\n", _seal.Box.Read(day));
    }

    // ---- What the server will not do, and what the folder will not show ----

    [Fact]
    public void A_leftover_the_server_will_not_delete_is_counted_and_passed_over_and_the_files_are_still_synced()
    {
        string old = BlobCodec.TemporaryName(BlobOf(Links));
        _server.Put(old, [1], Start.AddDays(-2));
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _server.Fault = call => call == "delete " + old ? new RemoteStoreException(RemoteFailure.Folder, "SftpPermissionDeniedException") : null;

        var result = _otter.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Null(result.Failure);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.RemovedFromServer);
        Assert.Equal(1, result.LeftForLater);
        Assert.False(result.Clean);
        Assert.Equal(new[] { old, BlobOf(Sig) }.Order(StringComparer.Ordinal), _server.Names);
        Assert.DoesNotContain(_otter.Log, line => line.StartsWith("Sync could not use the server", StringComparison.Ordinal));
    }

    [Fact]
    public void A_blob_that_vanished_between_the_listing_and_the_download_is_left_for_later_and_is_no_refusal_of_the_servers()
    {
        string[] paths = ["library/a.md", "library/b.md", "library/c.md", "library/d.md"];
        foreach (string path in paths)
        {
            _otter.Box.Write(path, path, Start.AddHours(-2));
        }

        _otter.Sync();

        // Three of the four go from the server after the seal has listed them, as when the
        // other machine removes them at that moment: the three the run asks for first, one
        // after the other. Three refusals in a row would end the run. These are none.
        var asked = paths.OrderBy(BlobOf, StringComparer.Ordinal).ToList();
        _server.Fault = call =>
        {
            if (call.StartsWith("download", StringComparison.Ordinal))
            {
                foreach (string gone in asked.Take(3))
                {
                    _server.Remove(BlobOf(gone));
                }
            }

            return null;
        };

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Null(result.Failure);
        Assert.Equal(4, _server.Count("download"));
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(3, result.LeftForLater);
        Assert.Equal(0, result.BadBlobs);
        Assert.Empty(_seal.State().Bad);
        Assert.Equal([asked[3]], _seal.Box.Files());
    }

    [Fact]
    public void A_download_the_server_refuses_is_left_for_later_not_called_unusable_and_the_run_goes_on()
    {
        string[] paths = ["library/a.md", "library/b.md", "library/c.md"];
        foreach (string path in paths)
        {
            _otter.Box.Write(path, path, Start.AddHours(-2));
        }

        _otter.Sync();
        _server.Fault = call => call == "download " + BlobOf("library/b.md") ? new RemoteStoreException(RemoteFailure.Server, "SshException") : null;

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal(0, result.BadBlobs);
        Assert.False(result.Clean);
        Assert.Empty(_seal.State().Bad);
        Assert.Equal(["library/a.md", "library/c.md"], _seal.Box.Files());
        Assert.DoesNotContain(_seal.Log, line => line.StartsWith("A blob could not be used", StringComparison.Ordinal));

        // The next run asks for it again.
        _server.Fault = null;
        var next = _seal.Sync();
        AssertClean(next);
        Assert.Equal(1, next.Downloaded);
        Assert.Equal("library/b.md", _seal.Box.Read("library/b.md"));
    }

    [Fact]
    public void A_folder_that_could_not_be_listed_makes_the_run_incomplete_even_when_the_record_knows_nothing_under_it()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Box.Write("library/locked/never-synced.md", "text", Start.AddHours(-2));
        string locked = _otter.Box.Full("library/locked");
        _otter.Box.DenyListing(locked);
        SyncResult result;
        try
        {
            result = _otter.Sync();
        }
        finally
        {
            _otter.Box.Allow(locked);
        }

        // Nothing the record knows is behind the folder, so nothing is counted as left for
        // later. The run is incomplete all the same: there is a part of the folder it did not see.
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, result.Unreadable);
        Assert.Equal(0, result.LeftForLater);
        Assert.False(result.Clean);
        Assert.Contains("The data folder holds folders or files that could not be read, which are tried again by the next run: 1", _otter.Log);

        // The next run can list it, and has nothing more to say about the folder.
        var next = _otter.Sync();
        AssertClean(next);
        Assert.Equal(1, next.Uploaded);
        Assert.Equal(0, next.Unreadable);
        Assert.Single(_otter.Log, line => line.StartsWith("The data folder holds", StringComparison.Ordinal));
    }

    [Fact]
    public void Links_and_names_that_cannot_be_synced_are_counted_in_the_log_and_do_not_make_the_run_incomplete()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Box.Write("library/ leading space.md", "text");
        _otter.Box.Junction(_otter.Box.Full("library/linked"), Path.Combine(_otter.Box.Root, "elsewhere"));

        var result = _otter.Sync();

        AssertClean(result);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(1, result.Links);
        Assert.Equal(1, result.BadNames);
        Assert.Equal(0, result.Unreadable);
        Assert.Contains("The data folder holds links, which are not followed: 1", _otter.Log);
        Assert.Contains("The data folder holds files whose names cannot be synced, which are left alone: 1", _otter.Log);
        Assert.DoesNotContain(_otter.Log, line => line.Contains("could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain(_otter.Log, line => line.Contains("leading", StringComparison.Ordinal) || line.Contains("linked", StringComparison.Ordinal) || line.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stop_asked_for_while_the_folder_was_looked_at_is_heeded_before_anything_is_written_to_the_server()
    {
        string old = BlobCodec.TemporaryName(BlobOf(Links));
        _server.Put(old, [1], Start.AddDays(-2));
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));

        // Asked for once the server has been listed, which is before the folder is looked at.
        _otter.Cancelled = () => _server.Count("list") > 0;

        var result = _otter.Sync();

        Assert.Equal(SyncEnd.Stopped, result.End);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal([old], _server.Names);
    }

    [Theory]
    [InlineData("list")]        // wiped before the plan names a blob
    [InlineData("now")]         // wiped before a file is sealed for sending
    [InlineData("download")]    // wiped before a downloaded blob is opened
    public void Keys_that_are_wiped_under_a_run_end_it_as_stopped_and_nothing_leaves_the_run_as_an_exception(string wipedAt)
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        _seal.Box.Write(Links, "[]", Start.AddHours(-2));
        _server.Calls.Clear();
        _server.Fault = call =>
        {
            if (call.StartsWith(wipedAt, StringComparison.Ordinal))
            {
                _seal.Keys!.Dispose();
            }

            return null;
        };

        var result = _seal.Sync();

        // Beacon is closing. It is not the server that failed, and not the blob that is bad.
        Assert.Equal(SyncEnd.Stopped, result.End);
        Assert.Null(result.Failure);
        Assert.Equal(0, result.BadBlobs + result.Downloaded);
        Assert.Empty(_seal.State().Bad);
        Assert.False(_seal.Box.Exists(Sig));
        Assert.DoesNotContain(_seal.Log, line => line.StartsWith("A blob could not be used", StringComparison.Ordinal));
        Assert.Equal(wipedAt, _server.Calls[^1].Split(' ')[0]);

        _server.Fault = null;
        AssertClean(_seal.Sync());
        Assert.Equal("walrus", _seal.Box.Read(Sig));
    }

    // ---- Rules that held before they were pinned ----

    [Theory]
    [InlineData("another path")]
    [InlineData("an unsafe path")]
    [InlineData("another hash")]
    public void A_blob_that_authenticates_and_does_not_hold_what_a_blob_holds_is_treated_like_one_that_does_not_authenticate(string wrong)
    {
        BothSynced(Sig, "the true text");
        using var keys = SyncKeys.FromMaster(Master);
        byte[] content = Encoding.UTF8.GetBytes("text from a blob that is not what it says");
        var header = wrong switch
        {
            // Sealed under this path's name, and saying it is another file.
            "another path" => new BlobHeader("library/snippets/other.md", Start, Deleted: false, BlobCodec.Hash(content)),

            // A path that would leave the data folder.
            "an unsafe path" => new BlobHeader("../outside.md", Start, Deleted: false, BlobCodec.Hash(content)),

            // The right path, and a hash that is not that of the content.
            _ => new BlobHeader(Sig, Start, Deleted: false, BlobCodec.Hash([1, 2, 3])),
        };
        _server.Put(BlobOf(Sig), BlobCodec.Seal(keys, BlobOf(Sig), header, content));

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(1, result.BadBlobs);
        Assert.Equal(0, result.Downloaded + result.Uploaded + result.DeletedHere + result.ConflictCopies);
        Assert.False(result.Clean);
        Assert.Equal("the true text", _seal.Box.Read(Sig));
        Assert.Equal([Sig], _seal.Box.Files());
        Assert.False(File.Exists(Path.Combine(_seal.Box.Root, "outside.md")));
        Assert.Contains($"A blob could not be used and was skipped (BadContent): {BlobOf(Sig)}", _seal.Log);
        Assert.Equal([BlobOf(Sig)], _seal.State().Bad.Keys);

        // Remembered like any other: not asked for again, and still counted.
        _server.Calls.Clear();
        var again = _seal.Sync();
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(1, again.BadBlobs);
    }

    [Fact]
    public void A_file_edited_after_the_run_looked_at_it_is_not_deleted_on_the_strength_of_a_tombstone()
    {
        BothSynced(Sig, "first");
        _otter.Box.Delete(Sig);
        _otter.Sync();
        _server.Fault = call =>
        {
            if (call.StartsWith("download", StringComparison.Ordinal))
            {
                _seal.Box.Write(Sig, "typed on the seal this very moment", Start.AddMinutes(-1));
            }

            return null;
        };

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(0, result.DeletedHere);
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal("typed on the seal this very moment", _seal.Box.Read(Sig));
        Assert.Equal(SyncedKind.Live, _seal.State().Find(Sig)!.Kind);

        // The next run sees an edit here and a deletion there: the edit wins and goes back.
        _server.Fault = null;
        var next = _seal.Sync();
        AssertClean(next);
        Assert.Equal(1, next.Uploaded);
        AssertClean(_otter.Sync());
        Assert.Equal("typed on the seal this very moment", _otter.Box.Read(Sig));
    }

    // A clock that stops the run dead, as a power cut would, at the first time it is read once
    // something has come about. Nothing after that moment happens, the record's write included.
    private sealed class CutOffClock(IClock clock, Func<bool> cut) : IClock
    {
        public DateTimeOffset UtcNow => cut() ? throw new CutOffException() : clock.UtcNow;
    }

    private sealed class CutOffException : Exception;

    [Fact]
    public void The_loser_of_a_conflict_is_still_there_when_the_run_is_cut_off_right_after_it_was_kept()
    {
        BothSynced(Sig, "first");
        _otter.Box.Write(Sig, "the otter's later edit", Start.AddMinutes(-10));
        _seal.Box.Write(Sig, "the seal's edit", Start.AddMinutes(-30));
        _otter.Sync();
        const string copy = "library/snippets/sig.conflict-SEAL-PC-20261003-120000.md";

        // The server's is newer, so the file here is renamed to its copy and the server's is
        // then written. The run dies between the two.
        _seal.Clock = new CutOffClock(_clock, () => _seal.Box.Exists(copy));
        Assert.Throws<CutOffException>(() => _seal.Sync());

        Assert.Equal([copy], _seal.Box.Files());
        Assert.Equal("the seal's edit", _seal.Box.Read(copy));

        // The next run finds no file where the record knows one, and the blob changed: the
        // edit there wins and is written. One copy, and neither version lost.
        _seal.Clock = null;
        var next = _seal.Sync();

        AssertClean(next);
        Assert.Equal(0, next.ConflictCopies + next.TombstonesSent);
        Assert.Equal("the otter's later edit", _seal.Box.Read(Sig));
        Assert.Equal("the seal's edit", _seal.Box.Read(copy));
        Assert.Equal([copy, Sig], _seal.Box.Files());
    }

    [Fact]
    public void A_path_this_run_could_not_look_at_gets_no_action_when_a_blob_names_it()
    {
        // A file that is too large is unknown to the run, and the record does not know the
        // path either, so the plan cannot tell that the server's blob is this path's: it is
        // opened like any blob of unknown path. It is a tombstone. Nothing is done about it.
        const string video = "library/video.bin";
        _seal.Box.Write(video, "small");
        using (var large = new FileStream(_seal.Box.Full(video), FileMode.Open))
        {
            large.SetLength(SyncLimits.MaxFileBytes + 1);
        }

        using var keys = SyncKeys.FromMaster(Master);
        _server.Put(BlobOf(video), BlobCodec.Seal(keys, BlobOf(video), new BlobHeader(video, Start, Deleted: true, ""), []));

        var result = _seal.Sync();

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(1, _server.Count("download"));
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal(0, result.DeletedHere + result.Downloaded + result.Uploaded + result.BadBlobs);
        Assert.True(_seal.Box.Exists(video));
        Assert.Empty(_seal.State().Bad);

        // The blob is remembered as one this machine does not keep, as the server lists it, so
        // that it is not downloaded again only to be left alone again. The path is still
        // counted as left for later at every run, and nothing is asked of the server for it.
        var entry = Assert.Single(_seal.State().Files).Value;
        Assert.Equal(SyncedKind.Ignored, entry.Kind);
        Assert.Equal(video, entry.Path);
        Assert.Equal(BlobOf(video), entry.Blob);
        for (int run = 2; run <= 3; run++)
        {
            Pass(TimeSpan.FromMinutes(5));
            var again = _seal.Sync();

            Assert.Equal(SyncEnd.Completed, again.End);
            Assert.Equal(["list"], _seal.Asked);
            Assert.Equal(1, again.LeftForLater);
            Assert.Equal(1, again.TooLarge);
            Assert.True(_seal.Box.Exists(video));
        }

        Assert.Equal(1, _server.Count("download"));

        // Once the path can be looked at, the record that says ignored counts as no record: the
        // blob is opened, as for any file that is new here where the server has a blob. It
        // holds a deletion, and the file here, which nobody has compared with it, is the edit
        // that wins.
        using (var small = new FileStream(_seal.Box.Full(video), FileMode.Open))
        {
            small.SetLength(5);
        }

        var known = _seal.Sync();

        AssertClean(known);
        Assert.Equal(["list", "download", "now", "upload"], _seal.Asked);
        Assert.Equal(1, known.Uploaded);
        Assert.Equal(0, known.DeletedHere);
        Assert.True(_seal.Box.Exists(video));
        Assert.Equal(SyncedKind.Live, _seal.State().Find(video)!.Kind);
    }

    [Fact]
    public void A_blob_whose_path_lies_behind_a_link_here_is_downloaded_once_and_not_again_at_every_run()
    {
        // The otter has a file in a folder that on the seal is a link, which sync never follows.
        const string behind = "library/linked/a.md";
        _otter.Box.Write(behind, "walrus", Start.AddHours(-2));
        AssertClean(_otter.Sync());
        string elsewhere = Path.Combine(_seal.Box.Root, "elsewhere");
        _seal.Box.Junction(_seal.Box.Full("library/linked"), elsewhere);
        _server.Calls.Clear();

        for (int run = 1; run <= 3; run++)
        {
            var result = _seal.Sync();

            // The path cannot be looked at here, so nothing is done about it, and it is
            // counted. Its blob is opened by the first run, which has no other way to learn
            // whose it is, and by none after that.
            Assert.Equal(SyncEnd.Completed, result.End);
            Assert.Equal(1, result.LeftForLater);
            Assert.Equal(1, result.Links);
            Assert.Equal(0, result.Downloaded + result.BadBlobs + result.Uploaded);
            Assert.Equal(run == 1 ? ["list", "download"] : ["list"], _seal.Asked);
            Pass(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(1, _server.Count("download"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Equal(SyncedKind.Ignored, Assert.Single(_seal.State().Files).Value.Kind);
        Assert.Empty(_seal.State().Bad);
        Assert.Equal([BlobOf(behind)], _server.Names);

        // When the blob has gone from the server, nothing is left of the path but that entry,
        // which goes too: the link is still there, and is no reason to call the run incomplete.
        _server.Remove(BlobOf(behind));

        var after = _seal.Sync();

        AssertClean(after);
        Assert.Equal(1, after.Links);
        Assert.Equal(["list"], _seal.Asked);
        Assert.Empty(_seal.State().Files);
    }

    [Fact]
    public void A_file_written_again_with_the_same_content_is_read_once_and_the_record_takes_its_new_time()
    {
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-2));
        _otter.Sync();
        _otter.Box.Write(Sig, "walrus", Start.AddHours(-1));

        // This run has to read the file to see that it is the same.
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
        var entry = _otter.State().Find(Sig)!;
        Assert.Equal(Start.AddHours(-1), entry.MtimeUtc);
        Assert.Equal(6, entry.Size);

        // The next one does not: the file is held so that reading it would fail, and a file
        // that has to be read and cannot be is unknown to the run and left for later.
        using var held = new FileStream(_otter.Box.Full(Sig), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _server.Calls.Clear();
        var next = _otter.Sync();

        AssertClean(next);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(0, next.Uploaded + next.Downloaded + next.Unreadable);
    }

    [Fact]
    public void A_downloaded_file_is_recorded_with_the_size_and_time_it_has_on_this_disk_and_is_not_read_again()
    {
        // A file whose time is days ahead: that time is not given to the copy here
        // (SyncFiles.Write refuses a time more than a day ahead), which keeps the time it was
        // written at. The record has to hold the time the file has, not the blob's. What that
        // time is depends on the machine, so it is only held against the file itself.
        _otter.Box.Write(Sig, "walrus", Start.AddDays(5));
        _otter.Sync();

        AssertClean(_seal.Sync());

        var entry = _seal.State().Find(Sig)!;
        Assert.Equal(_seal.Box.Mtime(Sig), entry.MtimeUtc);
        Assert.Equal(6, entry.Size);

        // Held so that reading it would fail: the second run takes it as unchanged from its
        // size and time alone.
        using var held = new FileStream(_seal.Box.Full(Sig), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _server.Calls.Clear();
        var next = _seal.Sync();

        AssertClean(next);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(0, next.Uploaded + next.Downloaded + next.Unreadable);
    }

    // ---- Two files with one key ----

    [Fact]
    public void Two_files_whose_keys_are_equal_are_neither_sent_nor_written_over_nor_taken_for_deleted()
    {
        // Windows keeps a file named with the Kelvin sign apart from one named with a small k;
        // lower-cased they are the same, so the two have one key and would have one blob.
        const string plain = "library/k.md";
        const string kelvin = "library/K.md";
        BothSynced(plain, "the one with a small k");

        // The second file appears on the otter, and the seal edits the one both have.
        _otter.Box.Write(kelvin, "the one with the Kelvin sign", Start.AddHours(-1));
        Assert.Equal(SyncPath.Key(plain), SyncPath.Key(kelvin));
        Assert.Equal(2, _otter.Box.Files().Count);
        _seal.Box.Write(plain, "edited on the seal", Start.AddMinutes(-30));
        AssertClean(_seal.Sync());
        _server.Calls.Clear();

        var result = _otter.Sync();

        // Neither file is known to the run. Nothing is asked of the server for the key, no
        // file is written over, and the path the record knows is left for later, not deleted.
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(2, result.BadNames);
        Assert.Equal(1, result.LeftForLater);
        Assert.Equal(0, result.Uploaded + result.Downloaded + result.TombstonesSent + result.DeletedHere + result.ConflictCopies);
        Assert.Equal("the one with a small k", _otter.Box.Read(plain));
        Assert.Equal("the one with the Kelvin sign", _otter.Box.Read(kelvin));
        Assert.Equal(SyncedKind.Live, _otter.State().Find(SyncPath.Key(plain))!.Kind);
        Assert.Contains("The data folder holds files whose names cannot be synced, which are left alone: 2", _otter.Log);

        // Once the owner has taken one of the two away, the other is the path's file again.
        _otter.Box.Delete(kelvin);
        var next = _otter.Sync();

        AssertClean(next);
        Assert.Equal(1, next.Downloaded);
        Assert.Equal(0, next.BadNames);
        Assert.Equal("edited on the seal", _otter.Box.Read(plain));
    }

    // ---- The brake on mass deletion ----
    //
    // A file that was synced and is no longer in the data folder is taken for one the owner
    // deleted. When that is so for many library files at once, the run sends nothing and
    // changes nothing, and the owner says which it is: Confirm Sync Deletions (a run that may
    // send them) or Restore Missing Files (a run that fetches them back).

    // So many library files, each with a text of its own, synced from the otter.
    private List<string> Library(int files, bool onTheSealToo = true)
    {
        var paths = Enumerable.Range(1, files).Select(number => FormattableString.Invariant($"library/snippets/note-{number:000}.md")).ToList();
        foreach (string path in paths)
        {
            _otter.Box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        AssertClean(_otter.Sync());
        if (onTheSealToo)
        {
            AssertClean(_seal.Sync());
        }

        return paths;
    }

    // Every file in a machine's data folder, with its time and its text.
    private static List<string> Folder(Machine machine) =>
        [.. machine.Box.Files().Select(path => FormattableString.Invariant($"{path} | {machine.Box.Mtime(path).UtcTicks} | {machine.Box.Read(path)}"))];

    // Every file in a machine's data folder, with its text and without its time.
    private static List<string> Texts(Machine machine) => [.. machine.Box.Files().Select(path => path + " | " + machine.Box.Read(path))];

    // Every blob on the server, with its bytes. A blob that is sent again has other bytes, whatever it holds.
    private List<string> Server() => [.. _server.Names.Select(name => name + " " + Convert.ToHexString(_server.Get(name)!))];

    // A run of the otter that the brake holds. It asks the server for its listing and for
    // nothing else, and leaves the server, the otter's folder, the otter's record and the seal
    // exactly as they were. The seal must have nothing of its own to do when this is called,
    // unless it is left out. "Paused" is what the run is told: that sync is paused already.
    private void AssertHeld(int missing, bool andTheSealFindsNothingToDo = true, bool paused = false)
    {
        var server = Server();
        var here = Folder(_otter);
        byte[] record = File.ReadAllBytes(_otter.StateFile);
        var there = Folder(_seal);

        var result = _otter.Sync(paused: paused);

        Assert.Equal(SyncEnd.Held, result.End);
        Assert.Equal(missing, result.HeldDeletions);
        Assert.False(result.NothingToHold);
        Assert.False(result.Clean);
        Assert.Equal(["list"], _otter.Asked);
        Assert.Equal(0, result.Uploaded + result.Downloaded + result.DeletedHere + result.TombstonesSent + result.ConflictCopies + result.RemovedFromServer);
        Assert.Equal(server, Server());
        Assert.Equal(here, Folder(_otter));
        Assert.Equal(record, File.ReadAllBytes(_otter.StateFile));

        // And the other machine finds nothing to do: nothing is deleted or replaced there.
        if (andTheSealFindsNothingToDo)
        {
            Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
        }

        Assert.Equal(there, Folder(_seal));
    }

    [Fact]
    public void A_library_folder_that_was_deleted_by_hand_is_held_and_nothing_is_sent_or_changed_on_either_machine()
    {
        Library(8);
        Directory.Delete(_otter.Box.Full("library"), recursive: true);

        AssertHeld(missing: 8);

        // It is looked at afresh at every run, and held for as long as the files are missing.
        Pass(TimeSpan.FromMinutes(5));
        AssertHeld(missing: 8);
        Assert.Equal(8, _seal.Box.Files().Count);
    }

    [Fact]
    public void Restore_Missing_Files_brings_back_a_library_folder_that_was_deleted_by_hand()
    {
        Library(8);
        var before = Folder(_otter);
        var server = Server();
        Directory.Delete(_otter.Box.Full("library"), recursive: true);
        AssertHeld(missing: 8);

        var result = _otter.Sync(restore: true);

        AssertClean(result);
        Assert.Equal(8, result.Downloaded);
        Assert.Equal(0, result.Uploaded + result.TombstonesSent + result.DeletedHere + result.ConflictCopies + result.RemovedFromServer + result.HeldDeletions);

        // The listing and the eight downloads: nothing is written, so the server is not asked the time.
        Assert.Equal(["list", .. Enumerable.Repeat("download", 8)], _otter.Asked);

        // Every file is back with its text and its time, and the server is as it was.
        Assert.Equal(before, Folder(_otter));
        Assert.Equal(server, Server());

        // Sync goes on as before on both machines.
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
        Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
        Assert.Equal(before, Folder(_seal));
    }

    [Fact]
    public void Confirm_Sync_Deletions_sends_the_deletions_of_a_library_folder_that_the_owner_did_delete()
    {
        Library(8);
        Directory.Delete(_otter.Box.Full("library"), recursive: true);
        AssertHeld(missing: 8);

        var sent = _otter.Sync(confirm: true);

        AssertClean(sent);
        Assert.Equal(8, sent.TombstonesSent);
        Assert.Equal(0, sent.HeldDeletions);
        Assert.Equal(["list", "now", .. Enumerable.Repeat("upload", 8)], _otter.Asked);

        // The other machine takes them: deletions that arrive from the other machine are not held.
        var taken = _seal.Sync();

        AssertClean(taken);
        Assert.Equal(8, taken.DeletedHere);
        Assert.Empty(_seal.Box.Files());
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));
        Assert.Equal(["list"], Calls(() => AssertClean(_seal.Sync())));
    }

    // A cleanup tool removes thirty of a hundred library files on the otter.
    private List<string> AToolRemovedThirtyOfAHundred()
    {
        var paths = Library(100);
        var removed = paths.Where((_, index) => index % 10 < 3).ToList();
        Assert.Equal(30, removed.Count);
        foreach (string path in removed)
        {
            _otter.Box.Delete(path);
        }

        return removed;
    }

    [Fact]
    public void Thirty_of_a_hundred_library_files_removed_by_a_tool_are_held_and_sync_goes_on_by_itself_once_they_are_back()
    {
        var removed = AToolRemovedThirtyOfAHundred();

        AssertHeld(missing: 30);

        // The owner puts them back as they were, out of the recycle bin say. Nothing more is
        // needed: the next run finds nothing missing and nothing to do.
        foreach (string path in removed)
        {
            _otter.Box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        Pass(TimeSpan.FromMinutes(5));
        Assert.Equal(["list"], Calls(() => AssertClean(_otter.Sync())));

        // And an edit made after that goes across as always.
        _otter.Box.Write(removed[0], "edited after all that", Start.AddMinutes(1));
        AssertClean(_otter.Sync());
        AssertClean(_seal.Sync());
        Assert.Equal("edited after all that", _seal.Box.Read(removed[0]));
        Assert.Equal(100, _seal.Box.Files().Count);
    }

    [Fact]
    public void Restore_Missing_Files_fetches_the_thirty_files_a_tool_removed()
    {
        AToolRemovedThirtyOfAHundred();
        var server = Server();
        AssertHeld(missing: 30);

        var result = _otter.Sync(restore: true);

        AssertClean(result);
        Assert.Equal(30, result.Downloaded);
        Assert.Equal(0, result.Uploaded + result.TombstonesSent + result.DeletedHere + result.ConflictCopies);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 30)], _otter.Asked);
        Assert.Equal(100, _otter.Box.Files().Count);
        Assert.Equal(Folder(_seal), Folder(_otter));
        Assert.Equal(server, Server());
    }

    [Fact]
    public void Confirm_Sync_Deletions_sends_the_thirty_deletions_and_the_other_machine_keeps_the_seventy()
    {
        var removed = AToolRemovedThirtyOfAHundred();
        AssertHeld(missing: 30);

        var sent = _otter.Sync(confirm: true);

        AssertClean(sent);
        Assert.Equal(30, sent.TombstonesSent);
        Assert.Equal(["list", "now", .. Enumerable.Repeat("upload", 30)], _otter.Asked);

        var taken = _seal.Sync();

        AssertClean(taken);
        Assert.Equal(30, taken.DeletedHere);
        Assert.Equal(70, _seal.Box.Files().Count);
        Assert.Equal(_otter.Box.Files(), _seal.Box.Files());
        Assert.DoesNotContain(removed[0], _seal.Box.Files());
    }

    // The otter's data folder is put back from a backup. Since the backup was made, three files
    // were edited on the otter and one on the seal, and six were made, all of it synced to both
    // machines. The folder that comes back misses the six, and holds the four in the version
    // they had at the backup, with the time they had then: or, put back by a tool that does not
    // keep file times (a zip, a copy tool, a cloud download), every file with the present time.
    private (List<string> Missing, List<string> Older) ABackupIsPutBack(bool withItsFileTimes = true)
    {
        var paths = Library(10);
        var backup = _otter.Box.Files().ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(_otter.Box.Full(path)), Mtime: _otter.Box.Mtime(path)));

        Pass(TimeSpan.FromHours(1));
        var edited = paths.Take(3).ToList();
        foreach (string path in edited)
        {
            _otter.Box.Write(path, "edited on the otter after the backup: " + path, Start.AddMinutes(30));
        }

        var made = Enumerable.Range(1, 6).Select(number => FormattableString.Invariant($"library/scripts/made-{number}.cmd")).ToList();
        foreach (string path in made)
        {
            _otter.Box.Write(path, "made after the backup: " + path, Start.AddMinutes(40));
        }

        _seal.Box.Write(paths[3], "edited on the seal after the backup", Start.AddMinutes(45));
        AssertClean(_seal.Sync());
        AssertClean(_otter.Sync());
        AssertClean(_seal.Sync());
        Assert.Equal(16, _otter.Box.Files().Count);
        Assert.Equal(Folder(_seal), Folder(_otter));

        Pass(TimeSpan.FromHours(1));
        foreach (string path in _otter.Box.Files())
        {
            _otter.Box.Delete(path);
        }

        foreach (var (path, file) in backup)
        {
            _otter.Box.WriteBytes(path, file.Bytes, withItsFileTimes ? file.Mtime : _clock.UtcNow);
        }

        Assert.Equal(10, _otter.Box.Files().Count);
        return (made, [.. edited, paths[3]]);
    }

    [Fact]
    public void A_data_folder_put_back_from_a_backup_is_held_and_neither_its_gaps_nor_its_older_versions_are_sent()
    {
        var (_, older) = ABackupIsPutBack();

        // Six files are missing, of the sixteen the record holds. The run is held as a whole:
        // the four older versions do not go up over the newer ones either.
        AssertHeld(missing: 6);

        Assert.All(older, path => Assert.StartsWith("edited on the ", _seal.Box.Read(path)));
        Assert.All(older, path => Assert.Equal("the text of " + path, _otter.Box.Read(path)));
    }

    [Fact]
    public void Restore_Missing_Files_after_a_backup_was_put_back_fetches_what_is_missing_and_keeps_the_newer_versions_with_the_older_ones_beside_them()
    {
        var (missing, older) = ABackupIsPutBack();
        var server = Server();
        var newest = Folder(_seal);
        AssertHeld(missing: 6);

        var result = _otter.Sync(restore: true);

        // Six files fetched because they were missing, and four because the file here was older
        // than the version that was synced. Nothing is written to the server: no deletion, and
        // no older version over a newer one. The server is not even asked the time.
        AssertClean(result);
        Assert.Equal(10, result.Downloaded);
        Assert.Equal(4, result.ConflictCopies);
        Assert.Equal(0, result.Uploaded + result.TombstonesSent + result.DeletedHere + result.RemovedFromServer);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 10)], _otter.Asked);
        Assert.Equal(server, Server());

        // Everything that was synced is here again, each file in its newest version ...
        Assert.All(missing, path => Assert.Equal("made after the backup: " + path, _otter.Box.Read(path)));
        Assert.Equal(newest, Folder(_otter).Where(file => !file.Contains(".conflict-", StringComparison.Ordinal)).ToList());

        // ... and each older version that the backup brought is kept beside it, as the backup had it.
        foreach (string path in older)
        {
            string copy = SyncPath.ConflictCopy(path, "OTTER-PC", _clock.UtcNow)!;
            Assert.Equal("the text of " + path, _otter.Box.Read(copy));
            Assert.Equal(Start.AddHours(-2), _otter.Box.Mtime(copy));
        }

        Assert.Equal(20, _otter.Box.Files().Count);

        // The next run sends the four copies like any new file, the other machine gets them,
        // and the newer versions stay on both.
        Pass(TimeSpan.FromMinutes(5));
        var next = _otter.Sync();

        AssertClean(next);
        Assert.Equal(4, next.Uploaded);
        Assert.Equal(0, next.Downloaded + next.TombstonesSent + next.ConflictCopies);
        AssertClean(_seal.Sync());
        Assert.Equal(Folder(_otter), Folder(_seal));
        Assert.Equal(newest, Folder(_seal).Where(file => !file.Contains(".conflict-", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void Restore_Missing_Files_after_a_backup_that_lost_its_file_times_keeps_the_newer_text_of_every_file_on_both_machines()
    {
        // The backup was put back by a tool that stamps every file with the present time. The
        // four older versions are then "changed, and newer than anything that was synced".
        var (missing, older) = ABackupIsPutBack(withItsFileTimes: false);
        var stamped = _clock.UtcNow;
        AssertHeld(missing: 6);

        var result = _otter.Sync(restore: true);

        // Six files fetched because they were missing. The four that were changed here are
        // fetched too, whatever their time, and settled like an edit on both sides. By the
        // times the file here wins, so it is sent: but the server's version, which holds the
        // newer text, is kept beside it as a conflict copy first.
        AssertClean(result);
        Assert.Equal(6, result.Downloaded);
        Assert.Equal(4, result.ConflictCopies);
        Assert.Equal(4, result.Uploaded);
        Assert.Equal(0, result.TombstonesSent + result.DeletedHere + result.RemovedFromServer);

        // The six that were missing, in the order of their paths; then each of the four: the
        // server's version is fetched, and the file here is sent only after that. The server is
        // asked the time once, before the first write.
        Assert.Equal(
            ["list", .. Enumerable.Repeat("download", 6), "download", "now", "upload", "download", "upload", "download", "upload", "download", "upload"],
            _otter.Asked);
        Assert.All(missing, path => Assert.Equal("made after the backup: " + path, _otter.Box.Read(path)));
        foreach (string path in older)
        {
            string copy = SyncPath.ConflictCopy(path, "OTTER-PC", _clock.UtcNow)!;
            Assert.Equal("the text of " + path, _otter.Box.Read(path));
            Assert.Equal(stamped, _otter.Box.Mtime(path));
            Assert.StartsWith("edited on the ", _otter.Box.Read(copy));
        }

        Assert.Equal(20, _otter.Box.Files().Count);

        // The next run sends the four copies, and the other machine takes all of it. The newer
        // text of each of the four files is then on both machines: as a conflict copy.
        Pass(TimeSpan.FromMinutes(5));
        var next = _otter.Sync();

        AssertClean(next);
        Assert.Equal(4, next.Uploaded);
        AssertClean(_seal.Sync());

        // The same files with the same texts on both. (Not the same times: the six files whose
        // text the backup did not change carry the stamp on the otter, and nothing was sent for them.)
        Assert.Equal(Texts(_otter), Texts(_seal));
        Assert.Equal(20, _seal.Box.Files().Count);
        foreach (var machine in new[] { _otter, _seal })
        {
            foreach (string path in older)
            {
                string stem = path[..^".md".Length];
                var versions = machine.Box.Files().Where(file => file == path || file.StartsWith(stem + ".conflict-", StringComparison.Ordinal)).Select(file => machine.Box.Read(file)!).ToList();
                Assert.Equal(2, versions.Count);
                Assert.Single(versions, text => text.StartsWith("edited on the ", StringComparison.Ordinal));
                Assert.Single(versions, text => text == "the text of " + path);
            }
        }
    }

    [Fact]
    public void Confirm_Sync_Deletions_after_a_backup_was_put_back_sends_what_the_owner_said_and_the_other_machine_keeps_its_newer_versions_as_copies()
    {
        var (missing, older) = ABackupIsPutBack();
        AssertHeld(missing: 6);

        // The owner says that the folder is as he wants it. The six deletions go out, and the
        // four older versions with them, as the edits they then are.
        var sent = _otter.Sync(confirm: true);

        AssertClean(sent);
        Assert.Equal(6, sent.TombstonesSent);
        Assert.Equal(4, sent.Uploaded);
        Assert.Equal(0, sent.Downloaded + sent.ConflictCopies);

        // The other machine deletes the six. It takes the four older versions, and keeps the
        // newer one of each beside it: a download never replaces a newer file unseen.
        var taken = _seal.Sync();

        AssertClean(taken);
        Assert.Equal(6, taken.DeletedHere);
        Assert.Equal(4, taken.Downloaded);
        Assert.Equal(4, taken.ConflictCopies);
        Assert.All(missing, path => Assert.False(_seal.Box.Exists(path)));
        foreach (string path in older)
        {
            Assert.Equal("the text of " + path, _seal.Box.Read(path));
            Assert.StartsWith("edited on the ", _seal.Box.Read(SyncPath.ConflictCopy(path, "SEAL-PC", _clock.UtcNow)!));
        }
    }

    [Theory]
    [InlineData(4, 10, false)]
    [InlineData(5, 20, false)]      // a quarter exactly is not more than a quarter
    [InlineData(20, 200, false)]    // twenty is not more than twenty
    [InlineData(5, 12, true)]
    [InlineData(5, 19, true)]
    [InlineData(21, 200, true)]
    public void A_run_is_held_for_more_than_twenty_missing_library_files_or_for_five_and_more_that_are_over_a_quarter_of_them(int missing, int of, bool held) =>
        AssertHeldOrSent(missing, of, held);

    [Theory]
    [InlineData(1, 1, false)]       // one file is no library: deleting the only file is deleting a file
    [InlineData(2, 2, true)]        // from two on, a library that is gone altogether is held
    [InlineData(4, 4, true)]        // fewer than five, and every file there was
    [InlineData(1, 2, false)]       // one of two is still here
    [InlineData(3, 4, false)]       // one of four is still here
    public void A_run_is_held_when_every_library_file_is_gone_and_there_were_at_least_two(int missing, int of, bool held) =>
        AssertHeldOrSent(missing, of, held);

    private void AssertHeldOrSent(int missing, int of, bool held)
    {
        var paths = Library(of, onTheSealToo: false);
        foreach (string path in paths.Take(missing))
        {
            _otter.Box.Delete(path);
        }

        var server = Server();

        var result = _otter.Sync();

        if (held)
        {
            Assert.Equal(SyncEnd.Held, result.End);
            Assert.Equal(missing, result.HeldDeletions);
            Assert.Equal(["list"], _otter.Asked);
            Assert.Equal(server, Server());
        }
        else
        {
            // Under the limits a missing file is a deleted file, and its deletion is sent.
            AssertClean(result);
            Assert.Equal(missing, result.TombstonesSent);
            Assert.Equal(0, result.HeldDeletions);
            Assert.Equal(["list", "now", .. Enumerable.Repeat("upload", missing)], _otter.Asked);
        }
    }

    [Fact]
    public void A_library_that_is_gone_altogether_is_held_also_when_no_deletion_would_be_sent_for_one_of_its_files()
    {
        var paths = Library(3);
        _seal.Box.Write(paths[0], "edited on the seal meanwhile, and longer", Start.AddMinutes(-5));
        AssertClean(_seal.Sync());
        Directory.Delete(_otter.Box.Full("library"), recursive: true);

        // All three are gone. For one of them the plan holds no deletion, because it was edited
        // on the other machine and the edit wins. The library is gone altogether all the same,
        // and the run is held with the two deletions it would have sent.
        AssertHeld(missing: 2);

        // With the owner's word the two deletions go out, and the edited file comes back.
        var sent = _otter.Sync(confirm: true);

        AssertClean(sent);
        Assert.Equal(2, sent.TombstonesSent);
        Assert.Equal(1, sent.Downloaded);
        Assert.Equal([paths[0]], _otter.Box.Files());
    }

    [Theory]
    [InlineData(12, true, true)]        // twelve of the forty the run could look at: more than a quarter
    [InlineData(10, true, false)]       // ten of forty: a quarter exactly is not more than a quarter
    [InlineData(12, false, false)]      // twelve of all hundred, when the run can look at them all
    public void The_share_is_taken_of_the_library_files_the_run_could_look_at(int missing, bool sixtyAreBehindAFolderThatCannotBeListed, bool held)
    {
        var visible = Enumerable.Range(1, 40).Select(number => FormattableString.Invariant($"library/snippets/note-{number:000}.md")).ToList();
        var hidden = Enumerable.Range(1, 60).Select(number => FormattableString.Invariant($"library/scripts/tool-{number:000}.cmd")).ToList();
        foreach (string path in visible.Concat(hidden))
        {
            _otter.Box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        AssertClean(_otter.Sync());
        foreach (string path in visible.Take(missing))
        {
            _otter.Box.Delete(path);
        }

        var server = Server();
        string locked = _otter.Box.Full("library/scripts");
        if (sixtyAreBehindAFolderThatCannotBeListed)
        {
            _otter.Box.DenyListing(locked);
        }

        SyncResult result;
        try
        {
            result = _otter.Sync();
        }
        finally
        {
            if (sixtyAreBehindAFolderThatCannotBeListed)
            {
                _otter.Box.Allow(locked);
            }
        }

        // Files the run could not look at are not files it saw to be here: they must not thin
        // the share that the missing ones are of.
        if (held)
        {
            Assert.Equal(SyncEnd.Held, result.End);
            Assert.Equal(missing, result.HeldDeletions);
            Assert.Equal(["list"], _otter.Asked);
            Assert.Equal(server, Server());
        }
        else
        {
            Assert.Equal(SyncEnd.Completed, result.End);
            Assert.Equal(missing, result.TombstonesSent);
            Assert.Equal(sixtyAreBehindAFolderThatCannotBeListed ? 60 : 0, result.LeftForLater);
            Assert.Equal(0, result.HeldDeletions);
        }
    }

    // A day of the otter's clipboard history with so many pictures, synced to both machines.
    private List<string> ClipboardDay(int pictures, out string day)
    {
        day = "clipboard/OTTER-PC/2026-10-03.jsonl";
        var content = Enumerable.Range(1, pictures).Select(number => FormattableString.Invariant($"clipboard/OTTER-PC/2026-10-03/picture-{number:00}.png")).ToList();
        _otter.Box.Write(day, "one line for each picture\n", Start.AddHours(-1));
        foreach (string path in content)
        {
            _otter.Box.Write(path, "png of " + path, Start.AddHours(-1));
        }

        AssertClean(_otter.Sync());
        AssertClean(_seal.Sync());
        return content;
    }

    [Fact]
    public void Clearing_the_clipboard_history_is_never_held_however_many_files_it_deletes()
    {
        var pictures = ClipboardDay(30, out string day);

        // Clear in the clipboard history: the day file is written again, and never removed,
        // and the content of every entry goes.
        _otter.Box.Write(day, "{\"delete\":\"all of them\"}\n", Start.AddMinutes(-1));
        foreach (string path in pictures)
        {
            _otter.Box.Delete(path);
        }

        var sent = _otter.Sync();

        AssertClean(sent);
        Assert.Equal(30, sent.TombstonesSent);
        Assert.Equal(0, sent.HeldDeletions);

        var taken = _seal.Sync();

        AssertClean(taken);
        Assert.Equal(30, taken.DeletedHere);
        Assert.Equal([day], _seal.Box.Files());
    }

    [Fact]
    public void The_clipboard_history_counts_neither_among_the_deletions_nor_among_the_files_they_are_held_against()
    {
        var paths = Library(12);
        var pictures = ClipboardDay(40, out string day);

        // Five of the twelve library files go: more than a quarter of the library, though a
        // small part of everything the record holds.
        foreach (string path in paths.Take(5))
        {
            _otter.Box.Delete(path);
        }

        AssertHeld(missing: 5);

        // One of them is back, so four are missing, and thirty pictures are deleted in the
        // clipboard history. Only the four count: the run is not held, and sends all of it.
        _otter.Box.Write(paths[0], "the text of " + paths[0], Start.AddHours(-2));
        _otter.Box.Write(day, "{\"delete\":\"thirty of them\"}\n", Start.AddMinutes(-1));
        foreach (string path in pictures.Take(30))
        {
            _otter.Box.Delete(path);
        }

        var sent = _otter.Sync();

        AssertClean(sent);
        Assert.Equal(34, sent.TombstonesSent);
        Assert.Equal(1, sent.Uploaded);
    }

    [Fact]
    public void A_held_run_does_none_of_the_other_things_it_had_to_do_and_writes_no_record_and_with_the_owners_word_it_does_them_all()
    {
        var paths = Library(12);

        // Everything a run can have to do, besides the deletions. On the seal: a file made, a
        // file edited, a file deleted, and its half of a conflict. All of it is on the server.
        _seal.Box.Write("library/made-on-the-seal.md", "made on the seal", Start.AddMinutes(-4));
        _seal.Box.Write(paths[10], "edited on the seal, and longer", Start.AddMinutes(-4));
        _seal.Box.Delete(paths[9]);
        _seal.Box.Write(paths[8], "the seal's edit, which is the later one", Start.AddMinutes(-4));
        AssertClean(_seal.Sync());

        // On the otter: a file made, a file edited, the other half of the conflict, and a file
        // written again with the same content, which an ordinary run notes in its record.
        _otter.Box.Write("library/made-on-the-otter.md", "made on the otter", Start.AddMinutes(-5));
        _otter.Box.Write(paths[11], "edited on the otter, and longer", Start.AddMinutes(-5));
        _otter.Box.Write(paths[8], "the otter's edit", Start.AddMinutes(-6));
        _otter.Box.Write(paths[7], "the text of " + paths[7], Start.AddMinutes(-3));

        // On the server: what an upload left behind days ago, which a run removes.
        string leftover = BlobCodec.TemporaryName(BlobOf(Sig));
        _server.Put(leftover, [1], Start.AddDays(-3));

        // And five of the twelve library files are gone from the otter.
        foreach (string path in paths.Take(5))
        {
            _otter.Box.Delete(path);
        }

        // (The seal is not asked to run here: its run would remove the leftover.)
        AssertHeld(missing: 5, andTheSealFindsNothingToDo: false);
        Assert.Contains(leftover, _server.Names);
        Assert.Equal(Start.AddHours(-2), _otter.State().Find(SyncPath.Key(paths[7]))!.MtimeUtc);

        // With the owner's word, the same run does every one of them.
        var result = _otter.Sync(confirm: true);

        AssertClean(result);
        Assert.Equal(5, result.TombstonesSent);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(3, result.Downloaded);
        Assert.Equal(1, result.DeletedHere);
        Assert.Equal(1, result.ConflictCopies);
        Assert.Equal(1, result.RemovedFromServer);
        Assert.DoesNotContain(leftover, _server.Names);
        Assert.Equal(Start.AddMinutes(-3), _otter.State().Find(SyncPath.Key(paths[7]))!.MtimeUtc);
    }

    [Fact]
    public void The_one_thing_a_held_run_may_remove_from_the_data_folder_is_a_leftover_temporary_file_of_syncs_own()
    {
        var paths = Library(12);
        foreach (string path in paths.Take(5))
        {
            _otter.Box.Delete(path);
        }

        // What a write of sync's own left behind two hours ago, when it was cut off, and a
        // temporary file of its own that is a minute old and may be in use.
        const string leftover = "library/.~beacon-sync-0123456789abcdef.tmp";
        const string inUse = "library/.~beacon-sync-fedcba9876543210.tmp";
        _otter.Box.Write(leftover, "half a file", Start.AddHours(-2), created: Start.AddHours(-2));
        _otter.Box.Write(inUse, "being written", Start.AddMinutes(-1), created: Start.AddMinutes(-1));
        var server = Server();
        var before = Folder(_otter);
        byte[] record = File.ReadAllBytes(_otter.StateFile);

        var result = _otter.Sync();

        // Held: nothing is sent, the record is not written, and every file of the owner's is
        // as it was. The look at the folder comes before the plan, in this run as in any other,
        // and it has removed the leftover: never a file of the owner's.
        Assert.Equal(SyncEnd.Held, result.End);
        Assert.Equal(5, result.HeldDeletions);
        Assert.Equal(["list"], _otter.Asked);
        Assert.Equal(server, Server());
        Assert.Equal(record, File.ReadAllBytes(_otter.StateFile));
        Assert.False(_otter.Box.Exists(leftover));
        Assert.Equal(before.Where(file => !file.StartsWith(leftover + " ", StringComparison.Ordinal)), Folder(_otter));
        Assert.Equal([inUse, .. paths.Skip(5)], _otter.Box.Files());
    }

    [Fact]
    public void With_nothing_held_Confirm_Sync_Deletions_is_an_ordinary_run_and_Restore_Missing_Files_brings_back_whatever_is_missing()
    {
        var paths = Library(12);
        _otter.Box.Delete(paths[0]);
        _otter.Box.Write(paths[1], "an ordinary edit", Start.AddMinutes(-1));

        // Nothing to hold: the run does what any run does. (A run goes by what it is told. It
        // is the service that gives this word only while sync is paused.)
        var confirmed = _otter.Sync(confirm: true);

        AssertClean(confirmed);
        Assert.Equal(1, confirmed.TombstonesSent);
        Assert.Equal(1, confirmed.Uploaded);

        // Two files are missing, fewer than the brake holds. An ordinary run would send their
        // deletions; this one fetches them back. The file that was deleted with the owner's
        // word stays deleted.
        //
        // An edit made just before the command is held against the server's version, as every
        // file is that was changed here: a run that restores does not go by a file's time to
        // tell an edit from an older version that was put back. The edit is the newer of the
        // two, so it is sent, and the server's version is kept beside it as a conflict copy.
        // That one needless copy is the price of the rule.
        _otter.Box.Delete(paths[2]);
        _otter.Box.Delete(paths[3]);
        _otter.Box.Write(paths[4], "another ordinary edit", Start.AddMinutes(-1));

        var restored = _otter.Sync(restore: true);

        AssertClean(restored);
        Assert.Equal(2, restored.Downloaded);
        Assert.Equal(1, restored.Uploaded);
        Assert.Equal(1, restored.ConflictCopies);
        Assert.Equal(0, restored.TombstonesSent + restored.DeletedHere);

        // The plan holds downloads only, so the server is not asked the time before them. The
        // paths in their order: the two that are fetched back, and then the edit, for which
        // the server's version is fetched, the time asked, and the file sent.
        Assert.Equal(["list", "download", "download", "download", "now", "upload"], _otter.Asked);
        Assert.Equal("the text of " + paths[2], _otter.Box.Read(paths[2]));
        Assert.Equal("the text of " + paths[3], _otter.Box.Read(paths[3]));
        Assert.Equal("another ordinary edit", _otter.Box.Read(paths[4]));
        Assert.Equal("the text of " + paths[4], _otter.Box.Read(SyncPath.ConflictCopy(paths[4], "OTTER-PC", _clock.UtcNow)!));
        Assert.False(_otter.Box.Exists(paths[0]));

        // The copy goes out with the next run, like any new file.
        var next = _otter.Sync();
        AssertClean(next);
        Assert.Equal(1, next.Uploaded);
        AssertClean(_seal.Sync());
        Assert.Equal(Folder(_otter), Folder(_seal));
    }

    [Fact]
    public void Restore_Missing_Files_leaves_deleted_what_the_other_machine_deleted_and_forgets_what_the_server_holds_nothing_for()
    {
        var paths = Library(12);

        // The seal deleted one file, so the server holds its deletion note. The blob of another
        // is gone from the server altogether. Both are missing on the otter, with four more.
        _seal.Box.Delete(paths[0]);
        AssertClean(_seal.Sync());
        _server.Remove(BlobOf(paths[1]));
        foreach (string path in paths.Take(6))
        {
            _otter.Box.Delete(path);
        }

        var result = _otter.Sync(restore: true);

        AssertClean(result);
        Assert.Equal(4, result.Downloaded);
        Assert.Equal(0, result.TombstonesSent + result.Uploaded + result.DeletedHere);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 5)], _otter.Asked);
        Assert.False(_otter.Box.Exists(paths[0]));
        Assert.False(_otter.Box.Exists(paths[1]));
        Assert.Equal(10, _otter.Box.Files().Count);
        Assert.Equal(SyncedKind.Tombstone, _otter.State().Find(SyncPath.Key(paths[0]))!.Kind);
        Assert.Null(_otter.State().Find(SyncPath.Key(paths[1])));
    }

    // ---- A pause, once begun, lasts until the owner has answered it ----
    //
    // Whether sync is paused is the service's to remember, in a mark on the disk. A run is
    // told, and while sync is paused it is held for the deletion of a single library file. It
    // says whether its plan left nothing to hold, which is what lets the service end a pause.

    [Fact]
    public void While_sync_is_paused_a_run_that_would_send_the_deletion_of_one_library_file_is_held()
    {
        var paths = Library(40);
        _otter.Box.Delete(paths[0]);

        AssertHeld(missing: 1, paused: true);
        AssertHeld(missing: 1, paused: true);

        // Not paused, one missing file of forty is a deleted file, and its deletion is sent.
        var sent = _otter.Sync();

        AssertClean(sent);
        Assert.Equal(1, sent.TombstonesSent);
        Assert.Equal(["list", "now", "upload"], _otter.Asked);
    }

    [Fact]
    public void While_sync_is_paused_a_run_whose_plan_holds_no_deletion_for_a_library_file_goes_on_as_usual_and_says_that_nothing_is_left_to_hold()
    {
        var paths = Library(12);

        // Everything but a deletion here: an edit and a new file on the otter, and from the
        // seal an edit and a deletion, which arrive and are not held.
        _otter.Box.Write(paths[0], "edited while sync was paused", Start.AddMinutes(-1));
        _otter.Box.Write("library/made-while-paused.md", "made while sync was paused", Start.AddMinutes(-1));
        _seal.Box.Write(paths[1], "edited on the seal, and longer", Start.AddMinutes(-2));
        _seal.Box.Delete(paths[2]);
        AssertClean(_seal.Sync());

        var result = _otter.Sync(paused: true);

        AssertClean(result);
        Assert.True(result.NothingToHold);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(1, result.DeletedHere);
        Assert.Equal(0, result.HeldDeletions + result.TombstonesSent);
        AssertClean(_seal.Sync());
        Assert.Equal(Folder(_otter), Folder(_seal));
    }

    [Fact]
    public void A_run_says_that_nothing_is_left_to_hold_only_when_its_plan_was_made_and_holds_no_deletion_for_a_library_file()
    {
        var paths = Library(12, onTheSealToo: false);

        // A run with nothing to do made its plan, and the plan holds no deletion.
        Assert.True(_otter.Sync().NothingToHold);

        // The server could not be listed, or Beacon is closing: no plan was made.
        _server.Fault = call => call == "list" ? new RemoteStoreException(RemoteFailure.Server) : null;
        var failed = _otter.Sync();
        _server.Fault = null;
        Assert.Equal(SyncEnd.Failed, failed.End);
        Assert.False(failed.NothingToHold);

        _otter.Cancelled = () => true;
        var stopped = _otter.Sync();
        _otter.Cancelled = () => false;
        Assert.Equal(SyncEnd.Stopped, stopped.End);
        Assert.False(stopped.NothingToHold);

        // A deletion that the plan holds, whether the run is held for it or sends it.
        _otter.Box.Delete(paths[0]);
        var held = _otter.Sync(paused: true);
        Assert.Equal(SyncEnd.Held, held.End);
        Assert.False(held.NothingToHold);

        var sent = _otter.Sync();
        AssertClean(sent);
        Assert.Equal(1, sent.TombstonesSent);
        Assert.False(sent.NothingToHold);

        // With that sent, nothing is left. And the plan of a run that restores holds no
        // deletion of a library file at all, whatever is missing: it is for the service to
        // know that such a run does not end a pause.
        Assert.True(_otter.Sync(paused: true).NothingToHold);
        _otter.Box.Delete(paths[1]);
        var restored = _otter.Sync(restore: true, paused: true);
        AssertClean(restored);
        Assert.Equal(1, restored.Downloaded);
        Assert.True(restored.NothingToHold);
    }

    [Fact]
    public void A_run_that_finds_no_data_folder_does_not_say_that_nothing_is_left_to_hold()
    {
        Library(12, onTheSealToo: false);
        Directory.Delete(_otter.Box.Data, recursive: true);

        var result = _otter.Sync(paused: true);

        Assert.Equal(SyncEnd.NoDataFolder, result.End);
        Assert.False(result.NothingToHold);
    }

    [Fact]
    public void A_run_that_could_not_look_at_a_part_of_the_library_does_not_say_that_nothing_is_left_to_hold()
    {
        // A file that is missing behind a folder that cannot be listed is not seen to be
        // missing: the plan holds nothing for it. That must not look like "the files are back".
        var paths = Library(12, onTheSealToo: false);
        _otter.Box.Delete(paths[0]);
        var server = Server();
        string locked = _otter.Box.Full("library/snippets");
        _otter.Box.DenyListing(locked);
        SyncResult result;
        try
        {
            result = _otter.Sync(paused: true);
        }
        finally
        {
            _otter.Box.Allow(locked);
        }

        Assert.Equal(SyncEnd.Completed, result.End);
        Assert.Equal(12, result.LeftForLater);
        Assert.False(result.NothingToHold);
        Assert.Equal(server, Server());

        // Once the folder can be listed, the missing file is seen, and held while sync is paused.
        AssertHeld(missing: 1, andTheSealFindsNothingToDo: false, paused: true);
    }

    [Fact]
    public void While_sync_is_paused_Restore_Missing_Files_fetches_and_Confirm_Sync_Deletions_sends_as_they_do_at_any_time()
    {
        var paths = Library(40);
        var before = Folder(_otter);
        _otter.Box.Delete(paths[0]);
        _otter.Box.Delete(paths[1]);
        AssertHeld(missing: 2, paused: true);

        // The run that restores holds no deletion in its plan, so there is nothing to hold it for.
        var restored = _otter.Sync(restore: true, paused: true);

        AssertClean(restored);
        Assert.Equal(2, restored.Downloaded);
        Assert.Equal(["list", "download", "download"], _otter.Asked);
        Assert.Equal(before, Folder(_otter));

        // The run the owner allowed sends the deletions, paused or not.
        _otter.Box.Delete(paths[2]);
        AssertHeld(missing: 1, paused: true);

        var sent = _otter.Sync(confirm: true, paused: true);

        AssertClean(sent);
        Assert.Equal(1, sent.TombstonesSent);
        Assert.Equal(["list", "now", "upload"], _otter.Asked);
        var taken = _seal.Sync();
        AssertClean(taken);
        Assert.Equal(1, taken.DeletedHere);
        Assert.Equal(Folder(_otter), Folder(_seal));
    }

    [Fact]
    public void Clearing_the_clipboard_history_is_not_held_while_sync_is_paused_either()
    {
        Library(12);
        var pictures = ClipboardDay(30, out string day);
        _otter.Box.Write(day, "{\"delete\":\"all of them\"}\n", Start.AddMinutes(-1));
        foreach (string path in pictures)
        {
            _otter.Box.Delete(path);
        }

        var sent = _otter.Sync(paused: true);

        // Deletions in this machine's own clipboard folders are the owner's doing in Beacon:
        // they are not what a pause holds, and they leave nothing to hold.
        AssertClean(sent);
        Assert.Equal(30, sent.TombstonesSent);
        Assert.Equal(0, sent.HeldDeletions);
        Assert.True(sent.NothingToHold);

        var taken = _seal.Sync();

        AssertClean(taken);
        Assert.Equal(30, taken.DeletedHere);
    }

    // ---- What reaches the log ----

    [Fact]
    public void The_log_holds_counts_and_blob_names_and_no_path_no_file_name_and_no_content()
    {
        _otter.Box.Write("library/snippets/private-walrus-note.md", "the secret text of the walrus", Start.AddHours(-2));
        _otter.Box.Write("clipboard/OTTER-PC/2026-10-03.jsonl", "copied secret\n", Start.AddHours(-1));
        _otter.Sync();
        _seal.Box.Write("library/snippets/private-walrus-note.md", "another secret text", Start.AddHours(-1));
        _seal.Sync();
        _server.Put(new string('e', 64), [1, 2, 3]);
        _seal.Sync();
        _otter.Box.Delete("library/snippets/private-walrus-note.md");
        _otter.Sync();

        var lines = _otter.Log.Concat(_seal.Log).ToList();

        Assert.Contains("Sync finished in 0 s: 2 sent, 0 fetched, 0 deleted here, 0 deletions sent, 0 conflict copies kept, 0 removed from the server, 0 blobs that could not be used, 0 left for the next run, 0 over the size limit and not synced", lines);
        Assert.Contains($"A blob could not be used and was skipped (NotABlob): {new string('e', 64)}", lines);
        foreach (string word in new[] { "walrus", "private", "secret", "snippets", "library", "clipboard", "OTTER", "SEAL", ".md", ".jsonl", "conflict-" })
        {
            Assert.DoesNotContain(lines, line => line.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    // The calls a piece of work makes to the server.
    private List<string> Calls(Action work)
    {
        _server.Calls.Clear();
        work();
        return [.. _server.Calls];
    }
}
