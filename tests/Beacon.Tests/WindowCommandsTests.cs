using Beacon.Core.Commands;
using Beacon.Core.Search;

namespace Beacon.Tests;

/// <summary>The commands that put the window in front somewhere, and where each puts it. No window is moved here: the mover is a list the test reads.</summary>
public class WindowCommandsTests
{
    private sealed class Mover : IWindowMover
    {
        public List<WindowLayout> Asked { get; } = [];

        public WindowMove Answer { get; set; } = WindowMove.Done;

        public WindowMove Place(WindowLayout layout)
        {
            Asked.Add(layout);
            return Answer;
        }
    }

    // A screen with a taskbar of 48 at the bottom, and a window somewhere on it.
    private static readonly ScreenRect Work = new(0, 0, 1920, 1032);
    private static readonly ScreenRect Window = new(300, 200, 800, 600);

    [Fact]
    public void There_is_one_command_for_every_layout_and_each_asks_for_its_own()
    {
        var mover = new Mover();
        var commands = WindowCommands.Create(mover);

        Assert.Equal(Enum.GetValues<WindowLayout>().Length, commands.Count);
        Assert.All(commands, command =>
        {
            Assert.StartsWith(WindowCommands.IdPrefix, command.Id);
            Assert.Null(command.Argument);
            Assert.False(command.KeepOpen);
            command.Run!();
        });
        Assert.Equal(Enum.GetValues<WindowLayout>(), mover.Asked);
        Assert.Equal(commands.Count, commands.Select(command => command.Id).Distinct().Count());
        Assert.Equal(commands.Count, commands.Select(command => command.Title).Distinct().Count());
    }

    [Fact]
    public void A_command_s_id_says_which_layout_it_is_and_no_other_id_does()
    {
        Assert.Equal(WindowLayout.LeftHalf, WindowCommands.LayoutOf("window-left-half"));
        Assert.Equal(WindowLayout.PreviousDisplay, WindowCommands.LayoutOf("window-previous-display"));
        Assert.All(WindowCommands.Create(new Mover()), command => Assert.NotNull(WindowCommands.LayoutOf(command.Id)));
        Assert.Null(WindowCommands.LayoutOf("lock-screen"));
        Assert.Null(WindowCommands.LayoutOf("window-"));
        Assert.Null(WindowCommands.LayoutOf("Window-Left-Half"));
    }

    [Theory]
    [InlineData(WindowMove.Done, null)]
    [InlineData(WindowMove.NoWindow, "No Window in Front")]
    [InlineData(WindowMove.Refused, "That Window Cannot Be Moved")]
    [InlineData(WindowMove.OneDisplay, "There Is Only One Display")]
    public void A_window_that_was_not_moved_is_said_and_one_that_was_is_not(WindowMove answer, string? words)
    {
        var said = new List<string>();
        var commands = WindowCommands.Create(new Mover { Answer = answer }, said.Add);

        commands[0].Run!();

        Assert.Equal(words is null ? new List<string>() : new List<string> { words }, said);
    }

    [Fact]
    public void They_are_found_by_name()
    {
        var provider = new CommandProvider(WindowCommands.Create(new Mover()));

        Assert.Equal("command:window-left-half", provider.Search("left half")[0].Item.Id);
        Assert.Equal("command:window-maximize", provider.Search("maximize")[0].Item.Id);
        Assert.All(provider.Browse(), item => Assert.Equal(ItemKind.Command, item.Kind));
    }

    [Theory]
    [InlineData(WindowLayout.LeftHalf, 0, 0, 960, 1032)]
    [InlineData(WindowLayout.RightHalf, 960, 0, 960, 1032)]
    [InlineData(WindowLayout.TopHalf, 0, 0, 1920, 516)]
    [InlineData(WindowLayout.BottomHalf, 0, 516, 1920, 516)]
    [InlineData(WindowLayout.TopLeftQuarter, 0, 0, 960, 516)]
    [InlineData(WindowLayout.TopRightQuarter, 960, 0, 960, 516)]
    [InlineData(WindowLayout.BottomLeftQuarter, 0, 516, 960, 516)]
    [InlineData(WindowLayout.BottomRightQuarter, 960, 516, 960, 516)]
    [InlineData(WindowLayout.FirstThird, 0, 0, 640, 1032)]
    [InlineData(WindowLayout.CenterThird, 640, 0, 640, 1032)]
    [InlineData(WindowLayout.LastThird, 1280, 0, 640, 1032)]
    [InlineData(WindowLayout.FirstTwoThirds, 0, 0, 1280, 1032)]
    [InlineData(WindowLayout.LastTwoThirds, 640, 0, 1280, 1032)]
    [InlineData(WindowLayout.AlmostMaximize, 96, 52, 1728, 928)]
    [InlineData(WindowLayout.MaximizeHeight, 300, 0, 800, 1032)]
    [InlineData(WindowLayout.MaximizeWidth, 0, 200, 1920, 600)]
    [InlineData(WindowLayout.Center, 560, 216, 800, 600)]
    public void Where_each_layout_puts_the_window(WindowLayout layout, int left, int top, int width, int height) =>
        Assert.Equal(new ScreenRect(left, top, width, height), WindowCommands.Place(layout, Work, Window));

