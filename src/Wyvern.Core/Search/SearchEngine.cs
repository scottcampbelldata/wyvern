namespace Wyvern.Core.Search;

/// <summary>Asks every provider for matches and merges them into one ranked list.</summary>
public sealed class SearchEngine
{
    public const int MaxResults = 50;

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly UsageStore _usage;
    private readonly Action<ISearchProvider, Exception>? _providerFailed;
    private readonly HashSet<ISearchProvider> _failing = [];
    private readonly ItemMarks? _marks;

    // Being asked for one item by its id is told of apart from a search: a provider can do one and fail at the other.
    private readonly HashSet<ISearchProvider> _failingLookups = [];

    /// <param name="providerFailed">
    /// Told when a provider throws. It is called once when a provider starts failing, not on
    /// every keystroke, and again only after that provider has worked in between.
    /// </param>
    /// <param name="marks">The owner's favorites and aliases. Left out by code that keeps none.</param>
    public SearchEngine(
        IEnumerable<ISearchProvider> providers,
        UsageStore usage,
        Action<ISearchProvider, Exception>? providerFailed = null,
        ItemMarks? marks = null)
    {
        _marks = marks;
        _providers = providers.ToList();
        _usage = usage;
        _providerFailed = providerFailed;
    }

    /// <summary>The owner's favorites and aliases, or null when none are kept.</summary>
    public ItemMarks? Marks => _marks;

    /// <summary>
    /// The owner's favorites that are there to be listed, in the owner's order. One whose
    /// item is gone for now, an app that was uninstalled, say, is left out and stays a
    /// favorite: it is listed again when its item is back.
    /// </summary>
    /// <param name="browsable">What <see cref="Browse"/> has just given, to save asking again.</param>
    public IReadOnlyList<SearchItem> Favorites(IReadOnlyList<SearchItem>? browsable = null)
    {
        if (_marks is null || _marks.Favorites.Count == 0)
        {
            return [];
        }

        browsable ??= Browse();
        var found = new List<SearchItem>();
        foreach (string id in _marks.Favorites)
        {
            if (Find(id, browsable) is { } item)
            {
                found.Add(item);
            }
        }

        return found;
    }

    // The item of an id: one of those that are listed before anything is typed, or one that
    // a provider can give by its id.
    private SearchItem? Find(string id, IReadOnlyList<SearchItem> browsable)
    {
        foreach (var item in browsable)
        {
            if (item.Id == id)
            {
                return item;
            }
        }

        foreach (var provider in _providers)
        {
            if (provider is not IItemLookup lookup)
            {
                continue;
            }

            try
            {
                var item = lookup.Find(id);
                _failingLookups.Remove(provider);
                if (item is not null)
                {
                    return item;
                }
            }
            catch (Exception ex)
            {
                if (_failingLookups.Add(provider))
                {
                    _providerFailed?.Invoke(provider, ex);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What the launcher lists before anything is typed: every item of every provider that can
    /// be browsed, in no particular order. A provider that throws is left out, as in a search.
    /// Call from one thread only (the UI thread).
    /// </summary>
    public IReadOnlyList<SearchItem> Browse()
    {
        var all = new List<SearchItem>();
        foreach (var provider in _providers)
        {
            if (provider is not IBrowsableProvider browsable)
            {
                continue;
            }

            try
            {
                all.AddRange(browsable.Browse());
                _failing.Remove(provider);
            }
            catch (Exception ex)
            {
                if (_failing.Add(provider))
                {
                    _providerFailed?.Invoke(provider, ex);
                }
            }
        }

        return all;
    }

    /// <summary>
    /// Ranks by match score multiplied by the frecency boost. Ties put files below every other
    /// kind, then sort by title. An empty query returns nothing. A provider that throws is left
    /// out of this search; the others still answer. The item whose alias is exactly what was
    /// typed stands first, whatever else was found. Call from one thread only (the UI thread).
    /// </summary>
    public IReadOnlyList<SearchItem> Search(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        string? aliased = _marks?.IdOf(query);
        bool aliasedFound = false;
        var ranked = new List<(SearchItem Item, double Score)>();
        foreach (var provider in _providers)
        {
            IReadOnlyList<SearchResult> results;
            try
            {
                results = provider.Search(query);
                _failing.Remove(provider);
            }
            catch (Exception ex)
            {
                // One broken provider must not empty the launcher or stop the others.
                if (_failing.Add(provider))
                {
                    _providerFailed?.Invoke(provider, ex);
                }

                continue;
            }

            foreach (var result in results)
            {
                if (result.Item.Id == aliased)
                {
                    // Its alias was typed: nothing ranks above it.
                    if (!aliasedFound)
                    {
                        ranked.Add((result.Item, double.MaxValue));
                        aliasedFound = true;
                    }

                    continue;
                }

                ranked.Add((result.Item, result.Score * _usage.Boost(result.Item.Id)));
            }
        }

        // An alias need not look like its item's name, so the item may not have been found
        // by what was typed. It is fetched by its id.
        if (aliased is not null && !aliasedFound && Find(aliased, Browse()) is { } byAlias)
        {
            ranked.Add((byAlias, double.MaxValue));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Item.Kind == ItemKind.File ? 1 : 0)
            .ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(r => r.Item)
            .ToList();
    }
}
