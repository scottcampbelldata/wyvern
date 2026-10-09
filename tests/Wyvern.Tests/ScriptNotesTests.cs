using System.Text;
using Wyvern.Core.Platform;
using Wyvern.Core.Scripts;

namespace Wyvern.Tests;

public class NoteTextTests
{
    [Fact]
    public void Text_that_fits_is_left_as_it_is()
    {
        Assert.Equal("short", NoteText.Shorten("short", 5));
        Assert.Equal("", NoteText.Shorten("", 5));
    }

    [Fact]
    public void Text_that_is_too_long_is_cut_to_the_limit_and_ends_in_an_ellipsis()
    {
        string cut = NoteText.Shorten("abcdefghij", 6);

        Assert.Equal("abcde…", cut);
        Assert.Equal(6, cut.Length);
    }

    [Fact]
    public void A_two_part_character_is_never_cut_in_half()
    {
        // The emoji sits where the cut would fall between its two halves.
        string cut = NoteText.Shorten("abcd\U0001F600fghij", 6);

        Assert.Equal("abcd…", cut);
    }

    [Fact]
    public void Characters_that_show_nothing_or_turn_the_text_around_are_taken_out()
    {
        Assert.Equal("abcd", NoteText.WithoutInvisibleCharacters("a\u202Eb\u0085c\U000E0001d\u001B"));
    }
}

public class ScriptNotesTests
{
    private static ScriptResult Exited(int exitCode, string output = "", string error = "") =>
        new(ScriptOutcome.Exited, exitCode, output, error, TimeSpan.FromSeconds(1));

