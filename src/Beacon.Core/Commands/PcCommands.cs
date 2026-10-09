using System.Globalization;
using Beacon.Core.Search;

namespace Beacon.Core.Commands;

/// <summary>The keys of a keyboard that tell whatever is playing what to do.</summary>
public enum MediaKey
{
    PlayPause,
    NextTrack,
    PreviousTrack,
}

/// <summary>What the launcher can ask Windows to do with the whole PC. Implemented with Windows calls in the app.</summary>
public interface ISystemActions
{
    /// <summary>Locks the PC, as Win+L does. What is running keeps running.</summary>
    void Lock();

    /// <summary>Puts the PC to sleep. It returns when the PC has woken again, or at once when Windows refused.</summary>
    void Sleep();

    /// <summary>Puts the PC into hibernation: what is open is written to the disk and the PC switches off.</summary>
    void Hibernate();

    /// <summary>Restarts the PC at once. Programs are asked to close, as when it is done from the Start menu.</summary>
    void Restart();

    /// <summary>Shuts the PC down at once.</summary>
    void ShutDown();

    /// <summary>Signs the owner out of Windows at once.</summary>
    void SignOut();

    /// <summary>Empties the Recycle Bin of every drive, without asking.</summary>
    /// <returns>False when there was nothing in it.</returns>
    bool EmptyRecycleBin();

    /// <summary>Shows the Recycle Bin in Explorer.</summary>
    void OpenRecycleBin();

    /// <summary>Switches the sound off, or on again.</summary>
    /// <returns>True when the sound is off now, or null when the PC has nothing to play sound through.</returns>
    bool? ToggleMute();

    /// <summary>Makes the sound louder or softer by so many hundredths of the whole, and switches it on again when it is made louder.</summary>
    /// <returns>How loud it is now, from 0 to 100, or null when the PC has nothing to play sound through.</returns>
    int? ChangeVolume(int by);

    /// <summary>Sets how loud the sound is, from 0 to 100.</summary>
    /// <returns>How loud it is now, or null when the PC has nothing to play sound through.</returns>
    int? SetVolume(int percent);

    /// <summary>Switches Windows and its apps from light to dark, or back.</summary>
    /// <returns>True when Windows is dark now.</returns>
    bool ToggleDarkMode();

    /// <summary>Shows the desktop by minimising every window, or puts the windows back, as Win+D does.</summary>
    void ShowDesktop();

    /// <summary>Tells whatever is playing music or a video what one of the keyboard's media keys would tell it. Nothing happens when nothing is playing.</summary>
    void Media(MediaKey key);
}

/// <summary>The launcher's commands for the PC itself.</summary>
public static class PcCommands
{
    /// <summary>How far Volume Up and Volume Down move the sound, in hundredths of the whole.</summary>
    public const int VolumeStep = 6;

    /// <summary>What the empty input line says while Set Volume waits for its number.</summary>
    public const string VolumePrompt = "A number from 0 to 100";

    /// <summary>Said when Set Volume was given anything but a whole number from 0 to 100.</summary>
    public const string NotAVolume = "Not a Number from 0 to 100";

    /// <summary>Said when the PC has nothing to play sound through.</summary>
    public const string NoSound = "No Sound Device";

