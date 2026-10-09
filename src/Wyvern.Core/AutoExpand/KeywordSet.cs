using Wyvern.Core.Library;

namespace Wyvern.Core.AutoExpand;

/// <summary>
/// The keywords that auto-expand watches for, taken from the library's snippets at one moment.
/// It never changes once made: when the library is loaded again, a new set is made.
/// </summary>
/// <remarks>
/// Only a snippet with a keyword takes part. Keywords are compared exactly, capitals included,
/// so <c>;sig</c> and <c>;Sig</c> are two keywords. When two snippets have the same keyword the
/// one that comes first in the library's order, which is by file path, is used. A keyword
/// longer than <see cref="MaxKeywordLength"/> takes no part, because no more characters than
/// that are ever kept of what is typed.
/// </remarks>
public sealed class KeywordSet
{
    /// <summary>
    /// The longest keyword that can be typed and replaced, in characters. It is also the most
    /// characters Wyvern ever keeps of what the owner types.
    /// </summary>
    public const int MaxKeywordLength = 64;

    // The snippets by the last character of their keyword, each list with the longest keyword
    // first. A key that is typed can only finish a keyword that ends with its character, so
    // one look-up finds the few keywords worth comparing.
    private readonly Dictionary<char, Snippet[]> _byLastCharacter = [];

    public KeywordSet(IReadOnlyList<Snippet> snippets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<char, List<Snippet>>();
        foreach (var snippet in snippets)
        {
            if (snippet.Keyword is not { Length: > 0 } keyword)
            {
                continue;
            }

            if (keyword.Length > MaxKeywordLength || !seen.Add(keyword))
            {
                LeftOut++;
                continue;
            }

            Longest = Math.Max(Longest, keyword.Length);
            char last = keyword[^1];
            if (!groups.TryGetValue(last, out var group))
            {
                groups[last] = group = [];
            }

            group.Add(snippet);
        }

        foreach (var (last, group) in groups)
        {
            // The order of equal lengths does not matter: two different keywords of one length
            // cannot both end the same text.
            _byLastCharacter[last] = [.. group.OrderByDescending(snippet => snippet.Keyword!.Length)];
        }

        Count = seen.Count;
    }

    /// <summary>A set with no keyword in it: nothing typed ever matches, and nothing typed is kept.</summary>
    public static KeywordSet Empty { get; } = new([]);

    /// <summary>How many keywords are watched for.</summary>
    public int Count { get; }

    /// <summary>The length of the longest keyword, and 0 when there is none.</summary>
    public int Longest { get; }

    /// <summary>
    /// How many keywords take no part: longer than <see cref="MaxKeywordLength"/>, or the same
    /// as the keyword of a snippet that comes earlier in the library.
    /// </summary>
    public int LeftOut { get; }

    /// <summary>
    /// The snippet whose keyword these characters end with, or null. When one keyword is the
    /// ending of another and both end the characters, the longer one is the answer.
    /// </summary>
    public Snippet? Match(ReadOnlySpan<char> typed)
    {
        if (typed.Length == 0 || !_byLastCharacter.TryGetValue(typed[^1], out var candidates))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (typed.EndsWith(candidate.Keyword, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}
