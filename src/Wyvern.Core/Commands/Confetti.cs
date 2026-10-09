namespace Wyvern.Core.Commands;

/// <summary>One piece of confetti as it is now: where its middle is, how it is turned, and how large it is drawn.</summary>
/// <param name="Angle">How far it is turned, in degrees.</param>
/// <param name="Width">Its width as it is seen now: a piece that tumbles shows more or less of itself.</param>
/// <param name="Colour">Which of the colours it has, by its place in the list of them.</param>
public readonly record struct ConfettiPiece(double X, double Y, double Angle, double Width, double Height, int Colour);

/// <summary>
/// A throw of confetti over a screen, worked out and not drawn: two bursts leave the bottom
/// corners, fly up and towards the middle, and flutter down again. Whoever draws it calls
/// <see cref="Step"/> with the time that has passed and draws <see cref="Pieces"/>.
/// </summary>
/// <remarks>
/// The screen's top left corner is nought, nought, and down is larger. Speeds are in parts of
/// the screen, so that a throw looks the same on a small screen and on a large one.
/// </remarks>
public sealed class Confetti
{
    /// <summary>How many colours a piece can have.</summary>
    public const int Colours = 7;

    /// <summary>How many pieces a throw has unless it is told otherwise.</summary>
    public const int DefaultPieces = 240;

    /// <summary>How long a throw lasts at most, in seconds. Whatever is still in the air then is gone.</summary>
    public const double MaxSeconds = 5.0;

    // How long the last pieces take to fade, in seconds, at the end of the longest throw.
    private const double FadeSeconds = 0.7;

    private readonly double _width;
    private readonly double _height;
    private readonly Flying[] _flying;
    private readonly ConfettiPiece[] _pieces;
    private double _seconds;
    private int _shown;
    private int _gone;

    /// <param name="width">The width of the screen it is thrown over.</param>
    /// <param name="height">The height of that screen.</param>
    /// <param name="seed">What the throw is made from: the same number gives the same throw.</param>
    public Confetti(double width, double height, int seed, int pieces = DefaultPieces)
    {
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        var random = new Random(seed);
        _flying = new Flying[Math.Max(pieces, 0)];
        _pieces = new ConfettiPiece[_flying.Length];

        // Down, in screen heights a second, each second.
        double gravity = 2.4 * _height;
        for (int i = 0; i < _flying.Length; i++)
        {
            bool fromLeft = i % 2 == 0;

            // How high it gets, and so how fast it leaves: a piece thrown up at a speed v
            // reaches v * v / (2 * gravity).
            double top = _height * (0.35 + (0.6 * random.NextDouble()));
            double up = Math.Sqrt(2 * gravity * top);
            double across = _width * (0.12 + (0.62 * random.NextDouble()));
            double size = Math.Max(_height / 110, 6) * (0.7 + (0.8 * random.NextDouble()));
            _flying[i] = new Flying
            {
                X = fromLeft ? 0 : _width,
                Y = _height,
                SpeedX = fromLeft ? across : -across,
                SpeedY = -up,
                Gravity = gravity,

                // A piece of paper does not fall like a stone: it comes down at a speed of its own.
                FallSpeed = _height * (0.16 + (0.2 * random.NextDouble())),
                Angle = 360 * random.NextDouble(),
                Spin = (random.NextDouble() - 0.5) * 720,
                Tumble = 2 * Math.PI * random.NextDouble(),
                TumbleSpeed = 4 + (9 * random.NextDouble()),
                Width = size,
                Height = size * (0.45 + (0.35 * random.NextDouble())),
                Colour = random.Next(Colours),

                // The two bursts are not one instant: each piece leaves within the first moments.
                Delay = 0.18 * random.NextDouble(),
            };
        }

        Show();
    }

    /// <summary>The pieces that are in the air and on the screen now.</summary>
    public ReadOnlySpan<ConfettiPiece> Pieces => _pieces.AsSpan(0, _shown);

    /// <summary>How much of themselves the pieces show now, from 1 down to 0 as the throw ends.</summary>
    public double Opacity => Math.Clamp((MaxSeconds - _seconds) / FadeSeconds, 0, 1);

    /// <summary>True when nothing is left to draw.</summary>
    public bool IsOver => _gone == _flying.Length;

    /// <summary>Lets the given time pass. A time that is not a positive number passes nothing.</summary>
    /// <returns>False when the throw is over.</returns>
    public bool Step(double seconds)
    {
        if (!(seconds > 0) || double.IsInfinity(seconds))
        {
            return !IsOver;
        }

        // A long gap between two frames is taken in small steps, so that a piece does not
        // jump through the top of its flight.
        double left = Math.Min(seconds, MaxSeconds);
        while (left > 0)
        {
            double step = Math.Min(left, 1.0 / 60);
            Advance(step);
            left -= step;
        }

        Show();
        return !IsOver;
    }

    private void Advance(double step)
    {
        _seconds += step;
        for (int i = 0; i < _flying.Length; i++)
        {
            ref Flying piece = ref _flying[i];
            if (piece.Gone || _seconds < piece.Delay)
            {
                continue;
            }

            // The air slows it sideways, and holds it to its own speed once it is falling.
            piece.SpeedX *= Math.Exp(-1.1 * step);
            piece.SpeedY = Math.Min(piece.SpeedY + (piece.Gravity * step), piece.FallSpeed);
            piece.X += piece.SpeedX * step;
            piece.Y += piece.SpeedY * step;
            piece.Angle = (piece.Angle + (piece.Spin * step)) % 360;
            piece.Tumble += piece.TumbleSpeed * step;

            // Below the bottom edge on its way down, or when the time is up, it is gone.
            if ((piece.SpeedY > 0 && piece.Y - piece.Width > _height) || _seconds >= MaxSeconds)
            {
                piece.Gone = true;
                _gone++;
            }
        }
    }

    private void Show()
    {
        _shown = 0;
        for (int i = 0; i < _flying.Length; i++)
        {
            ref Flying piece = ref _flying[i];
            if (piece.Gone || _seconds < piece.Delay)
            {
                continue;
            }

            _pieces[_shown++] = new ConfettiPiece(
                piece.X,
                piece.Y,
                piece.Angle,
                piece.Width * Math.Max(Math.Abs(Math.Cos(piece.Tumble)), 0.15),
                piece.Height,
                piece.Colour);
        }
    }

    private struct Flying
    {
        public double X;
        public double Y;
        public double SpeedX;
        public double SpeedY;
        public double Gravity;
        public double FallSpeed;
        public double Angle;
        public double Spin;
        public double Tumble;
        public double TumbleSpeed;
        public double Width;
        public double Height;
        public int Colour;
        public double Delay;
        public bool Gone;
    }
}
