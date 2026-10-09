using System.Security;
using System.Text;
using System.Text.Json;
using Beacon.Core.Files;
using Beacon.Core.Settings;
using Beacon.Core.Sync;

namespace Beacon.Core.Library;

/// <summary>Everything read from the library folder at one moment. It never changes once made.</summary>
/// <param name="BadFiles">
/// Full paths of the files that could not be used, or were used only in part, sorted. They are
/// file names: show them on screen, count them in the log.
/// </param>
public sealed record LibrarySnapshot(
    IReadOnlyList<Quicklink> Quicklinks,
    IReadOnlyList<Snippet> Snippets,
    IReadOnlyList<string> BadFiles)
{
    public static LibrarySnapshot Empty { get; } = new([], [], []);

    /// <summary>
    /// The scripts, in path order. Set by name and empty unless set, so that code which makes a
    /// snapshot without scripts reads as it did before there were any.
    /// </summary>
    public IReadOnlyList<Script> Scripts { get; init; } = [];

    /// <summary>
    /// True when a file could not be opened or read (another program has it locked, or the disk
    /// failed for a moment), as opposed to being malformed or too large. Such a file may well be
    /// readable a moment later, and when it is nothing on disk has changed, so nothing says so.
    /// </summary>
    public bool HasUnreadableFiles { get; init; }
}

/// <summary>Reads the library folder into memory. This is the only code that reads library files.</summary>
public static class LibraryLoader
{
    /// <summary>A quicklinks file or a snippet larger than this is not read.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly IReadOnlySet<string> NothingExcluded = new HashSet<string>();

    /// <summary>
    /// Reads quicklinks.json, every .md and .txt file under the snippets folder, and the first
    /// lines of every script under the scripts folder, at any depth. A file that is malformed,
    /// too large or locked is left out and listed in <see cref="LibrarySnapshot.BadFiles"/>,
    /// and the rest still load. A quicklinks.json with some unusable entries keeps the others
    /// and is listed too. A missing file or folder is not a problem: it holds nothing. Hidden
    /// and system files are left out, and folder links are not entered, as in the file index.
    /// A conflict copy that sync saved beside a snippet or a script (a file named
    /// <c>&lt;name&gt;.conflict-&lt;device&gt;-&lt;time&gt;&lt;extension&gt;</c>) is not loaded and is not a bad
    /// file: it has the same title as the file it is a copy of, and two commands with one title
    /// could not be told apart. It stays in the folder for the owner to compare and delete.
    /// </summary>
    public static LibrarySnapshot Load(DataPaths paths)
    {
        var badFiles = new List<string>();
        bool unreadable = false;
        var quicklinks = LoadQuicklinks(paths.QuicklinksFile, badFiles, ref unreadable);
        var snippets = LoadSnippets(paths.SnippetsDir, badFiles, ref unreadable);
        var scripts = LoadScripts(paths.ScriptsDir, badFiles, ref unreadable);
        badFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return new LibrarySnapshot(quicklinks, snippets, badFiles) { Scripts = scripts, HasUnreadableFiles = unreadable };
    }

    private static IReadOnlyList<Quicklink> LoadQuicklinks(string file, List<string> badFiles, ref bool unreadable)
    {
        if (!TryRead(file, out string? text, ref unreadable))
        {
            badFiles.Add(file);
            return [];
        }

        if (text is null)
        {
            return [];
        }

        try
        {
            var parsed = QuicklinkFile.Parse(text);
            if (parsed.Skipped > 0)
            {
                badFiles.Add(file);
            }

            return parsed.Quicklinks;
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            badFiles.Add(file);
            return [];
        }
    }

    private static IReadOnlyList<Snippet> LoadSnippets(string folder, List<string> badFiles, ref bool unreadable)
    {
        var snippets = new List<Snippet>();
        var files = FileScanner.Scan(folder, NothingExcluded, CancellationToken.None)
            .Where(entry => !entry.IsDirectory && SnippetFile.HasSnippetExtension(entry.Name) && !SyncPath.IsConflictCopy(entry.Name))
            .Select(entry => entry.FullPath)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (!TryRead(file, out string? text, ref unreadable))
            {
                badFiles.Add(file);
            }
            else if (text is not null)
            {
                if (TryParseSnippet(text, folder, file) is { } snippet)
                {
                    snippets.Add(snippet);
                }
                else
                {
                    badFiles.Add(file);
                }
            }
        }

        return snippets;
    }

    // A script can be any size, because only its first lines are read. That keeps a reload
    // quick however many scripts there are and however long they are.
    private static IReadOnlyList<Script> LoadScripts(string folder, List<string> badFiles, ref bool unreadable)
    {
        var scripts = new List<Script>();
        var files = FileScanner.Scan(folder, ScriptFile.SkippedFolders, CancellationToken.None)
            .Where(entry => !entry.IsDirectory && ScriptFile.HasScriptExtension(entry.Name) && !SyncPath.IsConflictCopy(entry.Name))
            .Select(entry => entry.FullPath)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (!TryReadHeader(file, out var header, ref unreadable))
            {
                badFiles.Add(file);
            }
            else if (header is not null)
            {
                if (TryParseScript(header, folder, file) is { } script)
                {
                    scripts.Add(script);
                }
                else
                {
                    badFiles.Add(file);
                }
            }
        }

        return scripts;
    }

    /// <returns>Null when the file cannot be used as a script, however the parser says so.</returns>
    private static Script? TryParseScript(IReadOnlyList<string> header, string folder, string file)
    {
        try
        {
            return ScriptFile.Parse(header, Path.GetRelativePath(folder, file), file);
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            return null;
        }
    }

    /// <returns>Null when the file cannot be used as a snippet, however the parser says so.</returns>
    private static Snippet? TryParseSnippet(string text, string folder, string file)
    {
        try
        {
            return SnippetFile.Parse(text, Path.GetRelativePath(folder, file), file);
        }
        catch (Exception ex) when (IsParseFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The parsers promise to report a bad file in one way only, but the loader's promise is that
    /// one bad file never stops the others loading. It does not rest on the parsers being perfect:
    /// text that cannot be read as JSON or as text is a bad file whichever exception says so.
    /// </summary>
    private static bool IsParseFailure(Exception exception) =>
        exception is JsonException or InvalidOperationException or ArgumentException;

    /// <returns>
    /// False when the file is there but cannot be used: too large, locked or unreadable.
    /// True otherwise, with <paramref name="text"/> null when the file does not exist.
    /// </returns>
    /// <param name="unreadable">
    /// Set to true when the file could not be opened or read. A file that is too large is not
    /// that: it is no more readable a moment later, so reading it again would be wasted.
    /// </param>
    private static bool TryRead(string file, out string? text, ref bool unreadable)
    {
        text = null;
        try
        {
            using var stream = OpenShared(file);
            if (stream.Length > MaxFileBytes)
            {
                return false;
            }

            // UTF-8, with or without a byte order mark.
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            unreadable = true;
            return false;
        }
    }

    /// <returns>
    /// False when the script is there but cannot be opened or read. True otherwise, with
    /// <paramref name="header"/> null when the file does not exist.
    /// </returns>
    private static bool TryReadHeader(string file, out IReadOnlyList<string>? header, ref bool unreadable)
    {
        header = null;
        try
        {
            using var stream = OpenShared(file);

            // UTF-8, with or without a byte order mark. Only the top of the file is read.
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            header = ScriptFile.ReadHeader(reader);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            unreadable = true;
            return false;
        }
    }

    // Opened so that an editor which still has the file open does not get in the way.
    private static FileStream OpenShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
