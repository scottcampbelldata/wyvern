using System.Security.Cryptography;
using System.Text;
using Wyvern.Core.Platform;

namespace Wyvern.Core.Sync;

/// <summary>How a secret's file was found.</summary>
public enum SecretState
{
    /// <summary>There is no file: the secret was never saved on this machine.</summary>
    Missing,

    /// <summary>
    /// The file is there and the secret cannot be had from it: it cannot be opened, it was not
    /// protected by this Windows user on this machine, or it does not hold what it should.
    /// </summary>
    Unreadable,

    /// <summary>The secret was read.</summary>
    Read,
}

/// <summary>
/// The two secrets sync keeps on this machine, each in a file of its own, protected so that only
/// the Windows user who is signed in can read it: the sync key (key.bin) and, when the SSH key
/// file has one, its passphrase.
/// </summary>
/// <remarks>
/// A file that cannot be read never counts as "no secret": the callers stop and tell the owner.
/// A secret is written to a temporary file that then takes the file's place in one step, and is
/// read back before the write counts as done, so that "saved" is said only of a key that can be
/// had again. The arrays that hold a secret here are wiped before they are let go of. The
/// passphrase has to be handed on as text, and text cannot be wiped. What is protected is one
/// byte that says which secret it is (<see cref="KeyTag"/> or <see cref="PassphraseTag"/>), and
/// then the secret, so that the file of one is never read as the file of the other.
/// </remarks>
public sealed class KeyStore
{
    // Far more than either file ever holds. A larger file is not that file.
    private const long MaxFileBytes = 64 * 1024;
    private const string TemporarySuffix = ".tmp";

    // The first byte of what is protected. A file that begins with the other's tag, or with
    // another byte, or with none, is not this secret's file.
    private const byte KeyTag = 1;
    private const byte PassphraseTag = 2;

    // The sentences of the exceptions that are kept as causes or thrown here. They are fixed, and
    // hold nothing of a file or of a secret.
    private const string NoFileName = "The file of a secret needs a name.";
    private const string NotText = "The passphrase file does not hold text.";
    private const string NotReadBack = "A secret could not be read back after it was written.";
    private const string PassphraseNotUnicode = "A passphrase must be valid text, without half of a surrogate pair.";

    // Encodes and decodes UTF-8 and throws for what is not text, where the usual encoding would
    // put U+FFFD in its place and say nothing.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _keyFile;
    private readonly string _passphraseFile;
    private readonly IKeyProtector _protector;

    public KeyStore(string keyFile, string passphraseFile, IKeyProtector protector)
    {
        _keyFile = keyFile;
        _passphraseFile = passphraseFile;
        _protector = protector;
    }

    /// <summary>Reads the sync key. Never throws for a problem with the file or with the protection.</summary>
    /// <param name="master">The key's 32 bytes in a pinned array, for the caller to wipe, when the state is <see cref="SecretState.Read"/>. Otherwise empty.</param>
    /// <param name="cause">What went wrong, for the log, which writes its type only. Null when nothing did.</param>
    public SecretState ReadMaster(out byte[] master, out Exception? cause)
    {
        master = [];
        var state = Read(_keyFile, KeyTag, out byte[] secret, out cause);
        try
        {
            if (state != SecretState.Read)
            {
                return state;
            }

            if (secret.Length != SyncKeys.KeyLength)
            {
                return SecretState.Unreadable;
            }

            master = GC.AllocateArray<byte>(SyncKeys.KeyLength, pinned: true);
            secret.CopyTo(master, 0);
            return SecretState.Read;
        }
        finally
        {
            SyncKeys.Wipe(secret);
        }
    }

    /// <summary>
    /// Saves the sync key, and reads it back to be sure it can be had again. When this returns,
    /// the key is saved; when it throws, the file that was there before is still there.
    /// </summary>
    /// <exception cref="ArgumentException">The key is not 32 bytes.</exception>
    /// <exception cref="IOException">The file could not be written, or what was written could not be read back.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    /// <exception cref="CryptographicException">Windows would not protect the key.</exception>
    public void WriteMaster(ReadOnlySpan<byte> master)
    {
        if (master.Length != SyncKeys.KeyLength)
        {
            throw new ArgumentException("A sync key is 32 bytes.", nameof(master));
        }

        Write(_keyFile, KeyTag, master);
    }

