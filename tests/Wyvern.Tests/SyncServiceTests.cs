using System.Globalization;
using System.Text;
using System.Text.Json;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Settings;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// When sync runs, when it must not, and what the owner is told. The server lives in memory and
/// the connector only counts: no test here can open a connection. The clock's timer never runs
/// out by itself; a test fires it. A run is done on the test's own thread, except in the two
/// tests of closing that hold a run with a signal: there the run and the closing each have a
/// thread of their own, never a pool thread, and the test waits for signals only.
/// </summary>
public sealed class SyncServiceTests : IDisposable
{
    private const string Host = "secret-otter.example.org";
    private const string User = "secret-walrus";
    private const string KeyPath = @"C:\SecretKeys\otter_ed25519";
    private const string Sig = "library/snippets/sig.md";
    private const string BadPortJson = "{ \"host\": \"secret-otter.example.org\", \"port\": \"twenty-two\" }";
    private const string BadPortNote = "sync.json cannot be used. The port must be a whole number from 1 to 65535. Sync is off until it is put right.";
    private const string OtherScopeLine = "The record of the last sync was made for another server, sync key or data folder, or for this data folder before it was emptied or replaced, so every file is compared afresh";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Master = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private readonly SyncSandbox _box = new();
    private readonly FakeClock _clock = new() { UtcNow = Start };
    private readonly ManualTimeProvider _time = new();
    private readonly MemoryRemoteStore _server = new() { Clock = Start };
    private readonly MemoryConnector _connector;
    private readonly ConnectingHook _hook;
    private readonly FakeKeyProtector _protector = new();
    private readonly WyvernPaths _paths;
    private readonly List<string> _notes = [];
    private readonly List<string> _log = [];
    private readonly SyncService _service;
    private int _statusChanges;
    private int _clipboardChanges;

    public SyncServiceTests()
    {
        _connector = new MemoryConnector(_server);
        _hook = new ConnectingHook(_connector);
        _paths = new WyvernPaths(_box.Root);
        _service = NewService(start: Inline);
    }

    public void Dispose()
    {
        _service.Dispose();
        _box.Dispose();
        _sealBox?.Dispose();
    }

    private static Task Inline(Action run)
    {
        run();
        return Task.CompletedTask;
    }

    // A run on a thread of its own, for the tests that hold a run with a signal: a pool thread
    // that is held makes every other test wait that gives a pool thread something to do. The
    // thread is kept, so that a test can wait for the run's end without waiting on a task.
    private Thread? _runThread;

    private Task OnItsOwnThread(Action run)
    {
        var done = new TaskCompletionSource();
        _runThread = new Thread(() =>
        {
            try
            {
                run();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "a sync run of a test",
        };
        _runThread.Start();
        return done.Task;
    }

    /// <summary>
    /// Stands between the service and the connector, and lets a test do something at the moment
    /// a run asks for its connection, before the connector answers: while the run connects.
    /// </summary>
    private sealed class ConnectingHook(IRemoteConnector inner) : IRemoteConnector
    {
        public Action? Connecting { get; set; }

        public KeyFileState CheckKeyFile(string path, string? passphrase) => inner.CheckKeyFile(path, passphrase);

        public HostKeyInfo ReadHostKey(SyncConfig config) => inner.ReadHostKey(config);

        public IRemoteStore Open(SyncConfig config, string? passphrase)
        {
            // Once: what it does may itself start a run.
            var connecting = Connecting;
            Connecting = null;
            connecting?.Invoke();
            return inner.Open(config, passphrase);
        }
    }

    private SyncService NewService(Func<Action, Task>? start, WyvernPaths? paths = null) => new(
        paths ?? _paths,
        _hook,
        _protector,
        _clock,
        (text, _) => { lock (_log) { _log.Add(text); } },
        note => { lock (_notes) { _notes.Add(note); } },
        () => Interlocked.Increment(ref _statusChanges),
        () => Interlocked.Increment(ref _clipboardChanges),
        _time,
        start);

    private ManualTimer Timer => _time.Timers[0];

    private static ClipboardOptions Device(string name = "OTTER-PC") =>
        ClipboardOptions.From(new WyvernSettings { DeviceName = JsonSerializer.SerializeToElement(name) }, "MADE-UP-MACHINE");

    private void Configure(SyncService? service = null, int minutes = 5) => (service ?? _service).Configure(
        new DataPaths(_box.Data),
        Device(),
        SyncOptions.From(new WyvernSettings { SyncIntervalMinutes = JsonSerializer.SerializeToElement(minutes) }));

    // What Sync Setup leaves behind: sync.json with the server's key pinned, and the sync key.
    // The key file is only ever a path here: nothing in these tests opens it.
    private void SetUp(string? fingerprint = MemoryConnector.ServerKey, WyvernPaths? paths = null, string keyPath = KeyPath)
    {
        paths ??= _paths;
        Assert.True(SyncConfig.TryCreate(Host, "22", User, keyPath, "/home/secret-walrus/wyvern", fingerprint, out var config, out string problem), problem);
        config!.Save(paths.SyncFile);
        new KeyStore(paths.KeyFile, paths.PassphraseFile, _protector).WriteMaster(Master);
    }

    private string Tray(SyncService? service = null) => (service ?? _service).Status.Tooltip(_clock.UtcNow, TimeZoneInfo.Utc);

    private void Ready()
    {
        SetUp();
        Configure();
    }

    private string BlobOf(string path)
    {
        using var keys = SyncKeys.FromMaster(Master);
        return keys.BlobName(SyncPath.Key(path));
    }

    // ---- Sync that is not set up ----

    [Fact]
    public void With_no_sync_json_no_connection_is_made_and_nothing_is_said()
    {
        const string words = "Sync is not set up, so no connection is made. Connections opened by this run of Wyvern: ";
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        int before = SftpRemoteStore.ConnectionsOpened;
        Configure();

        Timer.Fire();
        Timer.Fire();

        int after = SftpRemoteStore.ConnectionsOpened;
        Assert.Equal(SyncStatus.Off, _service.Status);
        Assert.Equal(0, _connector.Opens + _connector.HostKeyReads + _connector.KeyFileChecks);
        Assert.Empty(_server.Calls);
        Assert.Empty(_notes);
        Assert.Equal(0, _statusChanges);
        Assert.False(File.Exists(_paths.SyncStateFile));
        Assert.Equal([Sig], _box.Files());
        Assert.Equal("", _service.Status.Tooltip(_clock.UtcNow, TimeZoneInfo.Utc));

        // The count in the log is the one the service read. It is held between two readings of
        // this test's own, and not against a third: the count is one for the whole process, and
        // the tests that talk to a real server, when the owner turns them on, move it meanwhile.
        string line = Assert.Single(_log);
        Assert.StartsWith(words, line, StringComparison.Ordinal);
        Assert.InRange(int.Parse(line[words.Length..], NumberStyles.None, CultureInfo.InvariantCulture), before, after);
    }

    [Fact]
    public void Sync_Now_without_a_setup_says_so_and_makes_no_connection()
    {
        Configure();

        _service.SyncNow();

        Assert.Equal([SyncService.NotSetUpNote], _notes);
        Assert.Equal(0, _connector.Opens);
    }

    [Fact]
    public void Sync_Now_before_Wyvern_has_a_data_folder_says_so()
    {
        SetUp();

        _service.SyncNow();

        Assert.Equal([SyncService.NoDataFolderNote], _notes);
        Assert.Equal(0, _connector.Opens);
    }

    // ---- The ordinary course ----

    [Fact]
    public void The_first_run_begins_a_short_while_after_Wyvern_starts()
    {
        Assert.Null(Timer.Due);

        Configure();

        Assert.Equal(SyncService.StartDelay, Timer.Due);
        Assert.Equal(0, _connector.Opens);
    }

    [Fact]
    public void A_run_by_the_clock_syncs_says_nothing_and_sets_the_clock_for_the_next_one()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();

        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start), _service.Status);
        Assert.Equal([BlobOf(Sig)], _server.Names);
        Assert.Empty(_notes);

