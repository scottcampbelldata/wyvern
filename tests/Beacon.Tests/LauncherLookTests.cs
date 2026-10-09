using Beacon.Core.Apps;
using Beacon.Core.ClipboardHistory;
using Beacon.Core.Commands;
using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>
/// What the launcher lists before anything is typed, and what the clipboard history says about
/// its entries: the headings, the kinds, the choice of kinds and the lines of information.
/// </summary>
public sealed class LauncherLookTests : IDisposable
{
    private const string Folder = @"C:\made-up\clipboard\OTTER-PC\2026-10-03";

    // The clock stands at 2026-10-03 12:00 UTC, which is 01:00 on 4 October in this zone.
    private static readonly TimeZoneInfo Ahead =
        TimeZoneInfo.CreateCustomTimeZone("Beacon test ahead", TimeSpan.FromHours(13), "Beacon test ahead", "Beacon test ahead");

    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly UsageStore _usage;
    private readonly List<ClipEntry> _history = [];

    public LauncherLookTests() => _usage = new UsageStore(_dir.File("usage.json"), _clock);

    public void Dispose() => _dir.Dispose();

    private LauncherViewModel Launcher(params ISearchProvider[] providers) => new(new SearchEngine(providers, _usage), _usage);

    private static CommandProvider Commands() => new(
    [
        new BuiltInCommand("reload", "Reload", "Read everything again", () => { }),
        new BuiltInCommand("clipboard-history", "Clipboard History", "Find what you copied", () => { }),
        new BuiltInCommand("quit", "Quit Beacon", "Close it", () => { }),
    ]);

    private static AppProvider Apps(params string[] names)
    {
        var apps = new AppProvider(new Catalog(names), new FakeProcessLauncher());
        apps.Refresh();
        return apps;
    }

    private static ClipEntry Clip(string id, DateTimeOffset time) => new(id, time, "OTTER-PC", new DateOnly(2026, 10, 3), Folder);

    private ClipboardViewModel History()
    {
        var view = new ClipboardViewModel(() => _history, _clock, zone: Ahead);
        view.Open();
        return view;
    }

    [Fact]
    public void Before_anything_is_typed_the_clipboard_history_the_commands_and_the_apps_are_listed_under_their_headings()
    {
        var viewModel = Launcher(Commands(), Apps("Zebra Paint", "Abacus"));

        viewModel.Refresh();

        Assert.Equal(
            ["Clipboard History", "Quit Beacon", "Reload", "Abacus", "Zebra Paint"],
            viewModel.Results.Select(item => item.Title));
        Assert.Equal(
            [
                LauncherViewModel.SuggestionsSection,
                LauncherViewModel.CommandsSection,
                LauncherViewModel.CommandsSection,
                LauncherViewModel.AppsSection,
                LauncherViewModel.AppsSection,
            ],
            viewModel.Results.Select(viewModel.SectionOf));
        Assert.Equal(0, viewModel.SelectedIndex);
    }

    [Fact]
    public void An_item_that_was_chosen_is_a_suggestion_and_is_not_listed_a_second_time()
    {
        var viewModel = Launcher(Commands(), Apps("Zebra Paint", "Abacus"));
        _usage.RecordUse("app:Zebra Paint");

        viewModel.Refresh();

        Assert.Equal(
            ["Clipboard History", "Zebra Paint", "Quit Beacon", "Reload", "Abacus"],
            viewModel.Results.Select(item => item.Title));
        Assert.Equal(LauncherViewModel.SuggestionsSection, viewModel.SectionOf(viewModel.Results[1]));
        Assert.Single(viewModel.Results, item => item.Title == "Zebra Paint");
    }

    [Fact]
    public void No_more_suggestions_are_listed_than_the_most()
    {
        string[] names = [.. Enumerable.Range(0, 12).Select(i => "App " + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))];
        var viewModel = Launcher(Commands(), Apps(names));
        foreach (string name in names)
        {
            _usage.RecordUse("app:" + name);
        }

        viewModel.Refresh();

