using Beacon.Core.ClipboardHistory;
using Beacon.Core.Commands;
using Beacon.Core.Launcher;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>
/// The four clipboard commands, first by themselves and then as items in the launcher. What
/// each command calls is written down in one list and what it shows the owner in another.
/// </summary>
public class ClipboardCommandsTests
{
    private const string PausedSentence = "A made-up sentence about the walrus pausing.";
    private const string ResumedSentence = "A made-up sentence about the otter resuming.";

    private readonly List<string> _calls = [];
    private readonly List<string> _notes = [];
    private readonly IReadOnlyList<BuiltInCommand> _commands;

    public ClipboardCommandsTests()
    {
        _commands = ClipboardCommands.Create(
            open: () => _calls.Add("open"),
            pause: () =>
            {
                _calls.Add("pause");
                return PausedSentence;
            },
            resume: () =>
            {
                _calls.Add("resume");
                return ResumedSentence;
            },
            clear: () => _calls.Add("clear"),
            notify: _notes.Add);
    }

    private BuiltInCommand Command(string id) => Assert.Single(_commands, command => command.Id == id);

    private LauncherViewModel Launcher(TempDir dir, IReadOnlyList<BuiltInCommand> commands, ClipboardViewModel? clipboard = null)
    {
        var usage = new UsageStore(dir.File("usage.json"), new FakeClock());
        var viewModel = new LauncherViewModel(new SearchEngine([new CommandProvider(commands)], usage), usage, clipboard: clipboard);
        viewModel.Dismissed += (_, _) => _calls.Add("dismissed");
        return viewModel;
    }

    // Types the start of a command's name and selects the command whose title is given.
    private static void Find(LauncherViewModel viewModel, string query, string title)
    {
        viewModel.Query = query;
        viewModel.SelectedIndex = viewModel.Results.Select(r => r.Title).ToList().IndexOf(title);
        Assert.True(viewModel.SelectedIndex >= 0, "The command was not among the results.");
    }

    [Fact]
    public void Create_gives_the_four_commands_in_their_order()
    {
        Assert.Equal(
            ["clipboard-history", "pause-clipboard", "resume-clipboard", "clear-clipboard"],
            _commands.Select(command => command.Id));
        Assert.Equal(
            ["Clipboard History", "Pause Clipboard Capture", "Resume Clipboard Capture", "Clear Clipboard History"],
            _commands.Select(command => command.Title));
    }

