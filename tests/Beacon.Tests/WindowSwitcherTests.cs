using Beacon.Core.Commands;
using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The Switch Windows list, and a list of its own in the launcher. The open windows are a list the test fills: no window is asked for or brought to the front.</summary>
public sealed class WindowSwitcherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Desktop _desktop = new();
    private readonly UsageStore _usage;
    private readonly LauncherViewModel _launcher;

    public WindowSwitcherTests()
    {
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        _launcher = new LauncherViewModel(new SearchEngine([new Listed([new SearchItem("command:reload", "Reload", "", ItemKind.Command, () => { })])], _usage), _usage);
        _launcher.Refresh();
        _desktop.Windows.Add(new OpenWindow(0x1A2B, "Quarterly report.docx - Word", "Microsoft Word", "icon:word"));
        _desktop.Windows.Add(new OpenWindow(0x20, "Inbox - Outlook", "Microsoft Outlook", null));
        _desktop.Windows.Add(new OpenWindow(0x30, "report-figures.xlsx - Excel", "Microsoft Excel", "icon:excel"));
    }

    public void Dispose() => _dir.Dispose();

    private sealed class Desktop : IWindowList
    {
        public List<OpenWindow> Windows { get; } = [];

        public List<nint> Activated { get; } = [];

        public int Asked { get; private set; }

        public IReadOnlyList<OpenWindow> List()
        {
            Asked++;
            return Windows;
        }

        public void Activate(nint handle) => Activated.Add(handle);

        public List<nint> Closed { get; } = [];

        public List<nint> Quit { get; } = [];

        public void Close(nint handle) => Closed.Add(handle);

        public void ForceQuit(nint handle) => Quit.Add(handle);
    }

    private sealed class OnlyLists : IWindowList
    {
        public IReadOnlyList<OpenWindow> List() => [];

        public void Activate(nint handle)
        {
        }
    }

    private sealed class Listed(SearchItem[] items) : ISearchProvider, IBrowsableProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);

        public IReadOnlyList<SearchItem> Browse() => items;
    }

    private string[] Titles() => _launcher.Results.Select(item => item.Title).ToArray();

    [Fact]
    public void Nothing_typed_lists_every_window_in_the_order_given_with_its_program_and_icon()
    {
        var rows = WindowSwitcher.Find(_desktop.Windows, "", _desktop.Activate);

        Assert.Equal(["Quarterly report.docx - Word", "Inbox - Outlook", "report-figures.xlsx - Excel"], rows.Select(row => row.Title));
        Assert.Equal(["Microsoft Word", "Microsoft Outlook", "Microsoft Excel"], rows.Select(row => row.Subtitle));
        Assert.Equal(["icon:word", null, "icon:excel"], rows.Select(row => row.IconKey));
        Assert.Equal(["window:1A2B", "window:20", "window:30"], rows.Select(row => row.Id));
        Assert.All(rows, row =>
        {
            Assert.Equal(ItemKind.Window, row.Kind);
            Assert.Null(row.Argument);
            Assert.Null(row.RunSecondary);
            Assert.False(row.KeepOpen);
        });
    }

    [Theory]
    [InlineData("report", new[] { "Quarterly report.docx - Word", "report-figures.xlsx - Excel" })]
    [InlineData("REPORT word", new[] { "Quarterly report.docx - Word" })]
    [InlineData("outlook", new[] { "Inbox - Outlook" })]
    [InlineData("microsoft", new[] { "Quarterly report.docx - Word", "Inbox - Outlook", "report-figures.xlsx - Excel" })]
    [InlineData("  excel   report  ", new[] { "report-figures.xlsx - Excel" })]
    [InlineData("heron", new string[0])]
    public void What_is_typed_is_looked_for_in_titles_and_programs_every_word_of_it(string query, string[] found) =>
        Assert.Equal(found, WindowSwitcher.Find(_desktop.Windows, query, _desktop.Activate).Select(row => row.Title));

    [Fact]
    public void Enter_on_a_row_brings_that_window_to_the_front()
    {
        var rows = WindowSwitcher.Find(_desktop.Windows, "", _desktop.Activate);

        rows[2].Run!();

        Assert.Equal([(nint)0x30], _desktop.Activated);
    }

    [Fact]
    public void A_title_is_shown_on_one_line_cut_to_fit_and_a_window_without_one_still_has_a_row()
    {
        var windows = new List<OpenWindow>
        {
            new(1, "first line\r\nsecond line", "Notepad", null),
            new(2, new string('x', 500), "Notepad", null),
            new(3, "", "Notepad", null),
        };

        var rows = WindowSwitcher.Find(windows, "", _ => { });

        Assert.DoesNotContain('\n', rows[0].Title);
        Assert.DoesNotContain('\r', rows[0].Title);
        Assert.True(rows[1].Title.Length <= WindowSwitcher.MaxTitleLength);
        Assert.Equal("Untitled", rows[2].Title);
    }

    [Fact]
    public void A_window_prints_as_its_type_and_nothing_of_its_title()
    {
        string printed = new OpenWindow(1, "Quarterly report.docx - Word", "Microsoft Word", null).ToString();

        Assert.Equal("OpenWindow", printed);
    }

    [Fact]
    public void Opening_the_list_puts_it_in_place_of_the_launcher_s_own_under_its_one_heading()
    {
        Assert.Equal(["Reload"], Titles());
        Assert.False(_launcher.InScope);
        Assert.Equal("", _launcher.ScopeTitle);
        Assert.Equal("", _launcher.ScopePrompt);
        var announced = new List<string?>();
        _launcher.PropertyChanged += (_, changed) => announced.Add(changed.PropertyName);
        _launcher.Query = "rel";

        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));

        Assert.True(_launcher.InScope);
        Assert.Equal(WindowSwitcher.Heading, _launcher.ScopeTitle);
        Assert.Equal(WindowSwitcher.Prompt, _launcher.ScopePrompt);
        Assert.Equal("", _launcher.Query);
        Assert.Equal(["Quarterly report.docx - Word", "Inbox - Outlook", "report-figures.xlsx - Excel"], Titles());
        Assert.Equal(0, _launcher.SelectedIndex);
        Assert.All(_launcher.Results, item => Assert.Equal(WindowSwitcher.Section, _launcher.SectionOf(item)));
        Assert.Contains(nameof(LauncherViewModel.InScope), announced);
        Assert.Contains(nameof(LauncherViewModel.ScopePrompt), announced);
    }

    [Fact]
    public void While_the_list_is_open_typing_is_looked_for_in_it_alone()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));

        _launcher.Query = "report";
        Assert.Equal(["Quarterly report.docx - Word", "report-figures.xlsx - Excel"], Titles());

        // The launcher's own command of that name is not found.
        _launcher.Query = "reload";
        Assert.Empty(_launcher.Results);
        Assert.Equal(-1, _launcher.SelectedIndex);

        // Emptied, the whole list again and not the launcher's.
        _launcher.Query = "";
        Assert.Equal(3, _launcher.Results.Count);
    }

    [Fact]
    public void The_windows_are_asked_for_afresh_at_each_key()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        int asked = _desktop.Asked;

        _desktop.Windows.RemoveAt(1);
        _launcher.Query = "o";

        Assert.Equal(asked + 1, _desktop.Asked);
        Assert.DoesNotContain("Inbox - Outlook", Titles());
    }

    [Fact]
    public void Enter_hides_the_window_brings_the_chosen_one_to_the_front_does_not_count_its_use_and_ends_in_the_launcher_s_own_list()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        var order = new List<string>();
        _launcher.Dismissed += (_, _) => order.Add("dismissed");
        _launcher.MoveSelection(1);

        _launcher.RunSelected();

        Assert.Equal(["dismissed"], order);
        Assert.Equal([(nint)0x20], _desktop.Activated);
        Assert.Equal(1.0, _usage.Boost("window:20"));
        Assert.False(_launcher.InScope);
        Assert.Equal("", _launcher.Query);
        Assert.Equal(["Reload"], Titles());
    }

    [Fact]
    public void Esc_closes_the_list_and_goes_back_to_an_empty_search()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        _launcher.Query = "report";

        Assert.True(_launcher.CloseScope());

        Assert.False(_launcher.InScope);
        Assert.Equal("", _launcher.Query);
        Assert.Equal(["Reload"], Titles());
        Assert.False(_launcher.CloseScope());
    }

    [Fact]
    public void Hiding_the_window_closes_the_list_even_with_nothing_typed()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));

        _launcher.Reset();

        Assert.False(_launcher.InScope);
        Assert.Equal(["Reload"], Titles());
    }

    [Fact]
    public void A_command_that_opens_the_list_keeps_the_launcher_open_and_its_use_is_counted()
    {
        var scope = WindowSwitcher.Scope(_desktop);
        LauncherViewModel? launcher = null;
        var command = new SearchItem("command:switch-windows", "Switch Windows", "", ItemKind.Command, () => launcher!.OpenScope(scope), KeepOpen: true);
        launcher = new LauncherViewModel(new SearchEngine([new Listed([command])], _usage), _usage);
        launcher.Refresh();
        bool dismissed = false;
        launcher.Dismissed += (_, _) => dismissed = true;
        launcher.Query = "switch";

        launcher.RunSelected();

        Assert.False(dismissed);
        Assert.True(launcher.InScope);
        Assert.Equal("", launcher.Query);
        Assert.Equal(3, launcher.Results.Count);
        Assert.True(_usage.Boost("command:switch-windows") > 1.0);
    }

    [Fact]
    public void Opening_the_clipboard_history_or_the_emoji_picker_closes_the_list()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        _launcher.OpenClipboard();
        Assert.False(_launcher.InScope);
        Assert.Empty(_launcher.Results);

        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        Assert.False(_launcher.Clipboard.IsOpen);
        Assert.Equal(3, _launcher.Results.Count);

        _launcher.Emoji.Open();
        Assert.False(_launcher.InScope);
        Assert.Empty(_launcher.Results);
    }

    [Fact]
    public void A_window_of_the_list_can_be_closed_and_its_program_force_quit_and_only_the_second_asks_first()
    {
        var rows = WindowSwitcher.Scope(_desktop).Search("");

        Assert.All(rows, row => Assert.Equal([WindowSwitcher.CloseWindow, WindowSwitcher.ForceQuit], row.Actions.Select(action => action.Name)));
        Assert.Null(rows[0].Actions[0].Question);
        Assert.Equal(WindowSwitcher.ForceQuitQuestion, rows[0].Actions[1].Question);

        // Rows made without a way to close or to quit offer neither.
        Assert.All(WindowSwitcher.Find(_desktop.Windows, "", _desktop.Activate), row => Assert.Empty(row.Actions));
    }

    [Fact]
    public void Closing_a_window_hides_the_launcher_and_asks_that_window_to_close()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        var order = new List<string>();
        _launcher.Dismissed += (_, _) => order.Add("dismissed");
        _launcher.MoveSelection(1);

        _launcher.RunAction(_launcher.Results[1].Actions[0]);

        Assert.Equal(["dismissed"], order);
        Assert.Equal([(nint)0x20], _desktop.Closed);
        Assert.Empty(_desktop.Quit);
        Assert.Empty(_desktop.Activated);
        Assert.False(_launcher.InScope);
        Assert.Equal(1.0, _usage.Boost("window:20"));
    }

    [Fact]
    public void Force_quit_asks_in_the_input_line_first_and_a_second_enter_does_it()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        bool dismissed = false;
        _launcher.Dismissed += (_, _) => dismissed = true;
        _launcher.Query = "outlook";

        _launcher.RunAction(_launcher.Results[0].Actions[1]);

        // Nothing is ended yet: the launcher is still open, with the question in its input line.
        Assert.False(dismissed);
        Assert.Empty(_desktop.Quit);
        Assert.True(_launcher.InArgumentMode);
        Assert.Equal(WindowSwitcher.ForceQuit, _launcher.ArgumentTitle);
        Assert.Equal(WindowSwitcher.ForceQuitQuestion, _launcher.ArgumentPrompt);
        Assert.Equal("", _launcher.Query);
        Assert.Empty(_launcher.Results);

        _launcher.RunSelected();

        Assert.True(dismissed);
        Assert.Equal([(nint)0x20], _desktop.Quit);
        Assert.Empty(_desktop.Closed);
        Assert.False(_launcher.InArgumentMode);
        Assert.False(_launcher.InScope);
    }

    [Fact]
    public void Esc_at_the_question_gives_it_up_and_goes_back_to_the_window_in_the_list()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        _launcher.Query = "report";
        _launcher.MoveSelection(1);
        _launcher.RunAction(_launcher.Results[1].Actions[1]);
        Assert.True(_launcher.InArgumentMode);

        Assert.True(_launcher.LeaveArgumentMode());

        Assert.Empty(_desktop.Quit);
        Assert.True(_launcher.InScope);
        Assert.Equal("report", _launcher.Query);
        Assert.Equal(["Quarterly report.docx - Word", "report-figures.xlsx - Excel"], Titles());
        Assert.Equal(1, _launcher.SelectedIndex);
    }

    [Fact]
    public void A_list_of_windows_that_cannot_close_or_quit_one_says_so_by_throwing()
    {
        IWindowList plain = new OnlyLists();

        Assert.Throws<NotSupportedException>(() => plain.Close(1));
        Assert.Throws<NotSupportedException>(() => plain.ForceQuit(1));
    }

    [Fact]
    public void A_refresh_asks_the_list_again_and_keeps_the_selection_on_the_same_window()
    {
        _launcher.OpenScope(WindowSwitcher.Scope(_desktop));
        _launcher.MoveSelection(2);
        Assert.Equal("report-figures.xlsx - Excel", _launcher.Results[_launcher.SelectedIndex].Title);

        _desktop.Windows.Insert(0, new OpenWindow(0x99, "New window", "Notepad", null));
        _launcher.Refresh();

        Assert.Equal(4, _launcher.Results.Count);
        Assert.Equal("report-figures.xlsx - Excel", _launcher.Results[_launcher.SelectedIndex].Title);
    }
}
