using System.Globalization;
using System.Text.Json;
using Beacon.Core.Library;
using Beacon.Core.Settings;

namespace Beacon.Core.Scripts;

/// <summary>
/// Which program runs each kind of script, and how long a script may run, worked out from
/// settings.json.
/// </summary>
public sealed class ScriptOptions
{
    /// <summary>Five minutes.</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>One day. A longer wait is not a timeout any more, and the timer has an upper limit of its own.</summary>
    public const int MaxTimeoutSeconds = 86_400;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _interpreters;

    private ScriptOptions(
        IReadOnlyDictionary<string, IReadOnlyList<string>> interpreters,
        TimeSpan timeout,
        IReadOnlyList<string> notes)
    {
        _interpreters = interpreters;
        Timeout = timeout;
        Notes = notes;
    }

    /// <summary>What applies when settings.json says nothing about scripts.</summary>
    public static ScriptOptions Default { get; } = From(new BeaconSettings());

    /// <summary>How long a script may run before it is stopped.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Sentences to show the owner about script settings that could not be used. They are made
    /// of fixed words and counts, and never quote the file.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// The owner's own choice of program for one kind of script: the program, then its
    /// arguments. Null when "interpreters" has no usable entry for it, in which case the
    /// built-in program is used.
    /// </summary>
    /// <param name="extension">A script's extension in lower case with its dot, such as ".py".</param>
    public IReadOnlyList<string>? Interpreter(string extension) => _interpreters.GetValueOrDefault(extension);

    /// <summary>
    /// A missing or null "interpreters" or "scriptTimeoutSeconds" key means the built-in
    /// programs and 300 seconds. In "interpreters" a key is an extension, written with or
    /// without its dot and in any case, and its value is the program followed by its
    /// arguments, as a list of text, or one text for a program with no arguments. An entry for
    /// an extension that is not a kind of script, an entry for .cmd or .bat whatever program it
    /// names, an entry given twice, an entry whose program is cmd, an entry whose program is
    /// PowerShell and that has any argument, a key that is not readable text, and a value that
    /// is anything else are left out and counted in <see cref="Notes"/>. An "interpreters"
    /// that is not an object at all counts as one such entry. A timeout is a JSON number whose
    /// value is whole and from 1 to 86400, in whatever form it is written ("60.0" is 60);
    /// anything else, text and fractions included, is replaced by 300 and noted too.
    /// </summary>
    /// <remarks>
    /// <para>No entry is taken for a batch file, so that batch files always run on Beacon's own
    /// line, "cmd /d /q /v:off /c @" with the cmd.exe of the Windows system folder (see
    /// <see cref="ScriptCommand.BuiltIn"/>): the rules for the text they may be given
    /// (<see cref="BatchText"/>) are the rules for that line and for no other.</para>
    /// <para>cmd is never taken from here for any other kind of script either, which would be
    /// run through cmd, and cmd would read the text typed for it as commands. An owner who
    /// needs a shim names the interpreter's own .exe file instead.</para>
    /// <para>An entry for PowerShell names the program and nothing else. Beacon's own arguments
    /// are always used with it (see <see cref="ScriptCommand"/>): PowerShell accepts any
    /// unambiguous prefix of a switch and several kinds of dash, so no list of switches to
    /// refuse is complete, and one that was missed ("-Command", "-c") makes it run the text
    /// typed for the script as code.</para>
    /// </remarks>
    public static ScriptOptions From(BeaconSettings settings)
    {
        var interpreters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        int unusable = 0;
        if (Written(settings.Interpreters) is { } entries)
        {
            if (entries.ValueKind != JsonValueKind.Object)
            {
                // A text, a list or a number where the object should be: there is no entry in
                // it, and the one value that is there cannot be used.
                unusable++;
            }
            else
            {
                foreach (var property in entries.EnumerateObject())
                {
                    if (TryGetName(property, out string key) && TryAdd(interpreters, key, property.Value))
                    {
                        continue;
                    }

                    unusable++;
                }
            }
        }

        var notes = new List<string>();
        if (unusable == 1)
        {
            notes.Add("One entry of \"interpreters\" in settings.json cannot be used and is ignored.");
        }
        else if (unusable > 1)
        {
            notes.Add($"{unusable} entries of \"interpreters\" in settings.json cannot be used and are ignored.");
        }

        int seconds = DefaultTimeoutSeconds;
        if (Written(settings.ScriptTimeoutSeconds) is { } timeout)
        {
            if (WholeSeconds(timeout) is { } given)
            {
                seconds = given;
            }
            else
            {
                notes.Add($"\"scriptTimeoutSeconds\" must be a whole number from 1 to {MaxTimeoutSeconds}. {DefaultTimeoutSeconds} is used instead.");
            }
        }

        return new ScriptOptions(interpreters, TimeSpan.FromSeconds(seconds), notes);
    }

