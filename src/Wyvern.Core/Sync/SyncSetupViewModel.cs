using System.ComponentModel;
using System.Globalization;
using Wyvern.Core.Platform;
using Wyvern.Core.Settings;

namespace Wyvern.Core.Sync;

/// <summary>Which sync key Sync Setup saves.</summary>
public enum SyncKeyChoice
{
    /// <summary>Keep the key this machine already has. Offered only when it has one that can be read.</summary>
    Keep,

    /// <summary>Make a new key: for the first machine, and an empty folder on the server.</summary>
    Generate,

    /// <summary>Use the key that was typed in: the one the other machine shows.</summary>
    Enter,
}

/// <summary>
/// Everything the Sync Setup window does, free of UI types so it can be tested: the fields,
/// what is checked when, and what is written where. The window only binds to it.
/// </summary>
/// <remarks>
/// <para>The steps, in the order the window walks through them:</para>
/// <list type="number">
/// <item>The owner fills in the server, the port, the user, the SSH key file and the folder on
/// the server, and chooses which sync key to use. While a check or the saving is under way, and
/// once the result has been saved, no input is taken (<see cref="CanEdit"/>): what is on the
/// screen is then always what was tested and what is saved.</item>
/// <item>Test Connection reads the key file on this machine (and asks for its passphrase when
/// it has one), then connects only far enough to be shown the server's host key, and shows that
/// key's fingerprint. Nothing about the owner's key is sent to a server whose key has not been
/// confirmed.</item>
/// <item>The owner compares the fingerprint with what <c>ssh-keygen -lf</c> prints on the
/// server and ticks the box. Changing any field afterwards takes the fingerprint away again:
/// what was confirmed was that server, at that address.</item>
/// <item>Save signs in, with the confirmed key pinned, and looks at the folder. A new key is
/// refused for a folder that already holds blobs, which it could not read; a key that was
/// typed in, or kept, is tried on one blob (the smallest that is a blob, of the first few), and
/// refused when it does not open it, or when no blob could be used to try it. Only then is
/// anything written. A folder that holds no blob at all has nothing to try such a key on: the
/// key is saved, and the sentence shown says that it could not be checked, because a slip in
/// the folder's name on a second machine looks exactly like that.</item>
/// </list>
/// <para>What is written, in this order, each file through a temporary file that then takes its
/// place: sync.json is first taken away, so that sync is off while the rest is in doubt; then
/// the sync key (unless it is kept); then the passphrase of the key file, or its file is removed
/// when there is none; and last sync.json with the fingerprint pinned, which is what turns sync
/// on. If any step fails, the ones after it are not taken, sync stays off, and the sentence
/// shown says so: nothing is ever called saved that was not. When the window is closed during
/// the saving, what was written is still written and <c>saved</c> is still called, so that sync
/// starts; only the view model's own properties are left alone.</para>
/// <para>Everything that reads a file, unprotects a key or connects is handed to
/// <c>background</c>, and what comes of it is handed back through <c>post</c>, so the window
/// never waits. A key that is generated is shown once after saving. The passphrase and a typed
/// key are text while the window is open, which cannot be wiped; <see cref="Clear"/> lets go of
/// them when it closes.</para>
/// </remarks>
public sealed class SyncSetupViewModel : INotifyPropertyChanged
{
    public const string PassphraseNeededMessage = "This key file is protected by a passphrase. Type it and press Test Connection again.";
    public const string PassphraseWrongMessage = "The key file could not be opened with that passphrase.";
    public const string KeyFileUnreadableMessage = "The key file could not be read, or it is not a private key. It must be on a drive of this machine, kept on this disk (not an online-only file), and not reached through a link.";
    public const string UnreachableMessage = "The server could not be reached. Check the server, the port and your network.";
    public const string NotSshMessage = "The server did not answer as an SSH server does. Check the server and the port.";
    public const string CompareMessage = "Compare this fingerprint with the one your server prints for its own key (ssh-keygen -lf on the server). Tick the box only if they are the same.";
    public const string OtherKeyMessage = "This is NOT the key that was confirmed for this server before. If you did not change the server yourself, do not go on. Otherwise compare the fingerprint as the first time.";
    public const string KeyTextMessage = "That is not a sync key. A sync key is 44 characters, as Show Sync Key shows it on your other machine.";
    public const string NoKeptKeyMessage = "The sync key on this machine could not be read. Enter the key from your other machine, or generate a new one.";
    public const string KeyChangedAgainMessage = "The server showed another key this time, so nothing was sent and nothing was saved. Press Test Connection again.";
    public const string RefusedMessage = "The server refused the sign-in, so nothing was saved. Check the user name, and that this key is allowed on the server.";
    public const string FolderMessage = "The folder on the server could not be made or listed, so nothing was saved. Check the path, and that the folder it is in exists.";
    public const string ServerMessage = "The server could not be used, so nothing was saved. Details are in the log file.";
    public const string NotEmptyMessage = "The folder on the server already holds synced files, which a new key cannot read. Enter the key from your other machine, or choose an empty folder.";
    public const string WrongKeyMessage = "This key does not open what is on the server, so nothing was saved. Check that it is the key your other machine shows.";
    public const string CouldNotCheckMessage = "This key could not be checked against what is on the server, because none of the files there could be used to try it. Nothing was saved. Check that the folder is the one your other machine syncs to.";
    public const string NothingChangedMessage = "The settings could not be saved, and nothing was changed. Details are in the log file.";
    public const string NotSavedMessage = "The settings could not be saved, and sync is off. Details are in the log file.";
    public const string SavedMessage = "Saved. Sync starts now.";
    public const string SavedUntriedMessage = "Saved. Sync starts now. The server's folder was empty, so the key could not be checked against it. If your other machine already syncs, check the folder name.";
    public const string SavedNewKeyMessage = "Saved. Sync starts now. This is your sync key: enter it in Sync Setup on your other machine. Show Sync Key shows it again.";
    public const string FailedMessage = "That did not work. Details are in the log file.";
    public const string CopiedMessage = "Copied. The key stays on the clipboard until you copy something else. Clipboard history does not record it.";
    public const string CopyFailedMessage = "The key could not be copied. Press Copy again, or use Show Sync Key later.";

