using Beacon.Core.ClipboardHistory;
using Beacon.Core.Files;
using Beacon.Core.Library;

namespace Beacon.Core.Diagnostics;

/// <summary>Turns values into text that is safe to write to the log.</summary>
public static class LogSafe
{
    private const string PathWithheld = "(path withheld)";
    private const string NameWithheld = "(name withheld)";
    private const string IdWithheld = "(id withheld)";

    /// <summary>
    /// Most item ids are fixed identifiers such as "command:quit" and can be logged as they are.
    /// A file item's id is its path, and file and folder names must not reach the log. A
    /// snippet's id and a script's id hold a file name, and a quicklink's id is the name the
    /// owner gave it, so those are withheld too. The id of an app is often a path as well, such
    /// as "app:C:\Program Files\Tool\tool.exe", so any id with something path-like after its
    /// prefix is withheld in the same way. The id of a clipboard entry says nothing by itself,
    /// but it names one thing the owner copied, in files that hold what it was, so it is
    /// withheld as well. Prefixes are compared without regard to case.
    /// </summary>
    public static string ItemId(string itemId)
    {
        if (itemId.StartsWith(FileProvider.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return FileProvider.IdPrefix + PathWithheld;
        }

        if (itemId.StartsWith(Snippet.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Snippet.IdPrefix + PathWithheld;
        }

        if (itemId.StartsWith(Script.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Script.IdPrefix + PathWithheld;
        }

        if (itemId.StartsWith(Quicklink.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Quicklink.IdPrefix + NameWithheld;
        }

        if (itemId.StartsWith(ClipEntry.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ClipEntry.IdPrefix + IdWithheld;
        }

        int colon = itemId.IndexOf(':');
        if (colon < 0)
        {
            return itemId.AsSpan().IndexOfAny('\\', '/') >= 0 ? PathWithheld : itemId;
        }

        // A colon, a backslash or a slash after the prefix means a drive or a folder follows it.
        return itemId.AsSpan(colon + 1).IndexOfAny('\\', '/', ':') >= 0
            ? itemId[..(colon + 1)] + PathWithheld
            : itemId;
    }
}
