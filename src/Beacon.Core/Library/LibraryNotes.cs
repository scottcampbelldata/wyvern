using Beacon.Core.Platform;

namespace Beacon.Core.Library;

/// <summary>
/// Sentences about the library for a notification. They name files, which are allowed on
/// screen for a library file that could not be used. They must never be logged.
/// </summary>
public static class LibraryNotes
{
    /// <summary>How many files a notification names before it only counts the rest.</summary>
    public const int MaxNamedFiles = 3;

    // A notification holds about 250 characters. A file name can be longer than that by itself.
    private const int MaxNameLength = 40;

    /// <summary>One sentence naming the files that could not be used: file names only, never folders.</summary>
    /// <param name="badFiles">Full paths, at least one.</param>
    public static string BadFiles(IReadOnlyList<string> badFiles)
    {
        string names = string.Join(", ", badFiles.Take(MaxNamedFiles).Select(file => Shorten(Path.GetFileName(file))));
        int more = badFiles.Count - MaxNamedFiles;
        return more > 0
            ? $"Some of the library could not be loaded. Check: {names} and {more} more."
            : $"Some of the library could not be loaded. Check: {names}.";
    }

    // A file name can hold characters that show nothing or turn the text around, and it can be
    // longer than a notification. Both are dealt with a whole character at a time.
    private static string Shorten(string name) =>
        NoteText.Shorten(NoteText.WithoutInvisibleCharacters(name), MaxNameLength);
}
