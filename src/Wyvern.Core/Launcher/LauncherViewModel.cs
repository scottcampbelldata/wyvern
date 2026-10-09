using System.Collections.ObjectModel;
using System.ComponentModel;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Commands;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Core.Launcher;

/// <summary>
/// Describes an action that threw. It carries the item's id and not its title: a title can be
/// the name of a snippet or a quicklink, which must not reach the log or a notification. Pass
/// the id through <c>LogSafe.ItemId</c> before logging it.
/// </summary>
public sealed class ActionFailedEventArgs(string itemId, Exception exception) : EventArgs
{
    public string ItemId { get; } = itemId;

    public Exception Exception { get; } = exception;
}

/// <summary>
/// State and behaviour of the launcher window, kept free of UI types so it can be unit tested.
/// </summary>
/// <remarks>
/// The launcher is in one of four modes. In search mode the input line is a query and the
/// results follow it. In argument mode one item has been chosen that needs something typed
/// before it can run: the input line holds that text, there are no results, and Enter runs
/// the item with it. In output mode an item that keeps the launcher open has been run and
/// <see cref="Output"/> shows what came of it: there are no results, nothing can be run, and
/// Esc goes back to search mode. In clipboard mode <see cref="Clipboard"/> shows the clipboard
/// history: the input line filters it, there are no results, Enter pastes the selected entry,
/// and Esc goes back to search mode.
/// </remarks>
public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private readonly SearchEngine _engine;
    private readonly UsageStore _usage;
    private string _query = "";
    private int _selectedIndex = -1;
    private SearchItem? _argumentItem;

    // What Ask last put into the input line, and whether its answer leaves the launcher open. See Ask.
    private SearchItem? _asked;
    private bool _askedStays;

    // The list of its own that is open in place of the launcher's, or null. See OpenScope.
    private SearchScope? _scope;

    // The heading each listed item stands under, by its id. Made again with the results.
    private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);
    private string _searchText = "";

    /// <param name="output">
    /// The output view. The provider whose items keep the launcher open is given the same one,
    /// and opens it when such an item runs. Left out by code that has no such items.
    /// </param>
    /// <param name="clipboard">
    /// The clipboard history view. The Clipboard History command is given the same one, and
    /// opens it. Left out by code that has no clipboard history.
    /// </param>
    /// <param name="emoji">
    /// The emoji picker. Its command is given the same one, and opens it. Left out by code
    /// that has no picker.
    /// </param>
    public LauncherViewModel(SearchEngine engine, UsageStore usage, OutputViewModel? output = null, ClipboardViewModel? clipboard = null, EmojiViewModel? emoji = null)
    {
        _engine = engine;
        _usage = usage;
        Output = output ?? new OutputViewModel();
        Clipboard = clipboard ?? new ClipboardViewModel(() => [], new SystemClock());
        Emoji = emoji ?? new EmojiViewModel(() => []);

        // The picker is opened from outside as well, by its command.
        Emoji.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(EmojiViewModel.IsOpen))
            {
                EmojiOpenedOrClosed();
            }
        };

        // The view is opened from outside: by its command, which is an item like any other, and
        // by its hotkey. Whichever it was, the launcher follows.
        Clipboard.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ClipboardViewModel.IsOpen))
            {
                ClipboardOpenedOrClosed();
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised just before an item's action runs, so the window can hide first. Not raised for an item that keeps the launcher open.</summary>
    public event EventHandler? Dismissed;

    /// <summary>Raised when an item's action throws.</summary>
    public event EventHandler<ActionFailedEventArgs>? ActionFailed;

    /// <summary>The heading over the owner's favorites, the first of all before anything is typed.</summary>
    public const string FavoritesSection = "Favorites";

    public const string AddToFavorites = "Add to Favorites";
    public const string RemoveFromFavorites = "Remove from Favorites";
    public const string MoveUpInFavorites = "Move Up in Favorites";
    public const string MoveDownInFavorites = "Move Down in Favorites";
    public const string SetAlias = "Set Alias";

    /// <summary>What the empty input line says while an alias is typed.</summary>
    public const string AliasPrompt = "One short word, or nothing to remove its alias";

    /// <summary>Shows a few fixed words for a moment, to say what was done with a favorite or an alias. Nothing is said when it is left out.</summary>
    public Action<string> Say { get; init; } = _ => { };

    /// <summary>The heading over the items the owner uses most, shown before anything is typed.</summary>
    public const string SuggestionsSection = "Suggestions";

    /// <summary>The heading over the launcher's own commands, shown before anything is typed.</summary>
    public const string CommandsSection = "Commands";

    /// <summary>The heading over the owner's quicklinks, shown before anything is typed.</summary>
    public const string QuicklinksSection = "Quicklinks";

    /// <summary>The heading over the owner's snippets, shown before anything is typed.</summary>
    public const string SnippetsSection = "Snippets";

    /// <summary>The heading over the owner's scripts, shown before anything is typed.</summary>
    public const string ScriptsSection = "Scripts";

    /// <summary>The heading over the installed apps, shown before anything is typed.</summary>
    public const string AppsSection = "Applications";

    /// <summary>The heading over what a typed text found.</summary>
    public const string ResultsSection = "Results";

    /// <summary>How many items stand under <see cref="SuggestionsSection"/> at most.</summary>
    public const int MaxSuggestions = 6;

    /// <summary>The item that is always the first suggestion when it exists: the clipboard history is what the launcher is opened for most.</summary>
    public const string FirstSuggestion = "command:clipboard-history";

    /// <summary>
    /// What is listed: before anything is typed, the suggestions, the commands and the apps,
    /// in that order; after that, what the typed text found. Items under one heading stand together.
    /// </summary>
    public ObservableCollection<SearchItem> Results { get; } = [];

    /// <summary>The heading a listed item stands under, or empty for an item that is not listed.</summary>
    public string SectionOf(SearchItem item) => _sections.GetValueOrDefault(item.Id, "");

    /// <summary>What an item that keeps the launcher open shows. While it is open the launcher is in output mode.</summary>
    public OutputViewModel Output { get; }

    /// <summary>The clipboard history view. While it is open the launcher is in clipboard mode.</summary>
    public ClipboardViewModel Clipboard { get; }

    /// <summary>The emoji picker. While it is open the input line filters it, there are no results, and Enter pastes the selected emoji.</summary>
    public EmojiViewModel Emoji { get; }

    /// <summary>True while a list of its own is open in place of the launcher's, such as the open windows: what is typed is looked for in it alone.</summary>
    public bool InScope => _scope is not null;

    /// <summary>The name of the list that is open in place of the launcher's. Empty when none is.</summary>
    public string ScopeTitle => _scope?.Title ?? "";

    /// <summary>What to type, shown in the input line while that list is open and the line is empty. Empty when none is.</summary>
    public string ScopePrompt => _scope?.Prompt ?? "";

    /// <summary>
    /// The text in the input line: the search text, in argument mode the argument being typed,
    /// or in clipboard mode what the history is filtered by. Only search text is searched for,
    /// and nothing is searched for in output mode.
    /// </summary>
    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
            {
                return;
            }

            _query = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
            if (Clipboard.IsOpen)
            {
                Clipboard.Filter = value;
            }
            else if (Emoji.IsOpen)
            {
                Emoji.Filter = value;
            }
            else if (!InArgumentMode)
            {
                RefreshResults();
            }
        }
    }

    /// <summary>Index into <see cref="Results"/>, or -1 when there are none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value)
            {
                return;
            }

            _selectedIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
        }
    }

    /// <summary>True while the input line is collecting the argument for one item.</summary>
    public bool InArgumentMode => _argumentItem is not null;

    /// <summary>The title of the item the argument is for, shown beside the input. Empty in search mode.</summary>
    public string ArgumentTitle => _argumentItem?.Title ?? "";

    /// <summary>What to type, shown in the input line while it is empty. Empty in search mode.</summary>
    public string ArgumentPrompt => _argumentItem?.Argument?.Prompt ?? "";

    /// <summary>Moves the selection up (negative) or down (positive), stopping at either end. In clipboard mode it is the history's selection that moves.</summary>
    public void MoveSelection(int delta)
    {
        if (Clipboard.IsOpen)
        {
            Clipboard.MoveSelection(delta);
        }
        else if (Results.Count > 0)
        {
            SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
        }
    }

    /// <summary>
    /// What Tab does: starts collecting the argument for the selected item. The search text is
    /// remembered and the input line is emptied. Does nothing when nothing is selected, when
    /// the selected item takes no argument, when an argument is already being collected, or in
    /// output mode or clipboard mode.
    /// </summary>
    /// <returns>True when argument mode was entered.</returns>
    public bool EnterArgumentMode()
    {
        if (InArgumentMode
            || Output.IsOpen
            || Clipboard.IsOpen
            || Emoji.IsOpen
            || SelectedIndex < 0
            || SelectedIndex >= Results.Count
            || Results[SelectedIndex].Argument is null)
        {
            return false;
        }

        _argumentItem = Results[SelectedIndex];
        _searchText = _query;
        Results.Clear();
        _selectedIndex = -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
        SetInput("");
        AnnounceMode();
        return true;
    }

    /// <summary>
    /// What Esc does first: gives up the argument, puts the search text back and selects the
    /// item again, with the window still open.
    /// </summary>
    /// <returns>False in search mode, where there is nothing to leave and Esc hides the window instead.</returns>
    public bool LeaveArgumentMode()
    {
        if (_argumentItem is not { } item)
        {
            return false;
        }

        string searchText = _searchText;
        _argumentItem = null;
        _searchText = "";
        AnnounceMode();
        SetInput(searchText);
        RefreshResults();

        for (int i = 0; i < Results.Count; i++)
        {
            if (Results[i].Id == item.Id)
            {
                SelectedIndex = i;
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// What Esc does before anything else: closes the output view and goes back to search mode,
    /// with the window still open. Whatever the view was showing keeps running.
    /// </summary>
    /// <returns>False when the output view was not open.</returns>
    public bool CloseOutput()
    {
        if (!Output.Close())
        {
            return false;
        }

        if (!InArgumentMode)
        {
            RefreshResults();
        }

        return true;
    }

    /// <summary>
    /// Opens the clipboard history view, from whichever mode the launcher is in. This is what
    /// the clipboard hotkey does; the Clipboard History command opens the same view itself.
    /// </summary>
    public void OpenClipboard() => Clipboard.Open();

    /// <summary>
    /// What Esc does in clipboard mode: closes the history and goes back to an empty search,
    /// with the window still open.
    /// </summary>
    /// <returns>False when the clipboard history was not open.</returns>
    public bool CloseClipboard() => Clipboard.Close();

    /// <summary>
    /// Opens a list of its own in place of the launcher's, from whichever mode the launcher is
    /// in: the input line is emptied, and what is typed from now on is looked for in that
    /// list alone, until Esc or until the window hides. The command that opens one keeps the
    /// launcher open.
    /// </summary>
    public void OpenScope(SearchScope scope)
    {
        Output.Close();
        Clipboard.Close();
        Emoji.Close();
        if (InArgumentMode)
        {
            _argumentItem = null;
            _searchText = "";
            AnnounceMode();
        }

        _scope = scope;
        AnnounceScope();
        SetInput("");
        RefreshResults();
    }

    /// <summary>What Esc does while such a list is open: closes it and goes back to an empty search, with the window still open.</summary>
    /// <returns>False when none was open.</returns>
    public bool CloseScope()
    {
        if (_scope is null)
        {
            return false;
        }

        _scope = null;
        AnnounceScope();
        SetInput("");
        RefreshResults();
        return true;
    }

    /// <summary>What Esc does while the emoji picker is open: closes it and goes back to an empty search, with the window still open.</summary>
    /// <returns>False when the picker was not open.</returns>
    public bool CloseEmoji() => Emoji.Close();

    /// <summary>Puts the selected emoji on the clipboard without pasting it, and hides the window first, as a paste does.</summary>
    public void CopySelectedEmoji()
    {
        if (Emoji.IsOpen && Emoji.Selected is { } emoji)
        {
            RunEmoji(emoji, () => Emoji.Actions.Copy(emoji.Text));
        }
    }

    /// <summary>
    /// What Ctrl+Delete does in clipboard mode: deletes the selected entry. Its row goes at
    /// once and the window stays open. Does nothing when the history is not open or nothing is
    /// selected.
    /// </summary>
    public void DeleteSelectedClip()
    {
        if (!Clipboard.IsOpen || Clipboard.Remove() is not { } entry)
        {
            return;
        }

        try
        {
            Clipboard.Actions.Delete(entry.Id);
        }
        catch (Exception ex)
        {
            ActionFailed?.Invoke(this, new ActionFailedEventArgs(entry.ItemId, ex));
        }
    }

    /// <summary>
    /// Puts the selected entry of the clipboard history on the clipboard again without pasting
    /// it anywhere, and hides the window first, as a paste does.
    /// </summary>
    public void CopySelectedClip()
    {
        if (Clipboard.IsOpen && Clipboard.Selected is { } entry)
        {
            RunClip(entry, () => Clipboard.Actions.Copy(entry));
        }
    }

    /// <summary>
    /// Puts the text that was read in the picture of the selected entry on the clipboard,
    /// and hides the window first. Does nothing for an entry whose picture holds no text.
    /// </summary>
    public void CopySelectedClipPictureText()
    {
        if (Clipboard.IsOpen && Clipboard.Selected is { } entry && Clipboard.PictureText(entry) is { Length: > 0 } text)
        {
            RunClip(entry, () => Clipboard.Actions.CopyText(text));
        }
    }

    /// <summary>
    /// Pastes the text of the selected entry of the clipboard history without its formatting,
    /// and hides the window first, as a paste does. Does nothing for an entry without text.
    /// </summary>
    public void PasteSelectedClipAsText()
    {
        if (Clipboard.IsOpen && Clipboard.Selected is { Text: not null } entry)
        {
            RunClip(entry, () => Clipboard.Actions.PasteText(entry));
        }
    }

    /// <summary>
    /// Opens the form that makes a snippet with the text of the selected entry in it, and
    /// hides the window first. Does nothing for an entry without text.
    /// </summary>
    public void SaveSelectedClipAsSnippet()
    {
        if (Clipboard.IsOpen && Clipboard.Selected is { Text: not null } entry)
        {
            RunClip(entry, () => Clipboard.Actions.SaveAsSnippet(entry));
        }
    }

    /// <summary>
    /// Does one of the selected item's further actions (see <see cref="SearchItem.Actions"/>):
    /// the window is hidden first and the use is counted, as for the item's own action. An
    /// action that is not one of the selected item's is not done.
    /// </summary>
    public void RunAction(ItemAction action)
    {
        if (Output.IsOpen
            || Clipboard.IsOpen
            || Emoji.IsOpen
            || InArgumentMode
            || SelectedIndex < 0
            || SelectedIndex >= Results.Count
            || !Results[SelectedIndex].Actions.Contains(action))
        {
            return;
        }

        var item = Results[SelectedIndex];
        if (action.Question is { } question)
        {
            // It asks first: the input line takes the question, and Enter there does it.
            Ask(item, action.Name, question, _ => action.Run());
        }
        else
        {
            Run(item, action.Run, keepOpen: false);
        }
    }

    /// <summary>
    /// Asks for something about a listed item in the input line, the way an item that takes
    /// an argument is asked for it: the words are shown while the line is empty, Enter hands
    /// what was typed to <paramref name="answered"/>, and Esc goes back to the list with the
    /// item selected. Used for a question before something that cannot be taken back, and
    /// for a few words to keep about an item, such as its alias.
    /// </summary>
    /// <param name="about">The item it is about. A failure is reported by its id.</param>
    /// <param name="name">What is being asked for, shown in front of the input line.</param>
    /// <param name="stay">
    /// True when the launcher stays open once it is answered, back in the list the question
    /// was asked from with the item selected. Otherwise the window hides first, as for any action.
    /// </param>
    /// <remarks>What is answered is not counted as a use of the item.</remarks>
    public void Ask(SearchItem about, string name, string prompt, Action<string> answered, bool stay = false)
    {
        if (InArgumentMode || Output.IsOpen || Clipboard.IsOpen || Emoji.IsOpen)
        {
            return;
        }

        _argumentItem = new SearchItem(about.Id, name, "", about.Kind, Run: null, Argument: new ItemArgument(prompt, answered));
        _asked = _argumentItem;
        _askedStays = stay;
        _searchText = _query;
        Results.Clear();
        _selectedIndex = -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
        SetInput("");
        AnnounceMode();
    }

    // The item that a favorite or an alias would be made of: the selected one, in the
    // launcher's own list, of a kind that is there again the next time. Null otherwise.
    private SearchItem? Markable()
    {
        if (_engine.Marks is null
            || InScope
            || InArgumentMode
            || Output.IsOpen
            || Clipboard.IsOpen
            || Emoji.IsOpen
            || SelectedIndex < 0
            || SelectedIndex >= Results.Count)
        {
            return null;
        }

        var item = Results[SelectedIndex];
        return item.Kind is ItemKind.App or ItemKind.Command or ItemKind.Quicklink or ItemKind.Snippet or ItemKind.Script or ItemKind.File ? item : null;
    }

    /// <summary>True when the selected item can be made a favorite and given an alias.</summary>
    public bool CanMarkSelected => Markable() is not null;

    /// <summary>True when the selected item is one of the favorites.</summary>
    public bool SelectedIsFavorite => Markable() is { } item && _engine.Marks!.IsFavorite(item.Id);

    /// <summary>The alias of an item, or empty when it has none. Shown after its name.</summary>
    public string AliasOf(SearchItem item) => _engine.Marks?.AliasOf(item.Id) ?? "";

    /// <summary>
    /// Makes the selected item a favorite, or takes it from among them. The window stays open
    /// and the item stays selected, wherever in the list it stands now.
    /// </summary>
    public void ToggleFavoriteSelected()
    {
        if (Markable() is not { } item)
        {
            return;
        }

        Say(_engine.Marks!.ToggleFavorite(item.Id) switch
        {
            true => "Added to Favorites",
            false => "Removed from Favorites",
            null => "Favorites Are Full",
        });
        Refresh();
    }

    /// <summary>Moves the selected favorite one place up its list (a negative number) or down. The item stays selected.</summary>
    public void MoveSelectedFavorite(int by)
    {
        if (Markable() is { } item && _engine.Marks!.MoveFavorite(item.Id, by))
        {
            Refresh();
        }
    }

    /// <summary>
    /// Asks in the input line for the alias of the selected item. Enter there gives it that
    /// alias, or takes its alias away when nothing was typed, and goes back to the list.
    /// </summary>
    public void AskAliasForSelected()
    {
        if (Markable() is not { } item)
        {
            return;
        }

        var marks = _engine.Marks!;
        Ask(item, SetAlias, AliasPrompt, stay: true, answered: typed => Say(marks.SetAlias(item.Id, typed) switch
        {
            AliasChange.Set => "Alias Set",
            AliasChange.Removed => "Alias Removed",
            AliasChange.Taken => "That Alias Is Taken",
            AliasChange.NotOneWord => "An Alias Is One Word",
            AliasChange.TooLong => "An Alias Is 20 Characters at Most",
            _ => "No Alias",
        }));
    }

    /// <summary>
    /// What Ctrl+Shift+P does in clipboard mode: pins the selected entry, or unpins it. The
    /// window stays open and the entry stays selected.
    /// </summary>
    public void TogglePinSelectedClip()
    {
        if (Clipboard.IsOpen)
        {
            Clipboard.TogglePin();
        }
    }

    /// <summary>
    /// What Ctrl+S does in clipboard mode: saves a copy of the selected entry's pictures, and
    /// hides the window first, because Explorer is about to show the copy. Does nothing for
    /// an entry that holds no picture.
    /// </summary>
    public void SaveSelectedClip()
    {
        if (Clipboard.IsOpen && Clipboard.Selected is { HasPicture: true } entry)
        {
            RunClip(entry, () => Clipboard.Actions.Save(entry));
        }
    }

    /// <summary>
    /// Call when what the providers answer from has changed while results may be on screen:
    /// the library was loaded again, because a file was edited or sync brought one in. The
    /// search for what is typed is made again, so that no row stays for an item that has gone
    /// or changed, and the selection stays on the same item when it is still listed. Outside
    /// search mode there are no results, and nothing is done.
    /// </summary>
    /// <remarks>
    /// Call it on the UI thread only: it changes <see cref="Results"/>, which the window is
    /// bound to, and <see cref="SearchEngine.Search"/> is for one thread. What tells of a
    /// change (<see cref="LibraryService"/>'s <c>changed</c>) does so from a background
    /// thread, so whoever gets that call has to post this one to the UI thread.
    /// </remarks>
    public void Refresh()
    {
        if (InArgumentMode || Output.IsOpen || Clipboard.IsOpen || Emoji.IsOpen)
        {
            return;
        }

        string? selected = SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex].Id : null;
        RefreshResults();
        for (int i = 0; selected is not null && i < Results.Count; i++)
        {
            if (Results[i].Id == selected)
            {
                SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Call when Backspace is pressed. With nothing left to delete in argument mode, Backspace
    /// leaves argument mode, the same as Esc.
    /// </summary>
    /// <returns>True when the key was used for that. False when it should delete a character as usual.</returns>
    public bool Backspace() => InArgumentMode && _query.Length == 0 && LeaveArgumentMode();

    /// <summary>
    /// Call whenever the window is hidden, whatever the reason: back to search mode with an
    /// empty input line and the output view and the clipboard history closed, so the launcher
    /// always opens the same way.
    /// </summary>
    public void Reset()
    {
        // Whatever is listening to either view may throw as it closes. The other is closed and
        // the input is emptied all the same: the window is hidden by now, and must not open
        // again holding the text that was typed or showing what was copied.
        try
        {
            Output.Close();
        }
        finally
        {
            try
            {
                Clipboard.Close();
            }
            finally
            {
                try
                {
                    Emoji.Close();
                }
                finally
                {
                    bool hadScope = _scope is not null;
                    if (hadScope)
                    {
                        _scope = null;
                        AnnounceScope();
                    }

                    ClearInput();
                    if (hadScope)
                    {
                        // The input may have been empty already, and then nothing was
                        // looked for again: the launcher's own list is put back.
                        RefreshResults();
                    }
                }
            }
        }
    }

    /// <summary>
    /// What Enter (primary) and Ctrl+Enter (secondary) do. In search mode it runs the selected
    /// item's action, records the use and clears the input; an item that has no primary action
    /// because it cannot run without an argument goes into argument mode instead. In argument
    /// mode Enter runs the item with the text typed, which may be empty, and returns to search
    /// mode. Does nothing when nothing is selected, when the item has no secondary action, for
    /// Ctrl+Enter in argument mode, or in output mode. In clipboard mode Enter pastes the
    /// selected entry, and Ctrl+Enter opens it in the browser when it is a link and does
    /// nothing otherwise.
    /// </summary>
    public void RunSelected(bool secondary = false)
    {
        if (Output.IsOpen)
        {
            return;
        }

        if (Clipboard.IsOpen)
        {
            if (Clipboard.Selected is not { } entry)
            {
                return;
            }

            if (!secondary)
            {
                RunClip(entry, () => Clipboard.Actions.Paste(entry));
            }
            else if (entry.Link is { } link)
            {
                RunClip(entry, () => Clipboard.Actions.OpenLink(link));
            }

            return;
        }

        if (Emoji.IsOpen)
        {
            if (!secondary && Emoji.Selected is { } emoji)
            {
                RunEmoji(emoji, () => Emoji.Actions.Paste(emoji.Text));
            }

            return;
        }

        if (_argumentItem is { } waiting)
        {
            if (!secondary && waiting.Argument is { } argument)
            {
                string text = _query;
                if (ReferenceEquals(waiting, _asked) && _askedStays)
                {
                    // Answered in place: back to the list first, with the item selected,
                    // then the answer is used, and the list is made again to show it.
                    LeaveArgumentMode();
                    try
                    {
                        argument.Run(text);
                    }
                    catch (Exception ex)
                    {
                        ActionFailed?.Invoke(this, new ActionFailedEventArgs(waiting.Id, ex));
                    }

                    Refresh();
                }
                else
                {
                    Run(waiting, () => argument.Run(text), waiting.KeepOpen);
                }
            }

            return;
        }

        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
        {
            return;
        }

        var item = Results[SelectedIndex];
        if (secondary)
        {
            if (item.RunSecondary is { } runSecondary)
            {
                Run(item, runSecondary, keepOpen: false);
            }
        }
        else if (item.Run is { } run)
        {
            Run(item, run, item.KeepOpen);
        }
        else
        {
            EnterArgumentMode();
        }
    }

    // The window is dismissed before the action runs, so that whatever the action opens or
    // pastes into gets the focus instead of fighting this window for it. Whatever happens, even
    // a handler of either event that throws, the window is hidden by now, so the view model
    // must not be left in argument mode holding the text that was typed.
    //
    // An item that keeps the launcher open is the exception: the window stays, and the input is
    // emptied without closing the output view that the item's action has just opened.
    private void Run(SearchItem item, Action action, bool keepOpen)
    {
        try
        {
            if (!keepOpen)
            {
                Dismissed?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                action();

                // A window is there only for as long as it is open: its use is not counted.
                // Nor is the answer to a question about an item a use of the item.
                if (item.Kind != ItemKind.Window && !ReferenceEquals(item, _asked))
                {
                    _usage.RecordUse(item.Id);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (keepOpen)
                    {
                        // The action may have opened the output view before it threw, and nothing
                        // will ever complete it: it would say "Running" for ever. It is closed
                        // before the failure is reported, so that the report finds the launcher as it stays.
                        Output.Close();
                    }
                }
                finally
                {
                    ActionFailed?.Invoke(this, new ActionFailedEventArgs(item.Id, ex));
                }
            }
        }
        finally
        {
            if (keepOpen)
            {
                ClearInput();
            }
            else
            {
                Reset();
            }
        }
    }

    // What Enter, Ctrl+Enter and Ctrl+S do with a clipboard entry. The window is dismissed
    // first, as for any item, so that the window pasted into, the browser or Explorer gets the
    // focus. An entry is not an item: its use is not counted, and a failure names it by an id
    // that the log withholds. Whatever happens, the launcher ends in search mode, empty.
    private void RunClip(ClipEntry entry, Action action)
    {
        try
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ActionFailed?.Invoke(this, new ActionFailedEventArgs(entry.ItemId, ex));
            }
        }
        finally
        {
            Reset();
        }
    }

    // What Enter and the copy key do with an emoji. The window is dismissed first, as for any
    // item, so that the window pasted into gets the focus. Its use is counted, so that the
    // emoji picked most can be listed first. Whatever happens, the launcher ends in search
    // mode, empty.
    private void RunEmoji(EmojiItem emoji, Action action)
    {
        try
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
            try
            {
                action();
                _usage.RecordUse(emoji.Id);
            }
            catch (Exception ex)
            {
                ActionFailed?.Invoke(this, new ActionFailedEventArgs(emoji.Id, ex));
            }
        }
        finally
        {
            Reset();
        }
    }

    // The emoji picker has just been opened or closed. Opening it gives up whatever else the
    // launcher was doing; either way the input line starts empty and there are no results
    // from before.
    private void EmojiOpenedOrClosed()
    {
        if (Emoji.IsOpen)
        {
            Output.Close();
            Clipboard.Close();
            if (InArgumentMode)
            {
                _argumentItem = null;
                _searchText = "";
                AnnounceMode();
            }

            if (_scope is not null)
            {
                _scope = null;
                AnnounceScope();
            }
        }

        SetInput("");
        RefreshResults();
    }

    // The clipboard history has just been opened or closed. Opening it gives up whatever else
    // the launcher was doing; either way the input line starts empty and there are no results
    // from before.
    private void ClipboardOpenedOrClosed()
    {
        if (Clipboard.IsOpen)
        {
            Output.Close();
            Emoji.Close();
            if (InArgumentMode)
            {
                _argumentItem = null;
                _searchText = "";
                AnnounceMode();
            }

            if (_scope is not null)
            {
                _scope = null;
                AnnounceScope();
            }
        }

        SetInput("");
        RefreshResults();
    }

    private void AnnounceScope()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InScope)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScopeTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScopePrompt)));
    }

    // Back to an empty input line that is not collecting an argument.
    private void ClearInput()
    {
        if (InArgumentMode)
        {
            _argumentItem = null;
            _searchText = "";
            AnnounceMode();
        }

        // In argument mode the results were already empty, so only the text needs clearing.
        Query = "";
    }

    // Changes the text without searching for it.
    private void SetInput(string text)
    {
        if (_query != text)
        {
            _query = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Query)));
        }
    }

    private void AnnounceMode()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InArgumentMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArgumentTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArgumentPrompt)));
    }

    // What is listed before anything is typed. First the owner's favorites, in the owner's
    // order. Then the suggestions: the clipboard history, then what the owner has chosen most
    // and most lately. Then every command, quicklink, snippet, script and app, each kind
    // under its heading and by its title. An item that is a favorite or a suggestion is not
    // listed a second time.
    private IEnumerable<(SearchItem Item, string Section)> Browse()
    {
        var all = _engine.Browse();
        var favorites = _engine.Favorites(all);
        var favored = favorites.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in favorites)
        {
            yield return (item, FavoritesSection);
        }

        var suggested = new List<SearchItem>();
        if (all.FirstOrDefault(item => item.Id == FirstSuggestion) is { } first && !favored.Contains(first.Id))
        {
            suggested.Add(first);
        }

        suggested.AddRange(all
            .Where(item => item.Id != FirstSuggestion && !favored.Contains(item.Id))
            .Select(item => (Item: item, Boost: _usage.Boost(item.Id)))
            .Where(scored => scored.Boost > 1.0)
            .OrderByDescending(scored => scored.Boost)
            .ThenBy(scored => scored.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, MaxSuggestions - suggested.Count))
            .Select(scored => scored.Item));

        var taken = suggested.Select(item => item.Id).Concat(favored).ToHashSet(StringComparer.Ordinal);
        foreach (var item in suggested)
        {
            yield return (item, SuggestionsSection);
        }

        var rest = all
            .Where(item => !taken.Contains(item.Id))
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Each kind under its own heading, in this order. Anything of another kind that a
        // provider offers for browsing stands with the commands.
        (string Section, Func<SearchItem, bool> Belongs)[] groups =
        [
            (CommandsSection, item => item.Kind is not (ItemKind.Quicklink or ItemKind.Snippet or ItemKind.Script or ItemKind.App)),
            (QuicklinksSection, item => item.Kind == ItemKind.Quicklink),
            (SnippetsSection, item => item.Kind == ItemKind.Snippet),
            (ScriptsSection, item => item.Kind == ItemKind.Script),
            (AppsSection, item => item.Kind == ItemKind.App),
        ];
        foreach (var (section, belongs) in groups)
        {
            foreach (var item in rest.Where(belongs))
            {
                yield return (item, section);
            }
        }
    }

    private void RefreshResults()
    {
        Results.Clear();

        // While the output view or the clipboard history is open it stands where the results
        // would be, so there are none.
        _sections.Clear();
        if (!Output.IsOpen && !Clipboard.IsOpen && !Emoji.IsOpen)
        {
            if (_scope is { } scope)
            {
                // The list that is open in place of the launcher's: its rows, in its order,
                // under its one heading.
                foreach (var item in scope.Search(_query))
                {
                    if (_sections.TryAdd(item.Id, scope.Section))
                    {
                        Results.Add(item);
                    }
                }
            }
            else if (_query.Trim().Length == 0)
            {
                foreach (var (item, section) in Browse())
                {
                    // An id listed twice would stand under two headings: the first one counts.
                    if (_sections.TryAdd(item.Id, section))
                    {
                        Results.Add(item);
                    }
                }
            }
            else
            {
                foreach (var item in _engine.Search(_query))
                {
                    _sections[item.Id] = ResultsSection;
                    Results.Add(item);
                }
            }
        }

        // Always notify: a bound list control drops its own selection when the items are replaced.
        _selectedIndex = Results.Count > 0 ? 0 : -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
    }
}
