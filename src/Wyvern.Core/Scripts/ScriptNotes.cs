using System.Globalization;
using System.Text.RegularExpressions;
using Wyvern.Core.Platform;

namespace Wyvern.Core.Scripts;

/// <summary>
/// What the owner is told about a run of a script: one sentence for a notification, and a
/// status line and a text for the output view. A line the script printed can appear in them,
/// so they are for the screen and must never be logged. The script's title is not in them.
/// </summary>
public static partial class ScriptNotes
{
    /// <summary>The notification for a script that ended well and wrote nothing, to either output, that has anything on it.</summary>
    public const string Done = "Done";

    /// <summary>The output view's text for a script that ended and printed nothing at all.</summary>
    public const string NoOutput = "The script printed nothing.";

    /// <summary>Stands between what a script wrote to standard output and what it wrote to standard error.</summary>
    public const string ErrorHeading = "Standard error:";

    /// <summary>
    /// The longest line of a script's output that a notification shows. A notification holds
    /// about 250 characters, and the exit code goes in front of the line.
    /// </summary>
    public const int MaxLineLength = 200;

    /// <summary>The most characters of what a script printed that the output view shows: the newest ones.</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>The first line of the output view's text when the start of what the script printed is left out.</summary>
    public const string OutputCut = "The start of the output is not shown.";

    /// <summary>The sentence for an ending that has no words of its own. It says nothing about why.</summary>
    public const string UnknownEnding = "The script ended in a way Wyvern does not recognise.";

    /// <summary>The status line for an ending that has no words of its own.</summary>
    public const string UnknownStatus = "Ended";

    private const string NewLine = "\r\n";

    // Held once: a new array on every turn of the loop in LastLine would be garbage for nothing.
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>
    /// One sentence for a notification. A script that ended with exit code 0 gives the last
    /// line it wrote to standard output that has something on it; if there is none, the last
    /// such line of standard error; if there is none either, "Done". Standard error is looked at
    /// because most PowerShell errors do not end a script: one whose copy failed exits with 0,
    /// and "Done" would tell the owner that it worked. One that failed or ran out of time gives
    /// its exit code, or says that it timed out, and then the last line it wrote to standard
    /// error.
    /// </summary>
    /// <param name="extension">The script's extension in lower case with its dot. It is named when the program for it could not be started, and it decides which sentence a refused path gets.</param>
    public static string Notification(ScriptResult result, string extension) => result.Outcome switch
    {
        ScriptOutcome.Exited when result.ExitCode == 0 => LastLine(result.Output) ?? LastLine(result.Error) ?? Done,
        ScriptOutcome.Exited => WithLastLine(ExitCode(result) + ".", result.Error),
        ScriptOutcome.TimedOut => WithLastLine("Timed out and was stopped.", result.Error),
        ScriptOutcome.NotStarted => $"The program that runs {extension} scripts could not be started. Details are in the log file.",
        ScriptOutcome.UnsafeArgument =>
            "Nothing was run. A batch file cannot be given text that holds & | < > ^ % \" , ; = or a line break, or a space together with a backslash at the end, because cmd would act on them.",
        ScriptOutcome.UnsafePath when extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) =>
            "Nothing was run. PowerShell would misread a square bracket ([ or ]) or a backtick (`) in this script's name or folder. Rename the script or its folder.",
        ScriptOutcome.UnsafePath =>
            "Nothing was run. cmd would act on a character in this batch file's name or folder: one of ( % & ^ , ; =.",

        // Every outcome there is has its own arm above. This one is for an outcome added later
        // that has none yet, and it must not be taken for any of the others.
        _ => UnknownEnding,
    };

    /// <summary>A few words for the output view's status line, where "Running" stood while the script ran.</summary>
    public static string Status(ScriptResult result, string extension) => result.Outcome switch
    {
        ScriptOutcome.Exited when result.ExitCode == 0 => "Finished",
        ScriptOutcome.Exited => ExitCode(result),
        ScriptOutcome.TimedOut => "Timed out",
        ScriptOutcome.NotStarted => $"Could not start the program for {extension} scripts",
        ScriptOutcome.UnsafeArgument or ScriptOutcome.UnsafePath => "Not run",
        _ => UnknownStatus,
    };

    /// <summary>
    /// The text for the output view. For a script that ran: everything it wrote to standard
    /// output, then, when it wrote anything to standard error, a heading and all of that. When
    /// that is longer than <see cref="MaxOutputChars"/> only its newest part is kept, and the
    /// text begins with the line <see cref="OutputCut"/>. For a script that did not run: the
    /// sentence that says why.
    /// </summary>
    public static string OutputText(ScriptResult result, string extension)
    {
        if (result.Outcome is not (ScriptOutcome.Exited or ScriptOutcome.TimedOut))
        {
            return Notification(result, extension);
        }

        string output = Shown(result.Output);
        string error = Shown(result.Error);
        if (error.Length == 0)
        {
            return output.Length > 0 ? Newest(output) : NoOutput;
        }

        return Newest(output.Length > 0
            ? output + NewLine + NewLine + ErrorHeading + NewLine + error
            : ErrorHeading + NewLine + error);
    }

    /// <summary>
    /// The last line that has something on it, without colour codes and invisible characters,
    /// cut to <see cref="MaxLineLength"/>. Null when there is no such line.
    /// </summary>
    public static string? LastLine(string text)
    {
        int end = text.Length;
        while (end > 0)
        {
            int start = text.LastIndexOfAny(LineBreaks, end - 1) + 1;
            if (start < end)
            {
                // A tab would vanish with the other control characters and join the words beside it.
                // Colour codes go first, whole: with only their escape character taken out, "[32m"
                // would be left in the notification.
                string line = NoteText.WithoutInvisibleCharacters(WithoutColourCodes(text[start..end].Replace('\t', ' '))).Trim();
                if (line.Length > 0)
                {
                    return NoteText.Shorten(line, MaxLineLength);
                }
            }

            end = start - 1;
        }

        return null;
    }

    private static string ExitCode(ScriptResult result) =>
        "Exit code " + (result.ExitCode ?? 0).ToString(CultureInfo.InvariantCulture);

    private static string WithLastLine(string sentence, string text) =>
        LastLine(text) is { } line ? sentence + " " + line : sentence;

    // A program that writes UTF-16 puts a NUL after every letter when it is read as UTF-8, and a
    // text box stops at the first one. The line breaks at the very end are not worth showing.
    // A program that thinks it is writing to a terminal puts colour codes in, which a text box shows as letters.
    private static string Shown(string text) => WithoutColourCodes(text.Replace("\0", "")).TrimEnd('\r', '\n');

    // The newest part of the text that the view may show, under the line that says the start is missing.
    private static string Newest(string text)
    {
        if (text.Length <= MaxOutputChars)
        {
            return text;
        }

        int start = text.Length - MaxOutputChars;

        // A character outside the basic plane, such as an emoji, takes two positions. Cut
        // between them, what is left would start with the second half, which is not a
        // character by itself and is shown as a broken box.
        if (char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return string.Concat(OutputCut, NewLine, text.AsSpan(start));
    }

    private static string WithoutColourCodes(string text) => ColourCode().Replace(text, "");

    // A control sequence as terminals read it: the escape character, "[", parameter characters
    // (digits, ";", ":", "?" and the like), any intermediate characters, and one final letter
    // or symbol. "[32m" turns the text green; "[0m" turns it back; "[2K" clears a line.
    [GeneratedRegex(@"\u001B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex ColourCode();
}
