namespace Wyvern.Core.AutoExpand;

/// <summary>What the press of one key means for the characters that are being kept.</summary>
public enum KeyKind
{
    /// <summary>Shift, Ctrl or a lock key pressed by itself. It changes no text, so nothing happens.</summary>
    Modifier,

    /// <summary>The run of typing is broken: everything that was kept is forgotten.</summary>
    Break,

    /// <summary>Backspace: the newest character that was kept is forgotten.</summary>
    Backspace,

    /// <summary>
    /// A key that types an accent onto the character that follows. Neither it nor that
    /// character is kept, because the two together make a character Wyvern was not told.
    /// </summary>
    DeadKey,

    /// <summary>A character was typed.</summary>
    Character,
}

/// <summary>
/// The numbers Windows gives the keys that the rules of <see cref="TypedKey"/> name: its
/// virtual-key codes, as listed on the documentation page "Virtual-Key Codes"
/// (learn.microsoft.com/windows/win32/inputdev/virtual-key-codes).
/// </summary>
public static class KeyCodes
{
    public const int Backspace = 0x08;      // VK_BACK
    public const int Tab = 0x09;            // VK_TAB
    public const int Enter = 0x0D;          // VK_RETURN
    public const int Shift = 0x10;          // VK_SHIFT: either Shift key
    public const int Control = 0x11;        // VK_CONTROL: either Ctrl key
    public const int Alt = 0x12;            // VK_MENU: either Alt key
    public const int CapsLock = 0x14;       // VK_CAPITAL
    public const int Escape = 0x1B;         // VK_ESCAPE
    public const int PageUp = 0x21;         // VK_PRIOR
    public const int PageDown = 0x22;       // VK_NEXT
    public const int End = 0x23;            // VK_END
    public const int Home = 0x24;           // VK_HOME
    public const int Left = 0x25;           // VK_LEFT
    public const int Up = 0x26;             // VK_UP
    public const int Right = 0x27;          // VK_RIGHT
    public const int Down = 0x28;           // VK_DOWN
    public const int Insert = 0x2D;         // VK_INSERT
    public const int Delete = 0x2E;         // VK_DELETE
    public const int LeftWindows = 0x5B;    // VK_LWIN
    public const int RightWindows = 0x5C;   // VK_RWIN
    public const int NumLock = 0x90;        // VK_NUMLOCK
    public const int ScrollLock = 0x91;     // VK_SCROLL
    public const int LeftShift = 0xA0;      // VK_LSHIFT
    public const int RightShift = 0xA1;     // VK_RSHIFT
    public const int LeftControl = 0xA2;    // VK_LCONTROL
    public const int RightControl = 0xA3;   // VK_RCONTROL
}

/// <summary>
/// One key that went down, as the keyboard hook saw it. The hook only gathers these facts;
/// what they mean is decided here, in <see cref="Kind"/>, where it can be tested.
/// </summary>
/// <remarks>
/// This holds a character the owner typed. It is a plain structure and not a record, so that
/// nothing prints the character by accident: <see cref="ToString"/> gives the type's name only.
/// It lives for the length of one call and is never stored.
/// </remarks>
public readonly struct TypedKey
{
    /// <param name="virtualKey">Which key it is: its virtual-key code.</param>
    /// <param name="character">
    /// The one character the key types with Shift and Caps Lock as they are, or the zero
    /// character when it types none, or more than one.
    /// </param>
    /// <param name="deadKey">True for a key that types an accent onto the character that follows.</param>
    /// <param name="chorded">True when Ctrl, Alt or a Windows key was held as the key went down.</param>
    /// <param name="injected">True when a program made the key press, Wyvern included, and not the keyboard.</param>
    /// <param name="inWyvern">True when the window in front is one of Wyvern's own.</param>
    /// <param name="window">The window in front, as a number that is only ever compared. Zero when there is none.</param>
    public TypedKey(int virtualKey, char character, bool deadKey, bool chorded, bool injected, bool inWyvern, nint window)
    {
        VirtualKey = virtualKey;
        Character = character;
        DeadKey = deadKey;
        Chorded = chorded;
        Injected = injected;
        InWyvern = inWyvern;
        Window = window;
    }

    public int VirtualKey { get; }

    public char Character { get; }

    public bool DeadKey { get; }

    public bool Chorded { get; }

    public bool Injected { get; }

    public bool InWyvern { get; }

    public nint Window { get; }

    /// <summary>
    /// What this key press means. The rules, in this order:
    /// a key that a program pressed is a break, so that what another program types is never
    /// kept and Wyvern's own Backspaces count as what they are, a change of the text;
    /// a key typed into one of Wyvern's own windows is a break, so that nothing typed into the
    /// launcher or into Sync Setup is ever kept;
    /// a key with no window in front is a break;
    /// Shift, Ctrl and the lock keys by themselves are nothing;
    /// a key pressed with Ctrl, Alt or a Windows key held is a break (that includes a
    /// character typed with AltGr, which is Ctrl and Alt to Windows);
    /// Backspace is Backspace, and a dead key is a dead key;
    /// Enter, Esc, Tab, the arrows, Home, End, Page Up, Page Down, Insert and Delete are breaks;
    /// a key that types exactly one character, which is not a control character, is that
    /// character;
    /// and anything else is a break: a function key, the Alt or Windows key itself, a key
    /// that types nothing or more than one character.
    /// </summary>
    public KeyKind Kind
    {
        get
        {
            if (Injected || InWyvern || Window == 0)
            {
                return KeyKind.Break;
            }

            if (IsModifierByItself(VirtualKey))
            {
                return KeyKind.Modifier;
            }

            if (Chorded)
            {
                return KeyKind.Break;
            }

            if (VirtualKey == KeyCodes.Backspace)
            {
                return KeyKind.Backspace;
            }

            if (DeadKey)
            {
                return KeyKind.DeadKey;
            }

            if (MovesOrEnds(VirtualKey) || char.IsControl(Character) || char.IsSurrogate(Character))
            {
                return KeyKind.Break;
            }

            return KeyKind.Character;
        }
    }

    /// <summary>The type's name and nothing else: the character is the owner's and is never printed.</summary>
    public override string ToString() => nameof(TypedKey);

    // The keys that are held to change what other keys do, or switch a lock, and change no
    // text themselves. Alt and the Windows keys are not among them: each opens a menu when it
    // is let go of by itself, and what is typed next goes there.
    private static bool IsModifierByItself(int virtualKey) => virtualKey is
        KeyCodes.Shift or KeyCodes.LeftShift or KeyCodes.RightShift
        or KeyCodes.Control or KeyCodes.LeftControl or KeyCodes.RightControl
        or KeyCodes.CapsLock or KeyCodes.NumLock or KeyCodes.ScrollLock;

    // The keys that end a line or a field or move the caret. They are named, although the
    // last rule would catch them as well, so that this does not rest on what Windows gives as
    // the character of Enter, Tab or Esc.
    private static bool MovesOrEnds(int virtualKey) => virtualKey is
        KeyCodes.Enter or KeyCodes.Escape or KeyCodes.Tab
        or KeyCodes.Left or KeyCodes.Up or KeyCodes.Right or KeyCodes.Down
        or KeyCodes.Home or KeyCodes.End or KeyCodes.PageUp or KeyCodes.PageDown
        or KeyCodes.Insert or KeyCodes.Delete;
}
