using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Beacon.Core.Sync;

/// <summary>What a blob says about the file it holds. It is encrypted with the file.</summary>
/// <param name="Path">The file's path under the data folder, with forward slashes, as it was spelt.</param>
/// <param name="MtimeUtc">When the file was last written, or for a deleted file when that was noticed.</param>
/// <param name="Deleted">True for a tombstone: the note that the file was deleted. A tombstone holds no content.</param>
/// <param name="Sha256">The SHA-256 of the file's bytes in lower-case hex. Empty for a tombstone.</param>
public sealed record BlobHeader(string Path, DateTimeOffset MtimeUtc, bool Deleted, string Sha256)
{
    /// <summary>The type's name and whether it is a tombstone, and never the path.</summary>
    public override string ToString() => $"{nameof(BlobHeader)} {{ Deleted = {Deleted} }}";
}

/// <summary>Why a blob could not be opened.</summary>
public enum BlobProblem
{
    /// <summary>It was opened.</summary>
    None,

    /// <summary>It is too short to be a blob, or does not start with the four letters every blob starts with.</summary>
    NotABlob,

    /// <summary>
    /// It does not authenticate: it was made with another key, it was changed, or it is stored
    /// under a name it was not made for.
    /// </summary>
    NotAuthentic,

    /// <summary>It authenticates, and what it holds is not what this code writes: a header that cannot be read, a path that is not safe, content that does not match its hash.</summary>
    BadContent,
}

/// <summary>A blob that was opened: its header, and the file's bytes.</summary>
public sealed class OpenedBlob(BlobHeader header, ReadOnlyMemory<byte> content)
{
    public BlobHeader Header { get; } = header;

    /// <summary>The file's bytes. They are a part of the array the blob was in, which <see cref="BlobCodec.Open"/> decrypted in place.</summary>
    public ReadOnlyMemory<byte> Content { get; } = content;

    /// <summary>The type's name and the length of the content, and nothing of either.</summary>
    public override string ToString() => FormattableString.Invariant($"{nameof(OpenedBlob)} {{ Bytes = {Content.Length} }}");
}

/// <summary>
/// The one place that makes and opens blobs, which are what the server holds. A blob is the
/// four letters <c>BCN1</c>, a nonce of 12 random bytes, the ciphertext, and a tag of 16 bytes
/// (AES-256-GCM). The blob's name is the associated data, so a blob that is moved to another
/// name no longer authenticates. What is encrypted is a header length (4 bytes, little endian),
/// the header as UTF-8 JSON (<c>path</c>, <c>mtimeUtc</c>, <c>deleted</c>, <c>sha256</c>, in
/// that order and without white space), and then the file's bytes.
/// </summary>
public static class BlobCodec
{
    /// <summary>The length of a blob's name: 64 hex digits.</summary>
    public const int NameLength = 64;

    /// <summary>The length of the nonce, in bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>The length of the tag, in bytes.</summary>
    public const int TagLength = 16;

    /// <summary>How many bytes say how long the header is.</summary>
    public const int HeaderLengthBytes = 4;

    /// <summary>What the format adds around the ciphertext: the four letters, the nonce and the tag.</summary>
    public const int Overhead = MagicLength + NonceLength + TagLength;

