using Beacon.Core.Commands;
using Beacon.Core.Launcher;
using Beacon.Core.Library;
using Beacon.Core.Search;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>
/// What sync changes for the library and the launcher: a conflict copy is kept out of the
/// launcher, and results that are on screen are made again when the library has been loaded
/// again, which with sync happens while nobody is typing.
/// </summary>
public sealed class LibraryRefreshTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;
    private readonly UsageStore _usage;

    // What the made-up provider answers with. A test replaces it to stand for a library that was loaded again.
    private SearchItem[] _items = [];
    private readonly List<string> _ran = [];

    public LibraryRefreshTests()
    {
        _paths = new DataPaths(_dir.File("data"));
        LibraryFiles.EnsureFolders(_paths);
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
    }

    public void Dispose() => _dir.Dispose();

    private void Snippet(string name, string title) =>
        File.WriteAllText(Path.Combine(_paths.SnippetsDir, name), $"---\nname: {title}\n---\nbody of {title}\n");

    private void Script(string name, string title) =>
        File.WriteAllText(Path.Combine(_paths.ScriptsDir, name), $"# @beacon.title {title}\nWrite-Output 'x'\n");

    private SearchItem Item(string id, string title) => new(id, title, "", ItemKind.Snippet, () => _ran.Add(id));

    private sealed class Changing(Func<SearchItem[]> items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items());
    }

    private LauncherViewModel NewViewModel(OutputViewModel? output = null)
    {
        var commands = new CommandProvider([new BuiltInCommand("needs", "Signature needs text", "", Run: null, Argument: new ItemArgument("Type", _ => { }))]);
        return new LauncherViewModel(new SearchEngine([new Changing(() => _items), commands], _usage), _usage, output);
    }

    // The names of the properties the view model announces from now on.
    private static List<string?> Announcements(LauncherViewModel viewModel)
    {
        var announced = new List<string?>();
        viewModel.PropertyChanged += (_, changed) => announced.Add(changed.PropertyName);
        return announced;
    }

    // ---- Conflict copies ----

    [Fact]
    public void A_conflict_copy_of_a_snippet_is_not_loaded_and_is_not_a_bad_file()
    {
        Snippet("sig.md", "Signature");
        Snippet("sig.conflict-OTTER-PC-20261003-140509.md", "Signature");
        Snippet("sig.conflict-OTTER-PC-20261003-140509-2.txt", "Signature");

        var library = LibraryLoader.Load(_paths);

        Assert.Equal(["sig.md"], library.Snippets.Select(snippet => snippet.RelativePath));
        Assert.Empty(library.BadFiles);
    }

    [Fact]
    public void A_conflict_copy_of_a_script_is_not_a_second_command_with_the_same_title()
    {
        Script("deploy.ps1", "Deploy");
        Script("deploy.conflict-SEAL-PC-20261003-140509.ps1", "Deploy");
        Directory.CreateDirectory(Path.Combine(_paths.ScriptsDir, "work"));
        Script(Path.Combine("work", "build.conflict-Otter PC-20261003-140509.cmd"), "Build");

        var library = LibraryLoader.Load(_paths);

        Assert.Equal(["deploy.ps1"], library.Scripts.Select(script => script.RelativePath));
        Assert.Empty(library.BadFiles);
    }

    [Fact]
    public void A_conflict_copy_that_is_broken_is_not_listed_as_a_file_to_check()
    {
        Snippet("sig.md", "Signature");
        File.WriteAllText(Path.Combine(_paths.SnippetsDir, "sig.conflict-OTTER-PC-20261003-140509.md"), "---\nname:\n");

        Assert.Empty(LibraryLoader.Load(_paths).BadFiles);
    }

    [Fact]
    public void A_file_that_only_has_the_word_conflict_in_its_name_is_loaded_as_ever()
    {
        // "merge.conflict-notes.md" holds the very mark a conflict copy has, ".conflict-", and
        // is none: it has no machine name, no date and time after it. A rule that hid every
        // name that contains the mark would hide it from the owner.
        Snippet("conflict-notes.md", "Conflict notes");
        Snippet("merge.conflict.md", "Merge conflict");
        Snippet("merge.conflict-notes.md", "Merge conflict notes");

        var library = LibraryLoader.Load(_paths);

        Assert.Equal(["conflict-notes.md", "merge.conflict-notes.md", "merge.conflict.md"], library.Snippets.Select(snippet => snippet.RelativePath));
        Assert.Empty(library.BadFiles);
    }

    [Fact]
    public void A_conflict_copy_of_quicklinks_json_is_not_read()
    {
        File.WriteAllText(_paths.QuicklinksFile, """[ { "name": "Docs", "link": "https://example.org/docs" } ]""");
        File.WriteAllText(Path.Combine(_paths.LibraryDir, "quicklinks.conflict-OTTER-PC-20261003-140509.json"), """[ { "name": "Other", "link": "https://example.org/other" } ]""");

        var library = LibraryLoader.Load(_paths);

        Assert.Equal(["Docs"], library.Quicklinks.Select(link => link.Name));
        Assert.Empty(library.BadFiles);
    }

    // ---- The library says when it was loaded again ----

    [Fact]
    public async Task The_service_says_so_each_time_a_load_has_been_put_into_use()
    {
        int changes = 0;
        var time = new ManualTimeProvider();
        using var service = new LibraryService(time: time, changed: () => Interlocked.Increment(ref changes));

        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);
        Assert.Equal(1, Volatile.Read(ref changes));

        Snippet("sig.md", "Signature");
        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);

        Assert.Equal(2, Volatile.Read(ref changes));
        Assert.Single(service.Current.Snippets);
    }

    [Fact]
    public async Task A_load_that_failed_changes_nothing_and_says_nothing()
    {
        int changes = 0;
        using var service = new LibraryService(
            time: new ManualTimeProvider(),
            load: _ => throw new InvalidOperationException("the load failed"),
            changed: () => Interlocked.Increment(ref changes));

        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);

        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_stop_the_library_loading()
    {
        int calls = 0;
        var reported = new List<string>();
        using var service = new LibraryService(
            (message, _) =>
            {
                lock (reported)
                {
                    reported.Add(message);
                }
            },
            time: new ManualTimeProvider(),
            changed: () =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("listener");
            });
        Snippet("sig.md", "Signature");

        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);
        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);

        Assert.Single(service.Current.Snippets);

        // The listener was asked after each load, and its failure did not stop the second. It is
        // the service's own guard that holds it: without that, the failure would be caught
        // further out and logged as a load that failed, which is not what happened.
        Assert.Equal(2, Volatile.Read(ref calls));
        lock (reported)
        {
            Assert.DoesNotContain("The library could not be loaded", reported);
            Assert.Equal(2, reported.Count(message => message.StartsWith("Library loaded:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task A_load_that_was_thrown_away_says_nothing_of_having_changed()
    {
        // Held, as LibraryServiceTests holds a load: it has read the folder, and what it read
        // is not yet put into use. The folder changes meanwhile.
        using var hasRead = new ManualResetEventSlim();
        using var mayFinish = new ManualResetEventSlim();
        var other = new DataPaths(_dir.File("other"));
        Snippet("here.md", "Here");
        LibraryFiles.EnsureFolders(other);
        File.WriteAllText(Path.Combine(other.SnippetsDir, "there.md"), "---\nname: There\n---\nbody of There\n");
        int changes = 0;
        using var service = new LibraryService(
            time: new ManualTimeProvider(),
            load: paths =>
            {
                var snapshot = LibraryLoader.Load(paths);
                hasRead.Set();
                mayFinish.Wait(Limit);
                return snapshot;
            },
            changed: () => Interlocked.Increment(ref changes));

        try
        {
            service.Configure(_paths);
            Assert.True(hasRead.Wait(Limit), "Timed out waiting for the first load to read the folder");

            service.Configure(other);
        }
        finally
        {
            mayFinish.Set();
        }

        await service.Loading.WaitAsync(Limit);

        // Two loads were read. The first belonged to the folder that was given up, was thrown
        // away, and told nobody; only the second, which is in use, did.
        Assert.Equal(["there.md"], service.Current.Snippets.Select(snippet => snippet.RelativePath));
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    // ---- The launcher makes its results again ----

    [Fact]
    public void A_row_for_an_item_that_has_gone_leaves_the_screen_without_a_keystroke()
    {
        _items = [Item("snippet:sig.md", "Signature"), Item("snippet:old.md", "Signature old")];
        var viewModel = NewViewModel();
        viewModel.Query = "signature";
        Assert.Equal(3, viewModel.Results.Count);

        _items = [Item("snippet:sig.md", "Signature")];
        viewModel.Refresh();

        Assert.Equal(["Signature", "Signature needs text"], viewModel.Results.Select(item => item.Title));
        Assert.Equal("signature", viewModel.Query);
    }

    [Fact]
    public void The_row_that_runs_after_a_refresh_is_the_one_from_the_library_as_it_now_is()
    {
        _items = [Item("snippet:sig.md", "Signature")];
        var viewModel = NewViewModel();
        viewModel.Query = "signature";

        _items = [new SearchItem("snippet:sig.md", "Signature", "", ItemKind.Snippet, () => _ran.Add("the edited one"))];
        viewModel.Refresh();
        viewModel.RunSelected();

        Assert.Equal(["the edited one"], _ran);
    }

    [Fact]
    public void The_selection_stays_on_the_same_item_when_it_is_still_listed()
    {
        _items = [Item("snippet:a.md", "Signature a"), Item("snippet:b.md", "Signature b"), Item("snippet:c.md", "Signature c")];
        var viewModel = NewViewModel();
        viewModel.Query = "signature";
        viewModel.SelectedIndex = viewModel.Results.Select(item => item.Id).ToList().IndexOf("snippet:c.md");

        _items = [Item("snippet:new.md", "Signature 0"), Item("snippet:c.md", "Signature c"), Item("snippet:a.md", "Signature a")];
        viewModel.Refresh();

        Assert.Equal("snippet:c.md", viewModel.Results[viewModel.SelectedIndex].Id);
    }

    [Fact]
    public void The_selection_goes_to_the_first_row_when_its_item_has_gone()
    {
        _items = [Item("snippet:a.md", "Signature a"), Item("snippet:b.md", "Signature b")];
        var viewModel = NewViewModel();
        viewModel.Query = "signature";
        viewModel.MoveSelection(1);
        string selected = viewModel.Results[viewModel.SelectedIndex].Id;

        _items = [.. _items.Where(item => item.Id != selected)];
        viewModel.Refresh();

        Assert.Equal(0, viewModel.SelectedIndex);
    }

    [Fact]
    public void With_nothing_typed_a_refresh_lists_only_what_can_be_browsed_and_announces_the_selection()
    {
        _items = [Item("snippet:a.md", "Signature a")];
        var viewModel = NewViewModel();
        var announced = Announcements(viewModel);

        viewModel.Refresh();

        Assert.DoesNotContain(viewModel.Results, item => viewModel.SectionOf(item) == LauncherViewModel.ResultsSection);
        Assert.Equal(viewModel.Results.Count > 0 ? 0 : -1, viewModel.SelectedIndex);

        // The empty state is the starting state, so what shows that a refresh was made at all
        // is what it says: it announces the selection every time, because a list that is bound
        // to the results drops its own selection when they are replaced. A Refresh that does
        // nothing says nothing.
        Assert.Equal([nameof(LauncherViewModel.SelectedIndex)], announced);
    }

    [Fact]
    public void While_an_argument_is_being_typed_a_refresh_changes_nothing()
    {
        var viewModel = NewViewModel();
        viewModel.Query = "needs";
        Assert.True(viewModel.EnterArgumentMode());
        viewModel.Query = "typed so far";

        _items = [Item("snippet:a.md", "typed so far")];
        viewModel.Refresh();

        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("typed so far", viewModel.Query);
        Assert.Empty(viewModel.Results);
    }

    [Fact]
    public void While_the_output_view_or_the_clipboard_history_is_open_a_refresh_brings_no_results_back()
    {
        _items = [Item("snippet:a.md", "Signature a")];
        var output = new OutputViewModel();
        var viewModel = NewViewModel(output);
        viewModel.Query = "signature";
        var before = viewModel.Results.ToList();

        // The output view stands where the results would be. No search is made behind it.
        output.Open("A script");
        _items = [Item("snippet:a.md", "Signature a"), Item("snippet:b.md", "Signature b")];
        viewModel.Refresh();
        Assert.Equal(before, viewModel.Results);

        viewModel.CloseOutput();
        viewModel.OpenClipboard();
        Assert.Empty(viewModel.Results);
        var announced = Announcements(viewModel);
        viewModel.Refresh();

        // Opening the history has already emptied the results, so they would be empty after a
        // Refresh that searched as well. What shows that none was made is what a search always
        // does, which is to announce the selection: nothing is announced.
        Assert.Empty(viewModel.Results);
        Assert.Empty(announced);
    }
}
