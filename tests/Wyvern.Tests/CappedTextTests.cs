using Wyvern.Core.Scripts;

namespace Wyvern.Tests;

public class CappedTextTests
{
    [Fact]
    public void Text_under_the_limit_is_kept_whole()
    {
        var text = new CappedText(10);

        text.Append("abc");
        text.Append("def");

        Assert.Equal("abcdef", text.ToString());
    }

    [Fact]
    public void Nothing_added_gives_an_empty_text()
    {
        Assert.Equal("", new CappedText(10).ToString());
    }

    [Fact]
    public void Text_exactly_at_the_limit_is_kept_whole()
    {
        var text = new CappedText(6);

        text.Append("abcdef");

        Assert.Equal("abcdef", text.ToString());
    }

    [Fact]
    public void Over_the_limit_the_oldest_part_is_dropped_and_the_newest_kept()
    {
        var text = new CappedText(5);

        text.Append("abcdefgh");

        Assert.Equal("defgh", text.ToString());
    }

    [Fact]
    public void However_much_is_added_and_in_whatever_pieces_the_newest_part_is_what_is_kept()
    {
        var text = new CappedText(7);

        for (int i = 0; i < 1000; i++)
        {
            text.Append($"<{i}>");
        }

        Assert.Equal("8><999>", text.ToString());
    }

    [Fact]
    public void What_is_held_in_memory_stays_within_twice_the_limit_however_much_is_added()
    {
        var text = new CappedText(100);

        for (int i = 0; i < 10_000; i++)
        {
            text.Append("0123456789");
            Assert.True(text.HeldChars <= 200, $"{text.HeldChars} characters held after {i + 1} pieces");
        }

        Assert.Equal(100, text.ToString().Length);
    }

    [Fact]
    public void One_piece_many_times_the_limit_is_cut_to_its_end()
    {
        var text = new CappedText(4);

        text.Append(new string('x', 10_000) + "tail");

        Assert.Equal("tail", text.ToString());
    }

    [Fact]
    public void The_kept_part_never_starts_with_the_second_half_of_a_two_part_character()
    {
        // The emoji takes two positions. A limit of four would keep its second half and "abc".
        var text = new CappedText(4);

        text.Append("zz\U0001F600abc");

        Assert.Equal("abc", text.ToString());
    }

    [Fact]
    public void That_holds_after_the_oldest_part_has_really_been_thrown_away_too()
    {
        // Enough is added for the stored text to be cut down, and the cut falls inside the emoji.
        var text = new CappedText(4);

        text.Append(new string('z', 20) + "\U0001F600abc");

        Assert.Equal("abc", text.ToString());
    }

    [Fact]
    public void A_two_part_character_that_fits_is_kept_whole()
    {
        var text = new CappedText(5);

        text.Append("zz\U0001F600abc");

        Assert.Equal("\U0001F600abc", text.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_must_be_at_least_one(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CappedText(limit));
    }

    [Fact]
    public async Task One_thread_may_add_while_another_reads()
    {
        var text = new CappedText(1000);
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Assert.True(text.ToString().Length <= 1000);
            }
        });

        for (int i = 0; i < 20_000; i++)
        {
            text.Append("0123456789");
        }

        stop.Cancel();
        await reader;

        string kept = text.ToString();
        Assert.Equal(1000, kept.Length);
        Assert.EndsWith("0123456789", kept);
    }
}
