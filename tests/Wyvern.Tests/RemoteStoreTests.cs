using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Wyvern.Core.Sync;

namespace Wyvern.Tests;

/// <summary>
/// What every remote store promises, shown on the one that lives in memory (every later test of
/// sync rests on it behaving like this), and the rules that keep the network code in one place.
/// </summary>
public class RemoteStoreTests
{
    private static readonly string Name = new('a', 64);
    private static readonly string Other = new('b', 64);

    // How long a test waits for a signal that comes at once when the code is right. It is only
    // ever reached by a test that is failing.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly MemoryRemoteStore _store = new();

    [Fact]
    public void An_uploaded_blob_is_listed_with_its_size_and_the_servers_time_and_comes_back_the_same()
    {
        _store.Clock = new DateTimeOffset(2026, 10, 3, 12, 0, 0, 750, TimeSpan.Zero);

        var stored = _store.Upload(Name, [1, 2, 3]);

        Assert.Equal(new RemoteBlob(Name, 3, new DateTimeOffset(2026, 10, 3, 12, 0, 1, TimeSpan.Zero)), stored);
        Assert.Equal([stored], _store.List());
        Assert.Equal([1, 2, 3], _store.Download(Name, 3));
    }

    [Fact]
    public void A_second_upload_replaces_the_blob_whole_and_gives_it_a_later_time()
    {
        var first = _store.Upload(Name, [1, 2, 3]);

        var second = _store.Upload(Name, [9]);

        Assert.Equal([9], _store.Download(Name, 10));
        Assert.Single(_store.List());
        Assert.True(second.ModifiedUtc > first.ModifiedUtc);
    }

    [Fact]
    public void A_blob_larger_than_the_caller_allows_is_not_handed_over()
    {
        _store.Upload(Name, new byte[11]);

        Assert.Equal(RemoteFailure.TooMuch, Assert.Throws<RemoteStoreException>(() => _store.Download(Name, 10)).Failure);
    }

    [Fact]
    public void A_blob_that_is_not_there_is_missing_and_deleting_it_is_not_an_error()
    {
        Assert.Equal(RemoteFailure.Missing, Assert.Throws<RemoteStoreException>(() => _store.Download(Name, 10)).Failure);

        _store.Delete(Name);

        Assert.Empty(_store.List());
    }

