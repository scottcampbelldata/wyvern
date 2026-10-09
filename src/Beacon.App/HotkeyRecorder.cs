using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using Beacon.Core.Settings;

namespace Beacon.App;

/// <summary>
/// A box that takes a hotkey by having it pressed. The box shows the hotkey the way
/// settings.json writes it, such as Ctrl+Shift+V. Backspace and Delete empty the box, and Tab
/// moves on as usual. Used by every window that asks for a hotkey.
/// </summary>
public static class HotkeyRecorder
{
    /// <summary>Said when a key was pressed that cannot be a hotkey by itself.</summary>
    public const string NeedsModifier = "A hotkey needs Ctrl, Alt, Shift or the Windows key held with it, unless it is a function key.";

    /// <summary>What a box's PreviewKeyDown does.</summary>
    /// <param name="say">Shows a sentence at the foot of the window, or empties it when given nothing.</param>
    public static void OnKeyDown(object sender, KeyEventArgs e, Action<string> say)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (key is Key.Back or Key.Delete)
        {
            box.Text = "";
            return;
        }

        string? name = key switch
        {
            Key.Space => "Space",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Enter => "Enter",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => null,
        };
        if (name is null)
        {
            // A modifier by itself, or a key Beacon cannot use for a hotkey.
            return;
        }

        var modifiers = Keyboard.Modifiers;
        string text = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
            + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
            + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
            + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "")
            + name;
        if (Hotkey.TryParse(text, out _))
        {
            box.Text = text;
            say("");
        }
        else
        {
            say(NeedsModifier);
        }
    }
}
