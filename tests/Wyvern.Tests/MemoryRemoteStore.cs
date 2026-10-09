using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// A server's folder of blobs that lives in the test. It keeps to what <see cref="IRemoteStore"/>
/// promises: only a blob's name is read or written, an upload replaces a blob whole, times are
/// whole seconds by the server's own clock. A test can put anything in the folder as another
/// machine, or the server itself, might, and can make any call fail.
/// </summary>
public sealed class MemoryRemoteStore : IRemoteStore
{
    private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset Modified)> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The server's clock. Each write moves it on by a second, so that two writes never share a time.</summary>
    public DateTimeOffset Clock { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every call that reached the server, in order: "list", "now", "download name", "upload name", "delete name".</summary>
    public List<string> Calls { get; } = [];

    /// <summary>
    /// Asked before every call with what <see cref="Calls"/> would record. When it answers with
    /// an exception, the call fails with it and changes nothing.
    /// </summary>
    public Func<string, RemoteStoreException?>? Fault { get; set; }

    /// <summary>Called after a blob has been stored, with its name. A test uses it to do something in the middle of a run.</summary>
    public Action<string>? Uploaded { get; set; }

    /// <summary>The names in the folder, sorted.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>How many calls of this kind were made: "upload", "download", "delete", "list" or "now".</summary>
    public int Count(string kind)
    {
        lock (_gate)
        {
            return Calls.Count(call => call == kind || call.StartsWith(kind + " ", StringComparison.Ordinal));
        }
    }

    /// <summary>Puts a file in the folder as something else would: any name, any bytes, no rules.</summary>
    public void Put(string name, byte[] bytes, DateTimeOffset? modified = null)
    {
        lock (_gate)
        {
            _files[name] = (bytes, Seconds(modified ?? Tick()));
        }
    }

    /// <summary>The bytes under a name, or null.</summary>
    public byte[]? Get(string name)
    {
        lock (_gate)
        {
            return _files.TryGetValue(name, out var file) ? file.Bytes : null;
        }
    }

    /// <summary>Takes a file out of the folder as something else would, without a call being recorded.</summary>
    public void Remove(string name)
    {
        lock (_gate)
        {
            _files.Remove(name);
        }
    }

    public IReadOnlyList<RemoteBlob> List()
    {
        lock (_gate)
        {
            Enter("list");

            // A server lists a folder in no particular order. This one lists it backwards: the
            // same way every time, and never the sorted way that a caller might come to lean on.
            return [.. _files.OrderByDescending(file => file.Key, StringComparer.Ordinal).Select(file => new RemoteBlob(file.Key, file.Value.Bytes.Length, file.Value.Modified))];
        }
    }

    public byte[] Download(string name, long maxBytes)
    {
        RequireBlobName(name, allowTemporary: false);
        lock (_gate)
        {
            Enter("download " + name);
            if (!_files.TryGetValue(name, out var file))
            {
                throw new RemoteStoreException(RemoteFailure.Missing);
            }

            if (file.Bytes.Length > maxBytes)
            {
                throw new RemoteStoreException(RemoteFailure.TooMuch);
            }

            // A copy: the caller decrypts what it is given in place.
            return [.. file.Bytes];
        }
    }

    public RemoteBlob Upload(string name, byte[] content)
    {
        RequireBlobName(name, allowTemporary: false);
        RemoteBlob stored;
        lock (_gate)
        {
            Enter("upload " + name);
            var modified = Seconds(Tick());
            _files[name] = ([.. content], modified);
            stored = new RemoteBlob(name, content.Length, modified);
        }

        Uploaded?.Invoke(name);
        return stored;
    }

    public void Delete(string name)
    {
        RequireBlobName(name, allowTemporary: true);
        lock (_gate)
        {
            Enter("delete " + name);
            _files.Remove(name);
        }
    }

    public DateTimeOffset Now()
    {
        lock (_gate)
        {
            Enter("now");
            return Seconds(Clock);
        }
    }

    public void Dispose()
    {
    }

    private static DateTimeOffset Seconds(DateTimeOffset time) => new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), time.Offset);

    private static void RequireBlobName(string name, bool allowTemporary)
    {
        if (!BlobCodec.IsBlobName(name) && !(allowTemporary && BlobCodec.IsTemporaryName(name)))
        {
            throw new ArgumentException("That is not the name of a blob.", nameof(name));
        }
    }

    private DateTimeOffset Tick()
    {
        Clock = Clock.AddSeconds(1);
        return Clock;
    }

    private void Enter(string call)
    {
        if (Fault?.Invoke(call) is { } failure)
        {
            throw failure;
        }

        Calls.Add(call);
    }
}

