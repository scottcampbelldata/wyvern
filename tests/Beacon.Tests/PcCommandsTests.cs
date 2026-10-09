using Beacon.Core.Commands;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The launcher's commands for the PC itself. Nothing here locks, restarts or empties anything: the PC is a list the test reads.</summary>
public class PcCommandsTests
{
    private sealed class Pc : ISystemActions
    {
        public List<string> Asked { get; } = [];

        public bool BinHasSomething { get; set; } = true;

        public bool HasSound { get; set; } = true;

        public bool Muted { get; set; }

        public int Volume { get; set; } = 40;

        public bool Dark { get; set; }

        public void Lock() => Asked.Add("lock");

        public void Sleep() => Asked.Add("sleep");

        public void Hibernate() => Asked.Add("hibernate");

        public void Restart() => Asked.Add("restart");

        public void ShutDown() => Asked.Add("shut down");

        public void SignOut() => Asked.Add("sign out");

        public bool EmptyRecycleBin()
        {
            Asked.Add("empty bin");
            return BinHasSomething;
        }

        public void OpenRecycleBin() => Asked.Add("open bin");

        public bool? ToggleMute()
        {
            Asked.Add("mute");
            if (!HasSound)
            {
                return null;
            }

            Muted = !Muted;
            return Muted;
        }

        public int? ChangeVolume(int by)
        {
            Asked.Add("volume by " + by);
            return HasSound ? Volume = Math.Clamp(Volume + by, 0, 100) : null;
        }

        public int? SetVolume(int percent)
        {
            Asked.Add("volume " + percent);
            return HasSound ? Volume = percent : null;
        }

        public bool ToggleDarkMode()
        {
            Asked.Add("appearance");
            Dark = !Dark;
            return Dark;
        }

        public void ShowDesktop() => Asked.Add("desktop");

        public void Media(MediaKey key) => Asked.Add("media " + key);
    }

    private static BuiltInCommand Command(IReadOnlyList<BuiltInCommand> commands, string id) => commands.Single(command => command.Id == id);

    [Fact]
    public void The_commands_are_these_and_none_keeps_the_launcher_open()
    {
        var commands = PcCommands.Create(new Pc());

        Assert.Equal(
            [
                "lock-screen", "sleep", "hibernate", "restart", "shut-down", "sign-out", "empty-recycle-bin", "open-recycle-bin",
                "toggle-mute", "volume-up", "volume-down", "set-volume", "toggle-appearance", "show-desktop",
                "media-play-pause", "media-next", "media-previous",
            ],
            commands.Select(command => command.Id));
        Assert.Equal(
            [
                "Lock Screen", "Sleep", "Hibernate", "Restart", "Shut Down", "Sign Out", "Empty Recycle Bin", "Open Recycle Bin",
                "Toggle Mute", "Volume Up", "Volume Down", "Set Volume", "Toggle System Appearance", "Show Desktop",
                "Play or Pause Media", "Next Track", "Previous Track",
            ],
            commands.Select(command => command.Title));
        Assert.All(commands, command => Assert.False(command.KeepOpen));
    }

    [Fact]
    public void Lock_sleep_hibernate_the_bin_in_explorer_and_the_desktop_are_done_by_enter()
    {
        var pc = new Pc();
        var commands = PcCommands.Create(pc);

        foreach (string id in new[] { "lock-screen", "sleep", "hibernate", "open-recycle-bin", "show-desktop" })
        {
            Assert.Null(Command(commands, id).Argument);
            Command(commands, id).Run!();
        }

        Assert.Equal(["lock", "sleep", "hibernate", "open bin", "desktop"], pc.Asked);
    }

    [Fact]
    public void The_media_commands_press_the_keyboard_s_media_keys()
    {
        var pc = new Pc();
        var commands = PcCommands.Create(pc);

        foreach (string id in new[] { "media-play-pause", "media-next", "media-previous" })
        {
            Assert.Null(Command(commands, id).Argument);
            Command(commands, id).Run!();
        }

        Assert.Equal(["media PlayPause", "media NextTrack", "media PreviousTrack"], pc.Asked);
    }

    [Theory]
    [InlineData("restart", "restart", "Press Enter to restart this PC")]
    [InlineData("shut-down", "shut down", "Press Enter to shut this PC down")]
    [InlineData("sign-out", "sign out", "Press Enter to sign out of Windows")]
    [InlineData("empty-recycle-bin", "empty bin", "Press Enter to empty the Recycle Bin")]
    public void What_cannot_be_taken_back_has_no_action_of_its_own_and_asks_first(string id, string asked, string question)
    {
        var pc = new Pc();
        var command = Command(PcCommands.Create(pc), id);

        // Enter on the command only opens the input line with the question.
        Assert.Null(command.Run);
        Assert.Equal(question, command.Argument!.Prompt);
        Assert.Empty(pc.Asked);

        // Enter there does it.
        command.Argument.Run("");
        Assert.Equal([asked], pc.Asked);
    }

