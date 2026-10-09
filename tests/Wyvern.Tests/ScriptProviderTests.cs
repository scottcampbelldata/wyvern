using Wyvern.Core.Launcher;
using Wyvern.Core.Library;
using Wyvern.Core.Scripts;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>
/// The script provider with a runner that runs nothing. A result is handed over by calling the
/// runner's callback, as the real runner does from a background thread; what the provider then
/// asks to have done on the UI thread is collected and run by the test.
/// </summary>
public sealed class ScriptProviderTests : IDisposable
{
    private sealed class FixedItems(params SearchItem[] items) : ISearchProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);
    }

    private readonly TempDir _dir = new();
    private readonly FakeScriptRunner _runner = new();
    private readonly FakeProcessLauncher _launcher = new();
    private readonly OutputViewModel _output = new();
    private readonly List<string> _notes = [];
    private readonly List<Action> _posted = [];
    private readonly List<string> _events = [];
    private readonly ScriptProvider _provider;
    private readonly UsageStore _usage;
    private readonly LauncherViewModel _viewModel;
    private LibrarySnapshot _library = LibrarySnapshot.Empty;

    public ScriptProviderTests()
    {
        _provider = new ScriptProvider(() => _library, _runner, _launcher, _output, _notes.Add, _posted.Add);
        _usage = new UsageStore(_dir.File("usage.json"), new FakeClock());

        // As in WyvernShell: the provider and the launcher's view model share one output view.
        _viewModel = new LauncherViewModel(new SearchEngine([_provider], _usage), _usage, _output);
        _viewModel.Dismissed += (_, _) => _events.Add("dismissed");
        _viewModel.ActionFailed += (_, failure) => _events.Add("failed " + failure.ItemId);
    }

    public void Dispose() => _dir.Dispose();

    private void Load(params Script[] scripts) => _library = new LibrarySnapshot([], [], []) { Scripts = scripts };

    private static Script Script(string relativePath, string title, ScriptMode mode = ScriptMode.Silent, string? prompt = null) =>
        new(relativePath, @"C:\lib\scripts\" + relativePath, title, mode, prompt);

    private static ScriptResult Exited(int exitCode, string output = "", string error = "") =>
        new(ScriptOutcome.Exited, exitCode, output, error, TimeSpan.FromSeconds(1));

    /// <summary>Runs what the provider asked to have done on the UI thread.</summary>
    private void RunPosted()
    {
        foreach (var action in _posted.ToList())
        {
            action();
        }

        _posted.Clear();
    }

    [Fact]
    public void A_script_becomes_an_item_that_shows_where_its_file_is()
    {
        Load(Script(@"docker\restart.PS1", "Restart Docker"));

        var result = Assert.Single(_provider.Search("restart"));

        Assert.Equal(@"script:docker\restart.PS1", result.Item.Id);
        Assert.Equal("Restart Docker", result.Item.Title);
        Assert.Equal(@"docker\restart.PS1", result.Item.Subtitle);
        Assert.Equal(ItemKind.Script, result.Item.Kind);
        Assert.Equal("filetype:.ps1", result.Item.IconKey);
        Assert.NotNull(result.Item.Run);
        Assert.NotNull(result.Item.RunSecondary);
        Assert.Null(result.Item.Argument);
        Assert.False(result.Item.KeepOpen);
        Assert.Equal(FuzzyMatcher.Score("restart", "Restart Docker"), result.Score);
    }

    [Fact]
    public void A_script_is_found_by_its_title_and_not_by_its_file_name()
    {
        Load(Script("rd.cmd", "Restart Docker"));

        Assert.Single(_provider.Search("dock"));
        Assert.Single(_provider.Search("r"));
        Assert.Empty(_provider.Search("rd.cmd"));
        Assert.Empty(_provider.Search("xyz"));
    }

    [Fact]
    public void Enter_on_a_silent_script_hides_the_launcher_and_starts_it_and_says_nothing_until_it_ends()
    {
        var script = Script("backup.ps1", "Backup");
        Load(script);
        _viewModel.Query = "backup";

        _viewModel.RunSelected();

        Assert.Equal(["dismissed"], _events);
        var started = Assert.Single(_runner.Started);
        Assert.Same(script, started.Script);
        Assert.Null(started.Argument);
        Assert.False(_output.IsOpen);
        Assert.Empty(_notes);
        Assert.Empty(_posted);
        Assert.Equal(1.05, _usage.Boost("script:backup.ps1"), precision: 3);
    }

    [Fact]
    public void When_a_silent_script_ends_one_notification_shows_the_last_line_it_printed()
    {
        Load(Script("backup.ps1", "Backup"));
        _provider.Search("backup")[0].Item.Run!();

        _runner.Finish(Exited(0, "Copying...\r\nBackup finished\r\n"));

        // The result arrived on the runner's thread. Nothing is shown from there.
        Assert.Empty(_notes);
        Assert.Single(_posted);

        RunPosted();

        Assert.Equal(["Backup finished"], _notes);
        Assert.False(_output.IsOpen);
    }

    [Fact]
    public void A_silent_script_that_printed_nothing_is_announced_as_done()
    {
        Load(Script("backup.ps1", "Backup"));
        _provider.Search("backup")[0].Item.Run!();

        _runner.Finish(Exited(0));
        RunPosted();

        Assert.Equal(["Done"], _notes);
    }

    [Fact]
    public void A_silent_script_that_fails_is_announced_by_its_exit_code_and_standard_error()
    {
        Load(Script("backup.ps1", "Backup"));
        _provider.Search("backup")[0].Item.Run!();

        _runner.Finish(Exited(3, "Copying...\r\n", "The disk is full\r\n"));
        RunPosted();

        Assert.Equal(["Exit code 3. The disk is full"], _notes);
    }

    [Fact]
    public void A_notification_never_holds_the_scripts_title()
    {
        Load(Script("walrus.py", "Walrus payroll"));
        var run = _provider.Search("walrus")[0].Item.Run!;
        ScriptResult[] results =
        [
            Exited(0),
            Exited(0, "ok\r\n"),
            Exited(2, "", "bad\r\n"),
            new(ScriptOutcome.TimedOut, null, "", "", TimeSpan.Zero),
            new(ScriptOutcome.NotStarted, null, "", "", TimeSpan.Zero),
            new(ScriptOutcome.UnsafeArgument, null, "", "", TimeSpan.Zero),
            new(ScriptOutcome.UnsafePath, null, "", "", TimeSpan.Zero),
        ];

        foreach (var result in results)
        {
            run();
            _runner.Finish(result);
        }

        RunPosted();

        Assert.Equal(results.Length, _notes.Count);
        Assert.All(_notes, note => Assert.DoesNotContain("walrus", note, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("The program that runs .py scripts could not be started. Details are in the log file.", _notes);
    }

    [Fact]
    public void A_script_that_takes_an_argument_runs_only_with_one_and_shows_its_prompt()
    {
        var script = Script("restart.ps1", "Restart Docker", prompt: "Container name");
        Load(script);

        var item = Assert.Single(_provider.Search("restart")).Item;

        Assert.Null(item.Run);
        Assert.NotNull(item.Argument);
        Assert.Equal("Container name", item.Argument.Prompt);

        item.Argument.Run("web 1");
        item.Argument.Run("");

        Assert.Equal(["web 1", ""], _runner.Started.Select(s => s.Argument));
        Assert.All(_runner.Started, s => Assert.Same(script, s.Script));
    }

    [Fact]
    public void Enter_on_a_script_that_takes_an_argument_asks_for_it_and_Enter_again_runs_it_with_the_text()
    {
        Load(Script("restart.ps1", "Restart Docker", prompt: "Container name"));
        _viewModel.Query = "restart";

        _viewModel.RunSelected();

        Assert.True(_viewModel.InArgumentMode);
        Assert.Equal("Restart Docker", _viewModel.ArgumentTitle);
        Assert.Equal("Container name", _viewModel.ArgumentPrompt);
        Assert.Empty(_runner.Started);

        _viewModel.Query = "web 1";
        _viewModel.RunSelected();

        Assert.Equal(["dismissed"], _events);
        Assert.Equal("web 1", Assert.Single(_runner.Started).Argument);
        Assert.False(_viewModel.InArgumentMode);
    }

    [Fact]
    public void Tab_asks_for_the_argument_as_well()
    {
        Load(Script("restart.ps1", "Restart Docker", prompt: "Container name"));
        _viewModel.Query = "restart";

        Assert.True(_viewModel.EnterArgumentMode());

        Assert.Equal("Container name", _viewModel.ArgumentPrompt);
    }

    [Fact]
    public void Tab_on_a_script_that_takes_no_argument_does_nothing()
    {
        Load(Script("backup.ps1", "Backup"));
        _viewModel.Query = "backup";

        Assert.False(_viewModel.EnterArgumentMode());
        Assert.Empty(_runner.Started);
    }

    [Fact]
    public void Enter_on_an_output_script_keeps_the_launcher_open_and_shows_its_title_and_running()
    {
        var script = Script("report.py", "Disk report", ScriptMode.Output);
        Load(script);
        _viewModel.Query = "disk";
        Assert.True(_viewModel.Results[0].KeepOpen);

        _viewModel.RunSelected();

        Assert.Empty(_events);
        Assert.Same(script, Assert.Single(_runner.Started).Script);
        Assert.True(_output.IsOpen);
        Assert.Equal("Disk report", _output.Title);
        Assert.Equal("Running", _output.Status);
        Assert.Equal("", _output.Text);
        Assert.Empty(_viewModel.Results);
        Assert.Equal("", _viewModel.Query);
    }

    [Fact]
    public void When_an_output_script_ends_the_view_shows_everything_it_printed_and_there_is_no_notification()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();

        _runner.Finish(Exited(0, "C: 40 GB free\r\nD: 120 GB free\r\n"));
        Assert.Equal("Running", _output.Status);
        RunPosted();

        Assert.True(_output.IsOpen);
        Assert.Equal("Finished", _output.Status);
        Assert.Equal("C: 40 GB free\r\nD: 120 GB free", _output.Text);
        Assert.Empty(_notes);
    }

    [Fact]
    public void An_output_script_that_fails_shows_its_exit_code_and_standard_error_as_well()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();

        _runner.Finish(Exited(2, "C: 40 GB free\r\n", "D: is not ready\r\n"));
        RunPosted();

        Assert.Equal("Exit code 2", _output.Status);
        Assert.Equal("C: 40 GB free\r\n\r\nStandard error:\r\nD: is not ready", _output.Text);
        Assert.Empty(_notes);
    }

    [Fact]
    public void An_output_script_with_an_argument_runs_from_argument_mode_and_keeps_the_launcher_open()
    {
        Load(Script("restart.ps1", "Restart Docker", ScriptMode.Output, "Container name"));
        _viewModel.Query = "restart";
        _viewModel.RunSelected();
        _viewModel.Query = "web 1";

        _viewModel.RunSelected();

        Assert.Empty(_events);
        Assert.Equal("web 1", Assert.Single(_runner.Started).Argument);
        Assert.True(_output.IsOpen);
        Assert.Equal("Restart Docker", _output.Title);
        Assert.False(_viewModel.InArgumentMode);
    }

    [Fact]
    public void Closing_the_view_does_not_stop_the_script_and_its_end_is_then_announced_by_a_notification()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();

        Assert.True(_viewModel.CloseOutput());
        Assert.Single(_runner.Started);
        Assert.Empty(_notes);

        _runner.Finish(Exited(0, "C: 40 GB free\r\nD: 120 GB free\r\n"));
        RunPosted();

        Assert.False(_output.IsOpen);
        Assert.Equal(["D: 120 GB free"], _notes);
    }

    [Fact]
    public void Hiding_the_launcher_while_an_output_script_runs_has_the_same_effect()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();

        _viewModel.Reset();
        _runner.Finish(Exited(1, "", "No disks\r\n"));
        RunPosted();

        Assert.False(_output.IsOpen);
        Assert.Equal(["Exit code 1. No disks"], _notes);
    }

    [Fact]
    public void A_second_output_script_takes_the_view_and_the_first_is_announced_by_a_notification()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output), Script("uptime.cmd", "Uptime", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();
        _viewModel.CloseOutput();
        _viewModel.Query = "uptime";
        _viewModel.RunSelected();

        _runner.Finish(0, Exited(0, "first done\r\n"));
        _runner.Finish(1, Exited(0, "second done\r\n"));
        RunPosted();

        Assert.Equal("Uptime", _output.Title);
        Assert.Equal("second done", _output.Text);
        Assert.Equal(["first done"], _notes);
    }

    [Fact]
    public void A_refused_silent_script_is_announced_by_a_notification_that_says_why()
    {
        _runner.RefuseWith = new ScriptResult(ScriptOutcome.UnsafeArgument, null, "", "", TimeSpan.Zero);
        Load(Script("build.cmd", "Build", prompt: "Target"));
        var item = _provider.Search("build")[0].Item;

        item.Argument!.Run("a&b");
        RunPosted();

        Assert.StartsWith("Nothing was run. A batch file cannot be given text that holds", Assert.Single(_notes));
    }

    [Fact]
    public void A_refused_output_script_says_why_in_the_view()
    {
        _runner.RefuseWith = new ScriptResult(ScriptOutcome.UnsafeArgument, null, "", "", TimeSpan.Zero);
        Load(Script("build.cmd", "Build", ScriptMode.Output, "Target"));
        _viewModel.Query = "build";
        _viewModel.RunSelected();
        _viewModel.Query = "a&b";

        _viewModel.RunSelected();
        RunPosted();

        Assert.True(_output.IsOpen);
        Assert.Equal("Not run", _output.Status);
        Assert.StartsWith("Nothing was run. A batch file cannot be given text that holds", _output.Text);
        Assert.Empty(_notes);
    }

    // The texts are worked out where the result arrives, on the runner's thread. What is posted to
    // the UI thread only hands them over. So a result that cannot be turned into words fails
    // there, and nothing is posted: the null stands for a result that the work cannot be done on.
    [Fact]
    public void The_notification_is_made_where_the_result_arrives_and_not_on_the_ui_thread()
    {
        Load(Script("backup.ps1", "Backup"));
        _provider.Search("backup")[0].Item.Run!();

        Assert.ThrowsAny<Exception>(() => _runner.Finish(new ScriptResult(ScriptOutcome.Exited, 0, null!, "", TimeSpan.Zero)));

        Assert.Empty(_posted);
    }

    [Fact]
    public void The_status_and_the_text_of_the_output_view_are_made_where_the_result_arrives_and_not_on_the_ui_thread()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();

        Assert.ThrowsAny<Exception>(() => _runner.Finish(new ScriptResult(ScriptOutcome.Exited, 0, null!, "", TimeSpan.Zero)));

        Assert.Empty(_posted);
        Assert.Equal("Running", _output.Status);
    }

    [Fact]
    public void What_is_posted_only_hands_the_finished_texts_over()
    {
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";
        _viewModel.RunSelected();
        _runner.Finish(Exited(0, "C: 40 GB free\r\n"));

        // With the view closed before the posted work runs, the notification is the one made earlier.
        _viewModel.CloseOutput();
        RunPosted();

        Assert.False(_output.IsOpen);
        Assert.Equal(["C: 40 GB free"], _notes);
    }

    [Fact]
    public void An_output_script_that_cannot_even_be_started_is_reported_and_leaves_no_view_saying_running()
    {
        _runner.Throws = new ObjectDisposedException("runner");
        Load(Script("report.py", "Disk report", ScriptMode.Output));
        _viewModel.Query = "disk";

        _viewModel.RunSelected();

        Assert.Equal(["failed script:report.py"], _events);
        Assert.False(_output.IsOpen);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Ctrl_Enter_opens_the_scripts_file_for_editing_and_runs_nothing()
    {
        Load(Script(@"docker\restart.ps1", "Restart Docker", ScriptMode.Output, "Container name"));
        _viewModel.Query = "restart";

        _viewModel.RunSelected(secondary: true);

        Assert.Equal(["dismissed"], _events);
        Assert.Equal([@"edit C:\lib\scripts\docker\restart.ps1"], _launcher.Calls);
        Assert.Empty(_runner.Started);
        Assert.False(_output.IsOpen);
    }

    [Fact]
    public void A_library_loaded_again_is_searched_from_the_next_keystroke_and_rows_are_made_once_for_each_load()
    {
        Load(Script("backup.ps1", "Backup"));
        var first = _provider.Search("backup")[0].Item;
        Assert.Same(first, _provider.Search("back")[0].Item);

        Load(Script("backup.ps1", "Backup everything"), Script("clean.ps1", "Clean up"));

        Assert.Equal("Backup everything", Assert.Single(_provider.Search("backup")).Item.Title);
        Assert.Single(_provider.Search("clean"));
    }

    [Fact]
    public void At_equal_score_a_script_ranks_above_a_file()
    {
        Load(Script("budget.py", "Budget"));
        var file = new SearchItem(@"file:C:\Docs\Budget", "Budget", "", ItemKind.File, () => { });
        var engine = new SearchEngine([new FixedItems(file), _provider], _usage);

        Assert.Equal([ItemKind.Script, ItemKind.File], engine.Search("budget").Select(item => item.Kind));
    }
}