    // How many of the blobs in the folder a key is tried on at most, smallest first.
    private const int MostBlobsTried = 8;

    private readonly WyvernPaths _paths;
    private readonly IRemoteConnector _connector;
    private readonly KeyStore _keys;
    private readonly IClipboard _clipboard;
    private readonly Action<Action> _background;
    private readonly Action<Action> _post;
    private readonly Action _saved;
    private readonly Action<string, Exception?> _report;

    private string _host = "";
    private string _port = "22";
    private string _user = "";
    private string _keyFile = "";
    private string _remoteDir = "";
    private string _passphrase = "";
    private string _enteredKey = "";
    private string _generatedKey = "";
    private string _message = "";
    private string _fingerprint = "";
    private string _fingerprintKind = "";
    private string? _pinnedBefore;
    private string? _pinnedHost;
    private int _pinnedPort;
    private SyncConfig? _tested;
    private SyncKeyChoice _keyChoice = SyncKeyChoice.Generate;
    private bool _canKeep;
    private bool _needsPassphrase;
    private bool _confirmed;
    private bool _busy;
    private bool _isSaved;
    private bool _closed;

    /// <param name="protector">Protects the sync key and the passphrase for this Windows user.</param>
    /// <param name="clipboard">The clipboard writer that marks what it writes, for copying a new key.</param>
    /// <param name="background">Runs work on a background thread and returns at once.</param>
    /// <param name="post">Runs an action on the UI thread, later.</param>
    /// <param name="saved">Called on the UI thread after everything was saved, so that sync starts.</param>
    /// <param name="report">Receives fixed words and an exception for the log. Never a field of the window.</param>
    public SyncSetupViewModel(
        WyvernPaths paths,
        IRemoteConnector connector,
        IKeyProtector protector,
        IClipboard clipboard,
        Action<Action> background,
        Action<Action> post,
        Action? saved = null,
        Action<string, Exception?>? report = null)
    {
        _paths = paths;
        _connector = connector;
        _keys = new KeyStore(paths.KeyFile, paths.PassphraseFile, protector);
        _clipboard = clipboard;
        _background = background;
        _post = post;
        _saved = saved ?? (() => { });
        _report = report ?? ((_, _) => { });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The server's name or address.</summary>
    public string Host
    {
        get => _host;
        set => Edit(ref _host, value, nameof(Host));
    }

    /// <summary>The SSH port, as text. Empty means 22.</summary>
    public string Port
    {
        get => _port;
        set => Edit(ref _port, value, nameof(Port));
    }

    public string User
    {
        get => _user;
        set => Edit(ref _user, value, nameof(User));
    }

    /// <summary>The full path of the SSH private key file.</summary>
    public string KeyFile
    {
        get => _keyFile;
        set => Edit(ref _keyFile, value, nameof(KeyFile));
    }

    /// <summary>The folder on the server.</summary>
    public string RemoteDir
    {
        get => _remoteDir;
        set => Edit(ref _remoteDir, value, nameof(RemoteDir));
    }

    /// <summary>The passphrase of the key file. The window sets it from its password box; it is never read back into the window.</summary>
    public string Passphrase
    {
        get => _passphrase;
        set => Edit(ref _passphrase, value, nameof(Passphrase));
    }

    /// <summary>True once the key file has said that it needs a passphrase, so that the window shows the box for it.</summary>
    public bool NeedsPassphrase
    {
        get => _needsPassphrase;
        private set => Set(ref _needsPassphrase, value, nameof(NeedsPassphrase));
    }

    /// <summary>Which sync key to save.</summary>
    public SyncKeyChoice KeyChoice
    {
        get => _keyChoice;
        set
        {
            if (!CanEdit)
            {
                RaiseChoices();
                return;
            }

            if (value == SyncKeyChoice.Keep && !_canKeep)
            {
                return;
            }

            if (Set(ref _keyChoice, value, nameof(KeyChoice)))
            {
                Raise(nameof(KeepKey));
                Raise(nameof(GenerateKey));
                Raise(nameof(EnterKey));
                Raise(nameof(CanSave));
            }
        }
    }

    /// <summary>The three choices as one true-or-false each, which is what a radio button binds to.</summary>
    public bool KeepKey
    {
        get => _keyChoice == SyncKeyChoice.Keep;
        set => Choose(value, SyncKeyChoice.Keep);
    }

    public bool GenerateKey
    {
        get => _keyChoice == SyncKeyChoice.Generate;
        set => Choose(value, SyncKeyChoice.Generate);
    }

    public bool EnterKey
    {
        get => _keyChoice == SyncKeyChoice.Enter;
        set => Choose(value, SyncKeyChoice.Enter);
    }

    /// <summary>True when this machine has a sync key that can be read, so that keeping it can be offered.</summary>
    public bool CanKeep
    {
        get => _canKeep;
        private set => Set(ref _canKeep, value, nameof(CanKeep));
    }

    /// <summary>The key from the other machine, as typed.</summary>
    public string EnteredKey
    {
        get => _enteredKey;
        set
        {
            if (!CanEdit)
            {
                Raise(nameof(EnteredKey));
                return;
            }

            if (Set(ref _enteredKey, value ?? "", nameof(EnteredKey)))
            {
                Raise(nameof(CanSave));
            }
        }
    }

    /// <summary>The server's fingerprint as Test Connection found it, or empty.</summary>
    public string Fingerprint
    {
        get => _fingerprint;
        private set
        {
            if (Set(ref _fingerprint, value, nameof(Fingerprint)))
            {
                Raise(nameof(FingerprintShown));
                Raise(nameof(CanSave));
            }
        }
    }

    /// <summary>The kind of host key, such as <c>ssh-ed25519</c>, which says which of the server's key files to compare with.</summary>
    public string FingerprintKind
    {
        get => _fingerprintKind;
        private set => Set(ref _fingerprintKind, value, nameof(FingerprintKind));
    }

    public bool FingerprintShown => _fingerprint.Length > 0;

    /// <summary>The owner's word that the fingerprint is the server's. It can only be given while a fingerprint is shown.</summary>
    public bool FingerprintConfirmed
    {
        get => _confirmed;
        set
        {
            if (!CanEdit)
            {
                Raise(nameof(FingerprintConfirmed));
                return;
            }

            SetConfirmed(value && FingerprintShown);
        }
    }

    /// <summary>The one sentence the window shows: what to do next, or what went wrong. Fixed words.</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value, nameof(Message));
    }

