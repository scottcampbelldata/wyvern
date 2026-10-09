namespace Beacon.Core.Library;

/// <summary>
/// Reads what a script says about itself in the comment lines at its top:
/// <code>
/// # @beacon.title Restart Docker
/// # @beacon.mode output
/// # @beacon.argument Container name
/// </code>
/// </summary>
public static class ScriptFile
{
    /// <summary>Only this many lines at the top of a file are looked at.</summary>
    public const int HeaderLines = 20;

    /// <summary>
    /// Reading a file stops after this many characters even if fewer than
    /// <see cref="HeaderLines"/> lines have been seen, so that a file which is one enormous line
    /// (a minified .js file) is not read whole.
    /// </summary>
    public const int MaxHeaderChars = 8192;

    /// <summary>The prompt of a script whose "@beacon.argument" line gives none.</summary>
    public const string DefaultArgumentPrompt = "Argument";

    /// <summary>The kinds of file that are scripts.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".ps1", ".py", ".cmd", ".bat", ".js"];

    /// <summary>
    /// Folders under the scripts folder that are not looked into. They hold what a tool
    /// downloaded or generated, which can be thousands of files and none of them a command the
    /// owner wrote. A Python virtual environment beside the owner's scripts is one: every
    /// package under its "site-packages" folder would be a command, and so would its
    /// "activate" scripts. It is usually called ".venv" or "venv", and "site-packages" is
    /// skipped as well for one that is called something else.
    /// </summary>
    public static readonly IReadOnlySet<string> SkippedFolders =
        new HashSet<string>(["node_modules", ".git", "__pycache__", ".venv", "venv", "site-packages"], StringComparer.OrdinalIgnoreCase);

    private const string Marker = "@beacon.";

    /// <summary>True for a file name ending in one of <see cref="Extensions"/>, in any case.</summary>
    public static bool HasScriptExtension(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a .cmd or .bat file, in any case: the kinds that cmd runs.</summary>
    public static bool IsBatch(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a .ps1 file, in any case.</summary>
    public static bool IsPowerShell(string fileName) =>
        Path.GetExtension(fileName).Equals(".ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first lines of a file, without their line breaks: at most <see cref="HeaderLines"/>
    /// of them, and nothing beyond the first <see cref="MaxHeaderChars"/> characters. Nothing
    /// more is taken from the reader than that. A line that the character limit cuts short is
    /// left out, so that no title or prompt is ever half of what was written.
    /// </summary>
    public static IReadOnlyList<string> ReadHeader(TextReader reader)
    {
        char[] buffer = new char[MaxHeaderChars];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        bool more = read == buffer.Length && reader.Peek() >= 0;

        var lines = new List<string>();
        int position = 0;
        while (position < read && lines.Count < HeaderLines)
        {
            int end = Array.FindIndex(buffer, position, read - position, c => c is '\r' or '\n');
            if (end < 0)
            {
                // The last line read. When there is more of the file than was read, this line
                // goes on beyond the limit and is not whole.
                if (!more)
                {
                    lines.Add(new string(buffer, position, read - position));
                }

                break;
            }

            lines.Add(new string(buffer, position, end - position));

            // "\r\n" is one line break, not two.
            position = buffer[end] == '\r' && end + 1 < read && buffer[end + 1] == '\n' ? end + 2 : end + 1;
        }

        return lines;
    }

    /// <summary>
    /// A line counts when, after any spaces, it starts with a comment mark ("#", "//", "::", or
    /// "REM" followed by a space or a tab, in any case) and then "@beacon." and a key. The keys
    /// "title", "mode" and "argument" are understood, in any case, and any other key is ignored.
    /// Without a title the title is the file name without its extension. Without a mode the
    /// mode is silent. "@beacon.argument" makes the script take an argument, and the rest of
    /// that line is the prompt. When a key is given twice the later line counts.
    /// </summary>
    /// <param name="header">The first lines of the file, from <see cref="ReadHeader"/>. Lines beyond <see cref="HeaderLines"/> are not looked at.</param>
    /// <param name="relativePath">The file's path under the scripts folder.</param>
    /// <param name="filePath">The file's full path. It is kept as the script's <see cref="Script.FilePath"/> and is not looked at here.</param>
    /// <returns>Null when a "@beacon.mode" line says anything other than "silent" or "output".</returns>
    public static Script? Parse(IReadOnlyList<string> header, string relativePath, string filePath)
    {
        string title = Path.GetFileNameWithoutExtension(relativePath);
        if (title.Length == 0)
        {
            // A file called just ".ps1" would otherwise give a row with nothing to read.
            title = Path.GetFileName(relativePath);
        }

        var mode = ScriptMode.Silent;
        string? prompt = null;
        for (int i = 0; i < header.Count && i < HeaderLines; i++)
        {
            string line = header[i];
            if (i == 0 && line.StartsWith('\uFEFF'))
            {
                // The decoder that reads the file normally removes a byte order mark, but if
                // one is left it must not hide a comment mark on the first line.
                line = line[1..];
            }

            if (!TryReadKey(line, out string key, out string value))
            {
                continue;
            }

            if (key.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                // An empty title would give a row with nothing to read, so the earlier one stays.
                title = value.Length > 0 ? value : title;
            }
            else if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("silent", StringComparison.OrdinalIgnoreCase))
                {
                    mode = ScriptMode.Silent;
                }
                else if (value.Equals("output", StringComparison.OrdinalIgnoreCase))
                {
                    mode = ScriptMode.Output;
                }
                else
                {
                    // Guessing would run the script in a way its author did not ask for.
                    return null;
                }
            }
            else if (key.Equals("argument", StringComparison.OrdinalIgnoreCase))
            {
                prompt = value.Length > 0 ? value : DefaultArgumentPrompt;
            }
        }

        return new Script(relativePath, filePath, title, mode, prompt);
    }

    /// <returns>True when the line is a comment that holds "@beacon." and a key; the value is the rest of the line, trimmed.</returns>
    private static bool TryReadKey(string line, out string key, out string value)
    {
        key = "";
        value = "";

        var rest = line.AsSpan().TrimStart();
        if (rest.StartsWith("//", StringComparison.Ordinal) || rest.StartsWith("::", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }
        else if (rest.StartsWith("#", StringComparison.Ordinal))
        {
            rest = rest[1..];
        }
        else if (rest.Length > 3 && rest.StartsWith("REM", StringComparison.OrdinalIgnoreCase) && rest[3] is ' ' or '\t')
        {
            // "REM" must be a word of its own: a line that starts "remove" is not a comment.
            rest = rest[4..];
        }
        else
        {
            return false;
        }

        rest = rest.TrimStart();
        if (!rest.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rest = rest[Marker.Length..];
        int end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]))
        {
            end++;
        }

        key = rest[..end].ToString();
        value = rest[end..].Trim().ToString();
        return true;
    }
}