    [Fact]
    public void Making_the_commands_calls_nothing_and_shows_nothing()
    {
        Assert.Empty(_calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Clipboard_History_keeps_the_launcher_open()
    {
        Assert.True(Command("clipboard-history").KeepOpen);
    }

    [Theory]
    [InlineData("pause-clipboard")]
    [InlineData("resume-clipboard")]
    [InlineData("clear-clipboard")]
    public void The_other_three_commands_do_not_keep_the_launcher_open(string id)
    {
        Assert.False(Command(id).KeepOpen);
    }

    [Fact]
    public void Running_Clipboard_History_opens_the_view_and_shows_nothing()
    {
        var run = Command("clipboard-history").Run;
        Assert.NotNull(run);

        run();

        Assert.Equal(["open"], _calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Running_Pause_Clipboard_Capture_pauses_once_and_shows_the_sentence_that_pausing_gave()
    {
        var run = Command("pause-clipboard").Run;
        Assert.NotNull(run);

        run();

        Assert.Equal(["pause"], _calls);
        Assert.Equal([PausedSentence], _notes);
    }

    [Fact]
    public void Running_Resume_Clipboard_Capture_resumes_once_and_shows_the_sentence_that_resuming_gave()
    {
        var run = Command("resume-clipboard").Run;
        Assert.NotNull(run);

        run();

        Assert.Equal(["resume"], _calls);
        Assert.Equal([ResumedSentence], _notes);
    }

    [Theory]
    [InlineData("clipboard-history")]
    [InlineData("pause-clipboard")]
    [InlineData("resume-clipboard")]
    public void The_first_three_commands_take_no_argument(string id)
    {
        Assert.Null(Command(id).Argument);
    }

    // One key press must not clear the history: the command has nothing for Enter to run.
    [Fact]
    public void Clear_Clipboard_History_has_no_action_of_its_own_and_asks_for_a_word_instead()
    {
        var clear = Command("clear-clipboard");

        Assert.Null(clear.Run);
        Assert.NotNull(clear.Argument);
        Assert.Equal("Type yes to clear", clear.Argument.Prompt);
        Assert.Equal("Removes every entry on all your machines", clear.Subtitle);
    }

    [Fact]
    public void Typing_yes_clears_the_history_exactly_once_and_shows_nothing()
    {
        var argument = Command("clear-clipboard").Argument;
        Assert.NotNull(argument);

        argument.Run("yes");

        Assert.Equal(["clear"], _calls);
        Assert.Empty(_notes);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("YES")]
    [InlineData("yes ")]
    [InlineData(" yes")]
    [InlineData("yes\n")]
    [InlineData("yess")]
    [InlineData("ye")]
    [InlineData("y")]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("walrus")]
    public void Any_other_text_clears_nothing_and_shows_the_fixed_sentence_that_says_so(string typed)
    {
        var argument = Command("clear-clipboard").Argument;
        Assert.NotNull(argument);

        argument.Run(typed);

        Assert.Empty(_calls);
        Assert.Equal([ClipboardCommands.NotClearedNote], _notes);
    }

    [Fact]
    public void A_command_that_keeps_the_launcher_open_becomes_an_item_that_does()
    {
        var provider = new CommandProvider([new BuiltInCommand("walrus", "Walrus Show", "", () => { }, KeepOpen: true)]);

        var item = Assert.Single(provider.Search("walrus")).Item;

        Assert.Equal("command:walrus", item.Id);
        Assert.True(item.KeepOpen);
        Assert.NotNull(item.Run);
        Assert.Null(item.Argument);
    }

    [Fact]
    public void A_command_with_an_argument_and_no_action_becomes_an_item_with_that_argument_and_no_action()
    {
        var argument = new ItemArgument("Name the otter", _ => { });
        var provider = new CommandProvider([new BuiltInCommand("otter", "Otter Name", "", Run: null, Argument: argument)]);

        var item = Assert.Single(provider.Search("otter")).Item;

        Assert.Equal("command:otter", item.Id);
        Assert.Null(item.Run);
        Assert.Same(argument, item.Argument);
        Assert.False(item.KeepOpen);
    }

    [Fact]
    public void A_plain_command_becomes_an_item_that_takes_no_argument_and_does_not_keep_the_launcher_open()
    {
        var provider = new CommandProvider([new BuiltInCommand("seal", "Seal Song", "", () => { })]);

        var item = Assert.Single(provider.Search("seal")).Item;

        Assert.NotNull(item.Run);
        Assert.Null(item.Argument);
        Assert.False(item.KeepOpen);
    }

    [Fact]
    public void The_four_commands_become_items_named_after_their_ids()
    {
        var provider = new CommandProvider(_commands);

        Assert.Equal(
            ["command:clear-clipboard", "command:clipboard-history", "command:pause-clipboard", "command:resume-clipboard"],
            provider.Search("clipboard").Select(result => result.Item.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Enter_on_Clear_Clipboard_History_asks_for_the_word_and_clears_nothing_yet()
    {
        using var dir = new TempDir();
        var viewModel = Launcher(dir, _commands);
        Find(viewModel, "clear", "Clear Clipboard History");

        viewModel.RunSelected();

        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("Clear Clipboard History", viewModel.ArgumentTitle);
        Assert.Equal("Type yes to clear", viewModel.ArgumentPrompt);
        Assert.Empty(_calls);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Tab_on_Clear_Clipboard_History_asks_for_the_word_too()
    {
        using var dir = new TempDir();
        var viewModel = Launcher(dir, _commands);
        Find(viewModel, "clear", "Clear Clipboard History");

        Assert.True(viewModel.EnterArgumentMode());

        Assert.Equal("Type yes to clear", viewModel.ArgumentPrompt);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Typing_yes_and_Enter_in_the_launcher_dismisses_it_and_clears_the_history_once()
    {
        using var dir = new TempDir();
        var viewModel = Launcher(dir, _commands);
        Find(viewModel, "clear", "Clear Clipboard History");
        viewModel.RunSelected();

        viewModel.Query = "yes";
        viewModel.RunSelected();

        Assert.Equal(["dismissed", "clear"], _calls);
        Assert.Empty(_notes);
        Assert.False(viewModel.InArgumentMode);
        Assert.Equal("", viewModel.Query);
    }

    [Fact]
    public void Typing_another_word_and_Enter_in_the_launcher_clears_nothing_and_says_so()
    {
        using var dir = new TempDir();
        var viewModel = Launcher(dir, _commands);
        Find(viewModel, "clear", "Clear Clipboard History");
        viewModel.RunSelected();

        viewModel.Query = "Yes";
        viewModel.RunSelected();

        Assert.Equal(["dismissed"], _calls);
        Assert.Equal([ClipboardCommands.NotClearedNote], _notes);
    }

    [Fact]
    public void Enter_in_the_empty_argument_line_of_Clear_Clipboard_History_clears_nothing()
    {
        using var dir = new TempDir();
        var viewModel = Launcher(dir, _commands);
        Find(viewModel, "clear", "Clear Clipboard History");
        viewModel.RunSelected();

        viewModel.RunSelected();

        Assert.Equal(["dismissed"], _calls);
        Assert.Equal([ClipboardCommands.NotClearedNote], _notes);
    }

    // The command made with the view's own Open, as the app makes it, and a launcher that holds the same view.
    [Fact]
    public void Enter_on_Clipboard_History_in_the_launcher_shows_the_view_without_dismissing_the_launcher()
    {
        using var dir = new TempDir();
        var clipboard = new ClipboardViewModel(() => [], new FakeClock(), zone: TimeZoneInfo.Utc);
        var commands = ClipboardCommands.Create(clipboard.Open, () => PausedSentence, () => ResumedSentence, () => _calls.Add("clear"), _notes.Add);
        var viewModel = Launcher(dir, commands, clipboard);
        Find(viewModel, "history", "Clipboard History");

        viewModel.RunSelected();

        Assert.True(clipboard.IsOpen);
        Assert.Empty(_calls);
        Assert.Empty(_notes);
        Assert.Equal("", viewModel.Query);
        Assert.Empty(viewModel.Results);
    }

    [Fact]
    public void Enter_on_Pause_Clipboard_Capture_in_the_launcher_dismisses_it_and_then_pauses()
    {
        using var dir = new TempDir();
        var viewModel = Launcher(dir, _commands);
        Find(viewModel, "pause", "Pause Clipboard Capture");

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "pause"], _calls);
        Assert.Equal([PausedSentence], _notes);
    }
}
