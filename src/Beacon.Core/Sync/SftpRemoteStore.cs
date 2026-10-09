using System.ComponentModel;
using Beacon.Core.Files;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Beacon.Core.Sync;

/// <summary>
/// The server's folder of blobs over SFTP. This is the only file in Beacon that uses the SSH
/// library, and so the only code that can open a network connection: nothing else in Beacon
/// uses the SSH library or .NET's own network types. It opens one connection, to the server
/// and port in sync.json, when it is asked to and never otherwise.
/// </summary>
/// <remarks>
/// <para>It is thin on purpose, because it cannot be run without a server: what is uploaded,
/// when, and what a downloaded blob means are all decided elsewhere and tested against the
/// store that lives in memory. One integration test runs this class against a real server when
/// the owner asks for it.</para>
/// <para>What the SSH library throws quotes the server's name, the user and remote paths in
/// its message. No such exception leaves this class: each is turned into a
/// <see cref="RemoteStoreException"/> that carries only the kind of failure and the name of the
/// type that was thrown.</para>
/// <para>The names handed to the server are never the server's own: a file is read, written or
/// deleted only under a name that is a blob's name or an upload's temporary name by
/// <see cref="BlobCodec"/>'s rules, inside the one folder.</para>
/// <para>A call waits for the server on the calling thread. <see cref="Dispose"/> ends that
/// wait from another thread: the requests of a listing, a download, an upload, a delete and
/// the reading of the server's clock are made in the library's form that takes a cancellation
/// token, and the token is the one that <see cref="Dispose"/> cancels. The call then fails as
/// <see cref="RemoteFailure.Unreachable"/>. Two requests have no such form in the library: the
/// rename that puts an upload in its place, and the closing of a file that was downloaded.
/// They are made as before, each a short question to the server; the rename is not begun on a
/// store that is closed, and a download that ends because the store was closed does not ask
/// for its file to be closed. When <see cref="Dispose"/> comes while one of the two is already
/// under way, that one is not ended: the call waits for the server's answer, for as long as
/// <see cref="OperationTimeout"/> at most, and then fails as
/// <see cref="RemoteFailure.Unreachable"/> all the same. A download whose read ran into that
/// time-out on a server that has gone silent still asks for its file to be closed, and so can
/// wait that long a second time. The making and removing of folders, which happen when the
/// connection is opened and in the integration test, are made as before as well.</para>
/// <para>The SSH key file is opened only when it is on a drive of this machine, is reached
/// through no link and is no placeholder for a file that is kept elsewhere; all of that is
/// decided before it is opened, and without asking anything of another machine
/// (<see cref="IsOnDriveOfThisMachine"/> and the checks beside it).</para>
/// </remarks>
public sealed class SftpRemoteStore : IRemoteStore
{
    /// <summary>How long a connection may take to be made.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long the server may take to answer one request.</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    // A key file is a few kilobytes at most. A larger file is not handed to the parser.
    private const long MaxKeyFileBytes = 1024 * 1024;

    // "C:\": a letter, a colon and a separator.
    private const int DriveRootLength = 3;

    // What Windows marks a file with whose content is not on this disk, or not all of it: a
    // sync client's placeholder. "Offline", and the two marks for "recalled when it is opened"
    // (0x00040000) and "recalled when its data is read" (0x00400000), which .NET has no names
    // for. Reading such a file makes the sync client fetch it from wherever it is kept.
    private const FileAttributes NotOnThisDisk = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    // The first array a download is read into. It grows with what really arrives, so the size
    // the server claims for a blob never decides how much memory is taken.
    private const int FirstDownloadBuffer = 64 * 1024;

    // The longest name this store ever writes in the server's folder: the temporary name of an
    // upload. A name in a listing that is longer is nothing of Beacon's.
    private static readonly int LongestName = BlobCodec.TemporaryName(new string('0', BlobCodec.NameLength)).Length;

