using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;

namespace Wyvern.Core.Launcher;

/// <summary>What the preview beside the list is showing. A kind can be added here without changing the others.</summary>
public enum ClipPreviewKind
{
    None,
    Text,
    Image,
    Files,
    Link,
}

/// <summary>One row of the clipboard history list.</summary>
/// <param name="Title">
/// The first line of the text, a link's label or address, "Image", the first file's name (its
/// path when it has no name) and a count, "Formatted text" for an entry that has only HTML or
/// rich text, or "Empty text" for one with nothing to show. Never longer than
/// <see cref="ClipboardViewModel.MaxTitleLength"/>.
/// </param>
/// <param name="Detail">For a link that has a label, its address. Otherwise empty.</param>
/// <param name="Marks">Short words for the other forms the entry has, such as "HTML  Image".</param>
/// <param name="Source">The program it was copied from, or empty.</param>
/// <param name="Time">When it was copied, in local time.</param>
public sealed record ClipRow(ClipEntry Entry, string Title, string Detail, string Marks, string Source, string Time)
{
    /// <summary>The heading the row stands under: "Today", "Yesterday", or the day it was copied.</summary>
    public string Group { get; init; } = "";

    /// <summary>What kind of thing the entry is, for the small picture in front of its row.</summary>
    public ClipPreviewKind Kind { get; init; }

    /// <summary>The full path of the entry's picture, shown small in front of its row, or null when it has none.</summary>
    public string? Picture { get; init; }

    /// <summary>True for a picture that has no name of its own, whose title is only the word "Image". The view says its size after that word.</summary>
    public bool Unnamed { get; init; }

    /// <summary>True for an entry the owner has pinned. It stands under <see cref="ClipboardViewModel.PinnedGroup"/>, before the days.</summary>
    public bool Pinned { get; init; }

    /// <summary>The type's name only: a row holds the owner's text, the program and the paths.</summary>
    public override string ToString() => nameof(ClipRow);
}

/// <summary>Which entries the list shows. A row is of the kind its small picture says.</summary>
public enum ClipTypeFilter
{
    All,
    Text,
    Images,
    Files,
    Links,
}

/// <summary>One line of what the view says about the selected entry, such as "Source" and the program's name.</summary>
public sealed record ClipInfo(string Label, string Value)
{
    /// <summary>The type's name only: the value can be the program the owner copied from.</summary>
    public override string ToString() => nameof(ClipInfo);
}

/// <summary>What the clipboard view does with an entry. The launcher's view model decides when; these do it.</summary>
/// <param name="Paste">Puts the entry on the clipboard and pastes it into the window the owner was in.</param>
/// <param name="Delete">Deletes the entry with this id from the history.</param>
/// <param name="Save">Saves a copy of the entry's pictures where the owner can find them.</param>
/// <param name="OpenLink">Opens a web address in the browser.</param>
public sealed record ClipboardActions(Action<ClipEntry> Paste, Action<string> Delete, Action<ClipEntry> Save, Action<string> OpenLink)
{
    /// <summary>Does nothing. For code that has no clipboard history.</summary>
    public static ClipboardActions None { get; } = new(_ => { }, _ => { }, _ => { }, _ => { });

    /// <summary>Puts the entry on the clipboard and leaves it there, without pasting it anywhere. Does nothing where no way of copying was given.</summary>
    public Action<ClipEntry> Copy { get; init; } = _ => { };

    /// <summary>Pastes the entry's text alone, without its formatting. Does nothing where no way of doing it was given.</summary>
    public Action<ClipEntry> PasteText { get; init; } = _ => { };

    /// <summary>Opens the form that makes a snippet, with the entry's text in it. Does nothing where no way of doing it was given.</summary>
    public Action<ClipEntry> SaveAsSnippet { get; init; } = _ => { };

    /// <summary>Shows a few fixed words for a moment, to say what was done while the view stays open. Shows nothing where no way of saying was given.</summary>
    public Action<string> Say { get; init; } = _ => { };

