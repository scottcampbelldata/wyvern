using Wyvern.Core.Library;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>
/// The quiet period never runs out by itself here: the service is given a timer that only a
/// test can fire. Four tests also rely on a real change notification, which arrives a moment
/// after the change; they wait for it, up to a generous limit, and nothing else in them
/// depends on when it arrives.
/// </summary>
public sealed class LibraryServiceTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;
    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _reports = [];
    private readonly List<string> _notes = [];
    private readonly ManualResetEventSlim _loadHasRead = new();
    private readonly ManualResetEventSlim _loadMayFinish = new(initialState: true);
    private readonly LibraryService _service;
    private int _loads;
    private volatile bool _loadFails;
    private volatile bool _fileHeld;

    public LibraryServiceTests()
    {
        _paths = new DataPaths(_dir.File("data"));
        _service = new LibraryService(
            (message, _) =>
            {
                lock (_reports)
                {
                    _reports.Add(message);
                }
            },
            note =>
            {
                lock (_notes)
                {
                    _notes.Add(note);
                }
            },
            _time,
            paths =>
            {
                if (_loadFails)
                {
                    throw new InvalidOperationException("private words from whatever failed");
                }

                // The real load, with a place for a test to hold it: after it has read the
                // folder, before what it read is put into use.
                var snapshot = LibraryLoader.Load(paths);
                Interlocked.Increment(ref _loads);
                _loadHasRead.Set();
                _loadMayFinish.Wait(Limit);
                return snapshot;
            });
    }

    public void Dispose()
    {
        _loadMayFinish.Set();
        _service.Dispose();
        _service.Loading.Wait(Limit);
        _dir.Dispose();
    }

    private int Loads => Volatile.Read(ref _loads);

    private ManualTimer Timer => _time.Timers[^1];

    private List<string> Reports()
    {
        lock (_reports)
        {
            return _reports.ToList();
        }
    }

    private List<string> Notes()
    {
        lock (_notes)
        {
            return _notes.ToList();
        }
    }

    private static string WriteSnippet(DataPaths paths, string name, string text)
    {
        Directory.CreateDirectory(paths.SnippetsDir);
        string file = Path.Combine(paths.SnippetsDir, name);
        File.WriteAllText(file, text);
        return file;
    }

    private string WriteSnippet(string name, string text) => WriteSnippet(_paths, name, text);

    private async Task ConfigureAndWait(DataPaths? paths = null)
    {
        _service.Configure(paths ?? _paths);
        await _service.Loading.WaitAsync(Limit);
    }

    /// <summary>Says the quiet period is over and waits for the load that follows.</summary>
    private async Task QuietPeriodEnds(LibraryService? service = null)
    {
        Timer.Fire();
        await (service ?? _service).Loading.WaitAsync(Limit);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    private string[] SnippetNames() => _service.Current.Snippets.Select(s => s.Name).ToArray();

    [Fact]
    public void Nothing_is_loaded_before_it_is_configured()
    {
        Assert.Same(LibrarySnapshot.Empty, _service.Current);
        Assert.True(_service.Loading.IsCompleted);
    }

    [Fact]
    public async Task Configure_creates_the_folders_and_loads_what_is_there()
    {
        Directory.CreateDirectory(_paths.LibraryDir);
        File.WriteAllText(_paths.QuicklinksFile, """[{ "name": "Wiki", "link": "https://wiki.example.org" }]""");

        await ConfigureAndWait();

        Assert.True(Directory.Exists(_paths.SnippetsDir));
        Assert.Equal(["Wiki"], _service.Current.Quicklinks.Select(q => q.Name));
        Assert.Empty(_service.Current.Snippets);
        Assert.Equal(1, Loads);
    }

    [Fact]
    public async Task The_log_gets_counts_and_never_a_name()
    {
        Directory.CreateDirectory(_paths.LibraryDir);
        File.WriteAllText(_paths.QuicklinksFile, """[{ "name": "Walrus bank", "link": "https://walrus.example.org" }]""");
        WriteSnippet("walrus-letter.md", "Dear walrus");
        WriteSnippet("walrus-broken.md", "---\n");
        Directory.CreateDirectory(_paths.ScriptsDir);
        File.WriteAllText(Path.Combine(_paths.ScriptsDir, "walrus-task.cmd"), "REM @wyvern.title Walrus task\r\n");
        File.WriteAllText(Path.Combine(_paths.ScriptsDir, "walrus-report.py"), "# @wyvern.mode loud\n");

        await ConfigureAndWait();

        Assert.Equal(["Library loaded: 1 quicklinks, 1 snippets, 1 scripts, 2 files skipped"], Reports());
    }

    [Fact]
    public async Task A_change_is_not_loaded_until_the_quiet_period_ends()
    {
        await ConfigureAndWait();

        WriteSnippet("late.md", "Late");
        await Eventually(() => Timer.Starts > 0, "the change to be noticed");

        Assert.Equal(LibraryService.QuietPeriod, Timer.Due);
        Assert.Empty(SnippetNames());
        Assert.Equal(1, Loads);

        await QuietPeriodEnds();

        Assert.Equal(["late"], SnippetNames());
        Assert.Equal(2, Loads);
    }

    [Fact]
    public async Task Changes_that_follow_each_other_are_loaded_together_in_one_go()
    {
        await ConfigureAndWait();

        WriteSnippet("one.md", "1");
        await Eventually(() => Timer.Starts > 0, "the first change to be noticed");
        int startsSoFar = Timer.Starts;
        WriteSnippet("two.md", "2");
        await Eventually(() => Timer.Starts > startsSoFar, "the wait to be started again");

        await QuietPeriodEnds();

        Assert.Equal(["one", "two"], SnippetNames());
        Assert.Equal(2, Loads);
    }

    [Fact]
    public async Task Asking_while_a_load_is_running_loads_once_more_when_it_finishes()
    {
        _loadMayFinish.Reset();
        _service.Configure(_paths);
        Assert.True(_loadHasRead.Wait(Limit), "Timed out waiting for the first load to read the folder");

        // Written after the first load has read the folder, so only a second load can find it.
        WriteSnippet("during.md", "During");
        Timer.Fire();
        Timer.Fire();
        Timer.Fire();
        _loadMayFinish.Set();
        await _service.Loading.WaitAsync(Limit);

        Assert.Equal(["during"], SnippetNames());
        Assert.Equal(2, Loads);
    }

    [Fact]
    public async Task Bad_files_are_named_when_the_set_of_them_changes_and_not_on_every_load()
    {
        string first = WriteSnippet("first-bad.md", "---\n");
        await ConfigureAndWait();
        Assert.Equal(["Some of the library could not be loaded. Check: first-bad.md."], Notes());

        // The same bad file again: nothing new to say.
        await QuietPeriodEnds();
        Assert.Single(Notes());

        // One more bad file: the set has changed.
        string second = WriteSnippet("second-bad.md", "---\n");
        await QuietPeriodEnds();
        Assert.Equal(2, Notes().Count);
        Assert.Equal("Some of the library could not be loaded. Check: first-bad.md, second-bad.md.", Notes()[1]);

        // One of them mended: the set has changed again, and one is still bad.
        File.WriteAllText(first, "Mended");
        await QuietPeriodEnds();
        Assert.Equal(3, Notes().Count);
        Assert.Equal("Some of the library could not be loaded. Check: second-bad.md.", Notes()[2]);

        // The other mended: nothing is bad, so there is nothing to say.
        File.WriteAllText(second, "Mended");
        await QuietPeriodEnds();
        Assert.Equal(3, Notes().Count);

        // Broken again: said again, because in between it was fine.
        File.WriteAllText(second, "---\n");
        await QuietPeriodEnds();
        Assert.Equal(4, Notes().Count);
    }

    [Fact]
    public async Task Configuring_again_loads_afresh_without_repeating_the_note()
    {
        WriteSnippet("bad.md", "---\n");
        await ConfigureAndWait();
        WriteSnippet("added.md", "Added");

        await ConfigureAndWait();

        Assert.Equal(["added"], SnippetNames());
        Assert.Equal(2, Loads);
        Assert.Single(Notes());
    }

    [Fact]
    public async Task Configuring_another_folder_switches_to_it()
    {
        var other = new DataPaths(_dir.File("other"));
        WriteSnippet(_paths, "here.md", "Here");
        WriteSnippet(other, "there.md", "There");
        await ConfigureAndWait();
        var firstTimer = Timer;

        await ConfigureAndWait(other);

        Assert.Equal(["there"], SnippetNames());
        Assert.True(firstTimer.Disposed);
        Assert.NotSame(firstTimer, Timer);

        // A timer from the earlier folder whose callback was already on its way does nothing.
        firstTimer.Fire();
        await _service.Loading.WaitAsync(Limit);
        Assert.Equal(2, Loads);
    }

    [Fact]
    public async Task A_load_that_was_running_when_the_folder_changed_is_thrown_away()
    {
        var other = new DataPaths(_dir.File("other"));
        WriteSnippet(_paths, "here.md", "Here");
        WriteSnippet(other, "there.md", "There");
        _loadMayFinish.Reset();
        _service.Configure(_paths);
        Assert.True(_loadHasRead.Wait(Limit), "Timed out waiting for the first load to read the folder");

        _service.Configure(other);
        _loadMayFinish.Set();
        await _service.Loading.WaitAsync(Limit);

        Assert.Equal(["there"], SnippetNames());
        Assert.Equal(["Library loaded: 0 quicklinks, 1 snippets, 0 scripts, 0 files skipped"], Reports());
    }

    [Fact]
    public async Task A_load_that_throws_is_reported_and_what_was_loaded_stays_in_use()
    {
        WriteSnippet("kept.md", "Kept");
        await ConfigureAndWait();

        _loadFails = true;
        await QuietPeriodEnds();

        Assert.Equal(["kept"], SnippetNames());
        Assert.Equal("The library could not be loaded", Reports()[^1]);

        _loadFails = false;
        WriteSnippet("new.md", "New");
        await QuietPeriodEnds();

        Assert.Equal(["kept", "new"], SnippetNames());
    }

    [Fact]
    public async Task Callbacks_that_throw_do_not_stop_the_service()
    {
        using var service = new LibraryService(
            (_, _) => throw new InvalidOperationException("the log is broken"),
            _ => throw new InvalidOperationException("the tray is broken"),
            _time);
        WriteSnippet("bad.md", "---\n");
        WriteSnippet("good.md", "Good");

        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);
        Timer.Fire();
        await service.Loading.WaitAsync(Limit);

        Assert.Equal(["good"], service.Current.Snippets.Select(s => s.Name));
    }

    [Fact]
    public async Task A_data_folder_that_cannot_be_created_is_reported_and_leaves_the_library_empty()
    {
        WriteSnippet("here.md", "Here");
        await ConfigureAndWait();
        Assert.Equal(["here"], SnippetNames());

        // A file stands where the data folder should be, so no folder can be made inside it.
        string blocker = _dir.File("blocker");
        File.WriteAllText(blocker, "");
        await ConfigureAndWait(new DataPaths(Path.Combine(blocker, "data")));

        Assert.Empty(SnippetNames());
        Assert.Contains("The library folder could not be created", Reports());
        Assert.Equal(["The library folder could not be created. Details are in the log file."], Notes());
    }

    [Fact]
    public async Task After_dispose_nothing_more_is_loaded()
    {
        WriteSnippet("here.md", "Here");
        await ConfigureAndWait();
        var timer = Timer;

        _service.Dispose();
        _service.Dispose();
        timer.Fire();
        await _service.Loading.WaitAsync(Limit);

        Assert.True(timer.Disposed);
        Assert.Equal(1, Loads);
        Assert.Throws<ObjectDisposedException>(() => _service.Configure(_paths));
    }

    [Fact]
    public async Task A_library_folder_that_is_deleted_and_made_again_is_watched_again()
    {
        WriteSnippet("old.md", "Old");
        await ConfigureAndWait();
        Assert.Equal(["old"], SnippetNames());

        // Deleting the folder that is being watched ends that watcher with an error. Nothing
        // else tells the service, so it must put a new watcher in its place by itself. The
        // load that follows the new watcher is the sign that this has been done.
        Directory.Delete(_paths.LibraryDir, recursive: true);
        await Eventually(() => Loads >= 2 && Directory.Exists(_paths.SnippetsDir), "the folder to be made again and watched again");
        await _service.Loading.WaitAsync(Limit);
        Assert.Contains("The library folder watcher reported an error", Reports());
        Assert.Empty(SnippetNames());

        // A change in the new folder is noticed by the new watcher, which restarts the wait.
        int startsSoFar = Timer.Starts;
        WriteSnippet("new.md", "New");
        await Eventually(() => Timer.Starts > startsSoFar, "the change in the new folder to be noticed");
        await QuietPeriodEnds();

        Assert.Equal(["new"], SnippetNames());
    }

    // Another program with the file open and no sharing, as an editor or a sync tool can have it.
    private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.None);

    private const string HeldFile = @"C:\library\snippets\held.md";

    // A service whose load is a fake that never touches the disk. A file written just before
    // the watcher starts can still be reported as changed a moment later, which would start the
    // wait without the service having done so and spoil what a test says about the wait.
    // The fake finds the file held, or not, as _fileHeld says.
    private LibraryService NewServiceWithAHeldFile()
    {
        return new LibraryService(
            time: _time,
            load: _ =>
            {
                Interlocked.Increment(ref _loads);
                return _fileHeld
                    ? new LibrarySnapshot([], [], [HeldFile]) { HasUnreadableFiles = true }
                    : new LibrarySnapshot([], [new Snippet("held.md", HeldFile, "held", null, "Held")], []);
            });
    }

    [Fact]
    public async Task A_file_that_could_not_be_opened_is_read_again_when_the_quiet_period_ends()
    {
        string file = WriteSnippet("held.md", "Held");
        using var held = Hold(file);

        await ConfigureAndWait();
        Assert.Empty(SnippetNames());

        // Letting go of the file changes nothing on disk, so no change will be reported. The
        // load that could not read it has started the wait itself, as if a change had arrived.
        // (A real change notification can also start it, so this is not proof by itself: the
        // tests below, which have no files, are.)
        Assert.Equal(LibraryService.QuietPeriod, Timer.Due);

        held.Dispose();
        await QuietPeriodEnds();

        Assert.Equal(["held"], SnippetNames());
    }

    [Fact]
    public async Task A_load_that_could_not_open_a_file_starts_the_quiet_period_as_if_a_change_had_arrived()
    {
        _fileHeld = true;
        using var service = NewServiceWithAHeldFile();

        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);

        Assert.Empty(service.Current.Snippets);
        Assert.Equal(1, Timer.Starts);
        Assert.Equal(LibraryService.QuietPeriod, Timer.Due);

        _fileHeld = false;
        await QuietPeriodEnds(service);

        Assert.Equal(["held"], service.Current.Snippets.Select(s => s.Name));
        Assert.Null(Timer.Due);
        Assert.Equal(1, Timer.Starts);
    }

    [Fact]
    public async Task A_file_that_stays_unreadable_is_tried_again_three_times_and_no_more()
    {
        _fileHeld = true;
        using var service = NewServiceWithAHeldFile();
        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);
        Assert.Equal(1, Loads);

        for (int tries = 1; tries <= 3; tries++)
        {
            Assert.Equal(LibraryService.QuietPeriod, Timer.Due);
            await QuietPeriodEnds(service);
            Assert.Equal(1 + tries, Loads);
        }

        // The load after the third try found it unreadable again, and did not ask for a fourth.
        Assert.Null(Timer.Due);
        Assert.Empty(service.Current.Snippets);
    }

    [Fact]
    public async Task The_count_of_tries_starts_again_after_a_load_without_the_problem_and_after_configure()
    {
        _fileHeld = true;
        using var service = NewServiceWithAHeldFile();
        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);
        await QuietPeriodEnds(service);
        Assert.Equal(LibraryService.QuietPeriod, Timer.Due);

        // A load that finds nothing wrong starts the count again, however many tries were used.
        _fileHeld = false;
        await QuietPeriodEnds(service);
        Assert.Equal(["held"], service.Current.Snippets.Select(s => s.Name));
        Assert.Null(Timer.Due);

        // So three tries are allowed again, and no more.
        _fileHeld = true;
        await QuietPeriodEnds(service);
        await QuietPeriodEnds(service);
        await QuietPeriodEnds(service);
        Assert.Equal(LibraryService.QuietPeriod, Timer.Due);
        await QuietPeriodEnds(service);
        Assert.Null(Timer.Due);

        // Configure, which is what Reload does, starts the count again too.
        service.Configure(_paths);
        await service.Loading.WaitAsync(Limit);
        Assert.Equal(LibraryService.QuietPeriod, Timer.Due);
    }

    [Fact]
    public async Task A_watcher_that_keeps_failing_is_replaced_three_times_and_then_given_up_on()
    {
        WriteSnippet("old.md", "Old");
        await ConfigureAndWait();

        // Each deletion ends the watcher in use, and each time a new one is put in its place.
        for (int replacement = 1; replacement <= 3; replacement++)
        {
            int loadsSoFar = Loads;
            Directory.Delete(_paths.LibraryDir, recursive: true);
            await Eventually(() => Loads > loadsSoFar && Directory.Exists(_paths.SnippetsDir), $"watcher number {replacement} to be put in place");
            await _service.Loading.WaitAsync(Limit);
        }

        Assert.DoesNotContain("The library is no longer watched for changes until Reload", Reports());

        // The fourth failure is the last: it is reported in fixed words, and nothing is started.
        Directory.Delete(_paths.LibraryDir, recursive: true);
        await Eventually(() => Reports().Contains("The library is no longer watched for changes until Reload"), "the fourth failure to be given up on");
        await _service.Loading.WaitAsync(Limit);
        Assert.False(Directory.Exists(_paths.LibraryDir));
        Assert.Empty(SnippetNames());

        // Reload makes the folder again and watches it again. (That it also starts the count
        // of replacements again is not shown here: Configure starts a watcher whatever the count.)
        int loadsBeforeReload = Loads;
        await ConfigureAndWait();
        Assert.True(Directory.Exists(_paths.SnippetsDir));
        Assert.Equal(loadsBeforeReload + 1, Loads);
        int startsSoFar = Timer.Starts;
        WriteSnippet("again.md", "Again");
        await Eventually(() => Timer.Starts > startsSoFar, "the change in the folder to be noticed");
        await QuietPeriodEnds();
        Assert.Equal(["again"], SnippetNames());
    }
}

