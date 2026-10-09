using System.Globalization;

namespace Beacon.Core.Settings;

/// <summary>Values match the MOD_* flags that the Windows RegisterHotKey function expects.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <param name="VirtualKey">A Windows virtual-key code.</param>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const uint VkSpace = 0x20;
    private const uint VkF1 = 0x70;
    private const uint VkF24 = 0x87;

    /// <summary>Alt+Space.</summary>
    public static readonly Hotkey Default = new(HotkeyModifiers.Alt, VkSpace);

    /// <summary>
    /// Parses text such as "Alt+Space", "Ctrl+Shift+K", "Ctrl+Alt+Left" or "F12". Supported keys
    /// are Space, A to Z, 0 to 9, F1 to F24, the four arrows (Left, Right, Up, Down), Enter,
    /// Home, End, PageUp and PageDown. Every key except a function key needs at least one modifier.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            HotkeyModifiers modifier = part.ToLowerInvariant() switch
            {
                "alt" => HotkeyModifiers.Alt,
                "ctrl" or "control" => HotkeyModifiers.Control,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out uint virtualKey))
        {
            return false;
        }

        bool isFunctionKey = virtualKey is >= VkF1 and <= VkF24;
        if (modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, virtualKey);
        return true;
    }

    private static bool TryParseKey(string key, out uint virtualKey)
    {
        string upper = key.ToUpperInvariant();
        if (upper == "SPACE")
        {
            virtualKey = VkSpace;
            return true;
        }

        if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            // Virtual-key codes for letters and digits equal their ASCII codes.
            virtualKey = upper[0];
            return true;
        }

        if (upper.Length is 2 or 3
            && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= 24)
        {
            virtualKey = VkF1 + (uint)(number - 1);
            return true;
        }

        // The keys that have names. The numbers are their Windows virtual-key codes: VK_RETURN,
        // VK_PRIOR, VK_NEXT, VK_END, VK_HOME, VK_LEFT, VK_UP, VK_RIGHT and VK_DOWN.
        virtualKey = upper switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
