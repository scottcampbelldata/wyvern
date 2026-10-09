using Wyvern.Core.Library;

namespace Wyvern.Core.Scripts;

/// <summary>
/// What is started to run a script.
/// </summary>
/// <remarks>
/// The owner's "interpreters" entry for a kind of script replaces the built-in program and its
/// arguments, except for batch files, and two rules keep that from turning the text typed for a
/// script into a command:
/// <list type="bullet">
/// <item>Batch files always run on the built-in line, "cmd /d /q /v:off /c @" with the cmd.exe of
/// the system folder, which the rules for the text they are given (<see cref="BatchText"/>)
/// are made for. <see cref="ScriptOptions"/> leaves out an entry for .cmd or .bat, whatever
/// program it names, so there is never one to use. Nor is cmd taken from an entry for any other
/// kind of script, which would be run through cmd, and cmd would read the text typed for it as
/// commands.</item>
/// <item>An entry for PowerShell chooses the program only. Wyvern's own arguments, which end in
/// "-File", are always used with it (<see cref="ScriptOptions"/> leaves out an entry for
/// PowerShell that has arguments of its own). PowerShell accepts any unambiguous prefix of a
/// switch and several kinds of dash, so "-Command", "-c" and even an argument it does not
/// know would each make it run the path and the typed text as code, and no list of switches
/// to refuse would be complete.</item>
/// </list>
/// </remarks>
/// <param name="Program">The full path of the program file.</param>
/// <param name="Arguments">
/// The program's own arguments, then the script's full path, then the text typed for the
/// script when it takes an argument. Each is a value of its own and is handed over as one;
/// they are never joined into a command line here.
/// </param>
public sealed record ScriptCommand(string Program, IReadOnlyList<string> Arguments)
{
    // No profile is loaded, nothing is asked, and the execution policy is set aside for this one
    // run. "-File" is last: PowerShell takes the next value as the script's path and everything
    // after it as that script's arguments, never as code.
    private static readonly string[] PowerShellArguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    /// <summary>
    /// The built-in program for each kind of script, followed by its arguments.
    /// <list type="bullet">
    /// <item>.ps1: PowerShell 7 ("pwsh") when it is installed, otherwise Windows PowerShell. No
    /// profile is loaded, nothing is asked, and the execution policy is set aside for this one
    /// run, since the script is the owner's own.</item>
    /// <item>.py: "python". .js: "node".</item>
    /// <item>.cmd and .bat: "cmd /d /q /v:off /c @", where cmd is cmd.exe in the system folder, by
    /// its full path, and never one found by name: the rules for the text a batch file may be
    /// given (<see cref="BatchText"/>) are the rules of the real cmd.exe, and a file of that
    /// name in some other folder of the PATH could be anything. "/d" keeps a command that the
    /// registry runs for every cmd from changing the folder or adding to the output. "/q" turns
    /// echo off. Inside a batch file echo is on until the script turns it off, so every
    /// command would be printed after a prompt that is the script's own folder, and a script
    /// that prints nothing else would have its folder, and the title in its header comment,
    /// announced in a notification as the last line it "printed". With "/q" a script prints only
    /// what it prints, and one that has only comments prints nothing; a script that wants
    /// echo can turn it on. "/v:off" turns delayed
    /// expansion off: a machine can have it switched on for every cmd, and then "!NAME!" in
    /// the text typed, or in the path, would be replaced by a variable's value. The "@" is there
    /// because cmd, when what follows "/c" starts with a quote, takes away the first quote and
    /// the last one of the whole line, which breaks a quoted path that is followed by a quoted
    /// argument. With "@" in front the line does not start with a quote, and all "@" does is
    /// keep the command from being echoed.</item>
    /// </list>
    /// </summary>
    /// <param name="extension">A script's extension in lower case with its dot.</param>
    /// <param name="find">Finds a program file by name, as <see cref="ProgramFinder.Find"/> does.</param>
    /// <returns>Null for an extension that is not a kind of script.</returns>
    public static IReadOnlyList<string>? BuiltIn(string extension, Func<string, string?> find) => extension switch
    {
        ".ps1" => [find("pwsh") is null ? "powershell" : "pwsh", .. PowerShellArguments],
        ".py" => ["python"],
        ".cmd" or ".bat" => [Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/q", "/v:off", "/c", "@"],
        ".js" => ["node"],
        _ => null,
    };

    /// <summary>
    /// Works out what to start for a script: the owner's entry in "interpreters" for its
    /// extension when there is one, otherwise the built-in program.
    /// </summary>
    /// <param name="argument">The text typed for the script, or null when it takes none. An empty text is still passed, as an empty argument.</param>
    /// <returns>Null when the program cannot be found.</returns>
    public static ScriptCommand? For(Script script, string? argument, ScriptOptions options, Func<string, string?> find)
    {
        var interpreter = options.Interpreter(script.Extension);
        if (interpreter is not null && ScriptOptions.IsPowerShell(interpreter[0]))
        {
            // Only the program is the owner's. Whatever else the entry holds is dropped here as
            // well, so that this does not depend on ScriptOptions having left such an entry out.
            interpreter = [interpreter[0], .. PowerShellArguments];
        }

        interpreter ??= BuiltIn(script.Extension, find);
        if (interpreter is null || find(interpreter[0]) is not { } program)
        {
            return null;
        }

        var arguments = new List<string>(interpreter.Skip(1)) { script.FilePath };
        if (argument is not null)
        {
            arguments.Add(argument);
        }

        return new ScriptCommand(program, arguments);
    }
}
