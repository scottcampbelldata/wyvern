using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// One line of a day's file. Every record has an id of its own and the time it was written.
/// Every record type prints a short fixed form (its name and at most counts), not its members:
/// they are what the owner copied, and the names of programs and files.
/// </summary>
public abstract record ClipRecord(string Id, DateTimeOffset Time)
{
    /// <summary>The type's name only. Every record type of a line has a form of its own.</summary>
    public override string ToString() => nameof(ClipRecord);
}

/// <summary>A picture file that was copied as a file and whose content was kept.</summary>
/// <param name="Name">The file name it had, without its folder.</param>
/// <param name="File">The name of the file in the day's folder that holds its bytes.</param>
public sealed record KeptPicture(string Name, string File)
{
    /// <summary>The type's name only: the name is the owner's file name.</summary>
    public override string ToString() => nameof(KeptPicture);
}

/// <summary>
/// Something that was copied. It holds every form the clipboard offered at that moment. Short
/// text sits in the record; everything large is a file in the day's folder, named here.
/// </summary>
/// <param name="SourceApp">The program it was copied from, such as "chrome.exe", or null when that is not known.</param>
public sealed record EntryRecord(string Id, DateTimeOffset Time, string? SourceApp) : ClipRecord(Id, Time)
{
    /// <summary>The plain text, or its start when <see cref="TextFile"/> holds the whole of it. Null when no text was copied.</summary>
    public string? Text { get; init; }

    /// <summary>How many characters the whole text has.</summary>
    public int TextLength { get; init; }

    /// <summary>The file that holds the whole text when it is too long for the record, otherwise null.</summary>
    public string? TextFile { get; init; }

    /// <summary>The file that holds the clipboard's HTML, or null.</summary>
    public string? Html { get; init; }

    /// <summary>The file that holds the clipboard's rich text, or null.</summary>
    public string? Rtf { get; init; }

    /// <summary>The file that holds the clipboard's picture as PNG, or null.</summary>
    public string? Image { get; init; }

    /// <summary>The full paths of the files that were copied.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Those of the copied files that are pictures and whose content was kept.</summary>
    public IReadOnlyList<KeptPicture> Pictures { get; init; } = [];

    /// <summary>For a copied link, the words it had on the page, or null.</summary>
    public string? Label { get; init; }

    /// <summary>The type's name and how many files and pictures the record has, and nothing it holds.</summary>
    public override string ToString() =>
        FormattableString.Invariant($"{nameof(EntryRecord)} {{ Files = {Files.Count}, Pictures = {Pictures.Count} }}");

    /// <summary>Every file in the day's folder that this record names.</summary>
    public IEnumerable<string> FileNames()
    {
        foreach (string? name in (string?[])[TextFile, Html, Rtf, Image])
        {
            if (name is not null)
            {
                yield return name;
            }
        }

        foreach (var picture in Pictures)
        {
            yield return picture.File;
        }
    }
}

/// <summary>Says that the entry with the id <paramref name="Target"/> was deleted, on whichever machine it was copied.</summary>
public sealed record DeleteRecord(string Id, DateTimeOffset Time, string Target) : ClipRecord(Id, Time)
{
    /// <summary>The type's name only: an id names an entry, and the log withholds those.</summary>
    public override string ToString() => nameof(DeleteRecord);
}

/// <summary>Says that every entry older than <paramref name="Before"/> was cleared, on every machine.</summary>
public sealed record ClearRecord(string Id, DateTimeOffset Time, DateTimeOffset Before) : ClipRecord(Id, Time)
{
    /// <summary>The type's name only.</summary>
    public override string ToString() => nameof(ClearRecord);
}

