using System.Globalization;
using System.Security.Cryptography;

namespace Beacon.Core.ClipboardHistory;

/// <summary>
/// The names of the files that hold clipboard history. Under the clipboard folder each machine
/// has a folder of its own. In it there is one file of records for each day, such as
/// "2026-10-03.jsonl", and beside it a folder for the same day, "2026-10-03", for content that
/// is too large to sit in a record. A file in a day folder is named by the SHA-256 of its bytes,
/// so the same content copied twice in a day is one file, and by an extension that says what
/// it is.
/// </summary>
public static class ClipPaths
{
    /// <summary>The whole of a text that is too long to sit in its record, as UTF-8.</summary>
    public const string TextExtension = ".txt";

    /// <summary>The clipboard's "HTML Format", byte for byte.</summary>
    public const string HtmlExtension = ".html";

    /// <summary>The clipboard's "Rich Text Format", byte for byte.</summary>
    public const string RtfExtension = ".rtf";

    /// <summary>A picture that was on the clipboard, as PNG.</summary>
    public const string ImageExtension = ".png";

    private const string DayFileExtension = ".jsonl";
    private const string DayFormat = "yyyy-MM-dd";
    private const int HashLength = 64;

    /// <summary>The kinds of file that count as a picture when one is copied as a file. Lower case, with the dot.</summary>
    public static IReadOnlyList<string> PictureExtensions { get; } = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    /// <summary>The day a record belongs to: the date of its time in UTC.</summary>
    public static DateOnly DayOf(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    /// <summary>The name of the folder that holds a day's files, such as "2026-10-03".</summary>
    public static string DayName(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    /// <summary>The name of the file that holds a day's records, such as "2026-10-03.jsonl".</summary>
    public static string DayFileName(DateOnly day) => DayName(day) + DayFileExtension;

    /// <returns>True when the name is exactly that of a day's records file. Anything else in a machine's folder is left alone.</returns>
    public static bool TryParseDayFile(string fileName, out DateOnly day)
    {
        day = default;
        return fileName.EndsWith(DayFileExtension, StringComparison.Ordinal)
            && TryParseDayFolder(fileName[..^DayFileExtension.Length], out day);
    }

    /// <returns>True when the name is exactly that of a day's folder.</returns>
    public static bool TryParseDayFolder(string name, out DateOnly day) =>
        DateOnly.TryParseExact(name, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>The name for a file that holds these bytes: their SHA-256 in lower-case hex, then the extension.</summary>
    /// <param name="extension">Lower case, with its dot.</param>
    public static string BlobName(ReadOnlySpan<byte> content, string extension) =>
        Convert.ToHexStringLower(SHA256.HashData(content)) + extension;

    /// <summary>
    /// True when the name is 64 lower-case hex digits followed by exactly this extension. A name
    /// in a record comes from a file that another machine, or anything else, may have written,
    /// and it is joined to a folder to make a path: nothing but this shape is ever accepted, so
    /// a name can never point outside the day's folder.
    /// </summary>
    public static bool IsBlobName(string? name, string extension)
    {
        if (name is null || name.Length != HashLength + extension.Length || !name.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in name.AsSpan(0, HashLength))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the name is that of a kept picture: a hash and one of <see cref="PictureExtensions"/>.</summary>
    public static bool IsPictureName(string? name) => PictureExtensions.Any(extension => IsBlobName(name, extension));

    /// <summary>True when the name is that of any file this code puts in a day's folder.</summary>
    public static bool IsBlobName(string? name) =>
        IsBlobName(name, TextExtension) || IsBlobName(name, HtmlExtension) || IsBlobName(name, RtfExtension) || IsPictureName(name);

    /// <summary>
    /// The extension to keep a copied file's picture under, lower case with its dot, or null
    /// when the file is not one of the kinds that count as a picture. Only the name is looked at.
    /// </summary>
    public static string? PictureExtensionOf(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return PictureExtensions.Contains(extension) ? extension : null;
    }
}