    /// <summary>Puts a text on the clipboard and leaves it there: the text that was read in an entry's picture. Does nothing where no way of copying was given.</summary>
    public Action<string> CopyText { get; init; } = _ => { };
}

/// <summary>
/// The launcher's clipboard history view: a list that follows what is typed, and a preview of
/// the row that is selected. Kept free of UI types so it can be unit tested; the window only
/// shows it.
/// </summary>
/// <remarks>
/// Everything here answers from memory: the history holds, for each entry, the start of its
/// text and the names of its files, and that is all the list, the filter and the text preview
/// use. A picture is named by its path and read from disk by the window when its row is
/// selected. What is shown is the owner's: nothing here is logged. Use from one thread only,
/// the UI thread.
/// </remarks>
public sealed class ClipboardViewModel : INotifyPropertyChanged
{
    /// <summary>Shown in front of the input line while the view is open.</summary>
    public const string Heading = "Clipboard History";

    /// <summary>Shown in the empty input line while the view is open.</summary>
    public const string FilterPrompt = "Type to filter entries...";

    /// <summary>How many rows are listed at most: the newest that match.</summary>
    public const int MaxRows = 200;

    /// <summary>How much of a text the preview shows.</summary>
    public const int MaxPreviewChars = 20_000;

    /// <summary>How many paths of a file list the preview shows.</summary>
    public const int MaxPreviewFiles = 100;

    /// <summary>The longest title of a row.</summary>
    public const int MaxTitleLength = 80;

    public const string EmptyStatus = "Nothing has been copied yet.";
    public const string NoMatchStatus = "Nothing in the clipboard history matches.";
    public const string LinkNote = "Enter pastes the address. Ctrl+Enter opens it in your browser.";
    public const string PictureNote = "Ctrl+S saves a copy in your Pictures folder, in \"Wyvern recovered\".";
    public const string FormattedOnlyNote = "Formatted text with no plain text of its own. Enter pastes it.";

    /// <summary>The heading the pinned entries stand under, before the days.</summary>
    public const string PinnedGroup = "Pinned";

    public const string PinnedWords = "Pinned";
    public const string UnpinnedWords = "Unpinned";
    public const string TooManyPinsWords = "No More Than 100 Entries Can Be Pinned";
    public const string PinContentGoneWords = "This Entry Is No Longer Whole and Cannot Be Pinned";
    public const string PinFailedWords = "The Entry Could Not Be Pinned";
    public const string UnpinFailedWords = "The Entry Could Not Be Unpinned";

    private readonly Func<IReadOnlyList<ClipEntry>> _history;
    private readonly IClock _clock;
    private readonly TimeZoneInfo? _zone;
    private readonly ClipPins? _pins;
    private readonly Func<ClipEntry, string?>? _pictureText;

    // Ids of entries the owner has just deleted. The history still lists them until the
    // background task has done the deleting, and they must not come back in the meantime.
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private string _filter = "";
    private ClipTypeFilter _typeFilter;
    private int _selectedIndex = -1;

