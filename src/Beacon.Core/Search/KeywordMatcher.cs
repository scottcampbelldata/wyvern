namespace Beacon.Core.Search;

/// <summary>
/// Scores an item that has a name and may have a keyword: a short word the owner chose so
/// that typing it brings the item up at once. Quicklinks and snippets are matched this way.
/// </summary>
public static class KeywordMatcher
{
    /// <summary>
    /// The score when the query is the keyword. It is far above anything a name can score, even
    /// after the search engine has doubled that for an item chosen often, so typing a keyword
    /// in full always puts its item first.
    /// </summary>
    public const int ExactKeywordScore = 1_000_000;

    /// <summary>
    /// The name is matched as every other title is. A query that is the keyword, whatever its
    /// case, scores <see cref="ExactKeywordScore"/>. A query that is the start of the keyword
    /// scores as it would against a name that starts with it, which is a strong match, so the
    /// item is already near the top while the keyword is still being typed.
    /// </summary>
    /// <returns>A positive score, or null when neither the name nor the keyword matches.</returns>
    public static int? Score(string query, string name, string? keyword)
    {
        int? byName = FuzzyMatcher.Score(query, name);
        if (keyword is null || query.Length == 0 || !keyword.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return byName;
        }

        if (keyword.Length == query.Length)
        {
            return ExactKeywordScore;
        }

        // Whichever is better: the name can match the same letters even more strongly.
        int? byKeyword = FuzzyMatcher.Score(query, keyword);
        if (byKeyword is null || byName >= byKeyword)
        {
            return byName;
        }

        return byKeyword;
    }
}