/// <summary>
/// Reads and writes one line of a day's file: one JSON object. A plain text entry and a picture
/// entry are written exactly as the first design of the format had them, so anything that
/// knows only "text" and "image" still reads those; the other forms are further keys.
/// </summary>
/// <remarks>
/// A day's file can come from the owner's other machine, and before that from a server, so a
/// line is trusted for nothing. A line that is not exactly what is expected is not read at all:
/// in particular a file name that is not a hash and a known extension, which could otherwise
/// name a file outside the day's folder.
/// </remarks>
public static class ClipLine
{
    /// <summary>An id is a GUID, 36 characters. Anything much longer is not one of ours.</summary>
    public const int MaxIdLength = 100;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    // What is accepted when reading: with or without a fraction of a second, and with "Z" or an
    // offset. A time with neither is taken as UTC.
    private static readonly string[] ReadFormats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // The default writes every letter outside ASCII as an escape of six characters, which
        // would make text in most languages six times its size. A day's file is not a web page,
        // so letters are written as themselves. This writer still escapes what JSON requires,
        // a few characters that some readers take for a line break, and every character
        // outside the basic plane, such as an emoji, which it writes as two escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>How a time is written: UTC, whole seconds, such as "2026-10-03T14:02:11Z".</summary>
    public static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The word in a record's "kind": for an entry, the first form it has out of text, image,
    /// files, html and rtf.
    /// </summary>
    public static string Kind(ClipRecord record) => record switch
    {
        DeleteRecord => "delete",
        ClearRecord => "clear",
        EntryRecord { Text: not null } or EntryRecord { TextFile: not null } => "text",
        EntryRecord { Image: not null } => "image",
        EntryRecord { Files.Count: > 0 } or EntryRecord { Pictures.Count: > 0 } => "files",
        EntryRecord { Html: not null } => "html",
        _ => "rtf",
    };

