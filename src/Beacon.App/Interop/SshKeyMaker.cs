using System.Diagnostics;
using System.IO;
using Beacon.Core.Sync;

namespace Beacon.App.Interop;

/// <summary>
/// Makes an SSH key file for this PC with Windows' own ssh-keygen, or reads the one that is
/// there, and gives its public half. A key that is there is never written over.
/// </summary>
public static class SshKeyMaker
{
    /// <param name="File">The full path of the key file.</param>
    /// <param name="PublicKey">Its public half, one line.</param>
    /// <param name="Created">True when the key was made by this call.</param>
    public sealed record Made(string File, string PublicKey, bool Created);

    /// <exception cref="InvalidOperationException">No key could be made or read. The words say which step failed and nothing of the key.</exception>
    public static Made Make(string keyFile)
    {
        string publicFile = keyFile + ".pub";
        if (File.Exists(keyFile))
        {
            return new Made(keyFile, PublicHalf(publicFile), Created: false);
        }

        string keygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
        if (!File.Exists(keygen))
        {
            throw new InvalidOperationException("ssh-keygen is not installed.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);

        // An Ed25519 key without a passphrase, in the file given: ssh-keygen keeps the file to
        // the owner alone. -q says nothing, and the comment names this PC in the public half.
        var start = new ProcessStartInfo(keygen) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", "beacon@" + Environment.MachineName, "-f", keyFile })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ssh-keygen could not be started.");
        if (!process.WaitForExit(20_000))
        {
            process.Kill();
            throw new InvalidOperationException("ssh-keygen did not finish.");
        }

        if (process.ExitCode != 0 || !File.Exists(keyFile))
        {
            throw new InvalidOperationException("ssh-keygen made no key.");
        }

        return new Made(keyFile, PublicHalf(publicFile), Created: true);
    }

    private static string PublicHalf(string publicFile)
    {
        if (!File.Exists(publicFile))
        {
            throw new InvalidOperationException("The key file is there without its public half beside it.");
        }

        string line = File.ReadLines(publicFile).FirstOrDefault()?.Trim() ?? "";
        return SyncKeyHelp.IsPublicKey(line) ? line : throw new InvalidOperationException("The public half of the key does not read as one.");
    }
}
