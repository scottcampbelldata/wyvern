using Wyvern.Core.Settings;

namespace Wyvern.Core.Scripts;

/// <summary>
/// Finds the program file for a name such as "python" or "pwsh", so that a script is always
/// started by a full path.
/// </summary>
/// <remarks>
/// Windows, given a bare name, looks first in the folder of the program that asked and in the
/// folder that program was started from. A file called python.exe lying in either of those
/// would then be run in place of the real one. Only the folders of the PATH are looked in here.
/// </remarks>
public static class ProgramFinder
{
    private const string ProgramExtension = ".exe";

    /// <summary>
    /// The program file is always an .exe: a name that does not end in ".exe" gets it added, so
    /// "python" and "python3.12" both work, and a .cmd or .bat file is never taken for a
    /// program. (Windows would hand such a file to cmd, which reads its arguments by rules of
    /// its own.) A full path is used as it is when the file exists; environment variables in
    /// it, such as %LOCALAPPDATA%, are expanded. A bare name is looked for in the folders of
    /// the PATH, in their order. A relative path is not used at all, because it would depend on
    /// where Wyvern happened to be started from.
    /// </summary>
    /// <param name="program">The program as written in "interpreters", or a built-in name.</param>
    /// <param name="path">The PATH to look in. Left out everywhere except in tests: it is then the PATH of this process.</param>
    /// <returns>The full path of the program file, or null when there is none.</returns>
    public static string? Find(string program, string? path = null)
    {
        string name = Environment.ExpandEnvironmentVariables(program.Trim());
        if (name.Length == 0 || ExpandedPath.HasUnexpandedVariable(name))
        {
            return null;
        }

        try
        {
            if (!name.EndsWith(ProgramExtension, StringComparison.OrdinalIgnoreCase))
            {
                name += ProgramExtension;
            }

            if (Path.IsPathFullyQualified(name))
            {
                return File.Exists(name) ? Path.GetFullPath(name) : null;
            }

            if (name.AsSpan().IndexOfAny('\\', '/', ':') >= 0)
            {
                return null;
            }

            path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // An entry of the PATH may be written in quotes, and Windows then reads a
                // semicolon inside them as part of the folder's name. The split above cannot tell,
                // and cuts such an entry in two, each half with a quote left open. Neither half is
                // a folder of the PATH, so they are not looked in.
                if (entry.Count(c => c == '"') % 2 != 0)
                {
                    continue;
                }

                string folder = entry.Trim('"');

                // A relative folder would be taken from wherever Wyvern happened to be started.
                if (!Path.IsPathFullyQualified(folder))
                {
                    continue;
                }

                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // Text that is not a usable file name, in the setting or in the PATH. There is no such program.
            return null;
        }
    }
}
