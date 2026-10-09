using System.Text.Json;
using Wyvern.Core.Launcher;
using Wyvern.Core.Library;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

/// <summary>
/// The pieces the app joins to paste a snippet, joined here the way WyvernShell joins them:
/// the real search, usage store, launcher view model, providers and paste service, with the
/// retrying wrapper around a fake clipboard. Only the Windows ends are fakes, and the waits are
/// a list the test works through by hand, so nothing is pressed, focused or copied for real.
/// </summary>
public sealed class PastePathTests : IDisposable
{
    private sealed class FakeTarget(List<string> events) : IPasteTarget
    {
        public bool Activate()
        {
            events.Add("focus given back");
            return true;
        }

        public void SendPaste() => events.Add("Ctrl+V pressed");
    }

    private readonly TempDir _dir = new();
    private readonly List<string> _events = [];
    private readonly List<(TimeSpan After, Action Action)> _waits = [];
    private readonly List<string> _notes = [];
    private readonly List<(string Message, Exception Error)> _reports = [];
    private readonly FakeClipboard _clipboard;
    private readonly LauncherViewModel _viewModel;

    public PastePathTests()
    {
        _clipboard = new FakeClipboard(_events) { Text = "what I copied earlier" };
        var usage = new UsageStore(UsageFile, new FakeClock());
        var library = new LibrarySnapshot(
            [],
            [new Snippet("sig.md", @"C:\library\snippets\sig.md", "Signature", ";sig", "Quote: {clipboard}")],
            []);
        var launcher = new FakeProcessLauncher();

        // As in WyvernShell: one retrying clipboard, given both to the paste service and to the
        // snippet provider. It never has to wait here, so the wait it is given does nothing.
        var clipboard = new RetryingClipboard(_clipboard, _ => { });
        var paste = new PasteService(
            clipboard,
            new FakeTarget(_events),
            (after, action) => _waits.Add((after, action)),
            _notes.Add,
            (message, error) => _reports.Add((message, error)));
        var quicklinks = new QuicklinkProvider(() => library, launcher);
        var snippets = new SnippetProvider(() => library, paste, clipboard, launcher, new FakeClock(), TimeZoneInfo.Utc);

        _viewModel = new LauncherViewModel(new SearchEngine([quicklinks, snippets], usage), usage);
        _viewModel.Dismissed += (_, _) => _events.Add("launcher dismissed");
    }

    public void Dispose() => _dir.Dispose();

    private string UsageFile => _dir.File("usage.json");

    /// <summary>Lets the oldest wait run out.</summary>
    private void NextWaitEnds()
    {
        var wait = _waits[0];
        _waits.RemoveAt(0);
        wait.Action();
    }

    [Fact]
    public void Typing_a_keyword_and_pressing_Enter_pastes_the_expanded_snippet_in_order_and_puts_the_clipboard_back()
    {
        _viewModel.Query = ";sig";
        Assert.Equal(["Signature"], _viewModel.Results.Select(r => r.Title));

        _viewModel.RunSelected();

        // The window is hidden before anything else. The clipboard is read once to fill in
        // {clipboard}, and again by the paste service to remember what to put back.
        Assert.Equal(
            [
                "launcher dismissed",
                "clipboard read",
                "clipboard read",
                "clipboard set to Quote: what I copied earlier",
                "focus given back",
            ],
            _events);
        Assert.Equal(PasteService.SettleDelay, Assert.Single(_waits).After);

        NextWaitEnds();

        Assert.Equal("Ctrl+V pressed", _events[^1]);
        Assert.Equal(6, _events.Count);
        Assert.Equal(PasteService.RestoreDelay, Assert.Single(_waits).After);
        Assert.Equal("Quote: what I copied earlier", _clipboard.Text);

        NextWaitEnds();

        Assert.Equal(["clipboard read", "clipboard set to what I copied earlier"], _events.Skip(6));
        Assert.Equal("what I copied earlier", _clipboard.Text);
        Assert.Empty(_waits);
        Assert.Empty(_notes);
        Assert.Empty(_reports);
        Assert.Equal("", _viewModel.Query);

        // The use was recorded under the snippet's id, and nothing that was typed or pasted is in the file.
        string file = File.ReadAllText(UsageFile);
        using var document = JsonDocument.Parse(file);
        Assert.Equal(["snippet:sig.md"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain(";sig", file);
        Assert.DoesNotContain("copied", file);
        Assert.DoesNotContain("Quote", file);
    }
}