    private const int MagicLength = 4;
    private const int HashLength = 64;
    private const string TemporaryMark = ".tmp-";
    private const int TemporaryRandomLength = 16;
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly JsonWriterOptions HeaderOptions = new()
    {
        // The header is UTF-8, as the format says: a path is written as it is, not as escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The four letters are not authenticated: they are outside the ciphertext, and the tag does
    // not cover them. A format that comes after this one (BCN2) must therefore take HKDF info
    // strings of its own for its keys, and never the ones of BCN1, so that a blob of one format
    // cannot be passed off as one of the other by changing these four letters.
    private static ReadOnlySpan<byte> Magic => "BCN1"u8;

    /// <summary>True when the text is the name of a blob: exactly 64 lower-case hex digits.</summary>
    public static bool IsBlobName(string? name) => name is { Length: NameLength } && IsLowerHex(name);

    /// <summary>
    /// True when the text is the name an upload uses until it is finished: a blob's name,
    /// <c>.tmp-</c>, and 16 lower-case hex digits.
    /// </summary>
    public static bool IsTemporaryName(string? name) =>
        name is not null
        && name.Length == NameLength + TemporaryMark.Length + TemporaryRandomLength
        && name.AsSpan(NameLength, TemporaryMark.Length).SequenceEqual(TemporaryMark)
        && IsLowerHex(name.AsSpan(0, NameLength))
        && IsLowerHex(name.AsSpan(NameLength + TemporaryMark.Length));

    /// <summary>A new temporary name for an upload of this blob.</summary>
    public static string TemporaryName(string blobName) =>
        blobName + TemporaryMark + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TemporaryRandomLength / 2));

    /// <summary>The SHA-256 of these bytes in lower-case hex, as a header carries it.</summary>
    public static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Makes the blob for one file, or for a tombstone. Two blobs of the same file differ,
    /// because each has a nonce of its own: 12 fresh bytes from the system's random number
    /// generator, every time.
    /// </summary>
    /// <param name="blobName">The name the blob will be stored under. It is authenticated with the blob.</param>
    /// <param name="content">The file's bytes. Empty for a tombstone.</param>
    /// <exception cref="ArgumentException">The name is not a blob's name, a tombstone was given content, or the header would be larger than <see cref="SyncLimits.MaxHeaderBytes"/>.</exception>
    public static byte[] Seal(SyncKeys keys, string blobName, BlobHeader header, ReadOnlySpan<byte> content) =>
        SealCore(keys, blobName, header, content, []);

    /// <summary>
    /// Makes a blob under a nonce that the caller gives. This exists for tests that compare a
    /// blob with bytes worked out by another implementation, and for nothing else: under one key a
    /// nonce may be used once, and when it is used twice anyone who holds both blobs can read
    /// what they hold and forge others. Nothing that makes a blob for the server may call this,
    /// and a test of the source sees to it that no file but this one names it.
    /// </summary>
    /// <param name="nonce">Exactly <see cref="NonceLength"/> bytes. They are copied.</param>
    /// <exception cref="ArgumentException">The nonce is not 12 bytes, or any of what <see cref="Seal"/> refuses.</exception>
    public static byte[] SealWithKnownNonce(SyncKeys keys, string blobName, BlobHeader header, ReadOnlySpan<byte> content, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength)
        {
            throw new ArgumentException("A nonce is 12 bytes.", nameof(nonce));
        }

        return SealCore(keys, blobName, header, content, nonce);
    }

    // The one way a blob is made. An empty nonce stands for a fresh one; SealWithKnownNonce passes
    // nothing but 12 bytes, so no other length reaches here.
    private static byte[] SealCore(SyncKeys keys, string blobName, BlobHeader header, ReadOnlySpan<byte> content, ReadOnlySpan<byte> knownNonce)
    {
        if (!IsBlobName(blobName))
        {
            throw new ArgumentException("That is not the name of a blob.", nameof(blobName));
        }

        if (header.Deleted && content.Length > 0)
        {
            throw new ArgumentException("A tombstone holds no content.", nameof(content));
        }

        byte[] headerBytes = WriteHeader(header);
        if (headerBytes.Length > SyncLimits.MaxHeaderBytes)
        {
            throw new ArgumentException("The header is too large.", nameof(header));
        }

        int plainLength = HeaderLengthBytes + headerBytes.Length + content.Length;
        byte[] blob = new byte[Overhead + plainLength];
        Magic.CopyTo(blob);
        var nonce = blob.AsSpan(MagicLength, NonceLength);
        if (knownNonce.IsEmpty)
        {
            RandomNumberGenerator.Fill(nonce);
        }
        else
        {
            knownNonce.CopyTo(nonce);
        }

        // The plain bytes are put where the ciphertext will be and encrypted where they lie, so
        // that a large file is held once more and not twice.
        var plain = blob.AsSpan(MagicLength + NonceLength, plainLength);
        BinaryPrimitives.WriteUInt32LittleEndian(plain, (uint)headerBytes.Length);
        headerBytes.CopyTo(plain[HeaderLengthBytes..]);
        content.CopyTo(plain[(HeaderLengthBytes + headerBytes.Length)..]);

        using var aes = new AesGcm(keys.Content, TagLength);
        aes.Encrypt(nonce, plain, plain, blob.AsSpan(blob.Length - TagLength), Encoding.ASCII.GetBytes(blobName));
        return blob;
    }

    /// <summary>
    /// Opens a blob that was downloaded under this name. Nothing about the blob is trusted
    /// until it has authenticated, and what it then holds is still checked: the header's
    /// length, its four values, that its path is a safe one that sync would have uploaded and
    /// that this blob's name was made from, and that the content matches its hash.
    /// </summary>
    /// <param name="blob">The downloaded bytes. They are decrypted in place: the array is not the blob afterwards.</param>
    /// <returns>The blob, or null when it cannot be used; <paramref name="problem"/> then says why.</returns>
    public static OpenedBlob? Open(SyncKeys keys, string blobName, byte[] blob, out BlobProblem problem)
    {
        if (!IsBlobName(blobName)
            || blob.Length < Overhead + HeaderLengthBytes
            || !blob.AsSpan(0, MagicLength).SequenceEqual(Magic))
        {
            problem = BlobProblem.NotABlob;
            return null;
        }

        int plainLength = blob.Length - Overhead;
        var plain = blob.AsSpan(MagicLength + NonceLength, plainLength);
        try
        {
            using var aes = new AesGcm(keys.Content, TagLength);
            aes.Decrypt(blob.AsSpan(MagicLength, NonceLength), plain, blob.AsSpan(blob.Length - TagLength), plain, Encoding.ASCII.GetBytes(blobName));
        }
        catch (AuthenticationTagMismatchException)
        {
            problem = BlobProblem.NotAuthentic;
            return null;
        }

        problem = BlobProblem.BadContent;
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(plain);
        if (headerLength > SyncLimits.MaxHeaderBytes || headerLength > plainLength - HeaderLengthBytes)
        {
            return null;
        }

        int contentStart = MagicLength + NonceLength + HeaderLengthBytes + (int)headerLength;
        var content = blob.AsMemory(contentStart, blob.Length - TagLength - contentStart);
        if (ReadHeader(plain.Slice(HeaderLengthBytes, (int)headerLength)) is not { } header
            || !SyncPath.IsSynced(header.Path)
            || keys.BlobName(SyncPath.Key(header.Path)) != blobName
            || (header.Deleted ? content.Length > 0 : Hash(content.Span) != header.Sha256))
        {
            return null;
        }

        problem = BlobProblem.None;
        return new OpenedBlob(header, content);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] WriteHeader(BlobHeader header)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, HeaderOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("path", header.Path);
            writer.WriteString("mtimeUtc", header.MtimeUtc.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            writer.WriteBoolean("deleted", header.Deleted);
            writer.WriteString("sha256", header.Sha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    // The header of a blob that authenticated. It was written by a holder of the key, which is
    // to say by this code on one of the owner's machines; it is read strictly all the same.
    // Null for anything but one JSON object that has the four values, each once and of its kind.
    private static BlobHeader? ReadHeader(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object || reader.Read())
            {
                return null;
            }

            string? path = null;
            string? sha256 = null;
            DateTimeOffset? mtime = null;
            bool? deleted = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("path") && path is null && property.Value.ValueKind == JsonValueKind.String)
                {
                    path = property.Value.GetString();
                }
                else if (property.NameEquals("sha256") && sha256 is null && property.Value.ValueKind == JsonValueKind.String)
                {
                    sha256 = property.Value.GetString();
                }
                else if (property.NameEquals("mtimeUtc") && mtime is null && property.Value.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParseExact(
                        property.Value.GetString(),
                        TimeFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var time))
                {
                    mtime = time;
                }
                else if (property.NameEquals("deleted") && deleted is null && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    deleted = property.Value.GetBoolean();
                }
                else if (property.NameEquals("path") || property.NameEquals("sha256") || property.NameEquals("mtimeUtc") || property.NameEquals("deleted"))
                {
                    // One of the four, a second time or of the wrong kind.
                    return null;
                }
            }

            if (path is null || sha256 is null || mtime is null || deleted is null
                || !SyncPath.TryParse(path, out _)
                || !(deleted.Value ? sha256.Length == 0 : sha256.Length == HashLength && IsLowerHex(sha256)))
            {
                return null;
            }

            return new BlobHeader(path, mtime.Value, deleted.Value, sha256);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not JSON, or text that holds half of a surrogate pair written as an escape.
            return null;
        }
    }
}
