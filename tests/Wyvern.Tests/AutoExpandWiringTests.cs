using System.Text.RegularExpressions;

namespace Wyvern.Tests;

/// <summary>
/// The shell is the app, and no test can run it. How it joins snippet auto-expand to the rest
/// is a matter of safety all the same: that the keyboard hook is handed to the expander and to
/// nothing else, that it follows the setting and a setting that could be read, that a keyword
/// is pasted through the one paste service, and that the hook is the first thing to go when
/// Wyvern closes. So the shell's source is read as text here, as <see cref="SyncWiringTests"/>
/// reads it for sync.
/// </summary>
public class AutoExpandWiringTests
{
    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Wyvern.slnx")))
        {
            folder = folder.Parent;
        }

        Assert.True(folder is not null, $"Wyvern.slnx was not found above {AppContext.BaseDirectory}");
        return folder.FullName;
    }

    private static string Shell() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wyvern.App", "WyvernShell.cs")).ReplaceLineEndings("\n");

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

    // Where a piece of the shell's code is. It must be there exactly once, so that "before" and
    // "after" below mean something.
    private static int Place(string source, string code)
    {
        int first = source.IndexOf(code, StringComparison.Ordinal);
        Assert.True(first >= 0, $"The shell does not hold: {code}");
        Assert.True(source.IndexOf(code, first + code.Length, StringComparison.Ordinal) < 0, $"The shell holds this more than once: {code}");
        return first;
    }

    // The body of one method of the shell: from its first line to the first line after it that
    // closes a member (a brace four spaces in).
    private static string Method(string source, string firstLine)
    {
        int start = Place(source, firstLine);
        int end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The end of the method was not found: {firstLine}");
        return source[start..end];
    }

    // One block of a method of the shell, closing brace included: from the line that opens it
    // (an if at the method's own level, eight spaces in) to the first line after it that closes
    // a block at that level.
    private static string Block(string method, string openingLine)
    {
        int start = Place(method, openingLine);
        const string Closing = "\n        }\n";
        int end = method.IndexOf(Closing, start, StringComparison.Ordinal);
        Assert.True(end > start, $"The end of the block was not found: {openingLine}");
        return method[start..(end + Closing.Length - 1)];
    }

    // Code as the compiler reads it: the lines that are only a comment are left out.
    private static string WithoutComments(string text) =>
        string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // What the shell hands the expander when it makes it.
    private static string Made(string shell)
    {
        int start = Place(shell, "_autoExpand = new AutoExpander(");
        return shell[start..shell.IndexOf(");", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void The_keyboard_hook_is_made_once_and_handed_to_auto_expand_and_to_nothing_else()
    {
        string shell = Shell();

        Place(shell, "private readonly KeyboardHook _keyboardHook;");
        Place(shell, "_keyboardHook = new KeyboardHook(");
        Assert.StartsWith("_autoExpand = new AutoExpander(\n            _keyboardHook,\n            _pasteTarget,\n            paste,\n            clock,", Made(shell));

        // The field is named where it is declared, where it is made and where it is handed
        // over, and nowhere else: the shell itself never installs it, removes it or asks it
        // anything.
        Assert.Equal(3, Regex.Matches(shell, @"\b_keyboardHook\b").Count);
        Assert.Single(Regex.Matches(shell, @"new KeyboardHook\("));
        Assert.DoesNotContain(".Install(", shell);
        Assert.DoesNotContain("SetWindowsHookEx", shell);

        // And no other file of the app or of Wyvern.Core makes one or names the class.
        var files = SourceFiles();
        Assert.Contains(files, file => file.Path == "Wyvern.App/Interop/KeyboardHook.cs");
        var namesIt = files
            .Where(file => file.Path is not "Wyvern.App/WyvernShell.cs" and not "Wyvern.App/Interop/KeyboardHook.cs" && Regex.IsMatch(file.Code, @"\bKeyboardHook\b"))
            .Select(file => file.Path)
            .ToList();
        Assert.True(namesIt.Count == 0, $"These files name the keyboard hook: {string.Join(", ", namesIt)}");
    }

    [Fact]
    public void A_failure_inside_the_hooks_callback_is_posted_to_be_logged_after_the_callback_has_returned()
    {
        // The hook hands on the first thing that goes wrong inside its callback, on the hook's
        // own thread. Every key press in every program is waiting at that moment, so the shell
        // does not write the log there and does not wait for the UI thread: it posts with
        // BeginInvoke. The log gets fixed words and the exception, whose type and error number
        // are written and whose message never is.
        Place(
            Shell(),
            "_keyboardHook = new KeyboardHook(\n            error => _app.Dispatcher.BeginInvoke(() => _log.Error(\"The keyboard hook could not look at a key press\", error)));");
    }

    [Fact]
    public void The_hook_is_installed_by_the_expander_alone()
    {
        // In all of src, one call installs a hook: the expander's, which it makes only while
        // the setting is on. The other mentions are the interface and the class that does it.
        var installs = SourceFiles()
            .Where(file => Regex.IsMatch(file.Code, @"\.Install\("))
            .Select(file => file.Path)
            .ToList();

        Assert.Equal(["Wyvern.Core/AutoExpand/AutoExpander.cs"], installs);
        string expander = SourceFiles().Single(file => file.Path == "Wyvern.Core/AutoExpand/AutoExpander.cs").Code;
        Assert.Single(Regex.Matches(expander, @"\.Install\("));
    }

    [Fact]
    public void A_typed_keyword_is_pasted_through_the_one_paste_service_of_the_app()
    {
        string shell = Shell();

        // One paste service is made in all of src, with the clipboard writer that marks what
        // it writes, and the launcher's snippets, the clipboard history and auto-expand are all
        // given that one. A second one would not know of a paste the first is still finishing,
        // and would put the wrong text back.
        Assert.Single(SourceFiles().SelectMany(file => Regex.Matches(file.Code, @"new PasteService\(")));
        Place(shell, "_clipboard = new RetryingClipboard(_windowsClipboard, Thread.Sleep);");
        Place(shell, "var paste = new PasteService(clipboard, _pasteTarget, Later, Notify, (message, error) => _log.Error(message, error));");
        Place(shell, "var snippets = new SnippetProvider(() => _library.Current, paste, clipboard, _processes, clock, edit: EditSnippet, delete: DeleteSnippet);");
        Place(shell, "var paster = new ClipPaster(paste, codec, Notify, Report);");
        Assert.Contains("\n            paste,\n", Made(shell));
    }

    [Fact]
    public void The_replacement_is_posted_to_run_after_the_callback_and_what_is_said_goes_to_the_queue_and_the_log()
    {
        string made = Made(Shell());

        // Each callback is given by the name of its parameter: two of them take an action, so
        // by position a swap would still compile. The replacement is handed to the UI thread
        // with BeginInvoke, which returns at once. Never Invoke: that would make the hook's
        // thread, and with it the key press in the other program, wait for the UI thread.
        Assert.Contains("post: action => _app.Dispatcher.BeginInvoke(action),", made);
        Assert.Contains("later: Later,", made);
        Assert.Contains("notify: Notify,", made);
        Assert.EndsWith("report: Report", made);
        Assert.DoesNotContain("Dispatcher.Invoke(", made);
    }

    [Fact]
    public void The_expander_is_made_before_the_settings_are_applied_and_installs_nothing_until_then()
    {
        string shell = Shell();

        Assert.True(Place(shell, "_autoExpand = new AutoExpander(") < Place(shell, "ApplySettings(firstLoad: true);"));

        // The shell calls two things on it, and lets go of it when Wyvern closes. Nothing else:
        // Configure twice (taking the hook out where the switch goes off, and as the last step
        // of applying the settings) and UseLibrary once. No Install, no other member is named.
        Assert.Equal(
            ["Configure", "Configure", "UseLibrary"],
            Regex.Matches(shell, @"_autoExpand\.(\w+)").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToList());
        Place(shell, "_autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);");
        Place(shell, "_autoExpand.Configure(false, []);");
        Place(shell, "_autoExpand.UseLibrary(_library.Current.Snippets);");
        Place(shell, "Release(\"snippet auto-expand\", _autoExpand);");
    }

    [Fact]
    public void The_switch_is_off_until_a_settings_json_that_could_be_read_says_otherwise()
    {
        string shell = Shell();
        string applying = Method(shell, "    private void ApplySettings(bool firstLoad)");

        // No initial value: false. It is written in one place, from a decision that carries the
        // setting, which a settings.json that could not be read does not. What it was before
        // the write is kept first, to tell a switch from on to off.
        Place(shell, "private bool _autoExpandOn;");
        Assert.Single(Regex.Matches(shell, @"_autoExpandOn\s*="));
        Assert.Matches(@"if \(decision\.AutoExpand is \{ \} autoExpandOptions\)\s*\{\s*bool wasOn = _autoExpandOn;\s*_autoExpandOn = autoExpandOptions\.Enabled;", applying);
    }

    [Fact]
    public void Switching_off_takes_the_hook_out_in_the_block_that_keeps_the_switch_and_not_behind_the_rest_of_the_reload()
    {
        string shell = Shell();
        string applying = Method(shell, "    private void ApplySettings(bool firstLoad)");
        const string Opening = "        if (decision.AutoExpand is { } autoExpandOptions)";
        string block = Block(applying, Opening);
        int blockEnd = Place(applying, Opening) + block.Length;

        // Configure is called in two places in the shell. The first is in the block that keeps
        // the switch: only when the switch was on before this write and the settings now say
        // off, with false and no snippets, and nothing around the call. Not at every start and
        // not at every Reload with the setting off, so that the log's off line is not doubled
        // each time.
        Assert.Equal(2, Regex.Matches(shell, @"_autoExpand\.Configure\(").Count);
        Assert.Matches(
            @"bool wasOn = _autoExpandOn;\s*_autoExpandOn = autoExpandOptions\.Enabled;\s*if \(wasOn && !_autoExpandOn\)\s*\{\s*_autoExpand\.Configure\(false, \[\]\);\s*\}",
            WithoutComments(block));

        // The second is the last statement of ApplySettings, as it was: switching on is still
        // the last thing a Reload does.
        int last = Place(applying, "_autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);");
        Assert.True(blockEnd < last);
        Assert.EndsWith("_autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);", applying.TrimEnd());

        // And the first stands ahead of everything else that is applied: the hotkey is
        // registered below it.
        Assert.True(Place(applying, Opening) < Place(applying, "_hotkeys.Register(hotkey)"), "The switch block does not stand ahead of the registration of the hotkey.");
        Assert.True(Place(applying, "_autoExpand.Configure(false, []);") < Place(applying, "_hotkeys.Register(hotkey)"), "The hook is not taken out ahead of the registration of the hotkey.");
    }

    [Fact]
    public void The_switch_is_kept_directly_after_the_report_of_a_file_that_could_not_be_read_and_before_everything_else_is_applied()
    {
        string applying = Method(Shell(), "    private void ApplySettings(bool firstLoad)");
        const string Unreadable = "        if (loaded.Error is not null)";
        const string Opening = "        if (decision.AutoExpand is { } autoExpandOptions)";
        int unreadableEnd = Place(applying, Unreadable) + Block(applying, Unreadable).Length;
        int opening = Place(applying, Opening);

        // Directly behind the block for a settings.json that could not be read: nothing but
        // blank lines and the block's own comment stands between the two. The comment above
        // the switch says that the file is read, an unreadable one is reported, and then,
        // before anything else is applied, the switch is set and a hook that is no longer
        // wanted is taken out. That is true only while this holds.
        Assert.True(opening > unreadableEnd, "The switch block does not stand behind the block for a file that could not be read.");
        Assert.Equal("", WithoutComments(applying[unreadableEnd..opening]).Trim());

        // And ahead of every statement that applies a setting. The hotkey registration is the
        // one that used to stand first of them; the others are named so that none can be put
        // in front of the switch.
        string[] applied =
        [
            "if (decision.HotkeyInvalid)",
            "_clipboardHotkeys.Clear();",
            "_hotkeys.Register(hotkey)",
            "_clipboardHotkeys.Register(clipboardHotkey, clipboardHotkeyBefore)",
            "StartupRegistration.Apply(startWithWindows);",
            "_files.Configure(fileOptions)",
            "_scriptOptions = scriptOptions;",
            "_clipboardOptions = clipboardOptions;",
            "_syncOptions = syncOptions;",
        ];
        foreach (string statement in applied)
        {
            Assert.True(opening < Place(applying, statement), $"The switch block does not stand ahead of: {statement}");
        }
    }

    [Fact]
    public void A_settings_file_that_cannot_be_read_while_auto_expand_is_on_says_that_the_hook_stays()
    {
        string shell = Shell();
        string applying = Method(shell, "    private void ApplySettings(bool firstLoad)");

        // One fixed sentence, a named constant beside the shell's other, and one fixed line of
        // the log: each written once. Nothing of the file is quoted in either.
        Place(shell, "private const string AutoExpandStaysOnNote = \"Snippet auto-expand stays on: its keyboard hook is still installed.\";");
        Place(shell, "\"Snippet auto-expand stays on: its keyboard hook is still installed.\"");
        Place(shell, "\"settings.json could not be loaded while snippet auto-expand is on: it stays on\"");
        Place(shell, "notes.Add(AutoExpandStaysOnNote);");

        // They stand inside the block for a file that could not be read, guarded by both: not
        // at the first load (auto-expand is off then), and only while the switch is on.
        string unreadable = WithoutComments(Block(applying, "        if (loaded.Error is not null)"));
        var guarded = Regex.Match(
            unreadable,
            @"if \(([^)]*)\)\s*\{\s*_log\.Error\(""settings\.json could not be loaded while snippet auto-expand is on: it stays on""\);\s*notes\.Add\(AutoExpandStaysOnNote\);\s*\}");
        Assert.True(guarded.Success, "The sentence and the line of the log are not in one if block of the block for an unreadable file.");
        Assert.Equal(
            ["!firstLoad", "_autoExpandOn"],
            guarded.Groups[1].Value.Split("&&").Select(part => part.Trim()).Order(StringComparer.Ordinal).ToList());

        // It follows the notes the decision brought directly: no note is added before it.
        Assert.Matches(@"var notes = new List<string>\(decision\.Notes\);\s*if \(loaded\.Error is not null\)", applying);
        Assert.Equal(applying.IndexOf("notes.Add", StringComparison.Ordinal), Place(applying, "notes.Add(AutoExpandStaysOnNote);"));
    }

    [Fact]
    public void What_the_decision_says_about_the_switch_reaches_the_owner_and_the_log()
    {
        string applying = Method(Shell(), "    private void ApplySettings(bool firstLoad)");

        // A value of the setting that cannot be used gives a note, and the note is shown with
        // the others and logged in fixed words. Without these lines the owner would be told
        // nothing, and every test would still pass.
        var block = WithoutComments(Block(applying, "        if (decision.AutoExpand is { } autoExpandOptions)"));
        Assert.Matches(
            @"if \(autoExpandOptions\.Notes\.Count > 0\)\s*\{\s*_log\.Error\(""The snippetAutoExpand setting in settings\.json could not be used""\);\s*notes\.AddRange\(autoExpandOptions\.Notes\);\s*\}",
            block);
    }

    [Fact]
    public void Nothing_between_the_switch_and_the_last_step_of_applying_the_settings_returns()
    {
        string applying = Method(Shell(), "    private void ApplySettings(bool firstLoad)");
        const string Opening = "        if (decision.AutoExpand is { } autoExpandOptions)";
        int blockEnd = Place(applying, Opening) + Block(applying, Opening).Length;
        int last = Place(applying, "_autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);");

        // A return there would leave a Reload without its last step: the keywords of the
        // library, and a hook that was to be put in again, would be left as they were. The
        // switch block stands directly behind the one for a file that could not be read, so
        // this is nearly the whole method: the hotkeys, the start-with-Windows entry, the file
        // index, the script, clipboard and sync settings, the library and the notification.
        // There is no return in any of it today.
        string between = WithoutComments(applying[blockEnd..last]);
        foreach (string inside in new[] { "_hotkeys.Register(hotkey)", "_files.Configure(fileOptions)", "_syncOptions = syncOptions;", "_library.Configure(_data);", "RefreshAppsInBackground();", "Notify(string.Join(\" \", notes));" })
        {
            Assert.True(between.Contains(inside, StringComparison.Ordinal), $"The stretch looked at is not the one between the switch and the last step: {inside} is not in it.");
        }

        Assert.False(Regex.IsMatch(between, @"\breturn\b"), "A return statement stands between the switch and the last step of applying the settings.");
    }

    [Fact]
    public void The_hook_follows_the_switch_as_the_last_step_of_applying_the_settings()
    {
        string applying = Method(Shell(), "    private void ApplySettings(bool firstLoad)");

        int kept = Place(applying, "_autoExpandOn = autoExpandOptions.Enabled;");
        int library = Place(applying, "_library.Configure(_data);");
        int apps = Place(applying, "RefreshAppsInBackground();");
        int notes = Place(applying, "Notify(string.Join(\" \", notes));");
        int configured = Place(applying, "_autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);");

        // After the switch was read, and after everything else: nothing there depends on it.
        Assert.True(kept < configured && library < configured && apps < configured && notes < configured);
        Assert.EndsWith("_autoExpand.Configure(_autoExpandOn, _library.Current.Snippets);", applying.TrimEnd());
    }

    [Fact]
    public void The_keywords_follow_the_library_whenever_it_has_been_loaded_again()
    {
        string shell = Shell();

        Place(shell, "changed: () => _app.Dispatcher.BeginInvoke(LibraryChanged));");
        Assert.Matches(
            @"if \(!_closing\)\s*\{\s*_autoExpand\.UseLibrary\(_library\.Current\.Snippets\);\s*\}",
            Method(shell, "    private void LibraryChanged()"));
    }

    [Fact]
    public void At_closing_the_keyboard_hook_goes_before_everything_else()
    {
        string closing = Method(Shell(), "    public void Dispose()");

        int flagged = Place(closing, "_closing = true;");
        int hook = Place(closing, "Release(\"snippet auto-expand\", _autoExpand);");
        int firstRelease = closing.IndexOf("Release(", StringComparison.Ordinal);
        int firstGuarded = closing.IndexOf("Guarded(", StringComparison.Ordinal);

        // The hook's thread hands its work to this thread, which goes on to the paste service,
        // the notifications and the log. So the expander is let go of first: its Dispose takes
        // the hook out and waits for the hook's thread, and returns only after that has been
        // tried. A removal can fail, and Dispose then returns after one that did, with the
        // hook counted as one that could not be removed; the listener was closed before the
        // removal was tried, so what that hook still reports is dropped.
        Assert.True(flagged < hook);
        Assert.Equal(hook, firstRelease);
        Assert.True(hook < firstGuarded);
        Assert.True(hook < Place(closing, "Release(\"script runner\", _scripts);"));
        Assert.True(hook < Place(closing, "Release(\"clipboard\", _windowsClipboard);"));
    }

    [Fact]
    public void The_app_has_one_writer_of_the_clipboard_and_auto_expand_adds_none()
    {
        // What auto-expand pastes goes through WindowsClipboard, which puts the four markers
        // on the clipboard before the text, so that no clipboard history keeps it. No file of
        // auto-expand opens the clipboard itself.
        var opens = SourceFiles()
            .Where(file => Regex.IsMatch(file.Code, @"extern .*\b(OpenClipboard|SetClipboardData|EmptyClipboard|GetClipboardData)\b"))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Wyvern.App/Interop/ClipboardReader.cs", "Wyvern.App/Interop/WindowsClipboard.cs"], opens);
        foreach (var file in SourceFiles().Where(file => file.Path.StartsWith("Wyvern.Core/AutoExpand/", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("IClipboard", file.Code);
        }
    }

    [Fact]
    public void What_auto_expand_pastes_is_kept_out_of_the_clipboard_history_in_two_ways()
    {
        // Wyvern's clipboard capture is not paused around a paste: it looks at every change of
        // the clipboard, a replacement's among them. Two things keep the snippet out of the
        // history, and either would do. (That the capture keeps neither kind is tested where
        // the capture is: ClipCaptureTests and CaptureRulesTests.)
        var files = SourceFiles();
        string writer = files.Single(file => file.Path == "Wyvern.App/Interop/WindowsClipboard.cs").Code;
        string reader = files.Single(file => file.Path == "Wyvern.App/Interop/ClipboardReader.cs").Code;
        string rules = files.Single(file => file.Path == "Wyvern.Core/ClipboardHistory/CaptureRules.cs").Code;

        // One: every text the app writes goes through Put, which puts the markers on the
        // clipboard in front of the content. The capture, the clipboard history of Windows and
        // other clipboard managers leave marked content alone.
        Assert.Matches(@"public void SetText\(string text\)\s*\{[^}]*Put\(\[\(UnicodeText, Encoding\.Unicode\.GetBytes\(text \+ '\\0'\)\)\]\);", writer);
        Place(writer, "uint[] markers = KeepOutOfHistoryIds();");
        Place(writer, "uint[] formats = [.. markers, .. content.Select(item => item.Format)];");

        // Two: the capture asks whose window put the content there, and what a window of
        // Wyvern's own process put there is Wyvern's own write, whatever else is on the
        // clipboard.
        Assert.Matches(@"return ownerProcess == \(uint\)Environment\.ProcessId\s*\? new ClipboardOwner\(null, IsWyvern: true\)", reader);
        Assert.Matches(@"if \(owner\.IsWyvern\)\s*\{\s*return CaptureVerdict\.OwnWrite;\s*\}\s*if \(keptOutOfMonitors", rules);
    }

    [Theory]
    [InlineData("lock (")]
    [InlineData("Monitor.")]
    [InlineData("EnterScope")]
    [InlineData(".Enter(")]
    [InlineData("SpinWait")]
    [InlineData("SpinUntil")]
    [InlineData("Semaphore")]
    [InlineData("ResetEvent")]
    [InlineData("Mutex")]
    [InlineData(".Wait(")]
    [InlineData("Join(")]
    [InlineData("Sleep")]
    [InlineData("Task")]
    [InlineData(".Result")]
    [InlineData("Dispatcher")]
    public void Nothing_of_auto_expand_in_the_core_waits_for_a_thread_or_holds_a_lock(string never)
    {
        // The hook's thread and the UI thread share five things: the keywords going in; a
        // keyword that was typed, coming out, as an action handed to post; the count of keys
        // seen; the mark that a failure inside the callback has been handed over to be logged;
        // and what a listener kept (the typed characters, the window, a dead key waiting),
        // which is handed to the UI thread once, after the hook has been removed and its
        // thread has ended. The first four are each one value that is written whole and read
        // whole, and the last is not read until the thread is over. Neither thread can ever
        // wait for the other.
        foreach (var file in SourceFiles().Where(file => file.Path.StartsWith("Wyvern.Core/AutoExpand/", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(never, file.Code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void What_crosses_between_the_two_threads_is_read_and_written_whole()
    {
        string expander = SourceFiles().Single(file => file.Path == "Wyvern.Core/AutoExpand/AutoExpander.cs").Code;

        // The keywords going in, and the switch with them: a closed listener holds none.
        Place(expander, "public void Use(KeywordSet keywords) => Volatile.Write(ref _keywords, keywords);");
        Place(expander, "public void Close() => Volatile.Write(ref _keywords, null);");
        Place(expander, "var keywords = Volatile.Read(ref _keywords);");

        // The count of keys coming out.
        Place(expander, "public long Keys => Volatile.Read(ref _keys);");
        Place(expander, "Volatile.Write(ref _keys, keys);");

        // A keyword that was typed, coming out: handed over, and nothing else is done with it
        // on the hook's thread.
        Place(expander, "owner._post(() => owner.Replace(typed));");

        // The typed characters are touched in the listener and nowhere else in the class.
        string listener = expander[Place(expander, "    private sealed class Listener(AutoExpander owner, KeywordSet keywords)")..Place(expander, "    private sealed class Typed(")];
        Assert.Equal(Regex.Matches(expander, @"\b_buffer\b").Count, Regex.Matches(listener, @"\b_buffer\b").Count);
    }

    [Fact]
    public void What_the_hook_calls_for_a_key_never_writes_the_log_or_posts_a_note_itself()
    {
        string expander = SourceFiles().Single(file => file.Path == "Wyvern.Core/AutoExpand/AutoExpander.cs").Code;

        // KeyDown is what the hook calls, on its own thread, with every key press in every
        // program waiting for it. The tests of the expander see this by what it does: they
        // would not see a Report in a branch they do not type through. Whatever else it does,
        // KeyDown may hand on (post, and TellOfKey, which posts) but it may not write the log
        // (Report) or post a note to the owner (Notify), nor reach for the two callbacks that
        // do. The whole of the hook thread's half is searched, not KeyDown's own lines, so
        // that a helper of KeyDown that does one of these is found as well: nothing in that
        // class has a reason to. It is found by its place in the file, not by its lines.
        var listenerStart = Regex.Match(expander, @"\n    private sealed class Listener\(");
        var typedStart = Regex.Match(expander, @"\n    private sealed class Typed\(");
        Assert.True(listenerStart.Success && typedStart.Success && listenerStart.Index < typedStart.Index, "The hook thread's half of the expander was not found.");
        string listener = expander[listenerStart.Index..typedStart.Index];

        // Not an empty stretch, and the right one: KeyDown is in it, and it hands the
        // replacement over.
        Assert.Matches(@"\n        public void KeyDown\([^)]*\)\n", listener);
        Assert.Contains("owner._post(", listener, StringComparison.Ordinal);
        Assert.False(
            Regex.IsMatch(listener, @"\b_?(report|notify)\(", RegexOptions.IgnoreCase),
            "The hook thread's half of the expander calls Report or Notify (or the callback behind one).");
    }
}
