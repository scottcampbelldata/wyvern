using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wyvern.Core.Library;

/// <summary>How saving something into the library went.</summary>
/// <param name="Saved">True when the file was written.</param>
/// <param name="Note">When it was not: fixed words that say why, for the form to show. Empty otherwise.</param>
public sealed record LibrarySave(bool Saved, string Note)
{
    public static LibrarySave Done { get; } = new(true, "");

    public static LibrarySave Refused(string note) => new(false, note);
}

/// <summary>
/// Writes a snippet or a quicklink into the library, changes one and takes one out, for the
/// forms and the actions that do so. A snippet is a file of its own in the snippets folder; a
/// quicklink is one entry in quicklinks.json. Making one never writes over what is there: a
/// name that is taken is refused, and so is a quicklinks.json that cannot be read. Changing
/// one writes over that one alone. What is refused is said in fixed words. The library is
/// watched, so what is written here is in the launcher a moment later.
/// </summary>
public static class LibraryWriter
{
    /// <summary>The longest name a snippet or a quicklink is given here.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest keyword.</summary>
    public const int MaxKeywordLength = 50;

    public const string NameNeeded = "Give it a name.";
    public const string NameTooLong = "The name is too long: 100 characters at most.";
    public const string KeywordTooLong = "The keyword is too long: 50 characters at most.";
    public const string KeywordHasSpace = "A keyword is one word, without spaces.";
    public const string TextNeeded = "A snippet needs some text.";
    public const string SnippetExists = "A snippet of that name is already there. Give this one another name.";
    public const string LinkNeeded = "A quicklink needs a link: a web address, or the path of a file or a folder.";
    public const string QuicklinkExists = "A quicklink of that name is already there. Give this one another name.";
    public const string QuicklinksUnreadable = "quicklinks.json cannot be read, so nothing was added. Run Edit Quicklinks and put it right first.";
    public const string NotWritten = "The file could not be written.";
    public const string SnippetGone = "That snippet's file is no longer there.";
    public const string QuicklinkGone = "That quicklink is no longer in quicklinks.json.";
    public const string NotDeleted = "It could not be deleted.";