    [Fact]
    public void Deleting_takes_one_blob_and_leaves_the_others()
    {
        _store.Upload(Name, [1]);
        _store.Upload(Other, [2]);

        _store.Delete(Name);

        Assert.Equal([Other], _store.Names);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notes.txt")]
    [InlineData("../aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_name_that_is_not_a_blobs_name_is_refused_before_the_server_is_asked(string name)
    {
        Assert.Throws<ArgumentException>(() => _store.Download(name, 10));
        Assert.Throws<ArgumentException>(() => _store.Upload(name, [1]));
        Assert.Throws<ArgumentException>(() => _store.Delete(name));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public void A_temporary_name_can_be_deleted_and_nothing_else_can_be_done_with_it()
    {
        string temporary = BlobCodec.TemporaryName(Name);
        _store.Put(temporary, [1]);

        Assert.Throws<ArgumentException>(() => _store.Download(temporary, 10));
        Assert.Throws<ArgumentException>(() => _store.Upload(temporary, [1]));
        _store.Delete(temporary);

        Assert.Empty(_store.Names);
    }

    [Fact]
    public void A_call_that_is_made_to_fail_changes_nothing()
    {
        _store.Upload(Name, [1]);
        _store.Fault = call => call.StartsWith("upload", StringComparison.Ordinal) ? new RemoteStoreException(RemoteFailure.Unreachable) : null;

        Assert.Equal(RemoteFailure.Unreachable, Assert.Throws<RemoteStoreException>(() => _store.Upload(Name, [2])).Failure);

        Assert.Equal([1], _store.Download(Name, 10));
        Assert.Equal(1, _store.Count("upload"));
    }

    [Fact]
    public void A_connection_that_was_closed_answers_nothing_more_and_a_new_one_does()
    {
        var connector = new MemoryConnector(_store);
        Assert.True(SyncConfig.TryCreate("otter.example.org", "", "walrus", @"C:\Keys\otter", "/wyvern", MemoryConnector.ServerKey, out var config, out _));

        var first = connector.Open(config!, null);
        first.Upload(Name, [1]);
        first.Dispose();

        Assert.Equal(RemoteFailure.Unreachable, Assert.Throws<RemoteStoreException>(() => first.List()).Failure);
        Assert.Equal(0, connector.OpenNow);
        using var second = connector.Open(config!, null);
        Assert.Single(second.List());
    }

    [Fact]
    public async Task Closing_a_connection_ends_the_call_that_is_under_way_as_unreachable_and_it_changes_nothing()
    {
        var connector = new MemoryConnector(_store);
        Assert.True(SyncConfig.TryCreate("otter.example.org", "", "walrus", @"C:\Keys\otter", "/wyvern", MemoryConnector.ServerKey, out var config, out _));
        var connection = connector.Open(config!, null);
        int held = 0;
        using var inFlight = new ManualResetEventSlim();
        connector.InFlight = closed =>
        {
            // The call waits here as it would wait for a server that does not answer. Nothing
            // but the closing of its connection lets it go.
            Interlocked.Increment(ref held);
            inFlight.Set();
            Assert.True(closed.WaitOne(Limit));
        };

        var call = Task.Run(() => connection.Upload(Name, [1]));
        Assert.True(inFlight.Wait(Limit));
        connection.Dispose();

        var ended = await Assert.ThrowsAsync<RemoteStoreException>(() => call.WaitAsync(Limit));
        Assert.Equal(RemoteFailure.Unreachable, ended.Failure);
        Assert.Equal("closed", ended.Cause);
        Assert.Empty(_store.Names);
        Assert.Empty(_store.Calls);
        Assert.Equal(0, connector.OpenNow);

        // A call that is started after the close fails at once: it is not held at all.
        Assert.Equal(RemoteFailure.Unreachable, Assert.Throws<RemoteStoreException>(() => connection.Upload(Name, [1])).Failure);
        Assert.Equal(1, held);
    }

    [Fact]
    public void The_folder_is_listed_in_an_order_that_is_not_sorted_as_a_server_lists_it()
    {
        string third = new('c', 64);
        _store.Upload(Other, [2]);
        _store.Upload(third, [3]);
        _store.Upload(Name, [1]);

        // A fixed order, so that a test fails the same way every time, and not the sorted one:
        // code that only works on a sorted listing must not pass against this folder.
        Assert.Equal([third, Other, Name], _store.List().Select(blob => blob.Name));
    }

    [Fact]
    public void The_exception_of_a_remote_store_has_fixed_words_and_nothing_inside_it()
    {
        var failure = new RemoteStoreException(RemoteFailure.Refused, "SshAuthenticationException");

        Assert.Equal("The sync server could not be used.", failure.Message);
        Assert.Equal("SshAuthenticationException", failure.Cause);
        Assert.Null(failure.InnerException);

        // No constructor takes an exception, so none can ever be kept.
        Assert.DoesNotContain(
            typeof(RemoteStoreException).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Any(parameter => typeof(Exception).IsAssignableFrom(parameter.ParameterType)));
    }

    [Fact]
    public void Every_record_of_sync_prints_a_short_fixed_form_of_its_own()
    {
        // A record prints all of its members unless it says otherwise, and these hold the
        // server's name, paths and names of the owner's files.
        var records = typeof(SyncConfig).Assembly.GetTypes()
            .Where(type => type.Namespace == "Wyvern.Core.Sync" && type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null)
            .ToList();

        Assert.Contains(typeof(SyncConfig), records);
        Assert.Contains(typeof(BlobHeader), records);
        Assert.Contains(typeof(ClipboardPath), records);
        foreach (var record in records)
        {
            var print = record.GetMethod(nameof(ToString), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, Type.EmptyTypes);
            Assert.True(
                print is not null && print.GetCustomAttribute<CompilerGeneratedAttribute>() is null,
                $"{record.Name} is a record without a ToString of its own");
        }
    }

    [Fact]
    public void Only_the_SFTP_store_uses_the_SSH_library_or_anything_of_the_network()
    {
        string root = RepositoryRoot();
        var offences = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            files++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (NetworkCode().IsMatch(lines[i]) && relative != "src/Wyvern.Core/Sync/SftpRemoteStore.cs")
                {
                    offences.Add($"{relative}:{i + 1}");
                }
            }
        }

        // Proves the files are really being read, so the check cannot pass on an empty list.
        Assert.True(files > 100, $"only {files} source files were found under {root}");
        Assert.True(offences.Count == 0, "Network code outside the SFTP store. Look at: " + string.Join(", ", offences));
    }

    [Fact]
    public void The_SFTP_store_uses_the_SSH_library_and_none_of_its_other_ways_to_connect()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wyvern.Core", "Sync", "SftpRemoteStore.cs"));

        Assert.Contains("using Renci.SshNet;", source);

        // No type of .NET's own network namespaces, no port forwarding, no proxy, no command
        // run on the server. The library's logging is never switched on, neither for all of it
        // nor for one connection, and no connection is kept alive by packets of its own.
        Assert.DoesNotMatch(@"using System\.Net|HttpClient|WebRequest|ForwardedPort|ProxyTypes|SshClient|ScpClient|RunCommand|InitializeLogging|LoggerFactory|KeepAliveInterval", source);
    }

    [Fact]
    public void The_SSH_library_is_the_only_assembly_Wyvern_Core_uses_that_is_not_part_of_dotnet()
    {
        var referenced = typeof(SyncConfig).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name!).ToList();

        Assert.Contains("System.Runtime", referenced);
        Assert.Equal(["Renci.SshNet"], referenced.Where(name => !name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)));
        Assert.DoesNotContain(referenced, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_one_package_is_referenced_by_the_app_and_its_version_is_pinned_exactly()
    {
        string root = RepositoryRoot();
        string core = File.ReadAllText(Path.Combine(root, "src", "Wyvern.Core", "Wyvern.Core.csproj"));
        string app = File.ReadAllText(Path.Combine(root, "src", "Wyvern.App", "Wyvern.App.csproj"));

        var references = Regex.Matches(core + app, "<PackageReference[^>]*>").Select(match => match.Value).ToList();

        Assert.Equal(["<PackageReference Include=\"SSH.NET\" Version=\"[2026.0.0]\" />"], references);
    }

    // Anything that names the SSH library, the network namespaces of .NET, or the usual ways to
    // make a request.
    private static Regex NetworkCode() => new(@"Renci\.|System\.Net\b|\bHttpClient\b|\bWebRequest\b|\bWebClient\b|\bTcpClient\b|\bUdpClient\b|\bSocket\b|\bDns\.");

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
}
