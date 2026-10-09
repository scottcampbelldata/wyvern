using System.Globalization;
using System.Text.Json;

namespace Beacon.Core.Update;

/// <summary>
/// Where this copy of Beacon looks for a newer one, and the key a newer one has to be signed
/// with. Both are built into the program when it is built, from a file that is not in the
/// repository: a Beacon built without that file has no channel and never looks.
/// </summary>
/// <remarks>
/// The address is the owner's own server on his private network. Only an address inside that
/// network is taken (see <see cref="TryAddress"/>), written as numbers, so that no name has
/// to be looked up and nothing can point Beacon at a machine outside it. What is fetched from
/// there is trusted for nothing: an update is only ever installed when it is signed with the
/// key built in here (see <see cref="UpdateManifest"/>).
/// </remarks>
/// <param name="Address">The folder the update files are in, ending in a slash, such as "http://100.64.0.10:8795/".</param>
/// <param name="PublicKey">The public half of the signing key: the two coordinates of a point on the P-256 curve, 32 bytes each.</param>
public sealed record UpdateChannel(string Address, byte[] PublicKey)
{
    /// <summary>How many bytes a public key has.</summary>
    public const int PublicKeyLength = 64;

    /// <summary>The longest text that is read as a channel.</summary>
    public const int MaxLength = 2000;

    /// <summary>The type's name only: the address is the owner's server.</summary>
    public override string ToString() => nameof(UpdateChannel);

    /// <summary>The address of one of the update files. The name is one Beacon made itself: letters, digits, dots and hyphens.</summary>
    public string UrlOf(string fileName)
    {
        if (!IsPlainName(fileName))
        {
            throw new ArgumentException("An update file's name holds only letters, digits, dots and hyphens.", nameof(fileName));
        }

        return Address + fileName;
    }

    /// <summary>
    /// Reads the channel that was built in: a JSON object with "url" and "publicKey". Null
    /// when there is none, or when it cannot be used: no address inside the private network,
    /// or no key of the right length.
    /// </summary>
    public static UpdateChannel? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("url", out var url)
                || url.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("publicKey", out var key)
                || key.ValueKind != JsonValueKind.String
                || !TryAddress(url.GetString(), out string address))
            {
                return null;
            }

            byte[] publicKey = Convert.FromBase64String(key.GetString()!);
            return publicKey.Length == PublicKeyLength ? new UpdateChannel(address, publicKey) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for an address updates may be fetched from, and gives it ending in a slash. It
    /// must be plain http to a machine written as four numbers inside 100.64.0.0 to
    /// 100.127.255.255, the range the owner's private network gives its machines, with an
    /// optional port and folder: no name, no user, no question mark, nothing else.
    /// </summary>
    /// <remarks>
    /// Plain http is right here: the private network encrypts what goes between its machines,
    /// and what arrives is checked against a signature whatever it came through.
    /// </remarks>
    public static bool TryAddress(string? text, out string address)
    {
        address = "";
        if (text is null || text.Length > 200 || !text.StartsWith("http://", StringComparison.Ordinal))
        {
            return false;
        }

        string rest = text["http://".Length..];
        int slash = rest.IndexOf('/');
        string machine = slash < 0 ? rest : rest[..slash];
        string folder = slash < 0 ? "/" : rest[slash..];

        string numbers = machine;
        int colon = machine.IndexOf(':');
        if (colon >= 0)
        {
            numbers = machine[..colon];
            string port = machine[(colon + 1)..];
            if (port.Length is 0 or > 5
                || !port.All(char.IsAsciiDigit)
                || int.Parse(port, CultureInfo.InvariantCulture) is 0 or > 65535)
            {
                return false;
            }
        }

        string[] parts = numbers.Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        int[] bytes = [.. parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture))];
        if (bytes.Any(value => value > 255) || bytes[0] != 100 || bytes[1] is < 64 or > 127)
        {
            return false;
        }

        // The folder: plain names between slashes, nothing that climbs or asks.
        if (folder.Split('/').Any(name => name.Length > 0 && !IsPlainName(name)) || folder.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        address = "http://" + machine + (folder.EndsWith('/') ? folder : folder + "/");
        return true;
    }

    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 100
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && !name.StartsWith('.')
        && !name.StartsWith('-');
}
