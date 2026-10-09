using Beacon.Core.Library;
using Beacon.Core.Scripts;

namespace Beacon.Tests;

/// <summary>Records which scripts it was asked to run instead of running anything. A test says when and how each one ends.</summary>
public sealed class FakeScriptRunner : IScriptRunner
{
    /// <summary>One entry per call to <see cref="Start"/>, oldest first.</summary>
    public List<(Script Script, string? Argument, Action<ScriptResult> Finished)> Started { get; } = [];

    /// <summary>When set, <see cref="Start"/> hands this result over before it returns, as the real runner does for a script it refuses.</summary>
    public ScriptResult? RefuseWith { get; set; }

    /// <summary>When set, <see cref="Start"/> throws it and starts nothing.</summary>
    public Exception? Throws { get; set; }

    public void Start(Script script, string? argument, Action<ScriptResult> finished)
    {
        if (Throws is not null)
        {
            throw Throws;
        }

        Started.Add((script, argument, finished));
        if (RefuseWith is not null)
        {
            finished(RefuseWith);
        }
    }

    /// <summary>Says the script started last has ended, the way the real runner does: by calling back.</summary>
    public void Finish(ScriptResult result) => Started[^1].Finished(result);

    /// <summary>The same for the script started at this position.</summary>
    public void Finish(int index, ScriptResult result) => Started[index].Finished(result);
}
