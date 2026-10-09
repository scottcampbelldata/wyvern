using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Core.Apps;

/// <summary>Searches installed apps. Holds a snapshot that <see cref="Refresh"/> replaces.</summary>
public sealed class AppProvider : ISearchProvider, IBrowsableProvider
{
    private readonly IAppCatalog _catalog;
    private readonly IProcessLauncher _launcher;
    private readonly Action<string>? _copy;
    private volatile IReadOnlyList<SearchItem> _items = [];

    /// <param name="copy">Puts a text on the clipboard, for Copy Path. Left out by code that offers no such action.</param>
    public AppProvider(IAppCatalog catalog, IProcessLauncher launcher, Action<string>? copy = null)
    {
        _catalog = catalog;
        _launcher = launcher;
        _copy = copy;
    }

    public const string RunAsAdministrator = "Run as Administrator";
    public const string OpenFileLocation = "Open File Location";
    public const string CopyPath = "Copy Path";

    // What else can be done with an app whose program file is known: start it as an
    // administrator, show the file in Explorer, copy its path. An app from the Store has no
    // such file and no such actions.
    private IReadOnlyList<ItemAction> ActionsFor(AppEntry app)
    {
        if (app.Target is not { Length: > 0 } target)
        {
            return [];
        }

        var actions = new List<ItemAction>();
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(new ItemAction(RunAsAdministrator, () => _launcher.RunAsAdministrator(target)));
        }

        actions.Add(new ItemAction(OpenFileLocation, () => _launcher.RevealInFolder(target)));
        if (_copy is { } copy)
        {
            actions.Add(new ItemAction(CopyPath, () => copy(target)));
        }

        return actions;
    }

    /// <summary>Re-reads the catalog. Safe to call from a background thread while searches run.</summary>
    /// <returns>The apps now being searched.</returns>
    public IReadOnlyList<AppEntry> Refresh()
    {
        var apps = _catalog.GetApps()
            .Where(app => !string.IsNullOrWhiteSpace(app.Name) && !string.IsNullOrWhiteSpace(app.Id))
            .ToList();

        _items = apps
            .Select(app => new SearchItem(
                Id: "app:" + app.Id,
                Title: app.Name,
                Subtitle: "Application",
                Kind: ItemKind.App,
                Run: () => _launcher.LaunchApp(app.Id),
                IconKey: app.Id)
            {
                Actions = ActionsFor(app),
            })
            .ToList();

        return apps;
    }

    public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, _items);

    public IReadOnlyList<SearchItem> Browse() => _items;
}
