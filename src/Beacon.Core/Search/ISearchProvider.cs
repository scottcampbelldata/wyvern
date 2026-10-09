namespace Beacon.Core.Search;

public interface ISearchProvider
{
    /// <summary>
    /// Returns scored matches for a non-empty, trimmed query. Called on the UI thread for every
    /// keystroke, so it must answer from memory and never block on disk or network.
    /// </summary>
    IReadOnlyList<SearchResult> Search(string query);
}

/// <summary>
/// A provider whose items are listed before anything is typed: the launcher opens showing
/// them, as a list to pick from. Files and the clipboard history are not listed this way.
/// </summary>
public interface IBrowsableProvider
{
    /// <summary>
    /// Every item the provider has, in no particular order. Called on the UI thread each time
    /// the launcher opens, so it must answer from memory.
    /// </summary>
    IReadOnlyList<SearchItem> Browse();
}

/// <summary>
/// A provider that can give one of its items by its id although it does not list them all:
/// a file, which the launcher lists only once it is looked for. It is how a file that is a
/// favorite, or has an alias, is found again.
/// </summary>
public interface IItemLookup
{
    /// <summary>The item of this id as it is now, or null when the provider has none. Called on the UI thread; it must answer from memory.</summary>
    SearchItem? Find(string itemId);
}
