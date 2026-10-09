using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Settings;

namespace Wyvern.Core.Sync;

/// <summary>
/// The fingerprint of an SSH server's host key, in the form <c>ssh-keygen -lf</c> prints it:
/// <c>SHA256:</c> and the SHA-256 of the key as base64 without the padding, 43 characters.
/// </summary>
public static class HostKeyFingerprint
{
    public const string Prefix = "SHA256:";

    private const int Base64Length = 43;

    /// <summary>The fingerprint of a host key, from the bytes the server sent.</summary>
    public static string Of(ReadOnlySpan<byte> hostKey) =>
        Prefix + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    /// <summary>
    /// True when the text is a fingerprint that a key can have: its shape is right, and its 43
    /// characters are the SHA-256 of something, which is 32 bytes and no more. Nothing else is
    /// ever pinned or compared.
    /// </summary>
    public static bool IsValid(string? text)
    {
        if (text is null || text.Length != Prefix.Length + Base64Length || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> body = text.AsSpan(Prefix.Length);
        foreach (char c in body)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/'))
            {
                return false;
            }
        }

        // 43 characters carry 258 bits and a hash has 256, so the last character has two bits
        // that no hash gives. Decoded with the padding put back, the text must be exactly 32
        // bytes, and those bytes must give the same 43 characters again.
        Span<char> padded = stackalloc char[Base64Length + 1];
        body.CopyTo(padded);
        padded[Base64Length] = '=';

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        if (!Convert.TryFromBase64Chars(padded, hash, out int written) || written != hash.Length)
        {
            return false;
        }

        Span<char> again = stackalloc char[Base64Length + 1];
        return Convert.TryToBase64Chars(hash, again, out int length)
            && length == again.Length
            && again[..Base64Length].SequenceEqual(body);
    }

    /// <summary>
    /// True when the host key the server sent is the one that was pinned. A pinned text that is
    /// not a fingerprint matches no key.
    /// </summary>
    public static bool Matches(string? pinned, ReadOnlySpan<byte> hostKey) =>
        IsValid(pinned)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pinned!), Encoding.ASCII.GetBytes(Of(hostKey)));
}

/// <summary>How sync.json was found.</summary>
public enum SyncConfigState
{
    /// <summary>There is no sync.json: sync has never been set up. Nothing is said and no connection is made.</summary>
    Missing,

    /// <summary>The file is there and cannot be used as it is. No connection is made, and the owner is told.</summary>
    Unreadable,

    /// <summary>The file can be used but holds no host key fingerprint: the server's key has not been confirmed. No connection is made.</summary>
    Unpinned,

    /// <summary>Everything a connection needs is there, the pinned fingerprint included.</summary>
    Ready,
}

/// <summary>What reading sync.json gave.</summary>
/// <param name="Config">The settings when the state is <see cref="SyncConfigState.Unpinned"/> or <see cref="SyncConfigState.Ready"/>, otherwise null.</param>
/// <param name="Problem">For <see cref="SyncConfigState.Unreadable"/>, a sentence of fixed words that names at most a key of the file and a line number. Otherwise null.</param>
/// <param name="Cause">The exception behind the problem, for the log, which writes its type only.</param>
public sealed record SyncConfigLoad(SyncConfigState State, SyncConfig? Config, string? Problem, Exception? Cause = null)
{
    /// <summary>The type's name and the state, and nothing of the settings.</summary>
    public override string ToString() => $"{nameof(SyncConfigLoad)} {{ State = {State} }}";
}

/// <summary>
/// Where sync connects to, as sync.json holds it. The values are the owner's: the server's
/// name, the user name and the path of the key file never go to the log or a notification.
/// </summary>
/// <param name="Host">The server's name or address.</param>
/// <param name="Port">The SSH port.</param>
/// <param name="User">The user name on the server.</param>
/// <param name="PrivateKeyPath">The full path of the SSH private key file, on a drive.</param>
/// <param name="RemoteDir">The folder on the server, as its full path from the root, without a slash at its end. Blobs go into <c>blobs</c> inside it.</param>
/// <param name="HostKeyFingerprint">The pinned fingerprint of the server's host key, or null when none has been confirmed.</param>
public sealed record SyncConfig(string Host, int Port, string User, string PrivateKeyPath, string RemoteDir, string? HostKeyFingerprint)
{
    public const int DefaultPort = 22;

