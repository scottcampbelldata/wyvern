using Wyvern.Core.AutoExpand;
using Wyvern.Core.Settings;

namespace Wyvern.Tests;

/// <summary>
/// The switch for snippet auto-expand in settings.json. While it is on Wyvern is told of every
/// key that is pressed, so these tests hold one thing above all: nothing but the JSON value
/// true switches it on.
/// </summary>
public sealed class AutoExpandOptionsTests : IDisposable
{
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
    [InlineData("""{ "snippetAutoExpand": null }""")]
    [InlineData("""{ "snippetAutoExpand": false }""")]
    public void Without_the_key_and_with_false_auto_expand_is_off_and_nothing_is_said(string json)
    {
        var options = AutoExpandOptions.From(Load(json).Settings);

        Assert.False(options.Enabled);
        Assert.Empty(options.Notes);
    }

    [Fact]
    public void The_default_is_off()
    {
        Assert.False(AutoExpandOptions.Default.Enabled);
        Assert.Empty(AutoExpandOptions.Default.Notes);
        Assert.False(AutoExpandOptions.From(new WyvernSettings()).Enabled);
    }

    [Theory]
    [InlineData("""{ "snippetAutoExpand": true }""")]
    [InlineData("""{ "SnippetAutoExpand": true }""")]
    [InlineData("""{ "snippetautoexpand": true }""")]
    public void Only_the_value_true_switches_it_on(string json)
    {
        var options = AutoExpandOptions.From(Load(json).Settings);

        Assert.True(options.Enabled);
        Assert.Empty(options.Notes);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"yes\"")]
    [InlineData("\"on\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("[true]")]
    [InlineData("{ \"enabled\": true }")]
    public void Anything_that_is_neither_true_nor_false_leaves_it_off_and_says_so(string written)
    {
        var options = AutoExpandOptions.From(Load($$"""{ "snippetAutoExpand": {{written}} }""").Settings);

        Assert.False(options.Enabled);
        Assert.Equal(["\"snippetAutoExpand\" must be true or false. Snippet auto-expand is off."], options.Notes);
        Assert.Equal(AutoExpandOptions.NotTrueOrFalseNote, Assert.Single(options.Notes));
    }

    [Fact]
    public void A_setting_of_the_wrong_kind_does_not_make_settings_json_unreadable()
    {
        var loaded = Load("""{ "hotkey": "Win+1", "snippetAutoExpand": "true" }""");

        Assert.Equal("Win+1", loaded.Settings.Hotkey);
        Assert.False(AutoExpandOptions.From(loaded.Settings).Enabled);
    }

    [Theory]
    [InlineData("""{ "snippetAutoExpand": true, "snippetAutoExpand": false }""", false)]
    [InlineData("""{ "snippetAutoExpand": false, "snippetAutoExpand": true }""", true)]
    public void When_the_key_is_written_twice_the_later_one_counts(string json, bool enabled)
    {
        Assert.Equal(enabled, AutoExpandOptions.From(Load(json).Settings).Enabled);
    }

    [Fact]
    public void The_defaults_file_shows_the_switch_and_shows_it_off()
    {
        var loaded = SettingsStore.Load(SettingsPath);

        Assert.Null(loaded.Error);
        Assert.Contains("\"snippetAutoExpand\": false", File.ReadAllText(SettingsPath));
        Assert.False(AutoExpandOptions.From(loaded.Settings).Enabled);

        // Read back from the file that was written, it is off as well, with nothing to say.
        var reread = AutoExpandOptions.From(SettingsStore.Load(SettingsPath).Settings);
        Assert.False(reread.Enabled);
        Assert.Empty(reread.Notes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_the_switch_when_settings_json_was_read(bool firstLoad)
    {
        var on = SettingsDecision.From(Load("""{ "snippetAutoExpand": true }"""), firstLoad);
        var off = SettingsDecision.From(Load("""{ "snippetAutoExpand": false }"""), firstLoad);
        var absent = SettingsDecision.From(Load("{ }"), firstLoad);

        Assert.True(on.AutoExpand!.Enabled);
        Assert.False(off.AutoExpand!.Enabled);
        Assert.False(absent.AutoExpand!.Enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_decision_carries_no_switch_when_settings_json_could_not_be_read(bool firstLoad)
    {
        // Null means "leave it as it is": off at startup, and on Reload as the owner last had it.
        // A file that cannot be read must never be what switches the keyboard hook on.
        File.WriteAllText(SettingsPath, """{ "snippetAutoExpand": true, not json""");

        var decision = SettingsDecision.From(SettingsStore.Load(SettingsPath), firstLoad);

        Assert.Null(decision.AutoExpand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hotkey_that_cannot_be_parsed_does_not_lose_the_switch(bool firstLoad)
    {
        var decision = SettingsDecision.From(Load("""{ "hotkey": "Banana", "snippetAutoExpand": true }"""), firstLoad);

        Assert.True(decision.HotkeyInvalid);
        Assert.True(decision.AutoExpand!.Enabled);
    }

    [Fact]
    public void A_note_about_the_switch_is_fixed_words_and_quotes_nothing_from_the_file()
    {
        var options = AutoExpandOptions.From(Load("""{ "snippetAutoExpand": "the-private-walrus" }""").Settings);

        Assert.DoesNotContain("walrus", Assert.Single(options.Notes));
    }
}
