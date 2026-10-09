using System.Text.RegularExpressions;

namespace Beacon.Tests;

/// <summary>
/// Windows keeps the text of notifications in its own history, and the log is a file anyone can
/// open. So nothing in the app may read an exception's message or an item's title: both can hold
/// what the owner wrote, such as the name of a snippet or a quicklink. This reads the source
/// and fails when either appears as a member access.
/// </summary>
public class PrivacyRuleTests
{
    private static readonly Regex Forbidden = new(@"\.(Message|Title)\b");

    // Beacon.Tests cannot reference the WPF project, so the app's source is read from disk.
    // The test assembly sits in the test project folder under bin, so the repository is found
    // by going up until the folder that holds the solution file.
    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Beacon.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"Beacon.slnx was not found above {AppContext.BaseDirectory}");
        return folder.FullName;
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_app_never_reads_an_exceptions_message_or_an_items_title()
    {
        string root = RepositoryRoot();
        string appFolder = Path.Combine(root, "src", "Beacon.App");
        var files = Directory.EnumerateFiles(appFolder, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(Path.GetRelativePath(appFolder, file)))
            .ToList();

        // Proves the files are really being read, so the check below cannot pass on an empty list.
        Assert.Contains(files, file => Path.GetFileName(file) == "BeaconShell.cs");

        var offences = new List<string>();
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Forbidden.IsMatch(lines[i]))
                {
                    offences.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "The app reads an exception's message or an item's title, which must never reach the log or a notification. Look at: "
            + string.Join(", ", offences));
    }

    // The app is built against the Windows SDK, which lets it call every part of Windows, the
    // parts that go to the network among them. It uses three: the reader of text in pictures,
    // the pictures that reader takes, and the list of what is playing. Any other part has to
    // be added here by hand, which is the moment to ask what it does with the owner's data.
    [Fact]
    public void The_app_uses_only_these_parts_of_the_windows_runtime()
    {
        string root = RepositoryRoot();
        var mention = new Regex(@"\bWindows\.(ApplicationModel|Data|Devices|Foundation|Globalization|Graphics|Management|Media|Networking|Perception|Security|Services|Storage|System|UI|Web)(\.\w+)*");
        var used = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string project in new[] { "Beacon.App", "Beacon.Core" })
        {
            string folder = Path.Combine(root, "src", project);
            foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(Path.GetRelativePath(folder, file))))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    // "System.Windows.Media" is WPF, and a comment is not a use.
                    string code = line.Split("//")[0];
                    foreach (Match match in mention.Matches(code))
                    {
                        if (match.Index >= 7 && code.AsSpan(match.Index - 7, 7).SequenceEqual("System."))
                        {
                            continue;
                        }

                        if (!used.TryGetValue(match.Value, out var files))
                        {
                            used[match.Value] = files = new SortedSet<string>(StringComparer.Ordinal);
                        }

                        files.Add(project + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/'));
                    }
                }
            }
        }

        Assert.Equal(["Windows.Graphics.Imaging", "Windows.Media.Control", "Windows.Media.Ocr"], used.Keys);
        Assert.Equal(["Beacon.App/Interop/PictureTextReader.cs"], used["Windows.Graphics.Imaging"]);
        Assert.Equal(["Beacon.App/Interop/PictureTextReader.cs"], used["Windows.Media.Ocr"]);
        Assert.Equal(["Beacon.App/Interop/MediaControl.cs"], used["Windows.Media.Control"]);
    }
}
