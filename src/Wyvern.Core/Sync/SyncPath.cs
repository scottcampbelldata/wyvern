using System.Globalization;
using System.Text.RegularExpressions;
using Wyvern.Core.ClipboardHistory;

namespace Wyvern.Core.Sync;

/// <summary>What a path under the clipboard folder says: whose folder it is in, and which day it belongs to.</summary>
/// <param name="Device">The machine folder's name, as written in the path.</param>
/// <param name="Day">The day of a day file, or of a file in a day folder. Null for any other file in a machine's folder.</param>
public readonly record struct ClipboardPath(string Device, DateOnly? Day)
{
    /// <summary>The type's name and nothing else: a machine's folder name is not printed.</summary>
    public override string ToString() => nameof(ClipboardPath);
}

/// <summary>
/// The rules for the paths that sync works with, each in one place: what a path is, which
/// files are synced, how a path is compared, and what a conflict copy is called.
/// </summary>
/// <remarks>
/// A path here is relative to the data folder and written with forward slashes, such as
/// <c>library/snippets/sig.md</c>. A path can come out of a blob, which is to say from another
/// machine by way of the server, and it is joined to the data folder to make the name of a file
/// that is then written or deleted. So <see cref="TryParse"/> accepts nothing but plain names
/// between single slashes: no drive, no root, no <c>..</c>, no name Windows keeps for a device,
/// no colon (which would name an alternate data stream), nothing that Windows would quietly
/// change when the file is made.
/// </remarks>
public static partial class SyncPath
{
    private const string ClipboardFolder = "clipboard";
    private const string ConflictMark = ".conflict-";
    private const string ConflictTimeFormat = "yyyyMMdd-HHmmss";

    // The stem of a conflict copy is cut to this at most, so that the whole name stays a name Windows allows.
    private const int MaxConflictStem = 120;

    // What follows the last dot of a name, the dot included, is an extension up to this long. A
    // longer one is part of the stem: it is not an extension, and it is not bounded by anything.
    private const int MaxConflictExtension = 16;

    /// <summary>
    /// True when the text is a relative path that is safe to join to the data folder: at most
    /// <see cref="SyncLimits.MaxPathLength"/> characters, names separated by single forward
    /// slashes, and every name one that <see cref="ClipboardOptions.SafeFileName"/> would leave
    /// exactly as it is. That one rule refuses a control character, any of
    /// <c>&lt; &gt; : " / \ | ? *</c>, half of a surrogate pair, white space at either end, a dot
    /// or a space at the end (so also <c>.</c> and <c>..</c>), and the names Windows keeps for
    /// devices.
    /// </summary>
    /// <param name="path">The same text when it is accepted, otherwise empty.</param>
    public static bool TryParse(string? text, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(text) || text.Length > SyncLimits.MaxPathLength)
        {
            return false;
        }

        foreach (var name in text.AsSpan().Split('/'))
        {
            string part = text[name];
            if (part.Length == 0
                || part.Length > SyncLimits.MaxNameLength
                || ClipboardOptions.SafeFileName(part, SyncLimits.MaxNameLength) != part)
            {
                return false;
            }
        }