    /// <summary>Reads the passphrase of the SSH key file. Never throws for a problem with the file or with the protection.</summary>
    /// <param name="passphrase">The passphrase when the state is <see cref="SecretState.Read"/>. Otherwise null.</param>
    public SecretState ReadPassphrase(out string? passphrase, out Exception? cause)
    {
        passphrase = null;
        var state = Read(_passphraseFile, PassphraseTag, out byte[] secret, out cause);
        try
        {
            if (state != SecretState.Read)
            {
                return state;
            }

            passphrase = StrictUtf8.GetString(secret);
            return SecretState.Read;
        }
        catch (DecoderFallbackException)
        {
            // The decoder's message quotes the bytes it could not read, and the file is the
            // passphrase, unprotected. What is kept is a new exception of the same type with a
            // fixed sentence, and not the one that was thrown.
            cause = new DecoderFallbackException(NotText);
            return SecretState.Unreadable;
        }
        finally
        {
            SyncKeys.Wipe(secret);
        }
    }

    /// <summary>
    /// Saves the passphrase of the SSH key file, or, for null or empty text, removes the file:
    /// a key file without a passphrase has nothing to keep.
    /// </summary>
    /// <exception cref="ArgumentException">The passphrase holds half of a surrogate pair, which cannot be written as UTF-8 without becoming another character. Nothing is written, and the file that was there stays.</exception>
    /// <exception cref="IOException">The file could not be written or removed, or what was written could not be read back.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written or removed.</exception>
    /// <exception cref="CryptographicException">Windows would not protect the passphrase.</exception>
    public void WritePassphrase(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            File.Delete(_passphraseFile);
            return;
        }

        byte[] secret;
        try
        {
            secret = StrictUtf8.GetBytes(passphrase);
        }
        catch (EncoderFallbackException)
        {
            // Its message names the character. A fixed sentence is thrown instead.
            throw new ArgumentException(PassphraseNotUnicode, nameof(passphrase));
        }

        try
        {
            Write(_passphraseFile, PassphraseTag, secret);
        }
        finally
        {
            SyncKeys.Wipe(secret);
        }
    }

    // Reads a file and gives what is inside the protection after the tag, which has to be the
    // tag that was asked for. The array that came out of the protection, with the tag in it, is
    // wiped here, and the one that is given back is the caller's to wipe.
    //
    // The causes that are kept are the ones whose messages cannot hold what is in a file or a
    // secret: the open and read failures of the file system, which name the path and say what
    // went wrong, and what the protection throws, which says that it could not.
    private SecretState Read(string file, byte tag, out byte[] secret, out Exception? cause)
    {
        secret = [];
        cause = null;
        if (string.IsNullOrWhiteSpace(file))
        {
            cause = new ArgumentException(NoFileName);
            return SecretState.Unreadable;
        }

        byte[] stored;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is 0 or > MaxFileBytes)
            {
                return SecretState.Unreadable;
            }

            stored = new byte[stream.Length];
            stream.ReadExactly(stored);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The only answer that means there is no file. A folder that cannot be reached is
            // not that, and is not taken for "never set up".
            return SecretState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            cause = ex;
            return SecretState.Unreadable;
        }

        byte[] tagged = [];
        try
        {
            tagged = _protector.Unprotect(stored);

            // No bytes at all, or the tag of the other secret, or another byte: not this file.
            if (tagged.Length == 0 || tagged[0] != tag)
            {
                return SecretState.Unreadable;
            }

            secret = tagged[1..];
            return SecretState.Read;
        }
        catch (Exception ex) when (ex is CryptographicException or NotSupportedException or ArgumentException)
        {
            cause = ex;
            return SecretState.Unreadable;
        }
        finally
        {
            SyncKeys.Wipe(tagged);
        }
    }

    private void Write(string file, byte tag, ReadOnlySpan<byte> secret)
    {
        string? folder = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // The tag and then the secret, which is what is protected, and what the read-back below
        // is compared with.
        byte[] plain = new byte[secret.Length + 1];
        plain[0] = tag;
        secret.CopyTo(plain.AsSpan(1));
        string temporary = file + TemporarySuffix;
        try
        {
            byte[] stored = _protector.Protect(plain);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(stored);
                stream.Flush(flushToDisk: true);
            }

            // Read back from the temporary file, through the protection, before it takes the
            // place of the file that is there: a key that cannot be had again was not saved.
            byte[] back = _protector.Unprotect(File.ReadAllBytes(temporary));
            bool same = CryptographicOperations.FixedTimeEquals(back, plain);
            SyncKeys.Wipe(back);
            if (!same)
            {
                throw new IOException(NotReadBack);
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        finally
        {
            SyncKeys.Wipe(plain);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The failure that matters is the one being passed on.
        }
    }
}