    /// <returns>The record, or null when the line is not one this code understands.</returns>
    public static ClipRecord? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryText(root, "id", out string? id) || id is not { Length: > 0 and <= MaxIdLength }
                || !TryTime(root, "ts", out var time)
                || !TryText(root, "kind", out string? kind))
            {
                return null;
            }

            switch (kind)
            {
                case "delete":
                    return TryText(root, "target", out string? target) && target is { Length: > 0 and <= MaxIdLength }
                        ? new DeleteRecord(id, time, target)
                        : null;
                case "clear":
                    return TryTime(root, "before", out var before) ? new ClearRecord(id, time, before) : null;
                case "text" or "image" or "files" or "html" or "rtf":
                    return ParseEntry(root, id, time);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Text that holds half of a surrogate pair written as an escape is valid JSON, but
            // reading it throws this. It is one more line that cannot be read.
            return null;
        }
        catch (ArgumentException)
        {
            // Half of a surrogate pair as a character in the line itself: it cannot even be
            // handed to the JSON reader. A line from a file never holds one, because reading
            // the file replaces it, but this method must not throw for any text.
            return null;
        }
    }

    /// <summary>The record as one line of JSON, without a line break.</summary>
    public static string Write(ClipRecord record)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Safe(record.Id));
            writer.WriteString("ts", FormatTime(record.Time));
            writer.WriteString("kind", Kind(record));
            switch (record)
            {
                case DeleteRecord delete:
                    writer.WriteString("target", Safe(delete.Target));
                    break;
                case ClearRecord clear:
                    writer.WriteString("before", FormatTime(clear.Before));
                    break;
                case EntryRecord entry:
                    WriteEntry(writer, entry);
                    break;
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteEntry(Utf8JsonWriter writer, EntryRecord entry)
    {
        if (entry.Text is not null)
        {
            writer.WriteString("text", Safe(entry.Text));
        }

        if (entry.TextFile is not null)
        {
            writer.WriteString("textFile", entry.TextFile);
            writer.WriteNumber("textLength", entry.TextLength);
        }

        WriteOptional(writer, "html", entry.Html);
        WriteOptional(writer, "rtf", entry.Rtf);
        WriteOptional(writer, "image", entry.Image);
        if (entry.Files.Count > 0)
        {
            writer.WriteStartArray("files");
            foreach (string file in entry.Files)
            {
                writer.WriteStringValue(Safe(file));
            }

            writer.WriteEndArray();
        }

        if (entry.Pictures.Count > 0)
        {
            writer.WriteStartArray("pictures");
            foreach (var picture in entry.Pictures)
            {
                writer.WriteStartObject();
                writer.WriteString("name", Safe(picture.Name));
                writer.WriteString("file", picture.File);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        WriteOptional(writer, "label", entry.Label);
        WriteOptional(writer, "sourceApp", entry.SourceApp);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, Safe(value));
        }
    }

    // Half of a surrogate pair with no partner cannot be written as JSON. Text from the
    // clipboard has been made whole before it gets here; this is for everything else.
    private static string Safe(string value) => ClipboardBytes.WellFormed(value);

    private static EntryRecord? ParseEntry(JsonElement root, string id, DateTimeOffset time)
    {
        if (!TryText(root, "text", out string? text)
            || !TryName(root, "textFile", name => ClipPaths.IsBlobName(name, ClipPaths.TextExtension), out string? textFile)
            || !TryName(root, "html", name => ClipPaths.IsBlobName(name, ClipPaths.HtmlExtension), out string? html)
            || !TryName(root, "rtf", name => ClipPaths.IsBlobName(name, ClipPaths.RtfExtension), out string? rtf)
            || !TryName(root, "image", name => ClipPaths.IsBlobName(name, ClipPaths.ImageExtension), out string? image)
            || !TryFiles(root, out var files)
            || !TryPictures(root, out var pictures)
            || !TryText(root, "label", out string? label)
            || !TryText(root, "sourceApp", out string? sourceApp)
            || !TryLength(root, text, out int textLength))
        {
            return null;
        }

        // A text that is all in a file still counts as text: its start is simply not known here.
        if (text is null && textFile is not null)
        {
            text = "";
        }

        if (text is null && html is null && rtf is null && image is null && files.Count == 0 && pictures.Count == 0)
        {
            return null;
        }

        return new EntryRecord(id, time, sourceApp)
        {
            Text = text,
            TextLength = textLength,
            TextFile = textFile,
            Html = html,
            Rtf = rtf,
            Image = image,
            Files = files,
            Pictures = pictures,
            Label = label,
        };
    }

    /// <returns>False when the key is there and is not text. A key that is missing or null gives null text.</returns>
    private static bool TryText(JsonElement root, string name, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
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

    /// <returns>False when the key is there and is not a file name of the shape <paramref name="isName"/> accepts.</returns>
    private static bool TryName(JsonElement root, string name, Func<string, bool> isName, out string? fileName) =>
        TryText(root, name, out fileName) && (fileName is null || isName(fileName));

    private static bool TryTime(JsonElement root, string name, out DateTimeOffset time)
    {
        time = default;
        return TryText(root, name, out string? text)
            && text is not null
            && DateTimeOffset.TryParseExact(
                text,
                ReadFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out time);
    }

    // The whole text's length is written only when the text is in a file. Without it, or with a
    // number that cannot be right, the length is that of the text in the record.
    private static bool TryLength(JsonElement root, string? text, out int length)
    {
        length = text?.Length ?? 0;
        if (!root.TryGetProperty("textLength", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetInt32(out int written) && written > length)
        {
            length = written;
        }

        return true;
    }

    private static bool TryFiles(JsonElement root, out IReadOnlyList<string> files)
    {
        files = [];
        if (!root.TryGetProperty("files", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var found = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (item.GetString() is { Length: > 0 } path)
            {
                found.Add(path);
            }
        }

        files = found;
        return true;
    }

    private static bool TryPictures(JsonElement root, out IReadOnlyList<KeptPicture> pictures)
    {
        pictures = [];
        if (!root.TryGetProperty("pictures", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var found = new List<KeptPicture>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryText(item, "name", out string? name) || string.IsNullOrEmpty(name)
                || !TryName(item, "file", ClipPaths.IsPictureName, out string? file) || file is null)
            {
                return false;
            }

            found.Add(new KeptPicture(name, file));
        }

        pictures = found;
        return true;
    }
}
