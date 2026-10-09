using System.Text.Json;

namespace Beacon.Core.Search;

/// <summary>How giving an item an alias went.</summary>
public enum AliasChange
{
    /// <summary>The item has the alias now.</summary>
    Set,

    /// <summary>Nothing was typed, and the alias the item had is gone.</summary>
    Removed,

    /// <summary>Nothing was typed, and the item had no alias.</summary>
    None,

    /// <summary>Another item has that alias.</summary>
    Taken,

    /// <summary>An alias is one word.</summary>
    NotOneWord,

    /// <summary>It is longer than <see cref="ItemMarks.MaxAliasLength"/>.</summary>
    TooLong,
}

/// <summary>
/// What the owner has marked items with: a place among the favorites, which are listed first
/// when the launcher opens, and an alias, a short word that brings its item to the top when
/// it is typed. Both are kept by the item's id, per machine in favorites.json, and never
/// synced. The file is best effort, as usage.json is: a problem reading or writing it never
/// stops the launcher. Use on one thread (the UI thread).
/// </summary>
public sealed class ItemMarks
{
    /// <summary>How many favorites there can be.</summary>
    public const int MaxFavorites = 50;

    /// <summary>The longest alias.</summary>
    public const int MaxAliasLength = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly List<string> _favorites = [];
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public ItemMarks(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>The ids of the favorites, in the order they are listed in.</summary>
    public IReadOnlyList<string> Favorites => _favorites;

    public bool IsFavorite(string itemId) => _favorites.Contains(itemId, StringComparer.Ordinal);

    /// <summary>Makes an item a favorite, the last of them, or takes it from among them.</summary>
    /// <returns>True when it is a favorite now, false when it no longer is, and null when there was no room for one more.</returns>
    public bool? ToggleFavorite(string itemId)
    {
        if (_favorites.Remove(itemId))
        {
            TrySave();
            return false;
        }

        if (_favorites.Count >= MaxFavorites)
        {
            return null;
        }

        _favorites.Add(itemId);
        TrySave();
        return true;
    }

    /// <summary>Moves a favorite up the list (a negative number) or down it, as far as there is room.</summary>
    /// <returns>False when it is not a favorite, or stands at that end already.</returns>
    public bool MoveFavorite(string itemId, int by)
    {
        int from = _favorites.FindIndex(id => id == itemId);
        if (from < 0)
        {
            return false;
        }

        int to = Math.Clamp(from + by, 0, _favorites.Count - 1);
        if (to == from)
        {
            return false;
        }

        _favorites.RemoveAt(from);
        _favorites.Insert(to, itemId);
        TrySave();
        return true;
    }

    /// <summary>The alias of an item, or null when it has none.</summary>
    public string? AliasOf(string itemId) => _aliases.GetValueOrDefault(itemId);

    /// <summary>The id of the item that has this alias, whatever the case of its letters, or null.</summary>
    public string? IdOf(string alias)
    {
        alias = alias.Trim();
        if (alias.Length == 0 || alias.Length > MaxAliasLength)
        {
            return null;
        }

        foreach (var (id, known) in _aliases)
        {
            if (known.Equals(alias, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Gives an item an alias in place of the one it had. Nothing but spaces takes its alias
    /// away. An alias is one word, and no two items have the same one.
    /// </summary>
    public AliasChange SetAlias(string itemId, string alias)
    {
        alias = alias.Trim();
        if (alias.Length == 0)
        {
            if (!_aliases.Remove(itemId))
            {
                return AliasChange.None;
            }

            TrySave();
            return AliasChange.Removed;
        }

        if (alias.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return AliasChange.NotOneWord;
        }

        if (alias.Length > MaxAliasLength)
        {
            return AliasChange.TooLong;
        }

        if (IdOf(alias) is { } holder && holder != itemId)
        {
            return AliasChange.Taken;
        }

        _aliases[itemId] = alias;
        TrySave();
        return AliasChange.Set;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), JsonOptions);
            foreach (string? id in stored?.Favorites ?? [])
            {
                if (!string.IsNullOrEmpty(id) && _favorites.Count < MaxFavorites && !IsFavorite(id))
                {
                    _favorites.Add(id);
                }
            }

            // The file can be edited by hand: what is not an alias, or is one a second time, is left out.
            foreach (var (id, alias) in stored?.Aliases ?? [])
            {
                string word = alias?.Trim() ?? "";
                if (id.Length > 0
                    && word.Length is > 0 and <= MaxAliasLength
                    && !word.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                    && IdOf(word) is null)
                {
                    _aliases[id] = word;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read costs the marks, not the launcher. It starts without
            // any; the next mark that is made writes the file anew.
            _favorites.Clear();
            _aliases.Clear();
        }
    }

    private void TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stored { Favorites = [.. _favorites], Aliases = _aliases.ToDictionary(pair => pair.Key, pair => (string?)pair.Value) }, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Marking an item must never fail because this file cannot be written. The mark
            // holds for this run, and the next successful save writes it.
        }
    }

    private sealed class Stored
    {
        public List<string?>? Favorites { get; set; }

        public Dictionary<string, string?>? Aliases { get; set; }
    }
}