    [Fact]
    public void Emptying_the_bin_says_whether_there_was_anything_in_it()
    {
        var pc = new Pc();
        var said = new List<string>();
        var command = Command(PcCommands.Create(pc, said.Add), "empty-recycle-bin");

        command.Argument!.Run("");
        pc.BinHasSomething = false;
        command.Argument.Run("");

        Assert.Equal(["Recycle Bin Emptied", "Recycle Bin Is Already Empty"], said);
    }

    [Fact]
    public void The_sound_commands_say_how_things_stand_afterwards()
    {
        var pc = new Pc();
        var said = new List<string>();
        var commands = PcCommands.Create(pc, said.Add);

        Command(commands, "toggle-mute").Run!();
        Command(commands, "toggle-mute").Run!();
        Command(commands, "volume-up").Run!();
        Command(commands, "volume-down").Run!();
        Command(commands, "volume-down").Run!();

        Assert.Equal(["Muted", "Unmuted", "Volume 46%", "Volume 40%", "Volume 34%"], said);
        Assert.Equal(["mute", "mute", "volume by 6", "volume by -6", "volume by -6"], pc.Asked);
    }

    [Fact]
    public void With_nothing_to_play_sound_through_the_sound_commands_say_so()
    {
        var pc = new Pc { HasSound = false };
        var said = new List<string>();
        var commands = PcCommands.Create(pc, said.Add);

        Command(commands, "toggle-mute").Run!();
        Command(commands, "volume-up").Run!();
        Command(commands, "set-volume").Argument!.Run("50");

        Assert.Equal(["No Sound Device", "No Sound Device", "No Sound Device"], said);
    }

    [Fact]
    public void Set_volume_takes_a_whole_number_from_0_to_100_and_nothing_else()
    {
        var pc = new Pc();
        var said = new List<string>();
        var command = Command(PcCommands.Create(pc, said.Add), "set-volume");

        Assert.Null(command.Run);
        Assert.Equal("A number from 0 to 100", command.Argument!.Prompt);

        command.Argument.Run("75");
        command.Argument.Run(" 0 % ");
        command.Argument.Run("loud");
        command.Argument.Run("101");

        Assert.Equal(["Volume 75%", "Volume 0%", "Not a Number from 0 to 100", "Not a Number from 0 to 100"], said);
        Assert.Equal(["volume 75", "volume 0"], pc.Asked);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("100", 100)]
    [InlineData("  55  ", 55)]
    [InlineData("55%", 55)]
    [InlineData("055", 55)]
    public void A_volume_that_is_understood(string typed, int percent) => Assert.Equal(percent, PcCommands.Volume(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%")]
    [InlineData("101")]
    [InlineData("1000")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("5.5")]
    [InlineData("5 0")]
    [InlineData("fifty")]
    [InlineData("\u0665\u0660")]
    public void A_volume_that_is_not_understood(string typed) => Assert.Null(PcCommands.Volume(typed));

    [Fact]
    public void Toggling_the_appearance_says_which_one_windows_has_now()
    {
        var said = new List<string>();
        var command = Command(PcCommands.Create(new Pc(), said.Add), "toggle-appearance");

        command.Run!();
        command.Run!();

        Assert.Equal(["Dark Appearance", "Light Appearance"], said);
    }

    [Fact]
    public void Without_anything_to_say_it_through_the_commands_still_do_their_thing()
    {
        var pc = new Pc();
        var commands = PcCommands.Create(pc);

        Command(commands, "toggle-mute").Run!();
        Command(commands, "empty-recycle-bin").Argument!.Run("");

        Assert.Equal(["mute", "empty bin"], pc.Asked);
    }

    [Fact]
    public void They_are_found_by_name_and_listed_with_the_other_commands()
    {
        var provider = new CommandProvider(PcCommands.Create(new Pc()));

        Assert.Equal("command:lock-screen", Assert.Single(provider.Search("lock")).Item.Id);
        Assert.Equal("command:restart", provider.Search("restart")[0].Item.Id);
        Assert.Equal(17, provider.Browse().Count);
        Assert.Equal("command:media-play-pause", provider.Search("pause")[0].Item.Id);
        Assert.All(provider.Browse(), item => Assert.Equal(ItemKind.Command, item.Kind));
    }
}
