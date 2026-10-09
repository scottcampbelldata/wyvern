namespace Wyvern.Core.Library;

/// <summary>Reads the text of one snippet file.</summary>
public static class SnippetFile
{
    private const string Fence = "---";

    /// <summary>True for a file name ending in .md or .txt, in any case.</summary>
    public static bool HasSnippetExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file may start with front matter: a first line that is exactly "---", then
    /// "key: value" lines, then another line that is exactly "---". The keys "name" and
    /// "keyword" are understood, in any case; any other line there is ignored. Everything after
    /// the closing line is the body. Without front matter the whole file is the body and the
    /// name is the file name without its extension, or the whole file name when there is nothing
    /// before the extension, as in ".md". One line break at the very end of the body is dropped,
    /// because editors add one and it is not meant to be pasted. One byte order mark at the very
    /// start of the text is ignored.
    /// </summary>
    /// <param name="text">The file's text, already decoded.</param>
    /// <returns>Null when front matter is opened and never closed.</returns>
    public static Snippet? Parse(string text, string relativePath, string filePath)
    {
        // The decoder that reads the file normally removes the mark, but if one is left it must
        // not hide the first line of the front matter or end up in the pasted text.
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        string name = Path.GetFileNameWithoutExtension(relativePath);
        if (name.Length == 0)
        {
            // A name that is empty would give a row with nothing to read.
            name = Path.GetFileName(relativePath);
        }

        string? keyword = null;
        string body = text;

        int position = 0;
        if (ReadLine(text, ref position) == Fence)
        {
            bool closed = false;
            while (position < text.Length)
            {
                string line = ReadLine(text, ref position);
                if (line == Fence)
                {
                    closed = true;
                    break;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    // An empty name would give a row with nothing to read, so the file name stays.
                    name = value.Length > 0 ? value : name;
                }
                else if (key.Equals("keyword", StringComparison.OrdinalIgnoreCase))
                {
                    keyword = value.Length > 0 ? value : null;
                }
            }

            if (!closed)
            {
                return null;
            }

            body = text[position..];
        }

        return new Snippet(relativePath, filePath, name, keyword, WithoutFinalLineBreak(body));
    }

    /// <summary>The line starting at <paramref name="position"/>, without its line break; moves the position past it.</summary>
    private static string ReadLine(string text, ref int position)
    {
        int end = text.IndexOf('\n', position);
        string line = end < 0 ? text[position..] : text[position..end];
        position = end < 0 ? text.Length : end + 1;
        return line.EndsWith('\r') ? line[..^1] : line;
    }

    private static string WithoutFinalLineBreak(string body)
    {
        if (body.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return body[..^2];
        }

        return body.EndsWith('\n') ? body[..^1] : body;
    }
}
