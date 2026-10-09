using Wyvern.Core.Settings;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>The one sync setting of settings.json, and the per-machine files sync keeps beside it.</summary>
public sealed class SyncOptionsTests : IDisposable
{
    private const string Note = "\"syncIntervalMinutes\" must be a whole number from 1 to 1440. 5 is used instead.";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.File("settings.json");

    private SettingsLoadResult Load(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var loaded = SettingsStore.Load(SettingsPath);
        Assert.Null(loaded.Error);
        return loaded;
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("""{ "syncIntervalMinutes": null }""")]
    public void Without_the_key_sync_runs_every_five_minutes(string json)
    {
        var options = SyncOptions.From(Load(json).Settings);

        Assert.Equal(TimeSpan.FromMinutes(5), options.Interval);
        Assert.Empty(options.Notes);
        Assert.Equal(TimeSpan.FromMinutes(5), SyncOptions.Default.Interval);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("15", 15)]
    [InlineData("15.0", 15)]
    [InlineData("3e1", 30)]
    [InlineData("1440", 1440)]
    public void A_whole_number_of_minutes_from_1_to_1440_is_used_however_it_is_written(string written, int minutes)
    {
        var options = SyncOptions.From(Load($$"""{ "syncIntervalMinutes": {{written}} }""").Settings);

        Assert.Equal(TimeSpan.FromMinutes(minutes), options.Interval);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1441")]
    [InlineData("2.5")]
    [InlineData("\"5\"")]
    [InlineData("true")]
    [InlineData("[5]")]
    [InlineData("0.99999999999999999999999999999")]
    [InlineData("1e400")]
    public void Anything_else_gives_a_note_and_five_minutes(string written)
    {
        var options = SyncOptions.From(Load($$"""{ "syncIntervalMinutes": {{written}} }""").Settings);

        Assert.Equal(TimeSpan.FromMinutes(5), options.Interval);
        Assert.Equal([Note], options.Notes);
    }

    [Fact]
    public void A_setting_of_the_wrong_kind_does_not_make_settings_json_unreadable()
    {
        var loaded = Load("""{ "hotkey": "Win+1", "syncIntervalMinutes": { "every": "hour" } }""");

        Assert.Equal("Win+1", loaded.Settings.Hotkey);
        Assert.Equal([Note], SyncOptions.From(loaded.Settings).Notes);
    }

    [Fact]
    public void The_defaults_file_shows_the_interval()
    {
        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Null(loaded.Error);
        Assert.Contains("\"syncIntervalMinutes\": 5", File.ReadAllText(SettingsPath));
        Assert.Empty(SyncOptions.From(SettingsStore.Load(SettingsPath).Settings).Notes);
    }

    [Fact]
    public void The_defaults_file_holds_nothing_about_where_sync_connects_to()
    {
        SettingsStore.Load(SettingsPath);
        string written = File.ReadAllText(SettingsPath);

        Assert.DoesNotContain("host", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remoteDir", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_decision_carries_the_sync_setting_when_settings_json_was_read()
    {
        var decision = SettingsDecision.From(Load("""{ "syncIntervalMinutes": 20 }"""), firstLoad: true);

        Assert.Equal(TimeSpan.FromMinutes(20), decision.Sync!.Interval);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_no_sync_setting_when_settings_json_could_not_be_read(bool firstLoad)
    {
        File.WriteAllText(SettingsPath, "{ not json");

        var decision = SettingsDecision.From(SettingsStore.Load(SettingsPath), firstLoad);

        Assert.Null(decision.Sync);
    }

    [Fact]
    public void A_hotkey_that_cannot_be_parsed_does_not_lose_the_sync_setting()
    {
        var decision = SettingsDecision.From(Load("""{ "hotkey": "Banana", "syncIntervalMinutes": 20 }"""), firstLoad: false);

        Assert.True(decision.HotkeyInvalid);
        Assert.Equal(TimeSpan.FromMinutes(20), decision.Sync!.Interval);
    }

    [Fact]
    public void Sync_keeps_its_files_in_the_Wyvern_root_and_not_in_the_data_folder()
    {
        var paths = new WyvernPaths(@"C:\Otter\Wyvern");

        Assert.Equal(@"C:\Otter\Wyvern\sync.json", paths.SyncFile);
        Assert.Equal(@"C:\Otter\Wyvern\sync-state.json", paths.SyncStateFile);
        Assert.Equal(@"C:\Otter\Wyvern\sync-paused", paths.SyncPausedFile);
        Assert.Equal(@"C:\Otter\Wyvern\key.bin", paths.KeyFile);
        Assert.Equal(@"C:\Otter\Wyvern\ssh-passphrase.bin", paths.PassphraseFile);
        Assert.Equal(@"C:\Otter\Wyvern\clipboard-folders.json", paths.ClipboardFoldersFile);
    }
}
