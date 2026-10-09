using System.Text;
using System.Text.RegularExpressions;

namespace Beacon.Tests;

/// <summary>
/// No test may install a keyboard hook or send a key, so the two classes that do are never run
/// by a test. What can be held is what their source says, and a later change could undo any of
/// it without another test noticing: that one file alone asks Windows for keys, that its
/// callback always passes the key on and lets nothing out, that it neither waits nor writes,
/// that the hook lives on a thread of its own which never waits for another, that a key's
/// character is worked out without disturbing the keyboard, and that a keyword is only ever
/// taken away together with the paste. So the source is read as text here, the way
/// <see cref="PrivacyRuleTests"/> reads it.
/// </summary>
public class KeyboardHookSourceTests
{
    private const string HookFile = "Beacon.App/Interop/KeyboardHook.cs";
    private const string LoopFile = "Beacon.App/Interop/LoopThread.cs";
    private const string KeysFile = "Beacon.App/Interop/ForegroundWindowTarget.cs";

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Beacon.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"Beacon.slnx was not found above {AppContext.BaseDirectory}");
        return folder.FullName;
    }

    // Every source file under src, as its path from src (with forward slashes) and its lines
    // that are not comments. Build output is left out.
    private static List<(string Path, string Code)> SourceFiles()
    {
        string src = Path.Combine(RepositoryRoot(), "src");
        var files = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, file);
            bool buildOutput = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
            if (buildOutput || !(file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string code = string.Join("\n", File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            files.Add((relative.Replace('\\', '/'), code));
        }

        return files;
    }

    // One file's lines that are not comments.
    private static string Code(string path)
    {
        var found = SourceFiles().Where(file => file.Path == path).ToList();
        Assert.True(found.Count == 1, $"{path} is not under src");
        return found[0].Code;
    }

    // Where a piece of code is. It must be there exactly once, so that "before" and "after" mean something.
    private static int Place(string source, string code)
    {
        int first = source.IndexOf(code, StringComparison.Ordinal);
        Assert.True(first >= 0, $"The source does not hold: {code}");
        Assert.True(source.IndexOf(code, first + code.Length, StringComparison.Ordinal) < 0, $"The source holds this more than once: {code}");
        return first;
    }

    // The body of one member of a class: from its first line to the first line after it that
    // closes a member (a brace four spaces in).
    private static string Member(string source, string firstLine)
    {
        int start = Place(source, firstLine);
        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The end of the member was not found: {firstLine}");
        return source[start..end];
    }

    // The body of one member of a class inside a class: from its first line to the first line
    // after it that closes such a member (a brace eight spaces in).
    private static string Inner(string source, string firstLine)
    {
        int start = Place(source, firstLine);
        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The end of the member was not found: {firstLine}");
        return source[start..end];
    }

    // The files under src whose code names this, as paths from src.
    private static List<string> FilesThatName(string pattern) =>
        [.. SourceFiles().Where(file => Regex.IsMatch(file.Code, pattern)).Select(file => file.Path).Order(StringComparer.Ordinal)];

    // The fields of one structure of a source text, in order, each as "Type Name": the "public
    // Type Name;" lines of its body. A structure's body holds no brace of its own.
    private static List<string> Fields(string code, string structure)
    {
        var found = Regex.Match(code, @"\bstruct " + structure + @"\s*\{(?<body>[^{}]*)\}");
        Assert.True(found.Success, $"The structure {structure} was not found.");
        return [.. Regex.Matches(found.Groups["body"].Value, @"public (?<type>\w+) (?<name>\w+);").Select(field => $"{field.Groups["type"].Value} {field.Groups["name"].Value}")];
    }

    // What the classes and structs of a source text hold, in order: every field and event, and
    // every property that has a setter or an initialiser or is automatic, whatever its access
    // and whatever its type. Each is its declaration with white space made single and with the
    // initialiser cut off; for a property, its accessors follow. Left out, because none holds
    // anything: constants, methods, constructors, delegates, extern declarations (the
    // DllImport ones), and properties that are only worked out ("Name => expression", or a
    // getter with a body). Everything inside a method, an accessor or a lambda is skipped. So
    // are comments, strings and characters, which can hold braces and semicolons. Verbatim and
    // raw strings are not understood: none stands in the files this is used on.
    private static List<string> Members(string code)
    {
        var found = new List<string>();
        var header = new StringBuilder();
        var body = new StringBuilder();
        string accessors = "";
        int inside = 0;

        // The text of the declaration so far: single spaces, and without the attributes in front of it.
        string Tidy() => Regex.Replace(Regex.Replace(header.ToString(), @"\s+", " ").Trim(), @"^(?:\[[^\]]*\] ?)+", "");

        // Where the initialiser or the expression body of a declaration begins: its first "="
        // outside round and square brackets, or the end when it has none.
        static int Cut(string text)
        {
            int round = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '(' or '[')
                {
                    round++;
                }
                else if (text[i] is ')' or ']')
                {
                    round--;
                }
                else if (text[i] == '=' && round == 0)
                {
                    return i;
                }
            }

            return text.Length;
        }

        void Flush()
        {
            string text = Tidy();
            string accessed = accessors;
            header.Clear();
            accessors = "";

            int cut = Cut(text);
            string head = Regex.Replace(text[..cut].Trim(), @"\s+where\s.*$", "");
            bool worked = cut < text.Length && text[cut..].StartsWith("=>", StringComparison.Ordinal);

            // What ends in a name is a field or a property. What ends in a bracket is a method,
            // a constructor, a delegate or an extern declaration.
            bool holds = head.Length > 0
                && !worked
                && Regex.IsMatch(head, @"\w$")
                && !Regex.IsMatch(head, @"^(?:global )?(?:using|namespace)\b|\bconst\b");
            if (!holds)
            {
                return;
            }

            if (accessed.Length > 0)
            {
                if (!Regex.IsMatch(accessed, @"\b(?:set|init)\b|\bget ?;"))
                {
                    return;
                }

                head += " " + accessed;
            }

            found.Add(head);
        }

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            StringBuilder here = inside == 0 ? header : body;

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i + 1 < code.Length && code[i + 1] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Assert.True(end >= 0, "A comment is not closed.");
                i = end + 1;
                continue;
            }

            if (c is '"' or '\'')
            {
                int start = i;
                for (i++; i < code.Length && code[i] != c; i++)
                {
                    if (code[i] == '\\')
                    {
                        i++;
                    }
                }

                here.Append(code, start, Math.Min(i + 1, code.Length) - start);
                continue;
            }

            if (c == '{')
            {
                // The body of a namespace, a class or a struct is where the declarations are.
                if (inside == 0 && Regex.IsMatch(Tidy(), @"^(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|file|new|ref) )*(?:namespace [\w.]+|(?:record (?:class|struct)|class|struct|interface|record|enum) \w)"))
                {
                    header.Clear();
                    continue;
                }

                inside++;
                body.Append(c);
                continue;
            }

            if (c == '}')
            {
                if (inside == 0)
                {
                    // A type ends. What stands unfinished in it, the members of an enum, counts too.
                    Flush();
                    continue;
                }

                body.Append(c);
                inside--;
                if (inside > 0)
                {
                    continue;
                }

                string closed = Regex.Replace(body.ToString(), @"\s+", " ");
                body.Clear();
                string sofar = Tidy();
                if (Cut(sofar) < sofar.Length)
                {
                    // An initialiser or an expression body that goes on to its semicolon.
                    continue;
                }

                if (Regex.IsMatch(sofar, @"\w$") && Regex.IsMatch(closed, @"^\{ (?:(?:public|private|protected|internal) )?(?:get|set|init)\b"))
                {
                    accessors = closed;
                }

                int next = i + 1;
                while (next < code.Length && char.IsWhiteSpace(code[next]))
                {
                    next++;
                }

                // A property may be followed by an initialiser. Anything else ends it.
                if (!(next < code.Length && code[next] == '='))
                {
                    Flush();
                }

                continue;
            }

            if (c == ';' && inside == 0)
            {
                Flush();
                continue;
            }

            here.Append(c);
        }

        Assert.Equal(0, inside);
        Flush();
        return [.. found.Order(StringComparer.Ordinal)];
    }

    [Theory]
    [InlineData(@"\bSetWindowsHookEx")]
    [InlineData(@"\bUnhookWindowsHookEx\b")]
    [InlineData(@"\bCallNextHookEx\b")]
    [InlineData(@"\bToUnicodeEx\b")]
    [InlineData(@"\bGetKeyboardLayout\b")]
    [InlineData(@"\bGetKeyState\b")]
    public void One_file_alone_asks_Windows_for_key_presses_and_for_the_character_of_a_key(string call)
    {
        Assert.Equal([HookFile], FilesThatName(call));
    }

    [Theory]
    [InlineData(@"\bGetKeyboardState\b")]
    [InlineData(@"\bToUnicode\b")]
    [InlineData(@"\bToAscii")]
    [InlineData(@"\bRegisterRawInputDevices\b")]
    [InlineData(@"\bGetRawInputData\b")]
    [InlineData(@"\bAttachThreadInput\b")]
    [InlineData(@"\bkeybd_event\b")]
    [InlineData(@"\bSetKeyboardState\b")]
    [InlineData(@"\bBlockInput\b")]
    [InlineData(@"\bSendKeys\b")]
    public void No_file_reads_or_sends_keys_in_any_other_way(string call)
    {
        Assert.Empty(FilesThatName(call));
    }

    [Fact]
    public void One_file_alone_sends_key_presses()
    {
        Assert.Equal([KeysFile], FilesThatName(@"\bSendInput\b"));
    }

    [Fact]
    public void The_callback_always_passes_the_key_on_and_lets_nothing_out()
    {
        string callback = Inner(Code(HookFile), "        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)");

        // Its one way out is to hand the key to the next hook, with what it was given. It
        // never returns a value of its own, which is how a hook would swallow a key.
        Assert.Single(Regex.Matches(callback, @"\breturn\b"));
        Assert.EndsWith("return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);", callback.TrimEnd());

        // Everything before that is inside a try whose catch takes any exception, and only
        // hands it on.
        Assert.Matches(@"\{\s*try\s*\{", callback);
        Assert.Matches(@"catch \(Exception ex\)\s*\{\s*_owner\.Tell\(ex\);\s*\}\s*return CallNextHookEx\(", callback);
    }

    [Fact]
    public void A_failure_inside_the_callback_is_handed_on_once_and_cannot_leave_it_either()
    {
        string tell = Member(Code(HookFile), "    private void Tell(Exception error)");

        // Once for a run, whichever hook thread it happens on: a mistake that shows at every
        // key press must not be handed on at every key press. And whoever it is handed to
        // cannot make the callback throw.
        Assert.Matches(@"if \(Interlocked\.Exchange\(ref _failureTold, 1\) != 0\)\s*\{\s*return;\s*\}\s*try", tell);

        // A failure that could not be handed on has not been said, so it does not use the
        // once up: the mark is set back, with Interlocked as it was set, and a later failure
        // tries again. This is the twin of the expander's own TellOfKey.
        Assert.Matches(
            @"try\s*\{\s*_failed\(error\);\s*\}\s*catch \(Exception\)\s*\{\s*Interlocked\.Exchange\(ref _failureTold, 0\);\s*\}\s*$",
            tell);
        Assert.Equal(2, Regex.Matches(tell, @"\bInterlocked\.Exchange\(ref _failureTold, ").Count);
    }

    [Theory]
    [InlineData("File.")]
    [InlineData("Directory.")]
    [InlineData("Stream")]
    [InlineData("FileLog")]
    [InlineData("_log")]
    [InlineData("Report(")]
    [InlineData("Notify(")]
    [InlineData("Console.")]
    [InlineData("Debug.")]
    [InlineData("Trace.")]
    [InlineData("Thread.Sleep")]
    [InlineData("Task.")]
    [InlineData("lock (")]
    [InlineData(".Wait(")]
    [InlineData("WaitOne")]
    [InlineData("Invoke(")]
    [InlineData("Clipboard")]
    [InlineData("Registry")]
    [InlineData("Process.")]
    [InlineData("SendInput")]
    [InlineData("StringBuilder")]
    [InlineData("string.")]
    [InlineData("Join(")]
    [InlineData("Monitor.")]
    [InlineData("Semaphore")]
    [InlineData("ResetEvent")]
    public void The_hook_neither_waits_nor_writes_nor_logs_nor_keeps_text(string never)
    {
        // Every key press in every program waits for the callback, and what passes through it
        // is what the owner types. The file holds no call that waits for another thread, and
        // none that runs work on one (Invoke): the waiting that starting and stopping the
        // hook's thread takes is all in LoopThread, on the side of the thread that asks.
        Assert.DoesNotContain(never, Code(HookFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hook_keeps_nothing_of_a_key_between_two_calls()
    {
        // These are all the fields and properties the class and the class inside it have,
        // whatever their access, whether they are static or readonly, and whatever their type;
        // a member that is added fails this until it is added here on purpose. Constants and the
        // DllImport declarations hold nothing and are not counted. Of the hook itself: the
        // thread it was made on, which process is Beacon's, where a failure is handed and
        // whether one has been, the hook that is installed, and the two counts for the log. Of
        // one installed hook: its owner, who is told of a key, the callback, the two arrays for
        // the translation, its thread, its handle and what Windows answered when it was removed.
        // Both arrays are emptied again before each call returns.
        string code = Code(HookFile);
        Assert.Equal(
            [
                "private IntPtr _hook",
                "private Listening? _listening",
                "private LoopThread? _thread",
                "private bool _known",
                "private int _failureTold",
                "private readonly Action<Exception> _failed",
                "private readonly Action<TypedKey> _keyDown",
                "private readonly Dispatcher _made",
                "private readonly HookProcedure _procedure",
                "private readonly KeyboardHook _owner",
                "private readonly byte[] _keyState",
                "private readonly char[] _characters",
                "private readonly int _ownProcess",
                "public int Installs { get; private set; }",
                "public int Threads { get; private set; }",
            ],
            Members(code));

        string read = Inner(code, "        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)");
        int translated = read.IndexOf("ToUnicodeEx(", StringComparison.Ordinal);
        Assert.True(translated > 0);

        // Nothing of the key is left in either array when Read returns: both are cleared after
        // the call that filled them.
        Assert.True(read.IndexOf("Array.Clear(_characters);", translated, StringComparison.Ordinal) > translated);
        Assert.True(read.IndexOf("Array.Clear(_keyState);", translated, StringComparison.Ordinal) > translated);
    }

    [Fact]
    public void The_hooks_thread_keeps_only_what_it_needs_to_be_started_and_stopped()
    {
        // The thread, its event and its two actions, what it has begun and whether Start is
        // still waiting, and where its loop and a failure of its beginning are kept.
        Assert.Equal(
            [
                "private Dispatcher? _loop",
                "private Exception? _beginError",
                "private int _state",
                "private readonly Action _begin",
                "private readonly Action _end",
                "private readonly ManualResetEventSlim _begun",
                "private readonly Thread _thread",
                "public static readonly TimeSpan StartWait",
                "public static readonly TimeSpan StopWait",
            ],
            Members(Code(LoopFile)));

        // Nothing asks whether the thread has ended, so nothing offers to say so.
        Assert.DoesNotContain("HasEnded", Code(LoopFile));
    }

    [Fact]
    public void The_list_of_what_a_class_holds_sees_every_kind_of_field_and_property_and_nothing_else()
    {
        // Made-up source, as the two files above might one day grow it. The first lines are
        // the ones a pattern that looks only for "private readonly <simple type> _name" is blind to.
        const string made = """
            namespace Made;

            public sealed class One
            {
                private readonly Dictionary<nint, string> _typed = new();
                private static int _count;
                public string Last { get; set; } = "";
                internal byte[] Spy;
                private readonly Dictionary<nint, (string A, int B)> _pairs = new();
                private (int A, int B) _tuple;
                int plain;
                public int Only { get; }
                public int Written { get => _count; set => _count = value; }
                private int _sized = new int[] { 1, 2 }.Length;
                private Action _callback = () => { int local = 1; };
                public event Action? Changed;
                private static readonly string[] Names = ["x;", "y{"];
                private char _quote = '"';

                private const string Text = "a;b{c}";
                private const int Constant = 3;
                public int Computed => _count + 1;
                public int Read { get { return _count; } }
                // private int _commented;

                public One(int x = 0) { int local = x; }

                private static void Method(int y = 1, Dictionary<int, int>? map = null)
                {
                    int notAField = 1;
                    var alsoNot = new List<int> { 1 };
                }

                public void Short() => _count = 1;
                public void Generic<T>(T x) where T : class { }

                [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                private static extern short Call(int key);
                private delegate void Handler(int code);

                private sealed class Two
                {
                    private bool _inner;
                    private struct Three { public uint A; }
                }
            }
            """;

        Assert.Equal(
            [
                "int plain",
                "internal byte[] Spy",
                "private (int A, int B) _tuple",
                "private Action _callback",
                "private bool _inner",
                "private char _quote",
                "private int _sized",
                "private readonly Dictionary<nint, (string A, int B)> _pairs",
                "private readonly Dictionary<nint, string> _typed",
                "private static int _count",
                "private static readonly string[] Names",
                "public event Action? Changed",
                "public int Only { get; }",
                "public int Written { get => _count; set => _count = value; }",
                "public string Last { get; set; }",
                "public uint A",
            ],
            Members(made));
    }

    [Fact]
    public void A_keys_character_is_worked_out_without_changing_the_keyboards_state()
    {
        string code = Code(HookFile);

        // Bit 2 of the flags, the value 4: "keyboard state is not changed". Without it the
        // owner's dead keys (an accent typed before a letter) would be swallowed.
        Assert.Contains("private const uint LeaveKeyboardStateAlone = 0x4;", code);
        Assert.Matches(
            @"ToUnicodeEx\(\s*virtualKey,\s*scanCode,\s*_keyState,\s*_characters,\s*_characters\.Length,\s*LeaveKeyboardStateAlone,\s*GetKeyboardLayout\(thread\)\)",
            code);

        // Called in that one place: the other mention is its declaration.
        Assert.Equal(2, Regex.Matches(code, @"\bToUnicodeEx\(").Count);

        // And not at all on a Windows that would not honour the flag: there no thread is
        // started and nothing is installed.
        string install = Member(code, "    public void Install(Action<TypedKey> keyDown)");
        Assert.Contains("private const int FirstBuildWithThatFlag = 14393;", code);
        int refused = Place(install, "if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstBuildWithThatFlag))");
        Assert.True(refused < Place(install, "var listening = new Listening(this, keyDown);"));
        Assert.True(refused < Place(install, "Threads++;"));
    }

    [Fact]
    public void The_hook_is_the_low_level_keyboard_hook_and_its_callback_is_held_for_as_long_as_the_object_lives()
    {
        string code = Code(HookFile);

        Assert.Contains("private const int LowLevelKeyboard = 13;", code);
        Place(code, "_hook = SetWindowsHookEx(LowLevelKeyboard, _procedure, GetModuleHandle(null), 0);");

        // A delegate made on the spot for the call would be held by nothing, and Windows would
        // go on calling a callback that had been collected.
        Assert.Contains("private readonly HookProcedure _procedure;", code);
        Place(code, "_procedure = OnKey;");
        Place(code, "GC.KeepAlive(_procedure);");
    }

    [Fact]
    public void Only_a_key_that_goes_down_is_looked_at()
    {
        string code = Code(HookFile);

        Assert.Contains("private const int KeyDownMessage = 0x0100;", code);
        Assert.Contains("private const int SystemKeyDownMessage = 0x0104;", code);
        Place(code, "if (message is KeyDownMessage or SystemKeyDownMessage)");
        Place(code, "if (code == KeyboardAction)");
        Assert.Contains("private const int KeyboardAction = 0;", code);

        // The two messages for a key that is let go of are named nowhere.
        Assert.DoesNotContain("0x0101", code);
        Assert.DoesNotContain("0x0105", code);
    }

    [Fact]
    public void A_key_that_a_program_pressed_is_told_from_one_the_keyboard_sent()
    {
        string code = Code(HookFile);

        Assert.Contains("private const uint InjectedFlag = 0x10;", code);
        Place(code, "injected: (flags & InjectedFlag) != 0,");
        Place(code, "inBeacon: process == (uint)_owner._ownProcess,");
    }

    [Fact]
    public void The_callback_reads_the_three_values_it_uses_from_the_memory_Windows_hands_over_and_allocates_nothing()
    {
        string code = Code(HookFile);

        // KBDLLHOOKSTRUCT: DWORD vkCode; DWORD scanCode; DWORD flags; DWORD time; ULONG_PTR
        // dwExtraInfo. The first three DWORDs are at 0, 4 and 8 on every Windows Beacon runs on.
        // Reading them straight from lParam makes no object for a key press; a structure
        // copied out with PtrToStructure is a new object each time.
        Assert.DoesNotContain("PtrToStructure", code);
        Assert.DoesNotContain("KeyEvent", code);
        Assert.DoesNotContain("StructLayout", code);
        Assert.Contains("private const int VirtualKeyOffset = 0;", code);
        Assert.Contains("private const int ScanCodeOffset = 4;", code);
        Assert.Contains("private const int FlagsOffset = 8;", code);

        string callback = Inner(code, "        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)");
        int virtualKey = Place(callback, "uint virtualKey = unchecked((uint)Marshal.ReadInt32(lParam, VirtualKeyOffset));");
        int scanCode = Place(callback, "uint scanCode = unchecked((uint)Marshal.ReadInt32(lParam, ScanCodeOffset));");
        int flags = Place(callback, "uint flags = unchecked((uint)Marshal.ReadInt32(lParam, FlagsOffset));");
        int handed = Place(callback, "_keyDown(Read(virtualKey, scanCode, flags));");
        Assert.True(virtualKey < scanCode && scanCode < flags && flags < handed);

        // Only for a key that goes down, and nowhere else is lParam read.
        Assert.True(Place(callback, "if (message is KeyDownMessage or SystemKeyDownMessage)") < virtualKey);
        Assert.Equal(3, Regex.Matches(code, @"\bMarshal\.ReadInt32\(").Count);

        // The injected flag and the Alt flag are those of the value read at offset 8.
        string read = Inner(code, "        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)");
        Assert.Contains("(flags & AltDownFlag) != 0", read);
        Assert.Contains("injected: (flags & InjectedFlag) != 0,", read);
    }

    [Fact]
    public void A_key_that_is_pressed_with_Ctrl_Alt_or_a_Windows_key_held_is_told_so()
    {
        string read = Inner(Code(HookFile), "        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)");

        // Any one of Ctrl, Alt (asked of the keyboard, and also the flag of the event itself)
        // and either Windows key makes it chorded, and nothing else does. Dropping one would
        // keep a "c" after Ctrl+C.
        var chorded = Regex.Match(read, @"bool chorded =([^;]*);");
        Assert.True(chorded.Success, "The expression that says a key was chorded is gone.");
        Assert.DoesNotContain("&&", chorded.Groups[1].Value);
        Assert.Equal(
            [
                "(flags & AltDownFlag) != 0",
                "IsDown(KeyCodes.Alt)",
                "IsDown(KeyCodes.Control)",
                "IsDown(KeyCodes.LeftWindows)",
                "IsDown(KeyCodes.RightWindows)",
            ],
            chorded.Groups[1].Value.Split("||").Select(part => part.Trim()).Order(StringComparer.Ordinal).ToList());
        Place(read, "chorded,");
    }

    [Fact]
    public void A_key_is_only_translated_when_a_window_is_in_front_and_the_key_is_one_of_the_256_there_are()
    {
        string read = Inner(Code(HookFile), "        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)");

        // The key state is 256 bytes, and the key code indexes it: the guard stands before
        // anything is written into it, and before the translation.
        int guard = Place(read, "if (window != IntPtr.Zero && virtualKey is > 0 and < 256)");
        Assert.True(guard < read.IndexOf("Array.Clear(_keyState);", StringComparison.Ordinal));
        Assert.True(guard < Place(read, "_keyState[virtualKey] = Down;"));
        Assert.True(guard < read.IndexOf("ToUnicodeEx(", StringComparison.Ordinal));
    }

    [Fact]
    public void What_ToUnicodeEx_returns_is_read_as_a_character_only_for_exactly_one_and_as_a_dead_key_below_zero()
    {
        string read = Inner(Code(HookFile), "        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)");

        int called = read.IndexOf("ToUnicodeEx(", StringComparison.Ordinal);
        int dead = Place(read, "deadKey = written < 0;");
        int character = Place(read, "character = written == 1 ? _characters[0] : '\\0';");
        Assert.True(called < dead && dead < character);
        Assert.Matches(@"int written = ToUnicodeEx\(", read);
    }

    [Fact]
    public void A_held_key_is_one_whose_state_is_below_zero_in_both_files()
    {
        // GetAsyncKeyState: the top bit is set while the key is down, which makes the answer
        // negative. The other bits say nothing reliable, so "!= 0" or "& 1" would be wrong.
        Assert.Matches(@"private static bool IsDown\(int virtualKey\) => GetAsyncKeyState\(virtualKey\) < 0;", Code(HookFile));
        Assert.Matches(@"private static bool IsDown\(ushort virtualKey\) => GetAsyncKeyState\(virtualKey\) < 0;", Code(KeysFile));

        // The one call is in IsDown. The other mention is the declaration.
        Assert.Equal(2, Regex.Matches(Code(HookFile), @"\bGetAsyncKeyState\(").Count);
        Assert.Equal(2, Regex.Matches(Code(KeysFile), @"\bGetAsyncKeyState\(").Count);
    }

    [Fact]
    public void The_key_state_calls_are_made_in_the_two_files_that_look_at_held_keys_and_nowhere_else()
    {
        // GetKeyState is covered above: the hook file alone. GetAsyncKeyState is the other
        // way to read the keyboard, and the launcher's paste and the typed keyword look at
        // modifiers with it, so these two files and no other name it.
        Assert.Equal([KeysFile, HookFile], FilesThatName(@"\bGetAsyncKeyState\b"));
    }

    [Fact]
    public void A_failed_SetWindowsHookEx_is_a_failure_that_is_thrown()
    {
        string hook = Inner(Code(HookFile), "        private void Hook()");

        Assert.Matches(
            @"_hook = SetWindowsHookEx\(LowLevelKeyboard, _procedure, GetModuleHandle\(null\), 0\);\s*if \(_hook == IntPtr\.Zero\)\s*\{\s*throw new Win32Exception\(Marshal\.GetLastWin32Error\(\)\);\s*\}",
            hook);
    }

    [Fact]
    public void The_hook_is_asked_for_and_given_up_on_the_thread_it_was_made_on()
    {
        string code = Code(HookFile);

        // Install and Remove are for the UI thread, and refuse any other.
        Assert.StartsWith(
            "    public void Install(Action<TypedKey> keyDown)\n    {\n        _made.VerifyAccess();",
            Member(code, "    public void Install(Action<TypedKey> keyDown)").ReplaceLineEndings("\n"));
        Assert.StartsWith(
            "    public bool Remove()\n    {\n        _made.VerifyAccess();",
            Member(code, "    public bool Remove()").ReplaceLineEndings("\n"));
        Assert.Contains("private readonly Dispatcher _made = Dispatcher.CurrentDispatcher;", code);
    }

    [Fact]
    public void Each_hook_is_installed_and_removed_on_a_thread_of_its_own()
    {
        string code = Code(HookFile);

        // One thread for one hook. Windows is asked for the hook as that thread begins and
        // gives it up from inside that thread's loop: the two calls stand nowhere else.
        Place(code, "public void Start() => _thread = LoopThread.Start(\"Beacon keyboard hook\", Hook, Unhook);");
        Assert.Contains("_hook = SetWindowsHookEx(", Inner(code, "        private void Hook()"));
        Assert.Contains("_known = UnhookWindowsHookEx(hook);", Inner(code, "        private void Unhook()"));
        Assert.Equal(2, Regex.Matches(code, @"\bSetWindowsHookEx\(").Count);
        Assert.Equal(2, Regex.Matches(code, @"\bUnhookWindowsHookEx\(").Count);

        // A hook that is asked for gets a new thread, counted before it is started, and counts
        // as installed only once that thread has said so.
        string install = Member(code, "    public void Install(Action<TypedKey> keyDown)");
        int made = Place(install, "var listening = new Listening(this, keyDown);");
        int counted = Place(install, "Threads++;");
        int started = Place(install, "listening.Start();");
        int kept = Place(install, "_listening = listening;");
        int installed = Place(install, "Installs++;");
        Assert.True(made < counted && counted < started && started < kept && kept < installed);

        // Removing ends the thread, and answers what Windows said on that thread.
        string remove = Member(code, "    public bool Remove()");
        Assert.True(Place(remove, "_listening = null;") < Place(remove, "return listening.Stop();"));
        Assert.Matches(@"public bool Stop\(\)\s*\{\s*_thread!\.Stop\(\);\s*GC\.KeepAlive\(_procedure\);\s*return _known;", code);
    }

    [Fact]
    public void The_hooks_thread_is_a_background_thread_that_runs_a_message_loop_and_nothing_else()
    {
        string loop = Code(LoopFile);

        // A background thread never keeps Beacon from closing.
        Place(loop, "_thread = new Thread(Run) { IsBackground = true, Name = name };");

        // On that thread: its message queue, then what it is to begin with, then the loop.
        string run = Member(loop, "    private void Run()");
        int queue = Place(run, "_loop = Dispatcher.CurrentDispatcher;");
        int begin = Place(run, "_begin();");
        int told = Place(run, "_begun.Set();");
        int pumping = Place(run, "Dispatcher.Run();");
        Assert.True(queue < begin && begin < told && told < pumping);

        // It makes no Windows call of its own and knows nothing of keys.
        Assert.DoesNotContain("DllImport", loop);
        Assert.DoesNotContain("extern", loop);
        Assert.DoesNotContain("TypedKey", loop);

        // And only the keyboard hook uses it.
        Assert.Equal([HookFile, LoopFile], FilesThatName(@"\bLoopThread\b"));
    }

    [Fact]
    public void The_hooks_thread_holds_its_own_object_for_as_long_as_its_loop_runs()
    {
        // While the loop runs, this thread holds the LoopThread, and through it the end
        // action, the installed hook's object and the callback Windows was given, whatever the
        // UI side does with its references. Without the line the object is not used after
        // Dispatcher.Run begins, and a callback that is collected while its hook is installed
        // crashes the process at the next key.
        string run = Member(Code(LoopFile), "    private void Run()");
        Assert.Matches(@"Dispatcher\.Run\(\);\s*GC\.KeepAlive\(this\);", run);
        Assert.Single(Regex.Matches(run, @"GC\.KeepAlive\("));
    }

    [Fact]
    public void The_hooks_thread_runs_above_normal_priority()
    {
        // Every key press in every program waits for this thread to answer.
        string loop = Code(LoopFile);
        string made = Member(loop, "    private LoopThread(string name, Action begin, Action end)");
        int created = Place(made, "_thread = new Thread(Run) { IsBackground = true, Name = name };");
        int priority = Place(made, "_thread.Priority = ThreadPriority.AboveNormal;");
        Assert.True(created < priority);

        // The one place a priority is set, and not a higher one.
        Assert.Single(Regex.Matches(loop, @"ThreadPriority\."));
    }

    [Theory]
    [InlineData(".Wait(")]
    [InlineData("Join(")]
    [InlineData("lock (")]
    [InlineData("Monitor.")]
    [InlineData("Sleep")]
    [InlineData(".Invoke(")]
    [InlineData("WaitOne")]
    [InlineData(".Result")]
    public void The_hooks_thread_never_waits_for_another_thread(string never)
    {
        // What runs on the hook's thread besides the hook itself is Run. A key press in
        // another program waits for this thread, so this thread waits for nobody.
        Assert.DoesNotContain(never, Member(Code(LoopFile), "    private void Run()"), StringComparison.Ordinal);
    }

    [Fact]
    public void Starting_and_stopping_the_hooks_thread_wait_for_it_with_a_bound()
    {
        string loop = Code(LoopFile);

        Assert.Contains("public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(5);", loop);
        Assert.Contains("public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);", loop);

        // Starting: a thread that does not answer in time is given up on, and is told so in a
        // way that only one of the two threads can win, so that it undoes what it began.
        string start = Member(loop, "    public static LoopThread Start(string name, Action begin, Action end)");
        Assert.Matches(
            @"if \(!made\._begun\.Wait\(StartWait\)\s*&& Interlocked\.CompareExchange\(ref made\._state, Abandoned, Starting\) == Starting\)\s*\{\s*throw new TimeoutException\(",
            start);
        string run = Member(loop, "    private void Run()");
        Place(run, "bool waitedFor = Interlocked.CompareExchange(ref _state, Running, Starting) == Starting;");
        Assert.Matches(@"if \(error is null && !waitedFor\)\s*\{[^}]*try\s*\{\s*_end\(\);", run);

        // Stopping: the removal is handed to the hook's own thread, which then ends its loop,
        // and the thread is waited for, for two seconds and no longer.
        string stop = Member(loop, "    public void Stop()");
        int handed = Place(stop, "_loop!.BeginInvoke(DispatcherPriority.Send, new Action(() =>");
        int removed = Place(stop, "_end();");
        int ended = Place(stop, "Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);");
        int waited = Place(stop, "if (!_thread.Join(StopWait))");
        Assert.True(handed < removed && removed < ended && ended < waited);
        Assert.Matches(@"if \(!_thread\.Join\(StopWait\)\)\s*\{\s*throw new TimeoutException\(", stop);
        Assert.DoesNotContain("Join()", loop);
        Assert.DoesNotContain(".Invoke(", loop);
    }

    [Fact]
    public void A_typed_keyword_is_taken_away_and_pasted_in_one_call_after_the_window_and_the_held_keys_were_looked_at()
    {
        string typed = Member(Code(KeysFile), "    private sealed class TypedKeyword(ForegroundWindowTarget owner, IntPtr window, int erase) : IPasteTarget");

        int window = Place(typed, "if (!owner.IsInFront(window))");
        int held = Place(typed, "if (AnyModifierDown())");
        int sent = Place(typed, "Send(presses);");

        // The target looks for itself, whatever its caller has looked at. Then one call hands
        // Windows the Backspaces and the Ctrl+V together, and it is the only call that sends
        // anything: the keyword cannot be taken away without the paste.
        Assert.True(window < held && held < sent);
        Assert.Single(Regex.Matches(typed, @"\bSend\("));
        Assert.DoesNotContain("SendInput", typed);
        Place(typed, "presses[next++] = Key(BackspaceKey, 0);");
        Place(typed, "presses[next++] = Key(BackspaceKey, KeyUp);");
        Assert.True(typed.IndexOf("Key(BackspaceKey, KeyUp);", StringComparison.Ordinal) < typed.IndexOf("Key(ControlKey, 0);", StringComparison.Ordinal));

        // It moves no focus: the window is in front already, or nothing is pressed.
        Assert.DoesNotContain("SetForegroundWindow", typed);
        Place(typed, "public bool Activate() => owner.IsInFront(window);");
    }

    [Fact]
    public void The_key_events_of_a_typed_keyword_are_made_before_the_last_looks_and_nothing_but_the_call_follows_them()
    {
        string typed = Member(Code(KeysFile), "    private sealed class TypedKeyword(ForegroundWindowTarget owner, IntPtr window, int erase) : IPasteTarget");
        string send = Inner(typed, "        public void SendPaste()");

        // The array, with its calls to MapVirtualKey, is built first. Then the window and the
        // held keys are looked at, so that the moment between the last look and SendInput
        // holds nothing of the work of building the keys.
        int built = Place(send, "new Input[");
        int window = Place(send, "if (!owner.IsInFront(window))");
        int held = Place(send, "if (AnyModifierDown())");
        Assert.True(built < window && window < held);

        // Nothing is built after the first look: every Key( call stands before it, and so
        // does the loop that fills the array.
        Assert.True(send.LastIndexOf("Key(", StringComparison.Ordinal) < window);
        Assert.True(Place(send, "for (int i = 0; i < erase; i++)") < window);

        // Between the second look's throw and the call nothing stands but the call itself.
        Assert.Matches(
            @"if \(AnyModifierDown\(\)\)\s*\{\s*throw new PasteCalledOffException\(\);\s*\}\s*Send\(presses\);\s*$",
            send);
    }

    [Fact]
    public void The_arrays_that_hold_typed_characters_are_allocated_where_the_garbage_collector_does_not_move_them()
    {
        // A compacting collection copies an ordinary array and leaves the old copy, with the
        // typed characters in it, in freed memory, so that overwriting the live copy would not
        // be all of what is held. GC.AllocateArray with pinned: true gives a zeroed array that
        // is never moved. A behaviour test cannot see that, so the source is read.
        //
        // TypingBuffer.cs is read here, with the other two, because TypingBufferTests has no
        // helper for reading a source file. Its only array of characters is the one that holds
        // them; one of length 0 may stay as it is (a collection expression).
        string buffer = Code("Beacon.Core/AutoExpand/TypingBuffer.cs");
        int made = Regex.Matches(buffer, @"GC\.AllocateArray<char>\(").Count;
        Assert.True(made >= 1, "TypingBuffer no longer allocates its character array with GC.AllocateArray.");
        Assert.Equal(made, Regex.Matches(buffer, @"GC\.AllocateArray<char>\([^;]*\bpinned: true\)").Count);
        Assert.DoesNotMatch(@"new char\[\s*[^\]\s]", buffer);

        // The key state and the characters ToUnicodeEx writes, in the hook.
        string hook = Code(HookFile);
        Assert.Contains("private readonly byte[] _keyState = GC.AllocateArray<byte>(256, pinned: true);", hook);
        Assert.Contains("private readonly char[] _characters = GC.AllocateArray<char>(8, pinned: true);", hook);
        Assert.Equal(2, Regex.Matches(hook, @"GC\.AllocateArray<").Count);
        Assert.DoesNotMatch(@"new (?:byte|char)\[", hook);
    }

    [Fact]
    public void A_typed_keyword_that_can_no_longer_be_replaced_is_called_off_and_not_failed()
    {
        string code = Code(KeysFile);
        string typed = Member(code, "    private sealed class TypedKeyword(ForegroundWindowTarget owner, IntPtr window, int erase) : IPasteTarget");

        // The paste service takes PasteCalledOffException for a paste that is not made after
        // all: nothing is pressed, the clipboard is put back and nothing is said. The other two
        // exceptions it takes for a key press that Windows refused, which is a notification and
        // an error line, and the snippet left on the clipboard.
        Assert.Matches(@"if \(!owner\.IsInFront\(window\)\)\s*\{\s*throw new PasteCalledOffException\(\);\s*\}", typed);
        Assert.Matches(@"if \(AnyModifierDown\(\)\)\s*\{\s*throw new PasteCalledOffException\(\);\s*\}", typed);
        Assert.Equal(2, Regex.Matches(typed, @"\bthrow\b").Count);
        Assert.Equal(2, Regex.Matches(typed, @"throw new PasteCalledOffException\(\);").Count);
        Assert.True(typed.LastIndexOf("throw new PasteCalledOffException();", StringComparison.Ordinal) < typed.IndexOf("Send(presses);", StringComparison.Ordinal));
        Assert.DoesNotContain("AnotherWindowInFrontException", typed);
        Assert.DoesNotContain("ModifierKeyHeldException", typed);

        // The launcher's own paste is another matter: a key press that is refused there is
        // told to the owner, and it still throws the two.
        string launcher = Member(code, "\n    public void SendPaste()\n");
        Assert.Contains("throw new NoWindowRememberedException();", launcher);
        Assert.Contains("throw new AnotherWindowInFrontException();", launcher);
        Assert.Contains("throw new ModifierKeyHeldException();", launcher);
        Assert.DoesNotContain("PasteCalledOffException", launcher);
    }

    [Fact]
    public void When_Windows_took_the_keys_up_to_and_past_Beacons_own_Ctrl_down_that_Ctrl_is_let_go_before_the_failure_is_thrown_with_the_first_calls_error()
    {
        string send = Member(Code(KeysFile), "    private static void Send(Input[] presses)");

        // The documentation says how many events were inserted, and not that it is all or
        // none. If it can be some, a Ctrl that Beacon pressed may have gone down without
        // coming up. The first call's error number is taken before the second call can change
        // it. The key-up is sent only when the first Ctrl key-down of Beacon's own among the
        // events stands among those that were inserted: its index is 0 or more, and below
        // what was inserted. (For a typed keyword the Ctrl key-down stands behind the
        // Backspaces; when Windows took only Backspaces, no Ctrl went down and none is let
        // go. When nothing was inserted, nothing more is sent.) Its result is not looked at.
        int first = Place(send, "uint sent = SendInput((uint)presses.Length, presses, Marshal.SizeOf<Input>());");
        int shortfall = Place(send, "if (sent != presses.Length)");
        int taken = Place(send, "int error = Marshal.GetLastWin32Error();");
        int index = Place(send, "int ownControl = IndexOfOwnControlDown(presses);");
        int some = Place(send, "if (ownControl >= 0 && ownControl < sent)");
        int released = Place(send, "_ = SendInput(1, [Key(ControlKey, KeyUp)], Marshal.SizeOf<Input>());");
        int thrown = Place(send, "throw new Win32Exception(error);");
        Assert.True(first < shortfall && shortfall < taken && taken < index && index < some && some < released && released < thrown);
        Assert.Matches(@"int ownControl = IndexOfOwnControlDown\(presses\);\s*if \(ownControl >= 0 && ownControl < sent\)\s*\{\s*_ = SendInput\(1, \[Key\(ControlKey, KeyUp\)\], Marshal\.SizeOf<Input>\(\)\);\s*\}\s*throw new Win32Exception\(error\);", send);
        Assert.Single(Regex.Matches(send, @"GetLastWin32Error"));
        Assert.Equal(2, Regex.Matches(send, @"\bSendInput\(").Count);

        // Only a failure of the whole call is thrown: a call that inserted everything is not.
        Assert.Matches(@"if \(sent != presses\.Length\)\s*\{\s*int error", send);
    }

    [Fact]
    public void Where_Beacons_own_Ctrl_key_down_stands_among_the_events_is_read_from_the_events_and_not_from_the_keyboard()
    {
        string code = Code(KeysFile);
        string own = Member(code, "    private static int IndexOfOwnControlDown(Input[] presses)");

        // The index of the first key-down of the Ctrl key among the events that were handed
        // over: the Ctrl key, and no key-up flag; -1 when there is none. No native call is
        // made here and no key state is asked.
        Assert.Matches(
            @"for \(int i = 0; i < presses\.Length; i\+\+\)\s*\{\s*if \(presses\[i\]\.Data\.Keyboard\.VirtualKey == ControlKey && \(presses\[i\]\.Data\.Keyboard\.Flags & KeyUp\) == 0\)\s*\{\s*return i;\s*\}\s*\}\s*return -1;",
            own);
        Assert.DoesNotContain("SendInput", own);
        Assert.DoesNotContain("IsDown", own);
        Assert.DoesNotContain("GetAsyncKeyState", own);
    }

    [Fact]
    public void The_launchers_paste_with_the_owners_own_Ctrl_held_presses_no_Ctrl_so_none_is_let_go_after_a_partial_press()
    {
        string code = Code(KeysFile);
        string launcher = Member(code, "\n    public void SendPaste()\n");

        // With the owner holding Ctrl, only V goes down and up: no Ctrl key-down of ours is in
        // the events, so IndexOfOwnControlDown answers -1 for them, and a key-up of Ctrl here
        // would let go, for Windows, of a key the owner is still holding.
        var held = Regex.Match(launcher, @"if \(IsDown\(ControlKey\)\)\s*\{\s*presses =\s*\[(?<events>[^\]]*)\];\s*\}");
        Assert.True(held.Success, "The branch for a Ctrl that the owner holds is gone.");
        Assert.Equal(
            ["Key(VKey, 0)", "Key(VKey, KeyUp)"],
            Regex.Matches(held.Groups["events"].Value, @"Key\([^)]*\)").Select(match => match.Value).ToList());
        Assert.DoesNotContain("ControlKey", held.Groups["events"].Value);

        // Without it Beacon presses its own Ctrl, and so does the typed keyword's paste.
        Assert.Contains("Key(ControlKey, 0),", launcher);
        string typed = Member(code, "    private sealed class TypedKeyword(ForegroundWindowTarget owner, IntPtr window, int erase) : IPasteTarget");
        Assert.Contains("presses[next++] = Key(ControlKey, 0);", typed);
    }

    [Fact]
    public void A_keyword_is_replaced_only_in_the_window_in_front_and_with_no_Shift_Ctrl_Alt_or_Windows_key_held()
    {
        string code = Code(KeysFile);

        // With Ctrl held, Backspace takes away a whole word.
        Assert.Matches(
            @"private static bool AnyModifierDown\(\) =>\s*IsDown\(ShiftKey\) \|\| IsDown\(ControlKey\) \|\| IsDown\(AltKey\) \|\| IsDown\(LeftWindowsKey\) \|\| IsDown\(RightWindowsKey\);",
            code);
        Place(code, "public bool ModifierHeld() => AnyModifierDown();");

        // The window must be the one in front, and one a text can be pasted into: not Beacon's
        // own, not the desktop, not the taskbar. Asking moves nothing.
        Assert.Matches(
            @"public bool IsInFront\(nint window\) =>\s*window != IntPtr\.Zero\s*&& GetForegroundWindow\(\) == window\s*&& CanBePastedInto\(window\);",
            code);
    }

    [Fact]
    public void Backspace_Ctrl_and_V_are_the_only_keys_Beacon_ever_presses()
    {
        string code = Code(KeysFile);
        var keys = Regex.Matches(code, @"\bKey\((\w+), ")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["BackspaceKey", "ControlKey", "VKey"], keys);
        Assert.Contains("private const ushort BackspaceKey = 0x08;", code);
        Assert.Contains("private const ushort ControlKey = 0x11;", code);
        Assert.Contains("private const ushort VKey = 0x56;", code);

        // A key press is never made from a character (KEYEVENTF_UNICODE), which is how text would be typed out.
        Assert.DoesNotContain("0x0004", code);

        // The declaration, the one call that sends the keys, and the one that lets Ctrl go
        // when Windows took only some of them.
        Assert.Equal(3, Regex.Matches(code, @"\bSendInput\(").Count);
    }

    [Fact]
    public void The_structures_SendInput_is_given_are_laid_out_as_Windows_reads_them_and_the_two_values_in_them_are_those_of_Windows()
    {
        string code = Code(KeysFile);

        // INPUT: the type, then the union of the three kinds of input. SendInput is told the
        // size of one, and reads the keyboard input from the offset of the union.
        Assert.Matches(@"\[StructLayout\(LayoutKind\.Sequential\)\]\s*private struct Input\s*\{", code);
        Assert.Equal(["uint Type", "InputData Data"], Fields(code, "Input"));
        Assert.Matches(
            @"\[StructLayout\(LayoutKind\.Explicit\)\]\s*private struct InputData\s*\{\s*\[FieldOffset\(0\)\]\s*public MouseInput Mouse;\s*\[FieldOffset\(0\)\]\s*public KeyboardInput Keyboard;\s*\}",
            code);
        Assert.Equal(2, Regex.Matches(code, @"\bFieldOffset\(").Count);

        // KEYBDINPUT: wVk, wScan, dwFlags, time, dwExtraInfo. In another order a call would
        // press the key whose number is the scan code.
        Assert.Matches(@"\[StructLayout\(LayoutKind\.Sequential\)\]\s*private struct KeyboardInput\s*\{", code);
        Assert.Equal(["ushort VirtualKey", "ushort ScanCode", "uint Flags", "uint Time", "IntPtr ExtraInfo"], Fields(code, "KeyboardInput"));

        // MOUSEINPUT: never filled in, but it is the largest of the three, so it gives the
        // union, and with it INPUT, the size Windows checks.
        Assert.Matches(@"\[StructLayout\(LayoutKind\.Sequential\)\]\s*private struct MouseInput\s*\{", code);
        Assert.Equal(["int X", "int Y", "uint MouseData", "uint Flags", "uint Time", "IntPtr ExtraInfo"], Fields(code, "MouseInput"));

        // INPUT_KEYBOARD is 1, and KEYEVENTF_KEYUP is 0x0002; a key press is made with the
        // first, and every event is of the type it names.
        Assert.Contains("private const uint Keyboard = 1;", code);
        Assert.Contains("private const uint KeyUp = 0x0002;", code);
        Place(code, "Type = Keyboard,");
    }

    [Fact]
    public void The_two_calls_whose_error_number_is_read_afterwards_ask_for_it_to_be_kept()
    {
        // SendInput: Send reads Marshal.GetLastWin32Error after it. SetWindowsHookEx: Hook does
        // the same when it returns zero. Without SetLastError = true the number is not the one
        // of that call, and the failure is logged with a number that says nothing.
        Assert.Matches(@"\[DllImport\([^\]]*\bSetLastError = true[^\]]*\]\s*private static extern uint SendInput\(", Code(KeysFile));
        Assert.Matches(@"\[DllImport\([^\]]*\bSetLastError = true[^\]]*\]\s*private static extern IntPtr SetWindowsHookEx\(", Code(HookFile));
    }

    [Fact]
    public void The_key_state_a_key_is_translated_under_holds_the_three_Shift_keys_and_the_Caps_Lock_toggle()
    {
        string read = Inner(Code(HookFile), "        private TypedKey Read(uint virtualKey, uint scanCode, uint flags)");

        // Shift as it is held (either, the left and the right, each asked of IsDown, which
        // reads the top bit) and Caps Lock as it is toggled (the low bit of GetKeyState). All
        // are written into the key state inside the guard, before the translation: without
        // them a capital, or a sign that needs Shift, would be translated as a small letter.
        int guard = Place(read, "if (window != IntPtr.Zero && virtualKey is > 0 and < 256)");
        int translated = read.IndexOf("ToUnicodeEx(", StringComparison.Ordinal);
        foreach (string line in new[]
        {
            "_keyState[KeyCodes.Shift] = IsDown(KeyCodes.Shift) ? Down : (byte)0;",
            "_keyState[KeyCodes.LeftShift] = IsDown(KeyCodes.LeftShift) ? Down : (byte)0;",
            "_keyState[KeyCodes.RightShift] = IsDown(KeyCodes.RightShift) ? Down : (byte)0;",
            "_keyState[KeyCodes.CapsLock] = (GetKeyState(KeyCodes.CapsLock) & 1) != 0 ? ToggledOn : (byte)0;",
        })
        {
            int at = Place(read, line);
            Assert.True(guard < at && at < translated, $"Not inside the guard and before the translation: {line}");
        }
    }

    [Fact]
    public void The_hooks_thread_is_given_no_apartment()
    {
        // The thread is for a keyboard hook and WPF's Dispatcher loop, and uses no COM, so it
        // is given no apartment state of its own. A line that sets one is a change to how the
        // thread runs that no test, and nobody, has looked at.
        string loop = Code(LoopFile);
        Assert.DoesNotContain("SetApartmentState", loop);
        Assert.DoesNotContain("ApartmentState.STA", loop);
    }

    [Fact]
    public void The_focus_is_moved_in_one_place_only_which_is_the_launchers_paste()
    {
        string code = Code(KeysFile);

        // The call in the launcher's Activate, and the declaration. (The line is looked for
        // with its line break in front, to tell it from the typed keyword's own Activate,
        // which stands four spaces further in.)
        Assert.Equal(2, Regex.Matches(code, @"\bSetForegroundWindow\(").Count);
        Assert.Contains("return SetForegroundWindow(_remembered);", Member(code, "\n    public bool Activate()\n"));
    }
}
