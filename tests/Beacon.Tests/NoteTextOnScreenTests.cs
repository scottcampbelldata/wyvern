using System.Text;
using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// The one cleaner of a line of text that came from outside and is shown on screen. Every
/// unusual character is a number, never the character itself.
/// </summary>
public class NoteTextOnScreenTests
{
    private const char Ellipsis = (char)0x2026;

    private static string Cp(int code) => char.ConvertFromUtf32(code);

    // Each of these is white space: a control character, a separator of lines or of
    // paragraphs, or a space of another width.
    public static IEnumerable<object[]> WhiteSpace() =>
        new[] { 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0x85, 0xA0, 0x2003, 0x2028, 0x2029, 0x3000 }.Select(code => new object[] { code });

    // A control character that is not white space, and format characters.
    public static IEnumerable<object[]> ControlAndFormat() =>
        new[] { 0x01, 0x07, 0x1B, 0x7F, 0x9B, 0xAD, 0x200B, 0x200E, 0x202E, 0x2060, 0xFEFF, 0xE0001, 0xE0041 }.Select(code => new object[] { code });

    // Not assigned to any character, or for private use.
    public static IEnumerable<object[]> UnassignedAndPrivateUse() =>
        new[] { 0x2065, 0xFFF0, 0xE000, 0xF8FF, 0xE0080, 0xE01F0, 0xE0FFF, 0xF0000, 0x10FFFD }.Select(code => new object[] { code });

    [Theory]
    [MemberData(nameof(WhiteSpace))]
    public void Any_white_space_between_two_words_is_one_space(int code)
    {
        Assert.Equal("Name Amount", NoteText.OnScreen("Name" + Cp(code) + "Amount"));
        Assert.Equal("Name Amount", NoteText.OnScreen("Name" + Cp(code) + Cp(code) + " " + Cp(code) + "Amount"));
    }

    [Fact]
    public void White_space_at_the_ends_is_dropped()
    {
        Assert.Equal("Walrus facts", NoteText.OnScreen("\t" + Cp(0xA0) + "Walrus facts\t" + Cp(0x2028) + "  "));
    }

    [Fact]
    public void Text_of_nothing_but_white_space_is_empty()
    {
        Assert.Equal("", NoteText.OnScreen(" \t\r\n" + Cp(0xA0) + Cp(0x3000)));
        Assert.Equal("", NoteText.OnScreen(""));
    }

    [Theory]
    [MemberData(nameof(ControlAndFormat))]
    public void A_control_or_format_character_is_left_out_whole_and_the_text_around_it_runs_together(int code)
    {
        Assert.Equal("Example", NoteText.OnScreen("Exam" + Cp(code) + "ple"));
        Assert.Equal("", NoteText.OnScreen(Cp(code)));
    }

    [Theory]
    [MemberData(nameof(ClipLinksTests.InvisibleButNotFormat), MemberType = typeof(ClipLinksTests))]
    public void A_character_that_shows_nothing_and_is_neither_control_nor_format_is_left_out_too(int code)
    {
        Assert.Equal("Example", NoteText.OnScreen("Exam" + Cp(code) + "ple"));
        Assert.Equal("", NoteText.OnScreen(Cp(code)));
        Assert.True(NoteText.IsInvisible(new Rune(code)));
    }

    [Theory]
    [MemberData(nameof(UnassignedAndPrivateUse))]
    public void A_character_that_is_not_assigned_or_is_for_private_use_is_left_out_too(int code)
    {
        Assert.Equal("Example", NoteText.OnScreen("Exam" + Cp(code) + "ple"));
        Assert.Equal("", NoteText.OnScreen(Cp(code)));
        Assert.True(NoteText.IsInvisible(new Rune(code)));
    }

    [Fact]
    public void A_character_that_shows_nothing_between_spaces_does_not_leave_two_spaces()
    {
        Assert.Equal("Name Amount", NoteText.OnScreen("Name " + Cp(0x200B) + " " + Cp(0x3164) + " " + Cp(0xE000) + " Amount"));
    }

