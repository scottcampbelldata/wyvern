using System.Text;
using Beacon.Core.Platform;
using Beacon.Core.Settings;
using Beacon.Core.Sync;

namespace Beacon.Tests;

/// <summary>
/// The Sync Setup window's logic, without the window. The server lives in memory and the
/// connector only counts, so no test connects to anything; what would run in the background
/// runs at once, unless a test holds it back to look at the moment in between.
/// </summary>
public sealed class SyncSetupViewModelTests : IDisposable
{
    private const string Host = "secret-otter.example.org";
    private const string User = "secret-walrus";
    private const string KeyPath = @"C:\SecretKeys\otter_ed25519";
    private const string RemoteDir = "/home/secret-walrus/beacon";

    private static readonly byte[] Master = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly BeaconPaths _paths;
    private readonly MemoryRemoteStore _server = new();
    private readonly MemoryConnector _connector;
    private readonly FakeKeyProtector _protector = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly List<string> _reports = [];
    private readonly List<Action> _heldBack = [];
    private readonly List<Action> _queued = [];
    private bool _holdBack;
    private bool _queueBackground;
    private int _saved;

    public SyncSetupViewModelTests()
    {
        _paths = new BeaconPaths(_dir.Path);
        _connector = new MemoryConnector(_server);
    }

    public void Dispose() => _dir.Dispose();

    private KeyStore Keys => new(_paths.KeyFile, _paths.PassphraseFile, _protector);

    // The background work runs at once, unless a test asks for it to be queued; what comes of
    // it is handed back at once, unless a test holds it back.
    private SyncSetupViewModel NewViewModel(BeaconPaths? paths = null, IRemoteConnector? connector = null, IKeyProtector? protector = null)
    {
        var viewModel = new SyncSetupViewModel(
            paths ?? _paths,
            connector ?? _connector,
            protector ?? _protector,
            _clipboard,
            work =>
            {
                if (_queueBackground)
                {
                    _queued.Add(work);
                }
                else
                {
                    work();
                }
            },
            done =>
            {
                if (_holdBack)
                {
                    _heldBack.Add(done);
                }
                else
                {
                    done();
                }
            },
            () => _saved++,
            (text, _) => _reports.Add(text));
        viewModel.Load();
        return viewModel;
    }

    // Every change of a property that is announced from now on.
    private static List<string> Watch(SyncSetupViewModel viewModel)
    {
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        return changed;
    }

    private static object? Read(SyncSetupViewModel viewModel, string property) =>
        typeof(SyncSetupViewModel).GetProperty(property)!.GetValue(viewModel);

    private static void Write(SyncSetupViewModel viewModel, string property, object value) =>
        typeof(SyncSetupViewModel).GetProperty(property)!.SetValue(viewModel, value);

    private static void Fill(SyncSetupViewModel viewModel)
    {
        viewModel.Host = Host;
        viewModel.Port = "2222";
        viewModel.User = User;
        viewModel.KeyFile = KeyPath;
        viewModel.RemoteDir = RemoteDir;
    }

