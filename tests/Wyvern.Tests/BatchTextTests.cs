using Wyvern.Core.Scripts;

namespace Wyvern.Tests;

public class BatchTextTests
{
    [Theory]
    [InlineData("tom & jerry")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("100%")]
    [InlineData("%PATH%")]
    [InlineData("say \"hello\"")]
    [InlineData("\"")]
    [InlineData("one\ntwo")]
    [InlineData("one\rtwo")]
    [InlineData("one\r\ntwo")]
    [InlineData("&")]
    [InlineData("ends with &")]
    public void An_argument_that_cmd_would_act_on_is_unsafe(string argument)
    {
        Assert.True(BatchText.IsUnsafeArgument(argument));
    }

    [Theory]
    [InlineData("a=b")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData("name=value with a space")]
    public void An_argument_that_cmd_would_split_in_two_is_unsafe_too(string argument)
    {
        Assert.True(BatchText.IsUnsafeArgument(argument));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("tom jerry")]
    [InlineData("  spaces all round  ")]
    [InlineData(@"C:\Users\Sam\My Documents\report (final).txt")]
    [InlineData("/? -x --flag:value")]
    [InlineData("it's 5 o'clock!")]
    [InlineData("naïve café 日本")]
    [InlineData("a\tb")]
    [InlineData("(brackets) [and] {braces} ~ ` # $ @ + ' * ?")]
    public void Ordinary_text_is_safe(string argument)
    {
        Assert.False(BatchText.IsUnsafeArgument(argument));
    }

    [Theory]
    [InlineData(@"C:\Scripts\50%\build.cmd")]
    [InlineData(@"C:\My Scripts\%TEMP%\build.cmd")]
    public void A_percent_sign_in_the_path_of_a_batch_file_is_unsafe_with_or_without_a_space(string path)
    {
        Assert.True(BatchText.IsUnsafePath(path));
    }

    [Theory]
    [InlineData(@"C:\R&D\build.cmd")]
    [InlineData(@"C:\Scripts\a^b.cmd")]
    [InlineData(@"C:\Scripts\a,b.cmd")]
    [InlineData(@"C:\Scripts\a;b.cmd")]
    [InlineData(@"C:\Scripts\a=b.cmd")]
    public void A_path_without_a_space_is_unsafe_when_it_holds_a_character_cmd_acts_on_outside_quotes(string path)
    {
        Assert.True(BatchText.IsUnsafePath(path));
    }

    [Theory]
    [InlineData(@"C:\Scripts\build(1).cmd")]
    [InlineData(@"C:\Scripts\tools(old)\run.cmd")]
    [InlineData(@"C:\Scripts\(draft).cmd")]
    public void A_round_bracket_in_a_path_without_a_space_is_unsafe_because_cmd_ends_the_name_at_it(string path)
    {
        // Left as it is, "build(1).cmd" would start "build.cmd" and give it "(1).cmd" as an
        // argument, and Windows gives a copy of build.cmd exactly that name.
        Assert.True(BatchText.IsUnsafePath(path));
    }

    [Theory]
    [InlineData(@"C:\R&D team\build.cmd")]
    [InlineData(@"C:\Users\Sam\OneDrive - Contoso, Inc\Wyvern\library\scripts\build.cmd")]
    [InlineData(@"C:\My Scripts\a^b;c=d.cmd")]
    [InlineData(@"C:\My Scripts\build(1).cmd")]
    [InlineData(@"C:\My Scripts\tools(old)\run.cmd")]
    public void The_same_characters_are_safe_in_a_path_with_a_space_because_it_is_passed_in_quotes(string path)
    {
        Assert.False(BatchText.IsUnsafePath(path));
    }

    [Theory]
    [InlineData(@"C:\Scripts\build.cmd")]
    [InlineData(@"C:\Program Files (x86)\Tools\build it.bat")]
    [InlineData(@"\\server\share\scripts\build.cmd")]
    [InlineData(@"C:\Scripts\it's (final) #2 @home!.cmd")]
    [InlineData(@"C:\Scripts\close)only.cmd")]
    public void An_ordinary_path_is_safe(string path)
    {
        Assert.False(BatchText.IsUnsafePath(path));
    }

    [Theory]
    [InlineData(@"C:\My Folder\")]
    [InlineData(@"a b\")]
    [InlineData("tab\there\\")]
    [InlineData(@"\\server\share\My Folder\")]
    public void An_argument_with_white_space_that_ends_in_a_backslash_is_unsafe_because_it_would_arrive_changed(string argument)
    {
        // Put in quotes because of the space, the backslash at the end is doubled and cmd does
        // not undo that: the script would receive "C:\My Folder\\".
        Assert.True(BatchText.IsUnsafeArgument(argument));
    }

    [Theory]
    [InlineData(@"C:\Folder\")]
    [InlineData(@"\")]
    [InlineData(@"C:\My Folder\file.txt")]
    [InlineData(@"a \b c")]
    [InlineData("a\\ ")]
    public void A_backslash_is_safe_without_white_space_and_when_it_is_not_the_last_character(string argument)
    {
        Assert.False(BatchText.IsUnsafeArgument(argument));
    }
}
