using Wyvern.Core.ClipboardHistory;
using Wyvern.Core.Platform;

namespace Wyvern.Tests;

public class CaptureRulesTests
{
    private static readonly ClipboardOwner Notepad = new("notepad", IsWyvern: false);
    private static readonly byte[] Zero = [0, 0, 0, 0];
    private static readonly byte[] One = [1, 0, 0, 0];

    private static CaptureVerdict Judge(
        ClipboardOwner? owner = null,
        bool keptOut = false,
        byte[]? canInclude = null,
        byte[]? canUpload = null,
        Func<string?, bool>? excludes = null) =>
        CaptureRules.Judge(owner ?? Notepad, keptOut, canInclude, canUpload, excludes ?? (_ => false));

    [Fact]
    public void An_ordinary_copy_is_kept()
    {
        Assert.Equal(CaptureVerdict.Keep, Judge());
    }

    [Fact]
    public void Markers_that_say_yes_do_not_stop_a_copy_being_kept()
    {
        Assert.Equal(CaptureVerdict.Keep, Judge(canInclude: One, canUpload: One));
    }

    [Fact]
    public void What_Wyvern_wrote_itself_is_its_own_write()
    {
        Assert.Equal(CaptureVerdict.OwnWrite, Judge(owner: new ClipboardOwner(null, IsWyvern: true)));
    }

    [Fact]
    public void The_format_that_tells_monitors_to_leave_it_alone_makes_it_private()
    {
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(keptOut: true));
    }

    [Fact]
    public void Do_not_put_in_a_history_makes_it_private()
    {
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canInclude: Zero));
    }

    [Fact]
    public void Do_not_send_to_another_device_makes_it_private_because_the_history_is_synced()
    {
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canUpload: Zero));
    }

    [Fact]
    public void A_marker_cut_short_is_taken_to_say_no()
    {
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canInclude: [1]));
    }

    // What the capture passes for a marker that is there but that it could not read is an
    // empty block, and that must keep the copy out of the history.
    [Fact]
    public void A_marker_that_is_there_but_empty_makes_it_private_for_each_of_the_two_markers()
    {
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canInclude: []));
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canUpload: []));
    }

    [Fact]
    public void A_marker_that_holds_a_number_other_than_0_and_1_is_in_doubt_and_makes_it_private()
    {
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canInclude: [2, 0, 0, 0]));
        Assert.Equal(CaptureVerdict.MarkedPrivate, Judge(canUpload: [0x30, 0, 0, 0]));
    }

    [Fact]
    public void A_program_the_owner_excluded_is_excluded_and_is_asked_about_by_the_name_Windows_gave()
    {
        var asked = new List<string?>();

        var verdict = Judge(owner: new ClipboardOwner("KeePass", IsWyvern: false), excludes: program =>
        {
            asked.Add(program);
            return true;
        });

        Assert.Equal(CaptureVerdict.ExcludedProgram, verdict);
        Assert.Equal(["KeePass"], asked);
    }

    [Fact]
    public void A_program_that_is_not_known_is_still_asked_about()
    {
        var asked = new List<string?>();

        Judge(owner: new ClipboardOwner(null, IsWyvern: false), excludes: program =>
        {
            asked.Add(program);
            return false;
        });

        Assert.Equal([null], asked);
    }

    [Fact]
    public void Wyverns_own_write_comes_before_every_other_rule()
    {
        var verdict = Judge(owner: new ClipboardOwner("wyvern", IsWyvern: true), keptOut: true, canInclude: Zero, excludes: _ => true);

        Assert.Equal(CaptureVerdict.OwnWrite, verdict);
    }

    [Fact]
    public void A_private_mark_comes_before_the_excluded_programs_which_are_then_not_asked()
    {
        bool asked = false;

        var verdict = Judge(keptOut: true, excludes: _ =>
        {
            asked = true;
            return true;
        });

        Assert.Equal(CaptureVerdict.MarkedPrivate, verdict);
        Assert.False(asked);
    }
}