    // A view model that has been filled in and tested, with the fingerprint confirmed.
    private SyncSetupViewModel Confirmed()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        Assert.True(viewModel.CanSave, viewModel.Message);
        return viewModel;
    }

    private SyncConfig SavedConfig()
    {
        var loaded = SyncConfig.Load(_paths.SyncFile);
        Assert.Equal(SyncConfigState.Ready, loaded.State);
        return loaded.Config!;
    }

    private byte[] SavedKey()
    {
        Assert.Equal(SecretState.Read, Keys.ReadMaster(out byte[] master, out _));
        return master;
    }

    // What the other machine left on the server: one blob made with this key.
    private void ServerHoldsABlobMadeWith(byte[] master)
    {
        using var keys = SyncKeys.FromMaster(master);
        string name = keys.BlobName("library/quicklinks.json");
        byte[] content = Encoding.UTF8.GetBytes("[]");
        _server.Put(name, BlobCodec.Seal(keys, name, new BlobHeader("library/quicklinks.json", Noon, false, BlobCodec.Hash(content)), content));
    }

    // A file in the folder that has a blob's name and is not a blob. Small, so that it comes first by size.
    private void ServerHoldsAJunkFile(int number) =>
        _server.Put(JunkName(number), Encoding.ASCII.GetBytes("not-a-blob"));

    private static string JunkName(int number) => number.ToString("x").PadLeft(BlobCodec.NameLength, '0');

    private static byte[] AnotherKey() => [.. Enumerable.Repeat((byte)9, 32)];

    // A machine that is set up: settings that are pinned, and a sync key.
    private SyncConfig SetUpMachine()
    {
        Assert.True(SyncConfig.TryCreate(Host, "2222", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var config, out _));
        config!.Save(_paths.SyncFile);
        Keys.WriteMaster(Master);
        return config;
    }

    private void AssertNothingWasWritten()
    {
        Assert.False(File.Exists(_paths.SyncFile));
        Assert.False(File.Exists(_paths.KeyFile));
        Assert.False(File.Exists(_paths.PassphraseFile));
        Assert.Equal(0, _saved);
    }

    // ---- Opening the window ----

    [Fact]
    public void On_a_machine_that_was_never_set_up_the_fields_are_empty_and_a_new_key_is_offered()
    {
        var viewModel = NewViewModel();

        Assert.Equal(("", "22", "", "", ""), (viewModel.Host, viewModel.Port, viewModel.User, viewModel.KeyFile, viewModel.RemoteDir));
        Assert.False(viewModel.CanKeep);
        Assert.Equal(SyncKeyChoice.Generate, viewModel.KeyChoice);
        Assert.True(viewModel.GenerateKey);
        Assert.False(viewModel.NeedsPassphrase);
        Assert.True(viewModel.CanTest);
        Assert.False(viewModel.CanSave);
        Assert.Equal(0, _connector.Opens + _connector.HostKeyReads + _connector.KeyFileChecks);
    }

    [Fact]
    public void On_a_machine_that_is_set_up_the_fields_are_filled_in_the_key_is_kept_and_the_passphrase_is_not_shown()
    {
        Assert.True(SyncConfig.TryCreate(Host, "2222", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var config, out _));
        config!.Save(_paths.SyncFile);
        Keys.WriteMaster(Master);
        Keys.WritePassphrase("walrus tusk");

        var viewModel = NewViewModel();

        Assert.Equal((Host, "2222", User, KeyPath, RemoteDir), (viewModel.Host, viewModel.Port, viewModel.User, viewModel.KeyFile, viewModel.RemoteDir));
        Assert.True(viewModel.CanKeep);
        Assert.True(viewModel.KeepKey);
        Assert.True(viewModel.NeedsPassphrase);
        Assert.Equal("", viewModel.Passphrase);
        Assert.Equal("", viewModel.Fingerprint);
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public void Keeping_a_key_cannot_be_chosen_on_a_machine_that_has_none()
    {
        var viewModel = NewViewModel();

        viewModel.KeepKey = true;
        Assert.Equal(SyncKeyChoice.Generate, viewModel.KeyChoice);

        viewModel.EnterKey = true;
        Assert.Equal(SyncKeyChoice.Enter, viewModel.KeyChoice);
        Assert.False(viewModel.GenerateKey);
    }

    // ---- Test Connection ----

    [Fact]
    public void A_field_that_is_wrong_is_said_and_nothing_is_read_or_connected_to()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        viewModel.Port = "seventy";

        viewModel.TestConnection();

        Assert.Equal(SyncConfig.PortProblem, viewModel.Message);
        Assert.Equal(0, _connector.KeyFileChecks + _connector.HostKeyReads + _connector.Opens);
        Assert.False(viewModel.FingerprintShown);
    }

    [Fact]
    public void A_key_file_with_a_passphrase_asks_for_it_before_the_server_is_contacted()
    {
        _connector.Passphrase = "walrus tusk";
        var viewModel = NewViewModel();
        Fill(viewModel);

        viewModel.TestConnection();

        Assert.True(viewModel.NeedsPassphrase);
        Assert.Equal(SyncSetupViewModel.PassphraseNeededMessage, viewModel.Message);
        Assert.Equal(0, _connector.HostKeyReads);

        viewModel.Passphrase = "seal flipper";
        viewModel.TestConnection();
        Assert.Equal(SyncSetupViewModel.PassphraseWrongMessage, viewModel.Message);
        Assert.Equal(0, _connector.HostKeyReads);

        viewModel.Passphrase = "walrus tusk";
        viewModel.TestConnection();
        Assert.True(viewModel.FingerprintShown);
    }

    [Fact]
    public void A_key_file_that_cannot_be_read_is_said_and_the_server_is_not_contacted()
    {
        _connector.KeyFile = KeyFileState.Unreadable;
        var viewModel = NewViewModel();
        Fill(viewModel);

        viewModel.TestConnection();

        Assert.Equal(SyncSetupViewModel.KeyFileUnreadableMessage, viewModel.Message);
        Assert.Equal(0, _connector.HostKeyReads);

        // A key file on a network drive, behind a link or not really on the disk (an online-only
        // file) is not opened at all, and to the owner it is a file that opens everywhere else:
        // the sentence says where it must be.
        Assert.Equal(
            "The key file could not be read, or it is not a private key. It must be on a drive of this machine, kept on this disk (not an online-only file), and not reached through a link.",
            SyncSetupViewModel.KeyFileUnreadableMessage);
    }

    [Fact]
    public void A_server_that_answers_has_its_fingerprint_shown_and_nothing_is_signed_in_to()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);

        viewModel.TestConnection();

        Assert.Equal(MemoryConnector.ServerKey, viewModel.Fingerprint);
        Assert.Equal("ssh-ed25519", viewModel.FingerprintKind);
        Assert.True(viewModel.FingerprintShown);
        Assert.Equal(SyncSetupViewModel.CompareMessage, viewModel.Message);
        Assert.Equal(1, _connector.HostKeyReads);
        Assert.Equal(0, _connector.Opens);
        Assert.False(viewModel.FingerprintConfirmed);
        Assert.False(viewModel.CanSave);
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable, SyncSetupViewModel.UnreachableMessage)]
    [InlineData(RemoteFailure.Server, SyncSetupViewModel.NotSshMessage)]
    public void A_server_that_does_not_answer_is_said_in_fixed_words(RemoteFailure failure, string message)
    {
        _connector.Refusal = new RemoteStoreException(failure, "SocketException 10061");
        var viewModel = NewViewModel();
        Fill(viewModel);

        viewModel.TestConnection();

        Assert.Equal(message, viewModel.Message);
        Assert.False(viewModel.FingerprintShown);
    }

    [Fact]
    public void A_server_that_shows_another_key_than_the_one_confirmed_before_is_said_loudly()
    {
        Assert.True(SyncConfig.TryCreate(Host, "2222", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var config, out _));
        config!.Save(_paths.SyncFile);
        _connector.Fingerprint = MemoryConnector.OtherKey;
        var viewModel = NewViewModel();

        viewModel.TestConnection();

        Assert.Equal(MemoryConnector.OtherKey, viewModel.Fingerprint);
        Assert.Equal(SyncSetupViewModel.OtherKeyMessage, viewModel.Message);
        Assert.False(viewModel.FingerprintConfirmed);
    }

    [Theory]
    [InlineData("another-server.example.org", "2222")]
    [InlineData(Host, "22")]
    public void Moving_to_another_server_shows_the_plain_compare_message_and_not_the_alarm(string host, string port)
    {
        Assert.True(SyncConfig.TryCreate(Host, "2222", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var config, out _));
        config!.Save(_paths.SyncFile);
        _connector.Fingerprint = MemoryConnector.OtherKey;
        var viewModel = NewViewModel();
        viewModel.Host = host;
        viewModel.Port = port;

        viewModel.TestConnection();

        Assert.Equal(MemoryConnector.OtherKey, viewModel.Fingerprint);
        Assert.Equal(SyncSetupViewModel.CompareMessage, viewModel.Message);
    }

    [Fact]
    public void The_same_server_written_in_other_letters_still_gets_the_alarm_for_another_key()
    {
        Assert.True(SyncConfig.TryCreate(Host, "2222", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var config, out _));
        config!.Save(_paths.SyncFile);
        _connector.Fingerprint = MemoryConnector.OtherKey;
        var viewModel = NewViewModel();
        viewModel.Host = Host.ToUpperInvariant();

        viewModel.TestConnection();

        Assert.Equal(SyncSetupViewModel.OtherKeyMessage, viewModel.Message);
    }

    [Fact]
    public void The_fingerprint_cannot_be_confirmed_before_it_is_shown()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);

        viewModel.FingerprintConfirmed = true;

        Assert.False(viewModel.FingerprintConfirmed);
        Assert.False(viewModel.CanSave);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Port")]
    [InlineData("User")]
    [InlineData("KeyFile")]
    [InlineData("RemoteDir")]
    [InlineData("Passphrase")]
    public void Changing_a_field_after_the_test_takes_the_fingerprint_and_the_confirmation_away(string field)
    {
        var viewModel = Confirmed();

        typeof(SyncSetupViewModel).GetProperty(field)!.SetValue(viewModel, "changed");

        Assert.False(viewModel.FingerprintShown);
        Assert.False(viewModel.FingerprintConfirmed);
        Assert.False(viewModel.CanSave);
        Assert.Equal("", viewModel.Message);
    }

    [Fact]
    public void While_a_check_is_under_way_neither_button_can_be_pressed()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        _holdBack = true;

        viewModel.TestConnection();

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanTest);
        Assert.False(viewModel.CanSave);
        viewModel.TestConnection();
        Assert.Equal(1, _connector.HostKeyReads);
        viewModel.Save();
        Assert.Equal(0, _connector.Opens);

        _heldBack.Single()();
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.FingerprintShown);
    }

    [Fact]
    public void While_a_save_is_under_way_neither_button_can_be_pressed_again()
    {
        var viewModel = Confirmed();
        _holdBack = true;

        viewModel.Save();

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanTest);
        Assert.False(viewModel.CanSave);
        viewModel.Save();
        viewModel.TestConnection();
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(1, _connector.HostKeyReads);

        _heldBack.Single()();
        Assert.True(viewModel.IsSaved);
        Assert.Equal(1, _saved);
    }

    [Fact]
    public void Save_and_Test_Connection_when_they_are_not_allowed_do_nothing_and_do_not_throw()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);

        // Save before any test.
        viewModel.Save();
        AssertNothingWasWritten();
        Assert.Equal(0, _connector.Opens);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("", viewModel.Message);

        // Save with the fingerprint shown and not confirmed.
        viewModel.TestConnection();
        viewModel.Save();
        AssertNothingWasWritten();
        Assert.Equal(0, _connector.Opens);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(SyncSetupViewModel.CompareMessage, viewModel.Message);

        // Both, once everything was saved.
        viewModel.FingerprintConfirmed = true;
        viewModel.Save();
        Assert.True(viewModel.IsSaved);
        string message = viewModel.Message;
        viewModel.Save();
        viewModel.TestConnection();
        Assert.Equal(1, _connector.Opens);
        Assert.Equal(1, _connector.HostKeyReads);
        Assert.Equal(1, _saved);
        Assert.Equal(message, viewModel.Message);
        Assert.True(viewModel.IsSaved);
        Assert.False(viewModel.IsBusy);
    }

    // ---- Input while a test or a save is under way, and after a save ----

    [Theory]
    [InlineData("Host")]
    [InlineData("Port")]
    [InlineData("User")]
    [InlineData("KeyFile")]
    [InlineData("RemoteDir")]
    [InlineData("Passphrase")]
    [InlineData("EnteredKey")]
    public void A_field_set_while_a_test_is_under_way_keeps_its_value_and_the_result_is_for_the_fields_as_they_are(string field)
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        object? kept = Read(viewModel, field);
        _holdBack = true;
        viewModel.TestConnection();
        var changed = Watch(viewModel);

        Write(viewModel, field, "changed");

        Assert.Equal(kept, Read(viewModel, field));
        Assert.Contains(field, changed);

        _heldBack.Single()();
        _holdBack = false;
        Assert.Equal(MemoryConnector.ServerKey, viewModel.Fingerprint);
        Assert.Equal(SyncSetupViewModel.CompareMessage, viewModel.Message);
        viewModel.FingerprintConfirmed = true;
        viewModel.Save();

        Assert.True(viewModel.IsSaved, viewModel.Message);
        Assert.Equal(new SyncConfig(Host, 2222, User, KeyPath, RemoteDir, MemoryConnector.ServerKey), SavedConfig());
        Assert.Equal((Host, "2222", User, KeyPath, RemoteDir), (viewModel.Host, viewModel.Port, viewModel.User, viewModel.KeyFile, viewModel.RemoteDir));
        Assert.Null(_connector.LastPassphrase);
    }

    [Fact]
    public void A_key_choice_made_while_a_test_is_under_way_is_not_taken()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        _holdBack = true;
        viewModel.TestConnection();
        var changed = Watch(viewModel);

        viewModel.EnterKey = true;

        Assert.Equal(SyncKeyChoice.Generate, viewModel.KeyChoice);
        Assert.True(viewModel.GenerateKey);
        Assert.False(viewModel.EnterKey);
        Assert.Contains(nameof(SyncSetupViewModel.KeyChoice), changed);
        Assert.Contains(nameof(SyncSetupViewModel.EnterKey), changed);
        Assert.Contains(nameof(SyncSetupViewModel.GenerateKey), changed);
    }

    [Fact]
    public void A_field_set_while_a_save_is_under_way_is_not_taken_and_what_is_saved_is_what_the_fields_show()
    {
        var viewModel = Confirmed();
        _holdBack = true;
        viewModel.Save();
        var changed = Watch(viewModel);

        viewModel.Host = "changed.example.org";
        viewModel.User = "changed";
        viewModel.KeyChoice = SyncKeyChoice.Enter;
        viewModel.FingerprintConfirmed = false;

        Assert.Equal((Host, User, SyncKeyChoice.Generate, true), (viewModel.Host, viewModel.User, viewModel.KeyChoice, viewModel.FingerprintConfirmed));
        Assert.Contains(nameof(SyncSetupViewModel.Host), changed);
        Assert.Contains(nameof(SyncSetupViewModel.User), changed);
        Assert.Contains(nameof(SyncSetupViewModel.KeyChoice), changed);
        Assert.Contains(nameof(SyncSetupViewModel.FingerprintConfirmed), changed);

        _heldBack.Single()();
        Assert.True(viewModel.IsSaved);
        Assert.Equal(SyncSetupViewModel.SavedNewKeyMessage, viewModel.Message);
        Assert.True(viewModel.FingerprintShown);
        Assert.Equal((Host, User), (viewModel.Host, viewModel.User));
        Assert.Equal(new SyncConfig(Host, 2222, User, KeyPath, RemoteDir, MemoryConnector.ServerKey), SavedConfig());
    }

    [Fact]
    public void After_a_save_has_been_shown_an_edit_changes_nothing_and_the_saved_message_and_the_key_stay()
    {
        var viewModel = Confirmed();
        viewModel.Save();
        string message = viewModel.Message;
        string key = viewModel.GeneratedKey;
        Assert.True(viewModel.IsSaved);
        Assert.Equal(SyncSetupViewModel.SavedNewKeyMessage, message);
        var changed = Watch(viewModel);

        viewModel.Host = "changed.example.org";
        viewModel.Port = "1";
        viewModel.User = "changed";
        viewModel.KeyFile = @"C:\Changed\id_ed25519";
        viewModel.RemoteDir = "/changed";
        viewModel.Passphrase = "changed";
        viewModel.EnterKey = true;
        viewModel.EnteredKey = "changed";
        viewModel.FingerprintConfirmed = false;

        Assert.Equal(message, viewModel.Message);
        Assert.Equal(key, viewModel.GeneratedKey);
        Assert.True(viewModel.HasGeneratedKey);
        Assert.True(viewModel.IsSaved);
        Assert.True(viewModel.FingerprintShown);
        Assert.True(viewModel.FingerprintConfirmed);
        Assert.Equal(SyncKeyChoice.Generate, viewModel.KeyChoice);
        Assert.Equal((Host, "2222", User, KeyPath, RemoteDir, "", ""), (viewModel.Host, viewModel.Port, viewModel.User, viewModel.KeyFile, viewModel.RemoteDir, viewModel.Passphrase, viewModel.EnteredKey));
        Assert.Contains(nameof(SyncSetupViewModel.Host), changed);
        Assert.Contains(nameof(SyncSetupViewModel.EnteredKey), changed);
    }

    [Fact]
    public void Editing_is_off_while_a_check_is_under_way_and_once_a_save_has_been_shown_and_the_change_is_announced()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        Assert.True(viewModel.CanEdit);
        var changed = Watch(viewModel);
        _holdBack = true;

        viewModel.TestConnection();
        Assert.False(viewModel.CanEdit);
        Assert.Contains(nameof(SyncSetupViewModel.CanEdit), changed);

        changed.Clear();
        _heldBack.Single()();
        _holdBack = false;
        Assert.True(viewModel.CanEdit);
        Assert.Contains(nameof(SyncSetupViewModel.CanEdit), changed);

        viewModel.FingerprintConfirmed = true;
        changed.Clear();
        viewModel.Save();
        Assert.True(viewModel.IsSaved);
        Assert.False(viewModel.CanEdit);
        Assert.Contains(nameof(SyncSetupViewModel.CanEdit), changed);
    }

    // ---- The work is handed to the background ----

    [Fact]
    public void A_test_connection_does_nothing_on_the_calling_thread_until_the_queued_work_is_run()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        _queueBackground = true;

        viewModel.TestConnection();

        Assert.True(viewModel.IsBusy);
        Assert.Single(_queued);
        Assert.Equal(0, _connector.KeyFileChecks + _connector.HostKeyReads + _connector.Opens);
        Assert.False(viewModel.FingerprintShown);

        _queued.Single()();
        Assert.Equal(1, _connector.KeyFileChecks);
        Assert.Equal(1, _connector.HostKeyReads);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.FingerprintShown);
    }

    [Fact]
    public void A_save_reaches_the_connector_the_key_store_and_the_disk_only_when_the_queued_work_is_run()
    {
        Keys.WriteMaster(Master);
        _connector.Passphrase = "walrus tusk";
        var viewModel = NewViewModel();
        Fill(viewModel);
        viewModel.Passphrase = "walrus tusk";
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        Assert.True(viewModel.CanSave, viewModel.Message);
        Assert.True(viewModel.KeepKey);
        _queueBackground = true;

        viewModel.Save();

        Assert.True(viewModel.IsBusy);
        Assert.Single(_queued);
        Assert.Equal(0, _connector.Opens);
        Assert.Empty(_server.Calls);
        Assert.False(File.Exists(_paths.SyncFile));
        Assert.False(File.Exists(_paths.PassphraseFile));
        Assert.Equal(0, _saved);

        _queued.Single()();
        Assert.Equal(1, _connector.Opens);
        Assert.True(File.Exists(_paths.PassphraseFile));
        Assert.Equal(MemoryConnector.ServerKey, SavedConfig().HostKeyFingerprint);
        Assert.True(viewModel.IsSaved);
        Assert.Equal(1, _saved);
    }

    // ---- Save ----

    [Fact]
    public void Saving_a_new_key_for_an_empty_folder_writes_the_key_and_the_settings_and_shows_the_key_once()
    {
        var viewModel = Confirmed();

        viewModel.Save();

        Assert.True(viewModel.IsSaved);
        Assert.Equal(SyncSetupViewModel.SavedNewKeyMessage, viewModel.Message);
        Assert.Equal(1, _saved);
        Assert.Equal(new SyncConfig(Host, 2222, User, KeyPath, RemoteDir, MemoryConnector.ServerKey), SavedConfig());
        Assert.Equal(SyncKeys.Format(SavedKey()), viewModel.GeneratedKey);
        Assert.True(viewModel.HasGeneratedKey);
        Assert.Equal(44, viewModel.GeneratedKey.Length);
        Assert.False(File.Exists(_paths.PassphraseFile));
        Assert.False(viewModel.CanSave);
        Assert.False(viewModel.CanTest);
        Assert.Equal(0, _connector.OpenNow);
    }

    [Fact]
    public void Two_new_keys_are_never_the_same()
    {
        var first = Confirmed();
        first.Save();
        File.Delete(_paths.KeyFile);
        var second = Confirmed();
        second.GenerateKey = true;
        second.Save();

        Assert.NotEqual(first.GeneratedKey, second.GeneratedKey);
    }

    [Fact]
    public void A_new_key_is_refused_for_a_folder_that_already_holds_blobs_and_nothing_is_written()
    {
        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.NotEmptyMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        AssertNothingWasWritten();
    }

    [Fact]
    public void The_key_from_the_other_machine_is_saved_when_it_opens_what_is_on_the_server()
    {
        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = "  " + SyncKeys.Format(Master) + "  ";

        viewModel.Save();

        Assert.True(viewModel.IsSaved);
        Assert.Equal(SyncSetupViewModel.SavedMessage, viewModel.Message);
        Assert.Equal(Master, SavedKey());
        Assert.Equal("", viewModel.GeneratedKey);
        Assert.Equal(1, _saved);
    }

    [Fact]
    public void A_key_that_does_not_open_what_is_on_the_server_is_refused_and_nothing_is_written()
    {
        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format([.. Enumerable.Repeat((byte)9, 32)]);

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.WrongKeyMessage, viewModel.Message);
        AssertNothingWasWritten();
    }

    [Fact]
    public void A_typed_key_is_taken_on_trust_when_the_folder_is_empty_and_is_said_to_be_untried()
    {
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.True(viewModel.IsSaved);
        Assert.Equal(Master, SavedKey());
        Assert.Equal(1, _saved);

        // There was nothing to try the key on. A slip in the folder's name on the second machine
        // makes a new, empty folder, and the two machines would never meet: the owner is told.
        Assert.Equal(SyncSetupViewModel.SavedUntriedMessage, viewModel.Message);
        Assert.Equal(
            "Saved. Sync starts now. The server's folder was empty, so the key could not be checked against it. If your other machine already syncs, check the folder name.",
            SyncSetupViewModel.SavedUntriedMessage);
        Assert.Equal("", viewModel.GeneratedKey);
    }

    [Fact]
    public void A_kept_key_is_said_to_be_untried_when_the_folder_is_empty_and_not_when_it_opened_what_is_there()
    {
        SetUpMachine();
        var untried = NewViewModel();
        untried.TestConnection();
        untried.FingerprintConfirmed = true;
        Assert.True(untried.KeepKey);

        untried.Save();

        Assert.True(untried.IsSaved, untried.Message);
        Assert.Equal(SyncSetupViewModel.SavedUntriedMessage, untried.Message);
        Assert.Equal(Master, SavedKey());

        // With a blob in the folder that this key opens, the key was tried, and nothing is added.
        ServerHoldsABlobMadeWith(Master);
        var tried = NewViewModel();
        tried.TestConnection();
        tried.FingerprintConfirmed = true;
        Assert.True(tried.KeepKey);

        tried.Save();

        Assert.True(tried.IsSaved, tried.Message);
        Assert.Equal(SyncSetupViewModel.SavedMessage, tried.Message);
        Assert.Equal("Saved. Sync starts now.", SyncSetupViewModel.SavedMessage);
    }

    [Fact]
    public void A_new_key_for_an_empty_folder_keeps_its_own_sentence_and_says_nothing_of_a_key_that_was_not_tried()
    {
        var viewModel = Confirmed();
        Assert.True(viewModel.GenerateKey);

        viewModel.Save();

        // An empty folder is what a new key is for.
        Assert.True(viewModel.IsSaved);
        Assert.Equal(SyncSetupViewModel.SavedNewKeyMessage, viewModel.Message);
        Assert.Equal(
            "Saved. Sync starts now. This is your sync key: enter it in Sync Setup on your other machine. Show Sync Key shows it again.",
            SyncSetupViewModel.SavedNewKeyMessage);
        Assert.DoesNotContain("could not be checked", viewModel.Message, StringComparison.Ordinal);
    }

    // ---- A typed or kept key is tried on a blob that can decide ----

    [Fact]
    public void A_junk_file_that_is_the_smallest_does_not_decide_and_the_right_key_is_accepted_on_the_blob_after_it()
    {
        ServerHoldsAJunkFile(1);
        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.True(viewModel.IsSaved, viewModel.Message);
        Assert.Equal(Master, SavedKey());
        Assert.Equal(2, _server.Count("download"));
    }

    [Fact]
    public void A_junk_file_that_is_the_smallest_does_not_decide_and_a_wrong_key_is_refused_as_the_wrong_key()
    {
        ServerHoldsAJunkFile(1);
        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(AnotherKey());

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.WrongKeyMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        AssertNothingWasWritten();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_file_over_the_size_limit_is_skipped_without_being_downloaded_and_the_blob_after_it_decides(bool rightKey)
    {
        string oversize = JunkName(2);
        _server.Put(oversize, new byte[SyncLimits.MaxBlobBytes + 1]);
        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(rightKey ? Master : AnotherKey());

        viewModel.Save();

        Assert.Equal(rightKey, viewModel.IsSaved);
        Assert.Equal(rightKey ? SyncSetupViewModel.SavedMessage : SyncSetupViewModel.WrongKeyMessage, viewModel.Message);
        Assert.DoesNotContain("download " + oversize, _server.Calls);
        Assert.Equal(1, _server.Count("download"));
    }

    [Fact]
    public void A_folder_with_only_an_oversize_file_cannot_check_the_key_and_the_key_is_refused_in_words_of_its_own()
    {
        _server.Put(JunkName(2), new byte[SyncLimits.MaxBlobBytes + 1]);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.CouldNotCheckMessage, viewModel.Message);
        Assert.NotEqual(SyncSetupViewModel.WrongKeyMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        Assert.Equal(0, _server.Count("download"));
        AssertNothingWasWritten();
    }

    [Fact]
    public void A_folder_with_only_junk_files_cannot_check_the_key_and_the_key_is_refused_in_words_of_its_own()
    {
        ServerHoldsAJunkFile(1);
        ServerHoldsAJunkFile(2);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.CouldNotCheckMessage, viewModel.Message);
        Assert.NotEqual(SyncSetupViewModel.WrongKeyMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        AssertNothingWasWritten();
    }

    [Fact]
    public void At_most_eight_files_are_tried_smallest_first()
    {
        for (int i = 1; i <= 9; i++)
        {
            ServerHoldsAJunkFile(i);
        }

        ServerHoldsABlobMadeWith(Master);
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.CouldNotCheckMessage, viewModel.Message);
        Assert.Equal(8, _server.Count("download"));
        Assert.DoesNotContain("download " + JunkName(9), _server.Calls);
        AssertNothingWasWritten();
    }

    [Fact]
    public void A_folder_with_no_file_named_like_a_blob_takes_a_typed_key_on_trust()
    {
        _server.Put("readme.txt", Encoding.ASCII.GetBytes("not ours"));
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.True(viewModel.IsSaved, viewModel.Message);
        Assert.Equal(0, _server.Count("download"));

        // No blob was there to try the key on, and that is said.
        Assert.Equal(SyncSetupViewModel.SavedUntriedMessage, viewModel.Message);
    }

    [Fact]
    public void A_kept_key_that_does_not_open_what_is_on_the_server_is_refused_and_nothing_is_changed()
    {
        SetUpMachine();
        ServerHoldsABlobMadeWith(AnotherKey());
        byte[] keyBefore = File.ReadAllBytes(_paths.KeyFile);
        byte[] settingsBefore = File.ReadAllBytes(_paths.SyncFile);
        var viewModel = NewViewModel();
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        Assert.True(viewModel.KeepKey);
        Assert.True(viewModel.CanSave, viewModel.Message);

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.WrongKeyMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        Assert.Equal(keyBefore, File.ReadAllBytes(_paths.KeyFile));
        Assert.Equal(settingsBefore, File.ReadAllBytes(_paths.SyncFile));
        Assert.Equal(0, _saved);
    }

    [Theory]
    [InlineData("list", RemoteFailure.Unreachable, SyncSetupViewModel.UnreachableMessage)]
    [InlineData("list", RemoteFailure.Folder, SyncSetupViewModel.FolderMessage)]
    [InlineData("download", RemoteFailure.Unreachable, SyncSetupViewModel.UnreachableMessage)]
    [InlineData("download", RemoteFailure.Server, SyncSetupViewModel.ServerMessage)]
    public void A_server_that_fails_after_the_sign_in_is_said_and_the_key_and_the_settings_stay_as_they_were(string failingCall, RemoteFailure failure, string message)
    {
        var earlier = SetUpMachine();
        ServerHoldsABlobMadeWith(Master);
        byte[] keyBefore = File.ReadAllBytes(_paths.KeyFile);
        byte[] settingsBefore = File.ReadAllBytes(_paths.SyncFile);
        var viewModel = NewViewModel();
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        Assert.True(viewModel.CanSave, viewModel.Message);
        _server.Fault = call => call == failingCall || call.StartsWith(failingCall + " ", StringComparison.Ordinal)
            ? new RemoteStoreException(failure, "SocketException 10054")
            : null;

        viewModel.Save();

        Assert.Equal(message, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(keyBefore, File.ReadAllBytes(_paths.KeyFile));
        Assert.Equal(settingsBefore, File.ReadAllBytes(_paths.SyncFile));
        Assert.Equal(earlier, SavedConfig());
        Assert.Equal(0, _saved);
        Assert.Equal(0, _connector.OpenNow);
    }

    [Fact]
    public void The_array_a_download_returns_is_wiped_after_the_check()
    {
        ServerHoldsABlobMadeWith(Master);
        var downloads = new List<byte[]>();
        var viewModel = NewViewModel(connector: new RecordingConnector(_connector, downloads));
        Fill(viewModel);
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);

        viewModel.Save();

        Assert.True(viewModel.IsSaved, viewModel.Message);
        byte[] downloaded = Assert.Single(downloads);
        Assert.All(downloaded, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHg==")]
    public void Text_that_is_not_a_sync_key_is_said_and_no_connection_is_made(string typed)
    {
        var viewModel = Confirmed();
        viewModel.EnterKey = true;
        viewModel.EnteredKey = typed;

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.KeyTextMessage, viewModel.Message);
        Assert.Equal(0, _connector.Opens);
        AssertNothingWasWritten();
    }

    [Fact]
    public void Save_cannot_be_pressed_with_the_key_box_empty()
    {
        var viewModel = Confirmed();

        viewModel.EnterKey = true;
        Assert.False(viewModel.CanSave);

        viewModel.EnteredKey = "x";
        Assert.True(viewModel.CanSave);
    }

    [Fact]
    public void Keeping_the_key_leaves_it_as_it_is_and_saves_the_passphrase_with_the_settings()
    {
        Keys.WriteMaster(Master);
        byte[] before = File.ReadAllBytes(_paths.KeyFile);
        _connector.Passphrase = "walrus tusk";
        var viewModel = NewViewModel();
        Fill(viewModel);
        viewModel.Passphrase = "walrus tusk";
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;

        viewModel.Save();

        Assert.True(viewModel.IsSaved, viewModel.Message);
        Assert.Equal(before, File.ReadAllBytes(_paths.KeyFile));
        Assert.Equal(SecretState.Read, Keys.ReadPassphrase(out string? passphrase, out _));
        Assert.Equal("walrus tusk", passphrase);
        Assert.Equal("walrus tusk", _connector.LastPassphrase);
        Assert.Equal(MemoryConnector.ServerKey, SavedConfig().HostKeyFingerprint);
    }

    [Fact]
    public void A_key_file_without_a_passphrase_leaves_no_passphrase_file_behind()
    {
        Keys.WritePassphrase("of an earlier key file");
        var viewModel = Confirmed();

        viewModel.Save();

        Assert.True(viewModel.IsSaved);
        Assert.False(File.Exists(_paths.PassphraseFile));
        Assert.Null(_connector.LastPassphrase);
    }

    [Theory]
    [InlineData(RemoteFailure.Refused, SyncSetupViewModel.RefusedMessage)]
    [InlineData(RemoteFailure.Folder, SyncSetupViewModel.FolderMessage)]
    [InlineData(RemoteFailure.Unreachable, SyncSetupViewModel.UnreachableMessage)]
    [InlineData(RemoteFailure.KeyFile, SyncSetupViewModel.KeyFileUnreadableMessage)]
    [InlineData(RemoteFailure.TooMuch, SyncSetupViewModel.ServerMessage)]
    public void A_sign_in_that_fails_is_said_and_nothing_is_written(RemoteFailure failure, string message)
    {
        var viewModel = Confirmed();
        _connector.Refusal = new RemoteStoreException(failure, "SshAuthenticationException");

        viewModel.Save();

        Assert.Equal(message, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        AssertNothingWasWritten();
    }

    [Fact]
    public void A_server_that_shows_another_key_at_the_sign_in_than_at_the_test_gets_nothing_and_nothing_is_saved()
    {
        var viewModel = Confirmed();
        _connector.Fingerprint = MemoryConnector.OtherKey;

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.KeyChangedAgainMessage, viewModel.Message);
        Assert.Empty(_server.Calls);
        AssertNothingWasWritten();
    }

    [Fact]
    public void A_key_that_cannot_be_saved_leaves_sync_off_and_is_not_called_saved()
    {
        Assert.True(SyncConfig.TryCreate(Host, "22", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var earlier, out _));
        earlier!.Save(_paths.SyncFile);
        var viewModel = NewViewModel();
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        _protector.RefuseToProtect = true;

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.NotSavedMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        Assert.Equal("", viewModel.GeneratedKey);
        Assert.Equal(0, _saved);

        // The settings that were there are gone, so that sync cannot run with half of the new ones.
        Assert.Equal(SyncConfigState.Missing, SyncConfig.Load(_paths.SyncFile).State);
        Assert.False(File.Exists(_paths.KeyFile));
    }

    [Fact]
    public void Settings_that_cannot_even_be_taken_away_leave_everything_as_it_was()
    {
        Assert.True(SyncConfig.TryCreate(Host, "22", User, KeyPath, RemoteDir, MemoryConnector.ServerKey, out var earlier, out _));
        earlier!.Save(_paths.SyncFile);
        Keys.WriteMaster(Master);
        var viewModel = NewViewModel();
        viewModel.GenerateKey = true;
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;

        using (new FileStream(_paths.SyncFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            viewModel.Save();
        }

        Assert.Equal(SyncSetupViewModel.NothingChangedMessage, viewModel.Message);
        Assert.Equal(earlier, SavedConfig());
        Assert.Equal(Master, SavedKey());
        Assert.Equal(0, _saved);
    }

    [Fact]
    public void A_step_that_fails_in_a_way_nobody_foresaw_is_said_in_fixed_words_and_logged()
    {
        var viewModel = Confirmed();
        _connector.Opened = _ => throw new InvalidOperationException("private words about secret-otter.example.org");

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.FailedMessage, viewModel.Message);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("A step of Sync Setup failed", _reports);
        AssertNothingWasWritten();
    }

    [Fact]
    public void A_step_of_the_write_that_fails_in_a_way_nobody_foresaw_still_says_that_sync_is_off()
    {
        SetUpMachine();

        // The protection throws something that none of the write's steps expect.
        var viewModel = NewViewModel(protector: new UnforeseenProtector(_protector));
        viewModel.GenerateKey = true;
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        Assert.True(viewModel.CanSave, viewModel.Message);

        viewModel.Save();

        Assert.Equal(SyncSetupViewModel.NotSavedMessage, viewModel.Message);
        Assert.False(viewModel.IsSaved);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("", viewModel.GeneratedKey);
        Assert.Equal(0, _saved);

        // The settings that were there were taken away first: sync is off, and nothing is half in use.
        Assert.Equal(SyncConfigState.Missing, SyncConfig.Load(_paths.SyncFile).State);
        Assert.Contains("Sync Setup could not save, and sync is off", _reports);
        Assert.DoesNotContain("A step of Sync Setup failed", _reports);
    }

    [Fact]
    public void Taking_the_settings_away_when_their_folder_is_not_there_is_not_a_failure()
    {
        var paths = new BeaconPaths(Path.Combine(_dir.Path, "not-made-yet"));
        var viewModel = NewViewModel(paths);
        Fill(viewModel);
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        Assert.False(Directory.Exists(paths.Root));

        viewModel.Save();

        Assert.True(viewModel.IsSaved, viewModel.Message);
        Assert.Equal(SyncSetupViewModel.SavedNewKeyMessage, viewModel.Message);
        Assert.Equal(SyncConfigState.Ready, SyncConfig.Load(paths.SyncFile).State);
        Assert.True(File.Exists(paths.KeyFile));
        Assert.Equal(1, _saved);
    }

    // ---- After saving, and closing ----

    [Fact]
    public void Closing_the_window_during_a_save_that_succeeded_still_tells_the_service_and_changes_no_property()
    {
        var viewModel = Confirmed();
        _holdBack = true;
        viewModel.Save();

        viewModel.Clear();
        var changed = Watch(viewModel);
        _heldBack.Single()();

        Assert.Equal(1, _saved);
        Assert.Empty(changed);
        Assert.Equal(new SyncConfig(Host, 2222, User, KeyPath, RemoteDir, MemoryConnector.ServerKey), SavedConfig());
        Assert.False(viewModel.IsSaved);
        Assert.Equal("", viewModel.GeneratedKey);
    }

    [Theory]
    [InlineData(nameof(SyncSetupViewModel.IsBusy))]
    [InlineData(nameof(SyncSetupViewModel.IsSaved))]
    [InlineData(nameof(SyncSetupViewModel.Message))]
    public void The_service_is_told_of_a_save_even_when_showing_it_in_the_window_fails(string property)
    {
        var viewModel = Confirmed();
        _holdBack = true;
        viewModel.Save();

        // Everything is written. Something that listens to the window's properties then throws
        // while the result is shown.
        Assert.Equal(SyncConfigState.Ready, SyncConfig.Load(_paths.SyncFile).State);
        Assert.Equal(0, _saved);
        viewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == property)
            {
                throw new InvalidOperationException("a binding that throws");
            }
        };

        Assert.Throws<InvalidOperationException>(_heldBack.Single());

        // Sync is set up on the disk, so the service must hear of it, whatever became of the window.
        Assert.Equal(1, _saved);
    }

    [Fact]
    public void A_save_that_did_not_succeed_does_not_tell_the_service_when_showing_it_fails_either()
    {
        var viewModel = Confirmed();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Refused, "SshAuthenticationException");
        _holdBack = true;
        viewModel.Save();
        viewModel.PropertyChanged += (_, _) => throw new InvalidOperationException("a binding that throws");

        Assert.Throws<InvalidOperationException>(_heldBack.Single());

        Assert.Equal(0, _saved);
        AssertNothingWasWritten();
    }

    [Fact]
    public void Closing_the_window_during_a_save_that_did_not_succeed_does_not_tell_the_service()
    {
        var viewModel = Confirmed();
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Refused, "SshAuthenticationException");
        _holdBack = true;
        viewModel.Save();

        viewModel.Clear();
        var changed = Watch(viewModel);
        _heldBack.Single()();

        Assert.Equal(0, _saved);
        Assert.Empty(changed);
        AssertNothingWasWritten();
    }

    [Fact]
    public void Clearing_tells_the_bindings_about_the_passphrase_and_the_typed_key_that_are_let_go_of()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        viewModel.Passphrase = "walrus tusk";
        viewModel.EnterKey = true;
        viewModel.EnteredKey = SyncKeys.Format(Master);
        var changed = Watch(viewModel);

        viewModel.Clear();

        Assert.Equal("", viewModel.Passphrase);
        Assert.Equal("", viewModel.EnteredKey);
        Assert.Contains(nameof(SyncSetupViewModel.Passphrase), changed);
        Assert.Contains(nameof(SyncSetupViewModel.EnteredKey), changed);
    }

    [Fact]
    public void Clearing_tells_the_bindings_that_the_new_key_that_was_shown_is_gone()
    {
        var viewModel = Confirmed();
        viewModel.Save();
        Assert.True(viewModel.HasGeneratedKey);
        var changed = Watch(viewModel);

        viewModel.Clear();

        Assert.Equal("", viewModel.GeneratedKey);
        Assert.False(viewModel.HasGeneratedKey);
        Assert.Contains(nameof(SyncSetupViewModel.GeneratedKey), changed);
        Assert.Contains(nameof(SyncSetupViewModel.HasGeneratedKey), changed);
    }

    [Fact]
    public void A_new_key_is_copied_through_the_marked_clipboard_writer()
    {
        var viewModel = Confirmed();
        viewModel.Save();

        viewModel.CopyGeneratedKey();

        Assert.Equal(viewModel.GeneratedKey, _clipboard.Text);
        Assert.Equal(SyncSetupViewModel.CopiedMessage, viewModel.Message);

        _clipboard.Broken = new InvalidOperationException("the clipboard is broken");
        viewModel.CopyGeneratedKey();
        Assert.Equal(SyncSetupViewModel.CopyFailedMessage, viewModel.Message);
    }

    [Fact]
    public void With_no_new_key_the_copy_button_touches_nothing()
    {
        var viewModel = Confirmed();

        viewModel.CopyGeneratedKey();

        Assert.Equal(0, _clipboard.Calls);
    }

    [Fact]
    public void After_the_window_has_closed_what_was_still_under_way_shows_nothing()
    {
        var viewModel = NewViewModel();
        Fill(viewModel);
        viewModel.Passphrase = "walrus tusk";
        viewModel.EnteredKey = SyncKeys.Format(Master);
        _holdBack = true;
        viewModel.TestConnection();

        viewModel.Clear();
        _heldBack.Single()();

        Assert.Equal("", viewModel.Fingerprint);
        Assert.Equal("", viewModel.Passphrase);
        Assert.Equal("", viewModel.EnteredKey);
        Assert.Equal("", viewModel.GeneratedKey);
    }

    [Fact]
    public void Nothing_that_is_logged_or_shown_names_the_server_the_user_or_a_path()
    {
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Unreachable, "SocketException 10061");
        var viewModel = NewViewModel();
        Fill(viewModel);
        var shown = new List<string>();
        viewModel.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(SyncSetupViewModel.Message))
            {
                shown.Add(viewModel.Message);
            }
        };

        viewModel.TestConnection();
        _connector.Refusal = null;
        viewModel.TestConnection();
        viewModel.FingerprintConfirmed = true;
        _connector.Refusal = new RemoteStoreException(RemoteFailure.Refused, "SshAuthenticationException");
        viewModel.Save();

        var everything = _reports.Concat(shown).ToList();
        Assert.Contains("Sync Setup could not read the server's host key (Unreachable, SocketException 10061)", everything);
        Assert.Contains("Sync Setup could not use the server (Refused, SshAuthenticationException)", everything);
        foreach (string word in new[] { "secret", "otter", "walrus", "SecretKeys", "home" })
        {
            Assert.DoesNotContain(everything, text => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    // Reads as the fake does, and fails to protect with an exception that nothing expects.
    private sealed class UnforeseenProtector(IKeyProtector inner) : IKeyProtector
    {
        public byte[] Protect(byte[] secret) => throw new InvalidOperationException("nobody foresaw this");

        public byte[] Unprotect(byte[] protectedBytes) => inner.Unprotect(protectedBytes);
    }

    // The in-memory connector, and a store that keeps hold of each array a download returned,
    // so that a test can look at what was left in it.
    private sealed class RecordingConnector(IRemoteConnector inner, List<byte[]> downloads) : IRemoteConnector
    {
        public KeyFileState CheckKeyFile(string path, string? passphrase) => inner.CheckKeyFile(path, passphrase);

        public HostKeyInfo ReadHostKey(SyncConfig config) => inner.ReadHostKey(config);

        public IRemoteStore Open(SyncConfig config, string? passphrase) => new RecordingStore(inner.Open(config, passphrase), downloads);
    }

    private sealed class RecordingStore(IRemoteStore inner, List<byte[]> downloads) : IRemoteStore
    {
        public IReadOnlyList<RemoteBlob> List() => inner.List();

        public byte[] Download(string name, long maxBytes)
        {
            byte[] bytes = inner.Download(name, maxBytes);
            downloads.Add(bytes);
            return bytes;
        }

        public RemoteBlob Upload(string name, byte[] content) => inner.Upload(name, content);

        public void Delete(string name) => inner.Delete(name);

        public DateTimeOffset Now() => inner.Now();

        public void Dispose() => inner.Dispose();
    }
}
