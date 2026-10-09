namespace Wyvern.Core.Commands;

/// <summary>A place on the screens, in pixels: a window's, or the part of a screen that windows may take.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>Where a window can be put.</summary>
public enum WindowLayout
{
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,
    TopLeftQuarter,
    TopRightQuarter,
    BottomLeftQuarter,
    BottomRightQuarter,
    FirstThird,
    CenterThird,
    LastThird,
    FirstTwoThirds,
    LastTwoThirds,
    Maximize,
    AlmostMaximize,
    MaximizeHeight,
    MaximizeWidth,
    Center,
    Restore,
    NextDisplay,
    PreviousDisplay,
}

/// <summary>How putting a window somewhere went.</summary>
public enum WindowMove
{
    /// <summary>The window is where it was asked to be.</summary>
    Done,

    /// <summary>No window was in front when the launcher opened, or it has gone since.</summary>
    NoWindow,

    /// <summary>Windows would not move it: it belongs to a program that runs with more rights than Wyvern.</summary>
    Refused,

    /// <summary>It was to go to another display, and there is only one.</summary>
    OneDisplay,
}

/// <summary>Moves the window the owner was working in when the launcher opened. Implemented with Windows calls in the app.</summary>
public interface IWindowMover
{
    WindowMove Place(WindowLayout layout);
}

/// <summary>The launcher's commands for the window in front, and where each puts it.</summary>
public static class WindowCommands
{
    /// <summary>Said when there is no window to move.</summary>
    public const string NoWindow = "No Window in Front";

    /// <summary>Said when Windows would not move the window.</summary>
    public const string Refused = "That Window Cannot Be Moved";

    /// <summary>Said when the window was to go to another display and there is only one.</summary>
    public const string OneDisplay = "There Is Only One Display";

    private static readonly (WindowLayout Layout, string Id, string Title, string Subtitle)[] All =
    [
        (WindowLayout.LeftHalf, "window-left-half", "Left Half", "Put the window in front on the left half of its screen"),
        (WindowLayout.RightHalf, "window-right-half", "Right Half", "Put the window in front on the right half of its screen"),
        (WindowLayout.TopHalf, "window-top-half", "Top Half", "Put the window in front on the top half of its screen"),
        (WindowLayout.BottomHalf, "window-bottom-half", "Bottom Half", "Put the window in front on the bottom half of its screen"),
        (WindowLayout.TopLeftQuarter, "window-top-left-quarter", "Top Left Quarter", "Put the window in front in the top left corner of its screen"),
        (WindowLayout.TopRightQuarter, "window-top-right-quarter", "Top Right Quarter", "Put the window in front in the top right corner of its screen"),
        (WindowLayout.BottomLeftQuarter, "window-bottom-left-quarter", "Bottom Left Quarter", "Put the window in front in the bottom left corner of its screen"),
        (WindowLayout.BottomRightQuarter, "window-bottom-right-quarter", "Bottom Right Quarter", "Put the window in front in the bottom right corner of its screen"),
        (WindowLayout.FirstThird, "window-first-third", "First Third", "Put the window in front on the left third of its screen"),
        (WindowLayout.CenterThird, "window-center-third", "Center Third", "Put the window in front on the middle third of its screen"),
        (WindowLayout.LastThird, "window-last-third", "Last Third", "Put the window in front on the right third of its screen"),
        (WindowLayout.FirstTwoThirds, "window-first-two-thirds", "First Two Thirds", "Put the window in front on the left two thirds of its screen"),
        (WindowLayout.LastTwoThirds, "window-last-two-thirds", "Last Two Thirds", "Put the window in front on the right two thirds of its screen"),
        (WindowLayout.Maximize, "window-maximize", "Maximize", "Make the window in front fill its screen"),
        (WindowLayout.AlmostMaximize, "window-almost-maximize", "Almost Maximize", "Make the window in front fill most of its screen"),
        (WindowLayout.MaximizeHeight, "window-maximize-height", "Maximize Height", "Make the window in front as tall as its screen"),
        (WindowLayout.MaximizeWidth, "window-maximize-width", "Maximize Width", "Make the window in front as wide as its screen"),
        (WindowLayout.Center, "window-center", "Center", "Put the window in front in the middle of its screen"),
        (WindowLayout.Restore, "window-restore", "Restore", "Put the window in front back where it was before Wyvern moved it"),
        (WindowLayout.NextDisplay, "window-next-display", "Next Display", "Move the window in front to the next display"),
        (WindowLayout.PreviousDisplay, "window-previous-display", "Previous Display", "Move the window in front to the display before"),
    ];