        // Twice: "has not run yet" when the first run began, and "synced" when it ended.
        Assert.Equal(2, _statusChanges);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        Assert.Equal(0, _connector.OpenNow);
        Assert.True(File.Exists(_paths.SyncStateFile));
    }

    [Fact]
    public void The_interval_is_the_one_from_settings_json()
    {
        SetUp();
        Configure(minutes: 20);

        Timer.Fire();

        Assert.Equal(TimeSpan.FromMinutes(20), Timer.Due);
    }

    [Fact]
    public void Sync_Now_runs_at_once_and_says_how_it_went()
    {
        Ready();

        _service.SyncNow();

        Assert.Equal([SyncService.DoneNote], _notes);
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    [Fact]
    public void A_second_run_is_never_started_while_one_is_at_work()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        _server.Uploaded = _ =>
        {
            _service.SyncNow();
            Timer.Fire();
        };

        _service.SyncNow();

        Assert.Equal(1, _connector.Opens);
        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);

        // What the owner asked for while the run was at work is not lost: a run follows shortly,
        // and it says how it went, as the run he asked for.
        Assert.Equal(SyncService.StartDelay, Timer.Due);
        Timer.Fire();
        Assert.Equal(2, _connector.Opens);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote, SyncService.DoneNote], _notes);

        // Once: the run after that comes by the clock, and says nothing while things are well.
        Timer.Fire();
        Assert.Equal(3, _connector.Opens);
        Assert.Equal(3, _notes.Count);
    }

    [Fact]
    public void The_history_is_told_when_a_clipboard_file_arrived_and_not_for_the_library()
    {
        Ready();
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Timer.Fire();
        Assert.Equal(0, _clipboardChanges);

        // The other machine's history arrives.
        using var keys = SyncKeys.FromMaster(Master);
        const string day = "clipboard/SEAL-PC/2026-10-03.jsonl";
        string blob = keys.BlobName(SyncPath.Key(day));
        byte[] lines = Encoding.UTF8.GetBytes("{\"id\":\"1\"}\n");
        _server.Put(blob, BlobCodec.Seal(keys, blob, new BlobHeader(day, Start, false, BlobCodec.Hash(lines)), lines));

        Timer.Fire();

        Assert.Equal(1, _clipboardChanges);
        Assert.True(_box.Exists(day));
    }

    [Fact]
    public void A_run_that_kept_both_versions_of_a_file_says_so_even_when_nobody_asked()
    {
        Ready();
        _box.Write(Sig, "edited here", Start.AddMinutes(-5));
        using var keys = SyncKeys.FromMaster(Master);
        byte[] theirs = Encoding.UTF8.GetBytes("edited on the other machine");
        _server.Put(BlobOf(Sig), BlobCodec.Seal(keys, BlobOf(Sig), new BlobHeader(Sig, Start.AddMinutes(-50), false, BlobCodec.Hash(theirs)), theirs));

        Timer.Fire();

        Assert.Equal([SyncService.ConflictNote(1)], _notes);
        // True whichever way the copy came to be kept: edited on both machines, or the server's
        // version older than the file here, where only one machine edited.
        Assert.Equal("Sync kept two versions of a file. The other version is beside it, with \"conflict\" in its name.", _notes[0]);
        Assert.Equal("Sync kept two versions of 3 files. The other versions are beside them, with \"conflict\" in their names.", SyncService.ConflictNote(3));
        Assert.Equal(2, _box.Files().Count(file => file != SyncFolderId.FileName));
    }

    [Fact]
    public void A_folder_this_machine_wrote_under_an_earlier_name_is_synced_as_its_own()
    {
        ClipFolders.Remember(_paths.ClipboardFoldersFile, "OLD-OTTER");
        _box.Write("clipboard/OLD-OTTER/2026-10-03.jsonl", "lines\n", Start.AddHours(-1));
        _box.Write("clipboard/SEAL-PC/2026-10-03.jsonl", "not this machine's to send\n", Start.AddHours(-1));
        Ready();

        Timer.Fire();

        Assert.Equal([BlobOf("clipboard/OLD-OTTER/2026-10-03.jsonl")], _server.Names);
    }

    [Fact]
    public void A_record_made_for_another_data_folder_is_not_used_so_an_empty_folder_deletes_nothing()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        Timer.Fire();

        // "dataDir" now names another folder, which is empty.
        string elsewhere = Path.Combine(_box.Root, "other-data");
        Directory.CreateDirectory(elsewhere);
        _service.Configure(new DataPaths(elsewhere), Device(), SyncOptions.Default);
        Timer.Fire();

        Assert.Contains(OtherScopeLine, _log);
        Assert.Equal("walrus", File.ReadAllText(Path.Combine(elsewhere, "library", "snippets", "sig.md")));
        Assert.Equal(1, _server.Count("upload"));
    }

    // ---- The record belongs to the data folder as it stands on the disk ----

    private string IdFile => Path.Combine(_box.Data, SyncFolderId.FileName);

    private string ScopeInTheRecord()
    {
        using var record = JsonDocument.Parse(File.ReadAllBytes(_paths.SyncStateFile));
        return record.RootElement.GetProperty("scope").GetString()!;
    }

    [Fact]
    public void A_run_gives_the_data_folder_an_id_that_is_never_uploaded_and_the_next_run_uses_the_same_record()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();

        Timer.Fire();

        Assert.True(File.Exists(IdFile));
        string id = File.ReadAllText(IdFile);
        Assert.Equal(32, id.Length);
        Assert.Equal([BlobOf(Sig)], _server.Names);
        Assert.Equal(["list", "now", "upload " + BlobOf(Sig)], _server.Calls);
        Assert.Equal(ScopeNow(), ScopeInTheRecord());
        string scope = ScopeInTheRecord();

        // A second run, with one more file: the same id, the same scope, and the record is used,
        // so the file that is already synced is neither fetched to be compared nor sent again.
        _box.Write("library/snippets/other.md", "seal", Start.AddHours(-1));
        Timer.Fire();

        Assert.Equal(id, File.ReadAllText(IdFile));
        Assert.Equal(scope, ScopeInTheRecord());
        Assert.Equal(0, _server.Count("download"));
        Assert.Equal(2, _server.Count("upload"));
        Assert.Equal(2, _server.Names.Count);
        Assert.DoesNotContain(OtherScopeLine, _log);
        Assert.Equal([SyncFolderId.FileName, "library/snippets/other.md", Sig], _box.Files());
    }

    [Fact]
    public void An_emptied_data_folder_is_not_the_folder_of_the_record_so_nothing_is_deleted_and_the_servers_files_come_back()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        _box.Write("library/scripts/tidy.ps1", "seal", Start.AddHours(-2));
        Ready();
        Timer.Fire();
        Assert.Equal(2, _server.Count("upload"));
        string scope = ScopeInTheRecord();

        // Emptied by hand "to fetch everything again", or a drive that was replaced: the same
        // path, with nothing in it. The record of what was synced is still beside settings.json.
        Directory.Delete(_box.Data, recursive: true);
        Directory.CreateDirectory(_box.Data);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Timer.Fire();

        // No deletion went up, nothing was deleted on the server, and both files are back.
        Assert.Equal(2, _server.Count("upload"));
        Assert.Equal(0, _server.Count("delete"));
        Assert.Equal(2, _server.Names.Count);
        Assert.Equal("walrus", _box.Read(Sig));
        Assert.Equal("seal", _box.Read("library/scripts/tidy.ps1"));
        Assert.Contains(OtherScopeLine, _log);
        Assert.NotEqual(scope, ScopeInTheRecord());
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(5)), _service.Status);
    }

    [Fact]
    public void A_data_folder_whose_id_was_taken_away_is_treated_the_same_way_and_a_file_deleted_here_comes_back_rather_than_be_deleted_there()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        Timer.Fire();

        // The id is gone and so is a file: Wyvern cannot tell this folder from another one.
        File.Delete(IdFile);
        _box.Delete(Sig);
        Timer.Fire();

        Assert.Equal(1, _server.Count("upload"));
        Assert.Equal("walrus", _box.Read(Sig));
        Assert.True(File.Exists(IdFile));
    }

    [Fact]
    public void With_an_id_file_that_can_be_neither_read_nor_made_a_run_does_nothing_and_the_next_one_tries_again()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");
        using (new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // The first run by the clock that finds it so shows it and logs it at once, and says
            // nothing yet: a file that is only held for a moment gives the same failure.
            Timer.Fire();
            Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.FolderIdUnusable), _service.Status);
            Assert.Equal("Sync did nothing: Wyvern could not read or write its id file in the data folder", Tray());
            Assert.Equal(["Sync did nothing because the id file of the data folder could not be read or made"], _log);
            Assert.Empty(_notes);

            // By the clock it is said at the second run in a row, once, however many runs find it so.
            Timer.Fire();
            Assert.Equal([SyncService.FolderIdNote], _notes);
            Timer.Fire();
            Assert.Equal([SyncService.FolderIdNote], _notes);

            // An owner who asks is answered all the same.
            _service.SyncNow();
        }

        // Not a stop: shown in a line of its own, and looked at again at the ordinary pace.
        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.FolderIdUnusable), _service.Status);
        Assert.Equal("Sync did nothing: Wyvern could not read or write its id file in the data folder", Tray());
        Assert.Equal(0, _connector.Opens);
        Assert.Equal([SyncService.FolderIdNote, SyncService.FolderIdNote], _notes);
        Assert.Equal("Sync did nothing: Wyvern could not read or write its id file in the data folder.", SyncService.FolderIdNote);

        // The log has it once for the four runs, and not once a run.
        Assert.Equal(["Sync did nothing because the id file of the data folder could not be read or made"], _log);
        Assert.False(File.Exists(_paths.SyncStateFile));
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);

        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal("0123456789abcdef0123456789abcdef", File.ReadAllText(IdFile));

        // Held again after a run that went well, it is a change again: logged at once, and said
        // once more at the second run in a row.
        using (new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Timer.Fire();
            Assert.Equal(2, _log.Count(line => line == "Sync did nothing because the id file of the data folder could not be read or made"));
            Assert.Equal([SyncService.FolderIdNote, SyncService.FolderIdNote], _notes);

            Timer.Fire();
        }

        Assert.Equal(2, _log.Count(line => line == "Sync did nothing because the id file of the data folder could not be read or made"));
        Assert.Equal([SyncService.FolderIdNote, SyncService.FolderIdNote, SyncService.FolderIdNote], _notes);
    }

    [Fact]
    public void An_id_file_that_is_only_held_for_a_moment_costs_one_run_and_an_owner_who_asked_is_answered_at_once()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");
        using (new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _service.SyncNow();
        }

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.FolderIdUnusable), _service.Status);
        Assert.Equal([SyncService.FolderIdNote], _notes);
        Assert.Equal(0, _connector.Opens);

        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal([BlobOf(Sig)], _server.Names);
    }

    [Fact]
    public void An_id_file_that_is_held_while_one_run_by_the_clock_looks_is_shown_and_logged_and_nothing_is_said()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");
        using (new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Timer.Fire();
        }

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.FolderIdUnusable), _service.Status);
        Assert.Equal("Sync did nothing: Wyvern could not read or write its id file in the data folder", Tray());
        Assert.Equal(["Sync did nothing because the id file of the data folder could not be read or made"], _log);
        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_notes);

        // Let go of, the next run syncs, and the owner never heard of it.
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal([BlobOf(Sig)], _server.Names);
        Assert.Empty(_notes);
    }

    [Fact]
    public void An_owner_who_was_answered_about_the_id_file_is_not_told_again_by_the_clock()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");
        using (new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _service.SyncNow();
            Assert.Equal([SyncService.FolderIdNote], _notes);

            // The second run in a row, and the third: he has heard it.
            Timer.Fire();
            Timer.Fire();
        }

        Assert.Equal([SyncService.FolderIdNote], _notes);
        Assert.Equal(["Sync did nothing because the id file of the data folder could not be read or made"], _log);
    }

    [Fact]
    public void While_the_data_folder_is_away_its_record_is_neither_used_nor_replaced_and_it_serves_again_when_the_folder_is_back()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        Timer.Fire();
        byte[] record = File.ReadAllBytes(_paths.SyncStateFile);

        // A data folder on a drive that is not there for a while: nothing has the path.
        string away = _box.Data + "-away";
        Directory.Move(_box.Data, away);
        Timer.Fire();

        // Reported as it always was. A folder that is not there has no id, and the record is not
        // held against that: it is not called another folder's, and it stays as it is.
        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, Start), _service.Status);
        Assert.Contains("Sync did nothing because the data folder could not be listed", _log);
        Assert.DoesNotContain(OtherScopeLine, _log);
        Assert.Equal(record, File.ReadAllBytes(_paths.SyncStateFile));
        Assert.False(Directory.Exists(_box.Data));
        Assert.Empty(_notes);

        // Back, with its id in it: the record is this folder's, and nothing is compared afresh.
        Directory.Move(away, _box.Data);
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.DoesNotContain(OtherScopeLine, _log);
        Assert.Equal(0, _server.Count("download"));
        Assert.Equal(1, _server.Count("upload"));
        Assert.Equal(record, File.ReadAllBytes(_paths.SyncStateFile));
    }

    [Fact]
    public void While_the_data_folder_is_away_no_connection_is_made_so_a_folder_that_comes_back_meanwhile_is_not_worked_from_an_empty_record()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        Timer.Fire();
        byte[] record = File.ReadAllBytes(_paths.SyncStateFile);
        int calls = _server.Calls.Count;
        Assert.Equal(1, _connector.Opens);

        // The drive is not there when the run looks, and is reconnected at the moment the run
        // would ask for its connection: the hook runs only if a connection is asked for.
        string away = _box.Data + "-away";
        Directory.Move(_box.Data, away);
        bool asked = false;
        _hook.Connecting = () =>
        {
            asked = true;
            Directory.Move(away, _box.Data);
        };

        Timer.Fire();

        // The run ended where it found no folder: nothing was connected to, the server was asked
        // nothing, and the record of the last sync is as it was.
        Assert.False(asked);
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(calls, _server.Calls.Count);
        Assert.Equal(record, File.ReadAllBytes(_paths.SyncStateFile));
        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, Start), _service.Status);
        Assert.Contains("Sync did nothing because the data folder could not be listed", _log);
        Assert.DoesNotContain(OtherScopeLine, _log);
        Assert.Empty(_notes);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);

        // An owner who asks meanwhile is told, in the same words as ever.
        _hook.Connecting = null;
        _service.SyncNow();
        Assert.Equal([SyncService.DataFolderNote], _notes);
        Assert.Equal(1, _connector.Opens);

        // Back, the folder is the one of the record: nothing is fetched to be compared.
        Directory.Move(away, _box.Data);
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(2, _connector.Opens);
        Assert.Equal(0, _server.Count("download"));
        Assert.DoesNotContain(OtherScopeLine, _log);
        Assert.Equal(record, File.ReadAllBytes(_paths.SyncStateFile));
    }

    // ---- The list of this machine's own clipboard folders ----

    private const string OldDay = "clipboard/OLD-OTTER/2026-10-03.jsonl";

    [Theory]
    [InlineData("{ not a list")]
    [InlineData("[\"OLD-OTTER\"]")]
    [InlineData("{ \"folders\": \"OLD-OTTER\" }")]
    public void With_a_list_of_clipboard_folders_that_cannot_be_understood_a_run_does_nothing_and_says_so_once(string damaged)
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start), _service.Status);
        int opens = _connector.Opens;
        int calls = _server.Calls.Count;

        File.WriteAllText(_paths.ClipboardFoldersFile, damaged);
        _box.Write("library/snippets/other.md", "seal", Start.AddHours(-1));
        Timer.Fire();

        // The first run that finds it so shows it and logs it at once, and says nothing yet: a
        // file that is only held for a moment is unreadable too, and the sentence would then
        // advise deleting a good file.
        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.ClipboardFoldersUnreadable, Start), _service.Status);
        Assert.Equal("Sync did nothing: clipboard-folders.json cannot be understood. Correct it", Tray());
        Assert.Single(_log, line => line == "Sync did nothing because clipboard-folders.json could not be understood");
        Assert.Empty(_notes);

        // The second run in a row says it, once, and the runs after that say nothing more.
        Timer.Fire();
        Assert.Equal([SyncService.ClipboardFoldersNote], _notes);
        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.ClipboardFoldersUnreadable, Start), _service.Status);
        Assert.Equal([SyncService.ClipboardFoldersNote], _notes);
        Assert.Equal(
            "Sync did nothing: clipboard-folders.json cannot be understood. Correct it, or delete it only if this PC has always had its present name.",
            SyncService.ClipboardFoldersNote);
        Assert.Equal(opens, _connector.Opens);
        Assert.Equal(calls, _server.Calls.Count);
        Assert.Single(_log, line => line == "Sync did nothing because clipboard-folders.json could not be understood");
        Assert.Equal(damaged, File.ReadAllText(_paths.ClipboardFoldersFile));

        // Not a stop that waits for the owner: the next turn of the clock looks again, at the ordinary pace.
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        File.Delete(_paths.ClipboardFoldersFile);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(5)), _service.Status);
        Assert.Equal(2, _server.Names.Count);

        // Damaged again after a run that completed without it, it is news again: at the second run.
        File.WriteAllText(_paths.ClipboardFoldersFile, damaged);
        Timer.Fire();
        Assert.Equal([SyncService.ClipboardFoldersNote], _notes);
        Timer.Fire();
        Assert.Equal([SyncService.ClipboardFoldersNote, SyncService.ClipboardFoldersNote], _notes);
    }

    [Fact]
    public void A_list_of_clipboard_folders_that_is_only_held_for_a_moment_costs_one_run_and_an_owner_who_asked_is_answered_at_once()
    {
        ClipFolders.Remember(_paths.ClipboardFoldersFile, "OLD-OTTER");
        _box.Write(OldDay, "lines\n", Start.AddHours(-1));
        Ready();
        using (new FileStream(_paths.ClipboardFoldersFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _service.SyncNow();
        }

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.ClipboardFoldersUnreadable), _service.Status);
        Assert.Equal([SyncService.ClipboardFoldersNote], _notes);
        Assert.Equal(0, _connector.Opens);

        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal([BlobOf(OldDay)], _server.Names);
    }

    [Fact]
    public void A_list_of_clipboard_folders_that_is_held_while_one_run_by_the_clock_looks_is_shown_and_logged_and_nothing_is_said()
    {
        ClipFolders.Remember(_paths.ClipboardFoldersFile, "OLD-OTTER");
        _box.Write(OldDay, "lines\n", Start.AddHours(-1));
        Ready();
        using (new FileStream(_paths.ClipboardFoldersFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Timer.Fire();
        }

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.ClipboardFoldersUnreadable), _service.Status);
        Assert.Equal("Sync did nothing: clipboard-folders.json cannot be understood. Correct it", Tray());
        Assert.Equal(["Sync did nothing because clipboard-folders.json could not be understood"], _log);
        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_notes);

        // Let go of, the next run syncs, and the owner never heard of it.
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal([BlobOf(OldDay)], _server.Names);
        Assert.Empty(_notes);
    }

    [Fact]
    public void An_owner_who_was_answered_about_the_list_of_clipboard_folders_is_not_told_again_by_the_clock()
    {
        Ready();
        File.WriteAllText(_paths.ClipboardFoldersFile, "{ not a list");

        _service.SyncNow();
        Assert.Equal([SyncService.ClipboardFoldersNote], _notes);

        // The second run in a row, and the third: he has heard it.
        Timer.Fire();
        Timer.Fire();
        Assert.Equal([SyncService.ClipboardFoldersNote], _notes);
    }

    [Fact]
    public void With_a_damaged_list_an_earlier_folder_of_this_machine_is_not_taken_for_another_machines_and_written_over()
    {
        // This machine wrote a day under its earlier name, and that day is on the server.
        ClipFolders.Remember(_paths.ClipboardFoldersFile, "OLD-OTTER");
        _box.Write(OldDay, "{\"id\":\"1\"}\n{\"id\":\"2\"}\n", Start.AddHours(-2));
        Ready();
        Timer.Fire();
        Assert.Equal([BlobOf(OldDay)], _server.Names);

        // An entry is deleted here and the day file scrubbed, which only this machine may send.
        // Before the next run, a slip in a hand edit makes the list unreadable.
        _box.Write(OldDay, "{\"id\":\"1\"}\n", Start.AddHours(-1));
        File.WriteAllText(_paths.ClipboardFoldersFile, "{ \"folders\": [\"OLD-OTTER\" }");
        Timer.Fire();

        // The scrubbed file stays as it is: the server's older copy, with the deleted entry, is not fetched over it.
        Assert.Equal("{\"id\":\"1\"}\n", _box.Read(OldDay));
        Assert.Equal(0, _server.Count("download"));
        Assert.Equal(0, _clipboardChanges);

        // The list put right, the scrubbed day goes up as this machine's own.
        File.WriteAllText(_paths.ClipboardFoldersFile, "{ \"folders\": [\"OLD-OTTER\"] }");
        Timer.Fire();

        Assert.Equal("{\"id\":\"1\"}\n", _box.Read(OldDay));
        Assert.Equal(2, _server.Count("upload"));
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    [Fact]
    public void The_present_name_is_this_machines_own_when_the_list_holds_only_an_earlier_one()
    {
        const string today = "clipboard/OTTER-PC/2026-10-03.jsonl";
        ClipFolders.Remember(_paths.ClipboardFoldersFile, "OLD-OTTER");
        _box.Write(OldDay, "lines\n", Start.AddHours(-1));
        _box.Write(today, "more lines\n", Start.AddHours(-1));
        _box.Write("clipboard/SEAL-PC/2026-10-03.jsonl", "not this machine's to send\n", Start.AddHours(-1));
        Ready();

        Timer.Fire();

        // The list is read and does not hold the present name, which is this machine's all the same.
        Assert.Equal(ClipFoldersState.Read, ClipFolders.TryLoad(_paths.ClipboardFoldersFile, out var listed));
        Assert.Equal(["OLD-OTTER"], listed);
        Assert.Equal(new[] { BlobOf(OldDay), BlobOf(today) }.Order(StringComparer.Ordinal), _server.Names);
    }

    [Fact]
    public void A_list_of_clipboard_folders_of_zero_bytes_is_as_good_as_none_and_sync_runs()
    {
        File.WriteAllBytes(_paths.ClipboardFoldersFile, []);
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();

        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal([BlobOf(Sig)], _server.Names);
        Assert.Empty(_notes);
    }

    // ---- What stops sync before any connection ----

    [Fact]
    public void A_sync_json_that_cannot_be_used_stops_sync_with_no_connection_and_is_said_once()
    {
        SetUp();
        File.WriteAllText(_paths.SyncFile, BadPortJson);
        Configure();

        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.ConfigUnusable), _service.Status);
        Assert.Equal(0, _connector.Opens);
        Assert.Equal([BadPortNote], _notes);
        Assert.Equal(["Sync is stopped (ConfigUnusable)"], _log);

        // Put right by hand, it runs at the next turn of the clock.
        SetUp();
        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    [Fact]
    public void A_sync_json_without_a_confirmed_host_key_makes_no_connection()
    {
        SetUp(fingerprint: null);
        Configure();

        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.SetupUnfinished), _service.Status);
        Assert.Equal(0, _connector.Opens + _connector.HostKeyReads);
        Assert.Equal([SyncService.UnfinishedNote], _notes);
    }

    [Fact]
    public void Without_a_sync_key_no_connection_is_made()
    {
        SetUp();
        File.Delete(_paths.KeyFile);
        Configure();

        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.KeyMissing), _service.Status);
        Assert.Equal(0, _connector.Opens);
        Assert.Equal([SyncService.NoKeyNote], _notes);
    }

    [Theory]
    [InlineData("key.bin", SyncStop.KeyUnreadable, SyncService.KeyUnreadableNote, "Sync stopped: the sync key cannot be read. Run Sync Setup")]
    [InlineData("ssh-passphrase.bin", SyncStop.PassphraseUnreadable, SyncService.PassphraseUnreadableNote, "Sync stopped: the SSH key's saved passphrase cannot be read. Run Sync Setup")]
    public void A_sync_key_or_a_passphrase_that_cannot_be_read_back_makes_no_connection_and_each_is_named_as_what_it_is(string file, SyncStop stop, string note, string tray)
    {
        SetUp();
        File.WriteAllText(Path.Combine(_box.Root, file), "not protected by this user");
        Configure();

        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, stop), _service.Status);
        Assert.Equal(0, _connector.Opens);
        Assert.Equal([note], _notes);
        Assert.Equal(tray, Tray());
    }

    [Fact]
    public void The_sentence_for_a_passphrase_that_cannot_be_read_back_does_not_send_the_owner_to_the_sync_key()
    {
        Assert.Equal(
            "Sync is stopped: the saved passphrase of the SSH key file cannot be read on this machine. Run Sync Setup and enter the passphrase again.",
            SyncService.PassphraseUnreadableNote);
        Assert.DoesNotContain("sync key", SyncService.PassphraseUnreadableNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sync key", SyncService.KeyUnreadableNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_passphrase_of_the_key_file_is_handed_to_the_connection()
    {
        SetUp();
        new KeyStore(_paths.KeyFile, _paths.PassphraseFile, _protector).WritePassphrase("walrus tusk");
        _connector.Passphrase = "walrus tusk";
        Configure();

        Timer.Fire();

        Assert.Equal("walrus tusk", _connector.LastPassphrase);
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    // ---- What stops sync until the owner acts ----

    [Fact]
    public void A_changed_host_key_stops_sync_is_said_once_and_is_not_tried_again_by_the_clock()
    {
        Ready();
        Timer.Fire();
        _connector.Fingerprint = MemoryConnector.OtherKey;

        Timer.Fire();
        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.HostKeyChanged, Start), _service.Status);
        Assert.Equal(2, _connector.Opens);
        Assert.Equal([SyncService.HostKeyNote], _notes);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal("Sync stopped: the server's key changed. Run Sync Setup", _service.Status.Tooltip(_clock.UtcNow, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_changed_host_key_is_never_accepted_by_Sync_Now_and_only_Sync_Setup_pins_the_new_one()
    {
        Ready();
        _connector.Fingerprint = MemoryConnector.OtherKey;
        Timer.Fire();

        _service.SyncNow();

        Assert.Equal(SyncStop.HostKeyChanged, _service.Status.Stop);
        Assert.Equal(2, _connector.Opens);
        Assert.Equal([SyncService.HostKeyNote, SyncService.HostKeyNote], _notes);
        Assert.Empty(_server.Calls);

        // The owner confirms the new key in Sync Setup, which writes it to sync.json.
        SetUp(MemoryConnector.OtherKey);
        _service.SetupChanged();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(SyncService.DoneNote, _notes[^1]);
    }

    [Theory]
    [InlineData(RemoteFailure.Refused, SyncStop.Refused, SyncService.RefusedNote)]
    [InlineData(RemoteFailure.KeyFile, SyncStop.KeyFile, SyncService.KeyFileNote)]
    public void A_refused_sign_in_and_a_key_file_that_cannot_be_used_stop_sync_until_the_owner_acts(RemoteFailure failure, SyncStop stop, string note)
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(failure, "SshAuthenticationException");

        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, stop), _service.Status);
        Assert.Equal(1, _connector.Opens);
        Assert.Equal([note], _notes);

        // Reload lets it try once more.
        _connector.Refusal = null;
        Configure();
        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    [Fact]
    public void A_stop_that_waits_for_the_owner_comes_back_as_itself_after_a_sync_json_that_could_not_be_used()
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Refused, "SshAuthenticationException");
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.Refused), _service.Status);

        // A slip in a hand edit, or the file held for a moment: one run finds it unusable, and says so.
        File.WriteAllText(_paths.SyncFile, BadPortJson);
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.ConfigUnusable), _service.Status);
        Assert.Equal("Sync stopped: sync.json cannot be used", Tray());

        // Put right. The sign-in is still refused as far as Wyvern knows, and nobody has acted on that.
        SetUp();
        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.Refused), _service.Status);
        Assert.Equal("Sync stopped: the server refused the sign-in. Run Sync Setup", Tray());
        Assert.Equal(1, _connector.Opens);
        Assert.Equal([SyncService.RefusedNote, BadPortNote], _notes);
        Assert.Equal(["Sync could not connect (Refused, SshAuthenticationException)", "Sync is stopped (ConfigUnusable)", "Sync is stopped (Refused)"], _log);

        // Sync Now is the owner acting: it connects again.
        _connector.Refusal = null;
        _service.SyncNow();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(2, _connector.Opens);
    }

    [Theory]
    [InlineData(RemoteFailure.HostKeyChanged, SyncStop.HostKeyChanged, SyncService.HostKeyNote, "Sync stopped: the server's key changed. Run Sync Setup")]
    [InlineData(RemoteFailure.Refused, SyncStop.Refused, SyncService.RefusedNote, "Sync stopped: the server refused the sign-in. Run Sync Setup")]
    [InlineData(RemoteFailure.KeyFile, SyncStop.KeyFile, SyncService.KeyFileNote, "Sync stopped: the SSH key file cannot be used. Run Sync Setup")]
    public void A_stop_that_waits_for_the_owner_is_not_passed_by_when_sync_json_was_gone_for_a_while(RemoteFailure failure, SyncStop stop, string note, string tray)
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(failure);
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Stopped, stop), _service.Status);

        // The file is gone while a run looks: deleted by hand, or an editor is saving it.
        File.Delete(_paths.SyncFile);
        Timer.Fire();
        Assert.Equal(SyncStatus.Off, _service.Status);
        Assert.Equal("", Tray());

        // It is back, and the server would now let Wyvern in. Nobody has acted, so nothing is connected to.
        _connector.Refusal = null;
        SetUp();
        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, stop), _service.Status);
        Assert.Equal(tray, Tray());
        Assert.Equal(1, _connector.Opens);
        Assert.Equal([note], _notes);
        Assert.Empty(_server.Calls);

        // Sync Setup saved: whatever stopped sync is forgotten.
        _service.SetupChanged();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(2, _connector.Opens);
    }

    [Fact]
    public void A_sync_json_without_a_confirmed_host_key_does_not_lift_a_stop_that_waits_for_the_owner_either()
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.KeyFile);
        Timer.Fire();

        SetUp(fingerprint: null);
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.SetupUnfinished), _service.Status);

        _connector.Refusal = null;
        SetUp();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.KeyFile), _service.Status);
        Assert.Equal(1, _connector.Opens);
        Assert.Equal([SyncService.KeyFileNote, SyncService.UnfinishedNote], _notes);
    }

    [Fact]
    public void A_wrong_clock_is_not_a_stop_that_waits_for_the_owner_even_after_one_that_was()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Refused);
        Timer.Fire();

        // Reload: the owner acted on the refusal. From here on the clocks are what is wrong.
        _connector.Refusal = null;
        Configure();
        _server.Clock = Start.AddDays(3);
        Timer.Fire();
        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.ClockWrong), _service.Status);
        Assert.Equal([SyncService.RefusedNote, SyncService.ClockNote], _notes);
        Assert.Equal(4, _connector.Opens);
        Assert.Equal(TimeSpan.FromMinutes(20), Timer.Due);
        Assert.Equal("Sync stopped: this machine's clock and the server's differ", Tray());

        _server.Clock = Start;
        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
    }

    // ---- A server that cannot be reached ----

    [Fact]
    public void An_unreachable_server_shows_no_note_keeps_the_last_synced_time_and_is_tried_less_and_less_often()
    {
        Ready();
        Timer.Fire();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");
        var waits = new List<TimeSpan?>();

        for (int i = 0; i < 6; i++)
        {
            Timer.Fire();
            waits.Add(Timer.Due);
        }

        Assert.Equal(new SyncStatus(SyncCondition.Unreachable, SyncStop.None, Start), _service.Status);
        Assert.Empty(_notes);
        Assert.Equal([5, 10, 20, 40, 60, 60], waits.Select(wait => (int)wait!.Value.TotalMinutes));
        Assert.Equal("Sync server unreachable. Last synced 12:00", _service.Status.Tooltip(_clock.UtcNow, TimeZoneInfo.Utc));
        Assert.Contains("Sync could not connect (Unreachable, SocketException 10060)", _log);

        // Back again: synced, and the clock goes at its ordinary pace.
        _connector.Refusal = null;
        _clock.Advance(TimeSpan.FromHours(3));
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddHours(3)), _service.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Sync_Now_against_an_unreachable_server_says_so()
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable);

        _service.SyncNow();

        Assert.Equal([SyncService.UnreachableNote], _notes);
    }

    [Fact]
    public void A_server_that_is_lost_in_the_middle_of_a_run_counts_as_unreachable()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        _server.Fault = call => call.StartsWith("upload", StringComparison.Ordinal) ? new RemoteStoreException(RemoteFailure.Unreachable) : null;

        _service.SyncNow();

        Assert.Equal(SyncCondition.Unreachable, _service.Status.Condition);
        Assert.Equal([SyncService.UnreachableNote], _notes);
        Assert.Equal(0, _connector.OpenNow);
    }

    // ---- A server that was reached and could not be used ----

    [Theory]
    [InlineData(RemoteFailure.Server)]
    [InlineData(RemoteFailure.TooMuch)]
    [InlineData(RemoteFailure.Missing)]
    public void A_server_that_was_reached_and_could_not_be_used_is_shown_as_that_and_not_as_unreachable(RemoteFailure failure)
    {
        Ready();
        Timer.Fire();
        _connector.Refusal = new RemoteStoreException(failure, "SshException");
        var waits = new List<TimeSpan?>();

        for (int i = 0; i < 3; i++)
        {
            Timer.Fire();
            waits.Add(Timer.Due);
        }

        // A line of its own in the tray. By the clock nothing is said, and it is tried again less
        // and less often, as a server that cannot be reached is.
        Assert.Equal(new SyncStatus(SyncCondition.ServerUnusable, SyncStop.None, Start), _service.Status);
        Assert.Equal("Sync could not use the server. Last synced 12:00", Tray());
        Assert.Empty(_notes);
        Assert.Equal([5, 10, 20], waits.Select(wait => (int)wait!.Value.TotalMinutes));
        Assert.Equal(4, _connector.Opens);
        Assert.Contains("Sync could not connect (" + failure + ", SshException)", _log);

        _service.SyncNow();
        Assert.Equal([SyncService.ServerNote], _notes);

        _connector.Refusal = null;
        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
    }

    [Theory]
    [InlineData(RemoteFailure.Folder)]
    [InlineData(RemoteFailure.TooMuch)]
    [InlineData(RemoteFailure.Server)]
    public void A_server_that_could_not_be_used_in_the_middle_of_a_run_is_shown_in_the_same_line_and_nothing_is_said_by_the_clock(RemoteFailure failure)
    {
        Ready();
        _server.Fault = call => call == "list" ? new RemoteStoreException(failure, "SftpPermissionDeniedException") : null;

        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.ServerUnusable), _service.Status);
        Assert.Equal("Sync could not use the server. Not synced yet", Tray());
        Assert.Empty(_notes);
        Assert.Equal(TimeSpan.FromMinutes(10), Timer.Due);

        _service.SyncNow();
        Assert.Equal([SyncService.ServerNote], _notes);
    }

    [Fact]
    public void A_folder_on_the_server_that_cannot_be_made_or_listed_when_connecting_is_said_once_by_the_clock_and_is_not_a_stop()
    {
        Ready();
        Timer.Fire();

        // "remoteDir" names a folder whose parent is not there: it does not heal by itself.
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Folder, "SftpPathNotFoundException");
        var waits = new List<TimeSpan?>();
        for (int i = 0; i < 3; i++)
        {
            Timer.Fire();
            waits.Add(Timer.Due);
        }

        Assert.Equal(new SyncStatus(SyncCondition.ServerUnusable, SyncStop.ServerFolder, Start), _service.Status);
        Assert.Equal("Sync could not use the server. Last synced 12:00", Tray());
        Assert.Equal([SyncService.ServerFolderNote], _notes);
        Assert.Equal(
            "Sync could not use the server: the folder on it could not be made or listed. Check the folder in Sync Setup, and that the folder it is in exists. Wyvern will try again.",
            SyncService.ServerFolderNote);

        // Not a stop that waits for the owner: the owner may make the folder on the server and
        // tell Wyvern nothing, so it is tried again, less and less often.
        Assert.Equal(4, _connector.Opens);
        Assert.Equal([5, 10, 20], waits.Select(wait => (int)wait!.Value.TotalMinutes));

        // An owner who asks hears the same sentence, and Sync Setup with another folder ends it.
        _service.SyncNow();
        Assert.Equal([SyncService.ServerFolderNote, SyncService.ServerFolderNote], _notes);
        _connector.Refusal = null;
        _service.SetupChanged();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(SyncService.DoneNote, _notes[^1]);
    }

    [Theory]
    [InlineData(5, 0, 5)]
    [InlineData(5, 1, 5)]
    [InlineData(5, 2, 10)]
    [InlineData(5, 4, 40)]
    [InlineData(5, 5, 60)]
    [InlineData(5, 500, 60)]
    [InlineData(1, 3, 4)]
    [InlineData(45, 2, 60)]
    [InlineData(240, 3, 240)]
    public void The_wait_doubles_with_each_failure_up_to_an_hour_and_is_never_shorter_than_the_interval(int interval, int failures, int expected)
    {
        Assert.Equal(TimeSpan.FromMinutes(expected), SyncService.BackOff(TimeSpan.FromMinutes(interval), failures));
    }

    // ---- Runs that finish and leave something ----

    [Fact]
    public void A_blob_that_cannot_be_read_flags_sync_as_degraded_and_is_said_once()
    {
        Ready();
        _server.Put(new string('e', 64), [1, 2, 3]);

        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Degraded, SyncStop.None, Start), _service.Status);
        Assert.Equal([SyncService.DegradedNote], _notes);
        Assert.Equal("Sync degraded: some server files cannot be read. Last synced 12:00", _service.Status.Tooltip(_clock.UtcNow, TimeZoneInfo.Utc));

        // Taken off the server, sync is well again; a new one is said again.
        _server.Remove(new string('e', 64));
        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        _server.Put(new string('f', 64), [1, 2, 3]);
        Timer.Fire();
        Assert.Equal([SyncService.DegradedNote, SyncService.DegradedNote], _notes);
    }

    [Fact]
    public void What_was_said_once_is_not_said_again_because_the_server_could_not_be_reached_in_between()
    {
        Ready();
        _server.Put(new string('e', 64), [1, 2, 3]);
        Timer.Fire();
        Assert.Equal([SyncService.DegradedNote], _notes);

        // A laptop wakes without its network: one run cannot reach the server.
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");
        Timer.Fire();
        Assert.Equal(SyncCondition.Unreachable, _service.Status.Condition);
        _connector.Refusal = null;
        Timer.Fire();

        // The same blob is still there. No run has completed without it, so it is not news.
        Assert.Equal(SyncCondition.Degraded, _service.Status.Condition);
        Assert.Equal([SyncService.DegradedNote], _notes);
    }

    [Fact]
    public void What_was_said_once_is_not_said_again_because_sync_was_stopped_for_another_reason_in_between()
    {
        Ready();
        _server.Put(new string('e', 64), [1, 2, 3]);
        Timer.Fire();

        // The sync key is gone for one run, which is said, and then back.
        byte[] key = File.ReadAllBytes(_paths.KeyFile);
        File.Delete(_paths.KeyFile);
        Timer.Fire();
        File.WriteAllBytes(_paths.KeyFile, key);
        Timer.Fire();

        Assert.Equal(SyncCondition.Degraded, _service.Status.Condition);
        Assert.Equal([SyncService.DegradedNote, SyncService.NoKeyNote], _notes);

        // An owner who asks is answered all the same.
        _service.SyncNow();
        Assert.Equal([SyncService.DegradedNote, SyncService.NoKeyNote, SyncService.DegradedNote], _notes);
    }

    [Fact]
    public void A_run_that_found_a_blob_it_cannot_read_and_left_a_file_for_later_does_not_move_the_last_synced_time()
    {
        Ready();
        _server.Put(new string('e', 64), [1, 2, 3]);
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Degraded, SyncStop.None, Start), _service.Status);

        // Five minutes on, a file is held by another program and cannot be sent.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        using (new FileStream(_box.Full(Sig), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Timer.Fire();
        }

        Assert.DoesNotContain(BlobOf(Sig), _server.Names);
        Assert.Equal(new SyncStatus(SyncCondition.Degraded, SyncStop.None, Start), _service.Status);
        Assert.Equal("Sync degraded: some server files cannot be read. Last synced 12:00", Tray());

        // Let go of, it is sent, and then the time moves.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Timer.Fire();

        Assert.Contains(BlobOf(Sig), _server.Names);
        Assert.Equal(new SyncStatus(SyncCondition.Degraded, SyncStop.None, Start.AddMinutes(10)), _service.Status);
        Assert.Equal("Sync degraded: some server files cannot be read. Last synced 12:10", Tray());
    }

    [Fact]
    public void A_run_that_found_a_blob_it_cannot_read_and_could_not_save_its_record_does_not_move_the_last_synced_time()
    {
        Ready();
        _server.Put(new string('e', 64), [1, 2, 3]);
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Degraded, SyncStop.None, Start), _service.Status);

        // The name the record is first written under is taken by a folder, so it cannot be saved.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Directory.CreateDirectory(_paths.SyncStateFile + ".tmp");
        Timer.Fire();

        Assert.Contains(BlobOf(Sig), _server.Names);
        Assert.Contains("The record of the sync could not be written", _log);
        Assert.Equal(new SyncStatus(SyncCondition.Degraded, SyncStop.None, Start), _service.Status);
    }

    [Fact]
    public void A_run_that_ends_in_something_nobody_expected_is_shown_said_to_an_owner_who_asked_and_tried_less_and_less_often()
    {
        Ready();
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start), _service.Status);
        _clock.Advance(TimeSpan.FromMinutes(5));

        // Not one of the store's own failures: nothing in a run is written to expect this.
        _server.Fault = _ => throw new InvalidOperationException("otter: not one of the store's own");

        _service.SyncNow();

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, Start), _service.Status);
        Assert.Equal("Sync incomplete. Last synced 12:00", Tray());
        Assert.Equal([SyncService.UnexpectedNote], _notes);
        Assert.Equal("Sync failed in a way Wyvern did not expect. Details are in the log file. Wyvern will try again.", SyncService.UnexpectedNote);
        Assert.Contains("A sync run failed", _log);
        Assert.DoesNotContain(_log, line => line.Contains("otter", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, _connector.OpenNow);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);

        // By the clock it says nothing, and waits longer each time, as for a server that cannot be reached.
        var waits = new List<TimeSpan?>();
        for (int i = 0; i < 3; i++)
        {
            Timer.Fire();
            waits.Add(Timer.Due);
        }

        Assert.Equal([10, 20, 40], waits.Select(wait => (int)wait!.Value.TotalMinutes));
        Assert.Equal([SyncService.UnexpectedNote], _notes);

        _server.Fault = null;
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(5)), _service.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
    }

    [Fact]
    public void A_first_run_that_ends_in_something_nobody_expected_does_not_leave_the_tray_saying_nothing_of_sync()
    {
        Ready();
        _server.Fault = _ => throw new InvalidOperationException("not one of the store's own");

        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete), _service.Status);
        Assert.Equal("Sync incomplete. Not synced yet", Tray());
        Assert.Empty(_notes);
    }

    [Fact]
    public void A_run_that_left_files_for_later_is_not_called_synced()
    {
        Ready();
        Timer.Fire();
        _clock.Advance(TimeSpan.FromMinutes(5));
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        using var held = new FileStream(_box.Full(Sig), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        _service.SyncNow();

        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, Start), _service.Status);
        Assert.Equal([SyncService.IncompleteNote], _notes);
        Assert.Equal("Sync incomplete. Last synced 12:00", _service.Status.Tooltip(_clock.UtcNow, TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_run_that_could_not_list_a_folder_the_record_does_not_know_is_not_called_synced()
    {
        const string unread = "The data folder holds folders or files that could not be read, which are tried again by the next run: 1";
        Ready();
        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start), _service.Status);

        // Five minutes on there is a folder that cannot be listed. Nothing in it was ever synced,
        // so the record knows nothing behind it and no file counts as left for later.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        _box.Write("library/locked/never-synced.md", "seal", Start.AddHours(-1));
        string locked = _box.Full("library/locked");
        _box.DenyListing(locked);
        try
        {
            _service.SyncNow();
        }
        finally
        {
            _box.Allow(locked);
        }

        // What could be read went up. A part of the folder was not seen, and the log of the
        // same run says so: the run is not "synced", and the time does not move.
        Assert.Equal([BlobOf(Sig)], _server.Names);
        Assert.Contains(unread, _log);
        Assert.Equal(new SyncStatus(SyncCondition.Incomplete, SyncStop.None, Start), _service.Status);
        Assert.Equal("Sync incomplete. Last synced 12:00", Tray());
        Assert.Equal([SyncService.IncompleteNote], _notes);

        // The folder can be listed again: everything is synced, and then the time moves.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _service.SyncNow();

        Assert.Equal(2, _server.Names.Count);
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(10)), _service.Status);
        Assert.Equal([SyncService.IncompleteNote, SyncService.DoneNote], _notes);
        Assert.Single(_log, line => line == unread);
    }

    [Fact]
    public void A_clock_that_is_days_off_stops_sync_is_said_once_and_is_tried_again_by_itself()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        _server.Clock = Start.AddDays(3);

        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.ClockWrong), _service.Status);
        Assert.Equal([SyncService.ClockNote], _notes);
        Assert.Empty(_server.Names);
        Assert.Equal(TimeSpan.FromMinutes(10), Timer.Due);

        _server.Clock = Start;
        Timer.Fire();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    [Fact]
    public void A_data_folder_that_cannot_be_read_is_said_to_an_owner_who_asked()
    {
        SetUp();
        string nowhere = Path.Combine(_box.Root, "nowhere");
        _service.Configure(new DataPaths(nowhere), Device(), SyncOptions.Default);

        _service.SyncNow();

        Assert.Equal([SyncService.DataFolderNote], _notes);
        Assert.Equal(new SyncStatus(SyncCondition.Incomplete), _service.Status);

        // Reported as before there was an id: the folder is not made, and nothing is said of a record.
        Assert.False(Directory.Exists(nowhere));
        Assert.Equal(["Sync did nothing because the data folder could not be listed"], _log);

        // And no connection is made for a folder that is not there.
        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_server.Calls);
    }

    // ---- A data folder that holds Wyvern's own files ----
    //
    // The Wyvern root of these tests is a folder two levels down in the sandbox, so that a data
    // folder above it is still the test's own. Every path is text or a folder in the sandbox.

    private WyvernPaths NestedRoot() => new(Path.Combine(_box.Root, "home", "Wyvern"));

    [Theory]
    [InlineData(@"home\Wyvern")]
    [InlineData(@"home\WYVERN\")]
    [InlineData("home/wyvern/")]
    [InlineData("home")]
    [InlineData(@"HOME\")]
    [InlineData("")]
    public void A_data_folder_that_is_the_Wyvern_root_or_holds_it_stops_sync_before_anything_is_read_or_sent(string dataFolder)
    {
        var paths = NestedRoot();
        SetUp(paths: paths);
        File.WriteAllText(paths.SettingsFile, "{ }");
        using var service = NewService(Inline, paths);
        var timer = _time.Timers[^1];
        service.Configure(new DataPaths(Path.Combine(_box.Root, dataFolder)), Device(), SyncOptions.Default);

        timer.Fire();
        timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.DataFolderHoldsWyvern), service.Status);
        Assert.Equal("Sync stopped: the data folder holds Wyvern's own files. Choose another data folder", Tray(service));
        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_server.Names);
        Assert.Equal([SyncService.OwnFilesNote], _notes);
        Assert.Equal("Sync is stopped: the data folder holds Wyvern's own files. Choose another data folder in the settings.", SyncService.OwnFilesNote);
        Assert.Equal(["Sync is stopped (DataFolderHoldsWyvern)"], _log);
        Assert.False(File.Exists(paths.SyncStateFile));

        // It does not wait for more than a corrected setting and a Reload.
        service.Configure(paths.Data(new DataDirSetting(null, null)), Device(), SyncOptions.Default);
        Directory.CreateDirectory(paths.DefaultDataDir);
        timer.Fire();

        Assert.Equal(SyncCondition.Ok, service.Status.Condition);
        Assert.Empty(_server.Names);
    }

    [Fact]
    public void A_data_folder_that_holds_Wyverns_own_files_is_looked_at_after_sync_json_and_before_anything_else_is_read_or_made()
    {
        var paths = NestedRoot();
        Directory.CreateDirectory(paths.Root);
        using var service = NewService(Inline, paths);
        var timer = _time.Timers[^1];
        service.Configure(new DataPaths(paths.Root), Device(), SyncOptions.Default);

        // Sync is not set up: nothing is said of it, whatever the data folder is.
        timer.Fire();

        Assert.Equal(SyncStatus.Off, service.Status);
        Assert.Equal("", Tray(service));
        Assert.Empty(_notes);

        // Set up, and the sync key is gone as well. The data folder is what is said: nothing
        // past sync.json is looked for, and no id file is made among Wyvern's own files.
        SetUp(paths: paths);
        File.Delete(paths.KeyFile);
        timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.DataFolderHoldsWyvern), service.Status);
        Assert.Equal([SyncService.OwnFilesNote], _notes);
        Assert.Equal(0, _connector.Opens);
        Assert.False(File.Exists(Path.Combine(paths.Root, SyncFolderId.FileName)));
    }

    [Theory]
    [InlineData(@"home\Wyvern\data")]
    [InlineData(@"home\WyvernData")]
    [InlineData(@"home\Wyvern2")]
    [InlineData(@"home\Beaco")]
    [InlineData("elsewhere")]
    public void A_data_folder_inside_or_beside_the_Wyvern_root_is_synced(string dataFolder)
    {
        var paths = NestedRoot();
        SetUp(paths: paths);
        string data = Path.Combine(_box.Root, dataFolder);
        Directory.CreateDirectory(Path.Combine(data, "library"));
        File.WriteAllText(Path.Combine(data, "library", "note.md"), "walrus");
        using var service = NewService(Inline, paths);
        service.Configure(new DataPaths(data), Device(), SyncOptions.Default);

        _time.Timers[^1].Fire();

        Assert.Equal(SyncCondition.Ok, service.Status.Condition);
        Assert.Equal([BlobOf("library/note.md")], _server.Names);
        Assert.Empty(_notes);
    }

    [Theory]
    [InlineData(@"otter_ed25519")]
    [InlineData(@"keys\otter_ed25519")]
    [InlineData(@"library\scripts\keys\otter_ed25519")]
    public void A_key_file_inside_the_data_folder_stops_sync_with_a_sentence_of_its_own(string keyFile)
    {
        SetUp(keyPath: Path.Combine(_box.Data.ToUpperInvariant(), keyFile));
        Configure();

        Timer.Fire();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.KeyFileInDataFolder), _service.Status);
        Assert.Equal("Sync stopped: the SSH key file is inside the data folder. Move it elsewhere", Tray());
        Assert.Equal(0, _connector.Opens);
        Assert.Equal([SyncService.KeyFileInDataFolderNote], _notes);
        Assert.Equal("Sync is stopped: the SSH key file is inside the data folder, where it would be synced. Keep the key file somewhere else and run Sync Setup.", SyncService.KeyFileInDataFolderNote);
        Assert.Equal(["Sync is stopped (KeyFileInDataFolder)"], _log);

        // Named anew in Sync Setup, somewhere else.
        SetUp();
        _service.SetupChanged();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    [Fact]
    public void A_key_file_beside_the_data_folder_is_no_reason_to_stop()
    {
        SetUp(keyPath: Path.Combine(_box.Data + "-keys", "otter_ed25519"));
        Configure();

        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    // ---- What the owner is shown ----

    [Fact]
    public void Every_text_for_the_tray_fits_beside_the_longest_text_about_clipboard_capture()
    {
        var conditions = Enum.GetValues<SyncCondition>();
        var stops = Enum.GetValues<SyncStop>();
        var times = new DateTimeOffset?[] { null, Start, Start.AddDays(-400) };

        foreach (var condition in conditions)
        {
            foreach (var stop in stops)
            {
                foreach (var time in times)
                {
                    var status = new SyncStatus(condition, stop, time);
                    string tooltip = status.Tooltip(Start, TimeZoneInfo.Utc);
                    Assert.True(tooltip.Length <= SyncStatus.MaxTooltipLength, tooltip);
                    Assert.True(status.WithCapture(ClipCapture.PausedTooltip, Start, TimeZoneInfo.Utc).Length <= 127, tooltip);
                    Assert.DoesNotContain('\n', tooltip);
                }
            }
        }
    }

    [Fact]
    public void The_tray_says_nothing_of_sync_while_it_is_off_and_gives_the_time_in_the_owners_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(-5), "test", "test");
        var synced = new SyncStatus(SyncCondition.Ok, SyncStop.None, Start);

        Assert.Equal("Wyvern", SyncStatus.Off.WithCapture("Wyvern", Start, zone));
        Assert.Equal("Wyvern. Last synced 07:00", synced.WithCapture("Wyvern", Start.AddHours(1), zone));
        Assert.Equal("Wyvern (clipboard capture paused). Last synced 2026-10-03 07:00", synced.WithCapture(ClipCapture.PausedTooltip, Start.AddDays(1), zone));
        Assert.Equal("Sync has not run yet", new SyncStatus(SyncCondition.Waiting).Tooltip(Start, zone));
        Assert.Equal("Sync server unreachable. Not synced yet", new SyncStatus(SyncCondition.Unreachable).Tooltip(Start, zone));

        // A server that was reached and could not be used has a line of its own, whatever the reason.
        Assert.Equal("Sync could not use the server. Not synced yet", new SyncStatus(SyncCondition.ServerUnusable).Tooltip(Start, zone));
        Assert.Equal("Sync could not use the server. Last synced 07:00", new SyncStatus(SyncCondition.ServerUnusable, SyncStop.ServerFolder, Start).Tooltip(Start, zone));
    }

    [Fact]
    public void The_sentence_for_a_key_file_that_cannot_be_used_says_where_the_file_must_be()
    {
        // A key file on a network drive, behind a link or not really on the disk (an online-only
        // file) is refused before it is opened, and to the owner it is a file that opens
        // everywhere else.
        Assert.Equal(
            "Sync is stopped: the SSH key file could not be read, or its passphrase is missing or wrong. It must be on a drive of this machine, kept on this disk (not an online-only file), and not reached through a link. Run Sync Setup.",
            SyncService.KeyFileNote);
    }

    [Fact]
    public void Nothing_that_is_logged_or_said_names_the_server_the_user_a_path_or_a_file()
    {
        _box.Write("library/snippets/private-walrus-note.md", "the secret text", Start.AddHours(-1));
        Ready();
        _service.SyncNow();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10061");
        _service.SyncNow();
        _connector.Refusal = null;
        _connector.Fingerprint = MemoryConnector.OtherKey;
        _service.SyncNow();
        File.WriteAllText(_paths.SyncFile, "{ \"host\": \"secret-otter.example.org\", \"user\": \"secret walrus\" }");
        _service.SyncNow();

        var everything = _log.Concat(_notes).Append(_service.Status.ToString()).ToList();

        Assert.True(everything.Count >= 8);
        foreach (string word in new[] { "secret", "otter", "walrus", "SecretKeys", "private", "snippets", ".md", "home" })
        {
            Assert.DoesNotContain(everything, text => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void A_listener_that_throws_does_not_stop_a_run_or_the_clock()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        SetUp();
        using var service = new SyncService(
            _paths, _connector, _protector, _clock,
            (_, _) => throw new InvalidOperationException("log"),
            _ => throw new InvalidOperationException("note"),
            () => throw new InvalidOperationException("status"),
            () => throw new InvalidOperationException("clipboard"),
            _time,
            Inline);
        Configure(service);

        service.SyncNow();

        Assert.Equal(SyncCondition.Ok, service.Status.Condition);
        Assert.Equal(TimeSpan.FromMinutes(5), _time.Timers[^1].Due);
    }

    // ---- Closing ----

    [Fact]
    public void After_closing_nothing_runs_and_nothing_is_said()
    {
        Ready();

        _service.Dispose();
        _service.SyncNow();
        Timer.Fire();
        Configure();

        Assert.True(Timer.Disposed);
        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_notes);
        Assert.Equal(0, _statusChanges);
    }

    [Fact]
    public void A_run_that_begins_as_Wyvern_closes_connects_to_nothing()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        SetUp();

        // The run is asked for, and Wyvern closes before the thread that is to do it has begun.
        Action? begun = null;
        using var service = NewService(run =>
        {
            begun = run;
            return Task.CompletedTask;
        });
        Configure(service);
        service.SyncNow();
        Assert.NotNull(begun);

        service.Dispose();
        begun();

        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_server.Calls);
        Assert.Empty(_notes);
        Assert.Equal(0, _statusChanges);
        Assert.Equal(SyncStatus.Off, service.Status);
        Assert.Equal([Sig], _box.Files());
    }

    [Fact]
    public void Closing_while_a_run_connects_closes_the_connection_as_soon_as_it_is_there()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();

        // Wyvern closes while the connection is being made: closing finds no connection to close yet.
        _hook.Connecting = _service.Dispose;

        Timer.Fire();

        // The run has its connection, sees that Wyvern is closing, and asks the server nothing.
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(0, _connector.OpenNow);
        Assert.Empty(_server.Calls);
        Assert.Empty(_notes);
        Assert.False(File.Exists(_paths.SyncStateFile));
        Assert.True(Timer.Disposed);
    }

    [Fact]
    public void Closing_lets_a_run_finish_the_file_in_hand_records_it_and_starts_no_other()
    {
        _box.Write("library/a.md", "a", Start.AddHours(-1));
        _box.Write("library/b.md", "b", Start.AddHours(-1));
        SetUp();
        using var service = NewService(OnItsOwnThread);
        Configure(service);
        using var inUpload = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool released = false;
        _server.Uploaded = _ =>
        {
            inUpload.Set();
            released = release.Wait(Limit);
        };
        var closing = new Thread(service.Dispose) { IsBackground = true, Name = "the closing of a test" };
        try
        {
            service.SyncNow();
            Assert.True(inUpload.Wait(Limit));
            closing.Start();

            // Closing has made the timer of its wait: it is waiting for the run.
            Assert.NotNull(_time.WaitForTimer(SyncService.CloseWait, 1, Limit));
        }
        finally
        {
            // Whatever went wrong above, the run is not left held, and has ended before this test has.
            release.Set();
            _runThread?.Join(Limit);
        }

        Assert.True(closing.Join(Limit));
        Assert.True(service.Idle.IsCompletedSuccessfully);

        // The upload in hand was let go of by this test and did not give up waiting.
        Assert.True(released);

        // One file went up and is recorded; the second was not started; nothing was said.
        Assert.Equal(1, _server.Count("upload"));
        var state = SyncState.Load(_paths.SyncStateFile, ScopeNow(), out var how, out _);
        Assert.Equal(SyncStateLoad.Loaded, how);
        Assert.Single(state.Files);
        Assert.Equal(0, _connector.OpenNow);
        lock (_notes)
        {
            Assert.Empty(_notes);
        }

        // Once, before Wyvern began to close: "has not run yet". Nothing after that.
        Assert.Equal(1, _statusChanges);
        Assert.Equal(new SyncStatus(SyncCondition.Waiting), service.Status);
    }

    [Fact]
    public void Closing_does_not_wait_for_ever_for_a_call_to_the_server_but_closes_the_connection_under_it()
    {
        _box.Write("library/a.md", "a", Start.AddHours(-1));
        SetUp();
        using var service = NewService(OnItsOwnThread);
        Configure(service);
        using var inUpload = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _server.Uploaded = _ =>
        {
            inUpload.Set();
            release.Wait(Limit);
        };
        var closing = new Thread(service.Dispose) { IsBackground = true, Name = "the closing of a test" };
        try
        {
            service.SyncNow();
            Assert.True(inUpload.Wait(Limit));
            closing.Start();

            // The wait runs out twice: before the connection is closed, and after. Each time the
            // test is told that closing has made the timer of that wait, and then says it ran out.
            var first = _time.WaitForTimer(SyncService.CloseWait, 1, Limit);
            Assert.NotNull(first);
            Assert.Equal(1, _connector.OpenNow);
            first.Fire();

            var second = _time.WaitForTimer(SyncService.CloseWait, 2, Limit);
            Assert.NotNull(second);
            Assert.Equal(0, _connector.OpenNow);
            second.Fire();

            Assert.True(closing.Join(Limit));
            lock (_log)
            {
                Assert.Contains("Sync was still at work when Wyvern closed, and the wait for it ran out", _log);
            }

            Assert.False(service.Idle.IsCompleted);
        }
        finally
        {
            release.Set();
            _runThread?.Join(Limit);
        }

        Assert.True(service.Idle.IsCompletedSuccessfully);
    }

    // ---- Reload, Sync Setup and Sync Now while a run is at work ----

    [Fact]
    public void Reload_while_a_run_connects_is_honoured_when_that_run_ends()
    {
        string elsewhere = Path.Combine(_box.Root, "other-data");
        Directory.CreateDirectory(Path.Combine(elsewhere, "library"));
        File.WriteAllText(Path.Combine(elsewhere, "library", "note.md"), "seal");
        SetUp();
        Configure(minutes: 20);

        // Reload while the run connects: another data folder, and another pace.
        _hook.Connecting = () => _service.Configure(
            new DataPaths(elsewhere),
            Device(),
            SyncOptions.From(new WyvernSettings { SyncIntervalMinutes = JsonSerializer.SerializeToElement(30) }));

        Timer.Fire();

        // The run that was at work kept the folder it began with. The next one does not wait
        // twenty minutes or thirty: it comes ten seconds after that run ended.
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Empty(_server.Names);
        Assert.Equal(SyncService.StartDelay, Timer.Due);

        Timer.Fire();

        Assert.Equal([BlobOf("library/note.md")], _server.Names);
        Assert.Equal(TimeSpan.FromMinutes(30), Timer.Due);
    }

    [Theory]
    [InlineData(RemoteFailure.HostKeyChanged, "reload")]
    [InlineData(RemoteFailure.Refused, "reload")]
    [InlineData(RemoteFailure.KeyFile, "reload")]
    [InlineData(RemoteFailure.Refused, "sync now")]
    [InlineData(RemoteFailure.Refused, "sync setup")]
    public void A_run_that_ends_in_a_stop_does_not_make_sync_wait_for_an_owner_who_acted_while_it_was_at_work(RemoteFailure failure, string act)
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(failure);
        _hook.Connecting = act switch
        {
            "reload" => () => Configure(),
            "sync now" => _service.SyncNow,
            _ => _service.SetupChanged,
        };

        Timer.Fire();

        // The run ended as it would have, and is shown so. After Reload, which asks nothing, its
        // stop is said by the clock as any stop is. An owner who asked meanwhile was told that
        // sync is running, and is answered by the run that follows: the stop of the run that was
        // ending is not announced to him as news.
        string stop = StopNote(failure);
        Assert.Equal(SyncCondition.Stopped, _service.Status.Condition);
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(act == "reload" ? new[] { stop } : new[] { SyncService.AlreadyRunningNote }, _notes);

        // What the owner did is not lost to the run that was ending: a run follows in ten
        // seconds, and it connects, because the owner acted after that run had begun. It ends in
        // the same stop, and an owner who asked is told so by it; after Reload it is not news.
        Assert.Equal(SyncService.StartDelay, Timer.Due);
        Timer.Fire();
        Assert.Equal(2, _connector.Opens);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        Assert.Equal(act == "reload" ? new[] { stop } : new[] { SyncService.AlreadyRunningNote, stop }, _notes);

        // That run ended in the same stop with nobody acting, so from here on sync waits, and
        // says nothing more.
        Timer.Fire();
        Timer.Fire();
        Assert.Equal(2, _connector.Opens);
        Assert.Equal(act == "reload" ? 1 : 2, _notes.Count);
    }

    private static string StopNote(RemoteFailure failure) => failure switch
    {
        RemoteFailure.HostKeyChanged => SyncService.HostKeyNote,
        RemoteFailure.Refused => SyncService.RefusedNote,
        _ => SyncService.KeyFileNote,
    };

    [Fact]
    public void The_count_of_failures_that_Reload_set_back_stays_set_back_when_the_run_it_came_during_fails_too()
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable);
        Timer.Fire();
        Timer.Fire();
        Timer.Fire();
        Assert.Equal(TimeSpan.FromMinutes(20), Timer.Due);

        // A fourth run cannot reach the server either, and Reload comes while it tries.
        _hook.Connecting = () => Configure();
        Timer.Fire();

        Assert.Equal(SyncService.StartDelay, Timer.Due);

        // The runs after Reload are counted from nothing: the interval, then twice that.
        Timer.Fire();
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        Timer.Fire();
        Assert.Equal(TimeSpan.FromMinutes(10), Timer.Due);
    }

    [Fact]
    public void Sync_Setup_while_a_run_is_at_work_says_that_sync_is_running_and_a_run_with_the_new_setup_follows()
    {
        Ready();
        _connector.Fingerprint = MemoryConnector.OtherKey;

        // While the run connects, the owner confirms the server's new key in Sync Setup, which saves it.
        _hook.Connecting = () =>
        {
            SetUp(MemoryConnector.OtherKey);
            _service.SetupChanged();
        };

        Timer.Fire();

        // The run that was at work had read the old sync.json. Its stop is shown, and is not
        // announced: the owner has just put that right, and the run he asked for is still to come.
        Assert.Equal(SyncStop.HostKeyChanged, _service.Status.Stop);
        Assert.Equal([SyncService.AlreadyRunningNote], _notes);
        Assert.Equal(SyncService.StartDelay, Timer.Due);

        Timer.Fire();

        // The clock starts that run, and it answers him as the run he asked for.
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(2, _connector.Opens);
        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);

        // The runs after it come by the clock, and say nothing while things are well.
        Timer.Fire();
        Assert.Equal(3, _connector.Opens);
        Assert.Equal(2, _notes.Count);
    }

    [Fact]
    public void A_stop_that_was_not_announced_because_the_owner_asked_meanwhile_is_still_news_to_a_later_run_by_the_clock()
    {
        Ready();
        _connector.Fingerprint = MemoryConnector.OtherKey;
        _hook.Connecting = _service.SyncNow;
        Timer.Fire();
        Assert.Equal([SyncService.AlreadyRunningNote], _notes);

        // The run he asked for cannot reach the server, and says so. No run has completed.
        _connector.Fingerprint = MemoryConnector.ServerKey;
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable);
        Timer.Fire();
        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.UnreachableNote], _notes);

        // The server is back and shows the other key again, to a run by the clock. Nobody was
        // ever told of that stop, so it is said: what was not announced does not count as said.
        _connector.Refusal = null;
        _connector.Fingerprint = MemoryConnector.OtherKey;
        Timer.Fire();
        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.UnreachableNote, SyncService.HostKeyNote], _notes);
    }

    [Fact]
    public void An_owner_who_asks_again_before_the_run_that_was_to_follow_is_answered_once()
    {
        Ready();
        _hook.Connecting = _service.SyncNow;
        Timer.Fire();
        Assert.Equal([SyncService.AlreadyRunningNote], _notes);
        Assert.Equal(SyncService.StartDelay, Timer.Due);

        // He does not wait the ten seconds: this run is the one he asked for, and it answers.
        _service.SyncNow();
        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);

        // Nothing is owed any more: the next run by the clock says nothing.
        Timer.Fire();
        Assert.Equal(3, _connector.Opens);
        Assert.Equal(2, _notes.Count);
    }

    [Fact]
    public void Reload_while_a_run_is_at_work_asks_nothing_so_the_run_that_follows_says_nothing_while_things_are_well()
    {
        Ready();
        _hook.Connecting = () => Configure();

        Timer.Fire();
        Assert.Equal(SyncService.StartDelay, Timer.Due);
        Timer.Fire();

        Assert.Equal(2, _connector.Opens);
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Empty(_notes);
    }

    // ---- The first run ----

    [Fact]
    public void Until_its_first_run_has_ended_sync_that_is_set_up_is_shown_as_not_run_yet()
    {
        _box.Write(Sig, "walrus", Start.AddHours(-1));
        Ready();
        var seen = new List<(SyncStatus Status, string Tray, int Changes)>();
        _connector.Opened = _ => seen.Add((_service.Status, Tray(), _statusChanges));

        Assert.Equal(SyncStatus.Off, _service.Status);
        Timer.Fire();

        Assert.Equal((new SyncStatus(SyncCondition.Waiting), "Sync has not run yet", 1), Assert.Single(seen));
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start), _service.Status);
        Assert.Equal(2, _statusChanges);

        // Only the first: while a later run is at work the status stays what the last one left.
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, seen[1].Status.Condition);
        Assert.Equal(2, _statusChanges);
    }

    [Theory]
    [InlineData("no sync key", SyncCondition.Stopped, SyncStop.KeyMissing)]
    [InlineData("passphrase", SyncCondition.Stopped, SyncStop.PassphraseUnreadable)]
    [InlineData("key file in the data folder", SyncCondition.Stopped, SyncStop.KeyFileInDataFolder)]
    [InlineData("list", SyncCondition.Incomplete, SyncStop.ClipboardFoldersUnreadable)]
    [InlineData("id", SyncCondition.Incomplete, SyncStop.FolderIdUnusable)]
    [InlineData("no data folder", SyncCondition.Incomplete, SyncStop.None)]
    public void A_first_run_that_ends_before_it_goes_to_the_server_is_never_shown_as_not_run_yet(string what, SyncCondition condition, SyncStop stop)
    {
        SetUp(keyPath: what == "key file in the data folder" ? Path.Combine(_box.Data, "keys", "otter_ed25519") : KeyPath);
        switch (what)
        {
            case "no sync key":
                File.Delete(_paths.KeyFile);
                break;
            case "passphrase":
                File.WriteAllText(_paths.PassphraseFile, "not protected by this user");
                break;
            case "list":
                File.WriteAllText(_paths.ClipboardFoldersFile, "{ not a list");
                break;
            case "id":
                File.WriteAllText(IdFile, "0123456789abcdef0123456789abcdef");
                break;
        }

        using var held = what == "id" ? new FileStream(IdFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (what == "no data folder")
        {
            _service.Configure(new DataPaths(Path.Combine(_box.Root, "nowhere")), Device(), SyncOptions.Default);
        }
        else
        {
            Configure();
        }

        Timer.Fire();

        // One change, from nothing to how the run ended: "has not run yet" was never in between.
        Assert.Equal(new SyncStatus(condition, stop), _service.Status);
        Assert.Equal(1, _statusChanges);
        Assert.Equal(0, _connector.Opens);
    }

    [Fact]
    public void After_a_sync_json_that_was_gone_a_stop_that_waits_for_the_owner_is_shown_again_without_not_run_yet_in_between()
    {
        Ready();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Refused);
        Timer.Fire();

        // That run went to the server: "has not run yet", and then the stop.
        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.Refused), _service.Status);
        Assert.Equal(2, _statusChanges);

        File.Delete(_paths.SyncFile);
        Timer.Fire();
        Assert.Equal(SyncStatus.Off, _service.Status);
        Assert.Equal(3, _statusChanges);

        // Back. The stop still waits, nothing is connected to, and the tray goes straight to it.
        SetUp();
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.Refused), _service.Status);
        Assert.Equal(4, _statusChanges);
        Assert.Equal(1, _connector.Opens);
    }

    [Fact]
    public void Sync_that_is_not_set_up_or_cannot_be_used_is_never_shown_as_not_run_yet()
    {
        Configure();
        Timer.Fire();
        Assert.Equal(SyncStatus.Off, _service.Status);
        Assert.Equal(0, _statusChanges);

        File.WriteAllText(_paths.SyncFile, BadPortJson);
        Timer.Fire();
        Assert.Equal(SyncStop.ConfigUnusable, _service.Status.Stop);
        Assert.Equal(1, _statusChanges);
    }

    [Fact]
    public void A_sync_json_without_a_confirmed_host_key_is_shown_as_unfinished_and_not_as_not_run_yet()
    {
        SetUp(fingerprint: null);
        Configure();

        Timer.Fire();

        Assert.Equal(SyncStop.SetupUnfinished, _service.Status.Stop);
        Assert.Equal(1, _statusChanges);
    }

    // ---- A run that cannot be started ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_run_that_could_not_be_started_is_logged_and_does_not_keep_later_runs_from_starting(bool asked)
    {
        SetUp();
        int starts = 0;
        using var service = NewService(run =>
        {
            if (++starts == 1)
            {
                throw new InvalidOperationException("otter: no thread to be had");
            }

            return Inline(run);
        });
        var timer = _time.Timers[^1];
        Configure(service);

        if (asked)
        {
            service.SyncNow();
        }
        else
        {
            timer.Fire();
        }

        // Nothing came out of the call. The log has it, an owner who asked is told, and the clock goes on.
        Assert.Equal(["A sync run could not be started"], _log);
        Assert.Equal(asked ? new[] { SyncService.UnexpectedNote } : Array.Empty<string>(), _notes);
        Assert.Equal(0, _connector.Opens);
        Assert.Equal(TimeSpan.FromMinutes(5), timer.Due);

        timer.Fire();

        Assert.Equal(SyncCondition.Ok, service.Status.Condition);
        Assert.Equal(1, _connector.Opens);
    }

    // ---- The brake on mass deletion ----

    // So many library files, each with a text of its own, synced by a run by the clock.
    private List<string> Library(int files)
    {
        var paths = Enumerable.Range(1, files).Select(number => FormattableString.Invariant($"library/snippets/note-{number:000}.md")).ToList();
        foreach (string path in paths)
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        Timer.Fire();
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, _clock.UtcNow), _service.Status);
        Assert.Equal(files, _server.Names.Count);
        return paths;
    }

    // Every blob on the server, with its bytes. A blob that is sent again has other bytes, whatever it holds.
    private List<string> Server() => [.. _server.Names.Select(name => name + " " + Convert.ToHexString(_server.Get(name)!))];

    private const string ConfirmCommand = "Confirm Sync Deletions";
    private const string RestoreCommand = "Restore Missing Files";
    private const string PausedLine = "Sync is paused and nothing was sent because files that were synced are no longer in the data folder: ";

    private void DeleteTheLibrary() => Directory.Delete(_box.Full("library"), recursive: true);

    private static SyncStatus Paused(DateTimeOffset? lastSynced) => new(SyncCondition.Stopped, SyncStop.DeletionsHeld, lastSynced);

    // One of the two commands, by its title.
    private void Give(string command, SyncService? service = null)
    {
        if (command == ConfirmCommand)
        {
            (service ?? _service).ConfirmDeletions();
        }
        else
        {
            Assert.Equal(RestoreCommand, command);
            (service ?? _service).RestoreMissing();
        }
    }

    // What the server's blob for a path says it holds: the file, or the note that it was deleted.
    private BlobHeader OnTheServer(string path)
    {
        using var keys = SyncKeys.FromMaster(Master);
        byte[] copy = [.. _server.Get(BlobOf(path))!];
        return BlobCodec.Open(keys, BlobOf(path), copy, out _)!.Header;
    }

    [Fact]
    public void A_held_run_shows_sync_as_paused_is_said_once_by_the_clock_and_does_not_move_the_last_synced_time()
    {
        Ready();
        Library(8);
        var server = Server();
        byte[] record = File.ReadAllBytes(_paths.SyncStateFile);
        _clock.Advance(TimeSpan.FromMinutes(10));
        DeleteTheLibrary();
        _log.Clear();
        _server.Calls.Clear();

        Timer.Fire();
        Timer.Fire();
        Timer.Fire();

        // Shown in a line of its own, with "last synced" where it was. Said once, with the
        // count. One line in the log, with the count, for the three runs.
        Assert.Equal(new SyncStatus(SyncCondition.Stopped, SyncStop.DeletionsHeld, Start), _service.Status);
        Assert.Equal("Sync paused: synced files are missing here", Tray());
        Assert.Equal([SyncService.MissingFilesNote(8)], _notes);
        Assert.Equal([PausedLine + "8"], _log);

        // Nothing was sent and nothing was changed: each run asked the server for its listing
        // and for nothing else, and the record is the file the last good run wrote.
        Assert.Equal(["list", "list", "list"], _server.Calls);
        Assert.Equal(server, Server());
        Assert.Equal(record, File.ReadAllBytes(_paths.SyncStateFile));
        Assert.Equal([SyncFolderId.FileName], _box.Files());

        // Not a failure: every run connects, and the next one comes after the interval and no later.
        Assert.Equal(4, _connector.Opens);
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
    }

    [Fact]
    public void A_paused_sync_is_not_a_stop_that_waits_for_Reload_and_does_not_make_the_runs_come_less_often()
    {
        Ready();
        Library(8);
        DeleteTheLibrary();

        for (int run = 0; run < 6; run++)
        {
            Timer.Fire();
            Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
        }

        // Seven connections, the first for the run that synced the library: no run was skipped.
        Assert.Equal(7, _connector.Opens);
        Assert.Equal([SyncService.MissingFilesNote(8)], _notes);
    }

    [Fact]
    public void An_owner_who_asks_while_sync_is_paused_is_answered_every_time_with_the_count_as_it_is_then()
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        Timer.Fire();

        _service.SyncNow();
        _service.SyncNow();

        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.MissingFilesNote(8), SyncService.MissingFilesNote(8)], _notes);

        // Two of the files are back. Six of eight are still more than a quarter.
        _box.Write(paths[0], "the text of " + paths[0], Start.AddHours(-2));
        _box.Write(paths[1], "the text of " + paths[1], Start.AddHours(-2));
        _service.SyncNow();

        Assert.Equal(4, _notes.Count);
        Assert.Equal(SyncService.MissingFilesNote(6), _notes[3]);
        Assert.Equal(Paused(Start), _service.Status);

        // The clock said it once, and does not say it again.
        Timer.Fire();
        Assert.Equal(4, _notes.Count);
    }

    [Fact]
    public void The_clock_says_it_once_however_the_count_moves_and_the_log_has_one_line_for_each_count()
    {
        Ready();
        var paths = Library(40);
        foreach (string path in paths.Take(25))
        {
            _box.Delete(path);
        }

        _log.Clear();

        Timer.Fire();
        Timer.Fire();
        foreach (string path in paths.Skip(25).Take(5))
        {
            _box.Delete(path);
        }

        Timer.Fire();
        Timer.Fire();

        Assert.Equal([SyncService.MissingFilesNote(25)], _notes);
        Assert.Equal([PausedLine + "25", PausedLine + "30"], _log);
        Assert.Equal(Paused(Start), _service.Status);
    }

    [Fact]
    public void Sync_goes_on_by_itself_once_the_files_are_back_and_a_later_pause_is_said_again()
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        Assert.Equal(Paused(Start), _service.Status);

        // The owner puts the files back as they were. No command and no Reload: the next run by
        // the clock finds nothing missing, and "last synced" moves again.
        foreach (string path in paths)
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(5)), _service.Status);
        Assert.Equal([SyncService.MissingFilesNote(8)], _notes);

        // A run has completed without it, so the next time it is news, and logged, again.
        DeleteTheLibrary();
        _log.Clear();
        Timer.Fire();

        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.MissingFilesNote(8)], _notes);
        Assert.Equal([PausedLine + "8"], _log);
    }

    [Fact]
    public void Confirm_Sync_Deletions_starts_a_run_at_once_that_sends_the_deletions_and_says_how_it_went()
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        _clock.Advance(TimeSpan.FromMinutes(3));
        _server.Calls.Clear();

        _service.ConfirmDeletions();

        // The ordinary sentence for a run that went through, and "last synced" moves.
        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.DoneNote], _notes);
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(3)), _service.Status);
        Assert.Equal(8, _server.Count("upload"));
        Assert.All(paths, path => Assert.True(OnTheServer(path).Deleted));
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
    }

    [Fact]
    public void Restore_Missing_Files_starts_a_run_at_once_that_fetches_the_files_back_sends_nothing_and_says_how_it_went()
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        var server = Server();
        _clock.Advance(TimeSpan.FromMinutes(3));
        _server.Calls.Clear();

        _service.RestoreMissing();

        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.DoneNote], _notes);
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(3)), _service.Status);
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
        Assert.Equal(["list", .. Enumerable.Repeat("download", 8)], _server.Calls.Select(call => call.Split(' ')[0]));
        Assert.Equal(server, Server());
        Assert.Equal(TimeSpan.FromMinutes(5), Timer.Due);
    }

    [Fact]
    public void The_word_given_with_Confirm_Sync_Deletions_holds_for_that_one_run()
    {
        Ready();
        var paths = Library(16);
        foreach (string path in paths.Take(8))
        {
            _box.Delete(path);
        }

        Timer.Fire();
        _service.ConfirmDeletions();
        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.DoneNote], _notes);

        // Six of the eight files that are left go missing. That is another question, and it is
        // asked again: by the clock, and of an owner who only says Sync Now.
        foreach (string path in paths.Skip(8).Take(6))
        {
            _box.Delete(path);
        }

        var server = Server();
        Timer.Fire();
        _service.SyncNow();

        Assert.Equal(SyncStop.DeletionsHeld, _service.Status.Stop);
        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.DoneNote, SyncService.MissingFilesNote(6), SyncService.MissingFilesNote(6)], _notes);
        Assert.Equal(server, Server());
    }

    [Theory]
    [InlineData(ConfirmCommand)]
    [InlineData(RestoreCommand)]
    public void The_owners_word_is_used_up_by_the_run_it_starts_even_when_that_run_never_reaches_the_server(string command)
    {
        Ready();
        Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        var server = Server();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");

        Give(command);

        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.UnreachableNote], _notes);

        // The server is back. The run that comes by the clock is an ordinary run: it is held,
        // it sends nothing and it fetches nothing.
        _connector.Refusal = null;
        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(server, Server());
        Assert.Equal([SyncFolderId.FileName], _box.Files());
        Assert.Equal(2, _notes.Count);
    }

    [Theory]
    [InlineData(ConfirmCommand)]
    [InlineData(RestoreCommand)]
    public void A_command_given_while_a_run_is_at_work_is_told_that_sync_is_running_and_holds_for_the_run_that_follows_and_for_no_other(string command)
    {
        Ready();
        var paths = Library(16);
        foreach (string path in paths.Take(8))
        {
            _box.Delete(path);
        }

        // Sync is paused: a run by the clock was held, and said so.
        Timer.Fire();
        Assert.Equal([SyncService.MissingFilesNote(8)], _notes);
        _notes.Clear();
        var server = Server();

        // The next run by the clock is at work when the owner gives the command.
        _hook.Connecting = () => Give(command);
        Timer.Fire();

        // That run began without his word. It is held, and it says nothing of that: he asked
        // for another run, and that one answers him.
        Assert.Equal([SyncService.AlreadyRunningNote], _notes);
        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(server, Server());
        Assert.Equal(8, _box.Files().Count - 1);
        Assert.Equal(SyncService.StartDelay, Timer.Due);

        // The run that follows is the one he asked for.
        Timer.Fire();

        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        if (command == ConfirmCommand)
        {
            Assert.All(paths.Take(8), path => Assert.True(OnTheServer(path).Deleted));
            Assert.All(paths.Take(8), path => Assert.False(_box.Exists(path)));

            // The word was for that run. Six of the eight that are left go missing: held.
            foreach (string path in paths.Skip(8).Take(6))
            {
                _box.Delete(path);
            }

            server = Server();
            Timer.Fire();

            Assert.Equal(SyncStop.DeletionsHeld, _service.Status.Stop);
            Assert.Equal(SyncService.MissingFilesNote(6), _notes[^1]);
            Assert.Equal(server, Server());
        }
        else
        {
            Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
            Assert.Equal(server, Server());

            // The word was for that run. The same files go missing again: held, and not fetched back.
            foreach (string path in paths.Take(8))
            {
                _box.Delete(path);
            }

            Timer.Fire();

            Assert.Equal(SyncStop.DeletionsHeld, _service.Status.Stop);
            Assert.Equal(SyncService.MissingFilesNote(8), _notes[^1]);
            Assert.All(paths.Take(8), path => Assert.False(_box.Exists(path)));
            Assert.Equal(server, Server());
        }
    }

    [Theory]
    [InlineData(ConfirmCommand, RestoreCommand)]
    [InlineData(RestoreCommand, ConfirmCommand)]
    public void Of_two_commands_given_while_a_run_is_at_work_the_later_one_holds_for_the_run_that_follows(string first, string second)
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        Assert.Equal([SyncService.MissingFilesNote(8)], _notes);
        _notes.Clear();
        var server = Server();
        _hook.Connecting = () =>
        {
            Give(first);
            Give(second);
        };

        Timer.Fire();
        Timer.Fire();

        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);
        if (second == RestoreCommand)
        {
            Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
            Assert.Equal(server, Server());
        }
        else
        {
            Assert.All(paths, path => Assert.True(OnTheServer(path).Deleted));
            Assert.Equal([SyncFolderId.FileName], _box.Files());
        }
    }

    [Fact]
    public void A_command_given_while_a_run_is_at_work_holds_for_whichever_run_begins_next_also_one_the_owner_starts_with_Sync_Now()
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        var server = Server();
        _hook.Connecting = () => _service.RestoreMissing();
        Timer.Fire();

        // He does not wait for the clock.
        _service.SyncNow();

        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
        Assert.Equal(server, Server());
    }

    // Confirm Sync Deletions is answered before that: a service that has run nothing knows of
    // no pause, so there is nothing to confirm.
    [Theory]
    [InlineData(ConfirmCommand, SyncService.NotPausedNote)]
    [InlineData(RestoreCommand, SyncService.NoDataFolderNote)]
    public void A_command_given_before_Wyvern_has_a_data_folder_says_so_and_its_word_is_not_kept(string command, string answer)
    {
        Ready();
        Library(8);
        DeleteTheLibrary();
        var server = Server();
        using var later = NewService(Inline);
        var timer = _time.Timers[^1];

        Give(command, later);

        Assert.Equal([answer], _notes);
        Assert.Equal(1, _connector.Opens);

        // Once it has a data folder, its first run is an ordinary run.
        Configure(later);
        timer.Fire();

        Assert.Equal(Paused(null), later.Status);
        Assert.Equal(server, Server());
        Assert.Equal([SyncFolderId.FileName], _box.Files());
    }

    [Theory]
    [InlineData(ConfirmCommand)]
    [InlineData(RestoreCommand)]
    public void A_command_whose_run_could_not_be_started_leaves_no_word_behind(string command)
    {
        Ready();
        Library(8);
        DeleteTheLibrary();
        var server = Server();
        int starts = 0;
        using var service = NewService(run =>
        {
            if (++starts == 2)
            {
                throw new InvalidOperationException("otter: no thread to be had");
            }

            return Inline(run);
        });
        var timer = _time.Timers[^1];
        Configure(service);

        // Sync is paused: this service's first run is held.
        timer.Fire();
        Assert.Equal([SyncService.MissingFilesNote(8)], _notes);

        Give(command, service);

        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.UnexpectedNote], _notes);

        // The run that the clock starts next is an ordinary run.
        timer.Fire();

        Assert.Equal(Paused(null), service.Status);
        Assert.Equal(server, Server());
        Assert.Equal([SyncFolderId.FileName], _box.Files());
    }

    [Fact]
    public void With_nothing_held_Confirm_Sync_Deletions_starts_nothing_and_says_that_sync_is_not_paused_and_Restore_Missing_Files_brings_back_whatever_is_missing()
    {
        Ready();
        var paths = Library(8);
        _box.Delete(paths[0]);
        var server = Server();
        var due = Timer.Due;
        _server.Calls.Clear();

        // A deletion cannot be allowed in advance: with no pause there is nothing to confirm.
        // No run begins, no connection is made, and nothing a later run does is changed by it.
        _service.ConfirmDeletions();

        Assert.Equal([SyncService.NotPausedNote], _notes);
        Assert.Equal("Sync is not paused. There is nothing to confirm.", SyncService.NotPausedNote);
        Assert.Equal(1, _connector.Opens);
        Assert.Empty(_server.Calls);
        Assert.Equal(server, Server());
        Assert.Equal(due, Timer.Due);
        Assert.Equal(SyncPause.None, _service.Pause);
        Assert.False(File.Exists(Mark));

        // Three files are missing, which is fewer than the brake holds: an ordinary run would
        // send their deletions. Restore Missing Files fetches them back, at any time.
        _box.Delete(paths[1]);
        _box.Delete(paths[2]);

        _service.RestoreMissing();

        Assert.Equal([SyncService.NotPausedNote, SyncService.DoneNote], _notes);
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
        Assert.All(paths, path => Assert.False(OnTheServer(path).Deleted));
        Assert.Equal(server, Server());
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
    }

    // Without a setup nothing was ever held, so Confirm Sync Deletions has nothing to confirm.
    [Theory]
    [InlineData(ConfirmCommand, SyncService.NotPausedNote)]
    [InlineData(RestoreCommand, SyncService.NotSetUpNote)]
    public void Either_command_without_a_setup_says_so_and_makes_no_connection(string command, string answer)
    {
        Configure();

        Give(command);

        Assert.Equal([answer], _notes);
        Assert.Equal(0, _connector.Opens);
        Assert.Equal(SyncStatus.Off, _service.Status);
    }

    [Theory]
    [InlineData(ConfirmCommand)]
    [InlineData(RestoreCommand)]
    public void Either_command_is_the_owner_acting_so_a_stop_that_waited_for_him_is_tried_once_more_as_after_Sync_Now(string command)
    {
        // Sync is paused, and then the server shows another key.
        Ready();
        Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        _connector.Fingerprint = MemoryConnector.OtherKey;
        Timer.Fire();
        Timer.Fire();

        // Stopped until the owner acts: the last run by the clock made no connection.
        Assert.Equal(3, _connector.Opens);
        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.HostKeyNote], _notes);
        _server.Calls.Clear();

        Give(command);

        // One more try, which the server's key ends as it ended the one before. Nothing is
        // accepted, and nothing is asked of the server.
        Assert.Equal(4, _connector.Opens);
        Assert.Equal([SyncService.MissingFilesNote(8), SyncService.HostKeyNote, SyncService.HostKeyNote], _notes);
        Assert.Equal(SyncStop.HostKeyChanged, _service.Status.Stop);
        Assert.Empty(_server.Calls);
        Assert.True(File.Exists(Mark));
    }

    [Fact]
    public void Confirm_Sync_Deletions_does_not_lift_a_stop_that_waits_for_the_owner_when_sync_is_not_paused()
    {
        Ready();
        _connector.Fingerprint = MemoryConnector.OtherKey;
        Timer.Fire();
        Assert.Equal(1, _connector.Opens);

        // It starts nothing, so it is not the owner acting on the stop: no connection is tried.
        _service.ConfirmDeletions();
        Timer.Fire();

        Assert.Equal([SyncService.HostKeyNote, SyncService.NotPausedNote], _notes);
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(SyncStop.HostKeyChanged, _service.Status.Stop);
    }

    [Fact]
    public void The_service_says_whether_sync_is_paused_and_how_many_deletions_the_last_run_held()
    {
        Ready();
        Assert.Equal(SyncPause.None, _service.Pause);
        var paths = Library(40);
        Assert.Equal(SyncPause.None, _service.Pause);

        // A run is held: paused, with its count. The count follows every held run.
        foreach (string path in paths.Take(12))
        {
            _box.Delete(path);
        }

        Timer.Fire();
        Assert.Equal(new SyncPause(true, 12), _service.Pause);

        foreach (string path in paths.Take(11))
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        Timer.Fire();
        Assert.Equal(new SyncPause(true, 1), _service.Pause);

        // A run that never reaches the server learns nothing new: still paused, the count as it was.
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");
        Timer.Fire();
        _connector.Refusal = null;
        Assert.Equal(SyncCondition.Unreachable, _service.Status.Condition);
        Assert.Equal(new SyncPause(true, 1), _service.Pause);

        // A run that restores does not end the pause, and it was not held: paused still. It
        // completed, so the file is back and nothing is held, until the next ordinary run has
        // seen that.
        _service.RestoreMissing();
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(new SyncPause(true, 0, NothingHeld: true), _service.Pause);

        // The ordinary run that follows finds nothing to delete: not paused.
        Timer.Fire();
        Assert.Equal(SyncPause.None, _service.Pause);
        Assert.Equal(new SyncPause(false), SyncPause.None);
    }

    [Fact]
    public void After_a_restart_the_pause_is_known_from_the_mark_also_after_a_run_that_never_reaches_the_server()
    {
        Ready();
        var paths = Library(40);
        foreach (string path in paths.Take(12))
        {
            _box.Delete(path);
        }

        Timer.Fire();
        _service.Dispose();
        using var again = NewService(Inline);
        var timer = _time.Timers[^1];
        Configure(again);

        // Before the first run the mark is looked at when it is asked about (see
        // Before_any_run_has_looked_...): paused, and the count is not known.
        Assert.Equal(new SyncPause(true), again.Pause);

        // Its first run cannot reach the server. It has read the mark all the same: paused,
        // and the count is not known. Confirm Sync Deletions is then a run with his word.
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");
        timer.Fire();
        Assert.Equal(new SyncPause(true), again.Pause);
        again.ConfirmDeletions();
        Assert.Equal(SyncService.UnreachableNote, _notes[^1]);
        Assert.All(paths, path => Assert.False(OnTheServer(path).Deleted));

        // The server is back. His word was for the run that failed: the next one is held, and
        // then the count is known, and he confirms.
        _connector.Refusal = null;
        timer.Fire();
        Assert.Equal(new SyncPause(true, 12), again.Pause);
        again.ConfirmDeletions();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.Equal(12, paths.Count(path => OnTheServer(path).Deleted));
        Assert.Equal(SyncPause.None, again.Pause);
        Assert.False(File.Exists(Mark));
    }

    // Wyvern was closed while sync was paused: twelve of forty library files are missing and
    // the mark is on the disk. Returns a new service on the same folders, which has run nothing.
    private (SyncService Service, ManualTimer Timer, List<string> Paths) AWyvernThatStartsAgainWhileSyncIsPaused()
    {
        Ready();
        var paths = Library(40);
        foreach (string path in paths.Take(12))
        {
            _box.Delete(path);
        }

        Timer.Fire();
        Assert.Equal([SyncService.MissingFilesNote(12)], _notes);
        Assert.True(File.Exists(Mark));
        _service.Dispose();
        _notes.Clear();
        var again = NewService(Inline);
        var timer = _time.Timers[^1];
        Configure(again);
        return (again, timer, paths);
    }

    [Fact]
    public void Before_any_run_has_looked_Confirm_Sync_Deletions_looks_at_the_mark_itself_and_asks_for_the_word()
    {
        var (again, _, paths) = AWyvernThatStartsAgainWhileSyncIsPaused();
        using (again)
        {
            int opens = _connector.Opens;

            // The commands of the launcher over this service, as the shell wires them. The first
            // run is ten seconds away: until a run has read the mark, the service goes by looking
            // at it once, and does not answer "Sync is not paused" while the mark is there.
            var commands = SyncCommands.Create(() => { }, () => { }, () => { }, () => again.Pause, again.ConfirmDeletions, again.RestoreMissing, _notes.Add);
            var confirm = commands[3].Argument!;

            Assert.Equal(new SyncPause(true), again.Pause);
            Assert.Equal("Type yes to delete the missing files on your other machines too", confirm.Prompt);

            // Another word: nothing starts, and he is told sync is still paused.
            confirm.Run("no");

            Assert.Equal([SyncCommands.NotConfirmedNote], _notes);
            Assert.Equal(opens, _connector.Opens);

            // The word: the run is a run with his word, and the pause is over once it completes.
            confirm.Run("yes");

            Assert.Equal([SyncCommands.NotConfirmedNote, SyncService.DoneNote], _notes);
            Assert.Equal(12, paths.Count(path => OnTheServer(path).Deleted));
            Assert.False(File.Exists(Mark));
            Assert.Equal(SyncPause.None, again.Pause);
        }
    }

    [Fact]
    public void Before_any_run_has_looked_the_service_itself_says_the_same_as_the_command_does()
    {
        var (again, _, paths) = AWyvernThatStartsAgainWhileSyncIsPaused();
        using (again)
        {
            var server = Server();

            again.ConfirmDeletions();

            Assert.Equal([SyncService.DoneNote], _notes);
            Assert.DoesNotContain(SyncService.NotPausedNote, _notes);
            Assert.Equal(12, paths.Count(path => OnTheServer(path).Deleted));
            Assert.NotEqual(server, Server());
        }
    }

    [Fact]
    public void Before_any_run_has_looked_and_with_no_mark_Confirm_Sync_Deletions_still_says_that_sync_is_not_paused()
    {
        Ready();
        Library(8);
        using var again = NewService(Inline);
        Configure(again);
        _notes.Clear();
        int opens = _connector.Opens;

        Assert.Equal(SyncPause.None, again.Pause);
        again.ConfirmDeletions();

        Assert.Equal([SyncService.NotPausedNote], _notes);
        Assert.Equal(opens, _connector.Opens);
    }

    [Fact]
    public void Once_a_run_has_looked_at_the_mark_the_command_goes_by_what_the_runs_have_seen()
    {
        Ready();
        Library(8);
        _notes.Clear();

        // A run has found no mark. One that appears afterwards, which no run of this service
        // made, is for the next run to find: the command does not look at the disk again.
        File.WriteAllText(Mark, SyncService.PausedMarkText);

        Assert.Equal(SyncPause.None, _service.Pause);
        _service.ConfirmDeletions();
        Assert.Equal([SyncService.NotPausedNote], _notes);

        // The next run reads the mark, as it starts, whether or not it reaches the server.
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");
        Timer.Fire();
        _connector.Refusal = null;

        Assert.Equal(new SyncPause(true), _service.Pause);
    }

    [Fact]
    public void Confirm_Sync_Deletions_given_while_a_run_is_at_work_allows_nothing_once_that_run_has_ended_the_pause()
    {
        Ready();
        var paths = Library(40);
        foreach (string path in paths.Take(12))
        {
            _box.Delete(path);
        }

        Timer.Fire();
        Assert.Equal(new SyncPause(true, 12), _service.Pause);

        // The files are put back. While the run that will find them back is at work, the owner
        // gives Confirm Sync Deletions: sync is paused at that moment, so his word is taken for
        // the run that follows.
        foreach (string path in paths.Take(12))
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        _hook.Connecting = () => _service.ConfirmDeletions();
        Timer.Fire();

        Assert.Equal([SyncService.MissingFilesNote(12), SyncService.AlreadyRunningNote], _notes);
        Assert.Equal(SyncPause.None, _service.Pause);
        Assert.False(File.Exists(Mark));

        // Before that run begins, twelve other files go missing. His word answered a pause
        // that is over, and allows nothing now: the run is an ordinary one, and it is held.
        foreach (string path in paths.Skip(20).Take(12))
        {
            _box.Delete(path);
        }

        var server = Server();
        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(server, Server());
        Assert.Equal(SyncService.MissingFilesNote(12), _notes[^1]);
        Assert.Equal(3, _notes.Count);
    }

    // ---- Paused, and nothing held ----
    //
    // After a Restore Missing Files run that completed, the files are back and sync is still
    // paused, until the ordinary run that follows has seen that. Nothing is held then, and
    // Confirm Sync Deletions must not speak as if something were.

    [Fact]
    public void After_a_completed_Restore_Missing_Files_run_sync_is_paused_with_nothing_held_and_Confirm_Sync_Deletions_says_so_and_starts_nothing()
    {
        Ready();
        Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        Assert.Equal(new SyncPause(true, 8), _service.Pause);
        _clock.Advance(TimeSpan.FromMinutes(3));

        _service.RestoreMissing();

        // The files are back, and the mark is there still: paused, and nothing is held.
        Assert.True(File.Exists(Mark));
        Assert.Equal(new SyncPause(true, 0, NothingHeld: true), _service.Pause);

        // The tray says how the last run ended, which was well, and nothing of a pause.
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(3)), _service.Status);
        Assert.Equal("Last synced 12:03", Tray());
        Assert.DoesNotContain("paused", Tray(), StringComparison.OrdinalIgnoreCase);

        // Confirm Sync Deletions says that nothing is waiting, in fixed words, and starts
        // nothing: no connection, no call, no change to the clock or the pause.
        _notes.Clear();
        int opens = _connector.Opens;
        var due = Timer.Due;
        var server = Server();
        _server.Calls.Clear();

        _service.ConfirmDeletions();

        Assert.Equal([SyncService.NothingWaitingNote], _notes);
        Assert.Equal("Nothing is waiting to be confirmed.", SyncService.NothingWaitingNote);
        Assert.Equal(opens, _connector.Opens);
        Assert.Empty(_server.Calls);
        Assert.Equal(server, Server());
        Assert.Equal(due, Timer.Due);
        Assert.True(File.Exists(Mark));
        Assert.Equal(new SyncPause(true, 0, NothingHeld: true), _service.Pause);

        // The next ordinary run sees it too: the pause is over, and sync goes on by itself.
        Timer.Fire();

        Assert.False(File.Exists(Mark));
        Assert.Equal(SyncPause.None, _service.Pause);
        Assert.Equal([SyncService.NothingWaitingNote], _notes);
        _service.ConfirmDeletions();
        Assert.Equal([SyncService.NothingWaitingNote, SyncService.NotPausedNote], _notes);
    }

    [Fact]
    public void A_Restore_Missing_Files_run_when_nothing_was_missing_leaves_sync_paused_with_nothing_held_and_Confirm_Sync_Deletions_is_no_permission_given_in_advance()
    {
        Ready();
        var paths = Library(8);

        _service.RestoreMissing();

        Assert.Equal(new SyncPause(true, 0, NothingHeld: true), _service.Pause);
        Assert.Equal("Last synced 12:00", Tray());
        _notes.Clear();
        _service.ConfirmDeletions();
        Assert.Equal([SyncService.NothingWaitingNote], _notes);

        // A file is deleted after that, and Confirm Sync Deletions is given again: it allows
        // nothing, so the run that comes by the clock is held for that single file, as it is
        // while sync is paused, and says so.
        _box.Delete(paths[0]);
        var server = Server();
        _service.ConfirmDeletions();
        Assert.Equal([SyncService.NothingWaitingNote, SyncService.NothingWaitingNote], _notes);

        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(server, Server());
        Assert.Equal(SyncService.MissingFilesNote(1), _notes[^1]);
        Assert.Equal(new SyncPause(true, 1), _service.Pause);

        // Something is held now, and Confirm Sync Deletions answers it.
        _service.ConfirmDeletions();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.True(OnTheServer(paths[0]).Deleted);
        Assert.False(File.Exists(Mark));
        Assert.Equal(SyncPause.None, _service.Pause);
    }

    [Fact]
    public void A_Restore_Missing_Files_run_that_was_cut_off_does_not_say_that_nothing_is_held_and_Confirm_Sync_Deletions_still_answers_the_pause()
    {
        Ready();
        var paths = Library(8);
        foreach (string path in paths.Take(4))
        {
            _box.Delete(path);
        }

        int downloads = 0;
        _server.Fault = call => call.StartsWith("download ", StringComparison.Ordinal) && ++downloads > 2
            ? new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10054")
            : null;
        _service.RestoreMissing();
        _server.Fault = null;

        // Two of the four are back and two are missing: paused, and the count is not known.
        Assert.Equal(new SyncPause(true), _service.Pause);
        Assert.Equal("Type yes to delete the missing files on your other machines too", SyncCommands.ConfirmPrompt(_service.Pause));

        _service.ConfirmDeletions();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.Equal(2, paths.Take(4).Count(path => OnTheServer(path).Deleted));
        Assert.Equal(SyncPause.None, _service.Pause);
    }

    [Fact]
    public void A_Restore_Missing_Files_run_that_could_not_look_at_the_whole_library_does_not_say_that_nothing_is_held()
    {
        Ready();
        var (_, removed) = AToolRemovedThirtyOfAHundred();

        // For this run the folder that holds the library cannot be listed: it fetches nothing,
        // and it cannot see whether anything is missing.
        string locked = _box.Full("library/snippets");
        _box.DenyListing(locked);
        try
        {
            _service.RestoreMissing();
        }
        finally
        {
            _box.Allow(locked);
        }

        Assert.Equal(SyncCondition.Incomplete, _service.Status.Condition);
        Assert.True(File.Exists(Mark));
        Assert.Equal(new SyncPause(true), _service.Pause);

        // The thirty are still missing, and the owner can still answer.
        Assert.Equal(30, removed.Count(path => !_box.Exists(path)));
        _notes.Clear();
        _service.ConfirmDeletions();
        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.All(removed, path => Assert.True(OnTheServer(path).Deleted));
    }

    [Fact]
    public void A_Restore_Missing_Files_run_that_left_a_file_for_later_does_not_say_that_nothing_is_held()
    {
        Ready();
        var paths = Library(8);
        foreach (string path in paths.Take(3))
        {
            _box.Delete(path);
        }

        // The server refuses one download. The run goes on and completes, with that file left
        // for the next run: it is still missing.
        bool refused = false;
        _server.Fault = call =>
        {
            if (call.StartsWith("download ", StringComparison.Ordinal) && !refused)
            {
                refused = true;
                return new RemoteStoreException(RemoteFailure.Server, "an error of the server");
            }

            return null;
        };
        _service.RestoreMissing();
        _server.Fault = null;

        Assert.Equal(SyncCondition.Incomplete, _service.Status.Condition);
        Assert.Equal(1, paths.Take(3).Count(path => !_box.Exists(path)));
        Assert.Equal(new SyncPause(true), _service.Pause);

        _service.ConfirmDeletions();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.Equal(1, paths.Take(3).Count(path => OnTheServer(path).Deleted));
    }

    [Fact]
    public void Confirm_Sync_Deletions_given_while_a_restore_run_is_at_work_allows_nothing_once_that_run_has_left_nothing_held()
    {
        Ready();
        var paths = Library(8);
        DeleteTheLibrary();
        Timer.Fire();
        _notes.Clear();

        // The owner gives Restore Missing Files, and Confirm Sync Deletions while it is at work.
        // Sync is paused with something held at that moment, so his word is taken for the run
        // that follows.
        _hook.Connecting = () => _service.ConfirmDeletions();
        _service.RestoreMissing();

        Assert.Equal([SyncService.AlreadyRunningNote, SyncService.DoneNote], _notes);
        Assert.All(paths, path => Assert.True(_box.Exists(path)));
        Assert.Equal(new SyncPause(true, 0, NothingHeld: true), _service.Pause);

        // Before that run begins, three files are deleted. His word answered something that is
        // over, and allows nothing: the run is an ordinary one, and it is held for the three.
        foreach (string path in paths.Take(3))
        {
            _box.Delete(path);
        }

        var server = Server();
        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(server, Server());
        Assert.Equal(SyncService.MissingFilesNote(3), _notes[^1]);
        Assert.Equal(new SyncPause(true, 3), _service.Pause);
        Assert.True(File.Exists(Mark));
    }

    [Fact]
    public void The_tray_line_for_a_paused_sync_fits_beside_the_longest_text_about_clipboard_capture()
    {
        var paused = Paused(Start.AddDays(-400));

        string line = paused.Tooltip(Start, TimeZoneInfo.Utc);
        string whole = paused.WithCapture(ClipCapture.PausedTooltip, Start, TimeZoneInfo.Utc);

        // The same line whether or not a run ever went through; it gives no time and no count.
        Assert.Equal("Sync paused: synced files are missing here", line);
        Assert.Equal(line, Paused(null).Tooltip(Start, TimeZoneInfo.Utc));
        Assert.True(line.Length <= SyncStatus.MaxTooltipLength);
        Assert.Equal("Wyvern (clipboard capture paused). Sync paused: synced files are missing here", whole);
        Assert.True(whole.Length <= 127);
        Assert.All(new[] { ClipCapture.OnTooltip, ClipCapture.OffTooltip }, capture => Assert.True(capture.Length <= ClipCapture.PausedTooltip.Length));
    }

    [Fact]
    public void Nothing_that_is_said_or_logged_about_a_paused_sync_names_a_file_a_folder_the_server_or_the_user()
    {
        Ready();
        Library(8);
        _service.ConfirmDeletions();
        DeleteTheLibrary();
        Timer.Fire();
        _service.SyncNow();
        _service.RestoreMissing();
        DeleteTheLibrary();
        Timer.Fire();
        string mark = File.ReadAllText(Mark);
        var pause = _service.Pause;
        _service.ConfirmDeletions();

        // What is said and logged, how sync stands, the mark on the disk, and every text the
        // Confirm Sync Deletions command can show.
        var everything = _log.Concat(_notes)
            .Append(_service.Status.ToString())
            .Append(Paused(Start).ToString())
            .Append(Paused(Start).Tooltip(_clock.UtcNow, TimeZoneInfo.Utc))
            .Append(mark)
            .Append(pause.ToString())
            .Append(SyncCommands.ConfirmPrompt(pause))
            .Append(SyncCommands.ConfirmPrompt(new SyncPause(true)))
            .Append(SyncCommands.ConfirmPrompt(new SyncPause(true, 1)))
            .Append(SyncCommands.ConfirmPrompt(new SyncPause(true, 0, NothingHeld: true)))
            .Append(new SyncPause(true, 0, NothingHeld: true).ToString())
            .Append(SyncCommands.ConfirmPrompt(SyncPause.None))
            .Append(SyncCommands.NotConfirmedNote)
            .Append(SyncService.NothingWaitingNote)
            .Append(MarkNotWrittenLine)
            .Append(MarkNotRemovedLine)
            .ToList();

        Assert.Contains(PausedLine + "8", everything);
        Assert.Contains(SyncService.MissingFilesNote(8), everything);
        Assert.Contains(SyncService.NotPausedNote, _notes);
        Assert.Contains("SyncStatus { Condition = Stopped, Stop = DeletionsHeld }", everything);
        Assert.Contains("SyncPause { Paused = True, Held = 8, NothingHeld = False }", everything);
        Assert.Contains("SyncPause { Paused = True, Held = 0, NothingHeld = True }", everything);
        Assert.Contains("Nothing is waiting to be confirmed.", everything);
        Assert.Contains("Type yes to delete 8 files on your other machines too", everything);
        Assert.Contains(SyncService.PausedMarkText, everything);
        foreach (string word in new[] { "secret", "otter", "walrus", "SecretKeys", "library", "snippets", "note-", ".md", "home", "\\", "/" })
        {
            Assert.DoesNotContain(everything, text => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- A pause, once begun, lasts until the owner has answered it ----
    //
    // The pause is remembered in a mark in the Wyvern root. While it is there, a run is held
    // for the deletion of a single library file, whatever the count. It ends in two ways: an
    // ordinary run that finds nothing to delete, and a Confirm Sync Deletions run that completed.

    private const string MarkNotWrittenLine = "Sync could not write the mark that keeps it paused, so the next run counts the missing files afresh";
    private const string MarkNotRemovedLine = "Sync could not remove the mark that keeps it paused, so it stays paused";

    private string Mark => _paths.SyncPausedFile;

    // The owner's other machine: a data folder of its own and the same server. Its runs are
    // plain runs, as SyncRunTests makes them: no second service, no clock, no mark.
    private SyncSandbox? _sealBox;

    private SyncSandbox Seal => _sealBox ??= new SyncSandbox();

    private SyncResult TheSealSyncs()
    {
        string record = Seal.RootFile("sync-state.json");
        using var keys = SyncKeys.FromMaster(Master);
        return SyncRun.Execute(new SyncRunContext(
            Seal.Data,
            record,
            SyncState.Load(record, "scope-of-the-seal", out _, out _),
            _server,
            keys,
            "SEAL-PC",
            name => name.Equals("SEAL-PC", StringComparison.OrdinalIgnoreCase),
            90,
            _clock,
            (_, _) => { },
            () => false,
            _ => { }));
    }

    // Every file in a data folder with its text, but for the id file a service gives its folder.
    private static List<string> Folder(SyncSandbox box) =>
        [.. box.Files().Where(path => path != SyncFolderId.FileName).Select(path => path + " | " + box.Read(path))];

    // A cleanup tool removes thirty of a hundred library files, both machines having them all.
    // No run has been held for it yet: sync is not paused, and no mark is there.
    private (List<string> Paths, List<string> Removed) AToolRemovedThirtyOfAHundredBeforeAnyRun()
    {
        var paths = Library(100);
        Assert.Equal(100, TheSealSyncs().Downloaded);
        var removed = paths.Where((_, index) => index % 10 < 3).ToList();
        Assert.Equal(30, removed.Count);
        foreach (string path in removed)
        {
            _box.Delete(path);
        }

        Assert.False(File.Exists(Mark));
        return (paths, removed);
    }

    // The same, and the next run by the clock is held: sync is paused.
    private (List<string> Paths, List<string> Removed) AToolRemovedThirtyOfAHundred()
    {
        var made = AToolRemovedThirtyOfAHundredBeforeAnyRun();
        Timer.Fire();
        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal([SyncService.MissingFilesNote(30)], _notes);
        return made;
    }

    // The next run by the clock while sync is paused: held, and it sends nothing. The server
    // is asked for its listing and for nothing else, and the other machine deletes nothing.
    // The log has its line with the count when the pause is news or the count has moved, and
    // nothing when neither is so.
    private void AssertHeldByTheClock(int? logged, ManualTimer? timer = null, SyncService? service = null)
    {
        var server = Server();
        _log.Clear();
        _server.Calls.Clear();

        (timer ?? Timer).Fire();

        Assert.Equal(SyncStop.DeletionsHeld, (service ?? _service).Status.Stop);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(server, Server());
        Assert.Equal(logged is { } missing ? [PausedLine + missing.ToString(CultureInfo.InvariantCulture)] : [], _log);
        Assert.True(File.Exists(Mark));
        var onTheSeal = TheSealSyncs();
        Assert.Equal(SyncEnd.Completed, onTheSeal.End);
        Assert.Equal(0, onTheSeal.DeletedHere);
    }

    [Fact]
    public void A_pause_is_marked_in_the_Wyvern_root_and_ends_by_itself_only_when_every_missing_file_is_back()
    {
        Ready();
        var paths = Library(40);
        Assert.False(File.Exists(Mark));
        foreach (string path in paths.Take(12))
        {
            _box.Delete(path);
        }

        Timer.Fire();

        // The mark is a file of fixed words beside sync.json, and not in the data folder.
        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(Path.Combine(_box.Root, "sync-paused"), Mark);
        Assert.Equal(SyncService.PausedMarkText, File.ReadAllText(Mark));
        Assert.Equal("Wyvern keeps this file while sync is paused, and removes it when the pause is over. To end the pause, run Confirm Sync Deletions or Restore Missing Files in Wyvern.\n", SyncService.PausedMarkText);

        // Eleven of the twelve are put back. One missing file of forty would be a deleted file
        // on any other day. A pause with one file still missing is still a pause.
        foreach (string path in paths.Take(11))
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        var server = Server();
        Timer.Fire();
        _service.SyncNow();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal([SyncService.MissingFilesNote(12), SyncService.MissingFilesNote(1)], _notes);
        Assert.Equal(server, Server());
        Assert.True(File.Exists(Mark));

        // The last one is back, and something else has changed meanwhile. The next run by the
        // clock finds nothing to delete: the pause is over, the mark is gone, and that run
        // goes on as any run does.
        _box.Write(paths[11], "the text of " + paths[11], Start.AddHours(-2));
        _box.Write(paths[20], "edited while sync was paused", Start.AddMinutes(-1));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Timer.Fire();

        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(5)), _service.Status);
        Assert.False(File.Exists(Mark));
        Assert.Equal(2, _notes.Count);
        Assert.Equal(BlobCodec.Hash(Encoding.UTF8.GetBytes("edited while sync was paused")), OnTheServer(paths[20]).Sha256);
        Assert.All(paths, path => Assert.False(OnTheServer(path).Deleted));

        // Not paused any more: one missing file of forty is a deleted file again.
        _box.Delete(paths[0]);
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.True(OnTheServer(paths[0]).Deleted);
        Assert.False(File.Exists(Mark));
    }

    [Fact]
    public void A_Restore_Missing_Files_run_that_is_cut_off_leaves_the_rest_held_and_a_second_one_finishes_the_job()
    {
        Ready();
        var (paths, removed) = AToolRemovedThirtyOfAHundred();

        // The owner says he did not delete them. The connection goes after twelve are back.
        int downloads = 0;
        _server.Fault = call => call.StartsWith("download ", StringComparison.Ordinal) && ++downloads > 12
            ? new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10054")
            : null;
        _server.Calls.Clear();
        _service.RestoreMissing();
        _server.Fault = null;

        Assert.Equal([SyncService.MissingFilesNote(30), SyncService.UnreachableNote], _notes);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 12)], _server.Calls.Select(call => call.Split(' ')[0]));
        Assert.Equal(18, removed.Count(path => !_box.Exists(path)));
        Assert.True(File.Exists(Mark));

        // Eighteen of a hundred are fewer than begin a pause. But sync is paused: the next run
        // by the clock is held, and the eighteen are not sent as deletions. Nor by the run after.
        AssertHeldByTheClock(logged: 18);
        AssertHeldByTheClock(logged: null);
        Assert.Equal(100, Seal.Files().Count);

        // A second Restore Missing Files finishes the job, and writes nothing to the server.
        var server = Server();
        _server.Calls.Clear();
        _service.RestoreMissing();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 18)], _server.Calls.Select(call => call.Split(' ')[0]));
        Assert.Equal(server, Server());
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));

        // A run that restores does not end the pause by itself. The ordinary run that follows
        // it does, when it finds nothing to delete.
        Assert.True(File.Exists(Mark));
        Timer.Fire();

        Assert.False(File.Exists(Mark));
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(server, Server());

        // Both machines hold all hundred files.
        Assert.Equal(0, TheSealSyncs().DeletedHere);
        Assert.Equal(100, Seal.Files().Count);
        Assert.Equal(Folder(Seal), Folder(_box));
    }

    // The review's probe 4: the owner gives Restore Missing Files before any run was held, so
    // there is no pause yet, and the run that restores is cut off. Eighteen of a hundred are
    // fewer than begin a pause, so only a mark that the run itself wrote holds them.
    [Fact]
    public void A_Restore_Missing_Files_run_given_before_any_run_was_held_begins_a_pause_so_that_a_cut_off_leaves_the_rest_held_and_a_second_one_finishes_the_job()
    {
        Ready();
        var (paths, removed) = AToolRemovedThirtyOfAHundredBeforeAnyRun();

        // The connection goes after twelve are back.
        int downloads = 0;
        _server.Fault = call => call.StartsWith("download ", StringComparison.Ordinal) && ++downloads > 12
            ? new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10054")
            : null;
        _server.Calls.Clear();
        _service.RestoreMissing();
        _server.Fault = null;

        Assert.Equal([SyncService.UnreachableNote], _notes);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 12)], _server.Calls.Select(call => call.Split(' ')[0]));
        Assert.Equal(18, removed.Count(path => !_box.Exists(path)));
        Assert.True(File.Exists(Mark));

        // The next run by the clock is held, and the eighteen are not sent as deletions. Nor by
        // the run after it.
        AssertHeldByTheClock(logged: 18);
        AssertHeldByTheClock(logged: null);
        Assert.Equal([SyncService.UnreachableNote, SyncService.MissingFilesNote(18)], _notes);
        Assert.Equal(100, Seal.Files().Count);

        // A second Restore Missing Files finishes the job, and writes nothing to the server.
        var server = Server();
        _server.Calls.Clear();
        _service.RestoreMissing();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 18)], _server.Calls.Select(call => call.Split(' ')[0]));
        Assert.Equal(server, Server());
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));

        // The run that restores did not end the pause. The ordinary run after it does.
        Assert.True(File.Exists(Mark));
        Timer.Fire();

        Assert.False(File.Exists(Mark));
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal(server, Server());

        // Both machines hold all hundred files.
        Assert.Equal(0, TheSealSyncs().DeletedHere);
        Assert.Equal(100, Seal.Files().Count);
        Assert.Equal(Folder(Seal), Folder(_box));
    }

    // The same, cut off before the first file: the run could not even connect. What the owner
    // said, that he did not delete them, does not lapse with it.
    [Fact]
    public void A_Restore_Missing_Files_run_that_could_not_reach_the_server_has_begun_the_pause_all_the_same()
    {
        Ready();
        var paths = Library(8);
        foreach (string path in paths.Take(3))
        {
            _box.Delete(path);
        }

        var server = Server();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10060");
        _service.RestoreMissing();
        _connector.Refusal = null;

        Assert.Equal([SyncService.UnreachableNote], _notes);
        Assert.True(File.Exists(Mark));

        // Three of eight are fewer than begin a pause, and an ordinary run would send them as
        // deletions. This one is held.
        _server.Calls.Clear();
        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(server, Server());
        Assert.Equal(SyncService.MissingFilesNote(3), _notes[^1]);

        _service.RestoreMissing();
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
        Assert.Equal(server, Server());
    }

    // A restore run begins a pause when nothing was missing as well: it cannot know that
    // beforehand, and it does not say anything of it.
    [Fact]
    public void A_Restore_Missing_Files_run_when_nothing_was_missing_writes_the_mark_as_it_starts_and_the_next_ordinary_run_removes_it_and_says_nothing_of_a_pause()
    {
        Ready();
        var paths = Library(8);
        Assert.False(File.Exists(Mark));

        // The mark is there before the run asks the server for anything.
        bool markedAtTheFirstCall = false;
        _server.Fault = call =>
        {
            if (call == "list")
            {
                markedAtTheFirstCall = File.Exists(Mark);
            }

            return null;
        };
        _service.RestoreMissing();
        _server.Fault = null;

        Assert.True(markedAtTheFirstCall);
        Assert.Equal([SyncService.DoneNote], _notes);
        Assert.Equal(SyncService.PausedMarkText, File.ReadAllText(Mark));
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start), _service.Status);
        Assert.Equal("Last synced 12:00", Tray());
        Assert.True(_service.Pause.Paused);

        // The ordinary run that follows finds nothing to delete: the mark is gone, and that run
        // goes on as any run does. Nothing is said to the owner, in a notification or the log.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _box.Write(paths[2], "edited after the restore", Start.AddMinutes(1));
        Timer.Fire();

        Assert.False(File.Exists(Mark));
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(5)), _service.Status);
        Assert.Equal([SyncService.DoneNote], _notes);
        Assert.DoesNotContain(_log, line => line.Contains("paused", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SyncPause.None, _service.Pause);
        Assert.Equal(BlobCodec.Hash(Encoding.UTF8.GetBytes("edited after the restore")), OnTheServer(paths[2]).Sha256);

        // Not paused any more: one missing file of eight is a deleted file again.
        _box.Delete(paths[0]);
        Timer.Fire();

        Assert.True(OnTheServer(paths[0]).Deleted);
        Assert.False(File.Exists(Mark));
    }

    [Fact]
    public void Files_put_back_by_hand_leave_the_rest_held_and_Restore_Missing_Files_brings_the_rest_back()
    {
        Ready();
        var (paths, removed) = AToolRemovedThirtyOfAHundred();

        // The owner finds twelve of them in the recycle bin and puts them back himself.
        foreach (string path in removed.Take(12))
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        // The other eighteen are still files he did not delete: held, whatever the count.
        AssertHeldByTheClock(logged: 18);
        Assert.Equal(100, Seal.Files().Count);

        // The server still holds them, so Restore Missing Files brings them back.
        var server = Server();
        _server.Calls.Clear();
        _service.RestoreMissing();

        Assert.Equal([SyncService.MissingFilesNote(30), SyncService.DoneNote], _notes);
        Assert.Equal(["list", .. Enumerable.Repeat("download", 18)], _server.Calls.Select(call => call.Split(' ')[0]));
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
        Assert.All(paths, path => Assert.False(OnTheServer(path).Deleted));
        Assert.Equal(server, Server());

        // The ordinary run that follows finds nothing to delete, and the pause is over.
        Assert.True(File.Exists(Mark));
        _server.Calls.Clear();
        Timer.Fire();

        Assert.Equal(["list"], _server.Calls);
        Assert.False(File.Exists(Mark));
        Assert.Equal(0, TheSealSyncs().DeletedHere);
        Assert.Equal(Folder(Seal), Folder(_box));
        Assert.Equal(100, Seal.Files().Count);
    }

    [Fact]
    public void A_Confirm_Sync_Deletions_run_that_is_cut_off_leaves_the_rest_held_and_a_second_one_sends_them()
    {
        Ready();
        var paths = Library(40);
        Assert.Equal(40, TheSealSyncs().Downloaded);
        var deleted = paths.Take(12).ToList();
        foreach (string path in deleted)
        {
            _box.Delete(path);
        }

        Timer.Fire();
        Assert.Equal([SyncService.MissingFilesNote(12)], _notes);
        Assert.True(File.Exists(Mark));

        // The owner says he deleted them. The connection goes after nine deletions were sent.
        int uploads = 0;
        _server.Fault = call => call.StartsWith("upload ", StringComparison.Ordinal) && ++uploads > 9
            ? new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10054")
            : null;
        _service.ConfirmDeletions();
        _server.Fault = null;

        Assert.Equal([SyncService.MissingFilesNote(12), SyncService.UnreachableNote], _notes);
        Assert.Equal(9, deleted.Count(path => OnTheServer(path).Deleted));

        // His word was for that run. Three of thirty-one are fewer than begin a pause, but the
        // pause stays: the three that run had not sent are held again.
        Assert.True(File.Exists(Mark));
        var server = Server();
        _log.Clear();
        _server.Calls.Clear();
        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(["list"], _server.Calls);
        Assert.Equal(server, Server());
        Assert.Equal([PausedLine + "3"], _log);
        Assert.Equal(9, TheSealSyncs().DeletedHere);
        Assert.Equal(31, Seal.Files().Count);

        // He confirms again, and that run completes: the three are sent and the pause is over.
        _clock.Advance(TimeSpan.FromMinutes(3));
        _service.ConfirmDeletions();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.Equal(new SyncStatus(SyncCondition.Ok, SyncStop.None, Start.AddMinutes(3)), _service.Status);
        Assert.All(deleted, path => Assert.True(OnTheServer(path).Deleted));
        Assert.False(File.Exists(Mark));
        Assert.Equal(3, TheSealSyncs().DeletedHere);
        Assert.Equal(28, Seal.Files().Count);
        Assert.Equal(Folder(Seal), Folder(_box));
    }

    [Fact]
    public void A_pause_outlasts_Wyvern_a_restore_that_Wyverns_closing_cut_off_leaves_the_rest_held_after_the_restart()
    {
        Ready();
        var (paths, removed) = AToolRemovedThirtyOfAHundred();

        // Wyvern is closed while Restore Missing Files is at work: the run stops between two
        // files, with thirteen of the thirty back.
        int downloads = 0;
        _server.Fault = call =>
        {
            if (call.StartsWith("download ", StringComparison.Ordinal) && ++downloads == 13)
            {
                _service.Dispose();
            }

            return null;
        };
        _service.RestoreMissing();
        _server.Fault = null;

        Assert.Equal(17, removed.Count(path => !_box.Exists(path)));
        Assert.Equal([SyncService.MissingFilesNote(30)], _notes);
        Assert.True(File.Exists(Mark));

        // Wyvern starts again: a new service on the same folders, which remembers nothing but
        // what is on the disk. Seventeen of a hundred are fewer than begin a pause, and its
        // first run is held all the same, and says so.
        using var again = NewService(Inline);
        var timer = _time.Timers[^1];
        Configure(again);

        AssertHeldByTheClock(logged: 17, timer, again);
        Assert.Equal(Paused(null), again.Status);
        Assert.Equal([SyncService.MissingFilesNote(30), SyncService.MissingFilesNote(17)], _notes);
        Assert.Equal(100, Seal.Files().Count);

        // And the owner's answer finishes the job.
        again.RestoreMissing();
        timer.Fire();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.All(paths, path => Assert.Equal("the text of " + path, _box.Read(path)));
        Assert.False(File.Exists(Mark));
        Assert.Equal(SyncCondition.Ok, again.Status.Condition);
        Assert.Equal(0, TheSealSyncs().DeletedHere);
        Assert.Equal(Folder(Seal), Folder(_box));
    }

    [Theory]
    [InlineData("a file of other words")]
    [InlineData("an empty file")]
    [InlineData("a file that is held and cannot be read")]
    [InlineData("a folder")]
    public void Whatever_is_there_under_the_marks_name_is_the_mark_whether_or_not_it_can_be_read(string what)
    {
        Ready();
        var paths = Library(40);
        FileStream? held = null;
        switch (what)
        {
            case "a file of other words": File.WriteAllText(Mark, "otter"); break;
            case "an empty file": File.WriteAllBytes(Mark, []); break;
            case "a folder": Directory.CreateDirectory(Mark); break;
            default: held = new FileStream(Mark, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); break;
        }

        using (held)
        {
            // No run of this service was held, and one missing file of forty would be a deleted
            // file. The mark says that sync is paused: the run is held.
            _box.Delete(paths[0]);
            var server = Server();
            _server.Calls.Clear();

            Timer.Fire();

            Assert.Equal(Paused(Start), _service.Status);
            Assert.Equal([SyncService.MissingFilesNote(1)], _notes);
            Assert.Equal(["list"], _server.Calls);
            Assert.Equal(server, Server());
        }
    }

    [Fact]
    public void A_mark_that_cannot_be_removed_keeps_sync_paused_and_is_logged_once()
    {
        Ready();
        var paths = Library(40);
        Directory.CreateDirectory(Mark);
        _log.Clear();

        // Nothing is missing, so each run would end the pause, and cannot: the mark is a folder.
        Timer.Fire();
        Timer.Fire();

        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Equal([MarkNotRemovedLine], _log.Where(line => !line.StartsWith("Sync finished in ", StringComparison.Ordinal)));
        Assert.True(Directory.Exists(Mark));

        // So sync is still paused, and one missing file is held.
        _box.Delete(paths[0]);
        var server = Server();
        Timer.Fire();

        Assert.Equal(Paused(Start), _service.Status);
        Assert.Equal(server, Server());
    }

    // A service with a Wyvern root of its own, forty library files of which twelve went
    // missing, and the run that was held for them: the root was taken away while that run was
    // at work, which is the one way to have the mark's file refuse to be made without the mark
    // being there. The root is back when this returns. The caller lets go of the service.
    private (SyncService Service, ManualTimer Timer, List<string> Files, WyvernPaths Paths) AHeldRunWhoseMarkCouldNotBeWritten()
    {
        var paths = NestedRoot();
        var service = NewService(Inline, paths);
        var timer = _time.Timers[^1];
        SetUp(paths: paths);
        Configure(service);
        var files = Enumerable.Range(1, 40).Select(number => FormattableString.Invariant($"library/snippets/note-{number:000}.md")).ToList();
        foreach (string path in files)
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        timer.Fire();
        Assert.Equal(SyncCondition.Ok, service.Status.Condition);
        foreach (string path in files.Take(12))
        {
            _box.Delete(path);
        }

        string away = paths.Root + "-away";
        bool moved = false;
        _server.Fault = call =>
        {
            if (call == "list" && !moved)
            {
                moved = true;
                Directory.Move(paths.Root, away);
            }

            return null;
        };
        var server = Server();
        _log.Clear();

        timer.Fire();
        _server.Fault = null;
        Directory.Move(away, paths.Root);

        // Held as it would have been, shown and said as a pause, and the failure is in the log once.
        Assert.Equal(Paused(Start), service.Status);
        Assert.Equal([SyncService.MissingFilesNote(12)], _notes);
        Assert.Equal(server, Server());
        Assert.Equal([MarkNotWrittenLine, PausedLine + "12"], _log.Order(StringComparer.Ordinal));
        Assert.False(File.Exists(paths.SyncPausedFile));
        return (service, timer, files, paths);
    }

    [Fact]
    public void When_the_mark_cannot_be_written_the_run_is_still_held_and_shown_as_paused_and_the_next_run_counts_afresh()
    {
        var (service, timer, files, _) = AHeldRunWhoseMarkCouldNotBeWritten();
        using (service)
        {
            // Nothing on the disk says that sync is paused, so the next run counts afresh, as
            // every run did before a pause was remembered: with eleven of the files back, the
            // one that is still missing is a deleted file.
            foreach (string path in files.Take(11))
            {
                _box.Write(path, "the text of " + path, Start.AddHours(-2));
            }

            timer.Fire();

            Assert.Equal(SyncCondition.Ok, service.Status.Condition);
            Assert.True(OnTheServer(files[11]).Deleted);
            Assert.Equal(SyncPause.None, service.Pause);
        }
    }

    [Fact]
    public void When_the_mark_could_not_be_written_Confirm_Sync_Deletions_still_answers_the_pause_that_was_shown()
    {
        var (service, _, files, paths) = AHeldRunWhoseMarkCouldNotBeWritten();
        using (service)
        {
            // The owner was told that sync is paused, and to confirm. Sync is paused as far as
            // this service knows, with or without the mark, and his word is taken.
            Assert.Equal(new SyncPause(true, 12), service.Pause);

            service.ConfirmDeletions();

            Assert.Equal([SyncService.MissingFilesNote(12), SyncService.DoneNote], _notes);
            Assert.Equal(12, files.Count(path => OnTheServer(path).Deleted));
            Assert.Equal(SyncPause.None, service.Pause);
            Assert.False(File.Exists(paths.SyncPausedFile));
        }
    }

    [Fact]
    public void A_run_that_could_not_look_at_the_library_does_not_end_the_pause()
    {
        Ready();
        var (_, removed) = AToolRemovedThirtyOfAHundred();
        foreach (string path in removed.Take(12))
        {
            _box.Write(path, "the text of " + path, Start.AddHours(-2));
        }

        // For one run the folder that holds the library cannot be listed. The run's plan holds
        // no deletion, because the run cannot see that anything is missing. That is not "the
        // files are back", and the pause stays.
        string locked = _box.Full("library/snippets");
        _box.DenyListing(locked);
        try
        {
            Timer.Fire();
        }
        finally
        {
            _box.Allow(locked);
        }

        Assert.Equal(SyncCondition.Incomplete, _service.Status.Condition);
        Assert.Equal(SyncStop.None, _service.Status.Stop);
        Assert.True(File.Exists(Mark));

        // The next run sees the eighteen that are missing, and is held.
        AssertHeldByTheClock(logged: 18);
        Assert.Equal(100, Seal.Files().Count);
    }

    [Fact]
    public void A_Restore_Missing_Files_run_begins_a_pause_that_the_next_ordinary_run_ends_and_a_completed_Confirm_Sync_Deletions_run_ends_one()
    {
        Ready();
        var paths = Library(8);

        // With nothing held, Restore Missing Files brings back a file and writes the mark. It
        // does not end the pause it has begun: the ordinary run that follows does.
        _box.Delete(paths[0]);
        _service.RestoreMissing();

        Assert.Equal([SyncService.DoneNote], _notes);
        Assert.True(_box.Exists(paths[0]));
        Assert.True(File.Exists(Mark));

        Timer.Fire();

        Assert.False(File.Exists(Mark));
        Assert.Equal([SyncService.DoneNote], _notes);

        // A pause, answered with Confirm Sync Deletions: the run completes and the mark is gone.
        DeleteTheLibrary();
        Timer.Fire();
        Assert.True(File.Exists(Mark));

        _service.ConfirmDeletions();

        Assert.Equal(SyncService.DoneNote, _notes[^1]);
        Assert.False(File.Exists(Mark));
        Assert.All(paths, path => Assert.True(OnTheServer(path).Deleted));
    }

    // ---- The key file's path, when its text holds a tilde ----

    [Theory]
    [InlineData(@"OTTER~1\otter_ed25519")]
    [InlineData(@"keys\OTTER_~1")]
    [InlineData(@"~otter_ed25519")]
    public void A_key_path_with_a_tilde_in_its_text_is_not_made_full_to_see_whether_it_lies_in_the_data_folder(string keyFile)
    {
        // Making a path full goes by its text, except for a text that holds a "~": Windows is
        // then asked for the long form of what may be a short name, and reads the disk along
        // the path to find it. Where the path passes through a folder that is a link to a
        // share, that contacts the share, before the connection has refused the key file. So
        // the question whether the key file lies in the data folder is not asked of such a
        // path at all. The run goes on, and the connection refuses or accepts the key file by
        // its own rules, which look at every folder on the way without following a link.
        SetUp(keyPath: Path.Combine(_box.Data, keyFile));
        Configure();

        Timer.Fire();

        Assert.NotEqual(SyncStop.KeyFileInDataFolder, _service.Status.Stop);
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(Path.Combine(_box.Data, keyFile), _connector.LastConfig!.PrivateKeyPath);
        Assert.Equal(SyncCondition.Ok, _service.Status.Condition);
        Assert.Empty(_notes);
    }

    private string ScopeNow()
    {
        using var keys = SyncKeys.FromMaster(Master);
        Assert.True(SyncFolderId.TryGet(_box.Data, out string id));
        return SyncState.ScopeOf(SyncConfig.Load(_paths.SyncFile).Config!.RemoteId(keys.Check()), _box.Data, id);
    }
}
