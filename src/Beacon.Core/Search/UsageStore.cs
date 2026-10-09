using System.Text.Json;
using Beacon.Core.Platform;

namespace Beacon.Core.Search;

/// <summary>
/// Remembers how often and how recently each item was chosen, and turns that into a ranking
/// multiplier. Stored per machine in usage.json; never synced. The file is best effort: a
/// problem reading or writing it never stops the launcher.
/// </summary>
public sealed class UsageStore
{
    private const int MaxCountedUses = 20;
    private const double BoostPerUse = 0.05;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, UsageEntry> _entries;

    public UsageStore(string path, IClock clock)
    {
        _path = path;
        _clock = clock;
        _entries = Load(path);
    }

    public void RecordUse(string itemId)
    {
        lock (_gate)
        {
            int count = _entries.TryGetValue(itemId, out var existing) ? existing.Count + 1 : 1;
            _entries[itemId] = new UsageEntry(count, _clock.UtcNow);
            TrySave();
        }
    }

    /// <summary>The ids of the items chosen at least once whose id starts with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<string> IdsStartingWith(string prefix)
    {
        lock (_gate)
        {
            return _entries.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
    }

    /// <returns>1.0 for an item never chosen, rising to 2.0 for one chosen 20 or more times within the last day.</returns>
    public double Boost(string itemId)
    {
        UsageEntry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(itemId, out entry))
            {
                return 1.0;
            }
        }

        double recency = (_clock.UtcNow - entry.LastUsedUtc).TotalDays switch
        {
            <= 1 => 1.0,
            <= 7 => 0.7,
            <= 30 => 0.4,
            _ => 0.2,
        };
        return 1.0 + Math.Min(entry.Count, MaxCountedUses) * BoostPerUse * recency;
    }

    private static Dictionary<string, UsageEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, UsageEntry?>>(File.ReadAllText(path), JsonOptions) ?? [];
            return loaded
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable usage file only costs ranking history. Start fresh; the
            // next successful save replaces it.
            return [];
        }
    }

    private void TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Choosing an item must never fail because this file cannot be written. The use is
            // still counted in memory for this run, and the next successful save writes it.
        }
    }

    private sealed record UsageEntry(int Count, DateTimeOffset LastUsedUtc);
}
