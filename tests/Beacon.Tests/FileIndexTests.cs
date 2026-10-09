using Beacon.Core.Files;
using Beacon.Core.Search;

namespace Beacon.Tests;

public class FileIndexTests
{
    // The index is pure string handling, so these paths do not need to exist.
    private const string Docs = @"C:\Users\Sam\Documents";

    private static FileEntry File(string directory, string name) => new(directory, name, IsDirectory: false);

    private static FileEntry Folder(string directory, string name) => new(directory, name, IsDirectory: true);

    private static string[] Paths(FileIndex index, string query) =>
        index.Search(query, 50).Select(match => match.Entry.FullPath).ToArray();

    [Fact]
    public void Added_entries_are_found_by_name()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "Budget 2026.xlsx"));
        index.Add(File(Docs, "Holiday photos.zip"));

        var match = Assert.Single(index.Search("budget", 50));

        Assert.Equal(File(Docs, "Budget 2026.xlsx"), match.Entry);
        Assert.Equal(Docs + @"\Budget 2026.xlsx", match.Entry.FullPath);
        Assert.True(match.Score > 0);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void The_same_path_is_stored_once_whatever_its_case()
    {
        var index = new FileIndex();

        Assert.True(index.Add(File(Docs, "Notes.txt")));
        Assert.False(index.Add(File(Docs.ToUpperInvariant(), "NOTES.TXT")));

        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Search_returns_the_best_matches_first_and_no_more_than_the_limit()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "annual report draft.docx"));
        index.Add(File(Docs, "report.docx"));
        index.Add(File(Docs, "expense-report.xlsx"));
        index.Add(File(Docs, "unrelated.txt"));

        var best = index.Search("report", 2);

        Assert.Equal(["report.docx", "expense-report.xlsx"], best.Select(match => match.Entry.Name));
        Assert.True(best[0].Score > best[1].Score);
    }

    [Fact]
    public void Equal_scores_are_ordered_by_name_then_folder()
    {
        var index = new FileIndex();
        index.Add(File(Docs + @"\b", "todo.txt"));
        index.Add(File(Docs + @"\a", "todo.txt"));
        index.Add(File(Docs, "tide.txt"));

        Assert.Equal(
            [Docs + @"\tide.txt", Docs + @"\a\todo.txt", Docs + @"\b\todo.txt"],
            Paths(index, "t"));
    }

    [Fact]
    public void An_empty_query_or_a_zero_limit_finds_nothing()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "Notes.txt"));

        Assert.Empty(index.Search("", 50));
        Assert.Empty(index.Search("notes", 0));
    }

    [Fact]
    public void Matching_ignores_case_and_handles_digits_and_symbols()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "Q3_Results-2026 (final).PDF"));

        Assert.Single(index.Search("q3_2026", 50));
        Assert.Single(index.Search("RESULTS.pdf", 50));
        Assert.Single(index.Search("(final)", 50));
        Assert.Empty(index.Search("q4", 50));
    }

    [Fact]
    public void Removing_a_file_leaves_the_rest()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "one.txt"));
        index.Add(File(Docs, "two.txt"));
        index.Add(File(Docs, "three.txt"));

        Assert.True(index.Remove(Docs + @"\ONE.txt"));
        Assert.False(index.Remove(Docs + @"\one.txt"));

        Assert.Equal(2, index.Count);
        Assert.Empty(index.Search("one", 50));
        Assert.Single(index.Search("two", 50));
        Assert.Single(index.Search("three", 50));
    }

    [Fact]
    public void Removing_a_folder_removes_everything_under_it_but_not_its_neighbours()
    {
        var index = new FileIndex();
        index.Add(Folder(Docs, "data"));
        index.Add(File(Docs + @"\data", "inside.txt"));
        index.Add(Folder(Docs + @"\data", "deep"));
        index.Add(File(Docs + @"\data\deep", "deeper.txt"));
        index.Add(Folder(Docs, "data2"));
        index.Add(File(Docs + @"\data2", "neighbour.txt"));

        Assert.True(index.Remove(Docs + @"\data"));

        Assert.Equal([Docs + @"\data2", Docs + @"\data2\neighbour.txt"], Paths(index, "d").Concat(Paths(index, "neighbour")).Distinct().Order());
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Renaming_a_file_changes_its_name_and_keeps_one_entry()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "draft.txt"));

        Assert.True(index.Rename(Docs + @"\draft.txt", Docs + @"\final.txt"));

        Assert.Empty(index.Search("draft", 50));
        Assert.Equal([Docs + @"\final.txt"], Paths(index, "final"));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Renaming_only_the_case_of_a_name_updates_it()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "readme.md"));

        Assert.True(index.Rename(Docs + @"\readme.md", Docs + @"\README.md"));

        Assert.Equal("README.md", Assert.Single(index.Search("readme", 50)).Entry.Name);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Renaming_onto_an_existing_path_replaces_that_entry()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "report.docx"));
        index.Add(File(Docs, "report.tmp"));

        // What an editor does on save: write a temporary file, then rename it over the original.
        Assert.True(index.Rename(Docs + @"\report.tmp", Docs + @"\report.docx"));

        Assert.Equal([Docs + @"\report.docx"], Paths(index, "report"));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Renaming_a_folder_moves_everything_under_it()
    {
        var index = new FileIndex();
        index.Add(Folder(Docs, "old"));
        index.Add(File(Docs + @"\old", "a.txt"));
        index.Add(Folder(Docs + @"\old", "sub"));
        index.Add(File(Docs + @"\old\sub", "b.txt"));
        index.Add(File(Docs + @"\older", "untouched.txt"));

        Assert.True(index.Rename(Docs + @"\old", Docs + @"\new"));

        Assert.True(index.TryGet(Docs + @"\new", out var folder));
        Assert.True(folder.IsDirectory);
        Assert.True(index.TryGet(Docs + @"\new\a.txt", out _));
        Assert.True(index.TryGet(Docs + @"\new\sub", out _));
        Assert.True(index.TryGet(Docs + @"\new\sub\b.txt", out _));
        Assert.True(index.TryGet(Docs + @"\older\untouched.txt", out _));
        Assert.False(index.TryGet(Docs + @"\old\a.txt", out _));
        Assert.Equal(5, index.Count);
    }

    [Fact]
    public void Renaming_a_path_that_is_not_indexed_does_nothing()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "kept.txt"));

        Assert.False(index.Rename(Docs + @"\missing.txt", Docs + @"\other.txt"));

        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void ReplaceUnder_swaps_the_contents_of_one_folder_only()
    {
        var index = new FileIndex();
        index.Add(File(Docs + @"\work", "stale.txt"));
        index.Add(File(Docs + @"\home", "kept.txt"));

        index.ReplaceUnder(Docs + @"\work\", [File(Docs + @"\work", "fresh.txt")]);

        Assert.False(index.TryGet(Docs + @"\work\stale.txt", out _));
        Assert.True(index.TryGet(Docs + @"\work\fresh.txt", out _));
        Assert.True(index.TryGet(Docs + @"\home\kept.txt", out _));
    }

    [Fact]
    public void A_drive_root_counts_as_the_parent_of_everything_on_the_drive()
    {
        var index = new FileIndex();
        index.Add(File(@"D:\", "top.txt"));
        index.Add(File(@"D:\folder", "nested.txt"));
        index.Add(File(@"E:\folder", "elsewhere.txt"));

        index.ReplaceUnder(@"D:\", []);

        Assert.Equal(1, index.Count);
        Assert.True(index.TryGet(@"E:\folder\elsewhere.txt", out _));
    }

    [Fact]
    public void Clear_empties_the_index()
    {
        var index = new FileIndex();
        index.Add(File(Docs, "one.txt"));

        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.Empty(index.Search("one", 50));
        Assert.True(index.Add(File(Docs, "one.txt")));
    }

    [Fact]
    public void Removal_keeps_lookups_right_for_the_entry_that_fills_the_gap()
    {
        var index = new FileIndex();
        for (int i = 0; i < 100; i++)
        {
            index.Add(File(Docs, $"file{i:000}.txt"));
        }

        for (int i = 0; i < 100; i += 2)
        {
            Assert.True(index.Remove(Docs + $@"\file{i:000}.txt"));
        }

        Assert.Equal(50, index.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i % 2 == 1, index.TryGet(Docs + $@"\file{i:000}.txt", out _));
        }

        Assert.Equal(50, index.Search("file", 100).Count);
    }

    [Fact]
    public void A_large_index_is_searched_quickly()
    {
        // A guard against an accidental slow path, not a benchmark: the limit is far above the
        // few tens of milliseconds a search takes, so a busy machine does not fail the test.
        var index = new FileIndex();
        string[] words = ["report", "invoice", "photo", "notes", "budget", "draft", "final", "backup", "letter", "plan"];
        string[] extensions = [".docx", ".xlsx", ".pdf", ".txt", ".jpg", ".png"];
        var entries = new List<FileEntry>(300_000);
        for (int i = 0; i < 300_000; i++)
        {
            string directory = $@"{Docs}\folder{i % 3000:0000}";
            entries.Add(File(directory, $"{words[i % 10]}-{words[i / 10 % 10]}-{i:000000}{extensions[i % 6]}"));
        }

        index.AddRange(entries);
        index.Search("report", 50); // a first search that really scores names, so the timed ones are not the first

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var shortQuery = index.Search("re", 50);
        var longQuery = index.Search("budget-plan-123", 50);
        watch.Stop();

        Assert.Equal(50, shortQuery.Count);
        Assert.NotEmpty(longQuery);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"Two searches took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Renaming_a_folder_onto_one_that_is_already_indexed_replaces_its_contents()
    {
        var index = new FileIndex();
        index.Add(Folder(Docs, "a"));
        index.Add(File(Docs + @"\a", "x.txt"));
        index.Add(Folder(Docs, "b"));
        index.Add(File(Docs + @"\b", "x.txt"));
        index.Add(File(Docs + @"\b", "stale.txt"));

        Assert.True(index.Rename(Docs + @"\a", Docs + @"\b"));

        Assert.Equal([Docs + @"\b\x.txt"], Paths(index, "x.txt"));
        Assert.False(index.TryGet(Docs + @"\b\stale.txt", out _));
        Assert.False(index.TryGet(Docs + @"\a", out _));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void A_large_index_returns_exactly_the_best_matches_in_order()
    {
        // Large enough to be searched on several threads. The three strongest matches sit at the
        // start, in the middle and at the very end of the list, and every other name matches
        // less well and ties on score. A search that lost one thread's share, merged the shares
        // wrongly, or broke ties by position would return a different list from the plain
        // calculation below.
        var entries = new List<FileEntry>();
        for (int i = 0; i < 60_000; i++)
        {
            string name = i == 0 ? "report 0.txt"
                : i == 30_000 ? "report mid.txt"
                : i == 59_999 ? "report last.txt"
                : $"reminder portfolio table {i}.txt";
            entries.Add(File($@"{Docs}\folder{i % 300:000}", name));
        }

        var index = new FileIndex();
        index.AddRange(entries);

        var expected = entries
            .Select(entry => (Entry: entry, Score: FuzzyMatcher.Score("report", entry.Name)))
            .Where(scored => scored.Score is not null)
            .OrderByDescending(scored => scored.Score)
            .ThenBy(scored => scored.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(scored => scored.Entry.Directory, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(scored => new FileMatch(scored.Entry, scored.Score!.Value))
            .ToList();

        var found = index.Search("report", 50);

        Assert.Equal(["report 0.txt", "report mid.txt", "report last.txt"], found.Take(3).Select(match => match.Entry.Name));
        Assert.Equal(50, found.Count);
        Assert.Equal(expected, found);
    }

    [Fact]
    public async Task Searching_while_other_threads_change_the_index_stays_consistent()
    {
        var entries = new List<FileEntry>();
        for (int i = 0; i < 30_000; i++)
        {
            entries.Add(File($@"{Docs}\folder{i % 100:00}", $"file-{i:00000}.txt"));
        }

        var index = new FileIndex();
        index.AddRange(entries);

        using var writersDone = new CancellationTokenSource();
        var firstSearchDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searcher = Task.Run(() =>
        {
            int searches = 0;
            while (!writersDone.IsCancellationRequested)
            {
                Assert.Equal(50, index.Search("file", 50).Count);
                searches++;
                firstSearchDone.TrySetResult();
            }

            return searches;
        });

        // The writers start once a search has completed. Started sooner, on a single core they
        // can both finish before the searcher has run at all, and then nothing has been tested.
        var first = await Task.WhenAny(firstSearchDone.Task, searcher).WaitAsync(TimeSpan.FromSeconds(60));
        await first; // a search that failed ends the test here, with its own message

        var adder = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                string name = $"extra-{i:0000}.txt";
                Assert.True(index.Add(File(Docs + @"\extra", name)));
                Assert.True(index.Remove(Docs + @"\extra\" + name));
            }
        });
        var renamer = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                string original = $@"{Docs}\folder{i % 100:00}\file-{i:00000}.txt";
                string moved = $@"{Docs}\folder{i % 100:00}\moved-{i:00000}.txt";
                Assert.True(index.Rename(original, moved));
                Assert.True(index.Rename(moved, original));
            }
        });

        await Task.WhenAll(adder, renamer).WaitAsync(TimeSpan.FromSeconds(60));
        writersDone.Cancel();
        int searchesRun = await searcher.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(searchesRun > 0);
        Assert.Equal(30_000, index.Count);
        Assert.All(entries, entry => Assert.True(index.TryGet(entry.FullPath, out _)));
    }
}
