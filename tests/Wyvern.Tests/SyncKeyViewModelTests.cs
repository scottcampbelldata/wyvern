using Wyvern.Core.Commands;
using Wyvern.Core.Launcher;
using Wyvern.Core.Search;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// The Show Sync Key window's logic, and the five sync commands, first by themselves and then
/// as items in the launcher. The key is made up; the clipboard exists only in the test.
/// </summary>
public sealed class SyncKeyViewModelTests : IDisposable
{
    private const string KeyText = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static readonly byte[] Master = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private readonly TempDir _dir = new();
    private readonly FakeKeyProtector _protector = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly List<string> _reports = [];
    private readonly KeyStore _keys;
    private readonly SyncKeyViewModel _viewModel;

    public SyncKeyViewModelTests()
    {
        _keys = new KeyStore(_dir.File("key.bin"), _dir.File("ssh-passphrase.bin"), _protector);
        _viewModel = new SyncKeyViewModel(_keys, _clipboard, (text, _) => _reports.Add(text));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Before_it_is_loaded_nothing_is_shown()
    {
        _keys.WriteMaster(Master);

        Assert.Equal("", _viewModel.KeyText);
        Assert.False(_viewModel.HasKey);
    }

    [Fact]
    public void The_key_is_shown_as_the_text_the_other_machine_takes()
    {
        _keys.WriteMaster(Master);
        var changed = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.Load();

        Assert.Equal(KeyText, _viewModel.KeyText);
        Assert.True(_viewModel.HasKey);
        Assert.Equal(SyncKeyViewModel.ShownMessage, _viewModel.Message);
        Assert.Equal(["KeyText", "HasKey", "Message"], changed);
        Assert.True(SyncKeys.TryParse(_viewModel.KeyText, out byte[] back));
        Assert.Equal(Master, back);
    }

    [Fact]
    public void A_machine_without_a_key_says_so_and_shows_none()
    {
        _viewModel.Load();

        Assert.False(_viewModel.HasKey);
        Assert.Equal(SyncKeyViewModel.NoKeyMessage, _viewModel.Message);
        Assert.Empty(_reports);
    }

    [Fact]
    public void A_key_that_cannot_be_read_back_says_so_and_shows_none()
    {
        File.WriteAllBytes(_dir.File("key.bin"), Master);

        _viewModel.Load();

        Assert.False(_viewModel.HasKey);
        Assert.Equal(SyncKeyViewModel.UnreadableMessage, _viewModel.Message);
        Assert.Equal(["The sync key could not be read to be shown"], _reports);
    }

    [Fact]
    public void Copy_puts_the_key_on_the_clipboard_through_the_writer_it_was_given()
    {
        _keys.WriteMaster(Master);
        _viewModel.Load();

        _viewModel.Copy();

        Assert.Equal(KeyText, _clipboard.Text);
        Assert.Equal(1, _clipboard.Calls);
        Assert.Equal(SyncKeyViewModel.CopiedMessage, _viewModel.Message);
    }

    [Fact]
    public void With_no_key_shown_copy_touches_nothing()
    {
        _viewModel.Load();

        _viewModel.Copy();

        Assert.Equal(0, _clipboard.Calls);
        Assert.Equal(SyncKeyViewModel.NoKeyMessage, _viewModel.Message);
    }

    [Fact]
    public void A_copy_that_fails_is_said_and_the_key_is_not_in_the_log()
    {
        _keys.WriteMaster(Master);
        _viewModel.Load();
        _clipboard.Broken = new InvalidOperationException("the clipboard is broken: " + KeyText);

        _viewModel.Copy();

        Assert.Equal(SyncKeyViewModel.CopyFailedMessage, _viewModel.Message);
        Assert.Equal(["The sync key could not be put on the clipboard"], _reports);
    }

    [Fact]
    public void Closing_the_window_lets_go_of_the_key()
    {
        _keys.WriteMaster(Master);
        _viewModel.Load();

        _viewModel.Clear();

        Assert.Equal("", _viewModel.KeyText);
        Assert.False(_viewModel.HasKey);
        Assert.Equal("", _viewModel.Message);
        _viewModel.Copy();
        Assert.Equal(0, _clipboard.Calls);
    }

    // What the commands ran and what they showed the owner, and how sync stands when they ask.
    private readonly List<string> _ran = [];
    private readonly List<string> _notes = [];
    private SyncPause _pause = SyncPause.None;

    private IReadOnlyList<BuiltInCommand> Commands() => SyncCommands.Create(
        () => _ran.Add("now"),
        () => _ran.Add("setup"),
        () => _ran.Add("key"),
        () => _pause,
        () => _ran.Add("confirm"),
        () => _ran.Add("restore"),
        _notes.Add);

    [Fact]
    public void The_five_sync_commands_are_Sync_Now_Sync_Setup_Show_Sync_Key_Confirm_Sync_Deletions_and_Restore_Missing_Files()
    {
        _pause = new SyncPause(true, 30);

        var commands = Commands();
        foreach (var command in commands)
        {
            command.Run?.Invoke();
        }

        // Each title runs the action that was given for it: a swap of the last two would send
        // the deletions the owner asked to have undone. Four run on Enter. Confirm Sync
        // Deletions has no action of its own: it takes a typed word first.
        Assert.Equal(["sync-now", "sync-setup", "show-sync-key", "confirm-sync-deletions", "restore-missing-files"], commands.Select(command => command.Id));
        Assert.Equal(["Sync Now", "Sync Setup", "Show Sync Key", "Confirm Sync Deletions", "Restore Missing Files"], commands.Select(command => command.Title));
        Assert.Equal(["now", "setup", "key", "restore"], _ran);
        Assert.Null(commands[3].Run);
        commands[3].Argument!.Run("yes");
        Assert.Equal(["now", "setup", "key", "restore", "confirm"], _ran);
        Assert.Empty(_notes);
        Assert.All(commands, command =>
        {
            Assert.Equal(command.Id == "confirm-sync-deletions", command.Argument is not null);
            Assert.False(command.KeepOpen);
            Assert.NotEqual("", command.Subtitle);
        });
    }

    // ---- Confirm Sync Deletions asks before it acts ----
    //
    // It allows deletions on every machine, so one key press must not do it: like Clear
    // Clipboard History it takes the typed word yes, and its prompt says what will happen.

    [Fact]
    public void The_prompt_of_Confirm_Sync_Deletions_says_what_will_happen_with_the_count_as_it_is_when_the_prompt_is_shown()
    {
        var argument = Commands()[3].Argument!;

        _pause = new SyncPause(true, 30);
        Assert.Equal("Type yes to delete 30 files on your other machines too", argument.Prompt);

        // The count is asked for each time the prompt is shown: it is not the one the command was made with.
        _pause = new SyncPause(true, 18);
        Assert.Equal("Type yes to delete 18 files on your other machines too", argument.Prompt);
        _pause = new SyncPause(true, 1);
        Assert.Equal("Type yes to delete 1 file on your other machines too", argument.Prompt);

        // Paused, and no run has counted since: fixed words without a count.
        _pause = new SyncPause(true);
        Assert.Equal("Type yes to delete the missing files on your other machines too", argument.Prompt);

        // Not paused: there is nothing to type.
        _pause = SyncPause.None;
        Assert.Equal("Sync is not paused. There is nothing to confirm.", argument.Prompt);
        Assert.Equal(SyncService.NotPausedNote, argument.Prompt);
    }

    [Fact]
    public void Only_the_word_yes_starts_the_run_that_may_send_the_deletions()
    {
        _pause = new SyncPause(true, 30);

        Commands()[3].Argument!.Run("yes");

        Assert.Equal(["confirm"], _ran);
        Assert.Empty(_notes);
        Assert.Equal("yes", SyncCommands.ConfirmWord);
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("YES")]
    [InlineData("yes ")]
    [InlineData(" yes")]
    [InlineData("yes\n")]
    [InlineData("yess")]
    [InlineData("y")]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("30")]
    public void Any_other_text_starts_nothing_and_shows_the_fixed_sentence_that_says_so(string typed)
    {
        _pause = new SyncPause(true, 30);

        Commands()[3].Argument!.Run(typed);

        Assert.Empty(_ran);
        Assert.Equal([SyncCommands.NotConfirmedNote], _notes);
        Assert.Equal("Nothing was confirmed, and sync is still paused. To confirm the deletions, type yes and press Enter.", SyncCommands.NotConfirmedNote);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes")]
    [InlineData("")]
    [InlineData("no")]
    public void While_sync_is_not_paused_Confirm_Sync_Deletions_starts_nothing_whatever_is_typed_and_says_that_there_is_nothing_to_confirm(string typed)
    {
        _pause = SyncPause.None;

        Commands()[3].Argument!.Run(typed);

        Assert.Empty(_ran);
        Assert.Equal([SyncService.NotPausedNote], _notes);
    }

    // Paused with nothing held: the files are back, and the pause is there until the next
    // ordinary run has seen that. Confirm Sync Deletions has nothing to confirm.

    [Fact]
    public void While_sync_is_paused_with_nothing_held_the_prompt_of_Confirm_Sync_Deletions_says_that_nothing_is_waiting()
    {
        var argument = Commands()[3].Argument!;

        _pause = new SyncPause(true, 0, NothingHeld: true);

        Assert.Equal("Nothing is waiting to be confirmed.", argument.Prompt);
        Assert.Equal(SyncService.NothingWaitingNote, argument.Prompt);
        Assert.DoesNotContain("delete", argument.Prompt);

        // Paused with something held, or not known to hold nothing, it asks as before.
        _pause = new SyncPause(true);
        Assert.Equal("Type yes to delete the missing files on your other machines too", argument.Prompt);
        _pause = new SyncPause(true, 30);
        Assert.Equal("Type yes to delete 30 files on your other machines too", argument.Prompt);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes")]
    [InlineData("")]
    [InlineData("no")]
    public void While_sync_is_paused_with_nothing_held_Confirm_Sync_Deletions_starts_nothing_whatever_is_typed_and_says_that_nothing_is_waiting(string typed)
    {
        _pause = new SyncPause(true, 0, NothingHeld: true);

        Commands()[3].Argument!.Run(typed);

        Assert.Empty(_ran);
        Assert.Equal([SyncService.NothingWaitingNote], _notes);
    }

    [Fact]
    public void Restore_Missing_Files_stays_one_key_press_while_sync_is_paused_with_nothing_held()
    {
        var viewModel = Launcher();
        _pause = new SyncPause(true, 0, NothingHeld: true);
        Find(viewModel, "restore", "Restore Missing Files");

        viewModel.RunSelected();

        Assert.Equal(["dismissed", "restore"], _ran);
        Assert.Empty(_notes);
    }

    private LauncherViewModel Launcher()
    {
        var usage = new UsageStore(_dir.File("usage.json"), new FakeClock());
        var viewModel = new LauncherViewModel(new SearchEngine([new CommandProvider(Commands())], usage), usage);
        viewModel.Dismissed += (_, _) => _ran.Add("dismissed");
        return viewModel;
    }

    private static void Find(LauncherViewModel viewModel, string query, string title)
    {
        viewModel.Query = query;
        viewModel.SelectedIndex = viewModel.Results.Select(result => result.Title).ToList().IndexOf(title);
        Assert.True(viewModel.SelectedIndex >= 0, "The command was not among the results.");
    }

    [Fact]
    public void In_the_launcher_Enter_on_Confirm_Sync_Deletions_asks_for_the_word_with_the_count_of_that_moment_and_starts_nothing_yet()
    {
        var viewModel = Launcher();
        _pause = new SyncPause(true, 30);
        Find(viewModel, "confirm", "Confirm Sync Deletions");

        viewModel.RunSelected();

        Assert.True(viewModel.InArgumentMode);
        Assert.Equal("Confirm Sync Deletions", viewModel.ArgumentTitle);
        Assert.Equal("Type yes to delete 30 files on your other machines too", viewModel.ArgumentPrompt);
        Assert.Empty(_ran);
        Assert.Empty(_notes);

        // He leaves the prompt. Twelve of the files are back when he asks again, with Tab this time.
        Assert.True(viewModel.LeaveArgumentMode());
        _pause = new SyncPause(true, 18);
        Find(viewModel, "confirm", "Confirm Sync Deletions");
        Assert.True(viewModel.EnterArgumentMode());

        Assert.Equal("Type yes to delete 18 files on your other machines too", viewModel.ArgumentPrompt);
        Assert.Empty(_ran);
    }

    [Fact]
    public void In_the_launcher_typing_yes_and_Enter_starts_the_run_once_and_any_other_text_or_none_starts_nothing()
    {
        var viewModel = Launcher();
        _pause = new SyncPause(true, 30);
        Find(viewModel, "confirm", "Confirm Sync Deletions");
        viewModel.RunSelected();

        // Enter in the empty line, as after a second key press on the command.
        viewModel.RunSelected();

        Assert.Equal(["dismissed"], _ran);
        Assert.Equal([SyncCommands.NotConfirmedNote], _notes);

        Find(viewModel, "confirm", "Confirm Sync Deletions");
        viewModel.RunSelected();
        viewModel.Query = "yes";
        viewModel.RunSelected();

        Assert.Equal(["dismissed", "dismissed", "confirm"], _ran);
        Assert.Single(_notes);
        Assert.False(viewModel.InArgumentMode);
    }

    [Fact]
    public void Restore_Missing_Files_stays_one_key_press_whether_sync_is_paused_or_not()
    {
        var viewModel = Launcher();
        foreach (var pause in new[] { SyncPause.None, new SyncPause(true, 30) })
        {
            _pause = pause;
            Find(viewModel, "restore", "Restore Missing Files");
            viewModel.RunSelected();
        }

        Assert.Equal(["dismissed", "restore", "dismissed", "restore"], _ran);
        Assert.False(viewModel.InArgumentMode);
        Assert.Empty(_notes);
    }

    [Fact]
    public void The_sentence_about_a_paused_sync_names_the_two_commands_by_the_titles_the_launcher_lists()
    {
        var commands = Commands();
        string note = SyncService.MissingFilesNote(30);

        Assert.Equal(
            "Sync is paused and nothing was sent: 30 files that were synced are no longer in the data folder. If you deleted, moved or renamed them, run Confirm Sync Deletions. If you did not, run Restore Missing Files to get them back from the server.",
            note);
        Assert.Contains("run " + commands[3].Title + ".", note);
        Assert.Contains("run " + commands[4].Title + " to get them back", note);
    }

    [Fact]
    public void The_sentence_about_a_paused_sync_is_true_of_one_file_too()
    {
        // While sync is paused, a run is held for a single missing file.
        Assert.Equal(
            "Sync is paused and nothing was sent: 1 file that was synced is no longer in the data folder. If you deleted, moved or renamed it, run Confirm Sync Deletions. If you did not, run Restore Missing Files to get it back from the server.",
            SyncService.MissingFilesNote(1));
        Assert.StartsWith("Sync is paused and nothing was sent: 2 files that were synced are no longer in the data folder.", SyncService.MissingFilesNote(2));
    }
}
