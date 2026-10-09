namespace Beacon.Core.Library;

/// <summary>How the result of a script is shown.</summary>
public enum ScriptMode
{
    /// <summary>The launcher hides, and a notification shows the last line the script printed.</summary>
    Silent,

    /// <summary>The launcher stays open and shows everything the script printed.</summary>
    Output,
}

/// <summary>One file under the scripts folder: a command the launcher can run.</summary>
/// <param name="RelativePath">The file's path under the scripts folder, for example "docker\restart.ps1". It is what makes the item id.</param>
/// <param name="FilePath">The file's full path: what is run, and what Ctrl+Enter opens for editing.</param>
/// <param name="Title">What the launcher shows and matches.</param>
/// <param name="Mode">How the result is shown.</param>
/// <param name="ArgumentPrompt">
/// Set when the script takes an argument: the words shown in the empty input line while it is
/// typed. Null when the script takes none.
/// </param>
public sealed record Script(string RelativePath, string FilePath, string Title, ScriptMode Mode, string? ArgumentPrompt)
{
    /// <summary>Item ids are this prefix followed by the path under the scripts folder.</summary>
    public const string IdPrefix = "script:";

    public string Id => IdPrefix + RelativePath;

    /// <summary>The file's extension in lower case with its dot, such as ".ps1". It decides which program runs the script.</summary>
    public string Extension => Path.GetExtension(FilePath).ToLowerInvariant();
}
