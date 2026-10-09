using Wyvern.Core.Files;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Core.Library;

/// <summary>
/// Searches the quicklinks. Enter opens the link. A link that holds {query} cannot be opened
/// as it stands, so Enter and Tab both ask for the text to put there first.
/// </summary>
public sealed class QuicklinkProvider : ISearchProvider, IBrowsableProvider
{
    /// <summary>Shown in the empty input line while the text for {query} is being typed.</summary>
    public const string ArgumentPrompt = "Query";

    /// <summary>
    /// Every quicklink shows the icon Windows uses for an internet shortcut. It is worked out
    /// from the file type alone; no icon is ever fetched from the site a link points to.
    /// </summary>
    public const string IconKey = FileProvider.IconKeyPrefix + ".url";

    private readonly Func<LibrarySnapshot> _library;
    private readonly IProcessLauncher _launcher;
    private readonly LibraryRows<Quicklink> _rows;
    private readonly Action<Quicklink>? _edit;
    private readonly Action<Quicklink>? _delete;

    public const string EditQuicklink = "Edit Quicklink";
    public const string DeleteQuicklink = "Delete Quicklink";
    public const string DeleteQuestion = "Press Enter to delete this quicklink";

    /// <param name="library">Gives the library as last loaded. Called on every search; it must answer from memory.</param>
    /// <param name="edit">Opens the form that changes a quicklink. Left out by code that offers no such action.</param>
    /// <param name="delete">Takes a quicklink out of the library. Left out by code that offers no such action.</param>
    public QuicklinkProvider(Func<LibrarySnapshot> library, IProcessLauncher launcher, Action<Quicklink>? edit = null, Action<Quicklink>? delete = null)
    {
        _edit = edit;
        _delete = delete;
        _library = library;
        _launcher = launcher;
        _rows = new LibraryRows<Quicklink>(ToItem);
    }

    /// <summary>Every one of them, as the library holds them now, for the list shown before anything is typed.</summary>
    public IReadOnlyList<SearchItem> Browse() => _rows.For(_library().Quicklinks);

    public IReadOnlyList<SearchResult> Search(string query)
    {
        var quicklinks = _library().Quicklinks;
        var rows = _rows.For(quicklinks);

        var results = new List<SearchResult>();
        for (int i = 0; i < quicklinks.Count; i++)
        {
            if (KeywordMatcher.Score(query, quicklinks[i].Name, quicklinks[i].Keyword) is int score)
            {
                results.Add(new SearchResult(rows[i], score));
            }
        }

        return results;
    }

    private SearchItem ToItem(Quicklink quicklink) => quicklink.TakesArgument
        ? new SearchItem(
            quicklink.Id,
            quicklink.Name,
            quicklink.Link,
            ItemKind.Quicklink,
            Run: null,
            IconKey,
            Argument: new ItemArgument(ArgumentPrompt, argument => Open(quicklink.Resolve(argument))))
        {
            Actions = Further(quicklink),
        }
        : new SearchItem(
            quicklink.Id,
            quicklink.Name,
            quicklink.Link,
            ItemKind.Quicklink,
            Run: () => Open(quicklink.Link),
            IconKey)
        {
            Actions = Further(quicklink),
        };

    // What else can be done with a quicklink: change it in the form, and delete it, which asks first.
    private IReadOnlyList<ItemAction> Further(Quicklink quicklink)
    {
        var actions = new List<ItemAction>();
        if (_edit is { } edit)
        {
            actions.Add(new ItemAction(EditQuicklink, () => edit(quicklink)));
        }

        if (_delete is { } delete)
        {
            actions.Add(new ItemAction(DeleteQuicklink, () => delete(quicklink)) { Question = DeleteQuestion });
        }

        return actions;
    }

    // A link that is only {query}, with nothing typed for it, comes out blank. Handed to the
    // shell as it is, that is nothing to open. Throwing is how an action says it did not work:
    // the launcher turns it into the ordinary "that did not work" notification.
    private void Open(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new InvalidOperationException("The link is blank once the text typed is put in it.");
        }

        _launcher.OpenLink(link);
    }
}