    /// <summary>True while a check or the saving is under way. The buttons are off meanwhile.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value, nameof(IsBusy)))
            {
                Raise(nameof(CanTest));
                Raise(nameof(CanSave));
                Raise(nameof(CanEdit));
            }
        }
    }

    public bool CanTest => !_busy && !_isSaved;

    /// <summary>
    /// False while a check or the saving is under way, and once the result has been saved: then
    /// no input is taken, so that what is on the screen is always what was tested and what is saved.
    /// </summary>
    public bool CanEdit => !_busy && !_isSaved;

    /// <summary>True when the fingerprint is shown and confirmed and a key is chosen: only then can Save be pressed.</summary>
    public bool CanSave =>
        !_busy && !_isSaved && FingerprintShown && _confirmed && _tested is not null
        && (_keyChoice != SyncKeyChoice.Enter || _enteredKey.Trim().Length > 0);

    /// <summary>True once everything was saved. The window then only shows the result.</summary>
    public bool IsSaved
    {
        get => _isSaved;
        private set
        {
            if (Set(ref _isSaved, value, nameof(IsSaved)))
            {
                Raise(nameof(CanTest));
                Raise(nameof(CanSave));
                Raise(nameof(CanEdit));
            }
        }
    }

    /// <summary>The new sync key as text, shown once after saving when one was generated. Otherwise empty.</summary>
    public string GeneratedKey
    {
        get => _generatedKey;
        private set
        {
            if (Set(ref _generatedKey, value, nameof(GeneratedKey)))
            {
                Raise(nameof(HasGeneratedKey));
            }
        }
    }

    public bool HasGeneratedKey => _generatedKey.Length > 0;

    /// <summary>
    /// Fills the fields from the sync.json that is there, and finds out whether this machine has
    /// a sync key to keep. Call it away from the UI thread, before the window is shown.
    /// </summary>
    public void Load()
    {
        var loaded = SyncConfig.Load(_paths.SyncFile);
        if (loaded.Config is { } config)
        {
            _host = config.Host;
            _port = config.Port.ToString(CultureInfo.InvariantCulture);
            _user = config.User;
            _keyFile = config.PrivateKeyPath;
            _remoteDir = config.RemoteDir;
            _pinnedBefore = config.HostKeyFingerprint;
            _pinnedHost = config.Host;
            _pinnedPort = config.Port;
        }

        bool hasKey = _keys.ReadMaster(out byte[] master, out _) == SecretState.Read;
        SyncKeys.Wipe(master);
        _canKeep = hasKey;
        _keyChoice = hasKey ? SyncKeyChoice.Keep : SyncKeyChoice.Generate;
        _needsPassphrase = File.Exists(_paths.PassphraseFile);
        foreach (string property in new[]
        {
            nameof(Host), nameof(Port), nameof(User), nameof(KeyFile), nameof(RemoteDir), nameof(CanKeep),
            nameof(KeyChoice), nameof(KeepKey), nameof(GenerateKey), nameof(EnterKey), nameof(NeedsPassphrase),
        })
        {
            Raise(property);
        }
    }

    /// <summary>What the Test Connection button does. Returns at once; <see cref="Message"/> says what came of it.</summary>
    public void TestConnection()
    {
        if (!CanTest)
        {
            return;
        }

        Forget();
        if (!SyncConfig.TryCreate(_host, _port, _user, _keyFile, _remoteDir, null, out var config, out string problem))
        {
            Message = problem;
            return;
        }

        string passphrase = _passphrase;
        Start(() =>
        {
            switch (_connector.CheckKeyFile(config!.PrivateKeyPath, passphrase))
            {
                case KeyFileState.NeedsPassphrase:
                    return () =>
                    {
                        NeedsPassphrase = true;
                        Message = PassphraseNeededMessage;
                    };

                case KeyFileState.WrongPassphrase:
                    return () =>
                    {
                        NeedsPassphrase = true;
                        Message = PassphraseWrongMessage;
                    };

                case KeyFileState.Unreadable:
                    return () => Message = KeyFileUnreadableMessage;
            }

            HostKeyInfo shown;
            try
            {
                shown = _connector.ReadHostKey(config);
            }
            catch (RemoteStoreException ex)
            {
                _report($"Sync Setup could not read the server's host key ({ex.Failure}, {ex.Cause ?? "no detail"})", null);
                return () => Message = ex.Failure == RemoteFailure.Unreachable ? UnreachableMessage : NotSshMessage;
            }

            if (!HostKeyFingerprint.IsValid(shown.Fingerprint))
            {
                return () => Message = NotSshMessage;
            }

            return () =>
            {
                _tested = config;
                FingerprintKind = shown.Algorithm;
                Fingerprint = shown.Fingerprint;

                // Another key than the one confirmed before is an alarm only for that same server.
                // Moving to another one, which is the normal move, is compared as the first time.
                bool sameServer = string.Equals(config!.Host, _pinnedHost, StringComparison.OrdinalIgnoreCase)
                    && config.Port == _pinnedPort;
                Message = sameServer && _pinnedBefore is not null && _pinnedBefore != shown.Fingerprint ? OtherKeyMessage : CompareMessage;
            };
        });
    }

    /// <summary>What the Save button does. Returns at once; <see cref="Message"/> says what came of it, and <see cref="IsSaved"/> whether it was saved.</summary>
    public void Save()
    {
        if (!CanSave)
        {
            return;
        }

        var config = _tested!.Pinned(_fingerprint);
        var choice = _keyChoice;
        string passphrase = _passphrase;
        byte[] typed = [];
        if (choice == SyncKeyChoice.Enter && !SyncKeys.TryParse(_enteredKey, out typed))
        {
            Message = KeyTextMessage;
            return;
        }

        // Set in the background when everything has been written. The service is told then,
        // whether or not the window is still open to show it.
        bool written = false;
        Start(
            () =>
            {
                byte[] master = typed;
                try
                {
                    if (choice == SyncKeyChoice.Keep && _keys.ReadMaster(out master, out _) != SecretState.Read)
                    {
                        return () => Message = NoKeptKeyMessage;
                    }

                    if (choice == SyncKeyChoice.Generate)
                    {
                        master = SyncKeys.NewMaster();
                    }

                    if (CheckServer(config, passphrase, master, isNew: choice == SyncKeyChoice.Generate, out bool tried) is { } problem)
                    {
                        return () => Message = problem;
                    }

                    if (Write(config, passphrase, master, keep: choice == SyncKeyChoice.Keep) is { } notSaved)
                    {
                        return () => Message = notSaved;
                    }

                    written = true;
                    string shownOnce = choice == SyncKeyChoice.Generate ? SyncKeys.Format(master) : "";

                    // A new key is for an empty folder. A key that was typed in or kept and found
                    // nothing to be tried on is saved, and is said to be untried: with a slip in
                    // the folder's name, the other machine's files are in another folder.
                    string savedAs = shownOnce.Length > 0 ? SavedNewKeyMessage : tried ? SavedMessage : SavedUntriedMessage;
                    return () =>
                    {
                        GeneratedKey = shownOnce;
                        IsSaved = true;
                        Message = savedAs;
                    };
                }
                finally
                {
                    SyncKeys.Wipe(master);
                }
            },
            () =>
            {
                if (written)
                {
                    _saved();
                }
            });
    }

    /// <summary>
    /// What the Copy button beside a new key does. The key is shown as text that cannot be
    /// selected, so this is the only way it reaches the clipboard, and it goes through the
    /// writer that keeps it out of every clipboard history. For the UI thread.
    /// </summary>
    public void CopyGeneratedKey()
    {
        if (!HasGeneratedKey)
        {
            return;
        }

        try
        {
            _clipboard.SetText(_generatedKey);
            Message = CopiedMessage;
        }
        catch (Exception ex)
        {
            _report("The new sync key could not be put on the clipboard", ex);
            Message = CopyFailedMessage;
        }
    }

    /// <summary>Call when the window closes: the passphrase and the keys that were typed or shown are let go of, and nothing more is shown.</summary>
    public void Clear()
    {
        _closed = true;

        // The bindings are told, so that no control keeps showing a key or a passphrase.
        Set(ref _passphrase, "", nameof(Passphrase));
        if (Set(ref _enteredKey, "", nameof(EnteredKey)))
        {
            Raise(nameof(CanSave));
        }

        GeneratedKey = "";
    }

    // Signs in with the confirmed key pinned and looks at the folder. Null when the server and
    // the sync key fit together; otherwise the sentence that says why not. Nothing is written.
    // "Tried" is true only when the key opened a blob that is there: a folder that holds no
    // blob takes any key, because there is nothing to try one on.
    private string? CheckServer(SyncConfig config, string passphrase, byte[] master, bool isNew, out bool tried)
    {
        tried = false;
        try
        {
            using var store = _connector.Open(config, passphrase.Length > 0 ? passphrase : null);
            var blobs = store.List().Where(blob => BlobCodec.IsBlobName(blob.Name)).ToList();
            if (blobs.Count == 0)
            {
                return null;
            }

            if (isNew)
            {
                return NotEmptyMessage;
            }

            // The key is tried on one blob that can decide: the smallest first, a file that is too
            // large to download or is not a blob at all is passed over, and at most a few are
            // tried. A blob that does not authenticate under the key was made with another key:
            // saving would only fill the server with a second copy of everything, and mark every
            // blob that is there as unreadable. When none could decide, the key is not taken on
            // trust either.
            using var keys = SyncKeys.FromMaster(master);
            var candidates = blobs
                .Where(blob => blob.Size <= SyncLimits.MaxBlobBytes)
                .OrderBy(blob => blob.Size)
                .ThenBy(blob => blob.Name, StringComparer.Ordinal)
                .Take(MostBlobsTried);
            foreach (var blob in candidates)
            {
                byte[] downloaded = store.Download(blob.Name, SyncLimits.MaxBlobBytes);
                try
                {
                    BlobCodec.Open(keys, blob.Name, downloaded, out var blobProblem);
                    if (blobProblem == BlobProblem.NotAuthentic)
                    {
                        return WrongKeyMessage;
                    }

                    if (blobProblem != BlobProblem.NotABlob)
                    {
                        // It authenticates: this key made it. Whether what it holds is usable is for sync to say.
                        tried = true;
                        return null;
                    }
                }
                finally
                {
                    SyncKeys.Wipe(downloaded);
                }
            }

            return CouldNotCheckMessage;
        }
        catch (RemoteStoreException ex)
        {
            _report($"Sync Setup could not use the server ({ex.Failure}, {ex.Cause ?? "no detail"})", null);
            return ex.Failure switch
            {
                RemoteFailure.HostKeyChanged => KeyChangedAgainMessage,
                RemoteFailure.Refused => RefusedMessage,
                RemoteFailure.KeyFile => KeyFileUnreadableMessage,
                RemoteFailure.Folder => FolderMessage,
                RemoteFailure.Unreachable => UnreachableMessage,
                _ => ServerMessage,
            };
        }
    }

    // Writes what Save writes, in the order the remarks give. Null when all of it was written.
    // Otherwise the sentence for what happened: either nothing was changed (sync.json could
    // not even be taken away), or a later step failed, the steps after it were not taken, and
    // sync.json is not there, so sync is off.
    private string? Write(SyncConfig config, string passphrase, byte[] master, bool keep)
    {
        try
        {
            File.Delete(_paths.SyncFile);
        }
        catch (DirectoryNotFoundException)
        {
            // The folder is not there yet, so there is no settings file to take away.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _report("Sync Setup could not save, and changed nothing", ex);
            return NothingChangedMessage;
        }

        try
        {
            if (!keep)
            {
                _keys.WriteMaster(master);
            }

            _keys.WritePassphrase(passphrase);
            config.Save(_paths.SyncFile);
            return null;
        }
        catch (Exception ex)
        {
            // Whatever it was, foreseen or not: sync.json is gone, so the words must say that sync is off.
            _report("Sync Setup could not save, and sync is off", ex);
            return NotSavedMessage;
        }
    }

    // Runs a piece of work in the background. The work answers with what to do on the UI
    // thread when it is done; nothing of this view model is touched from the background.
    // What the work answers with is dropped when the window has closed meanwhile. The
    // afterwards step is not: it is for what has to happen whether or not anything is shown,
    // and so also when showing it failed. Whatever listens to this view model's properties is
    // the window's code and may throw; what it threw goes on to whoever posted, after the
    // afterwards step has been taken.
    private void Start(Func<Action> work, Action? afterwards = null)
    {
        IsBusy = true;
        _background(() =>
        {
            Action done;
            try
            {
                done = work();
            }
            catch (Exception ex)
            {
                _report("A step of Sync Setup failed", ex);
                done = () => Message = FailedMessage;
            }

            _post(() =>
            {
                try
                {
                    if (!_closed)
                    {
                        IsBusy = false;
                        done();
                    }
                }
                finally
                {
                    afterwards?.Invoke();
                }
            });
        });
    }

    // A field that the test rests on has changed: what was shown and confirmed is for another
    // server. While a check or the saving is under way, and after it has been saved, no new
    // value is taken; the property is announced again so that a control shows the kept one.
    private void Edit(ref string field, string? value, string property)
    {
        if (!CanEdit)
        {
            Raise(property);
            return;
        }

        if (Set(ref field, value ?? "", property))
        {
            Forget();
        }
    }

    private void Forget()
    {
        _tested = null;
        SetConfirmed(false);
        FingerprintKind = "";
        Fingerprint = "";
        Message = "";
    }

    private void SetConfirmed(bool confirmed)
    {
        if (Set(ref _confirmed, confirmed, nameof(FingerprintConfirmed)))
        {
            Raise(nameof(CanSave));
        }
    }

    private void Choose(bool chosen, SyncKeyChoice choice)
    {
        if (!CanEdit)
        {
            RaiseChoices();
            return;
        }

        if (chosen)
        {
            KeyChoice = choice;
        }
    }

    private void RaiseChoices()
    {
        Raise(nameof(KeyChoice));
        Raise(nameof(KeepKey));
        Raise(nameof(GenerateKey));
        Raise(nameof(EnterKey));
    }

    private bool Set<T>(ref T field, T value, string property)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(property);
        return true;
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
