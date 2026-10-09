using Wyvern.Core.Apps;
using Wyvern.Core.Commands;
using Wyvern.Core.Platform;
using Wyvern.Core.Search;

namespace Wyvern.Tests;

public class AppProviderTests
{
    private sealed class FakeCatalog : IAppCatalog
    {
        public List<AppEntry> Apps { get; } = [];

        public IReadOnlyList<AppEntry> GetApps() => Apps.ToList();
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<string> LaunchedApps { get; } = [];

        public void LaunchApp(string appId) => LaunchedApps.Add(appId);

        public void OpenPath(string path) => throw new NotSupportedException();

        public void RevealInFolder(string path) => throw new NotSupportedException();

        public void OpenLink(string link) => throw new NotSupportedException();

        public void EditFile(string path) => throw new NotSupportedException();
    }

    private readonly FakeCatalog _catalog = new();
    private readonly FakeLauncher _launcher = new();

    [Fact]
    public void Finds_nothing_before_the_first_refresh()
    {
        _catalog.Apps.Add(new AppEntry("Notepad", "notepad-id"));
        var provider = new AppProvider(_catalog, _launcher);

        Assert.Empty(provider.Search("note"));
    }

    [Fact]
    public void Finds_matching_apps_after_refresh()
    {
        _catalog.Apps.Add(new AppEntry("Notepad", "notepad-id"));
        _catalog.Apps.Add(new AppEntry("Calculator", "calc-id"));
        var provider = new AppProvider(_catalog, _launcher);
        provider.Refresh();

        var result = Assert.Single(provider.Search("note"));

        Assert.Equal("app:notepad-id", result.Item.Id);
        Assert.Equal("Notepad", result.Item.Title);
        Assert.Equal(ItemKind.App, result.Item.Kind);
        Assert.Equal("notepad-id", result.Item.IconKey);
    }

    [Fact]
    public void Running_an_item_launches_that_app()
    {
        _catalog.Apps.Add(new AppEntry("Notepad", "notepad-id"));
        var provider = new AppProvider(_catalog, _launcher);
        provider.Refresh();

        var item = provider.Search("note")[0].Item;
        Assert.NotNull(item.Run);
        item.Run();

        Assert.Equal(["notepad-id"], _launcher.LaunchedApps);
    }

    [Fact]
    public void Refresh_picks_up_newly_installed_apps_and_returns_the_list()
    {
        var provider = new AppProvider(_catalog, _launcher);
        provider.Refresh();
        _catalog.Apps.Add(new AppEntry("Notepad", "notepad-id"));

        var apps = provider.Refresh();

        Assert.Equal([new AppEntry("Notepad", "notepad-id")], apps);
        Assert.Single(provider.Search("note"));
    }

    [Fact]
    public void Entries_without_a_name_or_id_are_ignored()
    {
        _catalog.Apps.Add(new AppEntry("", "blank-name"));
        _catalog.Apps.Add(new AppEntry("Blank Id", " "));
        var provider = new AppProvider(_catalog, _launcher);

        Assert.Empty(provider.Refresh());
        Assert.Empty(provider.Search("blank"));
    }
}

public class CommandProviderTests
{
    [Fact]
    public void Finds_a_command_by_title_and_runs_it()
    {
        int quitCalls = 0;
        var provider = new CommandProvider(
        [
            new BuiltInCommand("quit", "Quit Wyvern", "Exit the launcher", () => quitCalls++),
            new BuiltInCommand("reload", "Reload", "Re-read settings", () => { }),
        ]);

        var result = Assert.Single(provider.Search("quit"));
        Assert.NotNull(result.Item.Run);
        result.Item.Run();

        Assert.Equal("command:quit", result.Item.Id);
        Assert.Equal("Exit the launcher", result.Item.Subtitle);
        Assert.Equal(ItemKind.Command, result.Item.Kind);
        Assert.Equal(1, quitCalls);
    }

    [Fact]
    public void Returns_nothing_when_no_title_matches()
    {
        var provider = new CommandProvider([new BuiltInCommand("quit", "Quit Wyvern", "", () => { })]);

        Assert.Empty(provider.Search("xyz"));
    }
}
