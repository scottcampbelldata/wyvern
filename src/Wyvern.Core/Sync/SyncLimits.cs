namespace Wyvern.Core.Sync;

/// <summary>
/// How much sync reads, keeps and trusts. What comes from the server is not trusted: every
/// size it gives is held against one of these before anything is read into memory.
/// </summary>
public static class SyncLimits
{
    /// <summary>A file larger than this is not synced. The log counts such files.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The largest header a blob may have. A header holds one path and three short values, so
    /// this is many times what any real one needs.
    /// </summary>
    public const int MaxHeaderBytes = 16 * 1024;

    /// <summary>The largest blob that is ever downloaded: the largest file, its header and what the blob format adds.</summary>
    public const long MaxBlobBytes = MaxFileBytes + MaxHeaderBytes + BlobCodec.Overhead + BlobCodec.HeaderLengthBytes;

    /// <summary>The longest relative path that is synced, in UTF-16 characters.</summary>
    public const int MaxPathLength = 400;

    /// <summary>The longest single name in a path. Windows allows no longer one.</summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// The most entries of the server's folder that are read. A heavy year of clipboard history
    /// from two machines is a fifth of this; a listing that is longer is not ours.
    /// </summary>
    public const int MaxRemoteEntries = 250_000;

    /// <summary>How long the note of a deleted library file stays on the server, in days.</summary>
    public const int TombstoneDays = 180;

    /// <summary>How old a temporary blob that an upload left behind has to be before it is deleted.</summary>
    public static readonly TimeSpan TemporaryBlobAge = TimeSpan.FromDays(1);

    /// <summary>How far this machine's clock and the server's may be apart before nothing is written to the server.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromDays(1);

    // The brake on mass deletion. A file that was synced and is no longer in the data folder is
    // taken for one the owner deleted, and its deletion goes to his other machine. That is
    // wrong when the files went some other way: the library folder was deleted or moved by
    // mistake, a cleanup tool removed files, or the data folder was put back from an older
    // backup. So a run that would send deletions for many library files at once sends nothing
    // and changes nothing, and waits for the owner to say which it is. The four numbers below
    // say what "many" is (SyncPlanner.IsMassDeletion). They decide only whether a pause
    // begins: once sync is paused, a run is held for the deletion of a single library file,
    // until the owner has answered or every missing file is back. Deletions in this machine's
    // own clipboard folders are not counted: those come from Delete and Clear in Wyvern itself.

    /// <summary>
    /// More deletions of library files than this in one run are held back, however many
    /// library files there are.
    /// </summary>
    public const int MassDeletionCount = 20;

    /// <summary>
    /// Fewer deletions of library files than this in one run do not begin a pause by their
    /// share of the library: deleting a few files is what deleting means.
    /// </summary>
    public const int MassDeletionFloor = 5;

    /// <summary>
    /// From <see cref="MassDeletionFloor"/> deletions on, a run is held back when it would
    /// delete more than one in this many of the library files that the record of the last sync
    /// holds as live and the run could look at: more than a quarter of them. Files it could
    /// not look at, behind a folder that could not be listed, are left out of the count: they
    /// were not seen to be here.
    /// </summary>
    public const int MassDeletionShare = 4;

    /// <summary>
    /// A library of at least this many files that is gone altogether is held back, however
    /// few they are: when every library file the run could look at is missing, it is the
    /// folder that went, and not files that were deleted one by one. Below this there is no
    /// telling: deleting the only file there is, is deleting a file.
    /// </summary>
    public const int MassDeletionWholeLibrary = 2;
}
