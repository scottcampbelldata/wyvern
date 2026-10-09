using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Wyvern.Core.Launcher;

/// <summary>One emoji or symbol that can be picked.</summary>
/// <param name="Text">The character itself, as it is pasted.</param>
/// <param name="Name">What it is called, such as "Face With Tears Of Joy".</param>
/// <param name="Group">The heading it stands under when everything is listed.</param>
public sealed record EmojiItem(string Text, string Name, string Group)
{
    /// <summary>What its use is counted under, so that the ones picked most can be listed first. An emoji in a skin tone counts as the emoji itself.</summary>
    public string Id => IdPrefix + (Plain ?? Text);

    /// <summary>True for an emoji of a person or a hand that can be had in a skin tone.</summary>
    public bool TakesSkinTone { get; init; }

    /// <summary>For an emoji that has been given a skin tone: the emoji as it is without one. Null otherwise.</summary>
    public string? Plain { get; init; }

    /// <summary>What every emoji's id starts with.</summary>
    public const string IdPrefix = "emoji:";
}

/// <summary>The skin tone the picker gives the emoji of people and hands.</summary>
public enum SkinTone
{
    /// <summary>None: the emoji as they are, in their own yellow.</summary>
    Default,
    Light,
    MediumLight,
    Medium,
    MediumDark,
    Dark,
}

/// <summary>One place in the grid: an emoji, and whether it is the selected one.</summary>
public sealed class EmojiCell(EmojiItem emoji) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public EmojiItem Emoji { get; } = emoji;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

/// <summary>One line of the grid as it is listed: a heading, or up to <see cref="EmojiViewModel.Columns"/> emoji side by side.</summary>
/// <param name="Heading">The heading's words, or null for a line of emoji.</param>
/// <param name="Cells">The emoji of the line, empty for a heading.</param>
public sealed record EmojiRow(string? Heading, IReadOnlyList<EmojiCell> Cells);

/// <summary>What the picker does with an emoji. The launcher's view model decides when; these do it.</summary>
/// <param name="Paste">Pastes the character into the window the owner was in.</param>
/// <param name="Copy">Puts the character on the clipboard and leaves it there.</param>
public sealed record EmojiActions(Action<string> Paste, Action<string> Copy)
{
    /// <summary>Does nothing. For code that has no picker.</summary>
    public static EmojiActions None { get; } = new(_ => { }, _ => { });
}

/// <summary>
/// The launcher's emoji picker: a grid that follows what is typed, with one emoji selected.
/// Kept free of UI types so it can be unit tested; the window only shows it. Use from one
/// thread only, the UI thread.
/// </summary>
public sealed class EmojiViewModel : INotifyPropertyChanged
{
    /// <summary>The picker's name.</summary>
    public const string Heading = "Search Emoji & Symbols";

    /// <summary>Shown in the empty input line while the picker is open.</summary>
    public const string FilterPrompt = "Search emoji and symbols...";

    /// <summary>How many emoji stand side by side.</summary>
    public const int Columns = 8;

    /// <summary>The heading over the emoji the owner has picked most, shown before anything is typed.</summary>
    public const string FrequentGroup = "Frequently Used";

    /// <summary>How many emoji stand under <see cref="FrequentGroup"/> at most: two lines.</summary>
    public const int MaxFrequent = Columns * 2;

    /// <summary>The heading over what a typed text found.</summary>
    public const string ResultsGroup = "Results";

    public const string NoneStatus = "Emoji cannot be shown on this PC.";
    public const string NoMatchStatus = "No emoji or symbol matches.";

    private readonly Func<IReadOnlyList<EmojiItem>> _all;
    private readonly Func<string, double> _boost;

    // The cells in the order they are read: line by line, each from the left.
    private readonly List<EmojiCell> _cells = [];

    // For each cell, the line of Rows it stands in and its place in that line.
    private readonly List<(int Row, int Column)> _places = [];
    private string _filter = "";
    private int _selected = -1;
    private SkinTone _tone;

