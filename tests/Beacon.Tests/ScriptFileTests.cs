using Beacon.Core.Library;

namespace Beacon.Tests;

public class ScriptFileTests
{
    private const string File = @"C:\lib\scripts\tools\restart.ps1";

    private static Script? Parse(params string[] lines) => ScriptFile.Parse(lines, @"tools\restart.ps1", File);

    private static IReadOnlyList<string> Header(string text) => ScriptFile.ReadHeader(new StringReader(text));

    [Theory]
    [InlineData("backup.ps1", true)]
    [InlineData("backup.PS1", true)]
    [InlineData("report.py", true)]
    [InlineData("build.cmd", true)]
    [InlineData("build.bat", true)]
    [InlineData("serve.js", true)]
    [InlineData("notes.txt", false)]
    [InlineData("backup.ps1.bak", false)]
    [InlineData("ps1", false)]
    [InlineData("tool.exe", false)]
    public void Five_kinds_of_file_are_scripts(string fileName, bool expected)
    {
        Assert.Equal(expected, ScriptFile.HasScriptExtension(fileName));
    }

    [Theory]
    [InlineData("build.cmd", true)]
    [InlineData("BUILD.BAT", true)]
    [InlineData("backup.ps1", false)]
    [InlineData("serve.js", false)]
    public void Batch_files_are_the_two_kinds_that_cmd_runs(string fileName, bool expected)
    {
        Assert.Equal(expected, ScriptFile.IsBatch(fileName));
    }

    [Theory]
    [InlineData("backup.ps1", true)]
    [InlineData("BACKUP.PS1", true)]
    [InlineData(@"C:\lib\scripts\report[1].ps1", true)]
    [InlineData("build.cmd", false)]
    [InlineData("report.py", false)]
    [InlineData("backup.ps1.bak", false)]
    [InlineData("ps1", false)]
    public void A_powershell_script_is_a_file_ending_in_ps1_in_any_case(string fileName, bool expected)
    {
        Assert.Equal(expected, ScriptFile.IsPowerShell(fileName));
    }

    [Fact]
    public void A_file_without_a_header_gets_its_name_as_title_runs_silently_and_takes_no_argument()
    {
        var script = Parse("Write-Output 'hello'");

        Assert.Equal(new Script(@"tools\restart.ps1", File, "restart", ScriptMode.Silent, null), script);
        Assert.Equal(@"script:tools\restart.ps1", script!.Id);
        Assert.Equal(".ps1", script.Extension);
    }

    [Fact]
    public void A_file_with_nothing_in_it_is_still_a_script()
    {
        Assert.Equal("restart", Parse()!.Title);
    }

    [Theory]
    [InlineData("# @beacon.title Restart Docker")]
    [InlineData("// @beacon.title Restart Docker")]
    [InlineData(":: @beacon.title Restart Docker")]
    [InlineData("REM @beacon.title Restart Docker")]
    [InlineData("rem @beacon.title Restart Docker")]
    [InlineData("Rem\t@beacon.title Restart Docker")]
    [InlineData("#@beacon.title Restart Docker")]
    [InlineData("//@beacon.title Restart Docker")]
    [InlineData("::@beacon.title Restart Docker")]
    [InlineData("   #   @beacon.title   Restart Docker   ")]
    [InlineData("\t// @beacon.title\tRestart Docker")]
    [InlineData("# @Beacon.Title Restart Docker")]
    public void The_title_is_read_after_any_of_the_four_comment_marks(string line)
    {
        Assert.Equal("Restart Docker", Parse(line)!.Title);
    }

