using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Beacon.Core.Update;

/// <summary>A newer Beacon that the update server offers: its version and the one file that installs it.</summary>
/// <param name="FileName">The setup program's name, always "BeaconSetup-" and the version and ".exe".</param>
/// <param name="Size">How many bytes the setup program has.</param>
/// <param name="Sha256">The SHA-256 of the setup program, as 64 small hex digits.</param>
public sealed record UpdateOffer(Version Version, string FileName, long Size, string Sha256)
{
    /// <summary>The version as three numbers, such as "1.8.0".</summary>
    public string VersionText => Version.ToString(3);
}

/// <summary>What a look at the update server came to.</summary>
public enum UpdateVerdict
{
    /// <summary>The server offers a newer version, properly signed.</summary>
    Newer,

    /// <summary>What the server offers is this version or an older one.</summary>
    UpToDate,

    /// <summary>The server could not be reached, or did not have the files.</summary>
    Unreachable,

    /// <summary>What the server gave is not signed with the key built into this Beacon. Nothing of it is used.</summary>
    NotSigned,

    /// <summary>It is signed, and still not something Beacon can use: a mistake in what was published.</summary>
    Unreadable,
}

/// <summary>What a look at the update server came to, with the offer when there is one.</summary>
public sealed record UpdateCheck(UpdateVerdict Verdict, UpdateOffer? Offer = null)
{
    public static UpdateCheck Unreachable { get; } = new(UpdateVerdict.Unreachable);
}

/// <summary>
/// Reads what the update server says is the newest Beacon, and trusts none of it until its
/// signature has been checked against the key built into this Beacon.
/// </summary>
/// <remarks>
/// The server holds three files: "latest.json", which names the newest version and the size
/// and SHA-256 of its setup program; "latest.json.sig", a signature of exactly those bytes
/// (ECDSA over P-256 with SHA-256, the two numbers one after the other, as base64); and the
/// setup program itself. The signing key never leaves the machine Beacon is built on. So a
/// file that was changed on the server, or on its way, is refused: the manifest because its
/// signature no longer fits, the setup program because its SHA-256 is no longer the one the
/// signed manifest names. An older manifest played back is of no use either: only a version
/// higher than the one running is ever offered.
/// </remarks>
public static class UpdateManifest
{
    /// <summary>The name of the file that says what the newest version is.</summary>
    public const string FileName = "latest.json";

    /// <summary>The name of the file that holds the signature of <see cref="FileName"/>.</summary>
    public const string SignatureFileName = "latest.json.sig";

    /// <summary>The largest manifest that is read.</summary>
    public const int MaxBytes = 4096;

    /// <summary>The largest signature file that is read.</summary>
    public const int MaxSignatureBytes = 512;

    /// <summary>The largest setup program that is fetched.</summary>
    public const long MaxInstallerBytes = 300L * 1024 * 1024;

    /// <summary>What a setup program's name starts with.</summary>
    public const string InstallerPrefix = "BeaconSetup-";

    /// <summary>The name of the setup program of a version.</summary>
    public static string InstallerName(Version version) => InstallerPrefix + version.ToString(3) + ".exe";

    /// <summary>
    /// Judges what the server gave. The signature is checked first, over the bytes exactly as
    /// they came, and nothing is read out of a manifest whose signature does not fit.
    /// </summary>
    /// <param name="manifest">The bytes of "latest.json".</param>
    /// <param name="signature">The bytes of "latest.json.sig": base64 text, white space round it allowed.</param>
    /// <param name="publicKey">The key built into this Beacon.</param>
    /// <param name="running">The version that is running.</param>
    public static UpdateCheck Read(byte[] manifest, byte[] signature, byte[] publicKey, Version running)
    {
        if (manifest.Length is 0 or > MaxBytes
            || signature.Length is 0 or > MaxSignatureBytes
            || !TryDecode(signature, out byte[] signed)
            || !Verify(manifest, signed, publicKey))
        {
            return new UpdateCheck(UpdateVerdict.NotSigned);
        }

        if (Parse(manifest) is not { } offer)
        {
            return new UpdateCheck(UpdateVerdict.Unreadable);
        }

        return offer.Version > ThreeNumbers(running)
            ? new UpdateCheck(UpdateVerdict.Newer, offer)
            : new UpdateCheck(UpdateVerdict.UpToDate);
    }

    /// <summary>True when the signature is that of the data under the public key. Never throws.</summary>
    public static bool Verify(byte[] data, byte[] signature, byte[] publicKey)
    {
        if (publicKey.Length != UpdateChannel.PublicKeyLength || signature.Length != 64)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = publicKey[..32], Y = publicKey[32..] },
            });
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or PlatformNotSupportedException)
        {
            // A key that is no point on the curve verifies nothing.
            return false;
        }
    }

    /// <summary>
    /// True when the file is the setup program the offer names: the same number of bytes and
    /// the same SHA-256. Checked before a downloaded file is kept, and again right before it
    /// is run. Never throws.
    /// </summary>
    public static bool FileMatches(string path, UpdateOffer offer)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != offer.Size)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(offer.Sha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A version as its first three numbers: 1.8.0.0 and 1.8.0 are the same version.</summary>
    public static Version ThreeNumbers(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    // The manifest: {"version":"1.8.0","file":"BeaconSetup-1.8.0.exe","size":8391500,"sha256":"..."}.
    // Every part has to be there and in its plain form, and the file has to be the one
    // name the version allows, so that a manifest can never name another file.
    private static UpdateOffer? Parse(byte[] manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var versionText) || versionText.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("sha256", out var sha256) || sha256.ValueKind != JsonValueKind.String
                || !TryVersion(versionText.GetString()!, out var version)
                || file.GetString() != InstallerName(version)
                || !size.TryGetInt64(out long bytes) || bytes is <= 0 or > MaxInstallerBytes)
            {
                return null;
            }

            string hash = sha256.GetString()!;
            return hash.Length == 64 && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                ? new UpdateOffer(version, InstallerName(version), bytes, hash)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Three numbers of at most four digits each, with dots between them and nothing else.
    private static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        string[] parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length is 0 or > 4 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        version = new Version(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
        return true;
    }

    private static bool TryDecode(byte[] base64Text, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(base64Text).Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
