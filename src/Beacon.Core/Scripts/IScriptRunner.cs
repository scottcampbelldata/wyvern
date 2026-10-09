using Beacon.Core.Library;

namespace Beacon.Core.Scripts;

/// <summary>How a run of a script ended.</summary>
public enum ScriptOutcome
{
    /// <summary>The script ran to its end. <see cref="ScriptResult.ExitCode"/> says how it went.</summary>
    Exited,

    /// <summary>The script ran for longer than it may and was stopped, together with everything it had started.</summary>
    TimedOut,

    /// <summary>Nothing ran: the program that runs this kind of script was not found or could not be started.</summary>
    NotStarted,

    /// <summary>Nothing ran: the text typed cannot be given to a batch file safely.</summary>
    UnsafeArgument,

    /// <summary>Nothing ran: the script's own path cannot be given safely to the program that would run it, cmd for a batch file and PowerShell for a .ps1 file.</summary>
    UnsafePath,
}

/// <summary>What came of running a script. The texts are what the script printed: show them, never log them.</summary>
/// <param name="ExitCode">The script's exit code when it ran to its end, otherwise null.</param>
/// <param name="Output">What was written to standard output, or the newest part of it when there was more than is kept.</param>
/// <param name="Error">The same for standard error.</param>
/// <param name="Duration">How long the run took, from just before the program was started.</param>
public sealed record ScriptResult(ScriptOutcome Outcome, int? ExitCode, string Output, string Error, TimeSpan Duration);

/// <summary>Runs scripts. <see cref="ScriptRunner"/> is the real one; tests of the launcher use a fake.</summary>
public interface IScriptRunner
{
    /// <summary>
    /// Starts the script and returns at once, without waiting for it. Any number of scripts
    /// may be running at the same time.
    /// </summary>
    /// <param name="argument">The text typed for the script, or null when it takes none.</param>
    /// <param name="finished">
    /// Called once with the result: on a background thread when the script has ended, or on the
    /// caller's own thread, before this method returns, when the script was refused. It is not
    /// called for a script that was stopped because the runner was disposed.
    /// </param>
    void Start(Script script, string? argument, Action<ScriptResult> finished);
}
