using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Beacon.Core.Sync;

/// <summary>What the record of the last sync says about one path.</summary>
public enum SyncedKind
{
    /// <summary>The file was the same here and on the server.</summary>
    Live,

    /// <summary>The file is deleted, and the server holds the note of that (a tombstone).</summary>
    Tombstone,

    /// <summary>
    /// The server holds a blob for this path that this machine does not keep: an expired day of
    /// another machine's clipboard history, or a blob whose path this machine could not look at
    /// when it opened the blob (behind a link, at a file that is too large, at a key that two
    /// files share). It is remembered so that it is not downloaded again only to be found
    /// expired, or only to be left alone again. Once such a path can be looked at, a record of
    /// this kind counts as no record.
    /// </summary>
    Ignored,
}

/// <summary>One path as it stood when it was last synced.</summary>
/// <param name="Path">The path as spelt, with forward slashes.</param>
/// <param name="Blob">The name of its blob on the server.</param>
/// <param name="Sha256">The hash of the file's content. Empty unless <paramref name="Kind"/> is <see cref="SyncedKind.Live"/>.</param>
/// <param name="Size">The local file's size then. Zero unless live.</param>
/// <param name="MtimeUtc">For a live file, when the local file was last written; for a tombstone, when the deletion was noticed.</param>
/// <param name="RemoteSize">The blob's size on the server then.</param>
/// <param name="RemoteMtimeUtc">The time the server gave the blob then.</param>
public sealed record SyncedFile(string Path, string Blob, SyncedKind Kind, string Sha256, long Size, DateTimeOffset MtimeUtc, long RemoteSize, DateTimeOffset RemoteMtimeUtc)
{
    /// <summary>The type's name and the kind, and never the path.</summary>
    public override string ToString() => $"{nameof(SyncedFile)} {{ Kind = {Kind} }}";
}

/// <summary>How the record of the last sync was found.</summary>
public enum SyncStateLoad
{
    /// <summary>It was read.</summary>
    Loaded,

    /// <summary>There is none: this is the first sync.</summary>
    Missing,

    /// <summary>It was made against another server, folder, sync key or data folder, so it says nothing about this one.</summary>
    OtherScope,

    /// <summary>It cannot be read or understood.</summary>
    Unreadable,
}

/// <summary>
/// The record of the last sync (sync-state.json): for each path the blob it has, the content
/// it had, and how the server described the blob. A run compares it with what is here now and
/// with what the server lists now, and that is how it tells an edit from a deletion and a file
/// that is new here from one that was deleted there.
/// </summary>
/// <remarks>
/// <para>Losing this record loses nothing of the owner's. Without it every file is compared by
/// its content: what is the same on both sides is recorded again, what differs is kept on both
/// sides (one of them as a conflict copy), and nothing is deleted on the strength of a record
/// that is not there. So a record that is missing, unreadable, or made for another scope is
/// simply not used, and the log says so.</para>
/// <para>The scope ties a record to the server, the folder on it, the sync key, the data
/// folder's path, and the id that the data folder carries in a file of its own
/// (<see cref="SyncFolderId"/>). A data folder that was moved, or a server that was changed,
/// starts with an empty record instead of taking every file for deleted. So does a data folder
/// at the same path that was emptied, or that was replaced by an empty folder or by another
/// data folder: it has no id, or another one, and the record is not used against it.</para>
/// <para>What the id does not cover is a data folder that was put back from a backup which
/// holds the id file. That folder has the same id as the folder the record was made for. The
/// scope is then the same, and the newer record is used against the older files: a file that
/// was made after the backup counts as deleted here, and a file that was edited after it
/// counts as edited here once more, so that its older content is sent over the newer. Nothing
/// in the record or in the folder tells that from the owner's own deletes and edits: the id
/// still does not tell such a folder from the one the record was made for, and this record
/// does not try to. What holds it, in part, is the brake on mass deletion
/// (<see cref="SyncEnd.Held"/>): when the folder misses many of the library files that were
/// synced, or all of them, the run sends nothing and sync is paused, and Restore Missing Files
/// then fetches what is missing and keeps both versions of every file that differs. Not
/// guarded: a folder that misses too few files to begin a pause, whose missing files go as
/// deletions and whose older versions go up as edits; and this machine's own clipboard
/// folders, which the brake does not count. So the advice to the owner stands: after putting
/// a data folder back, run Restore Missing Files first, or delete the id file in it, or this
/// record, before Beacon runs, so that every file is compared afresh by its content.</para>
/// <para>A path is entered here only after its file has been synced. The record is written to
/// a temporary file that then takes the file's place in one step.</para>
/// </remarks>
public sealed class SyncState
{
    /// <summary>
    /// How large the record's file may be and still be read: 512 MB. A larger file is not a
    /// record this code wrote for any data folder it would sync, and is not read into memory.
    /// </summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    private const int Version = 1;
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string TemporarySuffix = ".tmp";