    private static ScriptResult Ended(ScriptOutcome outcome, string output = "", string error = "") =>
        new(outcome, null, output, error, TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData("one\r\ntwo\r\nthree\r\n", "three")]
    [InlineData("one\ntwo\nthree", "three")]
    [InlineData("only", "only")]
    [InlineData("one\r\ntwo\r\n\r\n   \r\n\t\r\n", "two")]
    [InlineData("  padded  \r\n", "padded")]
    [InlineData("old Mac\rline breaks\r", "line breaks")]
    public void The_last_line_is_the_last_one_with_something_on_it(string text, string expected)
    {
        Assert.Equal(expected, ScriptNotes.LastLine(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("   \r\n \t \n\n")]
    public void Text_with_nothing_on_any_line_has_no_last_line(string text)
    {
        Assert.Null(ScriptNotes.LastLine(text));
    }

    [Fact]
    public void A_long_last_line_is_cut_and_never_in_the_middle_of_a_character()
    {
        string line = new string('x', ScriptNotes.MaxLineLength - 2) + "\U0001F600" + new string('y', 50);

        string? last = ScriptNotes.LastLine("earlier\r\n" + line + "\r\n");

        Assert.NotNull(last);
        Assert.Equal(new string('x', ScriptNotes.MaxLineLength - 2) + "…", last);
        Assert.True(last.Length <= ScriptNotes.MaxLineLength);
    }

    [Fact]
    public void Invisible_characters_are_taken_out_of_the_last_line_and_a_tab_becomes_a_space()
    {
        // A right-to-left override, a byte order mark, a zero-width space and a tab.
        Assert.Equal("All good now", ScriptNotes.LastLine(Real("All@@RLO@@ good@@BOM@@@@ZWSP@@\tnow\r\n")));
    }

    [Fact]
    public void A_last_line_that_is_only_invisible_characters_is_passed_over()
    {
        Assert.Equal("real", ScriptNotes.LastLine(Real("real\r\n@@ESC@@@@RLO@@\r\n")));
    }

    // A program that thinks it is writing to a terminal puts colour codes in what it prints. They
    // are an escape character, "[", some digits and semicolons, and a letter. Taking away only
    // the escape character would leave "[32m" in the notification.
    [Theory]
    [InlineData("@@ESC@@[32mAll good@@ESC@@[0m now", "All good now")]
    [InlineData("@@ESC@@[1;31;40mred on black@@ESC@@[0m", "red on black")]
    [InlineData("@@ESC@@[38;2;255;0;0mtrue colour@@ESC@@[m", "true colour")]
    [InlineData("@@ESC@@[38:5:208morange@@ESC@@[39m", "orange")]
    [InlineData("@@ESC@@[2K@@ESC@@[1Gprogress 100%", "progress 100%")]
    [InlineData("@@ESC@@[?25lhidden cursor@@ESC@@[?25h", "hidden cursor")]
    [InlineData("plain text", "plain text")]
    [InlineData("a [32m that is not a code", "a [32m that is not a code")]
    public void Colour_codes_are_taken_out_of_the_last_line_whole(string line, string expected)
    {
        Assert.Equal(expected, ScriptNotes.LastLine(Real(line) + "\r\n"));
    }

    [Fact]
    public void A_last_line_that_is_only_colour_codes_is_passed_over()
    {
        Assert.Equal("real", ScriptNotes.LastLine(Real("real\r\n@@ESC@@[0m\r\n@@ESC@@[32m@@ESC@@[1m \r\n")));
    }

    [Fact]
    public void A_colour_code_does_not_count_towards_the_length_of_the_last_line()
    {
        string coloured = Real("@@ESC@@[32m") + new string('x', ScriptNotes.MaxLineLength) + Real("@@ESC@@[0m");

        Assert.Equal(new string('x', ScriptNotes.MaxLineLength), ScriptNotes.LastLine(coloured));
    }

    [Fact]
    public void Colour_codes_are_taken_out_of_the_output_view_text_as_well()
    {
        Assert.Equal(
            "all good\r\n\r\nStandard error:\r\na warning",
            ScriptNotes.OutputText(Exited(0, Real("@@ESC@@[32mall good@@ESC@@[0m\r\n"), Real("@@ESC@@[33ma warning@@ESC@@[0m\r\n")), ".ps1"));
        Assert.Equal("one\r\ntwo", ScriptNotes.OutputText(Exited(0, Real("@@ESC@@[1mone@@ESC@@[0m\r\n@@ESC@@[4mtwo@@ESC@@[24m\r\n")), ".ps1"));
    }

    [Fact]
    public void Output_that_is_only_colour_codes_counts_as_nothing_printed()
    {
        Assert.Equal("The script printed nothing.", ScriptNotes.OutputText(Exited(0, Real("@@ESC@@[0m\r\n")), ".ps1"));
    }

    [Fact]
    public void Colour_codes_do_not_stop_a_failed_script_from_being_announced_by_its_last_error_line()
    {
        Assert.Equal(
            "Exit code 1. The disk is full",
            ScriptNotes.Notification(Exited(1, "", Real("@@ESC@@[31mThe disk is full@@ESC@@[0m\r\n")), ".ps1"));
    }

    // The test file holds the characters as the words below, so that no raw escape character or
    // right-to-left override sits in the source, where an editor would act on it.
    private static string Real(string text) => text
        .Replace("@@ESC@@", "\u001B", StringComparison.Ordinal)
        .Replace("@@RLO@@", "\u202E", StringComparison.Ordinal)
        .Replace("@@BOM@@", "\uFEFF", StringComparison.Ordinal)
        .Replace("@@ZWSP@@", "\u200B", StringComparison.Ordinal);

    [Fact]
    public void A_script_that_ended_well_is_announced_by_the_last_line_it_printed()
    {
        Assert.Equal("Backup finished", ScriptNotes.Notification(Exited(0, "Copying...\r\nBackup finished\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_ended_well_and_printed_nothing_is_announced_as_done()
    {
        Assert.Equal("Done", ScriptNotes.Notification(Exited(0), ".ps1"));
        Assert.Equal("Done", ScriptNotes.Notification(Exited(0, "\r\n  \r\n"), ".ps1"));
    }

    // Most PowerShell errors do not end the script: a backup whose copy failed still exits with 0.
    // Saying "Done" for it would tell the owner that it worked.
    [Fact]
    public void A_script_that_ended_well_and_printed_only_to_standard_error_is_announced_by_the_last_line_of_that()
    {
        Assert.Equal("Cannot copy D:\\data", ScriptNotes.Notification(Exited(0, "", "first\r\nCannot copy D:\\data\r\n"), ".ps1"));
        Assert.Equal("a warning", ScriptNotes.Notification(Exited(0, "\r\n  \r\n", "a warning\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_ended_well_and_printed_to_both_is_announced_by_the_last_line_of_standard_output()
    {
        Assert.Equal(
            "Backup finished",
            ScriptNotes.Notification(Exited(0, "Copying...\r\nBackup finished\r\n", "a warning\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_ended_well_and_wrote_nothing_that_can_be_read_to_either_is_announced_as_done()
    {
        Assert.Equal("Done", ScriptNotes.Notification(Exited(0, "", ""), ".ps1"));
        Assert.Equal("Done", ScriptNotes.Notification(Exited(0, " \r\n", "\t\r\n\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_failed_is_announced_by_its_exit_code_and_the_last_line_of_standard_error()
    {
        var result = Exited(3, "Copying...\r\nstill going\r\n", "first problem\r\nThe disk is full\r\n");

        Assert.Equal("Exit code 3. The disk is full", ScriptNotes.Notification(result, ".ps1"));
    }

    [Fact]
    public void A_script_that_failed_without_writing_to_standard_error_is_announced_by_its_exit_code_alone()
    {
        Assert.Equal("Exit code 1.", ScriptNotes.Notification(Exited(1, "printed to the wrong place\r\n"), ".cmd"));
        Assert.Equal("Exit code -1073741510.", ScriptNotes.Notification(Exited(-1073741510), ".cmd"));
    }

    [Fact]
    public void A_script_that_ran_out_of_time_is_announced_as_timed_out_with_the_last_line_of_standard_error()
    {
        Assert.Equal("Timed out and was stopped.", ScriptNotes.Notification(Ended(ScriptOutcome.TimedOut, "working\r\n"), ".py"));
        Assert.Equal(
            "Timed out and was stopped. still waiting for the server",
            ScriptNotes.Notification(Ended(ScriptOutcome.TimedOut, "", "still waiting for the server\r\n"), ".py"));
    }

    [Theory]
    [InlineData(".py")]
    [InlineData(".ps1")]
    public void A_script_whose_program_could_not_be_started_is_announced_with_the_kind_of_script(string extension)
    {
        Assert.Equal(
            $"The program that runs {extension} scripts could not be started. Details are in the log file.",
            ScriptNotes.Notification(Ended(ScriptOutcome.NotStarted), extension));
    }

    [Fact]
    public void A_refused_batch_file_is_announced_with_what_a_batch_file_cannot_be_given()
    {
        string argument = ScriptNotes.Notification(Ended(ScriptOutcome.UnsafeArgument), ".cmd");
        string path = ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".cmd");

        Assert.StartsWith("Nothing was run. A batch file cannot be given text that holds", argument);
        Assert.StartsWith("Nothing was run. cmd would act on a character in this batch file's name or folder", path);
    }

    [Fact]
    public void The_two_sentences_for_a_refused_batch_file_name_everything_that_is_refused()
    {
        // They list what BatchText refuses. When its rules change these words must change with them.
        Assert.Equal(
            "Nothing was run. A batch file cannot be given text that holds & | < > ^ % \" , ; = or a line break, or a space together with a backslash at the end, because cmd would act on them.",
            ScriptNotes.Notification(Ended(ScriptOutcome.UnsafeArgument), ".cmd"));
        Assert.Equal(
            "Nothing was run. cmd would act on a character in this batch file's name or folder: one of ( % & ^ , ; =.",
            ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".cmd"));
    }

    [Fact]
    public void A_refused_powershell_script_is_announced_with_what_powershell_would_misread_and_to_rename_it()
    {
        string path = ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".ps1");

        Assert.Equal(
            "Nothing was run. PowerShell would misread a square bracket ([ or ]) or a backtick (`) in this script's name or folder. Rename the script or its folder.",
            path);
    }

    [Fact]
    public void The_sentence_for_a_refused_path_is_chosen_by_the_kind_of_script()
    {
        string batch = ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".cmd");
        string powerShell = ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".ps1");

        Assert.Equal(batch, ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".bat"));
        Assert.StartsWith("Nothing was run. cmd would act on", batch);
        Assert.StartsWith("Nothing was run. PowerShell would misread", powerShell);
        Assert.Equal(powerShell, ScriptNotes.Notification(Ended(ScriptOutcome.UnsafePath), ".PS1"));
    }

    // What a refused run carries is nothing in practice. But the sentence is the whole of what the
    // owner is told, so whatever the result holds must not find its way into it.
    [Theory]
    [InlineData(ScriptOutcome.NotStarted, ".py")]
    [InlineData(ScriptOutcome.UnsafeArgument, ".cmd")]
    [InlineData(ScriptOutcome.UnsafePath, ".cmd")]
    [InlineData(ScriptOutcome.UnsafePath, ".ps1")]
    public void A_script_that_did_not_run_is_announced_by_a_fixed_sentence_whatever_its_result_carries(ScriptOutcome outcome, string extension)
    {
        var bare = Ended(outcome);
        var loaded = Ended(outcome, "secret output\r\n", "secret error\r\n");

        string notification = ScriptNotes.Notification(loaded, extension);

        Assert.Equal(ScriptNotes.Notification(bare, extension), notification);
        Assert.DoesNotContain("secret", notification);
        Assert.Equal(notification, ScriptNotes.OutputText(loaded, extension));
        Assert.Equal(ScriptNotes.Status(bare, extension), ScriptNotes.Status(loaded, extension));
        Assert.DoesNotContain("secret", ScriptNotes.Status(loaded, extension));
    }

    [Fact]
    public void An_ending_that_is_not_known_is_announced_by_a_neutral_sentence_and_not_as_an_unsafe_batch_path()
    {
        var unknown = Ended((ScriptOutcome)99, "secret output\r\n", "secret error\r\n");

        Assert.Equal("The script ended in a way Wyvern does not recognise.", ScriptNotes.Notification(unknown, ".cmd"));
        Assert.Equal(ScriptNotes.Notification(unknown, ".cmd"), ScriptNotes.OutputText(unknown, ".cmd"));
        Assert.Equal("Ended", ScriptNotes.Status(unknown, ".cmd"));
        Assert.Equal(ScriptNotes.Notification(unknown, ".cmd"), ScriptNotes.Notification(unknown, ".ps1"));
    }

    [Fact]
    public void Every_ending_there_is_has_a_sentence_and_a_status_of_its_own_and_none_falls_back_to_the_neutral_ones()
    {
        // A new outcome added to the enum without words of its own would be caught here.
        string neutralSentence = ScriptNotes.Notification(Ended((ScriptOutcome)99), ".cmd");
        string neutralStatus = ScriptNotes.Status(Ended((ScriptOutcome)99), ".cmd");

        Assert.All(Enum.GetValues<ScriptOutcome>(), outcome =>
        {
            Assert.NotEqual(neutralSentence, ScriptNotes.Notification(Ended(outcome), ".cmd"));
            Assert.NotEqual(neutralStatus, ScriptNotes.Status(Ended(outcome), ".cmd"));
        });
    }

    [Fact]
    public void Every_notification_fits_a_notification()
    {
        string longLine = new string('x', 5000);
        ScriptResult[] results =
        [
            Exited(0, longLine),
            Exited(-1073741510, "", longLine),
            Ended(ScriptOutcome.TimedOut, "", longLine),
            Ended(ScriptOutcome.NotStarted),
            Ended(ScriptOutcome.UnsafeArgument),
            Ended(ScriptOutcome.UnsafePath),
        ];

        Assert.All(results, result => Assert.True(ScriptNotes.Notification(result, ".ps1").Length <= 250));
        Assert.All(results, result => Assert.True(ScriptNotes.Notification(result, ".cmd").Length <= 250));
    }

    [Theory]
    [InlineData(0, "Finished")]
    [InlineData(3, "Exit code 3")]
    [InlineData(-1, "Exit code -1")]
    public void The_status_of_a_script_that_ended_is_finished_or_its_exit_code(int exitCode, string expected)
    {
        Assert.Equal(expected, ScriptNotes.Status(Exited(exitCode), ".ps1"));
    }

    [Fact]
    public void The_status_of_the_other_endings()
    {
        Assert.Equal("Timed out", ScriptNotes.Status(Ended(ScriptOutcome.TimedOut), ".js"));
        Assert.Equal("Could not start the program for .js scripts", ScriptNotes.Status(Ended(ScriptOutcome.NotStarted), ".js"));
        Assert.Equal("Not run", ScriptNotes.Status(Ended(ScriptOutcome.UnsafeArgument), ".cmd"));
        Assert.Equal("Not run", ScriptNotes.Status(Ended(ScriptOutcome.UnsafePath), ".cmd"));
        Assert.Equal("Not run", ScriptNotes.Status(Ended(ScriptOutcome.UnsafePath), ".ps1"));
    }

    [Fact]
    public void The_output_view_shows_everything_printed_without_the_line_breaks_at_the_very_end()
    {
        Assert.Equal("one\r\n\r\n  two", ScriptNotes.OutputText(Exited(0, "one\r\n\r\n  two\r\n\r\n"), ".ps1"));
    }

    [Fact]
    public void Standard_error_is_shown_under_its_own_heading_whatever_the_exit_code()
    {
        Assert.Equal(
            "one\r\ntwo\r\n\r\nStandard error:\r\nsomething broke",
            ScriptNotes.OutputText(Exited(3, "one\r\ntwo\r\n", "something broke\r\n"), ".ps1"));
        Assert.Equal(
            "all good\r\n\r\nStandard error:\r\na warning",
            ScriptNotes.OutputText(Exited(0, "all good\r\n", "a warning\r\n"), ".ps1"));
    }

    [Fact]
    public void Standard_error_alone_is_shown_under_its_heading()
    {
        Assert.Equal("Standard error:\r\nsomething broke", ScriptNotes.OutputText(Exited(1, "", "something broke\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_printed_nothing_says_so()
    {
        Assert.Equal("The script printed nothing.", ScriptNotes.OutputText(Exited(0), ".ps1"));
        Assert.Equal("The script printed nothing.", ScriptNotes.OutputText(Exited(0, "\r\n\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_timed_out_shows_what_it_printed_until_then()
    {
        Assert.Equal("half way", ScriptNotes.OutputText(Ended(ScriptOutcome.TimedOut, "half way\r\n"), ".ps1"));
    }

    [Fact]
    public void A_script_that_did_not_run_shows_the_sentence_that_says_why()
    {
        foreach (var outcome in new[] { ScriptOutcome.NotStarted, ScriptOutcome.UnsafeArgument, ScriptOutcome.UnsafePath })
        {
            var result = Ended(outcome);

            Assert.Equal(ScriptNotes.Notification(result, ".cmd"), ScriptNotes.OutputText(result, ".cmd"));
            Assert.Equal(ScriptNotes.Notification(result, ".ps1"), ScriptNotes.OutputText(result, ".ps1"));
        }
    }

    [Fact]
    public void Output_from_a_program_that_writes_two_bytes_a_letter_is_shown_without_the_gaps()
    {
        Assert.Equal("wide", ScriptNotes.OutputText(Exited(0, "w\0i\0d\0e\0\r\0\n\0"), ".ps1"));
    }

    // The runner keeps up to about a megabyte of each output. All of it laid out at once in a
    // wrapping text box would hold the window up, so the view is given the newest part only.
    [Fact]
    public void The_most_the_output_view_shows_is_200000_characters()
    {
        Assert.Equal(200_000, ScriptNotes.MaxOutputChars);
    }

    [Fact]
    public void Output_that_fits_is_shown_as_it_is_even_when_it_has_the_most_that_is_shown()
    {
        string fits = new('x', ScriptNotes.MaxOutputChars);

        Assert.Equal(fits, ScriptNotes.OutputText(Exited(0, fits), ".ps1"));
        Assert.DoesNotContain("not shown", ScriptNotes.OutputText(Exited(0, "short\r\n"), ".ps1"));
    }

    [Fact]
    public void Output_of_half_a_million_characters_is_shown_from_its_newest_part_under_a_fixed_first_line()
    {
        string printed = new string('a', 499_990) + "0123456789";

        string shown = ScriptNotes.OutputText(Exited(0, printed), ".ps1");

        Assert.Equal("The start of the output is not shown.", ScriptNotes.OutputCut);
        Assert.StartsWith(ScriptNotes.OutputCut + "\r\n", shown);
        Assert.Equal(ScriptNotes.OutputCut.Length + 2 + ScriptNotes.MaxOutputChars, shown.Length);
        Assert.True(shown.Length <= ScriptNotes.MaxOutputChars + ScriptNotes.OutputCut.Length + 2);
        Assert.EndsWith(new string('a', 100) + "0123456789", shown);
    }

    [Fact]
    public void Output_one_character_too_long_loses_its_first_character_only()
    {
        string printed = "S" + new string('x', ScriptNotes.MaxOutputChars - 1) + "E";

        string shown = ScriptNotes.OutputText(Exited(0, printed), ".ps1");

        Assert.Equal(ScriptNotes.OutputCut + "\r\n" + printed[1..], shown);
    }

    // An emoji is two characters, and the text is cut by count. Cut between its halves, the text
    // would start with half of one, which is shown as a broken box.
    [Theory]
    [InlineData("", 200_000)] // The cut falls between two emoji: all of the newest 200000 are kept.
    [InlineData("z", 199_999)] // The cut falls between the halves of one: its second half is left out too.
    public void A_cut_never_starts_in_the_middle_of_a_two_part_character(string ending, int expectedKept)
    {
        string printed = string.Concat(Enumerable.Repeat("\U0001F600", 150_000)) + ending;

        string shown = ScriptNotes.OutputText(Exited(0, printed), ".ps1");

        string kept = shown[(ScriptNotes.OutputCut.Length + 2)..];
        Assert.Equal(expectedKept, kept.Length);
        Assert.StartsWith("\U0001F600", kept);
        Assert.EndsWith("\U0001F600" + ending, kept);

        // A half that stands alone is read as the replacement character.
        Assert.DoesNotContain(Rune.ReplacementChar, shown.EnumerateRunes());
    }

    [Fact]
    public void When_the_start_is_cut_the_newest_standard_error_is_still_shown_under_its_heading()
    {
        string shown = ScriptNotes.OutputText(Exited(1, new string('o', 300_000), "something broke\r\n"), ".ps1");

        Assert.StartsWith(ScriptNotes.OutputCut + "\r\n", shown);
        Assert.EndsWith("\r\n\r\nStandard error:\r\nsomething broke", shown);
        Assert.True(shown.Length <= ScriptNotes.MaxOutputChars + ScriptNotes.OutputCut.Length + 2);
    }

    [Fact]
    public void Standard_error_alone_that_is_too_long_is_cut_the_same_way()
    {
        string shown = ScriptNotes.OutputText(Ended(ScriptOutcome.TimedOut, "", new string('e', 250_000) + "last"), ".ps1");

        Assert.StartsWith(ScriptNotes.OutputCut + "\r\n", shown);
        Assert.EndsWith("eeelast", shown);
        Assert.True(shown.Length <= ScriptNotes.MaxOutputChars + ScriptNotes.OutputCut.Length + 2);
    }

    [Fact]
    public void The_notification_of_output_that_is_too_long_to_show_is_still_the_last_line_printed()
    {
        string printed = new string('a', 400_000) + "\r\nthe real last line\r\n";

        Assert.Equal("the real last line", ScriptNotes.Notification(Exited(0, printed), ".ps1"));
    }
}
