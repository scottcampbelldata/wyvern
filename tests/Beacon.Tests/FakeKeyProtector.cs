using System.Security.Cryptography;
using Beacon.Core.Platform;

namespace Beacon.Tests;

/// <summary>
/// Stands in for the data protection of Windows. What it gives back is not the secret as it
/// was (so a test can see that a file does not hold the secret in the clear), and bytes that
/// it did not make are refused, as Windows refuses what another user protected.
/// </summary>
public sealed class FakeKeyProtector : IKeyProtector
{
    private static readonly byte[] Mark = "protected-by-the-fake:"u8.ToArray();

    /// <summary>When true, protecting fails as it does when Windows refuses.</summary>
    public bool RefuseToProtect { get; set; }

    /// <summary>When true, what is protected cannot be read back, as with a profile that is damaged.</summary>
    public bool RefuseToUnprotect { get; set; }

    public byte[] Protect(byte[] secret)
    {
        if (RefuseToProtect)
        {
            throw new CryptographicException("The fake was told to refuse.");
        }

        return [.. Mark, .. secret.Select(value => (byte)(value ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        if (RefuseToUnprotect || !protectedBytes.AsSpan().StartsWith(Mark))
        {
            throw new CryptographicException("These bytes were not protected by this user.");
        }

        return [.. protectedBytes.Skip(Mark.Length).Select(value => (byte)(value ^ 0x5A))];
    }
}
