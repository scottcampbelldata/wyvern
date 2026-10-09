namespace Wyvern.Core.Scripts;

/// <summary>
/// Decides whether a .cmd or .bat file can be given a piece of text without cmd acting on it.
/// </summary>
/// <remarks>
/// Every other kind of script gets its path and its argument as separate values that the
/// program receives untouched. A batch file is different: cmd reads the whole command line
/// again by its own rules, and a character such as "&amp;" in it starts a second command.
/// There is no way of writing such a character that is safe in every position, so text that
/// holds one is refused before anything is run.
/// </remarks>
public static class BatchText
{
    // Acted on by cmd wherever they stand, inside quotes or not: a percent sign starts a
    // variable, a quote ends the quoting that protects the rest, a line break ends the command.
    private static readonly char[] UnsafeAnywhere = ['%', '"', '\r', '\n'];

    // Acted on by cmd outside quotes. The first five start, join or redirect commands. The last
    // three separate one argument from the next, so that "a=b" would arrive as "a" and "b".
    private static readonly char[] UnsafeOutsideQuotes = ['&', '|', '<', '>', '^', ',', ';', '='];

    // In the path of the batch file itself, which is the first word of the line, an opening
    // round bracket is acted on as well: cmd ends the file's name at it, so that "build(1).cmd"
    // starts "build.cmd" and hands it "(1).cmd". Windows names a copy of build.cmd exactly that.
    // In an argument it is an ordinary character. A closing bracket alone does nothing.
    private static readonly char[] UnsafeInPathOutsideQuotes = [.. UnsafeOutsideQuotes, '('];

    /// <summary>
    /// True when the argument cannot be given to a batch file exactly as it was typed. That is
    /// the case when it holds one of &amp; | &lt; &gt; ^ % " , ; = or a line break, because the
    /// text is put in quotes only when it holds a space, and whether it will be is not
    /// something to rely on, so these are refused wherever they stand. It is also the case when
    /// it holds white space and ends in a backslash: it is then put in quotes, the backslash at
    /// the end is doubled, and cmd does not undo that. A backslash at the end of text without
    /// white space arrives as typed.
    /// </summary>
    public static bool IsUnsafeArgument(string argument) =>
        argument.AsSpan().IndexOfAny(UnsafeAnywhere) >= 0
        || argument.AsSpan().IndexOfAny(UnsafeOutsideQuotes) >= 0
        || (argument.EndsWith('\\') && argument.Any(char.IsWhiteSpace));

    /// <summary>
    /// True when cmd would act on a character in the batch file's own path. A percent sign is
    /// unsafe always. The others, including an opening round bracket, are unsafe only in a path
    /// without a space: a path with a space is passed in quotes, where they are ordinary
    /// characters, and folders named with a comma, such as the ones OneDrive makes for an
    /// organisation, have spaces too.
    /// </summary>
    public static bool IsUnsafePath(string path) =>
        path.AsSpan().IndexOfAny(UnsafeAnywhere) >= 0
        || (!path.Any(char.IsWhiteSpace) && path.AsSpan().IndexOfAny(UnsafeInPathOutsideQuotes) >= 0);
}
