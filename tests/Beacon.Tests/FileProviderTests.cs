using Beacon.Core.Diagnostics;
using Beacon.Core.Files;
using Beacon.Core.Platform;
using Beacon.Core.Search;

namespace Beacon.Tests;

public class FileProviderTests
{
    private const string Docs = @"C:\Users\Sam\Documents";

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<string> Calls { get; } = [];

        public void LaunchApp(string appId) => Calls.Add("launch " + appId);

        public void OpenPath(string path) => Calls.Add("open " + path);

        public void RevealInFolder(string path) => Calls.Add("reveal " + path);

        public void OpenLink(string link) => Calls.Add("link " + link);

        public void EditFile(string path) => Calls.Add("edit " + path);
    }

    private readonly FileIndex _index = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly List<string> _chosenBefore = [];

    private FileProvider NewProvider() => new(_index, _launcher, () => _chosenBefore);

    [Fact]
    public void A_one_letter_query_finds_no_files()
    {
        _index.Add(new FileEntry(Docs, "budget.xlsx", false));

        Assert.Empty(NewProvider().Search("b"));
        Assert.Single(NewProvider().Search("bu"));
    }

    [Fact]
    public void A_one_letter_query_finds_no_files_even_among_those_chosen_before()
    {
        _index.Add(new FileEntry(Docs, "budget.xlsx", false));
        _chosenBefore.Add(@"file:C:\Users\Sam\Documents\budget.xlsx");

        Assert.Empty(NewProvider().Search("b"));
    }

    [Fact]
    public void A_file_becomes_an_item_that_names_its_folder()
    {
        _index.Add(new FileEntry(Docs, "Budget 2026.XLSX", false));

        var result = Assert.Single(NewProvider().Search("budget"));

        Assert.Equal(@"file:C:\Users\Sam\Documents\Budget 2026.XLSX", result.Item.Id);
        Assert.Equal("Budget 2026.XLSX", result.Item.Title);
        Assert.Equal(Docs, result.Item.Subtitle);
        Assert.Equal(ItemKind.File, result.Item.Kind);
        Assert.Equal("filetype:.xlsx", result.Item.IconKey);
        Assert.Equal(FuzzyMatcher.Score("budget", "Budget 2026.XLSX"), result.Score);
    }

    [Theory]
    [InlineData("holiday", true, "filetype:folder")]
    [InlineData("makefile", false, "filetype:")]
    [InlineData("archive.tar.GZ", false, "filetype:.gz")]
    public void The_icon_key_comes_from_the_kind_of_entry(string name, bool isDirectory, string expected)
    {
        _index.Add(new FileEntry(Docs, name, isDirectory));

        Assert.Equal(expected, Assert.Single(NewProvider().Search(name[..3])).Item.IconKey);
    }

    [Fact]
    public void Enter_opens_the_file_and_the_secondary_action_reveals_it()
    {
        _index.Add(new FileEntry(Docs, "notes.txt", false));
        var item = Assert.Single(NewProvider().Search("notes")).Item;

        Assert.NotNull(item.Run);
        item.Run();
        Assert.NotNull(item.RunSecondary);
        item.RunSecondary();

        Assert.Equal([@"open C:\Users\Sam\Documents\notes.txt", @"reveal C:\Users\Sam\Documents\notes.txt"], _launcher.Calls);
    }

    [Fact]
    public void A_file_chosen_before_is_offered_even_when_fifty_others_match_better()
    {
        for (int i = 0; i < 60; i++)
        {
            _index.Add(new FileEntry(Docs, $"report-{i:00}.txt", false));
        }

        _index.Add(new FileEntry(Docs, "my annual report.txt", false));
        string favourite = @"file:C:\Users\Sam\Documents\my annual report.txt";

        Assert.DoesNotContain(NewProvider().Search("report"), result => result.Item.Id == favourite);

        _chosenBefore.Add(favourite);
        var results = NewProvider().Search("report");

        Assert.Equal(51, results.Count);
        Assert.Contains(results, result => result.Item.Id == favourite);
    }

    [Fact]
    public void Files_chosen_before_are_not_duplicated_invented_or_offered_when_they_do_not_match()
    {
        _index.Add(new FileEntry(Docs, "report.txt", false));
        _index.Add(new FileEntry(Docs, "holiday.jpg", false));
        _chosenBefore.Add(@"file:C:\Users\Sam\Documents\REPORT.txt");      // already among the matches
        _chosenBefore.Add(@"file:C:\Users\Sam\Documents\report-old.txt");  // matches the query, but no longer in the index
        _chosenBefore.Add(@"file:C:\Users\Sam\Documents\holiday.jpg");     // in the index, but does not match the query
        _chosenBefore.Add("app:report");                                   // matches the query, but is not a file

        var result = Assert.Single(NewProvider().Search("report"));

        Assert.Equal("report.txt", result.Item.Title);
    }

    [Theory]
    [InlineData(@"file:C:\Users\Sam\Documents\private diary.txt", "file:(path withheld)")]
    [InlineData(@"FILE:C:\x.txt", "file:(path withheld)")]
    [InlineData(@"app:C:\Program Files\Tool\tool.exe", "app:(path withheld)")]
    [InlineData(@"app:{6D809377-6AF0-444B-8957-A3773F02200E}\Tool\tool.exe", "app:(path withheld)")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Tool\tool.exe", "(path withheld)")]
    [InlineData("app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("command:quit", "command:quit")]
    public void File_paths_are_withheld_from_log_text(string itemId, string expected)
    {
        Assert.Equal(expected, LogSafe.ItemId(itemId));
    }
}