        path = text;
        return true;
    }

    /// <summary>
    /// What two spellings of one file have in common: the path in lower case. Windows takes
    /// <c>Sig.md</c> and <c>sig.md</c> for one file, so sync does too: this is what a blob is
    /// named from and what every table of paths is keyed by. A blob's header keeps the path as
    /// it was spelt.
    /// </summary>
    /// <remarks>
    /// The lower-casing does not depend on the culture, but it does depend on the Unicode data of
    /// the runtime and of Windows. A letter that Unicode added after the data of one machine was
    /// made may be lower-cased by another machine and not by this one, so that two machines name
    /// the same path differently. A blob that is then opened on the other machine is refused
    /// (the name it is stored under is not the one its path gives there), not put in the wrong
    /// file.
    /// </remarks>
    public static string Key(string path) => path.ToLowerInvariant();

    /// <summary>
    /// True when a file with this path is synced. Not synced: a name that ends in <c>.tmp</c>
    /// (a file that is being written) or starts with <c>~$</c> or <c>.~</c> (a lock file), in
    /// any folder of the path as well as the file's own name.
    /// </summary>
    public static bool IsSynced(string path)
    {
        foreach (var name in path.AsSpan().Split('/'))
        {
            var part = path.AsSpan(name);
            if (part.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("~$", StringComparison.Ordinal)
                || part.StartsWith(".~", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The full name of the file a path stands for. The path must have passed <see cref="TryParse"/>.</summary>
    public static string ToLocal(string dataRoot, string path) =>
        Path.Combine(dataRoot, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Whose clipboard folder a path is in, and its day, or null for a path that is not in any
    /// machine's clipboard folder (the library, and a file directly under <c>clipboard</c>).
    /// </summary>
    public static ClipboardPath? Clipboard(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length < 3 || !parts[0].Equals(ClipboardFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (parts.Length == 3)
        {
            return new ClipboardPath(parts[1], ClipPaths.TryParseDayFile(parts[2], out var fileDay) ? fileDay : null);
        }

        return new ClipboardPath(parts[1], ClipPaths.TryParseDayFolder(parts[2], out var folderDay) ? folderDay : null);
    }

    /// <summary>
    /// The path a conflict copy is saved under, beside the file it is a copy of:
    /// <c>&lt;name&gt;.conflict-&lt;device&gt;-&lt;time&gt;&lt;extension&gt;</c>, with the time in UTC as
    /// <c>yyyyMMdd-HHmmss</c>. The extension is kept so that the copy opens in the same editor,
    /// unless it is longer than 16 characters (the dot included): that is not an extension, and
    /// the whole name is then the stem. The stem is cut so that the copy keeps to
    /// <see cref="SyncLimits.MaxNameLength"/> and <see cref="SyncLimits.MaxPathLength"/>, and may
    /// be cut to nothing, so that the name of the copy starts with the mark.
    /// </summary>
    /// <param name="path">A path that <see cref="TryParse"/> accepts.</param>
    /// <param name="device">This machine's name, already safe as a folder name.</param>
    /// <param name="number">1 for the first name to try. A higher number gives the same name with <c>-2</c>, <c>-3</c> and so on, for when a name is taken.</param>
    /// <returns>
    /// A path that <see cref="TryParse"/> accepts, in the folder of <paramref name="path"/>; or
    /// null when no such name can be made: the folder and the device's name leave no room for
    /// one, or what they make is not a name Windows allows. Null does not mean that a name is
    /// taken, and a caller that gets it has no copy to make.
    /// </returns>
    public static string? ConflictCopy(string path, string device, DateTimeOffset time, int number = 1)
    {
        int slash = path.LastIndexOf('/');
        string folder = path[..(slash + 1)];
        string name = path[(slash + 1)..];
        string extension = Path.GetExtension(name);
        if (extension.Length > MaxConflictExtension)
        {
            extension = "";
        }

        string stem = name[..^extension.Length];

        string stamp = time.UtcDateTime.ToString(ConflictTimeFormat, CultureInfo.InvariantCulture);
        string count = number > 1 ? "-" + number.ToString(CultureInfo.InvariantCulture) : "";
        string fixedPart = $"{ConflictMark}{device}-{stamp}{count}{extension}";

        int room = Math.Min(
            MaxConflictStem,
            Math.Min(SyncLimits.MaxNameLength - fixedPart.Length, SyncLimits.MaxPathLength - folder.Length - fixedPart.Length));
        if (room < 0)
        {
            return null;
        }

        if (stem.Length > room)
        {
            stem = stem[..room].TrimEnd('.', ' ');
            if (stem.Length > 0 && char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }
        }

        // Whatever the input was, what is given back is a path that TryParse accepts.
        string copy = folder + stem + fixedPart;
        return TryParse(copy, out _) ? copy : null;
    }

    /// <summary>
    /// True when a file name is that of a conflict copy. The library does not load such a file:
    /// it would be a second snippet or script with the same title as the one it is a copy of.
    /// </summary>
    public static bool IsConflictCopy(string fileName) => ConflictCopyName().IsMatch(fileName);

    // ".conflict-", a device name, "-", eight digits, "-", six digits, an optional "-" and
    // number, and an optional extension, at the very end of the name. The digits are 0 to 9
    // and nothing else (\d also takes the digits of other scripts), and \z is the end of the
    // text (while $ also stands before a line feed that ends it).
    [GeneratedRegex(@"\.conflict-.+-[0-9]{8}-[0-9]{6}(-[0-9]+)?(\.[^.]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictCopyName();
}
