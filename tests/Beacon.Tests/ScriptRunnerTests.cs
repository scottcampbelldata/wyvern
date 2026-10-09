using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core.Library;
using Beacon.Core.Scripts;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>
/// These tests run real processes. The only scripts run are tiny batch files written here into
/// a temporary folder, and all they do is echo text, set an exit code or wait. cmd is the only
/// program used, so nothing depends on PowerShell, Python or Node being installed, and nothing
/// touches the network.
/// </summary>
/// <remarks>
/// The timeout never runs out by itself: the runner is given a timer that only a test can
/// fire. A script that waits does so with "waitfor /t 60", a Windows command that waits for a
/// signal nobody sends and gives up after a minute by itself. That is longer than any test
/// waits, so a test can only pass because the script was stopped, and even a test that fails
/// leaves nothing running for longer than a minute. Waits in the tests are for something to
/// become true, up to a generous limit.
/// </remarks>
public sealed class ScriptRunnerTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<(string Message, Exception? Error)> _reports = [];
    private readonly ScriptRunner _runner;
    private ScriptOptions _options = ScriptOptions.Default;

    public ScriptRunnerTests()
    {
        _runner = NewRunner();
    }

    public void Dispose()
    {
        _runner.Dispose();
        _runner.Idle.Wait(Limit);

        // A process that has just been stopped can hold its files for a moment longer.
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            try
            {
                _dir.Dispose();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private ScriptRunner NewRunner(
        int maxOutputChars = ScriptRunner.MaxOutputChars,
        TimeSpan? drainTime = null,
        Func<string, string?>? find = null) => new(
        () => _options,
        (message, error) =>
        {
            lock (_reports)
            {
                _reports.Add((message, error));
            }
        },
        _time,
        find,
        maxOutputChars,
        drainTime);

    private List<string> Reports()
    {
        lock (_reports)
        {
            return _reports.Select(report => report.Message).ToList();
        }
    }

    private List<Exception> ReportedErrors()
    {
        lock (_reports)
        {
            return _reports.Where(report => report.Error is not null).Select(report => report.Error!).ToList();
        }
    }

    /// <summary>Writes a batch file under the temporary folder. Lines are joined the way cmd expects.</summary>
    private Script Write(string relativePath, params string[] lines)
    {
        string file = Path.Combine(_dir.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new Script(relativePath, file, "Walrus title", ScriptMode.Silent, null);
    }

    private static string UniqueSignal() => "B" + Guid.NewGuid().ToString("N");

    private static void Interpreters(BeaconSettings settings, string json) =>
        settings.Interpreters = JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// What one Start was told. The first call gives the result; every call is counted, so that a
    /// second call of "finished" cannot go unnoticed.
    /// </summary>
    private sealed class Delivery
    {
        private readonly TaskCompletionSource<ScriptResult> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task<ScriptResult> Result => _first.Task;

        public int Calls => Volatile.Read(ref _calls);

        public void Finished(ScriptResult result)
        {
            Interlocked.Increment(ref _calls);
            _first.TrySetResult(result);
        }
    }

    private readonly Dictionary<Task<ScriptResult>, Delivery> _deliveries = [];

    private Task<ScriptResult> Start(Script script, string? argument = null, ScriptRunner? runner = null)
    {
        var delivery = new Delivery();
        lock (_deliveries)
        {
            _deliveries[delivery.Result] = delivery;
        }

        (runner ?? _runner).Start(script, argument, delivery.Finished);
        return delivery.Result;
    }

    private int CallsOf(Task<ScriptResult> result)
    {
        lock (_deliveries)
        {
            return _deliveries[result].Calls;
        }
    }

    /// <summary>
    /// Waits until the runner is idle, which it is only once every result has been handed over, and
    /// then checks that "finished" was called exactly once. The wait is for the runner to say so,
    /// not for a time.
    /// </summary>
    private async Task AssertHandedOverOnce(Task<ScriptResult> result, ScriptRunner? runner = null)
    {
        await (runner ?? _runner).Idle.WaitAsync(Limit);
        Assert.Equal(1, CallsOf(result));
    }

    private async Task<ScriptResult> Run(Script script, string? argument = null, ScriptRunner? runner = null)
    {
        var running = Start(script, argument, runner);
        var result = await running.WaitAsync(Limit);
        await AssertHandedOverOnce(running, runner);
        return result;
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>True while some process has the file open. A file that is not there is held by nobody.</summary>
    private static bool IsHeld(string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// A script that starts a second cmd, which in turn waits. The second cmd holds "held.txt"
    /// open for as long as it lives and writes "started.txt" once it is holding it.
    /// </summary>
    private Script WriteScriptWithAChild(string folder)
    {
        Write(
            Path.Combine(folder, "child.cmd"),
            "@echo off",
            $"(echo running> \"%~dp0started.txt\" & waitfor /t 60 {UniqueSignal()}) > \"%~dp0held.txt\" 2>&1");
        return Write(
            Path.Combine(folder, "parent.cmd"),
            "@echo off",
            "echo before the wait",
            "cmd /d /c call \"%~dp0child.cmd\"",
            "echo never reached");
    }

    [Fact]
    public async Task What_a_script_prints_is_captured_and_its_exit_code_is_zero()
    {
        var script = Write("hello.cmd", "@echo off", "echo hello", "echo world");

        var result = await Run(script);

        Assert.Equal(ScriptOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\r\nworld\r\n", result.Output);
        Assert.Equal("", result.Error);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_failing_script_gives_its_exit_code_and_what_it_wrote_to_standard_error()
    {
        var script = Write("fails.cmd", "@echo off", "echo working", "echo the disk is full 1>&2", "exit /b 3");

        var result = await Run(script);

        Assert.Equal(ScriptOutcome.Exited, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("working\r\n", result.Output);
        Assert.Equal("the disk is full \r\n", result.Error);
    }

    [Fact]
    public async Task The_argument_arrives_as_one_argument_even_with_spaces()
    {
        var script = Write("show.cmd", "@echo off", "echo [%~1][%~2]");

        var result = await Run(script, "tom jerry and friends");

        Assert.Equal("[tom jerry and friends][]\r\n", result.Output);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("tom jerry")]
    [InlineData("/?")]
    [InlineData("-x --long /y")]
    [InlineData("it's (really) here!")]
    [InlineData("  spaces all round  ")]
    public async Task A_script_whose_folder_and_name_have_spaces_and_brackets_gets_its_argument_whole(string argument)
    {
        var script = Write(@"My Tools (x86)\show it.cmd", "@echo off", "echo [%~1][%~2]");

        var result = await Run(script, argument);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"[{argument}][]\r\n", result.Output);
    }

    [Fact]
    public async Task An_empty_argument_is_passed_as_an_empty_argument_and_no_argument_as_none()
    {
        var script = Write("show.cmd", "@echo off", "echo [%1][%2]");

        Assert.Equal("[\"\"][]\r\n", (await Run(script, "")).Output);
        Assert.Equal("[][]\r\n", (await Run(script, argument: null)).Output);
    }

    [Theory]
    [InlineData("tom & jerry")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("100%")]
    [InlineData("say \"hi\"")]
    [InlineData("one\ntwo")]
    [InlineData("a=b")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    public void A_batch_file_is_not_run_with_an_argument_cmd_would_act_on(string argument)
    {
        var script = Write("marks.cmd", "@echo off", "echo ran> \"%~dp0ran.txt\"");
        ScriptResult? result = null;

        _runner.Start(script, argument, r => result = r);

        // Refused before Start returns: no thread, no process, no timer.
        Assert.NotNull(result);
        Assert.Equal(ScriptOutcome.UnsafeArgument, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal(0, _runner.Running);
        Assert.Empty(_time.Timers);
        Assert.False(File.Exists(_dir.File("ran.txt")));
        Assert.Equal(["Script script:(path withheld) was not run: the text typed cannot be given to a batch file safely"], Reports());
    }

    [Theory]
    [InlineData(@"R&D\marks.cmd")]
    [InlineData(@"tools\build(1).cmd")]
    public void A_batch_file_whose_own_path_cmd_would_act_on_is_not_run(string relativePath)
    {
        // No such file exists. The path is refused for what it is, before anything looks for it.
        var script = new Script(relativePath, @"C:\BeaconTestNoSuchFolder\" + relativePath, "Marks", ScriptMode.Silent, null);
        ScriptResult? result = null;

        _runner.Start(script, null, r => result = r);

        Assert.NotNull(result);
        Assert.Equal(ScriptOutcome.UnsafePath, result.Outcome);
        Assert.Equal(0, _runner.Running);
        Assert.Empty(_time.Timers);
        Assert.Equal(["Script script:(path withheld) was not run: its path cannot be given to cmd safely"], Reports());
    }

    /// <summary>A runner that finds no program at all and notes every name it is asked to look for.</summary>
    private ScriptRunner NewRunnerThatFindsNothing(List<string> asked) => NewRunner(find: program =>
    {
        lock (asked)
        {
            asked.Add(program);
        }

        return null;
    });

    [Theory]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report[1].ps1")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report1].ps1")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\REPORT[1].PS1")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\[draft]\run.ps1")]
    [InlineData(@"C:\BeaconTestNoSuchFolder[1]\tools\run.ps1")]
    [InlineData("C:\\BeaconTestNoSuchFolder\\tools\\a`b.ps1")]
    [InlineData(@"C:\Beacon Test No Such Folder\tools\report[1].ps1")]
    public void A_powershell_script_whose_path_powershell_would_misread_is_not_run(string filePath)
    {
        // No such file exists, and no program is found for it here, so a run that was not
        // refused would end as "not started". It is refused for what its path is, before
        // anything looks for a program.
        var asked = new List<string>();
        using var runner = NewRunnerThatFindsNothing(asked);
        var script = new Script(@"tools\report.ps1", filePath, "Report", ScriptMode.Silent, null);
        ScriptResult? result = null;

        runner.Start(script, null, r => result = r);

        // Refused before Start returns: no thread, no timer, no process.
        Assert.NotNull(result);
        Assert.Equal(ScriptOutcome.UnsafePath, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal(0, runner.Running);
        Assert.Empty(_time.Timers);
        Assert.Empty(asked);
        Assert.Equal(["Script script:(path withheld) was not run: its path cannot be given to PowerShell safely"], Reports());
    }

    [Theory]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report.ps1")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report(1) & co.ps1")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report[1].py")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report[1].js")]
    [InlineData(@"C:\BeaconTestNoSuchFolder\tools\report[1].cmd")]
    [InlineData("C:\\BeaconTestNoSuchFolder\\tools\\report`1.bat")]
    public async Task The_square_bracket_and_backtick_rule_is_for_powershell_scripts_only(string filePath)
    {
        // Each of these reaches the point of looking for a program, finds none, and ends as
        // "not started".
        var asked = new List<string>();
        using var runner = NewRunnerThatFindsNothing(asked);
        var script = new Script(@"tools\report" + Path.GetExtension(filePath), filePath, "Report", ScriptMode.Silent, null);

        var result = await Run(script, runner: runner);

        Assert.Equal(ScriptOutcome.NotStarted, result.Outcome);
        Assert.NotEmpty(asked);
    }

    [Fact]
    public async Task The_same_character_in_a_path_with_a_space_is_safe_and_the_script_runs()
    {
        var script = Write(@"R&D team\show.cmd", "@echo off", "echo [%~1]");

        var result = await Run(script, "tom jerry");

        Assert.Equal(ScriptOutcome.Exited, result.Outcome);
        Assert.Equal("[tom jerry]\r\n", result.Output);
    }

    [Fact]
    public async Task The_working_folder_is_the_scripts_own_folder()
    {
        var script = Write(@"deep\down\where.cmd", "@echo off", "echo %CD%");

        var result = await Run(script);

        Assert.Equal(Path.Combine(_dir.Path, "deep", "down"), result.Output.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task A_script_that_asks_a_question_gets_no_answer_instead_of_waiting_for_one()
    {
        var script = Write("asks.cmd", "@echo off", "set /p answer=", "echo after [%answer%]");

        var result = await Run(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("after []", result.Output);
    }

    [Fact]
    public async Task Output_is_read_as_utf8()
    {
        var script = Write("words.cmd", "@echo off", "chcp 65001 >nul", "echo Grüße … 日本");

        var result = await Run(script);

        Assert.Equal("Grüße … 日本\r\n", result.Output);
    }

    [Fact]
    public async Task A_script_that_prints_a_great_deal_to_both_outputs_at_once_is_read_to_the_end()
    {
        var script = Write(
            "chatty.cmd",
            "@echo off",
            "for /l %%i in (1,1,1500) do (",
            "  echo out line %%i",
            "  echo err line %%i 1>&2",
            ")");

        var result = await Run(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1500, result.Output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(1500, result.Error.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("out line 1500\r\n", result.Output);
        Assert.EndsWith("err line 1500 \r\n", result.Error);
    }

    [Fact]
    public async Task Only_the_newest_part_of_a_long_output_is_kept()
    {
        using var runner = NewRunner(maxOutputChars: 20);
        var script = Write("counts.cmd", "@echo off", "for /l %%i in (1,1,9) do echo line %%i");

        var result = await Run(script, runner: runner);

        Assert.Equal(" 7\r\nline 8\r\nline 9\r\n", result.Output);
    }

    [Fact]
    public async Task A_script_that_runs_past_its_time_is_stopped_together_with_the_process_it_started()
    {
        var script = WriteScriptWithAChild("tree");
        string held = _dir.File(@"tree\held.txt");

        var running = Start(script);
        await Eventually(() => File.Exists(_dir.File(@"tree\started.txt")) && _time.Timers.Count == 1, "the script's child to be running");
        Assert.True(IsHeld(held), "the child should be holding its file open while it runs");
        Assert.Equal(ScriptOptions.Default.Timeout, _time.Timers[0].Due);

        _time.Timers[0].Fire();
        var result = await running.WaitAsync(Limit);

        Assert.Equal(ScriptOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal("before the wait\r\n", result.Output);
        await Eventually(() => !IsHeld(held), "the child to be stopped as well as the script");
        await _runner.Idle.WaitAsync(Limit);
        Assert.Equal(0, _runner.Running);
        await AssertHandedOverOnce(running);
        Assert.Matches(@"^Script script:\(path withheld\) timed out and was stopped after \d+\.\d s$", Assert.Single(Reports()));
    }

    [Fact]
    public async Task Scripts_still_running_when_the_runner_is_disposed_are_stopped_with_what_they_started_and_report_nothing()
    {
        var script = WriteScriptWithAChild("quit");
        string held = _dir.File(@"quit\held.txt");

        var running = Start(script);
        await Eventually(() => File.Exists(_dir.File(@"quit\started.txt")) && _time.Timers.Count == 1, "the script's child to be running");

        _runner.Dispose();
        await _runner.Idle.WaitAsync(Limit);

        await Eventually(() => !IsHeld(held), "the child to be stopped as well as the script");
        Assert.False(running.IsCompleted, "a script stopped because Beacon is closing has nobody to tell");
        Assert.Equal(0, CallsOf(running));
        Assert.Equal(["Script script:(path withheld) was stopped because Beacon is closing"], Reports());
    }

    [Fact]
    public async Task Several_scripts_run_at_the_same_time()
    {
        var first = Write("first.cmd", "@echo off", "echo first", $"waitfor /t 60 {UniqueSignal()} >nul 2>&1");
        var second = Write("second.cmd", "@echo off", "echo second", $"waitfor /t 60 {UniqueSignal()} >nul 2>&1");

        var firstRunning = Start(first);
        var secondRunning = Start(second);
        await Eventually(() => _time.Timers.Count == 2, "both scripts to have been started");

        Assert.Equal(2, _runner.Running);
        Assert.False(firstRunning.IsCompleted);
        Assert.False(secondRunning.IsCompleted);

        foreach (var timer in _time.Timers)
        {
            timer.Fire();
        }

        Assert.Equal(ScriptOutcome.TimedOut, (await firstRunning.WaitAsync(Limit)).Outcome);
        Assert.Equal(ScriptOutcome.TimedOut, (await secondRunning.WaitAsync(Limit)).Outcome);
        await _runner.Idle.WaitAsync(Limit);
        Assert.Equal(0, _runner.Running);
        await AssertHandedOverOnce(firstRunning);
        await AssertHandedOverOnce(secondRunning);
    }

    [Fact]
    public async Task A_timeout_that_arrives_after_the_script_has_ended_changes_nothing()
    {
        var script = Write("quick.cmd", "@echo off", "echo quick");

        var running = Start(script);
        var result = await running.WaitAsync(Limit);
        await AssertHandedOverOnce(running);
        _time.Timers[0].Fire();

        // Looked at again now that the late timer has fired: it neither stopped anything nor
        // had a second result handed over.
        Assert.Equal(1, CallsOf(running));
        Assert.Equal(0, _runner.Running);
        Assert.Equal(ScriptOutcome.Exited, result.Outcome);
        Assert.Matches(@"^Script script:\(path withheld\) ran: exit code 0 after \d+\.\d s$", Assert.Single(Reports()));
    }

    [Fact]
    public async Task The_timeout_is_the_one_in_the_settings_when_the_script_starts()
    {
        _options = ScriptOptions.From(new BeaconSettings { ScriptTimeoutSeconds = JsonSerializer.SerializeToElement(45) });
        var script = Write("waits.cmd", "@echo off", $"waitfor /t 60 {UniqueSignal()} >nul 2>&1");

        var running = Start(script);
        await Eventually(() => _time.Timers.Count == 1, "the script to have been started");

        // A change to the settings now is for the next script, not for this one.
        _options = ScriptOptions.From(new BeaconSettings { ScriptTimeoutSeconds = JsonSerializer.SerializeToElement(5) });
        Assert.Equal(TimeSpan.FromSeconds(45), _time.Timers[0].Due);

        _time.Timers[0].Fire();
        Assert.Equal(ScriptOutcome.TimedOut, (await running.WaitAsync(Limit)).Outcome);
        await AssertHandedOverOnce(running);
    }

    [Fact]
    public async Task A_program_named_in_the_settings_is_used_in_place_of_the_built_in_one()
    {
        // The "program" for .py files here is a made-up name, which this runner's finder maps to
        // the real cmd.exe, echoing its arguments, so no Python is needed and the .py file is
        // never run by anything. It has to be a name other than cmd: an entry that names cmd
        // itself is not used (see ScriptOptionsTests). Nothing is copied or written for it.
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var runner = NewRunner(find: program => program == "my-own-program" ? cmd : null);
        var settings = new BeaconSettings();
        Interpreters(settings, """{ ".py": ["my-own-program", "/d", "/c", "echo", "started-by-my-own-program:"] }""");
        _options = ScriptOptions.From(settings);
        string file = _dir.File("report.py");
        File.WriteAllText(file, "print('never run')\n");
        var script = new Script("report.py", file, "Report", ScriptMode.Silent, null);

        var result = await Run(script, "walrus", runner);

        // The program's own arguments, then the script's path, then the text typed.
        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("started-by-my-own-program: ", result.Output);
        Assert.Contains("report.py", result.Output);
        Assert.EndsWith(" walrus\r\n", result.Output);
    }

    [Fact]
    public async Task A_program_that_cannot_be_found_is_reported_like_a_script_that_did_not_start()
    {
        // A .py script, not a batch file: an entry for a batch file is never used (see
        // ScriptOptionsTests). The real finder is used, and no Python is looked for instead of
        // the program the entry names, so the .py file is never run by anything.
        var settings = new BeaconSettings();
        Interpreters(settings, """{ ".py": "beacon-test-no-such-program" }""");
        _options = ScriptOptions.From(settings);
        string file = _dir.File("orphan.py");
        File.WriteAllText(file, "print('never run')\n");
        var script = new Script("orphan.py", file, "Orphan", ScriptMode.Silent, null);

        var result = await Run(script);
        await _runner.Idle.WaitAsync(Limit);

        Assert.Equal(ScriptOutcome.NotStarted, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Empty(_time.Timers);
        Assert.Equal(["Script script:(path withheld) could not be started: the program that runs it was not found"], Reports());
    }

    [Fact]
    public async Task A_program_that_windows_cannot_run_is_reported_the_same_way_with_the_reason_for_the_log()
    {
        // A text file with the name of a program. Windows refuses to start it; nothing runs. It
        // is the program for a .py script, not a batch file: an entry for a batch file is never
        // used (see ScriptOptionsTests).
        string notAProgram = _dir.File("not-a-program.exe");
        File.WriteAllText(notAProgram, "this is not a program");
        var settings = new BeaconSettings();
        settings.Interpreters = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [".py"] = notAProgram });
        _options = ScriptOptions.From(settings);
        string file = _dir.File("orphan.py");
        File.WriteAllText(file, "print('never run')\n");
        var script = new Script("orphan.py", file, "Orphan", ScriptMode.Silent, null);

        var result = await Run(script);
        await _runner.Idle.WaitAsync(Limit);

        Assert.Equal(ScriptOutcome.NotStarted, result.Outcome);
        Assert.Equal(["Script script:(path withheld) could not be started"], Reports());
        Assert.Single(ReportedErrors());
    }

    [Fact]
    public async Task A_batch_file_runs_on_the_built_in_line_even_when_the_settings_name_a_program_for_it()
    {
        // The entry would have this made-up program, mapped to the real cmd.exe, print its
        // arguments instead of running the script. It is not used: the script runs.
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var runner = NewRunner(find: program => program == "my-own-program" ? cmd : ProgramFinder.Find(program));
        var settings = new BeaconSettings();
        Interpreters(settings, """{ ".cmd": ["my-own-program", "/d", "/c", "echo", "owner-line:"], ".bat": "my-own-program" }""");
        _options = ScriptOptions.From(settings);
        var script = Write("hello.cmd", "@echo off", "echo hello");

        var result = await Run(script, runner: runner);

        Assert.Equal(ScriptOutcome.Exited, result.Outcome);
        Assert.Equal("hello\r\n", result.Output);
        Assert.Equal(["2 entries of \"interpreters\" in settings.json cannot be used and are ignored."], _options.Notes);
    }

    [Fact]
    public async Task A_batch_files_commands_are_not_echoed_into_its_output()
    {
        // Echo is on in a batch file until it is turned off, so with nothing to stop it each
        // command would be printed with the script's folder in front of it, and the last of
        // them would be announced in a notification. The header lines of a script are
        // comments and the first lines of one that has not turned echo off.
        var onlyHeader = Write("quiet.cmd", "REM @beacon.title Quiet one", "REM @beacon.mode silent");
        var headerThenEcho = Write("hello.cmd", "REM @beacon.title Quiet one", "REM @beacon.mode silent", "echo hello");

        var quiet = await Run(onlyHeader);
        var hello = await Run(headerThenEcho);

        Assert.Equal(0, quiet.ExitCode);
        Assert.Equal("", quiet.Output);
        Assert.Equal("", quiet.Error);
        Assert.Equal(0, hello.ExitCode);
        Assert.Equal("hello\r\n", hello.Output);
    }

    [Fact]
    public async Task The_log_gets_the_exit_code_and_the_time_and_nothing_the_script_or_the_owner_wrote()
    {
        var script = Write(@"zebra folder\otter.cmd", "@echo off", "echo secret output %~1", "echo secret error 1>&2", "exit /b 3");

        await Run(script, "walrus");
        await _runner.Idle.WaitAsync(Limit);

        string report = Assert.Single(Reports());
        Assert.Matches(@"^Script script:\(path withheld\) ran: exit code 3 after \d+\.\d s$", report);
        Assert.DoesNotMatch(new Regex("zebra|otter|walrus|secret|Walrus title", RegexOptions.IgnoreCase), report);
        Assert.Empty(ReportedErrors());
    }

    [Fact]
    public async Task A_callback_that_throws_is_reported_and_the_runner_carries_on()
    {
        var script = Write("quick.cmd", "@echo off", "echo quick");

        _runner.Start(script, null, _ => throw new InvalidOperationException("private words from the callback"));
        await _runner.Idle.WaitAsync(Limit);

        Assert.Equal("The result of script script:(path withheld) could not be shown", Reports()[^1]);
        Assert.IsType<InvalidOperationException>(Assert.Single(ReportedErrors()));
        Assert.Equal(0, _runner.Running);
        Assert.Equal("quick\r\n", (await Run(script)).Output);
    }

    [Fact]
    public void A_callback_that_throws_for_a_refused_script_does_not_throw_out_of_start()
    {
        var script = Write("marks.cmd", "@echo off");

        Assert.Null(Record.Exception(() =>
            _runner.Start(script, "a&b", _ => throw new InvalidOperationException("private words from the callback"))));

        Assert.Equal("The result of script script:(path withheld) could not be shown", Reports()[^1]);
    }

    [Fact]
    public async Task A_program_the_script_leaves_running_does_not_hold_up_the_result()
    {
        // The script starts a second cmd in the background and ends at once. The second cmd
        // has the script's output open, so the end of that output does not come until it
        // closes, and it does not close until this test says so (or, failing that, after
        // about a minute by itself).
        string release = _dir.File(@"lingers\release.txt");
        string gone = _dir.File(@"lingers\gone.txt");
        Write(
            @"lingers\linger.cmd",
            "@echo off",
            "set n=0",
            ":wait",
            "if exist \"%~dp0release.txt\" goto done",
            "set /a n+=1",
            "if %n% geq 60 goto done",
            $"waitfor /t 1 {UniqueSignal()} >nul 2>&1",
            "goto wait",
            ":done",
            "echo gone> \"%~dp0gone.txt\"");
        var script = Write(@"lingers\leaves.cmd", "@echo off", "start /b cmd /d /c call \"%~dp0linger.cmd\"", "echo done");

        try
        {
            var result = await Run(script);

            Assert.Equal(ScriptOutcome.Exited, result.Outcome);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("done\r\n", result.Output);
            Assert.False(File.Exists(gone), "the program left running should still be running when the result arrives");
        }
        finally
        {
            File.WriteAllText(release, "");
            await Eventually(() => File.Exists(gone) && !IsHeld(gone), "the program left running to end");
        }
    }

    [Fact]
    public async Task What_is_still_on_its_way_when_the_script_ends_is_read_before_the_result_is_handed_over()
    {
        // The script starts a second cmd in the background and ends without waiting for it.
        // The second one prints its line a moment after the script has gone. The result is not
        // put together until that output has ended too. This runner waits for that for as long
        // as the test's own limit, so the line is there however slow the machine is.
        using var runner = NewRunner(drainTime: Limit);
        Write(@"late\late.cmd", "@echo off", "echo late line");
        var script = Write(@"late\early.cmd", "@echo off", "start /b cmd /d /c call \"%~dp0late.cmd\"", "echo early line");

        var result = await Run(script, runner: runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("early line\r\n", result.Output);
        Assert.Contains("late line\r\n", result.Output);
    }

    [Fact]
    public void Nothing_is_started_once_the_runner_has_been_disposed()
    {
        var script = Write("marks.cmd", "@echo off", "echo ran> \"%~dp0ran.txt\"");
        _runner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _runner.Start(script, null, _ => { }));

        Assert.Equal(0, _runner.Running);
        Assert.False(File.Exists(_dir.File("ran.txt")));
    }

    [Fact]
    public void A_runner_that_has_been_disposed_throws_for_a_script_it_would_have_refused_too_and_calls_nobody_back()
    {
        // A batch file with text cmd would act on, a batch file whose own path cmd would act on,
        // and a PowerShell script whose path PowerShell would misread are refused with a result
        // while the runner is in use. Once it has been disposed the one answer is the
        // exception, and "finished" is not called for any of them.
        var ordinary = Write("marks.cmd", "@echo off");
        var unsafePath = new Script(@"R&D\marks.cmd", @"C:\BeaconTestNoSuchFolder\R&D\marks.cmd", "Marks", ScriptMode.Silent, null);
        var unsafePowerShell = new Script(@"marks[1].ps1", @"C:\BeaconTestNoSuchFolder\marks[1].ps1", "Marks", ScriptMode.Silent, null);
        int calls = 0;
        _runner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _runner.Start(ordinary, null, _ => calls++));
        Assert.Throws<ObjectDisposedException>(() => _runner.Start(ordinary, "a&b", _ => calls++));
        Assert.Throws<ObjectDisposedException>(() => _runner.Start(unsafePath, null, _ => calls++));
        Assert.Throws<ObjectDisposedException>(() => _runner.Start(unsafePowerShell, null, _ => calls++));

        Assert.Equal(0, calls);
        Assert.Equal(0, _runner.Running);
        Assert.Empty(_time.Timers);
        Assert.Empty(Reports());
    }
}
