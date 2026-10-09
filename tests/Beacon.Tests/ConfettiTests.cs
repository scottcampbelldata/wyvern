using Beacon.Core.Commands;

namespace Beacon.Tests;

/// <summary>A throw of confetti, worked out: where the pieces are as time passes. Nothing is drawn here.</summary>
public class ConfettiTests
{
    private const double Width = 1920;
    private const double Height = 1080;
    private const double Frame = 1.0 / 60;

    [Fact]
    public void A_throw_starts_in_the_two_bottom_corners()
    {
        var confetti = new Confetti(Width, Height, seed: 7);

        // A moment in, the first pieces have left and are still close to where they started.
        confetti.Step(Frame);

        Assert.False(confetti.IsOver);
        Assert.NotEqual(0, confetti.Pieces.Length);
        bool left = false;
        bool right = false;
        foreach (var piece in confetti.Pieces)
        {
            Assert.True(piece.Y > Height * 0.9, "A piece started away from the bottom.");
            Assert.True(piece.X < Width * 0.05 || piece.X > Width * 0.95, "A piece started away from the corners.");
            left |= piece.X < Width / 2;
            right |= piece.X > Width / 2;
        }

        Assert.True(left && right);
    }

    [Fact]
    public void The_pieces_fly_up_and_towards_the_middle_and_come_down_again()
    {
        var confetti = new Confetti(Width, Height, seed: 7);
        double highest = Height;
        double lowestAfterTheTop = 0;
        bool reachedTheMiddle = false;
        for (double t = 0; t < 2.5; t += Frame)
        {
            confetti.Step(Frame);
            double mean = 0;
            foreach (var piece in confetti.Pieces)
            {
                mean += piece.Y;
                reachedTheMiddle |= piece.X > Width * 0.3 && piece.X < Width * 0.7;
            }

            if (confetti.Pieces.Length > 0)
            {
                mean /= confetti.Pieces.Length;
                if (mean < highest)
                {
                    highest = mean;
                    lowestAfterTheTop = mean;
                }
                else
                {
                    lowestAfterTheTop = Math.Max(lowestAfterTheTop, mean);
                }
            }
        }

        // On the whole they got well up the screen, and were lower again afterwards.
        Assert.True(highest < Height * 0.6, $"The pieces only got to {highest}.");
        Assert.True(lowestAfterTheTop > highest + (Height * 0.1), "The pieces did not come down again.");
        Assert.True(reachedTheMiddle);
    }

    [Fact]
    public void A_throw_is_over_within_its_longest_time_and_shows_nothing_after()
    {
        var confetti = new Confetti(Width, Height, seed: 3);
        double passed = 0;
        while (confetti.Step(Frame))
        {
            passed += Frame;
            Assert.True(passed <= Confetti.MaxSeconds + (2 * Frame), "The throw did not end.");
        }

        Assert.True(confetti.IsOver);
        Assert.Equal(0, confetti.Pieces.Length);
        Assert.True(passed > 1.5, $"The throw was over after {passed} seconds.");

        // Time that passes afterwards changes nothing.
        Assert.False(confetti.Step(1));
        Assert.Equal(0, confetti.Pieces.Length);
    }

    [Fact]
    public void The_pieces_fade_only_at_the_very_end()
    {
        var confetti = new Confetti(Width, Height, seed: 3);
        Assert.Equal(1, confetti.Opacity);

        confetti.Step(2);
        Assert.Equal(1, confetti.Opacity);

        confetti.Step(Confetti.MaxSeconds - 2 - 0.2);
        Assert.InRange(confetti.Opacity, 0.05, 0.6);

        confetti.Step(1);
        Assert.Equal(0, confetti.Opacity);
    }

    [Fact]
    public void No_piece_is_ever_far_off_the_screen_or_misshapen()
    {
        var confetti = new Confetti(Width, Height, seed: 11);
        while (confetti.Step(Frame))
        {
            foreach (var piece in confetti.Pieces)
            {
                Assert.InRange(piece.X, -Width * 0.25, Width * 1.25);
                Assert.InRange(piece.Y, -Height * 0.25, Height * 1.1);
                Assert.True(piece.Width > 0 && piece.Height > 0);
                Assert.True(piece.Width < Height / 20 && piece.Height < Height / 20);
                Assert.InRange(piece.Colour, 0, Confetti.Colours - 1);
                Assert.InRange(piece.Angle, -360, 360);
            }
        }
    }

    [Fact]
    public void The_same_number_gives_the_same_throw_and_another_number_another()
    {
        var one = new Confetti(Width, Height, seed: 5);
        var same = new Confetti(Width, Height, seed: 5);
        var other = new Confetti(Width, Height, seed: 6);
        one.Step(0.5);
        same.Step(0.5);
        other.Step(0.5);

        Assert.True(one.Pieces.SequenceEqual(same.Pieces));
        Assert.False(one.Pieces.SequenceEqual(other.Pieces));
    }

    [Fact]
    public void One_long_gap_between_two_frames_comes_to_the_same_as_many_short_ones()
    {
        var smooth = new Confetti(Width, Height, seed: 9);
        var jerky = new Confetti(Width, Height, seed: 9);
        for (int i = 0; i < 60; i++)
        {
            smooth.Step(Frame);
        }

        jerky.Step(1);

        Assert.Equal(smooth.Pieces.Length, jerky.Pieces.Length);
        for (int i = 0; i < smooth.Pieces.Length; i++)
        {
            Assert.Equal(smooth.Pieces[i].X, jerky.Pieces[i].X, 3);
            Assert.Equal(smooth.Pieces[i].Y, jerky.Pieces[i].Y, 3);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Time_that_is_not_a_positive_number_passes_nothing(double seconds)
    {
        var confetti = new Confetti(Width, Height, seed: 1);
        confetti.Step(0.5);
        var before = confetti.Pieces.ToArray();

        Assert.True(confetti.Step(seconds));

        Assert.True(before.AsSpan().SequenceEqual(confetti.Pieces));
    }

    [Fact]
    public void A_throw_of_one_piece_is_not_over_before_the_piece_has_left()
    {
        // One piece, which leaves a moment after the start.
        var confetti = new Confetti(Width, Height, seed: 2, pieces: 1);

        Assert.False(confetti.IsOver);
        Assert.True(confetti.Step(0.001));
        Assert.False(confetti.IsOver);
    }

    [Fact]
    public void A_throw_of_nothing_is_over_at_once_and_a_screen_of_no_size_does_not_break_it()
    {
        Assert.True(new Confetti(Width, Height, seed: 1, pieces: 0).IsOver);
        Assert.True(new Confetti(Width, Height, seed: 1, pieces: -5).IsOver);

        var tiny = new Confetti(0, 0, seed: 1);
        while (tiny.Step(Frame))
        {
        }

        Assert.True(tiny.IsOver);
    }
}
