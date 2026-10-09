using Beacon.Core.Files;
using Beacon.Core.Platform;
using Beacon.Core.Search;

namespace Beacon.Core.Library;

/// <summary>
/// Searches the snippets. Enter pastes the snippet, with its placeholders filled in, into the
/// window the owner was working in. Ctrl+Enter opens the snippet's file in an editor.
/// </summary>
public sealed class SnippetProvider : ISearchProvider, IBrowsableProvider
{
    /// <summary>The longest subtitle. The first line of a snippet can be a whole paragraph.</summary>
    public const int MaxSubtitleLength = 100;

    private readonly Func<LibrarySnapshot> _library;
    private readonly IPasteService _paste;
    private readonly IClipboard _clipboard;
    private readonly IProcessLauncher _launcher;
    private readonly IClock _clock;
    private readonly TimeZoneInfo? _zone;
    private readonly LibraryRows<Snippet> _rows;
    private readonly Action<Snippet>? _edit;
    private readonly Action<Snippet>? _delete;

    public const string EditSnippet = "Edit Snippet";
    public const string DeleteSnippet = "Delete Snippet";
    public const string DeleteQuestion = "Press Enter to delete this snippet. Its file goes to the Recycle Bin";

    /// <param name="library">Gives the library as last loaded. Called on every search; it must answer from memory.</param>
    /// <param name="clipboard">Read for {clipboard}, at the moment a snippet is used.</param>
    /// <param name="zone">
    /// The time zone {date} and {time} are given in. Left out everywhere except in tests: it is then
    /// the machine's own, read again each time a snippet is used.
    /// </param>
    /// <param name="edit">Opens the form that changes a snippet. Left out by code that offers no such action.</param>
    /// <param name="delete">Takes a snippet out of the library. Left out by code that offers no such action.</param>
    public SnippetProvider(
        Func<LibrarySnapshot> library,
        IPasteService paste,
        IClipboard clipboard,
        IProcessLauncher launcher,
        IClock clock,
        TimeZoneInfo? zone = null,
        Action<Snippet>? edit = null,
        Action<Snippet>? delete = null)
    {
        _edit = edit;
        _delete = delete;
        _library = library;
        _paste = paste;
        _clipboard = clipboard;
        _launcher = launcher;
        _clock = clock;
        _zone = zone;
        _rows = new LibraryRows<Snippet>(ToItem);
    }

    /// <summary>Every one of them, as the library holds them now, for the list shown before anything is typed.</summary>
    public IReadOnlyList<SearchItem> Browse() => _rows.For(_library().Snippets);

    public IReadOnlyList<SearchResult> Search(string query)
    {
        var snippets = _library().Snippets;
        var rows = _rows.For(snippets);

        var results = new List<SearchResult>();
        for (int i = 0; i < snippets.Count; i++)
        {
            if (KeywordMatcher.Score(query, snippets[i].Name, snippets[i].Keyword) is int score)
            {
                results.Add(new SearchResult(rows[i], score));
            }
        }

        return results;
    }

    private SearchItem ToItem(Snippet snippet) => new(
        snippet.Id,
        snippet.Name,
        FirstLine(snippet.Body),
        ItemKind.Snippet,
        Run: () => _paste.Paste(Expand(snippet)),
        IconKey: FileProvider.IconKeyPrefix + Path.GetExtension(snippet.RelativePath).ToLowerInvariant(),
        RunSecondary: () => _launcher.OpenPath(snippet.FilePath))
    {
        Actions = Further(snippet),
    };

    // What else can be done with a snippet: change it in the form, and delete it, which asks first.
    private IReadOnlyList<ItemAction> Further(Snippet snippet)
    {
        var actions = new List<ItemAction>();
        if (_edit is { } edit)
        {
            actions.Add(new ItemAction(EditSnippet, () => edit(snippet)));
        }

        if (_delete is { } delete)
        {
            actions.Add(new ItemAction(DeleteSnippet, () => delete(snippet)) { Question = DeleteQuestion });
        }

        return actions;
    }

    // The placeholders are filled in by the one rule for the clock, which auto-expand goes by
    // as well. The clipboard is read here, from the clipboard itself; auto-expand gives its own
    // (see SnippetExpander.ExpandNow).
    private string Expand(Snippet snippet) =>
        SnippetExpander.ExpandNow(snippet.Body, _clock, _zone, _clipboard.GetText);

    /// <summary>The first line of the body that has something on it, cut to fit one row.</summary>
    private static string FirstLine(string body)
    {
        foreach (var line in body.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                if (trimmed.Length <= MaxSubtitleLength)
                {
                    return trimmed.ToString();
                }

                // Leave room for the ellipsis, and never end on the first half of a character
                // that takes two positions, such as an emoji.
                int keep = MaxSubtitleLength - 1;
                if (char.IsHighSurrogate(trimmed[keep - 1]))
                {
                    keep--;
                }

                return string.Concat(trimmed[..keep], "…");
            }
        }

        return "";
    }
}