    /// <param name="all">Gives every emoji there is, in the order they are listed in. It must answer from memory after the first time.</param>
    /// <param name="actions">What Enter and the copy key do.</param>
    /// <param name="boost">How much an emoji's past use counts for, by its id: more than 1 for one that has been picked. Left out by code that keeps no count.</param>
    public EmojiViewModel(Func<IReadOnlyList<EmojiItem>> all, EmojiActions? actions = null, Func<string, double>? boost = null)
    {
        _all = all;
        _boost = boost ?? (_ => 1.0);
        Actions = actions ?? EmojiActions.None;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the keys do with the selected emoji.</summary>
    public EmojiActions Actions { get; }

    /// <summary>True while the picker is on show in place of the result list.</summary>
    public bool IsOpen { get; private set; }

    /// <summary><see cref="Heading"/> while the picker is open, otherwise empty.</summary>
    public string Title => IsOpen ? Heading : "";

    /// <summary><see cref="FilterPrompt"/> while the picker is open, otherwise empty.</summary>
    public string Prompt => IsOpen ? FilterPrompt : "";

    /// <summary>The lines of the grid: headings, and under each its emoji, <see cref="Columns"/> to a line.</summary>
    public ObservableCollection<EmojiRow> Rows { get; } = [];

    /// <summary>A sentence shown instead of the grid when there is nothing in it. Empty otherwise.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The selected emoji, or null when there is none.</summary>
    public EmojiItem? Selected => _selected >= 0 && _selected < _cells.Count ? _cells[_selected].Emoji : null;

    /// <summary>The line of <see cref="Rows"/> the selected emoji stands in, or -1.</summary>
    public int SelectedRow => _selected >= 0 && _selected < _places.Count ? _places[_selected].Row : -1;

    /// <summary>
    /// What is typed while the picker is open. An emoji is kept when its name holds every word
    /// that was typed, whatever the case of the letters, or when it is the character typed.
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
                    Fill();
                }
            }
        }
    }

    /// <summary>
    /// The skin tone the emoji of people and hands are shown and pasted in. Changing it makes
    /// the grid again, as typing does. It is kept when the picker closes.
    /// </summary>
    public SkinTone Tone
    {
        get => _tone;
        set
        {
            if (_tone != value)
            {
                _tone = value;
                Raise(nameof(Tone));
                if (IsOpen)
                {
                    Fill();
                }
            }
        }
    }

    /// <summary>
    /// An emoji's text in a skin tone: the emoji without the mark that asks for its emoji
    /// form (U+FE0F), which a tone stands in for, and then the tone (U+1F3FB to U+1F3FF).
    /// The text itself for no tone.
    /// </summary>
    public static string Toned(string text, SkinTone tone) => tone == SkinTone.Default
        ? text
        : text.Replace("\uFE0F", "", StringComparison.Ordinal) + char.ConvertFromUtf32(0x1F3FB + (int)tone - 1);

    /// <summary>How a skin tone is written in settings.json.</summary>
    public static string ToneText(SkinTone tone) => tone switch
    {
        SkinTone.Light => "light",
        SkinTone.MediumLight => "medium-light",
        SkinTone.Medium => "medium",
        SkinTone.MediumDark => "medium-dark",
        SkinTone.Dark => "dark",
        _ => "default",
    };

    /// <summary>The skin tone a text names, whatever the case of its letters, or the default for any other text.</summary>
    public static SkinTone ToneOf(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "light" => SkinTone.Light,
        "medium-light" => SkinTone.MediumLight,
        "medium" => SkinTone.Medium,
        "medium-dark" => SkinTone.MediumDark,
        "dark" => SkinTone.Dark,
        _ => SkinTone.Default,
    };

    /// <summary>Opens the picker with nothing typed and the first emoji selected.</summary>
    public void Open()
    {
        IsOpen = true;
        _filter = "";
        AnnounceOpen();
        Fill();
    }

    /// <summary>Closes the picker and lets go of what it showed.</summary>
    /// <returns>False when the picker was not open.</returns>
    public bool Close()
    {
        if (!IsOpen)
        {
            return false;
        }

        IsOpen = false;
        _filter = "";
        Rows.Clear();
        _cells.Clear();
        _places.Clear();
        _selected = -1;
        Status = "";
        Raise(nameof(Status));
        Raise(nameof(Selected));
        AnnounceOpen();
        return true;
    }

    /// <summary>
    /// Moves the selection in the grid: so many places to the right (to the left when
    /// negative), then so many lines down (up when negative). Sideways it goes on from the end
    /// of one line to the start of the next; up and down it keeps its place in the line where
    /// the line it comes to is long enough. It stops at the first and at the last emoji.
    /// </summary>
    public void Move(int right, int down)
    {
        if (_cells.Count == 0)
        {
            return;
        }

        int at = Math.Clamp(_selected + right, 0, _cells.Count - 1);
        int step = Math.Sign(down);
        for (int left = Math.Abs(down); left > 0; left--)
        {
            int next = InLine(_places[at].Row, step, _places[at].Column);
            if (next < 0)
            {
                break;
            }

            at = next;
        }

        Select(at);
    }

    /// <summary>Selects the emoji of a cell that is listed, as a click on it does. A cell that is not listed changes nothing.</summary>
    public void Select(EmojiCell cell)
    {
        int at = _cells.IndexOf(cell);
        if (at >= 0)
        {
            Select(at);
        }
    }

    // The cell in the nearest line of emoji above (step -1) or below (step 1) the given line,
    // at the same place in it or at its end when it is shorter. -1 when there is no such line.
    private int InLine(int row, int step, int column)
    {
        for (int line = row + step; line >= 0 && line < Rows.Count; line += step)
        {
            int count = Rows[line].Cells.Count;
            if (count == 0)
            {
                continue;
            }

            // The cells of a line stand together in the order they are read.
            int first = _cells.IndexOf(Rows[line].Cells[0]);
            return first + Math.Min(column, count - 1);
        }

        return -1;
    }

    private void Select(int at)
    {
        if (at == _selected)
        {
            return;
        }

        if (_selected >= 0 && _selected < _cells.Count)
        {
            _cells[_selected].IsSelected = false;
        }

        _selected = at;
        if (_selected >= 0 && _selected < _cells.Count)
        {
            _cells[_selected].IsSelected = true;
        }

        Raise(nameof(Selected));
    }

    // Makes the grid again from what is typed, and selects its first emoji.
    private void Fill()
    {
        var all = _all();
        Rows.Clear();
        _cells.Clear();
        _places.Clear();
        _selected = -1;

        string[] words = _filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            // What has been picked before, the most picked first, and then everything under
            // its own heading, in the order it was given.
            var frequent = all
                .Select(emoji => (Emoji: emoji, Boost: _boost(emoji.Id)))
                .Where(scored => scored.Boost > 1.0)
                .OrderByDescending(scored => scored.Boost)
                .Take(MaxFrequent)
                .Select(scored => scored.Emoji)
                .ToList();
            Add(FrequentGroup, frequent);
            foreach (var group in all.GroupBy(emoji => emoji.Group))
            {
                Add(group.Key, group.ToList());
            }
        }
        else
        {
            Add(ResultsGroup, all.Where(emoji => Matches(emoji, words)).ToList());
        }

        Status = _cells.Count > 0 ? "" : all.Count == 0 ? NoneStatus : NoMatchStatus;
        Raise(nameof(Status));
        if (_cells.Count > 0)
        {
            _selected = 0;
            _cells[0].IsSelected = true;
        }

        Raise(nameof(Selected));
    }

    private void Add(string heading, IReadOnlyList<EmojiItem> emoji)
    {
        if (emoji.Count == 0)
        {
            return;
        }

        Rows.Add(new EmojiRow(heading, []));
        for (int from = 0; from < emoji.Count; from += Columns)
        {
            var line = new List<EmojiCell>(Columns);
            for (int i = from; i < Math.Min(from + Columns, emoji.Count); i++)
            {
                // An emoji of a person or a hand is shown, and pasted, in the tone that is chosen.
                var shown = _tone != SkinTone.Default && emoji[i].TakesSkinTone
                    ? emoji[i] with { Text = Toned(emoji[i].Text, _tone), Plain = emoji[i].Text }
                    : emoji[i];
                var cell = new EmojiCell(shown);
                line.Add(cell);
                _cells.Add(cell);
                _places.Add((Rows.Count, i - from));
            }

            Rows.Add(new EmojiRow(null, line));
        }
    }

    private static bool Matches(EmojiItem emoji, string[] words)
    {
        foreach (string word in words)
        {
            if (!emoji.Name.Contains(word, StringComparison.OrdinalIgnoreCase) && emoji.Text != word)
            {
                return false;
            }
        }

        return true;
    }

    private void AnnounceOpen()
    {
        Raise(nameof(IsOpen));
        Raise(nameof(Title));
        Raise(nameof(Prompt));
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
