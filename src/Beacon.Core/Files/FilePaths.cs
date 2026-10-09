namespace Beacon.Core.Files;

/// <summary>Path comparisons shared by the file index and its options. Case is ignored, as Windows does.</summary>
internal static class FilePaths
{
    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or anything inside it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "C:\data2" starts with "C:\data" but is not under it: the next character must be a separator.
        return path.Length == folder.Length
            || IsSeparator(folder[^1])
            || IsSeparator(path[folder.Length]);
    }

    public static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
