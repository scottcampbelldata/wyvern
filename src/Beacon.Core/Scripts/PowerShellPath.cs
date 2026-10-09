namespace Beacon.Core.Scripts;

/// <summary>
/// Decides whether a .ps1 file's path can be given to PowerShell without it being misread.
/// </summary>
/// <remarks>
/// PowerShell reads the path given to "-File" as a pattern as well as a name: a square bracket
/// starts or ends a set of characters, and a backtick escapes the character after it. A script
/// called "report[1].ps1" may then not be found, or "report1.ps1" beside it may be run in its
/// place. This is the PowerShell form of the round bracket that cmd reads in a batch file's
/// name (see <see cref="BatchText"/>). Nothing here has been tried against PowerShell itself,
/// so such a path is refused rather than quoted or escaped in some way that might not hold.
/// </remarks>
public static class PowerShellPath
{
    private static readonly char[] Unsafe = ['[', ']', '`'];

    /// <summary>
    /// True when the path holds a square bracket or a backtick, wherever it stands: in the
    /// file's name or in any folder above it, with a space in the path or without.
    /// </summary>
    public static bool IsUnsafe(string path) => path.AsSpan().IndexOfAny(Unsafe) >= 0;
}
