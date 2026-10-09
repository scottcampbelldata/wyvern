using System.Text.Json;
using Wyvern.Core.Scripts;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public sealed class ScriptOptionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.File("settings.json");

    /// <summary>Reads settings the way the app does: from a settings.json holding this text.</summary>
    private ScriptOptions FromFile(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var loaded = SettingsStore.Load(SettingsPath);
        Assert.Null(loaded.Error);
        return ScriptOptions.From(loaded.Settings);
    }

    [Fact]
    public void Without_the_keys_the_built_in_programs_and_five_minutes_apply()
    {
        var options = FromFile("{ }");

        Assert.Equal(TimeSpan.FromSeconds(300), options.Timeout);
        Assert.Empty(options.Notes);
        foreach (string extension in new[] { ".ps1", ".py", ".cmd", ".bat", ".js" })
        {
            Assert.Null(options.Interpreter(extension));
        }
    }

    [Fact]
    public void The_default_options_are_the_same_as_an_empty_file()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), ScriptOptions.Default.Timeout);
        Assert.Empty(ScriptOptions.Default.Notes);
        Assert.Null(ScriptOptions.Default.Interpreter(".py"));
    }

    [Fact]
    public void The_defaults_file_shows_the_timeout_and_leaves_the_interpreters_out()
    {
        var loaded = SettingsStore.Load(SettingsPath);

        string written = File.ReadAllText(SettingsPath);
        Assert.Contains("\"scriptTimeoutSeconds\": 300", written);
        Assert.DoesNotContain("interpreters", written);
        Assert.Equal(300, loaded.Settings.ScriptTimeoutSeconds?.GetInt32());
        Assert.Null(loaded.Settings.Interpreters);
        Assert.Empty(ScriptOptions.From(loaded.Settings).Notes);
    }

    [Fact]
    public void An_entry_is_the_program_followed_by_its_arguments()
    {
        var options = FromFile("""{ "interpreters": { ".py": ["py", "-3", ""], ".js": ["C:\\Tools\\deno.exe", "run"] } }""");

        Assert.Equal(["py", "-3", ""], options.Interpreter(".py"));
        Assert.Equal([@"C:\Tools\deno.exe", "run"], options.Interpreter(".js"));
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void A_plain_text_is_a_program_with_no_arguments()
    {
        var options = FromFile("""{ "interpreters": { ".py": "python3" } }""");

        Assert.Equal(["python3"], options.Interpreter(".py"));
    }

    [Fact]
    public void An_entry_replaces_the_built_in_program_for_its_own_extension_only()
    {
        var options = FromFile("""{ "interpreters": { ".py": "python3" } }""");

        Assert.NotNull(options.Interpreter(".py"));
        Assert.Null(options.Interpreter(".ps1"));
        Assert.Null(options.Interpreter(".cmd"));
        Assert.Null(options.Interpreter(".bat"));
        Assert.Null(options.Interpreter(".js"));
    }

    [Theory]
    [InlineData("py")]
    [InlineData("PY")]
    [InlineData(".Py")]
    [InlineData("  .py  ")]
    public void An_extension_may_be_written_without_its_dot_and_in_any_case(string key)
    {
        var options = FromFile($$"""{ "interpreters": { "{{key}}": "python3" } }""");

        Assert.Equal(["python3"], options.Interpreter(".py"));
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("""{ ".py": 3 }""")]
    [InlineData("""{ ".py": true }""")]
    [InlineData("""{ ".py": null }""")]
    [InlineData("""{ ".py": { "program": "python" } }""")]
    [InlineData("""{ ".py": [] }""")]
    [InlineData("""{ ".py": "" }""")]
    [InlineData("""{ ".py": "   " }""")]
    [InlineData("""{ ".py": ["", "-3"] }""")]
    [InlineData("""{ ".py": ["py", 3] }""")]
    [InlineData("""{ ".py": ["py", null] }""")]
    [InlineData("""{ ".py": ["py", ["-3"]] }""")]
    [InlineData("""{ ".py": "\ud800" }""")]
    [InlineData("""{ ".py": ["py", "\ud800"] }""")]
    [InlineData("""{ ".rb": "ruby" }""")]
    [InlineData("""{ "": "python" }""")]
    [InlineData("""{ ".py": "cmd" }""")]
    [InlineData("""{ ".py": ["cmd", "/c", "conda", "run", "python"] }""")]
    [InlineData("""{ ".py": ["CMD.exe", "/c"] }""")]
    [InlineData("""{ ".py": "  Cmd  " }""")]
    [InlineData("""{ ".py": "C:\\Windows\\System32\\cmd.exe" }""")]
    [InlineData("""{ ".py": "C:/Windows/System32/CMD.EXE" }""")]
    [InlineData("""{ ".py": "%SystemRoot%\\System32\\cmd.exe" }""")]
    [InlineData("""{ ".py": "%ComSpec%" }""")]
    public void An_entry_that_cannot_be_used_is_ignored_and_counted_and_the_built_in_program_stays(string interpreters)
    {
        var options = FromFile($$"""{ "interpreters": {{interpreters}} }""");

        Assert.Null(options.Interpreter(".py"));
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], options.Notes);
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void An_entry_for_a_batch_file_that_names_cmd_is_unusable_too_so_that_batch_files_run_on_the_built_in_line(string extension)
    {
        var options = FromFile($$"""{ "interpreters": { "{{extension}}": ["cmd", "/c"] } }""");

        Assert.Null(options.Interpreter(extension));
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], options.Notes);
    }

    [Theory]
    [InlineData(".cmd", "\"py\"")]
    [InlineData(".bat", "[\"python\", \"-u\"]")]
    [InlineData(".cmd", @"[""C:\\Tools\\my-own-program.exe"", ""/c""]")]
    [InlineData(".bat", @"""%LOCALAPPDATA%\\Tools\\shim.exe""")]
    [InlineData("cmd", "\"py\"")]
    [InlineData("bat", "\"py\"")]
    [InlineData(".CMD", "\"py\"")]
    [InlineData("  .Bat  ", "\"py\"")]
    [InlineData(".cmd", "[\"pwsh\"]")]
    public void An_entry_for_a_batch_file_is_unusable_whatever_program_it_names_so_that_batch_files_always_run_on_the_built_in_line(string key, string value)
    {
        // BatchText's rules are the rules of the built-in cmd line, so a batch file is never
        // run by any other line, and a program that is not cmd would not be an exception.
        var options = FromFile($$"""{ "interpreters": { "{{key}}": {{value}} } }""");

        Assert.Null(options.Interpreter(".cmd"));
        Assert.Null(options.Interpreter(".bat"));
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], options.Notes);
    }

    [Fact]
    public void An_entry_for_a_batch_file_does_not_spoil_the_other_entries()
    {
        var options = FromFile("""{ "interpreters": { ".cmd": "py", ".py": "python3", ".bat": "py" } }""");

        Assert.Equal(["python3"], options.Interpreter(".py"));
        Assert.Equal(["2 entries of \"interpreters\" in settings.json cannot be used and are ignored."], options.Notes);
    }

    [Theory]
    [InlineData("cmder")]
    [InlineData("cmd-shim")]
    [InlineData(@"C:\\cmd\\conda.exe")]
    [InlineData(@"C:\\Tools\\cmd.exe.d\\run.exe")]
    public void A_program_with_cmd_only_somewhere_in_its_name_is_still_usable(string program)
    {
        var options = FromFile($$"""{ "interpreters": { ".py": ["{{program}}", "-u"] } }""");

        Assert.Equal([program.Replace(@"\\", @"\"), "-u"], options.Interpreter(".py"));
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("""["pwsh", "-NoProfile"]""")]
    [InlineData("""["powershell", "-Command"]""")]
    [InlineData("""["powershell", "-File"]""")]
    [InlineData("""["pwsh", "-c"]""")]
    [InlineData("""["pwsh", "/c"]""")]
    [InlineData("""["pwsh", "-"]""")]
    [InlineData("""["pwsh", ""]""")]
    [InlineData("""["PWSH.EXE", "-NoProfile"]""")]
    [InlineData("""["  PowerShell  ", "-Command"]""")]
    [InlineData("""["C:\\Program Files\\PowerShell\\7\\pwsh.exe", "-NoProfile"]""")]
    [InlineData("""["C:/Windows/System32/WindowsPowerShell/v1.0/POWERSHELL.EXE", "-NoLogo"]""")]
    [InlineData("""["%ProgramFiles%\\PowerShell\\7\\pwsh.exe", "-NoProfile"]""")]
    public void An_entry_for_powershell_with_an_argument_of_its_own_is_ignored_and_counted_because_the_program_alone_is_named(string entry)
    {
        // PowerShell accepts any unambiguous prefix of a switch, and several kinds of dash, so
        // no list of switches to refuse is complete: "-Command" turns the text typed for the
        // script into code. Wyvern's own arguments are used instead (see ScriptCommandTests).
        var options = FromFile($$"""{ "interpreters": { ".ps1": {{entry}} } }""");

        Assert.Null(options.Interpreter(".ps1"));
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], options.Notes);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("PowerShell")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\\Program Files\\PowerShell\\7\\pwsh.exe")]
    [InlineData(@"%ProgramFiles%\\PowerShell\\7\\pwsh.exe")]
    public void An_entry_for_powershell_with_no_argument_of_its_own_is_usable_as_one_text_or_as_a_list_of_one(string program)
    {
        var asText = FromFile($$"""{ "interpreters": { ".ps1": "{{program}}" } }""");
        var asList = FromFile($$"""{ "interpreters": { ".ps1": ["{{program}}"] } }""");

        string expected = program.Replace(@"\\", @"\");
        Assert.Equal([expected], asText.Interpreter(".ps1"));
        Assert.Equal([expected], asList.Interpreter(".ps1"));
        Assert.Empty(asText.Notes);
        Assert.Empty(asList.Notes);
    }

    [Theory]
    [InlineData("pwsh-preview")]
    [InlineData("pwshell")]
    [InlineData(@"C:\\powershell\\tool.exe")]
    [InlineData(@"C:\\Tools\\pwsh.exe.d\\run.exe")]
    public void A_program_with_powershell_only_somewhere_in_its_name_keeps_its_arguments(string program)
    {
        var options = FromFile($$"""{ "interpreters": { ".ps1": ["{{program}}", "-NoProfile", "-File"] } }""");

        Assert.Equal([program.Replace(@"\\", @"\"), "-NoProfile", "-File"], options.Interpreter(".ps1"));
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void An_entry_for_cmd_does_not_take_the_place_of_a_usable_one_written_after_it()
    {
        var options = FromFile("""{ "interpreters": { ".py": "cmd", "PY": "python3", ".cmd": "cmd" } }""");

        Assert.Equal(["python3"], options.Interpreter(".py"));
        Assert.Null(options.Interpreter(".cmd"));
        Assert.Equal(["2 entries of \"interpreters\" in settings.json cannot be used and are ignored."], options.Notes);
    }

    [Fact]
    public void The_same_extension_written_twice_keeps_the_first_and_counts_the_other()
    {
        var options = FromFile("""{ "interpreters": { ".py": "first", "PY": "second" } }""");

        Assert.Equal(["first"], options.Interpreter(".py"));
        Assert.Single(options.Notes);
    }

    [Fact]
    public void Several_unusable_entries_are_counted_together_and_the_good_ones_are_kept()
    {
        var options = FromFile("""{ "interpreters": { ".py": 3, ".rb": "ruby", ".js": ["node", "--no-warnings"], ".ps1": [] } }""");

        Assert.Equal(["node", "--no-warnings"], options.Interpreter(".js"));
        Assert.Equal(["3 entries of \"interpreters\" in settings.json cannot be used and are ignored."], options.Notes);
    }

    [Fact]
    public void A_note_never_quotes_what_was_written()
    {
        var options = FromFile("""{ "interpreters": { ".walrus": "C:\\Private\\otter.exe" }, "scriptTimeoutSeconds": -77 }""");

        Assert.Equal(2, options.Notes.Count);
        Assert.All(options.Notes, note =>
        {
            Assert.DoesNotContain("walrus", note);
            Assert.DoesNotContain("otter", note);
            Assert.DoesNotContain("77", note);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(86_400)]
    public void The_timeout_is_read_in_seconds(int seconds)
    {
        var options = FromFile($$"""{ "scriptTimeoutSeconds": {{seconds}} }""");

        Assert.Equal(TimeSpan.FromSeconds(seconds), options.Timeout);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(86_401)]
    [InlineData(int.MaxValue)]
    public void A_timeout_that_is_not_from_one_second_to_one_day_is_replaced_by_five_minutes_and_noted(int seconds)
    {
        var options = FromFile($$"""{ "scriptTimeoutSeconds": {{seconds}} }""");

        Assert.Equal(TimeSpan.FromSeconds(300), options.Timeout);
        Assert.Equal(["\"scriptTimeoutSeconds\" must be a whole number from 1 to 86400. 300 is used instead."], options.Notes);
    }

    [Fact]
    public void A_null_timeout_means_five_minutes()
    {
        var options = FromFile("""{ "scriptTimeoutSeconds": null, "interpreters": null }""");

        Assert.Equal(TimeSpan.FromSeconds(300), options.Timeout);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData("60.0", 60)]
    [InlineData("60.000", 60)]
    [InlineData("6e1", 60)]
    [InlineData("6E1", 60)]
    [InlineData("0.6e2", 60)]
    [InlineData("1.0", 1)]
    [InlineData("86400.0", 86_400)]
    [InlineData("8.64e4", 86_400)]
    public void A_whole_number_of_seconds_is_used_in_whatever_form_it_is_written(string number, int seconds)
    {
        var options = FromFile($$"""{ "scriptTimeoutSeconds": {{number}} }""");

        Assert.Equal(TimeSpan.FromSeconds(seconds), options.Timeout);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("\"60\"")]
    [InlineData("\"\"")]
    [InlineData("1.5")]
    [InlineData("60.5")]
    [InlineData("0.5")]
    [InlineData("0.0")]
    [InlineData("-5")]
    [InlineData("86401")]
    [InlineData("86400.5")]
    [InlineData("99999999999")]
    [InlineData("1e999")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[60]")]
    [InlineData("""{ "seconds": 60 }""")]
    public void A_timeout_that_is_not_a_whole_number_from_1_to_86400_is_noted_and_does_not_make_the_file_unreadable(string value)
    {
        // FromFile checks that the file loads without an error: the rest of settings.json is
        // valid, and a value of the wrong kind in one key must not take the other keys with it.
        var options = FromFile($$"""{ "scriptTimeoutSeconds": {{value}} }""");

        Assert.Equal(TimeSpan.FromSeconds(300), options.Timeout);
        Assert.Equal(["\"scriptTimeoutSeconds\" must be a whole number from 1 to 86400. 300 is used instead."], options.Notes);
    }

    [Theory]
    [InlineData("\"python\"")]
    [InlineData("""["python"]""")]
    [InlineData("[]")]
    [InlineData("3")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"\"")]
    public void An_interpreters_setting_that_is_not_an_object_counts_as_one_unusable_entry(string value)
    {
        var options = FromFile($$"""{ "interpreters": {{value}} }""");

        foreach (string extension in new[] { ".ps1", ".py", ".cmd", ".bat", ".js" })
        {
            Assert.Null(options.Interpreter(extension));
        }

        Assert.Equal(TimeSpan.FromSeconds(300), options.Timeout);
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], options.Notes);
    }

    [Fact]
    public void A_key_that_is_not_readable_text_is_one_unusable_entry_and_the_others_are_kept()
    {
        // "\ud800" is half of a surrogate pair: valid JSON, but not text that can be read.
        var options = FromFile("""{ "interpreters": { "\ud800": "python", ".py": "python3" } }""");

        Assert.Equal(["python3"], options.Interpreter(".py"));
        Assert.Equal(["One entry of \"interpreters\" in settings.json cannot be used and is ignored."], options.Notes);
    }

    [Fact]
    public void Script_settings_of_the_wrong_kind_leave_every_other_setting_readable()
    {
        File.WriteAllText(
            SettingsPath,
            """{ "hotkey": "Ctrl+Shift+K", "indexedFolders": ["D:\\Work"], "scriptTimeoutSeconds": "60", "interpreters": ["python"] }""");

        var loaded = SettingsStore.Load(SettingsPath);
        var options = ScriptOptions.From(loaded.Settings);

        Assert.Null(loaded.Error);
        Assert.Equal("Ctrl+Shift+K", loaded.Settings.Hotkey);
        Assert.Equal([@"D:\Work"], loaded.Settings.IndexedFolders);
        Assert.Equal(
            [
                "One entry of \"interpreters\" in settings.json cannot be used and is ignored.",
                "\"scriptTimeoutSeconds\" must be a whole number from 1 to 86400. 300 is used instead.",
            ],
            options.Notes);
    }

    [Theory]
    [InlineData("""{ "scriptTimeoutSeconds": 60 """)]
    [InlineData("""{ "interpreters": { ".py": "python3" """)]
    [InlineData("""{ "scriptTimeoutSeconds": 6x }""")]
    public void A_file_that_is_not_valid_json_is_still_reported_as_unreadable(string json)
    {
        File.WriteAllText(SettingsPath, json);

        var loaded = SettingsStore.Load(SettingsPath);

        Assert.StartsWith("settings.json is not valid JSON", loaded.Error);
        Assert.IsAssignableFrom<JsonException>(loaded.Cause);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_the_script_settings(bool firstLoad)
    {
        var loaded = new SettingsLoadResult(new WyvernSettings { ScriptTimeoutSeconds = JsonSerializer.SerializeToElement(45) }, null);

        var decision = SettingsDecision.From(loaded, firstLoad);

        Assert.NotNull(decision.Scripts);
        Assert.Equal(TimeSpan.FromSeconds(45), decision.Scripts.Timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_them_when_the_hotkey_is_invalid_too(bool firstLoad)
    {
        var loaded = new SettingsLoadResult(new WyvernSettings { Hotkey = "Banana", ScriptTimeoutSeconds = JsonSerializer.SerializeToElement(45) }, null);

        var decision = SettingsDecision.From(loaded, firstLoad);

        Assert.True(decision.HotkeyInvalid);
        Assert.NotNull(decision.Scripts);
        Assert.Equal(TimeSpan.FromSeconds(45), decision.Scripts.Timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unreadable_file_leaves_scripts_running_as_before(bool firstLoad)
    {
        var failed = new SettingsLoadResult(new WyvernSettings(), "settings.json is not valid JSON (line 3)", new FormatException("line 3"));

        Assert.Null(SettingsDecision.From(failed, firstLoad).Scripts);
    }
}
