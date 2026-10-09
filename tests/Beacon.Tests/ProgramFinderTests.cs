using Beacon.Core.Scripts;

namespace Beacon.Tests;

/// <summary>
/// The program files here are empty files with the right names. Nothing is started: the
/// finder only looks at whether a file is there.
/// </summary>
public sealed class ProgramFinderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _first;
    private readonly string _second;
    private readonly string _path;

    public ProgramFinderTests()
    {
        _first = Directory.CreateDirectory(_dir.File("first")).FullName;
        _second = Directory.CreateDirectory(_dir.File("second folder")).FullName;
        _path = _first + ";" + _second;
    }

    public void Dispose() => _dir.Dispose();

    private static string Touch(string folder, string name)
    {
        string file = Path.Combine(folder, name);
        File.WriteAllText(file, "");
        return file;
    }

    [Fact]
    public void A_bare_name_is_found_in_a_folder_of_the_path_and_given_its_exe_ending()
    {
        string tool = Touch(_second, "tool.exe");

        Assert.Equal(tool, ProgramFinder.Find("tool", _path));
        Assert.Equal(tool, ProgramFinder.Find("tool.exe", _path));
        Assert.Equal(tool, ProgramFinder.Find("  TOOL.EXE  ", _path), ignoreCase: true);
    }

    [Fact]
    public void The_first_folder_of_the_path_that_holds_it_wins()
    {
        string first = Touch(_first, "tool.exe");
        Touch(_second, "tool.exe");

        Assert.Equal(first, ProgramFinder.Find("tool", _path));
    }

    [Fact]
    public void A_name_with_a_dot_in_it_still_gets_its_exe_ending()
    {
        string python = Touch(_first, "python3.12.exe");

        Assert.Equal(python, ProgramFinder.Find("python3.12", _path));
    }

    [Fact]
    public void A_program_that_is_nowhere_on_the_path_is_not_found()
    {
        Assert.Null(ProgramFinder.Find("tool", _path));
        Assert.Null(ProgramFinder.Find("tool", ""));
    }

    [Theory]
    [InlineData("tool.cmd")]
    [InlineData("tool.bat")]
    [InlineData("tool.ps1")]
    public void A_file_that_is_not_an_exe_is_never_taken_for_a_program(string name)
    {
        Touch(_first, name);

        Assert.Null(ProgramFinder.Find(name, _path));
        Assert.Null(ProgramFinder.Find("tool", _path));
        Assert.Null(ProgramFinder.Find(Path.Combine(_first, name), _path));
    }

    [Fact]
    public void A_full_path_is_used_as_it_is_when_the_file_exists()
    {
        string tool = Touch(_second, "tool.exe");

        Assert.Equal(tool, ProgramFinder.Find(tool, path: ""));
        Assert.Equal(tool, ProgramFinder.Find(Path.Combine(_second, "tool"), path: ""));
        Assert.Null(ProgramFinder.Find(Path.Combine(_second, "missing.exe"), path: ""));
    }

    [Fact]
    public void Environment_variables_in_a_full_path_are_expanded()
    {
        // A program every Windows has, named through a variable every Windows has.
        string? found = ProgramFinder.Find(@"%SystemRoot%\System32\cmd.exe", path: "");

        Assert.NotNull(found);
        Assert.True(File.Exists(found));
        Assert.DoesNotContain("%", found);
    }

    [Fact]
    public void A_variable_that_does_not_exist_is_not_taken_as_part_of_a_name()
    {
        // A file really called that, where it would be found if the text were taken as it stands.
        Touch(_first, "%BEACON_NO_SUCH_VARIABLE%.exe");

        Assert.Null(ProgramFinder.Find("%BEACON_NO_SUCH_VARIABLE%", _path));
        Assert.Null(ProgramFinder.Find(Path.Combine(_first, "%BEACON_NO_SUCH_VARIABLE%.exe"), _path));
    }

    [Theory]
    [InlineData(@"first\tool")]
    [InlineData(@"first\tool.exe")]
    [InlineData(@".\first\tool.exe")]
    [InlineData("first/tool.exe")]
    [InlineData(@"\tool.exe")]
    [InlineData("C:tool.exe")]
    public void A_relative_path_is_never_used(string program)
    {
        // The folder above "first" is on the path here, so each of the first four would name
        // a real file if a relative path were simply added to a folder of the path.
        Touch(_first, "tool.exe");

        Assert.Null(ProgramFinder.Find(program, path: _dir.Path));
    }

    [Fact]
    public void Entries_of_the_path_that_are_empty_quoted_relative_or_not_a_folder_name_are_coped_with()
    {
        string tool = Touch(_second, "tool.exe");
        string path = $";;relative\\folder; ;\"{_second}\";C:\\bad<>|name";

        Assert.Equal(tool, ProgramFinder.Find("tool", path));
    }

    [Fact]
    public void A_relative_entry_of_the_path_is_skipped_even_when_it_names_a_folder_that_exists()
    {
        // The folder is real and holds tool.exe, and the entry is written relative to the
        // folder this test runs in. Used, it would make the program found depend on where
        // Beacon happened to be started from. The folder is made under the current folder, not
        // under the temporary one: on another drive there is no relative way to write it.
        string folder = Path.Combine(Environment.CurrentDirectory, "beacon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Touch(folder, "tool.exe");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, folder);
            Assert.False(Path.IsPathFullyQualified(relative), "the test needs a relative path to the folder");
            Assert.True(File.Exists(Path.Combine(relative, "tool.exe")));

            Assert.Null(ProgramFinder.Find("tool", relative));
            Assert.Null(ProgramFinder.Find("tool", ".;" + relative));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_entry_in_quotes_that_holds_a_semicolon_is_not_cut_in_two_and_searched()
    {
        // The PATH is split at every semicolon, so the first half is "\"<folder>" with its quote
        // left open. It is not a folder on the PATH, although it names one that exists.
        Touch(_first, "tool.exe");
        Touch(_second, "tool.exe");

        Assert.Null(ProgramFinder.Find("tool", "\"" + _first + ";more\""));
        Assert.Null(ProgramFinder.Find("tool", "\"more;" + _second + "\""));
    }

    [Fact]
    public void An_entry_in_quotes_that_pairs_is_still_searched()
    {
        string tool = Touch(_second, "tool.exe");

        Assert.Equal(tool, ProgramFinder.Find("tool", "\"" + _second + "\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("to\0ol")]
    [InlineData("to|ol")]
    public void Text_that_is_not_a_program_name_finds_nothing_and_does_not_throw(string program)
    {
        Assert.Null(ProgramFinder.Find(program, _path));
    }

    [Fact]
    public void The_real_path_of_this_machine_holds_cmd()
    {
        string? cmd = ProgramFinder.Find("cmd");

        Assert.NotNull(cmd);
        Assert.True(Path.IsPathFullyQualified(cmd));
        Assert.EndsWith("cmd.exe", cmd, StringComparison.OrdinalIgnoreCase);
    }
}