    public const string HostProblem = "The server must be a host name or an address, such as server.example.org or 100.64.0.5.";
    public const string HostWithPortProblem = "The server must be a host name or an address without a port: the port has its own field.";
    public const string PortProblem = "The port must be a whole number from 1 to 65535.";
    public const string UserProblem = "The user name must be filled in, without spaces.";
    public const string KeyPathProblem = "The key file must be given by its full path on a drive, such as C:\\Users\\you\\.ssh\\id_ed25519.";
    public const string RemoteDirProblem = "The server folder must be given by its full path on the server, starting with /, such as /home/you/wyvern, without \"..\" in it.";
    public const string FingerprintProblem = "The host key fingerprint must be SHA256: followed by 43 characters, as ssh-keygen prints it.";

    private const string Unusable = "sync.json cannot be used. ";
    private const string NotValidUnicode = "A setting holds text that is not valid Unicode, and is not saved as another character.";
    private const string NotValidJson = "The file is not valid JSON.";
    private const string UnreadableText = "The file holds text that cannot be read.";
    private const long MaxFileBytes = 64 * 1024;
    private const int MaxHostLength = 253;
    private const int MaxUserLength = 64;
    private const int MaxRemoteDirLength = 1024;
    private const string TemporarySuffix = ".tmp";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        // The file can be edited by hand.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Throws for text that is not text, where the usual encoding would put U+FFFD in its place.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>The folder on the server that holds the blobs.</summary>
    public string BlobsDir => RemoteDir == "/" ? "/blobs" : RemoteDir + "/blobs";