    private static int _connections;

    private readonly SftpClient _client;
    private readonly AuthenticationMethod _signIn;
    private readonly PrivateKeyFile _key;
    private readonly string _folder;
    private readonly string _blobs;

    // Cancelled by Dispose, which ends a call that is waiting for the server. The source is
    // never disposed itself. A call on another thread may hold its token for a moment longer,
    // and a token whose source is disposed can throw from inside the library. Nothing is lost
    // by leaving it: it has no timer and no wait handle of its own, so it holds nothing but
    // memory, and the registrations on it are gone once it has been cancelled.
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationToken _closed;
    private int _disposed;

    private SftpRemoteStore(SftpClient client, AuthenticationMethod signIn, PrivateKeyFile key, SyncConfig config)
    {
        _client = client;
        _signIn = signIn;
        _key = key;
        _folder = config.RemoteDir;
        _blobs = config.BlobsDir;
        _closed = _closing.Token;
    }

    /// <summary>The connector the app uses.</summary>
    public static IRemoteConnector Connector { get; } = new SftpConnector();

    /// <summary>
    /// How many times this run of Beacon has started to open a connection, for any purpose. It
    /// is counted here, in the one place a connection can be opened, so the log can say with
    /// certainty that none was.
    /// </summary>
    public static int ConnectionsOpened => Volatile.Read(ref _connections);

    /// <summary>
    /// What a listing does with one entry the server sent. Every entry counts toward
    /// <see cref="SyncLimits.MaxRemoteEntries"/>, whatever it is, so that a server that sends
    /// folders without end is stopped like one that sends files without end. Of the entries
    /// that are read, a file is listed when its name is no longer than the longest name this
    /// store ever writes, the temporary name of an upload: a longer name cannot be Beacon's.
    /// Whether a name that is listed is a blob's is for the caller to judge.
    /// </summary>
    /// <param name="entriesBefore">How many entries the listing has had before this one.</param>
    /// <param name="name">The entry's name, as the server gave it.</param>
    /// <param name="isRegularFile">False for a folder, a link and everything else that is not a file.</param>
    /// <returns>True when the entry is listed.</returns>
    /// <exception cref="RemoteStoreException">With <see cref="RemoteFailure.TooMuch"/>: this entry is one more than is read.</exception>
    public static bool ListsEntry(int entriesBefore, string? name, bool isRegularFile)
    {
        if (entriesBefore >= SyncLimits.MaxRemoteEntries)
        {
            throw new RemoteStoreException(RemoteFailure.TooMuch);
        }

        return isRegularFile && name is not null && name.Length <= LongestName;
    }

    public IReadOnlyList<RemoteBlob> List() => Do(() =>
    {
        var found = new List<RemoteBlob>();
        Wait(ReadFolder(found));
        return (IReadOnlyList<RemoteBlob>)found;
    });

    public byte[] Download(string name, long maxBytes)
    {
        string path = PathOf(name, allowTemporary: false);
        return Do(() =>
        {
            // The file as it is, for reading: what OpenRead asks for, in the form that takes a token.
            SftpFileStream stream = Wait(_client.OpenAsync(path, FileMode.Open, FileAccess.Read, _closed));
            try
            {
                maxBytes = Math.Clamp(maxBytes, 0, Array.MaxLength);
                byte[] buffer = new byte[(int)Math.Min(maxBytes, FirstDownloadBuffer)];
                int length = 0;
                while (true)
                {
                    if (length == buffer.Length)
                    {
                        if (length >= maxBytes)
                        {
                            // One more byte is asked for, to tell a blob of exactly the limit from a larger one.
                            if (Wait(stream.ReadAsync(new byte[1], 0, 1, _closed)) > 0)
                            {
                                throw new RemoteStoreException(RemoteFailure.TooMuch);
                            }

                            break;
                        }

                        Array.Resize(ref buffer, (int)Math.Min(maxBytes, buffer.Length * 2L));
                    }

                    int read = Wait(stream.ReadAsync(buffer, length, buffer.Length - length, _closed));
                    if (read <= 0)
                    {
                        break;
                    }

                    length += read;
                }

                return length == buffer.Length ? buffer : buffer.AsSpan(0, length).ToArray();
            }
            finally
            {
                // Closing the file asks the server once more and waits for its answer, and the
                // library has no form of that wait that can be ended from outside. So a store
                // that is being closed does not ask: the connection goes, and the server lets
                // go of the file with it.
                if (!_closed.IsCancellationRequested)
                {
                    stream.Dispose();
                }
            }
        }, missing: RemoteFailure.Missing);
    }

