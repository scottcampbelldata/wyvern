using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Launcher;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>
/// An entry of the clipboard history put on the clipboard again without being pasted, and the
/// headings the owner's own quicklinks, snippets and scripts stand under before anything is
/// typed. Everything runs against fakes: no key is pressed and the real clipboard is not touched.
/// </summary>
public sealed class CopyWithoutPasteTests : IDisposable
{
    private const string Folder = @"C:\made-up\clipboard\OTTER-PC\2026-10-03";

    private readonly IReadOnlyList<ClipboardBlock> _blocks =
    [
        new(ClipboardFormat.Html, [1, 2, 3, 0]),
        new(ClipboardFormat.Dib, [4, 5, 6]),
    ];

    private readonly TempDir _dir = new();
    private readonly List<string> _events = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly List<string> _notes = [];
    private readonly FakeClipboard _clipboard;
    private readonly PasteService _paste;

    public CopyWithoutPasteTests()
    {
        _clipboard = new FakeClipboard(_events) { Text = "what I copied earlier" };
        _paste = new PasteService(
            _clipboard,
            new Target(_events),
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (_, _) => _events.Add("something was reported"));
    }

    public void Dispose() => _dir.Dispose();

    private void EveryWaitEnds()
    {
        while (_waits.Count > 0)
        {
            var wait = _waits[0];
            _waits.RemoveAt(0);
            wait.Action();
        }
    }

    [Fact]
    public void Keeping_puts_the_blocks_on_the_clipboard_and_presses_nothing()
    {
        _paste.Keep(_blocks);

        Assert.Equal(["clipboard set to 2 blocks"], _events);
        Assert.Same(_blocks, _clipboard.Blocks);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Keeping_no_blocks_touches_nothing()
    {
        _paste.Keep([]);

        Assert.Empty(_events);
        Assert.Equal("what I copied earlier", _clipboard.Text);
    }

    [Fact]
    public void An_earlier_paste_does_not_put_its_text_back_over_what_was_kept()
    {
        _paste.Paste("a snippet");
        _paste.Keep(_blocks);
        _events.Clear();

        EveryWaitEnds();

        // The earlier paste was overtaken: it presses nothing and puts nothing back.
        Assert.Empty(_events);
        Assert.Same(_blocks, _clipboard.Blocks);
    }

    [Fact]
    public void Copying_the_selected_entry_hides_the_launcher_first_and_hands_that_entry_to_the_copy()
    {
        var entry = new ClipEntry("a", new DateTimeOffset(2026, 10, 3, 11, 30, 0, TimeSpan.Zero), "OTTER-PC", new DateOnly(2026, 10, 3), Folder)
        {
            Text = "hello",
            TextLength = 5,
        };
        var done = new List<string>();
        var clock = new FakeClock();
        var view = new ClipboardViewModel(
            () => [entry],
            clock,
            new ClipboardActions(_ => done.Add("pasted"), _ => { }, _ => { }, _ => { }) { Copy = copied => done.Add("copied " + copied.Id) });
        var usage = new UsageStore(_dir.File("usage.json"), clock);
        var viewModel = new LauncherViewModel(new SearchEngine([], usage), usage, null, view);
        viewModel.Dismissed += (_, _) => done.Add("hidden");
        viewModel.OpenClipboard();

        viewModel.CopySelectedClip();

        Assert.Equal(["hidden", "copied a"], done);
    }

    [Fact]
    public void Nothing_is_copied_when_the_history_is_not_open()
    {
        var done = new List<string>();
        var clock = new FakeClock();
        var view = new ClipboardViewModel(() => [], clock, new ClipboardActions(_ => { }, _ => { }, _ => { }, _ => { }) { Copy = _ => done.Add("copied") });
        var usage = new UsageStore(_dir.File("usage.json"), clock);
        var viewModel = new LauncherViewModel(new SearchEngine([], usage), usage, null, view);

        viewModel.CopySelectedClip();

        Assert.Empty(done);
    }

    [Fact]
    public void A_paste_service_that_was_not_made_for_keeping_refuses()
    {
        IPasteService plain = new PasteOnly();

        Assert.Throws<NotSupportedException>(() => plain.Keep(_blocks));
    }

    [Fact]
    public void Quicklinks_snippets_and_scripts_stand_under_their_own_headings_between_the_commands_and_the_apps()
    {
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var viewModel = new LauncherViewModel(
            new SearchEngine(
                [
                    new Browsable(
                        Item("app:z", "Zebra Paint", ItemKind.App),
                        Item("script:b", "Backup", ItemKind.Script),
                        Item("snippet:s", "Signature", ItemKind.Snippet),
                        Item("quicklink:w", "Wiki", ItemKind.Quicklink),
                        Item("command:reload", "Reload", ItemKind.Command),
                        Item("command:clipboard-history", "Clipboard History", ItemKind.Command)),
                ],
                usage),
            usage);

        viewModel.Refresh();

        Assert.Equal(
            [
                ("Clipboard History", LauncherViewModel.SuggestionsSection),
                ("Reload", LauncherViewModel.CommandsSection),
                ("Wiki", LauncherViewModel.QuicklinksSection),
                ("Signature", LauncherViewModel.SnippetsSection),
                ("Backup", LauncherViewModel.ScriptsSection),
                ("Zebra Paint", LauncherViewModel.AppsSection),
            ],
            viewModel.Results.Select(item => (item.Title, viewModel.SectionOf(item))));
    }

    private static SearchItem Item(string id, string title, ItemKind kind) => new(id, title, "", kind, () => { });

    private sealed class Browsable(params SearchItem[] items) : ISearchProvider, IBrowsableProvider
    {
        public IReadOnlyList<SearchResult> Search(string query) => [];

        public IReadOnlyList<SearchItem> Browse() => items;
    }

    private sealed class PasteOnly : IPasteService
    {
        public void Paste(string text)
        {
        }

        public void PasteAndKeep(IReadOnlyList<ClipboardBlock> blocks)
        {
        }
    }

    private sealed class Target(List<string> events) : IPasteTarget
    {
        public bool Activate()
        {
            events.Add("focus given back");
            return true;
        }

        public void SendPaste() => events.Add("Ctrl+V pressed");
    }
}
