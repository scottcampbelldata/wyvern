using Beacon.Core.Search;

namespace Beacon.Core.Commands;

/// <param name="Id">Short stable name such as "quit". Becomes the item id "command:quit".</param>
/// <param name="Run">What Enter does. Null for a command that cannot run without its <paramref name="Argument"/>.</param>
/// <param name="Argument">Set for a command that takes something typed first, which Tab or Enter asks for.</param>
/// <param name="KeepOpen">True for a command that shows something in the launcher itself, which then stays open.</param>
public sealed record BuiltInCommand(
    string Id,
    string Title,
    string Subtitle,
    Action? Run,
    ItemArgument? Argument = null,
    bool KeepOpen = false);

/// <summary>Searches the launcher's own commands.</summary>
public sealed class CommandProvider : ISearchProvider, IBrowsableProvider
{
    private readonly IReadOnlyList<SearchItem> _items;

    public CommandProvider(IEnumerable<BuiltInCommand> commands)
    {
        _items = commands
            .Select(command => new SearchItem(
                "command:" + command.Id,
                command.Title,
                command.Subtitle,
                ItemKind.Command,
                command.Run,
                Argument: command.Argument,
                KeepOpen: command.KeepOpen))
            .ToList();
    }

    public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, _items);

    public IReadOnlyList<SearchItem> Browse() => _items;
}
