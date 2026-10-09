using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Core.Files;

/// <summary>Searches the file index. Enter opens the file or folder; Ctrl+Enter shows it in Explorer.</summary>
public sealed class FileProvider : ISearchProvider, IItemLookup
{
    /// <summary>Item ids are this prefix followed by the full path.</summary>
    public const string IdPrefix = "file:";

    /// <summary>Icon keys are this prefix followed by the lower-cased extension, or by "folder".</summary>
    public const string IconKeyPrefix = "filetype:";

    /// <summary>A one-letter query would match most of the index, so files join in from two letters.</summary>
    public const int MinimumQueryLength = 2;

    private readonly FileIndex _index;
    private readonly IProcessLauncher _launcher;
    private readonly Func<IReadOnlyList<string>> _chosenBefore;
    private readonly Action<string>? _copy;

    /// <summary>What the action that copies a file's or a folder's full path is called.</summary>
    public const string CopyPath = "Copy Path";

    /// <param name="chosenBefore">The ids of file items the owner has chosen before, from the usage store.</param>
    /// <param name="copy">Puts a text on the clipboard, for Copy Path. Left out by code that offers no such action.</param>
    public FileProvider(FileIndex index, IProcessLauncher launcher, Func<IReadOnlyList<string>> chosenBefore, Action<string>? copy = null)
    {
        _index = index;
        _launcher = launcher;
        _chosenBefore = chosenBefore;
        _copy = copy;
    }

    public IReadOnlyList<SearchResult> Search(string query)
    {
        if (query.Length < MinimumQueryLength)
        {
            return [];
        }

        var results = new List<SearchResult>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in _index.Search(query, SearchEngine.MaxResults))
        {
            results.Add(new SearchResult(ToItem(match.Entry), match.Score));
            included.Add(match.Entry.FullPath);
        }

        // The index returns its best matches by name alone. A file the owner opens often can
        // match less well than fifty others and still deserve a place once the engine applies
        // its frecency boost, so every matching file chosen before is offered as well.
        foreach (string id in _chosenBefore())
        {
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string path = id[IdPrefix.Length..];
            if (!included.Contains(path)
                && _index.TryGet(path, out var entry)
                && FuzzyMatcher.Score(query, entry.Name) is int score)
            {
                results.Add(new SearchResult(ToItem(entry), score));
                included.Add(path);
            }
        }

        return results;
    }

    /// <summary>The file or folder of this id, when the index holds it.</summary>
    public SearchItem? Find(string itemId) =>
        itemId.StartsWith(IdPrefix, StringComparison.Ordinal) && _index.TryGet(itemId[IdPrefix.Length..], out var entry) ? ToItem(entry) : null;

    private SearchItem ToItem(FileEntry entry)
    {
        string path = entry.FullPath;
        string iconKey = IconKeyPrefix + (entry.IsDirectory ? "folder" : Path.GetExtension(entry.Name).ToLowerInvariant());
        return new SearchItem(
            Id: IdPrefix + path,
            Title: entry.Name,
            Subtitle: entry.Directory,
            Kind: ItemKind.File,
            Run: () => _launcher.OpenPath(path),
            IconKey: iconKey,
            RunSecondary: () => _launcher.RevealInFolder(path))
        {
            Actions = _copy is { } copy ? [new ItemAction(CopyPath, () => copy(path))] : [],
        };
    }
}
