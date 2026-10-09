using Wyvern.Core.Search;

namespace Wyvern.Core.Library;

/// <summary>
/// The launcher rows for one list of library items. Rows are made once for each load of the
/// library, not once for each keystroke: they are kept until a different list arrives.
/// </summary>
/// <remarks>
/// A list that has been loaded is never changed afterwards; loading again makes a new list. So
/// "is this the list the rows were made from" is a comparison of two references and nothing
/// more. Use from one thread only, the UI thread, which is where searches run.
/// </remarks>
/// <param name="toRow">Makes the row for one item.</param>
public sealed class LibraryRows<T>(Func<T, SearchItem> toRow)
{
    private IReadOnlyList<T> _from = [];
    private IReadOnlyList<SearchItem> _rows = [];

    /// <returns>One row for each item, in the same order.</returns>
    public IReadOnlyList<SearchItem> For(IReadOnlyList<T> items)
    {
        if (!ReferenceEquals(_from, items))
        {
            // The library was loaded again since the last search.
            _rows = items.Select(toRow).ToList();
            _from = items;
        }

        return _rows;
    }
}
