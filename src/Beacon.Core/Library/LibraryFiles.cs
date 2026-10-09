using Beacon.Core.Settings;

namespace Beacon.Core.Library;

/// <summary>Creates the library's folders and the files the owner then edits by hand.</summary>
public static class LibraryFiles
{
    /// <summary>
    /// What a new quicklinks.json holds: an empty list with one example that is commented out,
    /// so the format can be seen and nothing points anywhere until the owner writes it.
    /// </summary>
    public const string QuicklinksTemplate =
        "[\r\n" +
        "  // { \"name\": \"Search GitHub\", \"keyword\": \"gh\", \"link\": \"https://github.com/search?q={query}\" }\r\n" +
        "]\r\n";

    /// <summary>What a new snippet file holds.</summary>
    public const string SnippetTemplate =
        "---\r\n" +
        "name: New snippet\r\n" +
        "keyword:\r\n" +
        "---\r\n" +
        "Replace this line with the text to paste.\r\n";

    // Far more unfinished snippets than anyone keeps. It only stops the search for a free name.
    private const int MaxNewSnippets = 1000;

    /// <summary>Creates the library folder, and the snippets and scripts folders inside it, when they are missing.</summary>
    /// <exception cref="IOException">A folder could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">A folder could not be created.</exception>
    public static void EnsureFolders(DataPaths paths)
    {
        Directory.CreateDirectory(paths.SnippetsDir);
        Directory.CreateDirectory(paths.ScriptsDir);
    }

    /// <summary>Creates quicklinks.json from <see cref="QuicklinksTemplate"/> unless it is already there.</summary>
    /// <returns>The file's path, for opening it in an editor.</returns>
    /// <exception cref="IOException">The folder or the file could not be created or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or the file could not be created or written.</exception>
    public static string EnsureQuicklinksFile(DataPaths paths)
    {
        Directory.CreateDirectory(paths.LibraryDir);
        TryCreate(paths.QuicklinksFile, QuicklinksTemplate);
        return paths.QuicklinksFile;
    }

    /// <summary>
    /// Creates new-snippet.md from <see cref="SnippetTemplate"/>, or new-snippet-2.md,
    /// new-snippet-3.md and so on when the earlier names are taken. A file that exists is never
    /// overwritten.
    /// </summary>
    /// <returns>The new file's path, for opening it in an editor.</returns>
    /// <exception cref="IOException">The folder or the file could not be created or written, or every name is taken.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or the file could not be created or written.</exception>
    public static string CreateSnippet(DataPaths paths)
    {
        Directory.CreateDirectory(paths.SnippetsDir);
        for (int number = 1; number <= MaxNewSnippets; number++)
        {
            string name = number == 1 ? "new-snippet.md" : $"new-snippet-{number}.md";
            string file = Path.Combine(paths.SnippetsDir, name);
            if (TryCreate(file, SnippetTemplate))
            {
                return file;
            }
        }

        throw new IOException("Every name for a new snippet is already taken.");
    }

    /// <returns>False when the file is already there; it is left as it is.</returns>
    private static bool TryCreate(string file, string content)
    {
        FileStream stream;
        try
        {
            // CreateNew refuses to open a file that exists, so two attempts at the same moment
            // cannot both write it. Only this step says "the name is taken": a failure to write
            // afterwards is a real failure, even though the file is there by then.
            stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(file))
        {
            return false;
        }

        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }

            return true;
        }
        catch
        {
            // The file is closed by now. A half-written file must not stay behind to be mistaken
            // for the owner's own, so it is removed and the failure is passed on.
            TryDelete(file);
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
            // Nothing more can be done; the failure that matters is the one being passed on.
        }
    }
}
