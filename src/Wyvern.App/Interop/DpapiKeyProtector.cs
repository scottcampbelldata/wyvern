using System.Security.Cryptography;
using Wyvern.Core.Platform;

namespace Wyvern.App.Interop;

/// <summary>
/// Protects the sync key, and the passphrase of the SSH key file, with the data protection of
/// Windows (DPAPI) for the user who is signed in. What it returns can be read back only by
/// that Windows user: in another user's hands a copy of the file is of no use. On another
/// machine that user can read it only where Windows roams the user's protection keys there
/// (the roaming profile, or the roaming credentials, of a domain account); on any other
/// machine a copy is of no use either.
/// </summary>
/// <remarks>
/// The second argument is not a secret. It names what the bytes are for, so that something
/// another program protected for the same user is not taken for one of Wyvern's.
/// </remarks>
public sealed class DpapiKeyProtector : IKeyProtector
{
    // Kept from the app's old name, Beacon, so keys and synced data made before the rename still work.
    private static readonly byte[] Purpose = "Beacon sync secret v1"u8.ToArray();

    public byte[] Protect(byte[] secret) => ProtectedData.Protect(secret, Purpose, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(protectedBytes, Purpose, DataProtectionScope.CurrentUser);
}