    /// <summary>What every one of these commands' ids starts with, after "command:".</summary>
    public const string IdPrefix = "window-";

    /// <summary>The layout a command of these puts the window in, by the command's id, or null for any other id.</summary>
    public static WindowLayout? LayoutOf(string commandId)
    {
        foreach (var (layout, id, _, _) in All)
        {
            if (id == commandId)
            {
                return layout;
            }
        }

        return null;
    }

    /// <param name="mover">Moves the window.</param>
    /// <param name="say">Shows a few fixed words for a moment, when the window was not moved. Left out by code that shows nothing.</param>
    public static IReadOnlyList<BuiltInCommand> Create(IWindowMover mover, Action<string>? say = null)
    {
        say ??= _ => { };
        return All
            .Select(one => new BuiltInCommand(one.Id, one.Title, one.Subtitle, () =>
            {
                switch (mover.Place(one.Layout))
                {
                    case WindowMove.NoWindow:
                        say(NoWindow);
                        break;
                    case WindowMove.Refused:
                        say(Refused);
                        break;
                    case WindowMove.OneDisplay:
                        say(OneDisplay);
                        break;
                }
            }))
            .ToList();
    }

    /// <summary>
    /// Where a layout puts a window: the place for the edge of the window that is seen, in the
    /// part of its screen that windows may take. Halves, quarters and thirds leave no gap and
    /// do not overlap: where the screen does not divide evenly, the right or the bottom piece
    /// takes the pixel that is left over. Null for <see cref="WindowLayout.Maximize"/>,
    /// <see cref="WindowLayout.Restore"/> and the two that go to another display, which are
    /// not places on this screen.
    /// </summary>
    /// <param name="work">The part of the window's screen that windows may take.</param>
    /// <param name="window">Where the window is now.</param>
    public static ScreenRect? Place(WindowLayout layout, ScreenRect work, ScreenRect window)
    {
        int halfWidth = work.Width / 2;
        int halfHeight = work.Height / 2;
        int third = work.Width / 3;
        int twoThirds = work.Width * 2 / 3;
        switch (layout)
        {
            case WindowLayout.LeftHalf:
                return new ScreenRect(work.Left, work.Top, halfWidth, work.Height);
            case WindowLayout.RightHalf:
                return new ScreenRect(work.Left + halfWidth, work.Top, work.Width - halfWidth, work.Height);
            case WindowLayout.TopHalf:
                return new ScreenRect(work.Left, work.Top, work.Width, halfHeight);
            case WindowLayout.BottomHalf:
                return new ScreenRect(work.Left, work.Top + halfHeight, work.Width, work.Height - halfHeight);
            case WindowLayout.TopLeftQuarter:
                return new ScreenRect(work.Left, work.Top, halfWidth, halfHeight);
            case WindowLayout.TopRightQuarter:
                return new ScreenRect(work.Left + halfWidth, work.Top, work.Width - halfWidth, halfHeight);
            case WindowLayout.BottomLeftQuarter:
                return new ScreenRect(work.Left, work.Top + halfHeight, halfWidth, work.Height - halfHeight);
            case WindowLayout.BottomRightQuarter:
                return new ScreenRect(work.Left + halfWidth, work.Top + halfHeight, work.Width - halfWidth, work.Height - halfHeight);
            case WindowLayout.FirstThird:
                return new ScreenRect(work.Left, work.Top, third, work.Height);
            case WindowLayout.CenterThird:
                return new ScreenRect(work.Left + third, work.Top, twoThirds - third, work.Height);
            case WindowLayout.LastThird:
                return new ScreenRect(work.Left + twoThirds, work.Top, work.Width - twoThirds, work.Height);
            case WindowLayout.FirstTwoThirds:
                return new ScreenRect(work.Left, work.Top, twoThirds, work.Height);
            case WindowLayout.LastTwoThirds:
                return new ScreenRect(work.Left + third, work.Top, work.Width - third, work.Height);
            case WindowLayout.AlmostMaximize:
                return Centered(work, work.Width * 9 / 10, work.Height * 9 / 10);
            case WindowLayout.MaximizeHeight:
            {
                int width = Math.Min(window.Width, work.Width);
                return new ScreenRect(Math.Clamp(window.Left, work.Left, work.Right - width), work.Top, width, work.Height);
            }

            case WindowLayout.MaximizeWidth:
            {
                int height = Math.Min(window.Height, work.Height);
                return new ScreenRect(work.Left, Math.Clamp(window.Top, work.Top, work.Bottom - height), work.Width, height);
            }

            case WindowLayout.Center:
                return Centered(work, Math.Min(window.Width, work.Width), Math.Min(window.Height, work.Height));
            default:
                return null;
        }
    }

