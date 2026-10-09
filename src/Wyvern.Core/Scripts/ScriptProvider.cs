using Wyvern.Core.Files;
using Wyvern.Core.Launcher;
using Wyvern.Core.Library;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Core.Scripts;

/// <summary>
/// Searches the scripts. Enter runs the script; a script that takes an argument asks for it
/// first, on Enter or on Tab, as a quicklink with {query} does. Ctrl+Enter opens the script's
/// file for editing.
/// </summary>
/// <remarks>
/// A silent script runs with the launcher hidden, and one notification says how it ended. A
/// script in output mode keeps the launcher open and shows its output there. If that view has
/// been closed by the time the script ends, a notification says how it ended instead, so that
/// no script ends without the owner being told. A notification can carry a line the script
/// printed. It never carries the script's title.
/// </remarks>
public sealed class ScriptProvider : ISearchProvider, IBrowsableProvider
{
    private readonly Func<LibrarySnapshot> _library;
    private readonly IScriptRunner _runner;
    private readonly IProcessLauncher _launcher;
    private readonly OutputViewModel _output;
    private readonly Action<string> _notify;
    private readonly Action<Action> _post;
    private readonly LibraryRows<Script> _rows;

    /// <param name="library">Gives the library as last loaded. Called on every search; it must answer from memory.</param>
    /// <param name="output">The launcher's output view, the same one the launcher's view model has.</param>
    /// <param name="notify">Shows one notification. Called on the UI thread.</param>
    /// <param name="post">
    /// Runs an action on the UI thread, later. A script ends on a background thread, and the
    /// output view and the notifications belong to the UI thread.
    /// </param>
    public ScriptProvider(
        Func<LibrarySnapshot> library,
        IScriptRunner runner,
        IProcessLauncher launcher,
        OutputViewModel output,
        Action<string> notify,
        Action<Action> post)
    {
        _library = library;
        _runner = runner;
        _launcher = launcher;
        _output = output;
        _notify = notify;
        _post = post;
        _rows = new LibraryRows<Script>(ToItem);
    }

    /// <summary>Every one of them, as the library holds them now, for the list shown before anything is typed.</summary>
    public IReadOnlyList<SearchItem> Browse() => _rows.For(_library().Scripts);

    public IReadOnlyList<SearchResult> Search(string query)
    {
        var scripts = _library().Scripts;
        var rows = _rows.For(scripts);

        var results = new List<SearchResult>();
        for (int i = 0; i < scripts.Count; i++)
        {
            if (FuzzyMatcher.Score(query, scripts[i].Title) is int score)
            {
                results.Add(new SearchResult(rows[i], score));
            }
        }

        return results;
    }

    private SearchItem ToItem(Script script) => new(
        script.Id,
        script.Title,
        script.RelativePath,
        ItemKind.Script,
        Run: script.ArgumentPrompt is null ? () => Start(script, null) : null,
        IconKey: FileProvider.IconKeyPrefix + script.Extension,
        RunSecondary: () => _launcher.EditFile(script.FilePath),
        Argument: script.ArgumentPrompt is { } prompt ? new ItemArgument(prompt, argument => Start(script, argument)) : null,
        KeepOpen: script.Mode == ScriptMode.Output);

    private void Start(Script script, string? argument)
    {
        string extension = script.Extension;
        if (script.Mode != ScriptMode.Output)
        {
            // The words are made here, where the result arrives, and not in what is posted:
            // the UI thread is the one the owner is typing on.
            _runner.Start(script, argument, result =>
            {
                string notification = ScriptNotes.Notification(result, extension);
                _post(() => _notify(notification));
            });
            return;
        }

        object ticket = _output.Open(script.Title);
        try
        {
            // Everything that takes work is done before the post, because the text can be up to
            // 200,000 characters and the UI thread must not be held up for it. What is posted
            // only hands the finished texts to the view, or to the notifications if the view
            // is no longer showing this run.
            _runner.Start(script, argument, result =>
            {
                string notification = ScriptNotes.Notification(result, extension);
                string status = ScriptNotes.Status(result, extension);
                string text = ScriptNotes.OutputText(result, extension);
                _post(() =>
                {
                    // False when the view was closed, or opened for another script, in the meantime.
                    if (!_output.Complete(ticket, status, text))
                    {
                        _notify(notification);
                    }
                });
            });
        }
        catch
        {
            // Nothing was started, so there is nothing for the view to wait for.
            _output.Close();
            throw;
        }
    }
}