    private static readonly JsonWriterOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, SyncedFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteBlob> _bad = new(StringComparer.Ordinal);

    /// <summary>An empty record for a scope.</summary>
    public SyncState(string scope)
    {
        Scope = scope;
    }

    /// <summary>What the record was made for; see <see cref="ScopeOf"/>.</summary>
    public string Scope { get; }

    /// <summary>True when something has changed since the record was read or last written.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Every path in the record, by its key (<see cref="SyncPath.Key"/>).</summary>
    public IReadOnlyDictionary<string, SyncedFile> Files => _files;

    /// <summary>
    /// The blobs that failed authentication or could not be used, as the server described them
    /// then. One that is still the same is not downloaded again, nothing is uploaded over it,
    /// and sync stays flagged for it.
    /// </summary>
    /// <remarks>
    /// This is kept beside the entries of the paths, not in place of them: a blob named here
    /// may be the blob of a path that has an entry in <see cref="Files"/>, which still says how
    /// that file stood when it was last synced. Entering a path (<see cref="Set"/>) does not
    /// take its blob out of here. A blob goes from here only by <see cref="RemoveBad"/>, which
    /// the run calls once the server no longer lists it, or lists it differently.
    /// </remarks>
    public IReadOnlyDictionary<string, RemoteBlob> Bad => _bad;

