namespace Wyvern.Core.Library;

/// <summary>One file under the snippets folder: a piece of text to paste.</summary>
/// <param name="RelativePath">The file's path under the snippets folder, for example "work\sig.md". It is what makes the item id.</param>
/// <param name="FilePath">The file's full path, for opening it in an editor.</param>
/// <param name="Name">What the launcher shows and matches.</param>
/// <param name="Keyword">A short word that brings this snippet to the top when typed, or null.</param>
/// <param name="Body">The text to paste, before its placeholders are filled in.</param>
public sealed record Snippet(string RelativePath, string FilePath, string Name, string? Keyword, string Body)
{
    /// <summary>Item ids are this prefix followed by the path under the snippets folder.</summary>
    public const string IdPrefix = "snippet:";

    public string Id => IdPrefix + RelativePath;
}
