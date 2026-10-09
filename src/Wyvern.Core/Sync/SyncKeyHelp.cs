namespace Wyvern.Core.Sync;

/// <summary>
/// Help with the one thing sync needs that Wyvern cannot be given by typing: an SSH key file
/// on this PC that the server accepts. The command that makes one shows these words. The key
/// file stays on this PC; only its public half, which is no secret, is shown.
/// </summary>
public static class SyncKeyHelp
{
    /// <summary>The name of the key file Wyvern makes, in the .ssh folder of the owner's profile.</summary>
    public const string FileName = "wyvern_ed25519";

    /// <summary>Shown when no key could be made or read.</summary>
    public const string Failed =
        "No key could be made or read. Windows' own ssh-keygen is used for it (Settings > System > Optional features > OpenSSH Client). Details are in the log file.";

    /// <summary>The full path of the key file for a profile folder.</summary>
    public static string KeyFile(string userProfile) => Path.Combine(userProfile, ".ssh", FileName);

    /// <summary>True for a line that reads as the public half of a key: its kind, its text and at most a comment, on one line.</summary>
    public static bool IsPublicKey(string? line)
    {
        if (line is null || line.Length is 0 or > 2000 || line.Any(char.IsControl))
        {
            return false;
        }

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].StartsWith("ssh-", StringComparison.Ordinal) && parts[1].Length >= 40;
    }

    /// <summary>What to do with the key, step by step.</summary>
    /// <param name="publicKey">The public half, one line.</param>
    /// <param name="keyFile">The full path of the key file on this PC.</param>
    /// <param name="created">True when the key was made just now, false when it was there already.</param>
    public static string Steps(string publicKey, string keyFile, bool created) =>
        (created ? "A new key was made for this PC." : "This PC already has a key for Wyvern. It was not changed.")
        + "\n\n1. On your server, add this one line to the end of the file ~/.ssh/authorized_keys of the user Wyvern signs in as:\n\n"
        + publicKey
        + "\n\n2. On this PC, run Sync Setup and give it this key file:\n\n"
        + keyFile
        + "\n\nThe line in step 1 is the public half of the key: it is no secret, and it can be selected here with the mouse and copied. "
        + "The key file itself stays on this PC. Wyvern never sends it anywhere.";
}