    /// <summary>
    /// Sixty-four hex digits that are the same for the same server, folder, user, sync key,
    /// data folder and id of that data folder, and name none of them.
    /// </summary>
    /// <param name="remoteId">What <see cref="SyncConfig.RemoteId"/> gives.</param>
    /// <param name="folderId">
    /// What <see cref="SyncFolderId.TryGet"/> gives: the id the data folder carries in a file of
    /// its own. The path alone says nothing of the folder as it stands on the disk. A folder of
    /// the same path that was emptied, or that was replaced by an empty folder or by another
    /// data folder (Beacon's own files copied to another machine without the data, say), has
    /// no id or another one, so the record of the folder that was there is not used against it
    /// and nothing in it is taken for deleted. The id does not tell a folder that was put back
    /// from a backup which holds the id file: that folder has the same id, the scope is the
    /// same, and the record is used against it (see the remarks on <see cref="SyncState"/>).
    /// </param>
    public static string ScopeOf(string remoteId, string dataRoot, string folderId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        remoteId + "\n" + System.IO.Path.TrimEndingDirectorySeparator(dataRoot).ToUpperInvariant() + "\n" + folderId)));

    /// <summary>The entry of a path, or null.</summary>
    public SyncedFile? Find(string key) => _files.GetValueOrDefault(key);

    /// <summary>
    /// Enters a path, in place of what the record said about it. What <see cref="Bad"/> holds
    /// about its blob is left as it is.
    /// </summary>
    public void Set(SyncedFile file)
    {
        string key = SyncPath.Key(file.Path);
        if (_files.TryGetValue(key, out var before) && before == file)
        {
            return;
        }

        _files[key] = file;
        Dirty = true;
    }

    /// <summary>Takes a path out of the record.</summary>
    public void Remove(string key)
    {
        if (_files.Remove(key))
        {
            Dirty = true;
        }
    }

    /// <summary>Remembers a blob that could not be used, as the server describes it now.</summary>
    public void SetBad(RemoteBlob blob)
    {
        if (!_bad.TryGetValue(blob.Name, out var before) || before != blob)
        {
            _bad[blob.Name] = blob;
            Dirty = true;
        }
    }

    /// <summary>Forgets a blob that could not be used: it is gone from the server, or has been replaced.</summary>
    public void RemoveBad(string blob)
    {
        if (_bad.Remove(blob))
        {
            Dirty = true;
        }
    }

    /// <summary>
    /// Reads the record for this scope. Never throws: whatever cannot be used gives an empty
    /// record, and <paramref name="how"/> says which kind of nothing it was. An entry that
    /// cannot be understood is left out and the rest is used.
    /// </summary>
    /// <param name="skipped">How many entries were left out.</param>
    /// <param name="maxBytes">
    /// How large the file may be. A file that is larger is not read and counts as one that
    /// cannot be read. Left out everywhere except in tests: <see cref="MaxFileBytes"/>.
    /// </param>
    public static SyncState Load(string file, string scope, out SyncStateLoad how, out int skipped, long maxBytes = MaxFileBytes)
    {
        var state = new SyncState(scope);
        skipped = 0;
        byte[] content;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes)
            {
                how = SyncStateLoad.Unreadable;
                return state;
            }

            content = new byte[stream.Length];
            stream.ReadExactly(content);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            how = SyncStateLoad.Missing;
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            how = SyncStateLoad.Unreadable;
            return state;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out int number) || number != Version
                || !root.TryGetProperty("scope", out var written) || written.ValueKind != JsonValueKind.String)
            {
                how = SyncStateLoad.Unreadable;
                return state;
            }

            if (written.GetString() != scope)
            {
                how = SyncStateLoad.OtherScope;
                return state;
            }

            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                // A path stands for one blob and a blob for one path. Of two entries with the
                // same path, or with the same blob, the first counts and the other is left out.
                var blobs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in files.EnumerateArray())
                {
                    if (ReadFile(entry) is { } read && !state._files.ContainsKey(SyncPath.Key(read.Path)) && blobs.Add(read.Blob))
                    {
                        state.Set(read);
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            if (root.TryGetProperty("bad", out var bad) && bad.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in bad.EnumerateArray())
                {
                    // Kept also when an entry above names the same blob: that a path was synced
                    // once does not make the blob it has now one that can be used.
                    if (ReadBad(entry) is { } read)
                    {
                        state._bad[read.Name] = read;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            state.Dirty = false;
            how = SyncStateLoad.Loaded;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            how = SyncStateLoad.Unreadable;
            return new SyncState(scope);
        }
    }

    /// <summary>
    /// Writes the record: to a temporary file beside it, which then takes its place in one
    /// step. A failure leaves the file that was there, and the record stays <see cref="Dirty"/>.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public void Save(string file)
    {
        string? folder = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = file + TemporarySuffix;
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16))
        {
            using (var writer = new Utf8JsonWriter(stream, WriteOptions))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Version);
                writer.WriteString("scope", Scope);
                writer.WriteStartArray("files");
                foreach (var synced in _files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", synced.Path);
                    writer.WriteString("blob", synced.Blob);
                    writer.WriteString("kind", KindName(synced.Kind));
                    writer.WriteString("sha256", synced.Sha256);
                    writer.WriteNumber("size", synced.Size);
                    writer.WriteString("mtimeUtc", Time(synced.MtimeUtc));
                    writer.WriteNumber("remoteSize", synced.RemoteSize);
                    writer.WriteString("remoteMtimeUtc", Time(synced.RemoteMtimeUtc));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("bad");
                foreach (var blob in _bad.Values.OrderBy(blob => blob.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("blob", blob.Name);
                    writer.WriteNumber("size", blob.Size);
                    writer.WriteString("mtimeUtc", Time(blob.ModifiedUtc));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, file, overwrite: true);
        Dirty = false;
    }

    private static string KindName(SyncedKind kind) => kind switch
    {
        SyncedKind.Tombstone => "tombstone",
        SyncedKind.Ignored => "ignored",
        _ => "live",
    };

    private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static bool TryTime(JsonElement entry, string name, out DateTimeOffset time)
    {
        time = default;
        return entry.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(value.GetString(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
    }

    private static bool TrySize(JsonElement entry, string name, out long size)
    {
        size = 0;
        return entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out size) && size >= 0;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // One entry, or null when it is not one this code wrote: the record is a file on disk, and
    // a path from it is joined to the data folder like any other.
    private static SyncedFile? ReadFile(JsonElement entry)
    {
        SyncedKind? kind = Text(entry, "kind") switch
        {
            "live" => SyncedKind.Live,
            "tombstone" => SyncedKind.Tombstone,
            "ignored" => SyncedKind.Ignored,
            _ => null,
        };
        if (kind is null
            || !SyncPath.TryParse(Text(entry, "path"), out string path)
            || !SyncPath.IsSynced(path)
            || Text(entry, "blob") is not { } blob || !BlobCodec.IsBlobName(blob)
            || Text(entry, "sha256") is not { } sha256
            || !(kind == SyncedKind.Live ? BlobCodec.IsBlobName(sha256) : sha256.Length == 0)
            || !TrySize(entry, "size", out long size)
            || !TryTime(entry, "mtimeUtc", out var mtime)
            || !TrySize(entry, "remoteSize", out long remoteSize)
            || !TryTime(entry, "remoteMtimeUtc", out var remoteMtime))
        {
            return null;
        }

        return new SyncedFile(path, blob, kind.Value, sha256, size, mtime, remoteSize, remoteMtime);
    }

    private static RemoteBlob? ReadBad(JsonElement entry) =>
        Text(entry, "blob") is { } blob && BlobCodec.IsBlobName(blob) && TrySize(entry, "size", out long size) && TryTime(entry, "mtimeUtc", out var mtime)
            ? new RemoteBlob(blob, size, mtime)
            : null;
}