    [Theory]
    [InlineData("@beacon.title Not in a comment")]
    [InlineData("REM@beacon.title No space after REM")]
    [InlineData("@REM @beacon.title Not one of the four marks")]
    [InlineData("@rem @beacon.title Not one of the four marks")]
    [InlineData("remove @beacon.title Not the word REM")]
    [InlineData("; @beacon.title Not one of the four marks")]
    [InlineData("-- @beacon.title Not one of the four marks")]
    [InlineData("# see @beacon.title Not straight after the mark")]
    [InlineData("## @beacon.title Two marks")]
    [InlineData("echo hello # @beacon.title Not at the start of the line")]
    [InlineData("# @beacon.titles An unknown key")]
    [InlineData("# @beacon.title:Colon An unknown key")]
    [InlineData("# @beacon. title A space inside the key")]
    [InlineData("# @beacontitle No dot")]
    public void Any_other_line_is_not_a_title(string line)
    {
        Assert.Equal("restart", Parse(line)!.Title);
    }

    [Fact]
    public void A_title_that_holds_another_key_is_still_a_title_and_the_other_key_is_not_read()
    {
        // The rest of the line is the title, whatever is in it. A key written inside it is not
        // a line of its own, so it does not make the script run in the mode it names.
        var script = Parse("# @beacon.title Run @beacon.mode output now");

        Assert.NotNull(script);
        Assert.Equal("Run @beacon.mode output now", script.Title);
        Assert.Equal(ScriptMode.Silent, script.Mode);
        Assert.Null(script.ArgumentPrompt);
    }

    [Fact]
    public void An_empty_title_leaves_the_file_name()
    {
        Assert.Equal("restart", Parse("# @beacon.title")!.Title);
        Assert.Equal("restart", Parse("# @beacon.title    ")!.Title);
    }

    [Theory]
    [InlineData("# @beacon.mode output", ScriptMode.Output)]
    [InlineData("# @beacon.mode OUTPUT", ScriptMode.Output)]
    [InlineData("// @beacon.mode   output  ", ScriptMode.Output)]
    [InlineData(":: @beacon.mode silent", ScriptMode.Silent)]
    [InlineData("REM @beacon.mode Silent", ScriptMode.Silent)]
    public void The_mode_is_silent_or_output_in_any_case(string line, ScriptMode expected)
    {
        Assert.Equal(expected, Parse(line)!.Mode);
    }

    [Theory]
    [InlineData("# @beacon.mode loud")]
    [InlineData("# @beacon.mode")]
    [InlineData("# @beacon.mode output please")]
    [InlineData("# @beacon.mode silent,output")]
    public void Any_other_mode_makes_the_file_a_bad_item(string line)
    {
        Assert.Null(Parse("# @beacon.title Fine so far", line));
    }

    [Fact]
    public void A_bad_mode_is_not_rescued_by_a_good_one_on_another_line()
    {
        Assert.Null(Parse("# @beacon.mode output", "# @beacon.mode loud"));
        Assert.Null(Parse("# @beacon.mode loud", "# @beacon.mode output"));
    }

    [Fact]
    public void An_argument_line_makes_the_script_take_an_argument_and_the_rest_of_the_line_is_the_prompt()
    {
        Assert.Equal("Container name", Parse("# @beacon.argument Container name")!.ArgumentPrompt);
        Assert.Equal("What to search for: a word, or two", Parse("// @beacon.argument   What to search for: a word, or two ")!.ArgumentPrompt);
    }

    [Fact]
    public void An_argument_line_without_a_prompt_gets_a_fixed_one()
    {
        Assert.Equal("Argument", Parse("# @beacon.argument")!.ArgumentPrompt);
    }

    [Fact]
    public void Other_beacon_keys_are_ignored()
    {
        var script = Parse("# @beacon.icon rocket", "# @beacon.schedule daily", "# @beacon.title Named");

        Assert.Equal(new Script(@"tools\restart.ps1", File, "Named", ScriptMode.Silent, null), script);
    }

    [Fact]
    public void All_three_keys_together_in_any_order_and_among_other_lines()
    {
        var script = Parse(
            "#!/usr/bin/env pwsh",
            "",
            "# Restarts the container named on the command line.",
            "# @beacon.argument Container name",
            "param($name)",
            "# @beacon.mode output",
            "# @beacon.title Restart Docker");

        Assert.Equal(new Script(@"tools\restart.ps1", File, "Restart Docker", ScriptMode.Output, "Container name"), script);
    }

