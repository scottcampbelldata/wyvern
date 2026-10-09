using Wyvern.Core.Settings;

namespace Wyvern.Tests;

public class DataPathsTests
{
    private static DataDirSetting From(string? dataDir) => DataDirSetting.From(new WyvernSettings { DataDir = dataDir });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_setting_means_the_default_folder(string? written)
    {
        Assert.Equal(new DataDirSetting(null, null), From(written));
    }

    [Theory]
    [InlineData(@"D:\Wyvern data", @"D:\Wyvern data")]
    [InlineData(@"  D:\Wyvern data\  ", @"D:\Wyvern data")]
    [InlineData(@"D:\Sync\..\Wyvern", @"D:\Wyvern")]
    [InlineData(@"\\server\share\wyvern", @"\\server\share\wyvern")]
    public void A_full_path_is_used_without_its_trailing_separator(string written, string expected)
    {
        Assert.Equal(new DataDirSetting(expected, null), From(written));
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(new DataDirSetting(Path.Combine(profile, "Wyvern"), null), From(@"%USERPROFILE%\Wyvern"));
    }

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData("data")]
    [InlineData(@"\no-drive")]
    [InlineData(@"C:no-separator")]
    [InlineData(@"%WYVERN_NO_SUCH_VARIABLE%\data")]
    public void Anything_that_is_not_a_full_path_is_refused_and_kept_as_written(string written)
    {
        Assert.Equal(new DataDirSetting(null, written), From(written));
    }

    // These are text and nothing else: no root and no share is created, read or listed. Sync
    // refuses the root of a drive or of a share (SyncFiles.IsDataFolder), but the library and
    // the clipboard history would use it, so the setting is refused where it is judged.
    [Theory]
    [InlineData(@"C:\")]
    [InlineData("D:/")]
    [InlineData(@"D:\")]
    [InlineData(@"  D:\  ")]
    [InlineData(@"C:\data\..")]
    [InlineData(@"D:\Sync\..\..")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\server\share")]
    [InlineData("//server/share/")]
    [InlineData(@"\\server\share\folder\..")]
    public void The_root_of_a_drive_or_of_a_share_is_refused_and_kept_as_written(string written)
    {
        Assert.Equal(new DataDirSetting(null, written), From(written));
    }

    [Theory]
    [InlineData(@"C:\data", @"C:\data")]
    [InlineData(@"D:\a\b", @"D:\a\b")]
    [InlineData(@"\\server\share\folder", @"\\server\share\folder")]
    [InlineData(@"\\server\share\folder\", @"\\server\share\folder")]
    public void A_folder_below_the_root_of_a_drive_or_of_a_share_is_used(string written, string expected)
    {
        Assert.Equal(new DataDirSetting(expected, null), From(written));
    }

    [Fact]
    public void A_variable_that_does_not_exist_is_refused_rather_than_taken_as_a_folder_name()
    {
        // After expansion "%WYVERN_NO_SUCH_VARIABLE%" is still there, so it would otherwise be
        // accepted as a folder literally named that.
        string written = @"D:\%WYVERN_NO_SUCH_VARIABLE%\data";

        Assert.Equal(new DataDirSetting(null, written), From(written));
    }

    [Fact]
    public void A_single_percent_sign_is_an_ordinary_character_in_a_folder_name()
    {
        Assert.Equal(new DataDirSetting(@"D:\50% done", null), From(@"D:\50% done"));
    }

    [Fact]
    public void The_data_folder_is_the_setting_when_there_is_one_and_the_default_otherwise()
    {
        var paths = new WyvernPaths(@"C:\Data\Wyvern");

        Assert.Equal(@"C:\Data\Wyvern\data", paths.Data(new DataDirSetting(null, null)).Root);
        Assert.Equal(@"C:\Data\Wyvern\data", paths.Data(new DataDirSetting(null, "relative")).Root);
        Assert.Equal(@"D:\Elsewhere", paths.Data(new DataDirSetting(@"D:\Elsewhere", null)).Root);
    }

    [Fact]
    public void The_library_is_laid_out_under_the_data_folder()
    {
        var data = new DataPaths(@"D:\Elsewhere");

        Assert.Equal(@"D:\Elsewhere\library", data.LibraryDir);
        Assert.Equal(@"D:\Elsewhere\library\quicklinks.json", data.QuicklinksFile);
        Assert.Equal(@"D:\Elsewhere\library\snippets", data.SnippetsDir);
        Assert.Equal(@"D:\Elsewhere\library\scripts", data.ScriptsDir);
    }
}
