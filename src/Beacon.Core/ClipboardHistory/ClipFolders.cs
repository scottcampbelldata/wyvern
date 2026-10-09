using System.Text.Encodings.Web;
using System.Text.Json;

namespace Beacon.Core.ClipboardHistory;

/// <summary>What <see cref="ClipFolders.TryLoad"/> found.</summary>
public enum ClipFoldersState
{
    /// <summary>
    /// The file, or the folder it would be in, is not there, or the file holds nothing at all
    /// (zero bytes): no name has been remembered yet.
    /// </summary>
    Missing,

    /// <summary>The file is there and is a list of names. The list may be empty.</summary>
    Read,

    /// <summary>
    /// The file is there, holds something, and is not a list that could be read: it is larger
    /// than the cap, held or denied by another program, not JSON in UTF-8, not an object, or has
    /// no "folders" array.
    /// </summary>
    Unreadable,
}

/// <summary>
/// Which clipboard folders are this machine's: the names it has written its history under,
/// remembered in a small file beside settings.json (clipboard-folders.json). A machine's folder
/// is named after "deviceName", and that can change, or the PC can be renamed. The folder of the
/// earlier name still holds what this machine copied, and it stays this machine's: to scrub
/// when something in it is deleted, and to keep in step on the sync server, which only the
/// machine that owns a folder may do.
/// </summary>
/// <remarks>
/// <para>
/// A name is added when a store takes its folder for writing. At most <see cref="MaxNames"/>
/// names are kept: beyond that the oldest are dropped, and their folders are then treated as
/// another machine's.
/// </para>
/// <para>
/// The file is one that a person may edit by hand, to add a name, so it is read with comments
/// and trailing commas allowed, and with or without the mark some editors put at the start of
/// a UTF-8 file. When it cannot be read the list is empty, which only means that earlier
/// folders are treated as another machine's, as they were before this file existed: the
/// history rewrites nothing more because the file was lost. A file that is there but cannot be
/// understood (damaged, too large, held by another program, not a list, not UTF-8) is never
/// written over: <see cref="Remember"/> leaves it as it is, says so, and does not add the
/// name, so that a slip in a hand edit does not wipe the names that were in it. Sync does not
/// run at all while the file is like that (<see cref="TryLoad"/> tells it apart): with fewer
/// names it would take this machine's earlier folders for another machine's. A file that is
/// not there is made, and so is one of zero bytes, which holds no name to lose.
/// </para>
/// <para>
/// Two machines must not have, or have had, the same name; that is the owner's to see to, as it
/// is for "deviceName" itself.
/// </para>
/// </remarks>
public static class ClipFolders
{
    /// <summary>The most names that are kept. A machine is renamed a few times in its life, not this often.</summary>
    public const int MaxNames = 32;

    private const long MaxFileBytes = 64 * 1024;
    private const string TemporarySuffix = ".tmp";

    // Fixed words, said once when a name could not be added because the list could not be read.
    private const string NotUnderstood = "The list of this machine's clipboard folders could not be understood. It was left as it is, and this folder's name was not added.";

    // The file is edited by a person: a comment or a comma left at the end of a list is not a reason to give it up.
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // What some editors put at the start of a file they save as UTF-8. It is not part of the JSON.
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// True when the text is a name a machine's clipboard folder can have: exactly what
    /// <see cref="ClipboardOptions.SafeDeviceName"/> makes, and so safe to join to the clipboard
    /// folder as one folder name.
    /// </summary>
    public static bool IsFolderName(string? name) =>
        !string.IsNullOrEmpty(name) && ClipboardOptions.SafeFileName(name, ClipboardOptions.MaxDeviceNameLength) == name;

    /// <summary>
    /// The names this machine has written under, oldest first. Empty when the file is not there
    /// or cannot be read or understood: <see cref="TryLoad"/> tells those apart. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Load(string file)
    {
        TryLoad(file, out var names);
        return names;
    }

    /// <summary>
    /// Reads the names this machine has written under, oldest first, and says what the file was.
    /// Never throws.
    /// </summary>
    /// <param name="names">The names when the state is <see cref="ClipFoldersState.Read"/>, otherwise empty.</param>
    public static ClipFoldersState TryLoad(string file, out IReadOnlyList<string> names)
    {
        names = [];
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes)
            {
                return ClipFoldersState.Unreadable;
            }

            if (stream.Length == 0)
            {
                // Nothing at all: no name is held, and this class's own writer never leaves such
                // a file. It is as good as not there, and may be written.
                return ClipFoldersState.Missing;
            }

            byte[] content = new byte[stream.Length];
            stream.ReadExactly(content);
            ReadOnlyMemory<byte> text = content;
            if (text.Span.StartsWith(ByteOrderMark))
            {
                text = text[ByteOrderMark.Length..];
            }

            using var document = JsonDocument.Parse(text, ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("folders", out var folders)
                || folders.ValueKind != JsonValueKind.Array)
            {
                return ClipFoldersState.Unreadable;
            }

            var found = new List<string>();
            foreach (var entry in folders.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && entry.GetString() is { } name
                    && IsFolderName(name)
                    && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(name);
                }
            }

            names = found.Count > MaxNames ? found[^MaxNames..] : found;
            return ClipFoldersState.Read;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClipFoldersState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or InvalidOperationException or ArgumentException)
        {
            return ClipFoldersState.Unreadable;
        }
    }

    /// <summary>
    /// Adds a name to the file when it is not in it yet. Never throws. A file that is not there is
    /// made. A file that is there and could not be read (<see cref="ClipFoldersState.Unreadable"/>)
    /// is left exactly as it is, the name is not added, and that is said once with fixed words: it
    /// may hold names that a hand edit has made unreadable, and they are not to be lost. A list
    /// that could not be written is reported with fixed words, and the name is simply tried again
    /// the next time.
    /// </summary>
    /// <param name="device">The name of the folder a store has just taken for writing.</param>
    /// <param name="report">Receives fixed words, and an exception for the log when a write failed. Never a name or a path.</param>
    public static void Remember(string file, string device, Action<string, Exception?>? report = null)
    {
        if (!IsFolderName(device))
        {
            return;
        }

        if (TryLoad(file, out var known) == ClipFoldersState.Unreadable)
        {
            report?.Invoke(NotUnderstood, null);
            return;
        }

        var names = new List<string>(known);
        if (names.Contains(device, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        names.Add(device);
        if (names.Count > MaxNames)
        {
            names.RemoveRange(0, names.Count - MaxNames);
        }

        string temporary = file + TemporarySuffix;
        try
        {
            string? folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, WriteOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("folders");
                    foreach (string name in names)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            report?.Invoke("The list of this machine's clipboard folders could not be written", ex);
            try
            {
                File.Delete(temporary);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // The next write replaces it.
            }
        }
    }
}
