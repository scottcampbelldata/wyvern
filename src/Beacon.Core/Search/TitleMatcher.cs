namespace Beacon.Core.Search;

public static class TitleMatcher
{
    /// <summary>Scores each item's title against the query and returns the ones that match.</summary>
    public static IReadOnlyList<SearchResult> Match(string query, IReadOnlyList<SearchItem> items)
    {
        var results = new List<SearchResult>();
        foreach (var item in items)
        {
            if (FuzzyMatcher.Score(query, item.Title) is int score)
            {
                results.Add(new SearchResult(item, score));
            }
        }

        return results;
    }
}