    // Characters beside the ones that show nothing, which do show something.
    [Theory]
    [InlineData(0x180A)]
    [InlineData(0x2801)]
    [InlineData(0x3165)]
    [InlineData(0xFFA1)]
    [InlineData(0xFE10)]
    [InlineData(0xE9)]
    [InlineData(0x1F600)]
    public void The_characters_next_to_the_ones_that_show_nothing_stay(int code)
    {
        Assert.Equal("a" + Cp(code) + "b", NoteText.OnScreen("a" + Cp(code) + "b"));
        Assert.False(NoteText.IsInvisible(new Rune(code)));
    }

    [Fact]
    public void Text_that_is_no_longer_than_the_limit_is_kept_whole()
    {
        string text = new('w', 80);

        Assert.Equal(text, NoteText.OnScreen(text, 80));
    }

    [Fact]
    public void Longer_text_is_cut_to_the_limit_and_ends_in_an_ellipsis()
    {
        Assert.Equal(new string('w', 79) + Ellipsis, NoteText.OnScreen(new string('w', 81), 80));
    }

    [Fact]
    public void A_character_that_takes_two_positions_is_never_cut_in_two()
    {
        // Only the first half of the emoji would fit before the ellipsis.
        string text = new string('w', 78) + Cp(0x1F600) + "tail";

        Assert.Equal(new string('w', 78) + Ellipsis, NoteText.OnScreen(text, 80));
    }

    [Fact]
    public void The_limit_counts_the_text_that_is_left_and_not_the_text_that_was_given()
    {
        // Eighty-one characters as given, half of them spaces and characters that show nothing.
        string text = string.Concat(Enumerable.Repeat("w" + Cp(0x200B) + " ", 27));

        Assert.Equal(string.Join(' ', Enumerable.Repeat("w", 27)), NoteText.OnScreen(text, 80));
    }

    // Text far longer than the limit is cut in the same place whether or not all of it is looked at.
    [Fact]
    public void A_text_of_a_million_characters_gives_the_same_start_as_a_short_one()
    {
        string unit = "word" + Cp(0x200B) + "\t" + Cp(0xE000) + " ";
        string huge = string.Concat(Enumerable.Repeat(unit, 200_000));
        string expected = Expected(string.Concat(Enumerable.Repeat(unit, 100)), 80);

        Assert.Equal(expected, NoteText.OnScreen(huge, 80));
        Assert.Equal(80, NoteText.OnScreen(huge, 80).Length);
        Assert.EndsWith(Ellipsis.ToString(), NoteText.OnScreen(huge, 80));
    }

    // The slow way, for comparison: clean everything, then cut.
    private static string Expected(string text, int maxLength)
    {
        var line = new StringBuilder();
        bool spaceWaits = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                spaceWaits = true;
            }
            else if (!NoteText.IsInvisible(rune))
            {
                if (spaceWaits && line.Length > 0)
                {
                    line.Append(' ');
                }

                spaceWaits = false;
                line.Append(rune);
            }
        }

        return NoteText.Shorten(line.ToString(), maxLength);
    }

    [Fact]
    public void The_text_without_a_limit_is_cleaned_and_not_cut()
    {
        string text = new string('w', 5000) + Cp(0x200B) + " " + new string('v', 5000);

        Assert.Equal(new string('w', 5000) + " " + new string('v', 5000), NoteText.OnScreen(text));
    }

    [Fact]
    public void Half_of_a_pair_with_no_partner_is_one_replacement_character_and_never_throws()
    {
        string text = "a" + (char)0xD83D + "b";

        Assert.Equal("a" + (char)0xFFFD + "b", NoteText.OnScreen(text));
    }

    [Fact]
    public void The_older_cleaner_still_takes_out_control_and_format_characters_only()
    {
        // Other code (script output, library names) is cleaned by it and is not changed.
        Assert.Equal("a" + Cp(0xE000) + Cp(0x3164) + "b", NoteText.WithoutInvisibleCharacters("a" + Cp(0xE000) + Cp(0x3164) + Cp(0x200B) + "b"));
    }
}
