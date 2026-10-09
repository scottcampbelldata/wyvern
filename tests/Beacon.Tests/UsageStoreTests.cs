using Beacon.Core.Search;

namespace Beacon.Tests;

public sealed class UsageStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private UsageStore NewStore() => new(_dir.File("usage.json"), _clock);

    [Fact]
    public void Item_never_used_has_no_boost()
    {
        Assert.Equal(1.0, NewStore().Boost("app:notepad"));
    }

    [Fact]
    public void Each_recent_use_adds_five_percent()
    {
        var store = NewStore();

        store.RecordUse("app:notepad");
        store.RecordUse("app:notepad");

        Assert.Equal(1.10, store.Boost("app:notepad"), precision: 3);
    }

    [Fact]
    public void Boost_is_capped_at_double()
    {
        var store = NewStore();

        for (int i = 0; i < 25; i++)
        {
            store.RecordUse("app:notepad");
        }

        Assert.Equal(2.0, store.Boost("app:notepad"), precision: 3);
    }

    [Theory]
    [InlineData(0.5, 1.050)]
    [InlineData(3, 1.035)]
    [InlineData(10, 1.020)]
    [InlineData(60, 1.010)]
    public void Boost_fades_as_the_last_use_gets_older(double daysAgo, double expected)
    {
        var store = NewStore();
        store.RecordUse("app:notepad");

        _clock.Advance(TimeSpan.FromDays(daysAgo));

        Assert.Equal(expected, store.Boost("app:notepad"), precision: 3);
    }

    [Fact]
    public void Usage_survives_a_restart()
    {
        NewStore().RecordUse("app:notepad");

        Assert.Equal(1.05, NewStore().Boost("app:notepad"), precision: 3);
    }

    [Fact]
    public void Corrupt_file_is_treated_as_empty_and_replaced_on_next_use()
    {
        File.WriteAllText(_dir.File("usage.json"), "{ not json");

        var store = NewStore();
        Assert.Equal(1.0, store.Boost("app:notepad"));

        store.RecordUse("app:notepad");
        Assert.Equal(1.05, NewStore().Boost("app:notepad"), precision: 3);
    }

    [Fact]
    public void Null_entries_in_the_file_are_ignored()
    {
        File.WriteAllText(_dir.File("usage.json"), """{ "app:notepad": null }""");

        var store = NewStore();
        Assert.Equal(1.0, store.Boost("app:notepad"));

        store.RecordUse("app:notepad");
        Assert.Equal(1.05, store.Boost("app:notepad"), precision: 3);
    }

    [Fact]
    public void File_locked_by_another_program_is_treated_as_empty()
    {
        NewStore().RecordUse("app:notepad");
        using var locked = new FileStream(_dir.File("usage.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(1.0, NewStore().Boost("app:notepad"));
    }

    [Fact]
    public void Use_is_still_counted_when_the_file_cannot_be_written()
    {
        // The usage path is an existing directory, so every save fails.
        Directory.CreateDirectory(_dir.File("usage.json"));
        var store = NewStore();

        store.RecordUse("app:notepad");

        Assert.Equal(1.05, store.Boost("app:notepad"), precision: 3);
    }

    [Fact]
    public void Creates_the_folder_when_it_does_not_exist()
    {
        var store = new UsageStore(Path.Combine(_dir.Path, "nested", "usage.json"), _clock);

        store.RecordUse("app:notepad");

        Assert.True(File.Exists(Path.Combine(_dir.Path, "nested", "usage.json")));
    }

    [Fact]
    public void Lists_the_ids_of_one_kind_of_item()
    {
        var store = NewStore();
        store.RecordUse("app:notepad");
        store.RecordUse(@"file:C:\Docs\budget.xlsx");
        store.RecordUse(@"file:C:\Docs\notes.txt");
        store.RecordUse(@"file:C:\Docs\notes.txt");

        Assert.Equal(
            [@"file:C:\Docs\budget.xlsx", @"file:C:\Docs\notes.txt"],
            store.IdsStartingWith("file:").Order());
        Assert.Empty(store.IdsStartingWith("command:"));
    }
}