public class LibraryNotesTests
{
    [Fact]
    public void One_file_is_named_by_its_file_name_alone()
    {
        Assert.Equal(
            "Some of the library could not be loaded. Check: sig.md.",
            LibraryNotes.BadFiles([@"C:\Users\Sam\AppData\Local\Wyvern\data\library\snippets\private\sig.md"]));
    }

    [Fact]
    public void Up_to_three_files_are_named()
    {
        Assert.Equal(
            "Some of the library could not be loaded. Check: quicklinks.json, a.md, b.txt.",
            LibraryNotes.BadFiles([@"C:\lib\quicklinks.json", @"C:\lib\snippets\a.md", @"C:\lib\snippets\b.txt"]));
    }

    [Fact]
    public void Beyond_three_the_rest_are_counted()
    {
        Assert.Equal(
            "Some of the library could not be loaded. Check: a.md, b.md, c.md and 2 more.",
            LibraryNotes.BadFiles([@"C:\s\a.md", @"C:\s\b.md", @"C:\s\c.md", @"C:\s\d.md", @"C:\s\e.md"]));
    }

    [Fact]
    public void A_very_long_file_name_is_cut_short()
    {
        string note = LibraryNotes.BadFiles([@"C:\s\" + new string('x', 200) + ".md"]);

        Assert.Equal("Some of the library could not be loaded. Check: " + new string('x', 39) + "….", note);
    }

    [Fact]
    public void A_name_is_not_cut_through_the_middle_of_a_character()
    {
        // The emoji is two UTF-16 characters, and the cut would fall between them.
        string note = LibraryNotes.BadFiles([@"C:\s\" + new string('x', 38) + "\U0001F600" + new string('y', 20) + ".md"]);

        Assert.Equal("Some of the library could not be loaded. Check: " + new string('x', 38) + "….", note);
    }

    [Fact]
    public void Control_and_formatting_characters_are_left_out_of_a_name()
    {
        // U+202E turns the text after it around, and U+0085 is a control character.
        string note = LibraryNotes.BadFiles(["C:\\s\\a\u202Eb\u0085c.md"]);

        Assert.Equal("Some of the library could not be loaded. Check: abc.md.", note);
    }

    [Fact]
    public void A_formatting_character_outside_the_basic_plane_is_left_out_of_a_name_too()
    {
        // U+E0001 is a language tag. It shows nothing, and it is two UTF-16 characters, so a test
        // of one character at a time sees two halves of a pair and keeps both.
        string note = LibraryNotes.BadFiles(["C:\\s\\a\U000E0001b.md"]);

        Assert.Equal("Some of the library could not be loaded. Check: ab.md.", note);
    }

    [Fact]
    public void A_cut_never_leaves_half_of_a_pair_even_beside_another_unpaired_half()
    {
        // Two high surrogates in a row: the first has no partner, and the cut falls just after it.
        string note = LibraryNotes.BadFiles([@"C:\s\" + new string('x', 37) + "\ud83d\ud83d" + new string('y', 20) + ".md"]);

        Assert.DoesNotContain(note, char.IsSurrogate);
    }
}