    public RemoteBlob Upload(string name, byte[] content)
    {
        string path = PathOf(name, allowTemporary: false);
        string temporary = PathOf(BlobCodec.TemporaryName(name), allowTemporary: true);
        return Do(() =>
        {
            try
            {
                using (var stream = new MemoryStream(content, writable: false))
                {
                    Wait(_client.UploadFileAsync(stream, temporary, canOverride: true, uploadProgress: null, cancellationToken: _closed));
                }

                // Before it takes the place of the blob that is there: all of it arrived.
                if (Wait(_client.GetAttributesAsync(temporary, _closed)).Size != content.Length)
                {
                    throw new RemoteStoreException(RemoteFailure.Server, "short upload");
                }

                Replace(temporary, path);
            }
            catch (Exception ex)
            {
                RemoveLeftover(temporary, ex);
                throw;
            }

            var attributes = Wait(_client.GetAttributesAsync(path, _closed));
            return new RemoteBlob(name, attributes.Size, Utc(attributes.LastWriteTimeUtc));
        });
    }

    public void Delete(string name)
    {
        string path = PathOf(name, allowTemporary: true);
        Do(() =>
        {
            try
            {
                Wait(_client.DeleteFileAsync(path, _closed));
            }
            catch (SftpPathNotFoundException)
            {
                // It is gone, which is what was asked for.
            }

            return true;
        });
    }

