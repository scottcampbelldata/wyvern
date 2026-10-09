namespace Wyvern.Core.Library;

/// <summary>One entry of quicklinks.json: a name for a web address or a local path.</summary>
/// <param name="Name">What the launcher shows and matches. No two quicklinks share a name.</param>
/// <param name="Link">The address or path, exactly as written. It may contain <see cref="Placeholder"/>.</param>
/// <param name="Keyword">A short word that brings this quicklink to the top when typed, or null.</param>
public sealed record Quicklink(string Name, string Link, string? Keyword)
{
    /// <summary>Item ids are this prefix followed by the name.</summary>
    public const string IdPrefix = "quicklink:";

    /// <summary>Where the text typed in argument mode goes.</summary>
    public const string Placeholder = "{query}";

    public string Id => IdPrefix + Name;

    /// <summary>True when the link cannot be opened until something has been typed for it.</summary>
    public bool TakesArgument => Link.Contains(Placeholder, StringComparison.Ordinal);

    /// <summary>
    /// The link with every <see cref="Placeholder"/> replaced by the argument. In a web address
    /// the argument is escaped, so that a space or an ampersand stays part of the query and
    /// cannot start a new parameter. Anywhere else, such as a file path, it goes in unchanged.
    /// An empty argument is allowed.
    /// </summary>
    public string Resolve(string argument)
    {
        bool isWebAddress = Link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || Link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return Link.Replace(Placeholder, isWebAddress ? Uri.EscapeDataString(argument) : argument, StringComparison.Ordinal);
    }
}
