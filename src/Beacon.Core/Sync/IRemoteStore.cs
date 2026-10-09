namespace Beacon.Core.Sync;

/// <summary>One file in the server's folder, as the server describes it. All of it is the server's word and none of it is trusted.</summary>
/// <param name="Name">The file's name. Whether it is the name of a blob is for the caller to judge; it never goes to the log unless it is one.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="ModifiedUtc">When it was last written, by the server's clock, in whole seconds.</param>
public sealed record RemoteBlob(string Name, long Size, DateTimeOffset ModifiedUtc)
{
    /// <summary>The type's name and the size, and not the name, which the server chose.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(RemoteBlob)} {{ Size = {Size} }}");
}

/// <summary>Why the server could not be used. Each is told to the owner in fixed words of its own.</summary>
public enum RemoteFailure
{
    /// <summary>No connection could be made, it was lost, or the server did not answer in time. Tried again later.</summary>
    Unreachable,

    /// <summary>The server's host key is not the one that was pinned, or nothing is pinned. Sync stops until the owner decides.</summary>
    HostKeyChanged,

    /// <summary>The server refused the sign-in.</summary>
    Refused,

    /// <summary>The SSH key file cannot be read, is not a key, or its passphrase is missing or wrong.</summary>
    KeyFile,

    /// <summary>The folder on the server cannot be made, listed or written.</summary>
    Folder,

    /// <summary>The server's folder holds more files than Beacon reads, or a blob is larger than any blob Beacon makes.</summary>
    TooMuch,

    /// <summary>The blob that was asked for is not there.</summary>
    Missing,

    /// <summary>Any other answer of the server that is not what was asked for.</summary>
    Server,
}

/// <summary>
/// The one exception a remote store throws. Its message is fixed words, and it carries no inner
/// exception: what an SSH library throws quotes host names and paths in its message, and by
/// not keeping that exception at all nothing can ever format it. What is kept of the cause is
/// its type's name, which is safe to log.
/// </summary>
public sealed class RemoteStoreException : Exception
{
    public RemoteStoreException(RemoteFailure failure, string? cause = null)
        : base("The sync server could not be used.")
    {
        Failure = failure;
        Cause = cause;
    }

    public RemoteFailure Failure { get; }

    /// <summary>The name of the type that was thrown underneath, with at most a fixed word of detail, or null. Never a message.</summary>
    public string? Cause { get; }
}

/// <summary>
/// The server's folder of blobs: list, download, upload, delete. One implementation talks SFTP,
/// one lives in memory for the tests. Every method works on the calling thread until it is
/// done, so it is only ever called from the sync run's background thread, and every failure is
/// a <see cref="RemoteStoreException"/>. <see cref="IDisposable.Dispose"/> closes the connection
/// and may be called from another thread to stop a call that is under way. In most cases that
/// call then ends at once, without waiting for the server any longer. Not in all: a step that
/// cannot be broken off and was already under way is waited for, for as long as the server is
/// given to answer one request at most. For the SFTP store those steps are the rename that
/// puts an upload in its place and the closing of a file that was downloaded, and the time is
/// <see cref="SftpRemoteStore.OperationTimeout"/>. Either way the call fails with
/// <see cref="RemoteFailure.Unreachable"/>, as does every call that is made after it.
/// </summary>
public interface IRemoteStore : IDisposable
{
    /// <summary>
    /// The files in the folder, in no particular order: a server lists a folder as it finds it,
    /// and a caller must not count on the names coming sorted. Folders and links are left out,
    /// and a file whose name is longer than any name Beacon gives may be left out as well. At
    /// most <see cref="SyncLimits.MaxRemoteEntries"/> entries are read, of whatever kind: a
    /// folder that holds more is not read to its end, and the call fails with
    /// <see cref="RemoteFailure.TooMuch"/>.
    /// </summary>
    IReadOnlyList<RemoteBlob> List();

    /// <summary>The bytes stored under a name.</summary>
    /// <param name="name">A blob's name. Anything else is refused before the server is asked.</param>
    /// <param name="maxBytes">The most that is read. A blob that turns out larger fails with <see cref="RemoteFailure.TooMuch"/>, whatever size the server gave for it.</param>
    byte[] Download(string name, long maxBytes);

    /// <summary>
    /// Stores bytes under a name, replacing what was there in one step: they are written under a
    /// temporary name and renamed, so that nobody ever reads half a blob.
    /// </summary>
    /// <returns>The blob as the server now describes it.</returns>
    RemoteBlob Upload(string name, byte[] content);

    /// <summary>Deletes what is stored under a blob's name or a temporary name. A name that is not there is not an error.</summary>
    void Delete(string name);

    /// <summary>
    /// The time by the server's clock, in whole seconds: the time it gives a file that was
    /// written this moment. It is what two machines have in common to hold their own clocks against.
    /// </summary>
    DateTimeOffset Now();
}

/// <summary>What the server showed as its host key.</summary>
/// <param name="Fingerprint">The key's fingerprint as <see cref="HostKeyFingerprint.Of"/> gives it.</param>
/// <param name="Algorithm">The kind of key, such as <c>ssh-ed25519</c>, or empty when it is not one of the plain names.</param>
public sealed record HostKeyInfo(string Fingerprint, string Algorithm)
{
    /// <summary>The type's name and nothing else: a fingerprint names the owner's server.</summary>
    public override string ToString() => nameof(HostKeyInfo);
}

/// <summary>How an SSH key file was found. Nothing is sent anywhere to find out.</summary>
public enum KeyFileState
{
    /// <summary>It is a private key that can be used as it is, or with the passphrase that was given.</summary>
    Usable,

    /// <summary>It is protected by a passphrase and none was given.</summary>
    NeedsPassphrase,

    /// <summary>A passphrase was given and the key could not be opened with it.</summary>
    WrongPassphrase,

    /// <summary>It cannot be read, or it is not a private key.</summary>
    Unreadable,
}

/// <summary>
/// Makes connections. Everything that can reach a server goes through this, so a test passes
/// its own and no test ever opens a connection.
/// </summary>
public interface IRemoteConnector
{
    /// <summary>
    /// Reads the key file on this machine and says whether it can be used. A file that is not on
    /// a drive of this machine, that is a link or is reached through a folder that is one, or
    /// that is a placeholder for a file kept elsewhere, is not opened at all and counts as
    /// <see cref="KeyFileState.Unreadable"/>: reading it could make Windows connect to another
    /// machine, or make a sync client fetch it.
    /// </summary>
    KeyFileState CheckKeyFile(string path, string? passphrase);

    /// <summary>
    /// Connects far enough to be shown the server's host key and hangs up there, before any
    /// sign-in: nothing about the owner's key reaches a server that has not been confirmed.
    /// </summary>
    /// <exception cref="RemoteStoreException">The server could not be reached.</exception>
    HostKeyInfo ReadHostKey(SyncConfig config);

    /// <summary>
    /// Connects and signs in. The server's host key must be the one pinned in
    /// <paramref name="config"/>; with any other key, and with none pinned, this fails with
    /// <see cref="RemoteFailure.HostKeyChanged"/> before the sign-in.
    /// </summary>
    /// <exception cref="RemoteStoreException">Why there is no connection.</exception>
    IRemoteStore Open(SyncConfig config, string? passphrase);
}