    private static readonly JsonSerializerOptions EntryOptions = new()
    {
        // The default encoder writes "&" and "+" as escapes, which would make a link unreadable in a file that is also edited by hand.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Writes a snippet as a new file in the snippets folder, named after the snippet, with
    /// its name and keyword at the top as a snippet file has them.
    /// </summary>
    /// <param name="keyword">Optional: null, or nothing but spaces, for none.</param>
    public static LibrarySave SaveSnippet(string snippetsFolder, string name, string? keyword, string text)
    {
        name = OneLine(name);
        keyword = OneLine(keyword ?? "");
        if (Check(name, keyword) is { } refusal)
        {
            return refusal;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return LibrarySave.Refused(TextNeeded);
        }

        try
        {
            Directory.CreateDirectory(snippetsFolder);
            string file = Path.Combine(snippetsFolder, FileName(name) + ".md");

            // A snippet is known by its name, whatever its file is called: one of that name
            // in any file is one too many, and so is a file of this one's name.
            if (File.Exists(file) || NameTaken(snippetsFolder, name))
            {
                return LibrarySave.Refused(SnippetExists);
            }

            var content = new StringBuilder();
            content.Append("---\n").Append("name: ").Append(name).Append('\n');
            if (keyword.Length > 0)
            {
                content.Append("keyword: ").Append(keyword).Append('\n');
            }

            // One line break at the very end of a snippet file is not part of the snippet, so
            // one is added: what the text itself ends with is then kept.
            content.Append("---\n").Append(text).Append('\n');
            WriteNew(file, content.ToString());
            return LibrarySave.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LibrarySave.Refused(NotWritten);
        }
    }

    /// <summary>
    /// Changes a snippet where it is: its file keeps its place and its file name, so the
    /// snippet stays the same item to the launcher, and its name, its keyword and its text
    /// are written anew. Lines at the top of the file that Wyvern does not read are kept.
    /// </summary>
    /// <param name="keyword">Optional: null, or nothing but spaces, for none.</param>
    public static LibrarySave UpdateSnippet(string snippetsFolder, Snippet snippet, string name, string? keyword, string text)
    {
        name = OneLine(name);
        keyword = OneLine(keyword ?? "");
        if (Check(name, keyword) is { } refusal)
        {
            return refusal;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return LibrarySave.Refused(TextNeeded);
        }

        try
        {
            string file = snippet.FilePath;
            if (!Inside(snippetsFolder, file) || !File.Exists(file))
            {
                return LibrarySave.Refused(SnippetGone);
            }

            if (NameTaken(snippetsFolder, name, except: file))
            {
                return LibrarySave.Refused(SnippetExists);
            }

            var content = new StringBuilder();
            content.Append("---\n").Append("name: ").Append(name).Append('\n');
            if (keyword.Length > 0)
            {
                content.Append("keyword: ").Append(keyword).Append('\n');
            }

            foreach (string line in OtherTopLines(File.ReadAllText(file, Encoding.UTF8)))
            {
                content.Append(line).Append('\n');
            }

            content.Append("---\n").Append(text).Append('\n');
            Replace(file, content.ToString());
            return LibrarySave.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LibrarySave.Refused(NotWritten);
        }
    }

    /// <summary>Takes a snippet out of the library by handing its file to <paramref name="remove"/>, which in the app puts it into the Recycle Bin.</summary>
    /// <param name="remove">Removes the file of this path. It is given a file under the snippets folder and nothing else.</param>
    public static LibrarySave RemoveSnippet(string snippetsFolder, Snippet snippet, Action<string> remove)
    {
        try
        {
            string file = snippet.FilePath;
            if (!Inside(snippetsFolder, file) || !SnippetFile.HasSnippetExtension(file) || !File.Exists(file))
            {
                return LibrarySave.Refused(SnippetGone);
            }

            remove(Path.GetFullPath(file));
            return File.Exists(file) ? LibrarySave.Refused(NotDeleted) : LibrarySave.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return LibrarySave.Refused(NotDeleted);
        }
    }

    /// <summary>
    /// Changes the quicklink of the name <paramref name="was"/> in quicklinks.json, in its
    /// place in the list. Whatever else its entry holds is kept, and so is every other entry.
    /// </summary>
    /// <param name="keyword">Optional: null, or nothing but spaces, for none.</param>
    public static LibrarySave UpdateQuicklink(string quicklinksFile, string was, string name, string link, string? keyword)
    {
        name = OneLine(name);
        link = OneLine(link);
        keyword = OneLine(keyword ?? "");
        if (Check(name, keyword) is { } refusal)
        {
            return refusal;
        }

        if (link.Length == 0)
        {
            return LibrarySave.Refused(LinkNeeded);
        }

        return Rewrite(quicklinksFile, entries =>
        {
            int at = entries.FindIndex(entry => Named(entry, was));
            if (at < 0)
            {
                return LibrarySave.Refused(QuicklinkGone);
            }

            if (Enumerable.Range(0, entries.Count).Any(i => i != at && Named(entries[i], name)))
            {
                return LibrarySave.Refused(QuicklinkExists);
            }

            var changed = new JsonObject { ["name"] = name, ["link"] = link };
            if (keyword.Length > 0)
            {
                changed["keyword"] = keyword;
            }

            // What else the entry holds is the owner's, and stays.
            using (var old = JsonDocument.Parse(entries[at]))
            {
                foreach (var property in old.RootElement.EnumerateObject())
                {
                    bool known = property.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("link", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("keyword", StringComparison.OrdinalIgnoreCase);
                    if (!known && !changed.ContainsKey(property.Name))
                    {
                        changed[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                    }
                }
            }

            entries[at] = changed.ToJsonString(EntryOptions);
            return LibrarySave.Done;
        },
        written => written.Any(known => known.Name == name));
    }

    /// <summary>Takes the quicklink of this name out of quicklinks.json. Every other entry stays as it is written.</summary>
    public static LibrarySave RemoveQuicklink(string quicklinksFile, string name) =>
        Rewrite(quicklinksFile, entries =>
        {
            // An entry further down with the same name was never in use; it would take the
            // place of the one that is deleted, so it goes as well.
            return entries.RemoveAll(entry => Named(entry, name)) > 0 ? LibrarySave.Done : LibrarySave.Refused(QuicklinkGone);
        },
        written => !written.Any(known => known.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Adds a quicklink to quicklinks.json as its last entry. The entries that are there stay
    /// as they are written, the ones Wyvern cannot use as well; comments in the file are not kept.
    /// </summary>
    /// <param name="keyword">Optional: null, or nothing but spaces, for none.</param>
    public static LibrarySave AddQuicklink(string quicklinksFile, string name, string link, string? keyword)
    {
        name = OneLine(name);
        link = OneLine(link);
        keyword = OneLine(keyword ?? "");
        if (Check(name, keyword) is { } refusal)
        {
            return refusal;
        }

        if (link.Length == 0)
        {
            return LibrarySave.Refused(LinkNeeded);
        }

        try
        {
            var entries = new List<string>();
            if (File.Exists(quicklinksFile))
            {
                string json = File.ReadAllText(quicklinksFile, Encoding.UTF8);
                try
                {
                    if (QuicklinkFile.Parse(json).Quicklinks.Any(known => known.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return LibrarySave.Refused(QuicklinkExists);
                    }

                    entries.AddRange(RawEntries(json));
                }
                catch (JsonException)
                {
                    return LibrarySave.Refused(QuicklinksUnreadable);
                }
            }

            var entry = new Dictionary<string, string> { ["name"] = name, ["link"] = link };
            if (keyword.Length > 0)
            {
                entry["keyword"] = keyword;
            }

            entries.Add(JsonSerializer.Serialize(entry, EntryOptions));
            string written = "[\n  " + string.Join(",\n  ", entries) + "\n]\n";

            // What is about to be written is read back first: the file is replaced only by
            // one that Wyvern can read and that holds the new quicklink.
            if (!QuicklinkFile.Parse(written).Quicklinks.Any(known => known.Name == name))
            {
                return LibrarySave.Refused(NotWritten);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(quicklinksFile)!);
            Replace(quicklinksFile, written);
            return LibrarySave.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LibrarySave.Refused(NotWritten);
        }
    }

    /// <summary>
    /// The file name a snippet of this name is given, without its ending: the name itself,
    /// with a hyphen in the place of every character a file name cannot have, and never one
    /// of the names Windows keeps for itself.
    /// </summary>
    public static string FileName(string name)
    {
        var file = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            file.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c);
        }

        // Windows drops dots and spaces from the end of a name, and a name that is only those is no name.
        string trimmed = file.ToString().Trim().TrimEnd('.', ' ');
        if (trimmed.Length == 0)
        {
            return "snippet";
        }

        string stem = trimmed.Split('.')[0].Trim();
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase) ? "_" + trimmed : trimmed;
    }

    private static LibrarySave? Check(string name, string keyword)
    {
        if (name.Length == 0)
        {
            return LibrarySave.Refused(NameNeeded);
        }

        if (name.Length > MaxNameLength)
        {
            return LibrarySave.Refused(NameTooLong);
        }

        if (keyword.Length > MaxKeywordLength)
        {
            return LibrarySave.Refused(KeywordTooLong);
        }

        return keyword.Any(char.IsWhiteSpace) ? LibrarySave.Refused(KeywordHasSpace) : null;
    }

    // A name, a keyword and a link are one line each: a line break in one would end it in
    // the file, so it and every other character that shows nothing becomes a space.
    private static string OneLine(string text)
    {
        var line = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            line.Append(char.IsControl(c) ? ' ' : c);
        }

        return line.ToString().Trim();
    }

    // Reads quicklinks.json, lets the change be made to its entries as they are written, and
    // writes the file again. What is about to be written is read back first: the file is
    // replaced only by one that Wyvern can read and that holds what the change was for.
    private static LibrarySave Rewrite(string quicklinksFile, Func<List<string>, LibrarySave> change, Func<IReadOnlyList<Quicklink>, bool> asMeant)
    {
        try
        {
            if (!File.Exists(quicklinksFile))
            {
                return LibrarySave.Refused(QuicklinkGone);
            }

            List<string> entries;
            try
            {
                string json = File.ReadAllText(quicklinksFile, Encoding.UTF8);
                _ = QuicklinkFile.Parse(json);
                entries = RawEntries(json).ToList();
            }
            catch (JsonException)
            {
                return LibrarySave.Refused(QuicklinksUnreadable);
            }

            var changed = change(entries);
            if (!changed.Saved)
            {
                return changed;
            }

            string written = entries.Count == 0 ? "[]\n" : "[\n  " + string.Join(",\n  ", entries) + "\n]\n";
            if (!asMeant(QuicklinkFile.Parse(written).Quicklinks))
            {
                return LibrarySave.Refused(NotWritten);
            }

            Replace(quicklinksFile, written);
            return LibrarySave.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LibrarySave.Refused(NotWritten);
        }
    }

    // True when this entry of quicklinks.json, as it is written, is a quicklink of this name.
    private static bool Named(string entry, string name)
    {
        try
        {
            return QuicklinkFile.Parse("[" + entry + "]").Quicklinks is [var quicklink] && quicklink.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // True when the file is somewhere under the folder.
    private static bool Inside(string folder, string file)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // The lines at the top of a snippet file, between its two fences, that are neither its
    // name nor its keyword: Wyvern does not read them, and they are the owner's.
    private static List<string> OtherTopLines(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        string[] lines = text.Split('\n');
        var others = new List<string>();
        if (lines[0].TrimEnd('\r') != "---")
        {
            return others;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line == "---")
            {
                return others;
            }

            int colon = line.IndexOf(':');
            string key = colon > 0 ? line[..colon].Trim() : "";
            if (!key.Equals("name", StringComparison.OrdinalIgnoreCase) && !key.Equals("keyword", StringComparison.OrdinalIgnoreCase))
            {
                others.Add(line);
            }
        }

        // The top was never closed: the file is not a snippet's, and nothing of it is carried over.
        return [];
    }

    // True when a snippet file in the folder already gives this name. The file that is being
    // changed does not count: it may keep its own name.
    private static bool NameTaken(string snippetsFolder, string name, string? except = null)
    {
        string? skipped = except is null ? null : Path.GetFullPath(except);
        foreach (string file in Directory.EnumerateFiles(snippetsFolder, "*", SearchOption.AllDirectories))
        {
            if (!SnippetFile.HasSnippetExtension(file) || Path.GetFullPath(file).Equals(skipped, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string relative = Path.GetRelativePath(snippetsFolder, file);
                if (SnippetFile.Parse(File.ReadAllText(file, Encoding.UTF8), relative, file) is { } snippet
                    && snippet.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read takes no name.
            }
        }

        return false;
    }

    // Every entry of the file's list as it is written, whether Wyvern can use it or not.
    private static IEnumerable<string> RawEntries(string json)
    {
        if (json.StartsWith('\uFEFF'))
        {
            json = json[1..];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.EnumerateArray().Select(entry => entry.GetRawText()).ToList();
    }

    // Written beside the file under another name first, so that half a file is never taken
    // for the whole. A file of the name that appeared meanwhile is not written over.
    private static void WriteNew(string file, string content)
    {
        string beside = file + ".tmp";
        File.WriteAllText(beside, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(beside, file, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(beside);
            throw;
        }
    }

    private static void Replace(string file, string content)
    {
        string beside = file + ".tmp";
        File.WriteAllText(beside, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(beside, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(beside);
            throw;
        }
    }
}
