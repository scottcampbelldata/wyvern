namespace Beacon.Core.Search;

public enum ItemKind
{
    App,
    Command,
    Quicklink,
    Snippet,
    Script,
    Clipboard,
    File,

    /// <summary>The answer to a sum that was typed. See <see cref="CalculatorProvider"/>.</summary>
    Calculation,

    /// <summary>A window that is open, in the Switch Windows list. Its use is not counted.</summary>
    Window,
}

/// <summary>What an item needs typed before it can run. The launcher collects it in argument mode.</summary>
/// <param name="Prompt">A few words shown in the empty input line, saying what to type.</param>
/// <param name="Run">Runs the item with the text that was typed. The text can be empty.</param>
public sealed record ItemArgument(string Prompt, Action<string> Run)
{
    private readonly string _prompt = Prompt;

    /// <summary>
    /// For an item whose words depend on how things stand when they are shown (a count, say):
    /// when set, <see cref="Prompt"/> asks this each time it is read, in place of the words the
    /// item was made with. An item is made once and listed for as long as Beacon runs, so words
    /// that are fixed when it is made cannot say what is so now. It is called on the UI
    /// thread, must answer at once, and must not throw. Null for every item with fixed words.
    /// </summary>
    public Func<string>? PromptNow { get; init; }

    /// <summary>A few words shown in the empty input line, saying what to type.</summary>
    public string Prompt => PromptNow?.Invoke() ?? _prompt;
}

/// <summary>Something more that can be done with an item, listed with its other actions (Ctrl+K).</summary>
/// <param name="Name">What the list of actions calls it, such as "Copy Path".</param>
/// <param name="Run">Does it. The window is hidden first, as for the item's own action.</param>
public sealed record ItemAction(string Name, Action Run)
{
    /// <summary>
    /// For an action that cannot be taken back: a few words that ask first, such as "Press
    /// Enter to delete this snippet". They are shown in the input line, a second Enter does
    /// the action and Esc gives it up. Null for an action that is done at once.
    /// </summary>
    public string? Question { get; init; }
}

/// <summary>One row in the launcher.</summary>
/// <param name="Id">Stable and unique across all providers, for example "app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App". Used as the frecency key.</param>
/// <param name="Run">
/// Primary action, run on Enter. Null for an item that cannot run without its
/// <paramref name="Argument"/>; Enter then asks for the argument instead.
/// </param>
/// <param name="IconKey">Opaque key the UI resolves to an image. Null means no icon.</param>
/// <param name="RunSecondary">Secondary action, run on Ctrl+Enter. Null means the item has none.</param>
/// <param name="Argument">Set when the item takes an argument, which Tab asks for. Null means it takes none.</param>
/// <param name="KeepOpen">
/// True for an item that shows its result in the launcher itself: the window is not hidden
/// when the item runs, on Enter or with its argument. The secondary action hides it as usual.
/// </param>
public sealed record SearchItem(
    string Id,
    string Title,
    string Subtitle,
    ItemKind Kind,
    Action? Run,
    string? IconKey = null,
    Action? RunSecondary = null,
    ItemArgument? Argument = null,
    bool KeepOpen = false)
{
    /// <summary>What else can be done with the item, after its own actions. Empty for most.</summary>
    public IReadOnlyList<ItemAction> Actions { get; init; } = [];
}

/// <summary>An item together with its match score for the current query. Higher is better.</summary>
public readonly record struct SearchResult(SearchItem Item, int Score);