/// <summary>
/// Stands in for everything that can reach a server. It counts what it was asked, so a test
/// can say that no connection was made, and it refuses a host key that is not the pinned one
/// as the real connector does.
/// </summary>
public sealed class MemoryConnector(MemoryRemoteStore store) : IRemoteConnector
{
    /// <summary>A fingerprint of the right shape for the made-up server's key.</summary>
    public const string ServerKey = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>
    /// The fingerprint of another key, as after the server was replaced. The last character is
    /// one that a hash can end in, which a fingerprint is checked for.
    /// </summary>
    public const string OtherKey = "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA";

    /// <summary>The key the server shows now.</summary>
    public string Fingerprint { get; set; } = ServerKey;

    /// <summary>When set, the server cannot be reached this way: <see cref="Open"/> and <see cref="ReadHostKey"/> throw it.</summary>
    public RemoteStoreException? Refusal { get; set; }

    /// <summary>What a key file is said to be when no passphrase, or the wrong one, is given.</summary>
    public KeyFileState KeyFile { get; set; } = KeyFileState.Usable;

    /// <summary>The passphrase the key file needs, or null when it needs none.</summary>
    public string? Passphrase { get; set; }

    public int Opens { get; private set; }

    public int HostKeyReads { get; private set; }

    public int KeyFileChecks { get; private set; }

    /// <summary>How many connections were opened and not yet closed.</summary>
    public int OpenNow { get; private set; }

    /// <summary>The settings and the passphrase of the last <see cref="Open"/>.</summary>
    public SyncConfig? LastConfig { get; private set; }

    public string? LastPassphrase { get; private set; }

    /// <summary>Called when a connection has been opened, with the connection. A test uses it to close one from outside.</summary>
    public Action<IRemoteStore>? Opened { get; set; }

    /// <summary>
    /// Called at the start of every call on a connection that is open, before the call reaches
    /// the folder, with a handle that is set when that connection is closed. A test uses it to
    /// hold a call in flight: it waits in here, as the call would wait for a server that is slow
    /// to answer. A call whose connection has been closed by the time this comes back ends with
    /// <see cref="RemoteFailure.Unreachable"/>, as a call of the SFTP store does that is under
    /// way when the store is disposed. Here such a call has changed nothing. With a real server
    /// it may or may not have: a call that ends this way says nothing of what the server did.
    /// </summary>
    public Action<WaitHandle>? InFlight { get; set; }

    public KeyFileState CheckKeyFile(string path, string? passphrase)
    {
        KeyFileChecks++;
        if (Passphrase is null)
        {
            return KeyFile;
        }

        return string.IsNullOrEmpty(passphrase) ? KeyFileState.NeedsPassphrase
            : passphrase == Passphrase ? KeyFileState.Usable
            : KeyFileState.WrongPassphrase;
    }

    public HostKeyInfo ReadHostKey(SyncConfig config)
    {
        HostKeyReads++;
        if (Refusal is not null)
        {
            throw Refusal;
        }

        return new HostKeyInfo(Fingerprint, "ssh-ed25519");
    }

    public IRemoteStore Open(SyncConfig config, string? passphrase)
    {
        Opens++;
        LastConfig = config;
        LastPassphrase = passphrase;
        if (config.HostKeyFingerprint != Fingerprint)
        {
            throw new RemoteStoreException(RemoteFailure.HostKeyChanged);
        }

        if (Refusal is not null)
        {
            throw Refusal;
        }

        if (Passphrase is not null && passphrase != Passphrase)
        {
            throw new RemoteStoreException(RemoteFailure.KeyFile);
        }

        OpenNow++;
        var connection = new Connection(this, store);
        Opened?.Invoke(connection);
        return connection;
    }

    // One connection to the shared folder. Once it is closed it answers nothing more.
    private sealed class Connection(MemoryConnector owner, MemoryRemoteStore store) : IRemoteStore
    {
        // Set when the connection is closed, for a call that is held in flight to wait on. It is
        // never disposed: such a call may still be waiting on it.
        private readonly ManualResetEvent _closing = new(false);
        private volatile bool _closed;

        public IReadOnlyList<RemoteBlob> List() => Open().List();

        public byte[] Download(string name, long maxBytes) => Open().Download(name, maxBytes);

        public RemoteBlob Upload(string name, byte[] content) => Open().Upload(name, content);

        public void Delete(string name) => Open().Delete(name);

        public DateTimeOffset Now() => Open().Now();

        public void Dispose()
        {
            if (!_closed)
            {
                _closed = true;
                owner.OpenNow--;
                _closing.Set();
            }
        }

        // Every call comes through here. One that is started after the close fails at once. One
        // that is under way when the close comes, which a test arranges by holding it in the
        // hook, ends the same way and never reaches the folder.
        private MemoryRemoteStore Open()
        {
            if (!_closed)
            {
                owner.InFlight?.Invoke(_closing);
            }

            return _closed ? throw new RemoteStoreException(RemoteFailure.Unreachable, "closed") : store;
        }
    }
}
