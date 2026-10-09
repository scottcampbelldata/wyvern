using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Sync;

/// <summary>
/// The two keys sync works with, made from the owner's sync key (the master key, 32 random
/// bytes): one that encrypts what a blob holds, and one that names blobs. Both are derived with
/// HKDF-SHA256, with no salt, and with the texts <c>beacon/content/v1</c> and
/// <c>beacon/name/v1</c> as the info.
/// </summary>
/// <remarks>
/// <para>The master key itself is not kept here: it is read from its file when a sync run
/// starts, these two are made from it, and it is wiped. These are wiped by
/// <see cref="Dispose"/>, which the run calls when it ends, so between two runs Beacon holds no
/// key in memory at all.</para>
/// <para>The arrays that hold keys are pinned, so that the garbage collector never moves them
/// and leaves a copy behind, and they are overwritten with zeros when they are let go of. What
/// cannot be wiped is text: the key as the owner reads and types it is a string for as long as
/// a window shows it.</para>
/// <para>An instance is made, used and disposed by one thread at a time. There is no lock: a
/// <see cref="Dispose"/> on another thread while a call is under way is not guarded against,
/// and that call would then work with a key that has been overwritten with zeros, and not
/// fail. That does not arise where it matters: the sync run makes its keys, uses them and
/// disposes them on its own thread, and nothing else holds them.</para>
/// </remarks>
public sealed class SyncKeys : IDisposable
{
    /// <summary>The length of the master key, and of each key made from it, in bytes.</summary>
    public const int KeyLength = 32;

    // A 32-byte key written as base64 is 44 characters. Text much longer than that is not one.
    private const int MaxTextLength = 64;

    private readonly byte[] _content = GC.AllocateArray<byte>(KeyLength, pinned: true);
    private readonly byte[] _name = GC.AllocateArray<byte>(KeyLength, pinned: true);
    private bool _disposed;

    private SyncKeys()
    {
    }

    private static ReadOnlySpan<byte> ContentInfo => "beacon/content/v1"u8;

    private static ReadOnlySpan<byte> NameInfo => "beacon/name/v1"u8;

    // What the check value is made from: a zero byte and then the text beacon/check/v1, 16 bytes
    // in all. No path sync works with can hold a zero byte (SyncPath.TryParse refuses every
    // control character), so no blob is ever named from this input and the check value is
    // never the start of a real blob's name. The text alone is a path sync accepts.
    private static ReadOnlySpan<byte> CheckInput => "\0beacon/check/v1"u8;

    /// <summary>The key that encrypts and authenticates what a blob holds (AES-256-GCM).</summary>
    internal ReadOnlySpan<byte> Content
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _content;
        }
    }

    /// <summary>Makes the two keys from the master key. The caller wipes the master key afterwards.</summary>
    /// <exception cref="ArgumentException">The master key is not 32 bytes.</exception>
    public static SyncKeys FromMaster(ReadOnlySpan<byte> master)
    {
        if (master.Length != KeyLength)
        {
            throw new ArgumentException("A sync key is 32 bytes.", nameof(master));
        }

        var keys = new SyncKeys();
        Derive(master, [], ContentInfo, keys._content);
        Derive(master, [], NameInfo, keys._name);
        return keys;
    }

    /// <summary>
    /// HKDF with SHA-256 (RFC 5869), extract and expand in one step. It is public so that the
    /// test vectors of the RFC can be run through the very call the keys are made with.
    /// </summary>
    public static void Derive(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, Span<byte> output) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, output, salt, info);

    /// <summary>A new master key: 32 bytes from the system's random number generator, in a pinned array.</summary>
    public static byte[] NewMaster()
    {
        byte[] master = GC.AllocateArray<byte>(KeyLength, pinned: true);
        RandomNumberGenerator.Fill(master);
        return master;
    }

    /// <summary>The master key as the owner reads it and types it on the other machine: base64, 44 characters.</summary>
    public static string Format(ReadOnlySpan<byte> master) => Convert.ToBase64String(master);

    /// <summary>
    /// Reads a master key that was typed or pasted: base64 of exactly 32 bytes, with or without
    /// white space around it. Anything else is not a key.
    /// </summary>
    /// <param name="master">The key in a pinned array, for the caller to wipe, or an empty array.</param>
    public static bool TryParse(string? text, out byte[] master)
    {
        master = [];
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxTextLength)
        {
            return false;
        }

        // Decoded into a pinned array that is wiped again, so that a key that was typed does
        // not stay behind in an array nobody holds.
        byte[] decoded = GC.AllocateArray<byte>(MaxTextLength, pinned: true);
        try
        {
            if (!Convert.TryFromBase64String(trimmed, decoded, out int written) || written != KeyLength)
            {
                return false;
            }

            master = GC.AllocateArray<byte>(KeyLength, pinned: true);
            decoded.AsSpan(0, KeyLength).CopyTo(master);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    /// <summary>Overwrites a secret with zeros. Null is nothing to wipe.</summary>
    public static void Wipe(byte[]? secret) => CryptographicOperations.ZeroMemory(secret);

    /// <summary>
    /// The name a file's blob has on the server: HMAC-SHA256 of the path, under the name key, as
    /// 64 lower-case hex digits. The server sees only this, and cannot turn it back into a path
    /// or tell whether two servers hold the same file.
    /// </summary>
    /// <param name="pathKey">The path as <see cref="SyncPath.Key"/> gives it.</param>
    public string BlobName(string pathKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_name, Encoding.UTF8.GetBytes(pathKey)));
    }

    /// <summary>
    /// Sixteen hex digits that are the same for the same sync key and say nothing about it. The
    /// record of the last sync carries it, so that a record made under another key is not used.
    /// </summary>
    public string Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_name, CheckInput))[..16];
    }

    /// <summary>Wipes both keys. They cannot be used afterwards.</summary>
    public void Dispose()
    {
        _disposed = true;
        CryptographicOperations.ZeroMemory(_content);
        CryptographicOperations.ZeroMemory(_name);
    }

    /// <summary>The type's name and nothing else: a key is never printed.</summary>
    public override string ToString() => nameof(SyncKeys);
}
