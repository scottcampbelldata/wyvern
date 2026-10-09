using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Beacon.Core.Diagnostics;
using Beacon.Core.Search;

namespace Beacon.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private string LogPath => Path.Combine(_dir.Path, "logs", "beacon.log");

    [Fact]
    public void Writes_a_timestamped_line_and_creates_the_folder()
    {
        var log = new FileLog(LogPath, _clock);

        log.Info("Beacon started");

        Assert.Equal(["2026-10-03T12:00:00Z INFO Beacon started"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Appends_to_an_existing_file()
    {
        var log = new FileLog(LogPath, _clock);

        log.Info("one");
        log.Info("two");

        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void Line_breaks_in_a_message_cannot_start_a_new_entry()
    {
        var log = new FileLog(LogPath, _clock);

        log.Info("one\r\ntwo\nthree");

        Assert.Equal(["2026-10-03T12:00:00Z INFO one two three"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Error_names_the_exception_type_without_its_message()
    {
        var log = new FileLog(LogPath, _clock);

        log.Error("Action failed", new InvalidOperationException("boom"));

        Assert.Equal(["2026-10-03T12:00:00Z ERROR Action failed: System.InvalidOperationException (0x80131509)"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Error_adds_the_windows_error_number_for_a_win32_exception()
    {
        var log = new FileLog(LogPath, _clock);

        // Win32 error 5 is "access denied".
        log.Error("Could not open", new Win32Exception(5));

        Assert.Equal(["2026-10-03T12:00:00Z ERROR Could not open: System.ComponentModel.Win32Exception (0x80004005, Win32 5)"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Error_logs_the_exception_chain_and_stack_frames_but_never_exception_messages()
    {
        var log = new FileLog(LogPath, _clock);
        Exception thrown = Assert.Throws<InvalidOperationException>(ThrowWithInner);

        log.Error("Action failed", thrown);

        string[] lines = File.ReadAllLines(LogPath);
        Assert.Equal("2026-10-03T12:00:00Z ERROR Action failed: System.InvalidOperationException (0x80131509) <- System.IO.IOException (0x80131620)", lines[0]);
        Assert.True(lines.Length > 2);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("    ", line));
        Assert.Contains(lines, line => line.Contains(nameof(ThrowWithInner)));
        Assert.Contains("    caused by System.IO.IOException", lines);
        Assert.DoesNotContain("secret", File.ReadAllText(LogPath));
    }

    private static void ThrowWithInner()
    {
        try
        {
            throw new IOException("secret inner text");
        }
        catch (IOException inner)
        {
            throw new InvalidOperationException("secret outer text", inner);
        }
    }

    [Fact]
    public void Starts_a_new_file_when_the_size_limit_is_reached()
    {
        var log = new FileLog(LogPath, _clock, maxBytes: 60);

        log.Info("first line that is long enough to pass the sixty byte limit");
        log.Info("second");

        Assert.Contains("first line", File.ReadAllText(LogPath + ".1"));
        Assert.Equal(["2026-10-03T12:00:00Z INFO second"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Keeps_writing_when_the_old_file_cannot_be_replaced()
    {
        var log = new FileLog(LogPath, _clock, maxBytes: 10);
        log.Info("first line, already over the limit");

        using (new FileStream(LogPath + ".1", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            log.Info("second");
        }

        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void A_path_that_cannot_be_written_does_not_throw()
    {
        // The log path is an existing directory, so appending to it fails.
        Directory.CreateDirectory(LogPath);
        var log = new FileLog(LogPath, _clock);

        Assert.Null(Record.Exception(() => log.Error("dropped", new InvalidOperationException("boom"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0name.log")]
    public void An_invalid_path_does_not_throw(string path)
    {
        var log = new FileLog(path, _clock);

        Assert.Null(Record.Exception(() => log.Info("dropped")));
    }
}

public class PrivacyTests
{
    [Fact]
    public void Core_references_no_networking_assemblies()
    {
        var referenced = typeof(SearchEngine).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        // Proves the list is really being read, so the check below cannot pass on an empty list.
        Assert.Contains("System.Runtime", referenced);
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Fact]
    public void App_references_no_networking_assemblies()
    {
        // Beacon.Tests cannot reference the WPF project, so this reads the built Beacon.dll directly.
        // The test assembly sits in the test project folder under bin, then the configuration, then net10.0.
        var testOutput = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string configuration = testOutput.Parent!.Name;
        string repositoryRoot = testOutput.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        string appAssembly = Path.Combine(repositoryRoot, "src", "Beacon.App", "bin", configuration, "net10.0-windows10.0.19041.0", "Beacon.dll");
        Assert.True(File.Exists(appAssembly), $"Build the whole solution first (dotnet build Beacon.slnx). Expected {appAssembly}");

        using var stream = File.OpenRead(appAssembly);
        using var portableExecutable = new PEReader(stream);
        var metadata = portableExecutable.GetMetadataReader();
        var referenced = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();

        // Proves the list is really being read, so the check below cannot pass on an empty list.
        Assert.Contains("PresentationFramework", referenced);
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }
}
