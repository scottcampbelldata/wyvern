using System.Diagnostics;
using Beacon.Core.Files;

namespace Beacon.Tests;

public sealed class FileScannerTests : IDisposable
{
    private static readonly IReadOnlySet<string> NoExclusions = new HashSet<string>();

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Root => _dir.Path;

    private string Touch(params string[] relative)
    {
        string path = Path.Combine([Root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static string[] Paths(IEnumerable<FileEntry> entries) =>
        entries.Select(entry => entry.FullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public void Lists_files_and_folders_at_every_depth_but_not_the_folder_itself()
    {
        Touch("top.txt");
        Touch("docs", "report.docx");
        Touch("docs", "old", "archive.zip");

        var entries = FileScanner.Scan(Root, NoExclusions, CancellationToken.None);

        Assert.Equal(
            [
                Path.Combine(Root, "docs"),
                Path.Combine(Root, "docs", "old"),
                Path.Combine(Root, "docs", "old", "archive.zip"),
                Path.Combine(Root, "docs", "report.docx"),
                Path.Combine(Root, "top.txt"),
            ],
            Paths(entries));
        Assert.Contains(new FileEntry(Root, "docs", IsDirectory: true), entries);
        Assert.Contains(new FileEntry(Path.Combine(Root, "docs"), "report.docx", IsDirectory: false), entries);
    }

    [Fact]
    public void Excluded_folder_names_are_left_out_with_everything_inside_them()
    {
        Touch("src", "app.cs");
        Touch("src", "bin", "Debug", "app.exe");
        Touch("NODE_MODULES", "lib", "index.js");
        Touch("notes", "bin"); // a file that happens to be called "bin" is kept

        var entries = FileScanner.Scan(Root, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "node_modules" }, CancellationToken.None);

        Assert.Equal(
            [
                Path.Combine(Root, "notes"),
                Path.Combine(Root, "notes", "bin"),
                Path.Combine(Root, "src"),
                Path.Combine(Root, "src", "app.cs"),
            ],
            Paths(entries));
    }

    [Fact]
    public void Hidden_files_and_folders_are_left_out()
    {
        Touch("visible.txt");
        string hiddenFile = Touch("secret.txt");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        Touch("cache", "inside.txt");
        new DirectoryInfo(Path.Combine(Root, "cache")).Attributes |= FileAttributes.Hidden;

        var entries = FileScanner.Scan(Root, NoExclusions, CancellationToken.None);

        Assert.Equal([Path.Combine(Root, "visible.txt")], Paths(entries));
    }

    [Fact]
    public void A_link_back_up_the_tree_is_listed_but_never_entered()
    {
        Touch("real", "inside.txt");
        string link = Path.Combine(Root, "real", "loop");
        CreateJunction(link, Root);
        try
        {
            var entries = FileScanner.Scan(Root, NoExclusions, CancellationToken.None);

            Assert.Equal(
                [
                    Path.Combine(Root, "real"),
                    Path.Combine(Root, "real", "inside.txt"),
                    link,
                ],
                Paths(entries));
        }
        finally
        {
            Directory.Delete(link); // removes the link only, so cleanup cannot follow it
        }
    }

    [Fact]
    public void A_folder_that_does_not_exist_gives_an_empty_list()
    {
        Assert.Empty(FileScanner.Scan(Path.Combine(Root, "missing"), NoExclusions, CancellationToken.None));
    }

    [Fact]
    public void A_cancelled_scan_throws()
    {
        Touch("a.txt");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => FileScanner.Scan(Root, NoExclusions, cancelled.Token));
    }

    // A junction needs no special privilege, unlike a symbolic link.
    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