    /// <summary>
    /// Where a window that cannot change its size goes when it is asked to take a place: in the
    /// middle of that place, as large as it is, and no further out than its screen allows.
    /// </summary>
    public static ScreenRect Fitted(ScreenRect place, ScreenRect work, ScreenRect window)
    {
        int width = Math.Min(window.Width, work.Width);
        int height = Math.Min(window.Height, work.Height);
        int left = place.Left + ((place.Width - width) / 2);
        int top = place.Top + ((place.Height - height) / 2);
        return new ScreenRect(Math.Clamp(left, work.Left, work.Right - width), Math.Clamp(top, work.Top, work.Bottom - height), width, height);
    }

    /// <summary>
    /// Where a window goes on another display: at the same place, measured in parts of the
    /// display, and of the same size in parts of the display, so that a window that took the
    /// left half takes the left half there too. It never comes out larger than the display
    /// or reaching over its edge.
    /// </summary>
    /// <param name="window">Where the window is now.</param>
    /// <param name="from">The part of its display that windows may take.</param>
    /// <param name="to">That part of the display it goes to.</param>
    public static ScreenRect OnDisplay(ScreenRect window, ScreenRect from, ScreenRect to)
    {
        if (from.Width <= 0 || from.Height <= 0)
        {
            return Centered(to, Math.Min(window.Width, to.Width), Math.Min(window.Height, to.Height));
        }

        int width = Math.Clamp((int)((long)window.Width * to.Width / from.Width), 1, Math.Max(to.Width, 1));
        int height = Math.Clamp((int)((long)window.Height * to.Height / from.Height), 1, Math.Max(to.Height, 1));
        int left = to.Left + (int)((long)(window.Left - from.Left) * to.Width / from.Width);
        int top = to.Top + (int)((long)(window.Top - from.Top) * to.Height / from.Height);
        return new ScreenRect(
            Math.Clamp(left, to.Left, Math.Max(to.Right - width, to.Left)),
            Math.Clamp(top, to.Top, Math.Max(to.Bottom - height, to.Top)),
            width,
            height);
    }

    /// <summary>
    /// The display after the one a window is on, or the one before, among the displays in
    /// the order they stand in from left to right and then from top to bottom. After the last
    /// comes the first again. Null when there are fewer than two.
    /// </summary>
    /// <param name="displays">Each display's whole area.</param>
    /// <param name="current">Which of them the window is on, by its place in the list.</param>
    /// <returns>The place in the list of the display to go to.</returns>
    public static int? OtherDisplay(IReadOnlyList<ScreenRect> displays, int current, bool next)
    {
        if (displays.Count < 2 || current < 0 || current >= displays.Count)
        {
            return null;
        }

        var order = Enumerable.Range(0, displays.Count)
            .OrderBy(i => displays[i].Left)
            .ThenBy(i => displays[i].Top)
            .ToList();
        int at = order.IndexOf(current);
        return order[(at + (next ? 1 : order.Count - 1)) % order.Count];
    }

    private static ScreenRect Centered(ScreenRect work, int width, int height) =>
        new(work.Left + ((work.Width - width) / 2), work.Top + ((work.Height - height) / 2), width, height);
}
