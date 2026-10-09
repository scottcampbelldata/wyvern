using System.Text.Json;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// Whether clipboard capture is paused, as remembered on this machine between runs. It is kept
/// in a small file of its own beside settings.json, because the app never rewrites
/// settings.json and this is something the app itself changes.
/// </summary>
/// <param name="Paused">True when nothing is to be recorded.</param>
/// <param name="Unreadable">
/// True when the file is there, or a save of it did not finish, and it could not be understood.
/// Capture is then paused: the file may have said "paused", and recording what the owner copies
/// against their wish is the worse mistake of the two.
/// </param>
public sealed record ClipboardState(bool Paused, bool Unreadable)
{
    // Far more than the file ever holds. A larger file is not this file.
    private const long MaxBytes = 64 * 1024;

    private const string Key = "capturePaused";
    private const string PausedContent = "{ \"capturePaused\": true }\r\n";
    private const string RunningContent = "{ \"capturePaused\": false }\r\n";
    private const string TemporarySuffix = ".tmp";

    // The UTF-8 byte order mark, written as its three bytes: a character that cannot be seen
    // does not belong inside a string literal.
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads the file. Capture is on only in two cases: there is no file, which is how a new
    /// installation starts and which is decided only when opening it says it is not there; and
    /// the file is a JSON object whose "capturePaused" is false. Anything else means paused and
    /// unreadable: text that is not JSON, a value that is not a plain true or false, an object
    /// without that exact key (or with it twice), a file that cannot be opened or read for any
    /// other reason than that it is not there, and a temporary file left beside it, which says
    /// that a save did not finish. Never throws.
    /// </summary>
    public static ClipboardState Load(string file)
    {
        try
        {
            // A save that did not finish leaves its temporary file. What it was going to say
            // is not known, and not known is paused.
            if (Path.Exists(file + TemporarySuffix))
            {
                return new ClipboardState(Paused: true, Unreadable: true);
            }

            byte[] content;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxBytes)
                {
                    return new ClipboardState(Paused: true, Unreadable: true);
                }

                content = new byte[stream.Length];
                stream.ReadExactly(content);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The only answer that means there is no file. Any other failure to open the
                // file is not that: a folder that cannot be reached looks the same to a check
                // of whether the file exists.
                return new ClipboardState(Paused: false, Unreadable: false);
            }

            // Windows PowerShell writes a byte order mark; a person may too.
            ReadOnlyMemory<byte> text = content;
            if (text.Span.StartsWith(ByteOrderMark))
            {
                text = text[ByteOrderMark.Length..];
            }

            using var document = JsonDocument.Parse(text, ReadOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ClipboardState(Paused: true, Unreadable: true);
            }

            JsonElement? paused = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(Key))
                {
                    if (paused is not null)
                    {
                        // Said twice: which one counts is in doubt.
                        return new ClipboardState(Paused: true, Unreadable: true);
                    }

                    paused = property.Value;
                }
            }

            return paused?.ValueKind switch
            {
                JsonValueKind.True => new ClipboardState(Paused: true, Unreadable: false),
                JsonValueKind.False => new ClipboardState(Paused: false, Unreadable: false),
                _ => new ClipboardState(Paused: true, Unreadable: true),
            };
        }
        catch (Exception)
        {
            // Whatever it was, it is in doubt, and in doubt capture is paused. This is a gate
            // that fails closed, so every failure is caught.
            return new ClipboardState(Paused: true, Unreadable: true);
        }
    }

    /// <summary>
    /// Writes the file: to a temporary file beside it, which then takes its place in one step,
    /// so that the file is never found half written. When the step fails the temporary file is
    /// left, and <see cref="Load"/> takes that to mean that a save did not finish; a later save
    /// that succeeds removes it.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public static void Save(string file, bool paused)
    {
        string? folder = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = file + TemporarySuffix;
        File.WriteAllText(temporary, paused ? PausedContent : RunningContent);
        File.Move(temporary, file, overwrite: true);
    }
}