    public DateTimeOffset Now()
    {
        // A file of one byte under a temporary name of the blob that no path can have. If it is
        // left behind, it is removed with the other leftovers after a day.
        string probe = PathOf(BlobCodec.TemporaryName(new string('0', BlobCodec.NameLength)), allowTemporary: true);
        return Do(() =>
        {
            DateTimeOffset time;
            try
            {
                using (var one = new MemoryStream([0], writable: false))
                {
                    Wait(_client.UploadFileAsync(one, probe, canOverride: true, uploadProgress: null, cancellationToken: _closed));
                }

                time = Utc(Wait(_client.GetAttributesAsync(probe, _closed)).LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                RemoveLeftover(probe, ex);
                throw;
            }

            TryDelete(probe);
            return time;
        });
    }

    /// <summary>
    /// Removes the blobs folder and the folder it is in, when they are empty. For the
    /// integration test, which works in a folder of its own and leaves nothing behind.
    /// </summary>
    public void RemoveEmptyFolders() => Do(() =>
    {
        _client.DeleteDirectory(_blobs);
        _client.DeleteDirectory(_folder);
        return true;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // First of all: a call that is waiting for the server ends now, and not when the
        // server's time to answer has run out.
        Quietly(_closing.Cancel);
        Quietly(_client.Dispose);
        Quietly(_signIn.Dispose);
        Quietly(_key.Dispose);
    }

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));

    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Letting go of a connection that is already broken. There is nothing to add.
        }
    }

    // Waits on the calling thread for a request that was made with the token Dispose cancels.
    // Every call here comes from the sync run's own background thread, which has nothing that
    // the rest of the request would have to come back to. What the request threw is thrown
    // here as it was: when the store was closed, that is an OperationCanceledException.
    private static void Wait(Task request) => request.GetAwaiter().GetResult();

    private static T Wait<T>(Task<T> request) => request.GetAwaiter().GetResult();

    private static RemoteStoreException Closed() => new(RemoteFailure.Unreachable, "closed");

    // The path of one file in the blobs folder. Only our own two shapes of name are ever joined
    // to it: a name the server chose, or any other text, is refused here.
    private string PathOf(string name, bool allowTemporary)
    {
        if (!BlobCodec.IsBlobName(name) && !(allowTemporary && BlobCodec.IsTemporaryName(name)))
        {
            throw new ArgumentException("That is not the name of a blob.", nameof(name));
        }

        return _blobs + "/" + name;
    }

    private async Task ReadFolder(List<RemoteBlob> found)
    {
        // Read as it arrives and not as one list, so that a folder of any size stops at the limit.
        int entries = 0;
        await foreach (ISftpFile file in _client.ListDirectoryAsync(_blobs, _closed).ConfigureAwait(false))
        {
            if (ListsEntry(entries++, file.Name, file.IsRegularFile))
            {
                found.Add(new RemoteBlob(file.Name, file.Length, Utc(file.LastWriteTimeUtc)));
            }
        }
    }

    // The rename that replaces the file that is there in one step. A server that cannot do that
    // fails here, and the blob that was there stays whole. The library has no form of this
    // request that takes a token, so it is not begun on a store that is already closed.
    private void Replace(string temporary, string path)
    {
        ThrowIfClosed();
        try
        {
            _client.RenameFile(temporary, path, true);
        }
        catch (NotSupportedException)
        {
            // The library's own answer, given before anything is sent, when the server did not
            // say at the start that it offers this rename.
            throw new RemoteStoreException(RemoteFailure.Server, "no posix-rename");
        }
    }

    // After a call failed, what it wrote under a temporary name is removed, when the server can
    // still be asked: after a refusal, such as a rename the server would not do. After a
    // time-out, a lost connection or the closing of this store it is not tried. It would only
    // wait as long again, and a later run removes what was left.
    private void RemoveLeftover(string path, Exception failure)
    {
        if (AsFailure(failure).Failure != RemoteFailure.Unreachable)
        {
            TryDelete(path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            Wait(_client.DeleteFileAsync(path, _closed));
        }
        catch (Exception)
        {
            // A leftover under a temporary name is removed by a later run.
        }
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw Closed();
        }
    }

    // Whether the connection is still there. A client that is being disposed on another thread
    // can throw when it is asked, and that is an answer as well.
    private bool IsConnected()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            return _client.IsConnected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private T Do<T>(Func<T> work, RemoteFailure missing = RemoteFailure.Folder)
    {
        try
        {
            ThrowIfClosed();
            return work();
        }
        catch (Exception ex) when (ex is not RemoteStoreException)
        {
            throw AsFailure(ex, missing);
        }
    }

    // What a call that failed leaves this class as.
    private RemoteStoreException AsFailure(Exception ex, RemoteFailure missing = RemoteFailure.Folder)
    {
        if (ex is RemoteStoreException told)
        {
            return told;
        }

        // The only token the library is ever given is the one that Dispose cancels.
        if (ex is OperationCanceledException)
        {
            return Closed();
        }

        // When the connection has gone, the library does not always throw one of its exceptions
        // for that: a server that hangs up or closes the channel can come out as an exception of
        // any kind. "The server refused this one thing" is only said of a server that is there.
        var failure = Translate(ex, missing);
        return failure.Failure == RemoteFailure.Server && !IsConnected()
            ? new RemoteStoreException(RemoteFailure.Unreachable, failure.Cause)
            : failure;
    }

    // What the SSH library, or the network under it, threw, as the one exception that leaves
    // this class. The exception itself is not kept, and its message is never read.
    private static RemoteStoreException Translate(Exception ex, RemoteFailure missing = RemoteFailure.Folder)
    {
        string cause = ex.GetType().Name;
        return ex switch
        {
            // A socket's exception, told by its name so that this file needs nothing of .NET's
            // own network namespaces. Its number says what happened (10061 is "refused").
            Win32Exception socket when cause == "SocketException" => new RemoteStoreException(RemoteFailure.Unreachable, FormattableString.Invariant($"{cause} {socket.NativeErrorCode}")),
            SshOperationTimeoutException or TimeoutException => new RemoteStoreException(RemoteFailure.Unreachable, cause),
            SshAuthenticationException => new RemoteStoreException(RemoteFailure.Refused, cause),
            SshConnectionException connection => new RemoteStoreException(RemoteFailure.Unreachable, $"{cause} {connection.DisconnectReason}"),
            ProxyException or ObjectDisposedException or IOException => new RemoteStoreException(RemoteFailure.Unreachable, cause),
            SftpPathNotFoundException => new RemoteStoreException(missing, cause),
            SftpPermissionDeniedException => new RemoteStoreException(RemoteFailure.Folder, cause),
            _ => new RemoteStoreException(RemoteFailure.Server, cause),
        };
    }

    private static PrivateKeyFile ReadKey(string path, string? passphrase)
    {
        RequireFileOfThisMachine(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxKeyFileBytes)
        {
            throw new IOException("That file is too large to be a key file.");
        }

        return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
    }

    /// <summary>
    /// True when a path is on a drive letter of this machine: one that does not stand for a
    /// share. The drive is taken from the text of the path, and Windows is asked one thing,
    /// what kind of drive that letter is. Nothing of the path below the drive's root is given
    /// to Windows to find this out, so nothing on the drive is read, and no other machine is
    /// contacted for a path on a network drive.
    /// </summary>
    /// <remarks>
    /// The path is not made full first. Making a path full asks the disk for the long form of
    /// a part that holds a "~", and for a path on a network drive that is a question to the
    /// share. sync.json only ever holds a key path that begins with a letter, a colon and a
    /// separator, so the first three characters are the drive.
    /// </remarks>
    internal static bool IsOnDriveOfThisMachine(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        return root.Length == DriveRootLength
            && char.IsAsciiLetter(root[0])
            && root[1] == Path.VolumeSeparatorChar
            && FilePaths.IsSeparator(root[2])
            && new DriveInfo(root).DriveType != DriveType.Network;
    }

    // The key file is read from this machine and from nowhere else, through no link, and only
    // when its content is on the disk; and all of this is decided before it is opened. Opening
    // a file on another machine makes Windows connect to that machine by itself, and that
    // would be a connection Beacon made without ever counting it. The checks come in the order
    // that keeps each of them on this machine:
    //
    // 1. The drive, from the text of the path. Until this has passed, nothing but the three
    //    characters of the drive's root is given to Windows.
    // 2. Every folder from the drive's root down to the one the file is in, top down. A folder
    //    that is a link can lead to another machine, and Windows goes there as soon as anything
    //    below the link is asked for, the file's attributes included. Each folder is looked at
    //    only after every folder above it was found to be no link, so each look stays on this
    //    machine, and the look at a link reads the link itself and does not follow it. A path
    //    through a link that leads nowhere but to this machine is refused too: where a link
    //    leads is not asked.
    // 3. The file itself: no link, and no placeholder for a file a sync client keeps elsewhere
    //    and would fetch when it is read.
    private static void RequireFileOfThisMachine(string path)
    {
        if (!IsOnDriveOfThisMachine(path))
        {
            throw new RemoteStoreException(RemoteFailure.KeyFile, "network drive");
        }

        if (FoldersOnTheWay(path) is not { } folders)
        {
            throw new RemoteStoreException(RemoteFailure.KeyFile, "link");
        }

        foreach (string folder in folders)
        {
            switch (Look(folder, out _))
            {
                case Found.Link:
                    throw new RemoteStoreException(RemoteFailure.KeyFile, "link");

                case Found.Nothing:
                    // Nothing can be below a folder that is not there. Opening the file says so
                    // in its own way, and looks no further than this folder did.
                    return;
            }
        }

        switch (Look(path, out var attributes))
        {
            case Found.Link:
                throw new RemoteStoreException(RemoteFailure.KeyFile, "link");

            case Found.Plain when (attributes & NotOnThisDisk) != 0:
                throw new RemoteStoreException(RemoteFailure.KeyFile, "placeholder");
        }
    }

    /// <summary>
    /// The folders between a drive's root and a file on that drive, outermost first, each as a
    /// full path: what is looked at, in this order, before a key file is opened. Worked out
    /// from the text of the path as Windows works it out before it opens anything: either
    /// separator, an empty part and "." are stepped over, and ".." takes the part before it
    /// away. Nothing is asked of the disk here.
    /// </summary>
    /// <param name="path">A path that begins with a letter, a colon and a separator.</param>
    /// <returns>
    /// The folders, or null when the folder Windows would go through cannot be told with
    /// certainty from the text: such a path is refused like one through a link. That is so for
    /// a path that does not begin with a drive's root, and for one with a folder whose name
    /// ends in a period or a space, or holds a colon. Windows drops a period or a space at the
    /// end of a path and keeps it in the middle of one (but for a single period), so the folder
    /// that would be looked at by its own path is not surely the one the file is opened
    /// through; and a name with a colon is no folder's name, and joined to a path it could
    /// name another drive.
    /// </returns>
    public static IReadOnlyList<string>? FoldersOnTheWay(string path)
    {
        if (path.Length < DriveRootLength || !char.IsAsciiLetter(path[0]) || path[1] != Path.VolumeSeparatorChar || !FilePaths.IsSeparator(path[2]))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (string part in path[DriveRootLength..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        // All but the last part, which is the file. Each folder is the one before it and one
        // name more, put together as text: no part can take the place of what came before it.
        var folders = new List<string>();
        string folder = path[..2];
        for (int i = 0; i < parts.Count - 1; i++)
        {
            string name = parts[i];
            if (name[^1] is '.' or ' ' || name.Contains(Path.VolumeSeparatorChar))
            {
                return null;
            }

            folder = folder + Path.DirectorySeparatorChar + name;
            folders.Add(folder);
        }

        return folders;
    }

    private enum Found
    {
        /// <summary>A file or a folder that is no link.</summary>
        Plain,

        /// <summary>A link, or something whose state cannot be told, which counts as one.</summary>
        Link,

        /// <summary>Nothing has the name.</summary>
        Nothing,
    }

    // What is at a path, by the rule the file index and the sync scan go by: a link is a
    // reparse point that has a link target. The attributes are those of the name itself, and
    // the target is read from the link, so a link is never followed to find out. Something
    // whose state cannot be told counts as a link.
    private static Found Look(string path, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            // Asked first because it throws for a name that nothing has, which the rule itself
            // cannot tell from a link.
            attributes = File.GetAttributes(path);
            return FileScanner.IsLink(new FileInfo(path)) ? Found.Link : Found.Plain;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Found.Nothing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Found.Link;
        }
    }

    private static SftpClient NewClient(SyncConfig config, AuthenticationMethod signIn) =>
        new(new ConnectionInfo(config.Host, config.Port, config.User, signIn) { Timeout = ConnectTimeout })
        {
            OperationTimeout = OperationTimeout,
        };

    private static string AlgorithmName(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@') ? name : "";

    private sealed class SftpConnector : IRemoteConnector
    {
        public KeyFileState CheckKeyFile(string path, string? passphrase)
        {
            try
            {
                using var key = ReadKey(path, passphrase);
                return KeyFileState.Usable;
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                return KeyFileState.NeedsPassphrase;
            }
            catch (RemoteStoreException)
            {
                // Not on a drive of this machine, reached through a link, a link itself, or a
                // placeholder: it was not opened, whatever passphrase was given.
                return KeyFileState.Unreadable;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                // The file could not be opened or read at all.
                return KeyFileState.Unreadable;
            }
            catch (Exception)
            {
                // Whatever the parser said. With a passphrase given, the likeliest cause is that it is wrong.
                return string.IsNullOrEmpty(passphrase) ? KeyFileState.Unreadable : KeyFileState.WrongPassphrase;
            }
        }

        public HostKeyInfo ReadHostKey(SyncConfig config)
        {
            HostKeyInfo? shown = null;
            AuthenticationMethod? signIn = null;
            SftpClient? client = null;
            try
            {
                // Everything the library is given is made in here, so that whatever it throws
                // at any step leaves as this class's own exception and nothing is left open.
                signIn = new NoneAuthenticationMethod(config.User);
                client = NewClient(config, signIn);
                client.HostKeyReceived += (_, e) =>
                {
                    shown = new HostKeyInfo(HostKeyFingerprint.Of(e.HostKey), AlgorithmName(e.HostKeyName));

                    // Never trusted here: the connection ends before any sign-in is tried.
                    e.CanTrust = false;
                };

                Interlocked.Increment(ref _connections);
                client.Connect();
            }
            catch (Exception ex)
            {
                // Refusing the key ends the connection with an exception, which is the way this
                // is meant to end. Only without a key shown is it a failure.
                if (shown is null)
                {
                    throw Translate(ex);
                }
            }
            finally
            {
                Quietly(() => client?.Dispose());
                Quietly(() => signIn?.Dispose());
            }

            return shown ?? throw new RemoteStoreException(RemoteFailure.Server, "no host key");
        }

        public IRemoteStore Open(SyncConfig config, string? passphrase)
        {
            // Nothing pinned, or something that is not a fingerprint: no connection at all.
            if (!HostKeyFingerprint.IsValid(config.HostKeyFingerprint))
            {
                throw new RemoteStoreException(RemoteFailure.HostKeyChanged, "nothing pinned");
            }

            PrivateKeyFile key;
            try
            {
                key = ReadKey(config.PrivateKeyPath, passphrase);
            }
            catch (Exception ex) when (ex is not RemoteStoreException)
            {
                throw new RemoteStoreException(RemoteFailure.KeyFile, ex.GetType().Name);
            }

            AuthenticationMethod? signIn = null;
            SftpClient? client = null;
            bool otherKey = false;
            try
            {
                signIn = new PrivateKeyAuthenticationMethod(config.User, key);
                client = NewClient(config, signIn);
                client.HostKeyReceived += (_, e) =>
                {
                    bool pinned = HostKeyFingerprint.Matches(config.HostKeyFingerprint, e.HostKey);
                    otherKey = !pinned;
                    e.CanTrust = pinned;
                };

                Interlocked.Increment(ref _connections);
                client.Connect();

                // From here the store holds all three, and lets go of them when it is disposed.
                var store = new SftpRemoteStore(client, signIn, key, config);
                store.MakeFolders();
                return store;
            }
            catch (Exception ex)
            {
                Quietly(() => client?.Dispose());
                Quietly(() => signIn?.Dispose());
                Quietly(key.Dispose);
                throw otherKey
                    ? new RemoteStoreException(RemoteFailure.HostKeyChanged)
                    : ex as RemoteStoreException ?? Translate(ex);
            }
        }
    }

    // The folder and the blobs folder inside it are made when they are not there. A folder
    // whose parent is not there is not made: the owner names a place that exists.
    private void MakeFolders() => Do(() =>
    {
        foreach (string folder in new[] { _folder, _blobs })
        {
            if (!_client.Exists(folder))
            {
                _client.CreateDirectory(folder);
            }
        }

        return true;
    });
}