    /// <param name="system">The PC.</param>
    /// <param name="say">Shows a few fixed words for a moment, to say what was done. Left out by code that shows nothing.</param>
    public static IReadOnlyList<BuiltInCommand> Create(ISystemActions system, Action<string>? say = null)
    {
        say ??= _ => { };
        return
        [
            new BuiltInCommand("lock-screen", "Lock Screen", "Lock this PC, as Win+L does", system.Lock),
            new BuiltInCommand("sleep", "Sleep", "Put this PC to sleep", system.Sleep),
            new BuiltInCommand("hibernate", "Hibernate", "Save what is open to the disk and switch this PC off", system.Hibernate),

            // These three end what is running, so one key press must not do it: Enter asks
            // first, and a second Enter does it. Esc goes back.
            Confirmed("restart", "Restart", "Restart this PC", "Press Enter to restart this PC", system.Restart),
            Confirmed("shut-down", "Shut Down", "Switch this PC off", "Press Enter to shut this PC down", system.ShutDown),
            Confirmed("sign-out", "Sign Out", "Sign out of Windows", "Press Enter to sign out of Windows", system.SignOut),

            // What is in the Recycle Bin cannot be had back afterwards.
            Confirmed(
                "empty-recycle-bin",
                "Empty Recycle Bin",
                "Delete what is in the Recycle Bin for good",
                "Press Enter to empty the Recycle Bin",
                () => say(system.EmptyRecycleBin() ? "Recycle Bin Emptied" : "Recycle Bin Is Already Empty")),
            new BuiltInCommand("open-recycle-bin", "Open Recycle Bin", "Show the Recycle Bin in Explorer", system.OpenRecycleBin),

            new BuiltInCommand("toggle-mute", "Toggle Mute", "Switch the sound off, or on again", () => say(system.ToggleMute() switch
            {
                true => "Muted",
                false => "Unmuted",
                null => NoSound,
            })),
            new BuiltInCommand("volume-up", "Volume Up", "Make the sound louder", () => say(Loudness(system.ChangeVolume(VolumeStep)))),
            new BuiltInCommand("volume-down", "Volume Down", "Make the sound softer", () => say(Loudness(system.ChangeVolume(-VolumeStep)))),
            new BuiltInCommand(
                "set-volume",
                "Set Volume",
                "Set how loud the sound is, from 0 to 100",
                Run: null,
                Argument: new ItemArgument(VolumePrompt, typed => say(Volume(typed) is { } percent ? Loudness(system.SetVolume(percent)) : NotAVolume))),

            new BuiltInCommand(
                "toggle-appearance",
                "Toggle System Appearance",
                "Switch Windows between light and dark",
                () => say(system.ToggleDarkMode() ? "Dark Appearance" : "Light Appearance")),
            new BuiltInCommand("show-desktop", "Show Desktop", "Minimise every window, or put them back, as Win+D does", system.ShowDesktop),

            // The keyboard's media keys, for a keyboard that has none.
            new BuiltInCommand("media-play-pause", "Play or Pause Media", "Play or pause the music or video that is playing", () => system.Media(MediaKey.PlayPause)),
            new BuiltInCommand("media-next", "Next Track", "Skip to the next track of what is playing", () => system.Media(MediaKey.NextTrack)),
            new BuiltInCommand("media-previous", "Previous Track", "Go back to the previous track of what is playing", () => system.Media(MediaKey.PreviousTrack)),
        ];
    }

    /// <summary>The number that was typed for Set Volume: digits only, with spaces round them and a percent sign after them allowed, from 0 to 100. Anything else is null.</summary>
    public static int? Volume(string typed)
    {
        string digits = typed.Trim();
        if (digits.EndsWith('%'))
        {
            digits = digits[..^1].TrimEnd();
        }

        return digits.Length is > 0 and <= 3
            && digits.All(char.IsAsciiDigit)
            && int.Parse(digits, CultureInfo.InvariantCulture) is var percent and <= 100
            ? percent
            : null;
    }

    private static string Loudness(int? percent) =>
        percent is { } now ? string.Create(CultureInfo.InvariantCulture, $"Volume {now}%") : NoSound;

    // A command that asks before it does its thing: it has no action of its own, so Enter
    // opens the input line with the question in it, as Tab does, and Enter there does it
    // whatever was typed. Esc gives it up.
    private static BuiltInCommand Confirmed(string id, string title, string subtitle, string question, Action run) =>
        new(id, title, subtitle, Run: null, Argument: new ItemArgument(question, _ => run()));
}
