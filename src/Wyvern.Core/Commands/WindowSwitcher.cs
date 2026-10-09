using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Core.Commands;

/// <summary>A window that is open on the desktop, as the list of them shows it.</summary>
/// <param name="Handle">What Windows knows the window by.</param>
/// <param name="Title">What its title bar says. It is the owner's: shown, and never logged or kept.</param>
/// <param name="Program">The name of the program it belongs to, or empty when that is not known.</param>
/// <param name="IconKey">A key the UI resolves to the program's icon, or null.</param>
public sealed record OpenWindow(nint Handle, string Title, string Program, string? IconKey)
{
    /// <summary>The type's name only: the title is the owner's.</summary>
    public override string ToString() => nameof(OpenWindow);
}

/// <summary>The windows that are open, and bringing one of them to the front. Implemented with Windows calls in the app.</summary>
public interface IWindowList
{
    /// <summary>The open windows, the one used last first, without Wyvern's own. Never throws.</summary>
    IReadOnlyList<OpenWindow> List();

    /// <summary>Brings a window to the front, putting it back on the screen first when it is minimised.</summary>
    void Activate(nint handle);

    /// <summary>Asks a window to close, as its own close button does: the program may ask about unsaved work first.</summary>
    void Close(nint handle) => throw new NotSupportedException();

    /// <summary>Ends the program a window belongs to at once, without asking it. What is not saved in it is lost.</summary>
    void ForceQuit(nint handle) => throw new NotSupportedException();
}

/// <summary>A list of its own that a command opens in place of the launcher's: what is typed is looked for in it alone.</summary>
/// <param name="Title">The list's name, shown at the foot of the window.</param>
/// <param name="Prompt">A few words shown in the empty input line.</param>
/// <param name="Section">The heading its rows stand under.</param>
/// <param name="Search">Gives the rows for what is typed, which can be nothing. Called on the UI thread at each key.</param>
public sealed record SearchScope(string Title, string Prompt, string Section, Func<string, IReadOnlyList<SearchItem>> Search);

/// <summary>The Switch Windows list: the open windows, found by their titles and their programs' names.</summary>
public static class WindowSwitcher
{
    public const string Heading = "Switch Windows";
    public const string Prompt = "Search open windows...";
    public const string Section = "Open Windows";

    /// <summary>What every listed window's id starts with. A window's use is not counted.</summary>
    public const string IdPrefix = "window:";

    /// <summary>The longest title a row shows.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The list, for the launcher to open.</summary>
    public static SearchScope Scope(IWindowList windows) =>
        new(Heading, Prompt, Section, query => Find(windows.List(), query, windows.Activate, windows.Close, windows.ForceQuit));

    public const string CloseWindow = "Close Window";
    public const string ForceQuit = "Force Quit Program";
    public const string ForceQuitQuestion = "Press Enter to force quit this program. Unsaved work in it is lost";

    /// <summary>
    /// The rows for what is typed: every window when nothing is, otherwise those whose title or
    /// program holds every word that was typed, whatever the case of the letters. They stay in
    /// the order they were given, which is the order they were last used in.
    /// </summary>
    /// <param name="close">Asks a window to close. Left out by code that offers no such action.</param>
    /// <param name="forceQuit">Ends a window's program at once. Left out by code that offers no such action.</param>
    public static IReadOnlyList<SearchItem> Find(
        IReadOnlyList<OpenWindow> windows,
        string query,
        Action<nint> activate,
        Action<nint>? close = null,
        Action<nint>? forceQuit = null)
    {
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rows = new List<SearchItem>();
        foreach (var window in windows)
        {
            if (!words.All(word => window.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || window.Program.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // A title comes from another program: it is shown on one line and cut to fit.
            string shown = NoteText.OnScreen(window.Title, MaxTitleLength);
            nint handle = window.Handle;
            rows.Add(new SearchItem(
                IdPrefix + handle.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                shown.Length > 0 ? shown : "Untitled",
                NoteText.OnScreen(window.Program, MaxTitleLength),
                ItemKind.Window,
                () => activate(handle),
                window.IconKey)
            {
                Actions = Further(handle, close, forceQuit),
            });
        }

        return rows;
    }

    // What else can be done with a window: ask it to close, or end its program, which asks first.
    private static IReadOnlyList<ItemAction> Further(nint handle, Action<nint>? close, Action<nint>? forceQuit)
    {
        var actions = new List<ItemAction>();
        if (close is not null)
        {
            actions.Add(new ItemAction(CloseWindow, () => close(handle)));
        }

        if (forceQuit is not null)
        {
            actions.Add(new ItemAction(ForceQuit, () => forceQuit(handle)) { Question = ForceQuitQuestion });
        }

        return actions;
    }
}