    /// <summary>
    /// Sixty-four hex digits that are the same for the same server, port, user, folder and sync
    /// key, and say nothing about any of them. The record of the last sync carries it, so that a
    /// record made against another server, or under another key, is not used against this one.
    /// </summary>
    /// <param name="keyCheck">What <see cref="SyncKeys.Check"/> gives.</param>
    public string RemoteId(string keyCheck) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Host.ToLowerInvariant(), Port.ToString(CultureInfo.InvariantCulture), User, RemoteDir, keyCheck))));

    /// <summary>The same settings with this fingerprint pinned.</summary>
    /// <exception cref="ArgumentException">The text is not a fingerprint (<see cref="Sync.HostKeyFingerprint.IsValid"/>): settings that are pinned always hold one.</exception>
    public SyncConfig Pinned(string fingerprint) =>
        Sync.HostKeyFingerprint.IsValid(fingerprint)
            ? this with { HostKeyFingerprint = fingerprint }
            : throw new ArgumentException(FingerprintProblem, nameof(fingerprint));

    /// <summary>The type's name and whether a fingerprint is pinned: never the server, the user or a path.</summary>
    public override string ToString() => $"{nameof(SyncConfig)} {{ Pinned = {HostKeyFingerprint is not null} }}";

    /// <summary>
    /// Makes the settings from what was typed or read, or says what is wrong with them. This is
    /// the one place that judges them: the Sync Setup window and sync.json both go through it.
    /// </summary>
    /// <param name="port">A whole number as text. Blank means 22.</param>
    /// <param name="privateKeyPath">A full path on a drive, such as C:\Users\you\.ssh\id_ed25519. White space and one pair of double quotes around it are dropped, and environment variables such as %USERPROFILE% are expanded.</param>
    /// <param name="remoteDir">The full path of a folder on the server, starting with a slash. White space around it and slashes at its end are dropped.</param>
    /// <param name="fingerprint">A fingerprint, or blank for none.</param>
    /// <param name="problem">One of this type's fixed sentences when the answer is false, otherwise empty.</param>
    public static bool TryCreate(string? host, string? port, string? user, string? privateKeyPath, string? remoteDir, string? fingerprint, out SyncConfig? config, out string problem)
    {
        config = null;
        int number = DefaultPort;
        if (!string.IsNullOrWhiteSpace(port)
            && !(port.Trim().All(char.IsAsciiDigit) && int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number)))
        {
            problem = PortProblem;
            return false;
        }

        return TryCreate(host, number, user, privateKeyPath, remoteDir, fingerprint, out config, out problem);
    }

    /// <summary>
    /// Reads sync.json. Nothing here opens a connection, and nothing here throws: a file that
    /// cannot be read or understood is <see cref="SyncConfigState.Unreadable"/>, which stops
    /// sync and never loosens it. Only a file that is not there at all means sync was never set up.
    /// </summary>
    public static SyncConfigLoad Load(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return Unreadable("There is no file name to read it from.");
        }

        byte[] content;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes)
            {
                return Unreadable("It is far too large to be that file.");
            }

            content = new byte[stream.Length];
            stream.ReadExactly(content);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SyncConfigLoad(SyncConfigState.Missing, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unreadable("The file could not be read.", ex);
        }

        try
        {
            ReadOnlyMemory<byte> text = content;
            if (text.Span.StartsWith(ByteOrderMark))
            {
                text = text[ByteOrderMark.Length..];
            }

            using var document = JsonDocument.Parse(text, ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Unreadable("It must be one JSON object.");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // A key that is written twice leaves in doubt which one counts.
                if (!values.TryAdd(property.Name, property.Value))
                {
                    return Unreadable("A key is written twice.");
                }
            }

            int port = DefaultPort;
            if (Written(values, "port") is { } portValue
                && !(portValue.ValueKind == JsonValueKind.Number && portValue.TryGetInt32(out port)))
            {
                return Unreadable(PortProblem);
            }

            if (!Text(values, "host", out string? host)
                || !Text(values, "user", out string? user)
                || !Text(values, "privateKeyPath", out string? keyPath)
                || !Text(values, "remoteDir", out string? remoteDir)
                || !Text(values, "hostKeyFingerprint", out string? fingerprint))
            {
                return Unreadable("\"host\", \"user\", \"privateKeyPath\", \"remoteDir\" and \"hostKeyFingerprint\" must each be text.");
            }

            if (!TryCreate(host, port, user, keyPath, remoteDir, fingerprint, out var config, out string problem))
            {
                return Unreadable(problem);
            }

            return new SyncConfigLoad(config!.HostKeyFingerprint is null ? SyncConfigState.Unpinned : SyncConfigState.Ready, config, null);
        }
        catch (JsonException ex)
        {
            // The parser's own words quote the file: the character it stopped at and the path of
            // the value. The line number is enough to find the mistake, and a cause of the same
            // type with a fixed sentence stands in for the exception, which is let go of.
            string where = ex.LineNumber is long line ? $" (line {line + 1})" : "";
            return Unreadable("It is not valid JSON" + where + ".", new JsonException(NotValidJson));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Text that holds half of a surrogate pair written as an escape, or a byte that is not
            // UTF-8. The exception quotes it, and holds the decoder's own exception, which quotes
            // the bytes: so what is kept is a new one of the same kind, with a fixed sentence.
            Exception cause = ex is InvalidOperationException ? new InvalidOperationException(UnreadableText) : new ArgumentException(UnreadableText);
            return Unreadable("It holds text that cannot be read.", cause);
        }
    }

    /// <summary>
    /// Writes sync.json: to a temporary file beside it, which then takes its place in one step,
    /// so that the file is never found half written.
    /// </summary>
    /// <remarks>
    /// When this fails, no temporary file is left beside the file, and the file that was there is
    /// as it was.
    /// </remarks>
    /// <exception cref="ArgumentException">A setting holds half of a surrogate pair, which cannot be written as UTF-8 without becoming another character. Nothing is written.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public void Save(string file)
    {
        // The writer below would put U+FFFD in place of a lone half and say nothing, and the
        // settings that were loaded would not be the ones that were saved.
        if (!IsWellFormed(Host) || !IsWellFormed(User) || !IsWellFormed(PrivateKeyPath) || !IsWellFormed(RemoteDir) || !IsWellFormed(HostKeyFingerprint))
        {
            throw new ArgumentException(NotValidUnicode);
        }

        string? folder = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = file + TemporarySuffix;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, WriteOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteString("host", Host);
                    writer.WriteNumber("port", Port);
                    writer.WriteString("user", User);
                    writer.WriteString("privateKeyPath", PrivateKeyPath);
                    writer.WriteString("remoteDir", RemoteDir);
                    if (HostKeyFingerprint is not null)
                    {
                        writer.WriteString("hostKeyFingerprint", HostKeyFingerprint);
                    }

                    writer.WriteEndObject();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
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

    private static bool TryCreate(string? host, int port, string? user, string? privateKeyPath, string? remoteDir, string? fingerprint, out SyncConfig? config, out string problem)
    {
        config = null;
        string server = (host ?? "").Trim();
        if (server.Length is 0 or > MaxHostLength
            || !server.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_')

            // Nothing but dots, hyphens and the like names nothing, and a hyphen in front is what
            // an option of a command looks like.
            || !server.Any(char.IsAsciiLetterOrDigit)
            || server[0] == '-')
        {
            problem = HostProblem;
            return false;
        }

        // "server:2222": the port has a field of its own, and one typed here would go unused.
        // Two colons or more is an address written without brackets, and is judged as it was.
        if (server.Count(c => c == ':') == 1)
        {
            problem = HostWithPortProblem;
            return false;
        }

        if (port is < 1 or > 65535)
        {
            problem = PortProblem;
            return false;
        }

        string name = (user ?? "").Trim();
        // Any surrogate is refused, so half of a pair is refused too.
        if (name.Length is 0 or > MaxUserLength || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c) || IsInvisible(c)))
        {
            problem = UserProblem;
            return false;
        }

        if (KeyPath(privateKeyPath) is not { } keyPath)
        {
            problem = KeyPathProblem;
            return false;
        }

        if (Folder(remoteDir) is not { } folder)
        {
            problem = RemoteDirProblem;
            return false;
        }

        string? pinned = string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Trim();
        if (pinned is not null && !Sync.HostKeyFingerprint.IsValid(pinned))
        {
            problem = FingerprintProblem;
            return false;
        }

        problem = "";
        config = new SyncConfig(server, port, name, keyPath, folder, pinned);
        return true;
    }

    // True for a character that shows as nothing, or as something else than what it is: the
    // Unicode categories Format (a zero-width space, a right-to-left override, a byte order
    // mark), LineSeparator and ParagraphSeparator.
    private static bool IsInvisible(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    // False when the text holds half of a surrogate pair that has no other half. A UTF-8 encoding
    // that throws for such text is asked, so that this is the same judgment that the file is
    // written under: the usual encoding puts U+FFFD in its place and says nothing.
    private static bool IsWellFormed(string? text)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(text ?? "");
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    // "C:\" or "C:/": an ASCII letter, a colon and a separator.
    private static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    // The key file's full path, or null when what was written is not one. Only a path on a drive
    // is taken: sync.json names one network destination, the SSH server, and a path on a share
    // ("\\server\share\key") would be a second one, which Windows contacts when the key file is
    // read. A path of the extended or device forms, of a pipe or a device, is not taken either.
    private static string? KeyPath(string? written)
    {
        string text = (written ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        if (text.Length == 0)
        {
            return null;
        }

        // Judged as text only. Nothing here asks Windows about the path. Half of a surrogate pair
        // would be saved as another character, and the file that was named would not be the one
        // that is read.
        string expanded = Environment.ExpandEnvironmentVariables(text);
        return IsDrivePath(expanded)
            && Path.IsPathFullyQualified(expanded)
            && !ExpandedPath.HasUnexpandedVariable(expanded)
            && !ClipboardBytes.StandsForPipeOrDevice(expanded)
            && IsWellFormed(expanded)
            && !Path.EndsInDirectorySeparator(expanded)
            && expanded.IndexOfAny(['<', '>', '"', '|', '?', '*']) < 0
            && !expanded.Any(char.IsControl)
            ? expanded
            : null;
    }

    // The folder on the server without slashes at its end, or null. It is the full path of a
    // folder on a Unix machine: it starts with a slash, it has forward slashes, and no part
    // between them is empty, ".", or "..". A path that is not from the root ("wyvern", "~/wyvern",
    // "$HOME/wyvern") would be taken by the server as relative to wherever its session starts,
    // or as a name with a "~" or a "$" in it. Any surrogate is refused, so half of a pair is too.
    private static string? Folder(string? written)
    {
        string text = (written ?? "").Trim();
        if (text.Length is 0 or > MaxRemoteDirLength
            || text[0] != '/'
            || text.Any(c => char.IsControl(c) || char.IsSurrogate(c) || IsInvisible(c) || c == '\\'))
        {
            return null;
        }

        string trimmed = text.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/";
        }

        // The first part is empty, in front of the first slash. Each one after it must be a name.
        string[] parts = trimmed.Split('/');
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || parts[i] is "." or "..")
            {
                return null;
            }
        }

        return trimmed;
    }

    private static JsonElement? Written(Dictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    /// <returns>False when the key holds something that is not text. A key that is missing or null gives true and null.</returns>
    private static bool Text(Dictionary<string, JsonElement> values, string key, out string? text)
    {
        text = null;
        if (Written(values, key) is not { } value)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = value.GetString();
        return true;
    }

    private static SyncConfigLoad Unreadable(string sentence, Exception? cause = null) =>
        new(SyncConfigState.Unreadable, null, Unusable + sentence, cause);
}