    [Theory]
    [InlineData(WindowLayout.Maximize)]
    [InlineData(WindowLayout.Restore)]
    [InlineData(WindowLayout.NextDisplay)]
    [InlineData(WindowLayout.PreviousDisplay)]
    public void What_is_not_a_place_on_the_screen_has_none(WindowLayout layout) =>
        Assert.Null(WindowCommands.Place(layout, Work, Window));

    [Fact]
    public void A_screen_that_does_not_start_at_nought_keeps_its_own_corner()
    {
        // A second display to the left of the first and a little lower, with its taskbar at the top.
        var work = new ScreenRect(-1280, 148, 1280, 976);

        Assert.Equal(new ScreenRect(-1280, 148, 640, 976), WindowCommands.Place(WindowLayout.LeftHalf, work, Window));
        Assert.Equal(new ScreenRect(-640, 636, 640, 488), WindowCommands.Place(WindowLayout.BottomRightQuarter, work, Window));
    }

    [Fact]
    public void Halves_quarters_and_thirds_leave_no_gap_and_do_not_overlap_on_a_screen_of_any_size()
    {
        foreach (var work in new[] { new ScreenRect(0, 0, 1921, 1033), new ScreenRect(7, 3, 1366, 727), new ScreenRect(-5, -5, 1000, 1001), new ScreenRect(0, 0, 3440, 1392) })
        {
            ScreenRect At(WindowLayout layout) => WindowCommands.Place(layout, work, Window)!.Value;

            // Left and right.
            Assert.Equal(work.Left, At(WindowLayout.LeftHalf).Left);
            Assert.Equal(At(WindowLayout.LeftHalf).Right, At(WindowLayout.RightHalf).Left);
            Assert.Equal(work.Right, At(WindowLayout.RightHalf).Right);

            // Top and bottom.
            Assert.Equal(work.Top, At(WindowLayout.TopHalf).Top);
            Assert.Equal(At(WindowLayout.TopHalf).Bottom, At(WindowLayout.BottomHalf).Top);
            Assert.Equal(work.Bottom, At(WindowLayout.BottomHalf).Bottom);

            // The four quarters are the halves cut again.
            Assert.Equal(At(WindowLayout.LeftHalf).Width, At(WindowLayout.TopLeftQuarter).Width);
            Assert.Equal(At(WindowLayout.TopLeftQuarter).Right, At(WindowLayout.TopRightQuarter).Left);
            Assert.Equal(At(WindowLayout.TopLeftQuarter).Bottom, At(WindowLayout.BottomLeftQuarter).Top);
            Assert.Equal(work.Right, At(WindowLayout.BottomRightQuarter).Right);
            Assert.Equal(work.Bottom, At(WindowLayout.BottomRightQuarter).Bottom);

            // The three thirds, and the two that are two of them.
            Assert.Equal(work.Left, At(WindowLayout.FirstThird).Left);
            Assert.Equal(At(WindowLayout.FirstThird).Right, At(WindowLayout.CenterThird).Left);
            Assert.Equal(At(WindowLayout.CenterThird).Right, At(WindowLayout.LastThird).Left);
            Assert.Equal(work.Right, At(WindowLayout.LastThird).Right);
            Assert.Equal(At(WindowLayout.CenterThird).Right, At(WindowLayout.FirstTwoThirds).Right);
            Assert.Equal(At(WindowLayout.FirstThird).Right, At(WindowLayout.LastTwoThirds).Left);
            Assert.Equal(work.Right, At(WindowLayout.LastTwoThirds).Right);

            // Every one of them is on the screen and as tall as it says.
            foreach (var layout in Enum.GetValues<WindowLayout>())
            {
                if (WindowCommands.Place(layout, work, Window) is { } place)
                {
                    Assert.True(place.Left >= work.Left && place.Top >= work.Top && place.Right <= work.Right && place.Bottom <= work.Bottom, layout.ToString());
                    Assert.True(place.Width > 0 && place.Height > 0, layout.ToString());
                }
            }
        }
    }

    [Fact]
    public void A_window_larger_than_its_screen_is_cut_to_it_where_it_keeps_its_size()
    {
        var huge = new ScreenRect(-200, -100, 4000, 3000);

        Assert.Equal(Work, WindowCommands.Place(WindowLayout.Center, Work, huge));
        Assert.Equal(Work, WindowCommands.Place(WindowLayout.MaximizeHeight, Work, huge));
        Assert.Equal(Work, WindowCommands.Place(WindowLayout.MaximizeWidth, Work, huge));
    }

