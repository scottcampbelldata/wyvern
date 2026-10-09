using Beacon.Core.Library;
using Beacon.Core.Scripts;
using Beacon.Core.Settings;

namespace Beacon.Tests;

/// <summary>
/// What would be started for a script. Nothing is started here, and nothing is looked for on
/// the disk: the programs "installed" are the ones a test says are.
/// </summary>
public class ScriptCommandTests
{
    // Batch files run on this one cmd.exe and on none found by name, so it is the only form of
    // "cmd" that is installed here.
    private static readonly string SystemCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh", "powershell", "python", SystemCmd, "node",
    };

    // A name is "installed" under C:\Programs. A full path is found as it is, when a test says it exists.
    private string? Find(string program) => !_installed.Contains(program)
        ? null
        : Path.IsPathFullyQualified(program) ? program : @"C:\Programs\" + program + ".exe";

    private static Script Script(string fileName) =>
        new(fileName, @"C:\lib\scripts\my tools\" + fileName, "A script", ScriptMode.Silent, null);

    private static ScriptOptions Options(string? json = null)
    {
        var settings = new BeaconSettings();
        if (json is not null)
        {
            settings.Interpreters = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        }

        return ScriptOptions.From(settings);
    }

    private ScriptCommand? For(string fileName, string? argument = null, string? interpreters = null) =>
        ScriptCommand.For(Script(fileName), argument, Options(interpreters), Find);

    [Fact]
    public void A_powershell_script_runs_under_pwsh_without_a_profile_and_with_the_policy_set_aside()
    {
        var command = For("backup.ps1");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\pwsh.exe", command.Program);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\lib\scripts\my tools\backup.ps1"],
            command.Arguments);
    }

    [Fact]
    public void Without_pwsh_a_powershell_script_runs_under_windows_powershell_in_the_same_way()
    {
        _installed.Remove("pwsh");

        var command = For("backup.ps1");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\powershell.exe", command.Program);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\lib\scripts\my tools\backup.ps1"],
            command.Arguments);
    }

    [Theory]
    [InlineData("report.py", "python")]
    [InlineData("REPORT.PY", "python")]
    [InlineData("serve.js", "node")]
    public void Python_and_node_get_the_script_and_nothing_else(string fileName, string program)
    {
        var command = For(fileName);

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\" + program + ".exe", command.Program);
        Assert.Equal([@"C:\lib\scripts\my tools\" + fileName], command.Arguments);
    }

    [Theory]
    [InlineData("build.cmd")]
    [InlineData("build.bat")]
    public void A_batch_file_runs_under_cmd_with_a_first_word_that_is_not_in_quotes(string fileName)
    {
        var command = For(fileName, "tom jerry");

        Assert.NotNull(command);
        Assert.Equal(SystemCmd, command.Program);
        Assert.Equal(["/d", "/q", "/v:off", "/c", "@", @"C:\lib\scripts\my tools\" + fileName, "tom jerry"], command.Arguments);
    }

    public static TheoryData<string, string, string[]> EveryKindOfScript => new()
    {
        { "backup.ps1", @"C:\Programs\pwsh.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"] },
        { "report.py", @"C:\Programs\python.exe", [] },
        { "serve.js", @"C:\Programs\node.exe", [] },
        { "build.cmd", SystemCmd, ["/d", "/q", "/v:off", "/c", "@"] },
        { "build.bat", SystemCmd, ["/d", "/q", "/v:off", "/c", "@"] },
    };

    [Theory]
    [MemberData(nameof(EveryKindOfScript))]
    public void Each_kind_of_script_gets_exactly_its_own_arguments_then_the_path_then_the_text_when_there_is_one(
        string fileName, string program, string[] own)
    {
        string path = @"C:\lib\scripts\my tools\" + fileName;

        var without = For(fileName);
        var with = For(fileName, "tom jerry");

        Assert.NotNull(without);
        Assert.NotNull(with);
        Assert.Equal(program, without.Program);
        Assert.Equal(program, with.Program);
        Assert.Equal([.. own, path], without.Arguments);
        Assert.Equal([.. own, path, "tom jerry"], with.Arguments);
    }

    [Fact]
    public void The_argument_is_one_value_after_the_script_exactly_as_typed()
    {
        var command = For("report.py", "  tom & \"jerry\"  ");

        Assert.NotNull(command);
        Assert.Equal([@"C:\lib\scripts\my tools\report.py", "  tom & \"jerry\"  "], command.Arguments);
    }

    [Fact]
    public void An_empty_argument_is_still_passed()
    {
        var command = For("report.py", "");

        Assert.NotNull(command);
        Assert.Equal([@"C:\lib\scripts\my tools\report.py", ""], command.Arguments);
    }

    [Fact]
    public void A_script_that_takes_no_argument_gets_none()
    {
        var command = For("report.py", argument: null);

        Assert.NotNull(command);
        Assert.Equal([@"C:\lib\scripts\my tools\report.py"], command.Arguments);
    }

    [Fact]
    public void An_entry_in_the_settings_replaces_the_built_in_program_and_its_arguments()
    {
        _installed.Add("py");

        var command = For("report.py", "x", """{ ".py": ["py", "-3", "-u"] }""");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\py.exe", command.Program);
        Assert.Equal(["-3", "-u", @"C:\lib\scripts\my tools\report.py", "x"], command.Arguments);
    }

    [Theory]
    [InlineData("""{ ".py": ["cmd", "/c", "conda", "run", "python"] }""")]
    [InlineData("""{ ".py": "cmd" }""")]
    [InlineData("""{ ".py": ["CMD.exe", "/c"] }""")]
    [InlineData("""{ ".py": "C:\\Windows\\System32\\cmd.exe" }""")]
    public void A_script_that_is_not_a_batch_file_is_never_run_through_cmd_whatever_the_settings_say(string interpreters)
    {
        // Through cmd the typed text would be read as a command: "a&echo(INJECTED" ran echo.
        var command = For("report.py", "a&echo(INJECTED", interpreters);

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\python.exe", command.Program);
        Assert.Equal([@"C:\lib\scripts\my tools\report.py", "a&echo(INJECTED"], command.Arguments);
    }

    [Theory]
    [InlineData("build.cmd", """{ ".cmd": ["cmd", "/c"] }""")]
    [InlineData("build.bat", """{ ".bat": "cmd" }""")]
    [InlineData("build.cmd", """{ ".cmd": ["CMD.exe", "/d", "/c"] }""")]
    [InlineData("build.cmd", """{ ".cmd": "C:\\Windows\\System32\\cmd.exe" }""")]
    [InlineData("build.cmd", """{ ".cmd": "py" }""")]
    [InlineData("build.bat", """{ ".bat": ["python", "-u"] }""")]
    [InlineData("build.cmd", """{ ".cmd": ["my-own-program", "/c", "echo"] }""")]
    [InlineData("build.bat", """{ ".bat": "C:\\Tools\\my-own-program.exe" }""")]
    [InlineData("build.cmd", """{ "cmd": "py" }""")]
    [InlineData("build.cmd", """{ ".CMD": "py" }""")]
    [InlineData("build.bat", """{ "  .Bat  ": "py" }""")]
    public void A_batch_file_always_runs_on_the_built_in_cmd_line_whatever_the_settings_say(string fileName, string interpreters)
    {
        // Whatever program the entry names, it is not used: an owner's line without the "@"
        // would have cmd take the quotes off a quoted path and start a different file, and a
        // program other than cmd would not follow the rules for text that BatchText enforces.
        _installed.Add("py");
        _installed.Add("my-own-program");
        _installed.Add(@"C:\Tools\my-own-program.exe");

        var command = For(fileName, "tom jerry", interpreters);

        Assert.NotNull(command);
        Assert.Equal(SystemCmd, command.Program);
        Assert.Equal(["/d", "/q", "/v:off", "/c", "@", @"C:\lib\scripts\my tools\" + fileName, "tom jerry"], command.Arguments);
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], Options(interpreters).Notes);
    }

    private static readonly string[] BeaconsPowerShellArguments =
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    [Theory]
    [InlineData("""{ ".ps1": "pwsh" }""", @"C:\Programs\pwsh.exe")]
    [InlineData("""{ ".ps1": ["pwsh"] }""", @"C:\Programs\pwsh.exe")]
    [InlineData("""{ ".ps1": "powershell" }""", @"C:\Programs\powershell.exe")]
    [InlineData("""{ ".ps1": "PowerShell" }""", @"C:\Programs\PowerShell.exe")]
    [InlineData("""{ ".ps1": "PWSH" }""", @"C:\Programs\PWSH.exe")]
    public void An_entry_for_powershell_names_the_program_only_and_beacons_own_arguments_are_always_used(string interpreters, string program)
    {
        // The test's finder gives back the name as it was written, so the case shows in the path.
        var command = For("backup.ps1", "tom jerry", interpreters);

        Assert.NotNull(command);
        Assert.Equal(program, command.Program);
        Assert.Equal([.. BeaconsPowerShellArguments, @"C:\lib\scripts\my tools\backup.ps1", "tom jerry"], command.Arguments);
    }

    [Fact]
    public void The_full_path_of_a_powershell_gets_beacons_own_arguments_in_the_same_way()
    {
        const string windowsPowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        _installed.Add(windowsPowerShell);

        var command = For("backup.ps1", "tom jerry", """{ ".ps1": "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" }""");

        Assert.NotNull(command);
        Assert.Equal(windowsPowerShell, command.Program);
        Assert.Equal([.. BeaconsPowerShellArguments, @"C:\lib\scripts\my tools\backup.ps1", "tom jerry"], command.Arguments);
    }

    [Theory]
    [InlineData("""{ ".ps1": ["pwsh", "-NoProfile"] }""")]
    [InlineData("""{ ".ps1": ["powershell", "-Command"] }""")]
    [InlineData("""{ ".ps1": ["powershell", "-File"] }""")]
    [InlineData("""{ ".ps1": ["pwsh", "-c", "-File"] }""")]
    [InlineData("""{ ".ps1": ["PowerShell.EXE", "/c"] }""")]
    [InlineData("""{ ".ps1": ["pwsh", ""] }""")]
    public void An_entry_for_powershell_with_an_argument_of_its_own_is_not_used_and_the_built_in_line_stays(string interpreters)
    {
        // PowerShell given -Command, a prefix of it, or a first argument it does not know, runs
        // the text typed for the script as code. No list of switches to refuse can be complete,
        // so an entry for PowerShell may not have any.
        var command = For("backup.ps1", "tom jerry", interpreters);

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\pwsh.exe", command.Program);
        Assert.Equal([.. BeaconsPowerShellArguments, @"C:\lib\scripts\my tools\backup.ps1", "tom jerry"], command.Arguments);
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], Options(interpreters).Notes);
    }

    [Fact]
    public void An_unusable_entry_for_powershell_leaves_windows_powershell_when_that_is_the_built_in_program()
    {
        _installed.Remove("pwsh");

        var command = For("backup.ps1", null, """{ ".ps1": ["powershell", "-Command"] }""");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\powershell.exe", command.Program);
        Assert.Equal([.. BeaconsPowerShellArguments, @"C:\lib\scripts\my tools\backup.ps1"], command.Arguments);
    }

    [Fact]
    public void A_program_that_is_not_powershell_keeps_its_own_arguments()
    {
        _installed.Add("py");
        _installed.Add("pwsh-preview");

        var python = For("backup.ps1", null, """{ ".ps1": ["py", "-3"] }""");
        var preview = For("backup.ps1", null, """{ ".ps1": ["pwsh-preview", "-NoProfile", "-File"] }""");

        Assert.NotNull(python);
        Assert.Equal(["-3", @"C:\lib\scripts\my tools\backup.ps1"], python.Arguments);
        Assert.NotNull(preview);
        Assert.Equal(["-NoProfile", "-File", @"C:\lib\scripts\my tools\backup.ps1"], preview.Arguments);
    }

    [Fact]
    public void An_entry_for_one_kind_leaves_the_others_on_their_built_in_programs()
    {
        _installed.Add("py");

        var command = For("serve.js", null, """{ ".py": "py" }""");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Programs\node.exe", command.Program);
    }

    [Fact]
    public void A_built_in_program_that_is_not_installed_gives_nothing_to_start()
    {
        _installed.Remove("python");

        Assert.Null(For("report.py"));
    }

    [Fact]
    public void A_program_from_the_settings_that_is_not_installed_gives_nothing_to_start_and_the_built_in_one_is_not_used_instead()
    {
        Assert.Null(For("report.py", null, """{ ".py": "python9" }"""));
    }

    [Fact]
    public void With_neither_powershell_installed_there_is_nothing_to_start()
    {
        _installed.Remove("pwsh");
        _installed.Remove("powershell");

        Assert.Null(For("backup.ps1"));
    }

    [Fact]
    public void Only_the_five_kinds_of_script_have_a_built_in_program()
    {
        Assert.Null(ScriptCommand.BuiltIn(".rb", Find));
        Assert.Null(ScriptCommand.BuiltIn(".exe", Find));
        Assert.Null(ScriptCommand.BuiltIn("", Find));
    }

    [Theory]
    [InlineData("build.cmd")]
    [InlineData("build.bat")]
    public void With_the_real_finder_a_batch_file_runs_on_the_cmd_in_the_system_folder(string fileName)
    {
        // BatchText's rules are made for the real cmd.exe, so that is the one used.
        var command = ScriptCommand.For(Script(fileName), "tom jerry", ScriptOptions.Default, program => ProgramFinder.Find(program));

        Assert.NotNull(command);
        Assert.True(Path.IsPathFullyQualified(command.Program));
        Assert.Equal(SystemCmd, command.Program, ignoreCase: true);
        Assert.True(File.Exists(command.Program));
    }

    [Fact]
    public void A_cmd_exe_on_the_path_is_not_the_one_batch_files_run_on()
    {
        using var folder = new TempDir();
        File.WriteAllText(folder.File("cmd.exe"), "");
        string? onThePath(string program) => ProgramFinder.Find(program, folder.Path);
        Assert.Equal(folder.File("cmd.exe"), onThePath("cmd"));

        // The PATH here has that folder alone, so a program looked for as "cmd" would be that file.
        var command = ScriptCommand.For(Script("build.cmd"), null, ScriptOptions.Default, onThePath);

        Assert.NotNull(command);
        Assert.Equal(SystemCmd, command.Program, ignoreCase: true);
    }
}
