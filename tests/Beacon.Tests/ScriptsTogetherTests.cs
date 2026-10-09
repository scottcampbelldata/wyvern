using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core.Launcher;
using Beacon.Core.Library;
using Beacon.Core.Scripts;
using Beacon.Core.Search;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>
/// The pieces that make a script a command, all of them real and joined as Beacon joins them:
/// the loader reads the library, the search engine and the launcher's view model find the
/// script and take its argument, the provider hands it to the runner, and the runner starts a
/// real process. The one script is a tiny batch file written into a temporary folder, as in
/// <see cref="ScriptRunnerTests"/>: cmd is the only program run, and nothing touches the network.
/// </summary>
public sealed class ScriptsTogetherTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new();
    private readonly List<string> _notes = [];
    private readonly List<string> _logged = [];
    private ScriptRunner? _runner;

    public void Dispose()
    {
        _runner?.Dispose();
        _runner?.Idle.Wait(Limit);

        // A process that has just ended can hold its folder for a moment longer.
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

    private void Note(string note)
    {
        lock (_notes)
        {
            _notes.Add(note);
        }
    }

    private void Log(string line, Exception? error)
    {
        lock (_logged)
        {
            _logged.Add(line);
            if (error is not null)
            {
                _logged.Add("exception " + error.GetType().Name);
            }
        }
    }

    [Fact]
    public async Task A_script_in_the_library_is_found_run_with_an_argument_and_announced_and_nothing_private_is_kept()
    {
        // The library: one silent script. Its title, its file name and what is typed for it are
        // all words that must never reach the log or the usage file.
        var data = new DataPaths(_dir.File("data"));
        Directory.CreateDirectory(data.ScriptsDir);
        File.WriteAllText(
            Path.Combine(data.ScriptsDir, "walrus-ledger.cmd"),
            string.Join("\r\n", "REM @beacon.title Zebra payroll", "REM @beacon.argument Which otter", "@echo off", "echo got [%~1]") + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var snapshot = LibraryLoader.Load(data);
        var script = Assert.Single(snapshot.Scripts);
        Assert.Equal("Zebra payroll", script.Title);
        Assert.Equal("Which otter", script.ArgumentPrompt);

        // The real runner, with the real settings and the real way of finding cmd; the provider's
        // post runs the action at once, where the app would hand it to the UI thread.
        string usageFile = _dir.File("usage.json");
        var usage = new UsageStore(usageFile, new FakeClock());
        _runner = new ScriptRunner(() => ScriptOptions.Default, Log);
        var provider = new ScriptProvider(() => snapshot, _runner, new FakeProcessLauncher(), new OutputViewModel(), Note, action => action());
        var viewModel = new LauncherViewModel(new SearchEngine([provider], usage), usage);

        // Found by its title. Enter asks for the argument; Enter again runs it with the text.
        viewModel.Query = "zebra";
        Assert.Equal("Zebra payroll", Assert.Single(viewModel.Results).Title);
        viewModel.RunSelected();
        Assert.True(viewModel.InArgumentMode);
        viewModel.Query = "otter secret";
        viewModel.RunSelected();

        await _runner.Idle.WaitAsync(Limit);

        string note;
        lock (_notes)
        {
            note = Assert.Single(_notes);
        }

        Assert.Equal("got [otter secret]", note);

        // The usage file holds the script's id and nothing else of it.
        using (var usageJson = JsonDocument.Parse(File.ReadAllText(usageFile)))
        {
            Assert.Equal(["script:walrus-ledger.cmd"], usageJson.RootElement.EnumerateObject().Select(property => property.Name));
        }

        // The runner's lines for the log: that it ran, its exit code and its time.
        string[] logged;
        lock (_logged)
        {
            logged = [.. _logged];
        }

        string line = Assert.Single(logged);
        Assert.Matches(@"^Script script:\(path withheld\) ran: exit code 0 after \d+\.\d s$", line);
        Assert.DoesNotMatch(new Regex("zebra|payroll|walrus|ledger|\\.cmd|otter|secret|beacon-tests", RegexOptions.IgnoreCase), line);
        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.Query);
    }
}
