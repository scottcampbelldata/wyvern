using Beacon.Core.Scripts;

namespace Beacon.Tests;

public class PowerShellPathTests
{
    [Theory]
    [InlineData(@"C:\Scripts\report[1].ps1")]
    [InlineData(@"C:\Scripts\report1].ps1")]
    [InlineData(@"C:\Scripts\[draft]\run.ps1")]
    [InlineData(@"C:\Scripts[old]\run.ps1")]
    [InlineData(@"C:\Scripts\a`b.ps1")]
    [InlineData(@"C:\Scripts\`run.ps1")]
    [InlineData(@"C:\My Scripts\report[1].ps1")]
    [InlineData(@"\\server\share\[team]\run.ps1")]
    public void A_path_with_a_square_bracket_or_a_backtick_is_unsafe_with_or_without_a_space(string path)
    {
        // PowerShell reads "[" and "]" in the path given to -File as a wildcard set, and a
        // backtick as an escape, so "report[1].ps1" may not be found, or "report1.ps1" is run.
        Assert.True(PowerShellPath.IsUnsafe(path));
    }

    [Theory]
    [InlineData(@"C:\Scripts\report.ps1")]
    [InlineData(@"C:\Program Files (x86)\Tools\build it.ps1")]
    [InlineData(@"\\server\share\scripts\run.ps1")]
    [InlineData(@"C:\Scripts\it's {final} #2 @home! $x & y; z=1, 100%.ps1")]
    [InlineData(@"C:\Scripts\build(1).ps1")]
    [InlineData(@"C:\Scripts\close)only.ps1")]
    public void An_ordinary_path_is_safe_whatever_else_it_holds(string path)
    {
        // The path is one value to -File and is never read as code, so only the three
        // characters above are a problem.
        Assert.False(PowerShellPath.IsUnsafe(path));
    }
}
