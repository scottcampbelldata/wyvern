using System.Text.RegularExpressions;

namespace Beacon.Tests;

/// <summary>
/// The shell is the app, and no test can run it. A few things about how it joins sync to the
/// rest are matters of safety all the same, and a later change could undo any of them without
/// another test noticing: what may connect, what is stopped first when Beacon closes, which
/// clipboard writer a key goes through. So the shell's source is read as text here, the way
/// <see cref="PrivacyRuleTests"/> reads it.
/// </summary>
public class SyncWiringTests
{
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

    private static string AppFile(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Beacon.App", name)).ReplaceLineEndings("\n");

    private static string Shell() => AppFile("BeaconShell.cs");

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

    // What the first Task.Run of a method hands to the pool thread: its lambda, between the
    // brace that opens it and the line that closes it (a brace eight spaces in).
    private static string BackgroundWork(string method)
    {
        const string Open = "Task.Run(() =>\n        {\n";
        int start = Place(method, Open) + Open.Length;
        int end = method.IndexOf("\n        });", start, StringComparison.Ordinal);
        Assert.True(end > start, "The end of the background work was not found");
        return method[start..end];
    }

    [Fact]
    public void The_real_connector_is_handed_to_sync_and_to_Sync_Setup_and_to_nothing_else()
    {
        string shell = Shell();

        Assert.Equal(2, Regex.Matches(shell, @"SftpRemoteStore\.Connector").Count);
        Assert.Contains("SftpRemoteStore.Connector", Method(shell, "    private void OpenSyncSetup()"));

        // Nothing else in the app names the store, so nothing else in the app can connect.
        Assert.Equal(2, Regex.Matches(shell, @"SftpRemoteStore\b").Count);
    }

    [Fact]
    public void No_other_file_under_src_names_the_real_connector()
    {
        var files = SourceFiles();

        // Proves the files are really being read, so the checks below cannot pass on an empty list.
        Assert.Contains(files, file => file.Path == "Beacon.App/BeaconShell.cs");
        Assert.Contains(files, file => file.Path == "Beacon.Core/Sync/SftpRemoteStore.cs");
        Assert.Contains(files, file => file.Path == "Beacon.App/SyncSetupWindow.xaml.cs");

        // The connector itself is the shell's to hand out. Not a file of the app, not a file of
        // Beacon.Core, and not through a "using static" of the store, which would let a line
        // say Connector without naming the store.
        var connector = new Regex(@"SftpRemoteStore\s*\.\s*Connector\b|using\s+static\s+[\w.]*SftpRemoteStore\b");
        var namesIt = files
            .Where(file => file.Path is not "Beacon.App/BeaconShell.cs" and not "Beacon.Core/Sync/SftpRemoteStore.cs" && connector.IsMatch(file.Code))
            .Select(file => file.Path)
            .ToList();
        Assert.True(namesIt.Count == 0, $"These files name the real connector: {string.Join(", ", namesIt)}");

        // Outside sync's own folder, no file but the shell names the store at all.
        var namesStore = files
            .Where(file => file.Path is not "Beacon.App/BeaconShell.cs" && !file.Path.StartsWith("Beacon.Core/Sync/", StringComparison.Ordinal) && file.Code.Contains("SftpRemoteStore", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();
        Assert.True(namesStore.Count == 0, $"These files name the SFTP store: {string.Join(", ", namesStore)}");
    }

    [Fact]
    public void Sync_is_made_before_capture_is_started_and_before_the_settings_are_applied()
    {
        string shell = Shell();

        int sync = Place(shell, "_sync = new SyncService(");
        int history = Place(shell, "_history = new ClipHistoryService(");
        int capture = Place(shell, "if (_capture.Start() is { } captureNote)");
        int settings = Place(shell, "ApplySettings(firstLoad: true);");

        // Sync asks the history to read its files again, so the history is there first. Capture
        // and the settings both say how things stand through the tray text, which reads sync.
        Assert.True(history < sync, "the history must be made before sync");
        Assert.True(sync < capture, "sync must be made before capture is started");
        Assert.True(capture < settings, "the settings are applied last");
    }

    [Fact]
    public void What_sync_tells_is_posted_to_the_ui_thread_and_a_run_that_brought_history_in_has_it_read_again()
    {
        string shell = Shell();
        int start = Place(shell, "_sync = new SyncService(");
        string made = shell[start..shell.IndexOf(");", start, StringComparison.Ordinal)];

        // Each of the three callbacks is given by the name of its parameter. Two of them are an
        // Action, so a swap would still compile, and would have the tray text rebuilt when a
        // run brought history in and the history read again when the status changed.
        Assert.Contains("notify: note => _app.Dispatcher.BeginInvoke(() => Notify(note))", made);
        Assert.Contains("statusChanged: () => _app.Dispatcher.BeginInvoke(ShowCaptureState)", made);
        Assert.Contains("clipboardChanged: _history.Reload", made);
        Assert.Contains("_protector", made);
    }

    [Fact]
    public void At_closing_the_sync_windows_and_sync_are_let_go_of_before_the_clipboard_history()
    {
        string closing = Method(Shell(), "    public void Dispose()");

        int windows = Place(closing, "Guarded(\"close the sync windows\", CloseSyncWindows);");
        int sync = Place(closing, "Release(\"sync\", _sync);");
        int listener = Place(closing, "Release(\"clipboard listener\", _clipboardListener);");
        int history = Place(closing, "Release(\"clipboard history\", _history);");
        int scripts = Place(closing, "Release(\"script runner\", _scripts);");

        Assert.True(scripts < windows, "scripts are stopped first, as before");
        Assert.True(windows < sync, "the windows that show a key are closed before sync is stopped");
        Assert.True(sync < listener && sync < history, "sync is stopped before anything of the clipboard history");
    }

    [Fact]
    public void Each_sync_window_is_closed_under_a_guard_of_its_own()
    {
        string close = Method(Shell(), "    private void CloseSyncWindows()");

        // One that throws as it closes must not leave the other open: the window that shows the
        // key is the one that matters.
        Place(close, "Guarded(\"close the Sync Setup window\", () => _setupWindow?.Close());");
        Place(close, "Guarded(\"close the sync key window\", () => _keyWindow?.Close());");
        Assert.Equal(2, Regex.Matches(close, @"\.Close\(\)").Count);
    }

    [Fact]
    public void Sync_is_given_the_data_folder_only_where_the_settings_are_applied_and_after_the_history()
    {
        string shell = Shell();

        int history = Place(shell, "_history.Configure(_data.ClipboardDir, _clipboardOptions);");
        int sync = Place(shell, "_sync.Configure(_data, _clipboardOptions, _syncOptions);");

        Assert.True(history < sync);
        Assert.Contains("_sync.Configure(", Method(shell, "    private void ApplySettings(bool firstLoad)"));
    }

    [Fact]
    public void The_sync_setting_that_was_read_is_kept_before_sync_is_given_it()
    {
        string applying = Method(Shell(), "    private void ApplySettings(bool firstLoad)");

        // Without the assignment, Reload would go on giving sync the interval it started with.
        var kept = Regex.Match(applying, @"if \(decision\.Sync is \{ \} syncOptions\)\s*\{\s*_syncOptions = syncOptions;");
        Assert.True(kept.Success, "settings.json's sync setting is not kept in _syncOptions");
        Assert.True(kept.Index < Place(applying, "_sync.Configure(_data, _clipboardOptions, _syncOptions);"));
    }

    [Theory]
    [InlineData("    private void OpenSyncSetup()", "_setupWindow = new SyncSetupWindow(viewModel, Report);")]
    [InlineData("    private void ShowSyncKey()", "_keyWindow = new SyncKeyWindow(viewModel, Report);")]
    public void A_sync_window_has_its_files_read_away_from_the_ui_thread_before_it_is_shown(string method, string window)
    {
        string body = Method(Shell(), method);

        int background = Place(body, "Task.Run(() =>");
        int load = Place(body, "viewModel.Load();");
        int posted = Place(body, "_app.Dispatcher.BeginInvoke(() =>");
        int shown = Place(body, window);

        Assert.True(background < load && load < posted && posted < shown);

        // The order of the lines is not enough: the read has to be inside what the pool thread
        // is given, and not before it or after it on this thread.
        Assert.Contains("viewModel.Load();", BackgroundWork(body));

        // A window that Beacon's closing overtook is never shown.
        Assert.Contains("if (_closing)", body[posted..shown]);
    }

    [Theory]
    [InlineData("    private void OpenSyncSetup()", "_setupWindow", "_setupOpening")]
    [InlineData("    private void ShowSyncKey()", "_keyWindow", "_keyOpening")]
    public void Only_one_of_each_sync_window_is_open_or_on_its_way(string method, string window, string opening)
    {
        string body = Method(Shell(), method);

        // A second command while the window is open brings it forward and opens nothing.
        var open = Regex.Match(body, @"if \(" + window + @" is not null\)\s*\{\s*" + window + @"\.Activate\(\);\s*return;\s*\}");
        // A second command while the first is still reading does nothing, and the first one
        // says it is on its way before it starts.
        var onItsWay = Regex.Match(body, @"if \(" + opening + @"\)\s*\{\s*return;\s*\}\s*" + opening + " = true;");
        Assert.True(open.Success, $"{method.Trim()} does not bring an open window forward");
        Assert.True(onItsWay.Success, $"{method.Trim()} does not refuse a second command while the first is on its way");
        Assert.True(open.Index < onItsWay.Index && onItsWay.Index < Place(body, "Task.Run(() =>"));

        // And it stops saying so when the window is there, or the command would never work again.
        Assert.Matches(@"BeginInvoke\(\(\) =>\s*\{\s*" + opening + " = false;", body);
    }

    [Theory]
    [InlineData("    private void OpenSyncSetup()", "_setupWindow", "The Sync Setup window could not be shown")]
    [InlineData("    private void ShowSyncKey()", "_keyWindow", "The sync key window could not be shown")]
    public void A_sync_window_that_cannot_be_shown_is_forgotten_so_that_the_command_works_again(string method, string window, string failure)
    {
        string body = Method(Shell(), method);

        var shown = Regex.Match(body, @"try\s*\{\s*" + window + @"\.Show\(\);\s*\}\s*catch \(Exception ex\)\s*\{(?<handler>[^}]*)\}");
        Assert.True(shown.Success, $"{method.Trim()} does not catch a Show that throws");
        string handler = shown.Groups["handler"].Value;

        // The field is what makes the command do nothing, so it is let go of, and what the view
        // model holds with it. The failure is said in fixed words with the exception, to the log
        // and to the owner, and nothing goes on to use a window that is not there.
        Assert.Contains(window + " = null;", handler);
        Assert.Contains("viewModel.Clear();", handler);
        Assert.Contains("_log.Error(\"" + failure + "\", ex);", handler);
        Assert.Contains("Notify(ActionFailedNote);", handler);
        Assert.Contains("return;", handler);

        // The window is brought forward after Show, and only when Show did not throw.
        Assert.Matches(@"catch \(Exception ex\)\s*\{[^}]*\}\s*" + window + @"\.Activate\(\);", body);
    }

    [Fact]
    public void A_key_window_that_Beacons_closing_overtook_lets_go_of_the_key()
    {
        string body = Method(Shell(), "    private void ShowSyncKey()");

        Assert.Matches(@"if \(_closing\)\s*\{\s*viewModel\.Clear\(\);\s*return;\s*\}", body);
    }

    [Fact]
    public void What_is_posted_to_the_ui_thread_does_nothing_once_Beacon_is_closing()
    {
        string shell = Shell();

        // The tray text, when sync or capture says how things stand.
        Assert.Matches(@"if \(_closing\b[^)]*\)\s*\{\s*return;\s*\}", Method(shell, "    private void ShowCaptureState()"));

        // The results, when the library has been loaded again.
        Assert.Matches(@"if \(!_closing\)\s*\{\s*_viewModel\.Refresh\(\);\s*\}", Method(shell, "    private void LibraryChanged()"));
    }

    [Fact]
    public void A_key_is_copied_through_the_clipboard_writer_that_marks_what_it_writes()
    {
        string shell = Shell();

        // The one writer of the shell: the Windows clipboard, which marks everything Beacon
        // puts there so that no clipboard history records it, tried a few times.
        Place(shell, "_clipboard = new RetryingClipboard(_windowsClipboard, Thread.Sleep);");
        Assert.Matches(@"new SyncSetupViewModel\(\s*_paths,\s*SftpRemoteStore\.Connector,\s*_protector,\s*_clipboard,", shell);
        Assert.Matches(@"new SyncKeyViewModel\(new KeyStore\(_paths\.KeyFile, _paths\.PassphraseFile, _protector\), _clipboard, Report\)", shell);
        Place(shell, "private readonly DpapiKeyProtector _protector = new();");
    }

    [Fact]
    public void The_commands_the_tray_and_the_library_are_joined_to_sync()
    {
        string shell = Shell();

        Place(shell, ".. SyncCommands.Create(SyncNow, OpenSyncSetup, ShowSyncKey, PausedSync, ConfirmSyncDeletions, RestoreMissingFiles, Notify),");
        Place(shell, "syncNow: Safely(\"Sync Now\", SyncNow),");
        Place(shell, "private void SyncNow() => _sync.SyncNow();");
        Place(shell, "_sync.Status.WithCapture(_capture.Tooltip, _clock.UtcNow, LocalZone())");
        Place(shell, "foldersFile: _paths.ClipboardFoldersFile);");
        Place(shell, "changed: () => _app.Dispatcher.BeginInvoke(LibraryChanged));");
        Assert.Contains("_viewModel.Refresh();", Method(shell, "    private void LibraryChanged()"));
        Assert.Contains("new TrayMenuItem(\"\\uE895\", \"Sync Now\", syncNow),", AppFile("TrayIcon.cs"));
    }

    [Fact]
    public void The_shell_lists_Confirm_Sync_Deletions_and_Restore_Missing_Files_and_each_starts_its_own_kind_of_run()
    {
        string shell = Shell();

        // The two commands are listed with the three that were there, in the order in which
        // SyncCommands.Create takes them: after the three, how the pause stands, then what
        // Confirm Sync Deletions starts, then what Restore Missing Files starts, then where a
        // sentence for the owner goes (SyncCommands' own test pins which title each gets).
        Place(shell, ".. SyncCommands.Create(SyncNow, OpenSyncSetup, ShowSyncKey, PausedSync, ConfirmSyncDeletions, RestoreMissingFiles, Notify),");

        // Each calls its own method of sync, and nothing else: no window. A swap would
        // compile, and would send the deletions the owner asked to have undone. The typed word
        // that Confirm Sync Deletions takes first is asked for by the command itself
        // (SyncCommands), with the count that sync gives at that moment.
        Place(shell, "    private void ConfirmSyncDeletions() => _sync.ConfirmDeletions();\n");
        Place(shell, "    private void RestoreMissingFiles() => _sync.RestoreMissing();\n");
        Place(shell, "    private SyncPause PausedSync() => _sync.Pause;\n");
        Assert.Equal(2, Regex.Matches(shell, @"\bPausedSync\b").Count);

        // Nothing else in the shell can give either word: not the tray, and not a second command.
        Assert.Single(Regex.Matches(shell, @"\.ConfirmDeletions\("));
        Assert.Single(Regex.Matches(shell, @"\.RestoreMissing\("));
        Assert.Equal(2, Regex.Matches(shell, @"\bConfirmSyncDeletions\b").Count);
        Assert.Equal(2, Regex.Matches(shell, @"\bRestoreMissingFiles\b").Count);
        Assert.DoesNotContain("ConfirmDeletions", AppFile("TrayIcon.cs"));
        Assert.DoesNotContain("RestoreMissing", AppFile("TrayIcon.cs"));
    }

    [Fact]
    public void Sync_makes_the_key_files_path_full_in_one_place_and_only_when_its_text_holds_no_tilde()
    {
        string service = SourceFiles().Single(file => file.Path == "Beacon.Core/Sync/SyncService.cs").Code;

        // Making a path full asks the disk when its text holds a "~", and along a path that
        // passes through a link to a share that contacts the share. The service gives the key
        // file's path to one check only, whether it lies in the data folder, and there the
        // path stands behind two questions that go by its text alone: no tilde, and a drive of
        // this machine.
        Assert.Matches(
            @"if \(!config\.PrivateKeyPath\.Contains\('~'\)\s*&& SftpRemoteStore\.IsOnDriveOfThisMachine\(config\.PrivateKeyPath\)\s*&& Holds\(data\.Root, config\.PrivateKeyPath\)\)",
            service);
        Assert.Equal(3, Regex.Matches(service, @"config\.PrivateKeyPath").Count);
        Assert.Single(Regex.Matches(service, @"Path\.GetFullPath\("));
    }

    [Fact]
    public void The_trays_text_is_made_again_when_its_menu_opens_and_not_by_a_timer()
    {
        string tray = AppFile("TrayIcon.cs");

        // The time in "Last synced" has no date until the day has changed, and nothing says so
        // until something rebuilds the text. It is rebuilt when the owner opens the menu, on
        // the UI thread, from the status sync has at that moment.
        Assert.Matches(@"Action menuOpening\b", tray);
        Assert.Matches(@"menuOpening\(\);\s+_menu\.ShowAt\(", tray);
        Assert.DoesNotContain("Timer", tray);

        string shell = Shell();
        Place(shell, "menuOpening: () => Guarded(\"refresh the tray text\", ShowCaptureState)");
        Assert.Contains("_sync.Status.WithCapture(", Method(shell, "    private void ShowCaptureState()"));
    }

    [Fact]
    public void The_sentence_for_a_data_folder_that_cannot_be_used_says_that_the_root_of_a_drive_is_one()
    {
        // DataDirSetting refuses the root of a drive or of a share as well as a path that is
        // not complete (DataPathsTests). The owner who wrote D:\ must not be told only that the
        // setting "must be a full path", which it is.
        string applying = Method(Shell(), "    private void ApplySettings(bool firstLoad)");

        Assert.Contains("must be a full path to a folder, not the root of a drive or of a share.", applying);
        Assert.Contains("The dataDir setting is not a full path to a folder, so the default data folder is used", applying);
    }
}
