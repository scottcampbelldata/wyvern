namespace Wyvern.Core.Platform;

/// <summary>
/// Protects a secret so that only the Windows user who is signed in, on this machine, can read
/// it back. The app implements it with the data protection of Windows (DPAPI); a test passes
/// its own. What is protected here is the sync key and the passphrase of an SSH key file.
/// </summary>
public interface IKeyProtector
{
    /// <summary>The secret in a form that can be written to a file.</summary>
    byte[] Protect(byte[] secret);

    /// <summary>The secret again. The caller wipes it when it is done with it.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The bytes were not protected by this user on this machine, or have been changed since.
    /// </exception>
    byte[] Unprotect(byte[] protectedBytes);
}