    [Fact]
    public void When_a_key_is_given_twice_the_later_line_counts()
    {
        var script = Parse("# @beacon.title First", "# @beacon.title Second", "# @beacon.mode output", "# @beacon.mode silent");

        Assert.Equal("Second", script!.Title);
        Assert.Equal(ScriptMode.Silent, script.Mode);
    }

    [Fact]
    public void Only_the_first_twenty_lines_are_looked_at()
    {
        string[] lines = Enumerable.Repeat("echo filler", 21).ToArray();
        lines[19] = "# @beacon.title On line twenty";
        lines[20] = "# @beacon.mode loud";

        var script = Parse(lines);

        Assert.NotNull(script);
        Assert.Equal("On line twenty", script.Title);
    }

    [Fact]
    public void A_byte_order_mark_left_on_the_first_line_does_not_hide_it()
    {
        Assert.Equal("Marked", Parse("\uFEFF# @beacon.title Marked")!.Title);
    }

    [Fact]
    public void A_file_named_only_by_its_extension_gets_the_whole_name_as_its_title()
    {
        Assert.Equal(".ps1", ScriptFile.Parse([], ".ps1", @"C:\lib\scripts\.ps1")!.Title);
    }

    [Fact]
    public void The_header_is_split_at_every_kind_of_line_break()
    {
        Assert.Equal(["one", "two", "", "three", "four"], Header("one\r\ntwo\n\nthree\rfour"));
        Assert.Equal(["one"], Header("one\r\n"));
        Assert.Empty(Header(""));
    }

    [Fact]
    public void No_more_than_twenty_lines_are_taken()
    {
        string text = string.Join("\n", Enumerable.Range(1, 50).Select(n => $"line {n}"));

        var header = Header(text);

        Assert.Equal(20, header.Count);
        Assert.Equal("line 20", header[^1]);
    }

    [Fact]
    public void Reading_stops_at_the_character_limit_and_never_takes_the_whole_of_a_long_file()
    {
        string text = "# @beacon.title Short\n" + new string('x', 100_000) + "\n# @beacon.title Too far down";
        var reader = new StringReader(text);

        var header = ScriptFile.ReadHeader(reader);

        Assert.Equal(["# @beacon.title Short"], header);
        // Exactly the limit was taken. The one character more that was looked at, to see
        // whether there was more of the file, was only looked at.
        Assert.Equal(text.Length - ScriptFile.MaxHeaderChars, reader.ReadToEnd().Length);
    }

    [Fact]
    public void A_line_cut_short_by_the_character_limit_is_left_out_rather_than_used_in_part()
    {
        // The second line starts inside the limit and ends outside it. Used as far as it was
        // read, its title would stop in the middle, here in the middle of a two-part character.
        string start = "# @beacon.title Whole\n# @beacon.title ";
        string filler = new string('x', ScriptFile.MaxHeaderChars - start.Length - 1);
        string text = start + filler + "\U0001F600 and more";

        var header = Header(text);

        Assert.Equal(["# @beacon.title Whole"], header);
        Assert.Equal("Whole", ScriptFile.Parse(header, "a.ps1", @"C:\a.ps1")!.Title);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100_000)]
    public void A_text_with_no_line_break_that_is_longer_than_the_limit_gives_no_header_lines(int extra)
    {
        // The one line there is goes on beyond the limit, so it is not whole, and no line is
        // used in part. This is what a minified .js file looks like.
        string start = "// @beacon.title ";
        string text = start + new string('y', ScriptFile.MaxHeaderChars - start.Length + extra);

        Assert.Empty(Header(text));
    }

    [Fact]
    public void A_last_line_that_ends_exactly_at_the_limit_with_the_file_is_whole_and_is_used()
    {
        string start = "# @beacon.title ";
        string text = start + new string('y', ScriptFile.MaxHeaderChars - start.Length);
        Assert.Equal(ScriptFile.MaxHeaderChars, text.Length);

        Assert.Equal([text], Header(text));
    }
}