    [Fact]
    public void A_window_that_reaches_over_the_edge_is_brought_onto_the_screen_when_it_is_made_as_tall_or_as_wide_as_it()
    {
        var off = new ScreenRect(1500, 800, 800, 600);

        Assert.Equal(new ScreenRect(1120, 0, 800, 1032), WindowCommands.Place(WindowLayout.MaximizeHeight, Work, off));
        Assert.Equal(new ScreenRect(0, 432, 1920, 600), WindowCommands.Place(WindowLayout.MaximizeWidth, Work, off));
    }

    [Fact]
    public void A_window_that_keeps_its_size_goes_to_the_middle_of_the_place_and_stays_on_the_screen()
    {
        var fixedSize = new ScreenRect(50, 50, 400, 300);
        var left = WindowCommands.Place(WindowLayout.LeftHalf, Work, fixedSize)!.Value;
        var corner = WindowCommands.Place(WindowLayout.BottomRightQuarter, Work, fixedSize)!.Value;

        Assert.Equal(new ScreenRect(280, 366, 400, 300), WindowCommands.Fitted(left, Work, fixedSize));
        Assert.Equal(new ScreenRect(1240, 624, 400, 300), WindowCommands.Fitted(corner, Work, fixedSize));

        // Wider than the place: it reaches out of the place, but not off the screen.
        var wide = new ScreenRect(50, 50, 1400, 300);
        Assert.Equal(new ScreenRect(0, 366, 1400, 300), WindowCommands.Fitted(left, Work, wide));
        Assert.Equal(new ScreenRect(520, 624, 1400, 300), WindowCommands.Fitted(corner, Work, wide));
    }

    [Fact]
    public void On_another_display_the_window_takes_the_same_part_of_it()
    {
        var from = new ScreenRect(0, 0, 1920, 1032);
        var to = new ScreenRect(1920, 0, 2560, 1392);

        // The left half is the left half there.
        Assert.Equal(new ScreenRect(1920, 0, 1280, 1392), WindowCommands.OnDisplay(new ScreenRect(0, 0, 960, 1032), from, to));

        // The whole is the whole.
        Assert.Equal(to, WindowCommands.OnDisplay(from, from, to));

        // And back again to a smaller one.
        Assert.Equal(new ScreenRect(960, 516, 960, 516), WindowCommands.OnDisplay(new ScreenRect(3200, 696, 1280, 696), to, from));
    }

    [Fact]
    public void On_another_display_a_window_never_reaches_over_its_edge()
    {
        var from = new ScreenRect(0, 0, 1920, 1032);
        var to = new ScreenRect(-1280, 100, 1280, 976);

        // It hung over the right and bottom edges of the display it was on.
        var hanging = WindowCommands.OnDisplay(new ScreenRect(1500, 800, 800, 600), from, to);
        Assert.True(hanging.Left >= to.Left && hanging.Top >= to.Top && hanging.Right <= to.Right && hanging.Bottom <= to.Bottom);

        // It was larger than the display it was on.
        Assert.Equal(to, WindowCommands.OnDisplay(new ScreenRect(-100, -100, 4000, 3000), from, to));

        // A display that says it has no size is not divided by.
        Assert.Equal(new ScreenRect(-1040, 288, 800, 600), WindowCommands.OnDisplay(new ScreenRect(10, 10, 800, 600), new ScreenRect(0, 0, 0, 0), to));
    }

    [Fact]
    public void The_next_display_is_the_one_to_the_right_and_after_the_last_comes_the_first()
    {
        // As Windows lists them: the main one first, then one to its left, then one to its right.
        ScreenRect[] displays = [new(0, 0, 1920, 1080), new(-1920, 0, 1920, 1080), new(1920, 0, 2560, 1440)];

        Assert.Equal(2, WindowCommands.OtherDisplay(displays, 0, next: true));
        Assert.Equal(1, WindowCommands.OtherDisplay(displays, 2, next: true));
        Assert.Equal(0, WindowCommands.OtherDisplay(displays, 1, next: true));

        Assert.Equal(1, WindowCommands.OtherDisplay(displays, 0, next: false));
        Assert.Equal(2, WindowCommands.OtherDisplay(displays, 1, next: false));
        Assert.Equal(0, WindowCommands.OtherDisplay(displays, 2, next: false));
    }

    [Fact]
    public void Displays_one_above_the_other_go_from_top_to_bottom()
    {
        ScreenRect[] displays = [new(0, 0, 1920, 1080), new(0, -1080, 1920, 1080)];

        Assert.Equal(0, WindowCommands.OtherDisplay(displays, 1, next: true));
        Assert.Equal(1, WindowCommands.OtherDisplay(displays, 0, next: true));
    }

    [Fact]
    public void With_one_display_or_a_window_on_none_there_is_no_other()
    {
        ScreenRect[] one = [new(0, 0, 1920, 1080)];
        ScreenRect[] two = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];

        Assert.Null(WindowCommands.OtherDisplay(one, 0, next: true));
        Assert.Null(WindowCommands.OtherDisplay([], 0, next: true));
        Assert.Null(WindowCommands.OtherDisplay(two, -1, next: true));
        Assert.Null(WindowCommands.OtherDisplay(two, 2, next: false));
    }
}