    /// <returns>The value, or null when the key is missing or is written as null.</returns>
    private static JsonElement? Written(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } written ? written : null;

    /// <returns>True when the key is a kind of script and the value is a usable entry for it that no earlier entry has taken.</returns>
    private static bool TryAdd(Dictionary<string, IReadOnlyList<string>> interpreters, string key, JsonElement value)
    {
        string extension = key.Trim().ToLowerInvariant();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        // A batch file is only ever run on the built-in line, so an entry for one is never used,
        // whatever program it names.
        return ScriptFile.Extensions.Contains(extension)
            && extension is not (".cmd" or ".bat")
            && Command(value) is { } command
            && !IsCmd(command[0])
            && !(IsPowerShell(command[0]) && command.Count > 1)
            && interpreters.TryAdd(extension, command);
    }

    /// <returns>The number of seconds when the value is a JSON number that is whole and from 1 to <see cref="MaxTimeoutSeconds"/>, however it is written ("60", "60.0" and "6e1" are all 60). Otherwise null.</returns>
    private static int? WholeSeconds(JsonElement value)
    {
        // The number is read from the text it was written as, so that its form decides nothing
        // and a number too large for any type is simply out of range.
        if (value.ValueKind != JsonValueKind.Number
            || !decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal seconds)
            || seconds != decimal.Truncate(seconds)
            || seconds < 1
            || seconds > MaxTimeoutSeconds)
        {
            return null;
        }

        return (int)seconds;
    }

    private static bool TryGetName(JsonProperty property, out string name)
    {
        name = "";
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            // A key that holds half of a surrogate pair written as an escape ("\ud800") is valid
            // JSON, but it is not text that can be read. It is one more entry that cannot be used.
            return false;
        }
    }

    /// <returns>The program and its arguments, or null when the value is not one text or a list of text that starts with a program.</returns>
    private static IReadOnlyList<string>? Command(JsonElement value)
    {
        var command = new List<string>();
        foreach (var part in value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value])
        {
            if (!TryGetText(part, out string text))
            {
                return null;
            }

            command.Add(text);
        }

        // The first text is the program. Arguments may be anything, even empty.
        return command.Count > 0 && !string.IsNullOrWhiteSpace(command[0]) ? command : null;
    }

    // The file name without its extension is "cmd", in any case, wherever the file is: "cmd",
    // "CMD.exe" and "C:\Windows\System32\cmd.exe". Environment variables are expanded first, as
    // ProgramFinder expands them, so that "%ComSpec%" is not a way round.
    private static bool IsCmd(string program) => IsNamed(program, "cmd");

    /// <summary>
    /// True when the program is PowerShell, found the same way as cmd is: "powershell" or "pwsh"
    /// as the file name without its extension, in any case, wherever the file is.
    /// </summary>
    internal static bool IsPowerShell(string program) => IsNamed(program, "powershell") || IsNamed(program, "pwsh");

    private static bool IsNamed(string program, string name) =>
        Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(program.Trim()))
            .Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetText(JsonElement value, out string text)
    {
        text = "";
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            text = value.GetString() ?? "";
            return true;
        }
        catch (InvalidOperationException)
        {
            // Half of a surrogate pair written as an escape ("\ud800") is valid JSON, but it is
            // not text that can be read. It is one more value that cannot be used.
            return false;
        }
    }
}
