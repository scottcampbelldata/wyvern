using Wyvern.Core.Files;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public class FileIndexOptionsTests
{
    private static FileIndexOptions Options(List<string>? folders, List<string>? excluded = null) =>
        FileIndexOptions.From(new WyvernSettings { IndexedFolders = folders, ExcludedFolderNames = excluded });

    [Fact]
    public void Missing_keys_mean_the_default_folders_and_excluded_names()
    {
        var options = Options(folders: null);

        foreach (string folder in FileIndexOptions.DefaultFolders())
        {
            Assert.Contains(Path.TrimEndingDirectorySeparator(folder), options.Folders);
        }

        Assert.Equal(6, options.ExcludedNames.Count);
        Assert.Contains("node_modules", options.ExcludedNames);
        Assert.Contains(".GIT", options.ExcludedNames);
        Assert.Empty(options.Rejected);
    }

    [Fact]
    public void Folders_are_written_as_full_paths_without_a_trailing_separator()
    {
        var options = Options([@"C:\Work\", @"C:\Work\..\Other", @"  D:\Photos  "]);

        Assert.Equal([@"C:\Work", @"C:\Other", @"D:\Photos"], options.Folders);
    }

    [Fact]
    public void A_folder_listed_twice_is_kept_once_whatever_its_case()
    {
        var options = Options([@"C:\Work", @"c:\work\"]);

        Assert.Equal([@"C:\Work"], options.Folders);
    }

    [Fact]
    public void A_folder_inside_another_listed_folder_is_dropped()
    {
        var options = Options([@"C:\Work\Sub", @"C:\Work", @"C:\Workshop"]);

        Assert.Equal([@"C:\Work", @"C:\Workshop"], options.Folders);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;

        var options = Options([@"%SystemRoot%\Temp"]);

        Assert.Equal([Path.Combine(systemRoot, "Temp")], options.Folders);
    }

    [Fact]
    public void Entries_that_are_not_full_paths_are_rejected_and_blank_ones_ignored()
    {
        var options = Options([@"relative\path", "", "   ", @"C:\ok", "notes"]);

        Assert.Equal([@"C:\ok"], options.Folders);
        Assert.Equal([@"relative\path", "notes"], options.Rejected);
    }

    [Fact]
    public void A_variable_that_does_not_exist_is_rejected_rather_than_taken_as_a_folder_name()
    {
        // After expansion "%WYVERN_NO_SUCH_VARIABLE%" is still there, so it would otherwise be
        // accepted as a folder literally named that.
        string written = @"D:\%WYVERN_NO_SUCH_VARIABLE%\data";

        var options = Options([written, @"C:\ok"]);

        Assert.Equal([@"C:\ok"], options.Folders);
        Assert.Equal([written], options.Rejected);
    }

    [Fact]
    public void A_single_percent_sign_is_an_ordinary_character_in_a_folder_name()
    {
        var options = Options([@"D:\50% done"]);

        Assert.Equal([@"D:\50% done"], options.Folders);
        Assert.Empty(options.Rejected);
    }

    [Fact]
    public void Excluded_names_are_trimmed_and_compared_without_regard_to_case()
    {
        var options = Options([@"C:\Work"], [" target ", "", ".Cache"]);

        Assert.Equal(2, options.ExcludedNames.Count);
        Assert.Contains("TARGET", options.ExcludedNames);
        Assert.Contains(".cache", options.ExcludedNames);
        Assert.DoesNotContain("node_modules", options.ExcludedNames);
    }

    [Fact]
    public void An_empty_excluded_list_excludes_nothing()
    {
        var options = Options([@"C:\Work"], []);

        Assert.Empty(options.ExcludedNames);
        Assert.False(options.IsExcluded(@"C:\Work", @"C:\Work\node_modules\x.js", isDirectory: false));
    }

    [Theory]
    [InlineData(@"C:\Work\a.txt", @"C:\Work")]
    [InlineData(@"c:\work\deep\er\a.txt", @"C:\Work")]
    [InlineData(@"C:\Workshop\a.txt", @"C:\Workshop")]
    [InlineData(@"C:\Elsewhere\a.txt", null)]
    [InlineData(@"C:\Wor", null)]
    public void FolderOf_finds_the_indexed_folder_that_contains_a_path(string path, string? expected)
    {
        var options = Options([@"C:\Work", @"C:\Workshop"]);

        Assert.Equal(expected, options.FolderOf(path));
    }

    [Theory]
    [InlineData(@"C:\Work\node_modules\lib\index.js", false, true)]
    [InlineData(@"C:\Work\src\NODE_MODULES", true, true)]
    [InlineData(@"C:\Work\src\bin\Debug\app.exe", false, true)]
    [InlineData(@"C:\Work\src\app.cs", false, false)]
    [InlineData(@"C:\Work\src", true, false)]
    [InlineData(@"C:\Work\notes\bin", false, false)]
    public void IsExcluded_looks_at_folder_names_only(string path, bool isDirectory, bool expected)
    {
        var options = Options([@"C:\Work"]);

        Assert.Equal(expected, options.IsExcluded(@"C:\Work", path, isDirectory));
    }
}
