using System.Diagnostics;
using System.Text;

namespace Wyvern.Tests;

/// <summary>
/// A throwaway Wyvern root with a data folder in it, for the tests of sync: every file a test
/// makes is in here, under the machine's temporary folder, and all of it goes when the test
/// ends. Paths are given the way sync writes them, relative to the data folder and with
/// forward slashes. Every link the sandbox makes and every right it takes away is inside its
/// root (a path outside it is refused), and is undone before the sandbox is deleted, whether
/// the test passed or not.
/// </summary>
public sealed class SyncSandbox : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<string> _links = [];
    private readonly List<string> _denied = [];

    public SyncSandbox()
    {
        Directory.CreateDirectory(Data);
    }

    /// <summary>The Wyvern root: where sync.json, key.bin and sync-state.json would be.</summary>
    public string Root => _dir.Path;

    /// <summary>The data folder.</summary>
    public string Data => Path.Combine(_dir.Path, "data");

    /// <summary>A file in the Wyvern root.</summary>
    public string RootFile(string name) => Path.Combine(Root, name);

    /// <summary>The full name of the file a path stands for.</summary>
    public string Full(string path) => Path.Combine(Data, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Writes a text file in the data folder, with its folders, and gives it a last-write time
    /// when one is given, and a creation time when one is given.
    /// </summary>
    public void Write(string path, string text, DateTimeOffset? mtime = null, DateTimeOffset? created = null) =>
        WriteBytes(path, Encoding.UTF8.GetBytes(text), mtime, created);

    public void WriteBytes(string path, byte[] bytes, DateTimeOffset? mtime = null, DateTimeOffset? created = null)
    {
        string full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        if (created is { } born)
        {
            File.SetCreationTimeUtc(full, born.UtcDateTime);
        }

        if (mtime is { } time)
        {
            File.SetLastWriteTimeUtc(full, time.UtcDateTime);
        }
    }

    /// <summary>The text of a file in the data folder, or null when it is not there.</summary>
    public string? Read(string path) => File.Exists(Full(path)) ? File.ReadAllText(Full(path)) : null;

    public bool Exists(string path) => File.Exists(Full(path));

    public void Delete(string path) => File.Delete(Full(path));

    /// <summary>When a file in the data folder was last written.</summary>
    public DateTimeOffset Mtime(string path) => new(File.GetLastWriteTimeUtc(Full(path)));

    /// <summary>Every file under the data folder, as paths with forward slashes, sorted.</summary>
    public IReadOnlyList<string> Files() =>
        [.. Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Data, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Makes a folder that is a junction to another folder, the way <c>mklink /J</c> does, which
    /// needs no special right. It is taken away again before the sandbox is deleted, so that
    /// deleting never goes through it. Both the link and the target must be inside the
    /// sandbox's root: nothing else is ever linked to.
    /// </summary>
    public void Junction(string link, string target)
    {
        RequireInside(link);
        RequireInside(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(target);
        Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"");
        _links.Add(link);

        // The rule the code goes by. If this is null for a junction, a test of links proves nothing.
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    /// <summary>
    /// Takes the right to list a folder away from everybody, which needs no special right
    /// either (<c>icacls /deny</c>): the folder can then be seen but not listed. The folder
    /// must be inside the sandbox's root. A test gives it back with <see cref="Allow"/> in a
    /// <c>finally</c>; the sandbox gives back whatever is still denied before it is deleted.
    /// </summary>
    public void DenyListing(string folder) => Deny(folder, "RD");

    /// <summary>Gives back what <see cref="DenyListing"/> took from a folder.</summary>
    public void Allow(string path)
    {
        RequireInside(path);
        Run(Path.Combine(Environment.SystemDirectory, "icacls.exe"), $"\"{path}\" /remove:d *S-1-1-0");
        _denied.Remove(path);
    }

    public void Dispose()
    {
        // What is denied first, the outer folders before what is in them: a folder that cannot
        // be listed cannot be deleted.
        foreach (string path in _denied.OrderBy(path => path.Length).ToList())
        {
            try
            {
                Allow(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Xunit.Sdk.XunitException)
            {
                // Nothing more can be done about it here; deleting the sandbox says so.
            }
        }

        foreach (string link in _links)
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }

        _dir.Dispose();
    }

    private void Deny(string path, string right)
    {
        RequireInside(path);
        _denied.Add(path);
        Run(Path.Combine(Environment.SystemDirectory, "icacls.exe"), $"\"{path}\" /deny *S-1-1-0:({right})");
    }

    // A path that is not strictly inside the sandbox's root is never linked to, nor denied.
    private void RequireInside(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The sandbox only links to, and takes rights from, what is inside its own root.", nameof(path));
        }
    }

    private static void Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