        Assert.Equal(
            LauncherViewModel.MaxSuggestions,
            viewModel.Results.Count(item => viewModel.SectionOf(item) == LauncherViewModel.SuggestionsSection));
        Assert.Equal(names.Length + 3, viewModel.Results.Count);
    }

    [Fact]
    public void What_a_typed_text_finds_stands_under_results_and_emptying_the_input_brings_the_list_back()
    {
        var viewModel = Launcher(Commands(), Apps("Zebra Paint", "Abacus"));

        viewModel.Query = "aba";

        Assert.Equal(["Abacus"], viewModel.Results.Select(item => item.Title));
        Assert.Equal(LauncherViewModel.ResultsSection, viewModel.SectionOf(viewModel.Results[0]));

        viewModel.Query = "";

        Assert.Equal(5, viewModel.Results.Count);
        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
    }

    [Fact]
    public void A_provider_that_cannot_be_browsed_lists_nothing_before_something_is_typed()
    {
        var viewModel = Launcher(new SearchOnly());

        viewModel.Refresh();

        Assert.Empty(viewModel.Results);
        Assert.Equal(-1, viewModel.SelectedIndex);
    }

    [Fact]
    public void A_row_of_the_history_stands_under_the_day_it_was_copied_and_says_what_kind_it_is()
    {
        // 01:00 on 4 October here. Copied at 00:30 today, at 23:00 yesterday, and on 1 October.
        _history.Add(Clip("a", _clock.UtcNow.AddMinutes(-30)) with { Text = "hello there", TextLength = 11 });
        _history.Add(Clip("b", _clock.UtcNow.AddHours(-2)) with { Image = "b.png" });
        _history.Add(Clip("c", _clock.UtcNow.AddDays(-3)) with { Text = "https://example.org/", TextLength = 20, Link = "https://example.org/" });
        _history.Add(Clip("d", _clock.UtcNow.AddDays(-400)) with { Files = [@"C:\made-up\report.txt"] });
        var view = History();

        Assert.Equal(["Today", "Yesterday", "October 1", "August 30, 2025"], view.Rows.Select(row => row.Group));
        Assert.Equal(
            [ClipPreviewKind.Text, ClipPreviewKind.Image, ClipPreviewKind.Link, ClipPreviewKind.Files],
            view.Rows.Select(row => row.Kind));
        Assert.Equal([null, Path.Combine(Folder, "b.png"), null, null], view.Rows.Select(row => row.Picture));
    }

    [Fact]
    public void The_choice_of_kinds_narrows_the_list_and_says_so_when_nothing_is_left()
    {
        _history.Add(Clip("a", _clock.UtcNow.AddMinutes(-30)) with { Text = "hello there", TextLength = 11 });
        _history.Add(Clip("b", _clock.UtcNow.AddMinutes(-40)) with { Image = "b.png" });
        var view = History();

        view.TypeFilter = ClipTypeFilter.Images;

        Assert.Equal(["b"], view.Rows.Select(row => row.Entry.Id));
        Assert.Equal(0, view.SelectedIndex);

        view.TypeFilter = ClipTypeFilter.Links;

        Assert.Empty(view.Rows);
        Assert.Equal(ClipboardViewModel.NoMatchStatus, view.Status);

        view.TypeFilter = ClipTypeFilter.All;

        Assert.Equal(["a", "b"], view.Rows.Select(row => row.Entry.Id));
    }

    [Theory]
    [InlineData("SnagitEditor.exe", "Snagit Editor")]
    [InlineData("chrome.exe", "Chrome")]
    [InlineData("OUTLOOK.EXE", "Outlook")]
    [InlineData("WindowsTerminal.exe", "Windows Terminal")]
    [InlineData("Code.exe", "Code")]
    public void The_information_names_the_program_as_a_name_to_read(string file, string shown)
    {
        _history.Add(Clip("a", _clock.UtcNow.AddMinutes(-30)) with { Text = "one two  three\nfour", TextLength = 19, SourceApp = file });
        var view = History();

        Assert.Equal(
            [
                new ClipInfo("Source", shown),
                new ClipInfo("Type", "Text"),
                new ClipInfo("Characters", "19"),
                new ClipInfo("Words", "4"),
                new ClipInfo("Copied", "Today at 00:30"),
                new ClipInfo("Device", "OTTER-PC"),
            ],
            view.Info);

        // The entry keeps the file's name, and the filter finds it by that.
        Assert.Equal(file, view.Rows[0].Source);
    }

    [Fact]
    public void The_information_of_a_picture_has_no_count_of_characters_and_nothing_selected_has_no_information()
    {
        _history.Add(Clip("b", _clock.UtcNow.AddHours(-2)) with { Image = "b.png" });
        var view = History();

        Assert.Equal(
            [new ClipInfo("Type", "Image"), new ClipInfo("Copied", "Yesterday at 23:00"), new ClipInfo("Device", "OTTER-PC")],
            view.Info);

        view.TypeFilter = ClipTypeFilter.Text;

        Assert.Empty(view.Info);
    }

    [Fact]
    public void A_line_of_information_prints_as_its_type_and_nothing_of_what_it_holds()
    {
        Assert.Equal("ClipInfo", new ClipInfo("Source", "Secret Program").ToString());
    }

    private sealed class Catalog(string[] names) : IAppCatalog
    {
        public IReadOnlyList<AppEntry> GetApps() => [.. names.Select(name => new AppEntry(name, name))];
    }

    private sealed class SearchOnly : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => [];
    }
}
