using System.Text;
using Wyvern.Core.Apps;
using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Files;
using Wyvern.Core.Launcher;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>What else can be done with a row besides its own action: an app, a file, an entry of the clipboard history. Everything runs against fakes.</summary>
public sealed class RowActionsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeProcessLauncher _launcher = new();
    private readonly List<string> _copied = [];

    public void Dispose() => _dir.Dispose();

    private sealed class Catalog(params AppEntry[] apps) : IAppCatalog
    {
        public IReadOnlyList<AppEntry> GetApps() => apps;
    }

    private SearchItem App(AppEntry app, bool withCopy = true)
    {
        var provider = new AppProvider(new Catalog(app), _launcher, withCopy ? _copied.Add : null);
        provider.Refresh();
        return Assert.Single(provider.Browse());
    }

    [Fact]
    public void A_program_with_a_file_of_its_own_can_be_run_as_administrator_shown_in_its_folder_and_have_its_path_copied()
    {
        var item = App(new AppEntry("Notepad", "notepad-id", @"C:\made-up\Notepad.EXE"));

        Assert.Equal([AppProvider.RunAsAdministrator, AppProvider.OpenFileLocation, AppProvider.CopyPath], item.Actions.Select(action => action.Name));

        foreach (var action in item.Actions)
        {
            action.Run();
        }

        Assert.Equal([@"admin C:\made-up\Notepad.EXE", @"reveal C:\made-up\Notepad.EXE"], _launcher.Calls);
        Assert.Equal([@"C:\made-up\Notepad.EXE"], _copied);
    }

    [Fact]
    public void What_is_not_a_program_file_is_not_run_as_administrator()
    {
        var item = App(new AppEntry("Services", "services-id", @"C:\made-up\services.msc"));

        Assert.Equal([AppProvider.OpenFileLocation, AppProvider.CopyPath], item.Actions.Select(action => action.Name));
    }

    [Fact]
    public void An_app_whose_file_is_not_known_has_only_its_own_action()
    {
        Assert.Empty(App(new AppEntry("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")).Actions);
        Assert.Empty(App(new AppEntry("Calculator", "calc-id", "")).Actions);
    }

    [Fact]
    public void Without_a_way_to_copy_there_is_no_copy_path()
    {
        var item = App(new AppEntry("Notepad", "notepad-id", @"C:\made-up\notepad.exe"), withCopy: false);

        Assert.Equal([AppProvider.RunAsAdministrator, AppProvider.OpenFileLocation], item.Actions.Select(action => action.Name));
    }

    [Fact]
    public void A_file_s_path_can_be_copied()
    {
        var index = new FileIndex();
        index.Add(new FileEntry(@"C:\made-up\docs", "Budget 2026.xlsx", IsDirectory: false));
        var withCopy = new FileProvider(index, _launcher, () => [], _copied.Add);
        var without = new FileProvider(index, _launcher, () => []);

        var item = Assert.Single(withCopy.Search("budget")).Item;
        var action = Assert.Single(item.Actions);
        Assert.Equal(FileProvider.CopyPath, action.Name);
        action.Run();

        Assert.Equal([@"C:\made-up\docs\Budget 2026.xlsx"], _copied);
        Assert.Empty(Assert.Single(without.Search("budget")).Item.Actions);
    }

    private sealed class Listed(SearchItem[] items) : ISearchProvider, IBrowsableProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => TitleMatcher.Match(query, items);

        public IReadOnlyList<SearchItem> Browse() => items;
    }

    private LauncherViewModel Launcher(out UsageStore usage, ClipboardViewModel? clipboard, params SearchItem[] items)
    {
        usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var launcher = new LauncherViewModel(new SearchEngine([new Listed(items)], usage), usage, clipboard: clipboard);
        launcher.Refresh();
        return launcher;
    }

    [Fact]
    public void A_further_action_of_the_selected_item_hides_the_window_first_and_counts_as_a_use_of_the_item()
    {
        var done = new List<string>();
        var item = new SearchItem("app:notepad", "Notepad", "", ItemKind.App, () => done.Add("opened"))
        {
            Actions = [new ItemAction("Copy Path", () => done.Add("copied"))],
        };
        var launcher = Launcher(out var usage, null, item);
        launcher.Dismissed += (_, _) => done.Add("dismissed");
        launcher.Query = "note";

        launcher.RunAction(item.Actions[0]);

        Assert.Equal(["dismissed", "copied"], done);
        Assert.Equal("", launcher.Query);
        Assert.True(usage.Boost("app:notepad") > 1.0);
    }

    [Fact]
    public void An_action_that_is_not_the_selected_item_s_is_not_done()
    {
        var done = new List<string>();
        var first = new SearchItem("app:a", "Alpha", "", ItemKind.App, () => { }) { Actions = [new ItemAction("One", () => done.Add("one"))] };
        var second = new SearchItem("app:b", "Beta", "", ItemKind.App, () => { }) { Actions = [new ItemAction("Two", () => done.Add("two"))] };
        var launcher = Launcher(out _, null, first, second);
        launcher.Query = "alpha";

        launcher.RunAction(second.Actions[0]);
        launcher.RunAction(new ItemAction("One", () => done.Add("a stranger")));

        Assert.Empty(done);
    }

    [Fact]
    public void An_action_that_fails_is_reported_by_the_item_s_id()
    {
        var item = new SearchItem("app:notepad", "Notepad", "", ItemKind.App, () => { })
        {
            Actions = [new ItemAction("Run as Administrator", () => throw new InvalidOperationException("no"))],
        };
        var launcher = Launcher(out _, null, item);
        var failures = new List<string>();
        launcher.ActionFailed += (_, failure) => failures.Add(failure.ItemId);

        launcher.RunAction(item.Actions[0]);

        Assert.Equal(["app:notepad"], failures);
    }

    [Fact]
    public void An_item_without_further_actions_has_an_empty_list_and_not_none()
    {
        Assert.Empty(new SearchItem("command:x", "X", "", ItemKind.Command, () => { }).Actions);
    }

    private sealed class RecordingPaste : IPasteService
    {
        public List<string> Texts { get; } = [];

        public List<IReadOnlyList<ClipboardBlock>> Kept { get; } = [];

        public void Paste(string text) => Texts.Add(text);

        public void PasteAndKeep(IReadOnlyList<ClipboardBlock> blocks) => Kept.Add(blocks);
    }

    private ClipEntry Entry(string id) => new(id, new DateTimeOffset(2026, 10, 3, 11, 30, 0, TimeSpan.Zero), "OTTER-PC", new DateOnly(2026, 10, 3), _dir.Path);

    [Fact]
    public void Pasting_as_text_pastes_the_text_alone_and_keeps_it_on_the_clipboard()
    {
        string html = new string('2', 64) + ClipPaths.HtmlExtension;
        File.WriteAllText(_dir.File(html), "<b>harbour seal</b>");
        var paste = new RecordingPaste();
        var notes = new List<string>();
        var paster = new ClipPaster(paste, new FakeImageCodec(), notes.Add);
        var entry = Entry("a") with { Text = "harbour seal", TextLength = 12, Html = html };

        paster.PasteText(entry);

        var blocks = Assert.Single(paste.Kept);
        var block = Assert.Single(blocks);
        Assert.Equal(ClipboardFormat.UnicodeText, block.Format);
        Assert.Equal("harbour seal", ClipboardBytes.Text(block.Bytes));
        Assert.Empty(notes);
        Assert.Empty(paste.Texts);

        // The ordinary paste still takes every form.
        paster.Paste(entry);
        Assert.Equal(2, paste.Kept[1].Count);
    }

    [Fact]
    public void A_long_text_is_pasted_whole_from_its_file()
    {
        string whole = new string('a', 500) + " the end";
        string file = new string('1', 64) + ClipPaths.TextExtension;
        File.WriteAllText(_dir.File(file), whole, new UTF8Encoding(false));
        var paste = new RecordingPaste();
        var paster = new ClipPaster(paste, new FakeImageCodec(), _ => { });
        var entry = Entry("a") with { Text = whole[..100], TextLength = whole.Length, TextFile = file };

        paster.PasteText(entry);

        Assert.Equal(whole, ClipboardBytes.Text(Assert.Single(Assert.Single(paste.Kept)).Bytes));
        Assert.Equal(whole, paster.Text(entry));
    }

    [Fact]
    public void An_entry_without_text_is_not_pasted_as_text_and_the_owner_is_told()
    {
        var paste = new RecordingPaste();
        var notes = new List<string>();
        var paster = new ClipPaster(paste, new FakeImageCodec(), notes.Add);

        paster.PasteText(Entry("a") with { Files = [@"C:\made-up\seal.png"] });

        Assert.Empty(paste.Kept);
        Assert.Equal([ClipPaster.NoTextNote], notes);
        Assert.Null(paster.Text(Entry("a")));
    }

    [Fact]
    public void In_the_history_pasting_as_text_and_saving_as_a_snippet_hide_the_window_first_and_need_an_entry_with_text()
    {
        var done = new List<string>();
        var history = new List<ClipEntry>
        {
            Entry("text") with { Text = "harbour seal", TextLength = 12 },
            Entry("files") with { Files = [@"C:\made-up\seal.png"] },
        };
        var view = new ClipboardViewModel(
            () => history,
            new FakeClock(),
            new ClipboardActions(_ => done.Add("paste"), _ => { }, _ => { }, _ => { })
            {
                PasteText = entry => done.Add("as text " + entry.Id),
                SaveAsSnippet = entry => done.Add("snippet " + entry.Id),
            },
            TimeZoneInfo.Utc);
        var launcher = Launcher(out _, view);
        launcher.Dismissed += (_, _) => done.Add("dismissed");

        // With the history closed neither does anything.
        launcher.PasteSelectedClipAsText();
        launcher.SaveSelectedClipAsSnippet();
        Assert.Empty(done);

        launcher.OpenClipboard();
        launcher.PasteSelectedClipAsText();
        Assert.Equal(["dismissed", "as text text"], done);
        Assert.False(view.IsOpen);

        done.Clear();
        launcher.OpenClipboard();
        launcher.SaveSelectedClipAsSnippet();
        Assert.Equal(["dismissed", "snippet text"], done);

        // An entry that is only files has no text to paste or to save.
        done.Clear();
        launcher.OpenClipboard();
        launcher.MoveSelection(1);
        launcher.PasteSelectedClipAsText();
        launcher.SaveSelectedClipAsSnippet();
        Assert.Empty(done);
        Assert.True(view.IsOpen);
    }

    [Fact]
    public void A_launcher_that_cannot_start_a_program_as_administrator_says_so_by_throwing()
    {
        IProcessLauncher plain = new OpensNothing();

        Assert.Throws<NotSupportedException>(() => plain.RunAsAdministrator(@"C:\made-up\notepad.exe"));
    }

    private sealed class OpensNothing : IProcessLauncher
    {
        public void LaunchApp(string appId)
        {
        }

        public void OpenPath(string path)
        {
        }

        public void RevealInFolder(string path)
        {
        }

        public void OpenLink(string link)
        {
        }

        public void EditFile(string path)
        {
        }
    }
}
