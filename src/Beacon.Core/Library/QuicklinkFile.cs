using System.Text.Json;

namespace Beacon.Core.Library;

/// <summary>Reads the text of quicklinks.json.</summary>
public static class QuicklinkFile
{
    // The file is edited by hand, so comments and a comma after the last entry are allowed,
    // as they are in settings.json.
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="Quicklinks">The usable entries, in the order they are written.</param>
    /// <param name="Skipped">How many entries were left out because they were not usable.</param>
    public sealed record Parsed(IReadOnlyList<Quicklink> Quicklinks, int Skipped);

    /// <summary>
    /// The file holds an array of objects, each with a "name" and a "link" and optionally a
    /// "keyword". An entry without a name or a link, with a value that is not text, or with a
    /// name already used by an earlier entry (compared without regard to case) is skipped and
    /// counted; the rest are kept. A file with nothing in it holds no quicklinks. One byte order
    /// mark at the very start is ignored.
    /// </summary>
    /// <exception cref="JsonException">
    /// The text is not JSON, or its top level is not an array. This is the only exception; a bad
    /// entry never throws.
    /// </exception>
    public static Parsed Parse(string json)
    {
        // The mark is not part of the text, and it is not white space either, so it would
        // otherwise count as a first character that is not JSON.
        if (json.StartsWith('\uFEFF'))
        {
            json = json[1..];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new Parsed([], 0);
        }

        using var document = ParseDocument(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The top level of quicklinks.json is not an array.");
        }

        var quicklinks = new List<Quicklink>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (TryRead(entry) is { } quicklink && names.Add(quicklink.Name))
            {
                quicklinks.Add(quicklink);
            }
            else
            {
                skipped++;
            }
        }

        return new Parsed(quicklinks, skipped);
    }

    /// <summary>
    /// The caller is told about a file that cannot be read in one way only, with a JsonException,
    /// so an argument exception from the parser is passed on as one.
    /// </summary>
    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, Options);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The text of quicklinks.json cannot be read as JSON.", exception);
        }
    }

    private static Quicklink? TryRead(JsonElement entry)
    {
        try
        {
            return ReadEntry(entry);
        }
        catch (InvalidOperationException)
        {
            // Half of a surrogate pair written as an escape is valid JSON, but the text it makes
            // cannot be read, and reading it throws. The entry is a bad entry like any other.
            return null;
        }
    }

    private static Quicklink? ReadEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? name = null;
        string? link = null;
        string? keyword = null;
        foreach (var property in entry.EnumerateObject())
        {
            // Key names are matched without regard to case, as in settings.json. Other keys are ignored.
            if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryText(property.Value, out name))
                {
                    return null;
                }
            }
            else if (property.Name.Equals("link", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryText(property.Value, out link))
                {
                    return null;
                }
            }
            else if (property.Name.Equals("keyword", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryText(property.Value, out keyword))
                {
                    return null;
                }
            }
        }

        return name is null || link is null ? null : new Quicklink(name, link, keyword);
    }

    /// <returns>
    /// False when the value is neither text nor null. <paramref name="text"/> is the trimmed
    /// text, or null when there is none.
    /// </returns>
    private static bool TryText(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string trimmed = value.GetString()!.Trim();
        text = trimmed.Length > 0 ? trimmed : null;
        return true;
    }
}