    /// <param name="history">Gives the history as it stands, newest first. It must answer from memory.</param>
    /// <param name="actions">What Enter, Ctrl+Delete, Ctrl+S and Ctrl+Enter do.</param>
    /// <param name="zone">The time zone times are shown in. Left out everywhere except in tests: the machine's own.</param>
    /// <param name="pins">The entries the owner has pinned. Left out by code that has no pinning: nothing is listed as pinned and nothing can be.</param>
    /// <param name="pictureText">
    /// Gives the text that was read in the pictures of an entry, or null: an entry is then
    /// found by a word that stands in its picture. It must answer from memory. Left out by
    /// code that reads no pictures.
    /// </param>
    public ClipboardViewModel(
        Func<IReadOnlyList<ClipEntry>> history,
        IClock clock,
        ClipboardActions? actions = null,
        TimeZoneInfo? zone = null,
        ClipPins? pins = null,
        Func<ClipEntry, string?>? pictureText = null)
    {
        _pictureText = pictureText;
        _history = history;
        _clock = clock;
        _zone = zone;
        _pins = pins;
        Actions = actions ?? ClipboardActions.None;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the keys do with the selected entry.</summary>
    public ClipboardActions Actions { get; }

    /// <summary>True while the view is on show in place of the result list.</summary>
    public bool IsOpen { get; private set; }

    /// <summary><see cref="Heading"/> while the view is open, otherwise empty.</summary>
    public string Title => IsOpen ? Heading : "";

    /// <summary><see cref="FilterPrompt"/> while the view is open, otherwise empty.</summary>
    public string Prompt => IsOpen ? FilterPrompt : "";

    /// <summary>The rows that match the filter, newest first, at most <see cref="MaxRows"/>.</summary>
    public ObservableCollection<ClipRow> Rows { get; } = [];

    /// <summary>A sentence shown instead of the list when there are no rows. Empty otherwise.</summary>
    public string Status { get; private set; } = "";

    public ClipPreviewKind PreviewKind { get; private set; }

    /// <summary>The text the preview shows: the start of a text, a link, or a list of paths. Empty for a picture.</summary>
    public string PreviewText { get; private set; } = "";

    /// <summary>The full path of the picture the preview shows, or null. The window reads the file.</summary>
    public string? PreviewImage { get; private set; }

    /// <summary>A line under the preview: how much is shown, or what a key does. Can be empty.</summary>
    public string PreviewNote { get; private set; } = "";

    /// <summary>
    /// What is typed while the view is open. Rows are kept when their text, a file name, the
    /// program they came from or a link's label contains it, whatever the case of the letters.
    /// A space in it stands for white space of any kind and any length (a tab, a line break, a
    /// run of spaces), as a row shows it: "name amount" finds a text that holds "Name", a tab
    /// and "Amount".
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (_filter != value)
            {
                _filter = value;
                if (IsOpen)
                {
                    // Typing starts the list afresh: the first row is selected.
                    Fill(keep: null, fallback: 0);
                }
            }
        }
    }

    /// <summary>Which kinds of entries are listed. Changing it starts the list afresh, as typing does.</summary>
    public ClipTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (_typeFilter != value)
            {
                _typeFilter = value;
                Raise(nameof(TypeFilter));
                if (IsOpen)
                {
                    Fill(keep: null, fallback: 0);
                }
            }
        }
    }

    /// <summary>What is said about the selected entry, line by line: where it came from, what it is, how much of it there is, when it was copied. Empty when nothing is selected.</summary>
    public IReadOnlyList<ClipInfo> Info { get; private set; } = [];

    /// <summary>Index into <see cref="Rows"/>, or -1 when there are none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex != value)
            {
                _selectedIndex = value;
                Raise(nameof(SelectedIndex));
                ShowPreview();
            }
        }
    }

    /// <summary>The entry of the selected row, or null.</summary>
    public ClipEntry? Selected => _selectedIndex >= 0 && _selectedIndex < Rows.Count ? Rows[_selectedIndex].Entry : null;

    /// <summary>True when entries can be pinned at all.</summary>
    public bool CanPin => _pins is not null;

    /// <summary>True when the selected row is a pinned entry.</summary>
    public bool SelectedIsPinned => _selectedIndex >= 0 && _selectedIndex < Rows.Count && Rows[_selectedIndex].Pinned;

    /// <summary>
    /// Pins the selected entry, or unpins it when it is pinned, and says which in a few words.
    /// The row moves to where it now belongs and stays selected. An entry that is unpinned
    /// after the history has let go of it is gone from the list. Does nothing when nothing
    /// is selected or there is no pinning.
    /// </summary>
    public void TogglePin()
    {
        if (_pins is null || Selected is not { } entry)
        {
            return;
        }

        bool wasPinned = SelectedIsPinned;
        string words;
        try
        {
            if (wasPinned)
            {
                _pins.Unpin(entry.Id);
                words = UnpinnedWords;
            }
            else
            {
                words = _pins.Pin(entry) switch
                {
                    PinResult.TooMany => TooManyPinsWords,
                    PinResult.ContentGone => PinContentGoneWords,
                    _ => PinnedWords,
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            words = wasPinned ? UnpinFailedWords : PinFailedWords;
        }

        Fill(keep: entry.Id, fallback: _selectedIndex);
        Actions.Say(words);
    }

    /// <summary>Opens the view with an empty filter and the newest entry selected.</summary>
    public void Open()
    {
        IsOpen = true;
        _filter = "";
        _removed.Clear();
        AnnounceOpen();
        Fill(keep: null, fallback: 0);
    }

    /// <summary>Closes the view and lets go of what it showed.</summary>
    /// <returns>False when the view was not open.</returns>
    public bool Close()
    {
        if (!IsOpen)
        {
            return false;
        }

        IsOpen = false;
        _filter = "";
        _removed.Clear();
        Rows.Clear();
        Status = "";
        _selectedIndex = -1;
        Raise(nameof(SelectedIndex));
        Raise(nameof(Status));
        ShowPreview();
        AnnounceOpen();
        return true;
    }

    /// <summary>Moves the selection up (negative) or down (positive), stopping at either end.</summary>
    public void MoveSelection(int delta)
    {
        if (Rows.Count > 0)
        {
            SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Rows.Count - 1);
        }
    }

    /// <summary>
    /// Call when the history has changed. The rows are made again, and the row that was
    /// selected stays selected if it is still there. When it is gone (deleted on another
    /// machine, expired, scrubbed) the selection stays at the same place in the list, or at the
    /// last row if the list is now shorter: an Enter pressed before the owner has seen the list
    /// change must not paste an entry he was not looking at. Does nothing while the view is
    /// closed.
    /// </summary>
    public void Refresh()
    {
        if (IsOpen)
        {
            Fill(keep: Selected?.Id, fallback: _selectedIndex);
        }
    }

    /// <summary>
    /// Takes the selected row out of the list at once, for an entry that is being deleted. The
    /// row below takes its place.
    /// </summary>
    /// <returns>The entry that was selected, or null when nothing was.</returns>
    public ClipEntry? Remove()
    {
        if (Selected is not { } entry)
        {
            return null;
        }

        // A pinned entry that is deleted is unpinned as well: its copy must not stay behind.
        // If that cannot be done now the row goes all the same, and is back when the view is
        // next opened.
        if (SelectedIsPinned)
        {
            try
            {
                _pins?.Unpin(entry.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Actions.Say(UnpinFailedWords);
            }
        }

        // The rows are made again with the selection already at the index the row below took,
        // so that it is announced once, where it ends up.
        _removed.Add(entry.Id);
        Fill(keep: null, fallback: _selectedIndex);
        return entry;
    }

    // Makes the rows again from the history. With an id to keep, that entry's row is selected
    // when it is still listed; otherwise the row at the fallback index is, or the last row
    // when the list is shorter than that.
    private void Fill(string? keep, int fallback)
    {
        var history = _history();
        IReadOnlyList<ClipEntry> pinned = _pins?.All ?? [];
        if (_removed.Count > 0)
        {
            // Once neither the history nor the pins have an entry, its id need not be remembered.
            _removed.IntersectWith(history.Concat(pinned).Select(entry => entry.Id));
        }

        string filter = _filter.Trim();
        string[]? words = SpacedWords(filter);
        var now = Local(_clock.UtcNow);
        Rows.Clear();
        int selected = -1;

        // The pinned entries first, under a heading of their own, and then the history
        // without them: an entry that is pinned is listed once.
        var pinnedIds = pinned.Count == 0 ? null : new HashSet<string>(pinned.Select(entry => entry.Id), StringComparer.Ordinal);
        foreach (var entry in pinned.Concat(history.Where(entry => pinnedIds is null || !pinnedIds.Contains(entry.Id))))
        {
            if (Rows.Count == MaxRows)
            {
                break;
            }

            if (_removed.Contains(entry.Id) || !OfType(entry, _typeFilter) || !(Matches(entry, filter, words) || InPicture(entry, filter, words)))
            {
                continue;
            }

            if (entry.Id == keep)
            {
                selected = Rows.Count;
            }

            bool isPinned = pinnedIds is not null && pinnedIds.Contains(entry.Id);
            Rows.Add(isPinned ? ToRow(entry, now) with { Group = PinnedGroup, Pinned = true } : ToRow(entry, now));
        }

        Status = Rows.Count > 0 ? "" : filter.Length > 0 || _typeFilter != ClipTypeFilter.All ? NoMatchStatus : EmptyStatus;
        Raise(nameof(Status));

        // Always notify: a bound list control drops its own selection when the items are replaced.
        _selectedIndex = Rows.Count == 0 ? -1 : selected >= 0 ? selected : Math.Clamp(fallback, 0, Rows.Count - 1);
        Raise(nameof(SelectedIndex));
        ShowPreview();
    }

    /// <summary>What was read in the pictures of an entry, or null when nothing was, or no pictures are read.</summary>
    public string? PictureText(ClipEntry entry) => entry.HasPicture ? _pictureText?.Invoke(entry) : null;

    // True when what was typed stands in the text that was read in the entry's picture.
    private bool InPicture(ClipEntry entry, string filter, string[]? words) =>
        PictureText(entry) is { Length: > 0 } text && Contains(text, filter, words);

    /// <param name="filter">What was typed, trimmed.</param>
    /// <param name="words">The words of the filter when it has white space inside it, otherwise null. Worked out once for all the entries (see <see cref="SpacedWords"/>).</param>
    private static bool Matches(ClipEntry entry, string filter, string[]? words)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        if (Contains(entry.Text, filter, words) || Contains(entry.SourceApp, filter, words) || Contains(entry.Label, filter, words))
        {
            return true;
        }

        foreach (string path in entry.Files)
        {
            if (Contains(FileNameOf(path), filter, words))
            {
                return true;
            }
        }

        return false;
    }

    // The filter is found in the text as it is typed, whatever the case of the letters, and then
    // with a gap in it standing for white space of any kind and length: a row shows a text that
    // holds "Name", a tab and "Amount" as "Name Amount", so "name amount" finds it. The text is
    // looked through where it is and no copy of it is made, which matters for a history of
    // entries whose texts are 100,000 characters each, at each key.
    private static bool Contains(ReadOnlySpan<char> text, string filter, string[]? words) =>
        text.Contains(filter, StringComparison.OrdinalIgnoreCase) || (words is not null && ContainsSpaced(text, words));

    /// <summary>
    /// The words of a filter that has white space inside it: what is typed between two words,
    /// of whatever kind and however many, is one gap. Null for a filter of one word, which
    /// needs nothing but the plain look for it in the text: a text that holds it holds it in
    /// one stretch with no white space in it, whatever the white space round it is.
    /// </summary>
    private static string[]? SpacedWords(string filter)
    {
        string[] words = filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? words : null;
    }

    // True when the words stand in the text one after the other with white space, and nothing
    // else, between each two. The last may be the start of a word, as in the plain look. Each
    // place the first word stands is tried in turn, and each try looks no further than the
    // white space it passes through, so the time taken is in proportion to the text.
    private static bool ContainsSpaced(ReadOnlySpan<char> text, string[] words)
    {
        int from = 0;
        while (from < text.Length)
        {
            int found = text[from..].IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            int at = from + found + words[0].Length;
            bool all = true;
            for (int i = 1; i < words.Length && all; i++)
            {
                int gap = at;
                while (gap < text.Length && char.IsWhiteSpace(text[gap]))
                {
                    gap++;
                }

                all = gap > at && text[gap..].StartsWith(words[i], StringComparison.OrdinalIgnoreCase);
                at = gap + words[i].Length;
            }

            if (all)
            {
                return true;
            }

            from += found + 1;
        }

        return false;
    }

    private ClipRow ToRow(ClipEntry entry, DateTimeOffset now)
    {
        var marks = new List<string>();
        string title;
        string detail = "";
        bool titleIsFiles = false;
        bool titleIsImage = false;
        if (entry.Link is { } link)
        {
            // An address can be thousands of characters long, and like any text from outside
            // it is shown cut to fit and without characters that would change how it reads.
            // A label is shown only when it is fit to be (ClipLinks.ShownLabel, the one rule,
            // judged as it is cut for the row): an entry's label can have been written on
            // another machine, and one that fails the rule leaves the address alone.
            string? label = ClipLinks.ShownLabel(entry.Label, MaxTitleLength);
            title = label ?? NoteText.OnScreen(link, MaxTitleLength);
            detail = label is null ? "" : NoteText.OnScreen(link, MaxTitleLength);
            marks.Add("Link");
        }
        else if (FirstLine(entry.Text) is { Length: > 0 } line)
        {
            title = line;
        }
        else if (FilesTitle(entry.Files) is { } files)
        {
            title = files;
            titleIsFiles = true;
        }
        else if (entry.HasPicture)
        {
            title = "Image";
            titleIsImage = true;
        }
        else
        {
            title = entry.Html is not null || entry.Rtf is not null ? "Formatted text" : "Empty text";
        }

        if (entry.Html is not null)
        {
            marks.Add("HTML");
        }

        if (entry.Rtf is not null)
        {
            marks.Add("RTF");
        }

        if (entry.HasPicture && !titleIsImage)
        {
            marks.Add("Image");
        }

        if (entry.Files.Count > 0 && !titleIsFiles)
        {
            marks.Add("Files");
        }

        var kind = KindOf(entry);
        return new ClipRow(entry, title, detail, string.Join("  ", marks), NoteText.OnScreen(entry.SourceApp ?? ""), TimeText(entry.Time, now))
        {
            Group = GroupText(entry.Time, now),
            Kind = kind,
            Picture = kind == ClipPreviewKind.Image ? entry.PathOf(entry.Image ?? entry.Pictures[0].File) : null,
            Unnamed = titleIsImage,
        };
    }

    // What an entry is shown as, in the order the preview decides it: a link, a text, a
    // picture, a list of files; an entry with nothing but formatted text counts as text.
    private static ClipPreviewKind KindOf(ClipEntry entry) =>
        entry.Link is not null ? ClipPreviewKind.Link
        : entry.Text is { Length: > 0 } ? ClipPreviewKind.Text
        : entry.HasPicture ? ClipPreviewKind.Image
        : entry.Files.Count > 0 ? ClipPreviewKind.Files
        : ClipPreviewKind.Text;

    private static bool OfType(ClipEntry entry, ClipTypeFilter filter) => filter switch
    {
        ClipTypeFilter.Text => KindOf(entry) == ClipPreviewKind.Text,
        ClipTypeFilter.Images => KindOf(entry) == ClipPreviewKind.Image,
        ClipTypeFilter.Files => KindOf(entry) == ClipPreviewKind.Files,
        ClipTypeFilter.Links => KindOf(entry) == ClipPreviewKind.Link,
        _ => true,
    };

    // The heading a row stands under: the day it was copied, in words for today and yesterday.
    private string GroupText(DateTimeOffset time, DateTimeOffset now)
    {
        var day = Local(time).Date;
        return day == now.Date ? "Today"
            : day == now.Date.AddDays(-1) ? "Yesterday"
            : day.Year == now.Year ? day.ToString("MMMM d", CultureInfo.InvariantCulture)
            : day.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    // When an entry was copied, for the information beside the preview.
    private string CopiedText(DateTimeOffset time, DateTimeOffset now)
    {
        var local = Local(time);
        string clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == now.Date ? "Today at " + clock
            : local.Date == now.Date.AddDays(-1) ? "Yesterday at " + clock
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " at " + clock;
    }

    // A program's file name as a name to read: "SnagitEditor.exe" is "Snagit Editor", "chrome.exe"
    // is "Chrome", "OUTLOOK.EXE" is "Outlook". Only what is shown changes; the entry keeps the
    // file name, and the filter finds it by that.
    private static string ProgramName(string file)
    {
        string name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        if (name.Length == 0)
        {
            return "";
        }

        bool shouting = !name.Any(char.IsLower);
        var words = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool starts = i == 0 || !char.IsLetterOrDigit(name[i - 1]);
            if (shouting)
            {
                c = starts ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
            }
            else if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]))
            {
                // A capital after a small letter starts a new word.
                words.Append(' ');
            }
            else if (starts)
            {
                c = char.ToUpperInvariant(c);
            }

            words.Append(c);
        }

        return words.ToString();
    }

    private static int Words(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool space = char.IsWhiteSpace(c);
            if (!space && !inWord)
            {
                words++;
            }

            inWord = !space;
        }

        return words;
    }

    /// <summary>
    /// The title of a file list: the name of the first file and, for more than one, a count of
    /// the others. A first path that has no name (a root such as C:\) or whose name shows
    /// nothing gives the path itself. Null when the list is empty or nothing of the path can be
    /// shown, in which case the entry is titled as one that has nothing to show.
    /// </summary>
    private static string? FilesTitle(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        // The count is what must not be lost, so it is the name that gives way.
        string more = files.Count == 1 ? "" : $" and {(files.Count - 1).ToString(CultureInfo.InvariantCulture)} more";
        int room = MaxTitleLength - more.Length;
        string first = NoteText.OnScreen(FileName(files[0]), room);
        if (first.Length == 0)
        {
            first = NoteText.OnScreen(files[0], room);
        }

        return first.Length == 0 ? null : first + more;
    }

    /// <summary>The first line that has something on it, made fit to show in one row.</summary>
    private static string? FirstLine(string? text)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                // Looking at more than the row can show would be work for nothing on a very long line.
                string start = trimmed.Length > MaxTitleLength * 4 ? WholeStart(trimmed, MaxTitleLength * 4) : trimmed.ToString();
                string shown = NoteText.OnScreen(start, MaxTitleLength);
                if (shown.Length > 0)
                {
                    return shown;
                }
            }
        }

        return null;
    }

    // The first characters of a text, never ending on half of a character that takes two.
    private static string WholeStart(ReadOnlySpan<char> text, int length)
    {
        if (text.Length <= length)
        {
            return text.ToString();
        }

        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length].ToString();
    }

    // The part of a path after its last separator. Path.GetFileName is not used: a path in a
    // record can come from anywhere, and this must never throw.
    private static string FileName(string path) => FileNameOf(path).ToString();

    // The same, as a view of the path, for the filter, which looks at it at every key and makes
    // no copy of it.
    private static ReadOnlySpan<char> FileNameOf(string path) => path.AsSpan(path.LastIndexOfAny(['\\', '/']) + 1);

    private DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, _zone ?? TimeZoneInfo.Local);

    // Today's entries show the time alone; older ones show the date as well.
    private string TimeText(DateTimeOffset time, DateTimeOffset now)
    {
        var local = Local(time);
        return local.Date == now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private void ShowPreview()
    {
        var kind = ClipPreviewKind.None;
        string text = "";
        string? image = null;
        string note = "";
        if (Selected is { } entry)
        {
            if (entry.Link is { } link)
            {
                kind = ClipPreviewKind.Link;
                string? label = ClipLinks.ShownLabel(entry.Label);
                text = label is null ? NoteText.OnScreen(link) : label + "\n" + NoteText.OnScreen(link);
                note = LinkNote;
            }
            else if (entry.Text is { Length: > 0 } whole)
            {
                kind = ClipPreviewKind.Text;
                text = WholeStart(whole, MaxPreviewChars);
                if (entry.TextLength > text.Length)
                {
                    note = $"Showing the first {text.Length.ToString("N0", CultureInfo.InvariantCulture)} of {entry.TextLength.ToString("N0", CultureInfo.InvariantCulture)} characters.";
                }
            }
            else if (entry.HasPicture)
            {
                kind = ClipPreviewKind.Image;
                image = entry.PathOf(entry.Image ?? entry.Pictures[0].File);
                note = entry.Pictures.Count > 1
                    ? $"The first of {entry.Pictures.Count.ToString(CultureInfo.InvariantCulture)} pictures. {PictureNote}"
                    : PictureNote;
            }
            else if (entry.Files.Count > 0)
            {
                kind = ClipPreviewKind.Files;
                // Each path is one line, whatever it holds, and is shown whole.
                text = string.Join("\n", entry.Files.Take(MaxPreviewFiles).Select(path => NoteText.OnScreen(path)));
                if (entry.Files.Count > MaxPreviewFiles)
                {
                    note = $"Showing the first {MaxPreviewFiles.ToString(CultureInfo.InvariantCulture)} of {entry.Files.Count.ToString("N0", CultureInfo.InvariantCulture)} files.";
                }
            }
            else
            {
                kind = ClipPreviewKind.Text;
                note = FormattedOnlyNote;
            }
        }

        var info = new List<ClipInfo>();
        if (Selected is { } shown)
        {
            if (ProgramName(NoteText.OnScreen(shown.SourceApp ?? "")) is { Length: > 0 } source)
            {
                info.Add(new ClipInfo("Source", source));
            }

            info.Add(new ClipInfo("Type", kind switch
            {
                ClipPreviewKind.Link => "Link",
                ClipPreviewKind.Image => "Image",
                ClipPreviewKind.Files => "Files",
                _ => "Text",
            }));

            // A picture with text in it says how much: it is what the entry is found by.
            if (PictureText(shown) is { Length: > 0 } read)
            {
                info.Add(new ClipInfo("Words in Picture", Words(read).ToString("N0", CultureInfo.InvariantCulture)));
            }

            if (kind is ClipPreviewKind.Text or ClipPreviewKind.Link && shown.TextLength > 0)
            {
                info.Add(new ClipInfo("Characters", shown.TextLength.ToString("N0", CultureInfo.InvariantCulture)));
                if (shown.Text is { } held && held.Length == shown.TextLength)
                {
                    info.Add(new ClipInfo("Words", Words(held).ToString("N0", CultureInfo.InvariantCulture)));
                }
            }

            if (shown.Files.Count > 0)
            {
                info.Add(new ClipInfo("Files", shown.Files.Count.ToString("N0", CultureInfo.InvariantCulture)));
            }

            info.Add(new ClipInfo("Copied", CopiedText(shown.Time, Local(_clock.UtcNow))));
            if (NoteText.OnScreen(shown.Device) is { Length: > 0 } device)
            {
                info.Add(new ClipInfo("Device", device));
            }
        }

        Info = info;
        Raise(nameof(Info));
        PreviewKind = kind;
        PreviewText = text;
        PreviewImage = image;
        PreviewNote = note;
        Raise(nameof(PreviewKind));
        Raise(nameof(PreviewText));
        Raise(nameof(PreviewImage));
        Raise(nameof(PreviewNote));
    }

    private void AnnounceOpen()
    {
        Raise(nameof(IsOpen));
        Raise(nameof(Title));
        Raise(nameof(Prompt));
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
