using System.Runtime.InteropServices;
using Beacon.Core.Search;

namespace Beacon.Core.Files;

/// <summary>One file or folder in the index.</summary>
/// <param name="Directory">Full path of the containing folder, without a trailing separator.</param>
/// <param name="Name">File or folder name, with its extension.</param>
public readonly record struct FileEntry(string Directory, string Name, bool IsDirectory)
{
    public string FullPath => Path.Combine(Directory, Name);
}

/// <summary>An entry and how well its name matched the query. Higher is better.</summary>
public readonly record struct FileMatch(FileEntry Entry, int Score);

/// <summary>
/// The names of the files and folders under the indexed folders, held in memory and searched on
/// every keystroke. Paths are compared without regard to case, as Windows does. Safe to use from
/// several threads: the background scan and the file watchers write while the UI thread searches.
/// </summary>
public sealed class FileIndex
{
    // Below this many entries one thread is quicker than sharing the work out.
    private const int ParallelThreshold = 20_000;
    private const int MaxWorkers = 8;

    private readonly Lock _gate = new();
    private readonly List<Slot> _slots = [];
    private readonly Dictionary<PathKey, int> _positions = new(PathKeyComparer.Instance);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    /// <returns>False when the path is already in the index.</returns>
    public bool Add(FileEntry entry)
    {
        lock (_gate)
        {
            return AddCore(entry);
        }
    }

    public void AddRange(IEnumerable<FileEntry> entries)
    {
        // Taken as a finished collection before the lock: a lazy sequence would otherwise do
        // its work, perhaps reading a disk, while a search is waiting.
        var ready = entries as IReadOnlyCollection<FileEntry> ?? entries.ToList();
        lock (_gate)
        {
            foreach (var entry in ready)
            {
                AddCore(entry);
            }
        }
    }

    /// <summary>
    /// Replaces everything under <paramref name="folder"/> in one step, so a search never sees the
    /// folder half empty while it is being rescanned.
    /// </summary>
    public void ReplaceUnder(string folder, IEnumerable<FileEntry> entries)
    {
        // Finished before the lock for the same reason, and so that a sequence that throws
        // part-way cannot leave the folder emptied.
        var ready = entries as IReadOnlyCollection<FileEntry> ?? entries.ToList();
        lock (_gate)
        {
            RemoveUnderCore(Normalize(folder));
            foreach (var entry in ready)
            {
                AddCore(entry);
            }
        }
    }

    /// <summary>Removes the entry for a path. If it was a folder, everything under it goes too.</summary>
    /// <returns>False when the path was not in the index.</returns>
    public bool Remove(string path)
    {
        lock (_gate)
        {
            if (!TrySplit(path, out var key) || !_positions.TryGetValue(key, out int position))
            {
                return false;
            }

            bool wasDirectory = _slots[position].Entry.IsDirectory;
            RemoveAtCore(position);
            if (wasDirectory)
            {
                RemoveUnderCore(Normalize(path));
            }

            return true;
        }
    }

    /// <summary>
    /// Gives an entry a new path. A folder takes everything under it along. If another entry
    /// already has the new path, as happens when a save replaces a file, that entry is dropped,
    /// along with everything under it if it was a folder.
    /// </summary>
    /// <returns>False when the old path was not in the index.</returns>
    public bool Rename(string oldPath, string newPath)
    {
        lock (_gate)
        {
            if (!TrySplit(oldPath, out var oldKey)
                || !TrySplit(newPath, out var newKey)
                || !_positions.TryGetValue(oldKey, out int position))
            {
                return false;
            }

            bool isDirectory = _slots[position].Entry.IsDirectory;
            if (_positions.TryGetValue(newKey, out int replaced) && replaced != position)
            {
                bool replacedDirectory = _slots[replaced].Entry.IsDirectory;
                RemoveAtCore(replaced);
                if (replacedDirectory)
                {
                    // Its contents would otherwise collide with the ones about to move in.
                    RemoveUnderCore(Normalize(newPath));
                }

                // Removal moves the last entry into the gap, and that entry may be this one.
                if (!_positions.TryGetValue(oldKey, out position))
                {
                    return true; // the old entry sat inside the folder that was just replaced
                }
            }

            RemoveAtCore(position);
            AddCore(new FileEntry(newKey.Directory, newKey.Name, isDirectory));
            if (isDirectory)
            {
                MoveChildrenCore(Normalize(oldPath), Normalize(newPath));
            }

            return true;
        }
    }

    public bool TryGet(string path, out FileEntry entry)
    {
        lock (_gate)
        {
            if (TrySplit(path, out var key) && _positions.TryGetValue(key, out int position))
            {
                entry = _slots[position].Entry;
                return true;
            }

            entry = default;
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _slots.Clear();
            _positions.Clear();
        }
    }

    /// <summary>The best <paramref name="limit"/> matches for the query against entry names, best first.</summary>
    public IReadOnlyList<FileMatch> Search(string query, int limit)
    {
        if (query.Length == 0 || limit <= 0)
        {
            return [];
        }

        string lowerQuery = string.Create(query.Length, query, static (destination, source) => FuzzyMatcher.ToLower(source, destination));
        ulong wanted = LetterMask(lowerQuery);

        var matches = new List<FileMatch>();
        lock (_gate)
        {
            // The lock is held for the whole search, so nothing changes the entries while the
            // worker threads below read them.
            int count = _slots.Count;
            if (count < ParallelThreshold)
            {
                matches = BestInRange(0, count, lowerQuery, wanted, limit);
            }
            else
            {
                // Each worker finds the best matches in its own share; the overall best are among them.
                int workers = Math.Min(Environment.ProcessorCount, MaxWorkers);
                var perWorker = new List<FileMatch>[workers];
                Parallel.For(0, workers, worker =>
                {
                    int start = (int)((long)count * worker / workers);
                    int end = (int)((long)count * (worker + 1) / workers);
                    perWorker[worker] = BestInRange(start, end, lowerQuery, wanted, limit);
                });
                foreach (var found in perWorker)
                {
                    matches.AddRange(found);
                }
            }
        }

        matches.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int byName = string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.Entry.Directory, b.Entry.Directory, StringComparison.OrdinalIgnoreCase);
        });
        if (matches.Count > limit)
        {
            matches.RemoveRange(limit, matches.Count - limit);
        }

        return matches;
    }

    // Called with the lock held. Reads entries only, so several threads can run it at once.
    private List<FileMatch> BestInRange(int start, int end, string lowerQuery, ulong wanted, int limit)
    {
        // Keeps the best matches seen so far; the weakest of them is the one a better match evicts.
        // "Weakest" uses the same order as the final sort, so which entries survive when many
        // tie on score does not depend on where they sit in the list or on how many workers ran.
        var best = new PriorityQueue<int, Rank>(RankComparer.Instance);
        ReadOnlySpan<Slot> slots = CollectionsMarshal.AsSpan(_slots);
        for (int i = start; i < end; i++)
        {
            ref readonly Slot slot = ref slots[i];
            if ((wanted & ~slot.Letters) != 0)
            {
                continue; // the name lacks a character the query needs; skip the costlier check
            }

            if (FuzzyMatcher.ScoreLowered(lowerQuery, slot.Entry.Name, slot.LowerName) is not int score)
            {
                continue;
            }

            var rank = new Rank(score, slot.Entry.Name, slot.Entry.Directory);
            if (best.Count < limit)
            {
                best.Enqueue(i, rank);
            }
            else if (best.TryPeek(out _, out var weakest) && RankComparer.Instance.Compare(rank, weakest) > 0)
            {
                best.EnqueueDequeue(i, rank);
            }
        }

        var matches = new List<FileMatch>(best.Count);
        while (best.TryDequeue(out int position, out var rank))
        {
            matches.Add(new FileMatch(slots[position].Entry, rank.Score));
        }

        return matches;
    }

    private bool AddCore(FileEntry entry)
    {
        if (!_positions.TryAdd(new PathKey(entry.Directory, entry.Name), _slots.Count))
        {
            return false;
        }

        string lowerName = LowerOf(entry.Name);
        _slots.Add(new Slot(entry, lowerName, LetterMask(lowerName)));
        return true;
    }

    // Fills the gap with the last entry, so removal does not shift everything after it.
    private void RemoveAtCore(int position)
    {
        int last = _slots.Count - 1;
        _positions.Remove(KeyOf(_slots[position].Entry));
        if (position != last)
        {
            _slots[position] = _slots[last];
            _positions[KeyOf(_slots[position].Entry)] = position;
        }

        _slots.RemoveAt(last);
    }

    private void RemoveUnderCore(string folder)
    {
        // Backwards, because removal moves the last entry into the gap and that entry was already checked.
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (FilePaths.IsUnder(_slots[i].Entry.Directory, folder))
            {
                RemoveAtCore(i);
            }
        }
    }

    private void MoveChildrenCore(string oldFolder, string newFolder)
    {
        // Entries in one folder share a single Directory string; keep it that way after the move.
        var movedFolders = new Dictionary<string, string>();
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            string directory = slot.Entry.Directory;
            if (!FilePaths.IsUnder(directory, oldFolder))
            {
                continue;
            }

            if (!movedFolders.TryGetValue(directory, out string? moved))
            {
                moved = newFolder + directory[oldFolder.Length..];
                movedFolders[directory] = moved;
            }

            var entry = slot.Entry with { Directory = moved };
            _positions.Remove(KeyOf(slot.Entry));
            _positions[KeyOf(entry)] = i;
            _slots[i] = slot with { Entry = entry };
        }
    }

    private static PathKey KeyOf(FileEntry entry) => new(entry.Directory, entry.Name);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool TrySplit(string path, out PathKey key)
    {
        path = Normalize(path);
        string? directory = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || name.Length == 0)
        {
            key = default;
            return false;
        }

        key = new PathKey(Normalize(directory), name);
        return true;
    }

    // Reuses the name itself when it has nothing to lower, which is most names.
    private static string LowerOf(string name)
    {
        foreach (char c in name)
        {
            if (char.ToLowerInvariant(c) != c)
            {
                return string.Create(name.Length, name, static (destination, source) => FuzzyMatcher.ToLower(source, destination));
            }
        }

        return name;
    }

    // One bit per letter and digit, and a shared spread of bits for everything else. A name can
    // only match a query if it has every bit the query has, which rules most names out cheaply.
    private static ulong LetterMask(ReadOnlySpan<char> lower)
    {
        ulong mask = 0;
        foreach (char c in lower)
        {
            mask |= c switch
            {
                >= 'a' and <= 'z' => 1UL << (c - 'a'),
                >= '0' and <= '9' => 1UL << (26 + c - '0'),
                _ => 1UL << (36 + c % 28),
            };
        }

        return mask;
    }

    private readonly record struct Slot(FileEntry Entry, string LowerName, ulong Letters);

    private readonly record struct PathKey(string Directory, string Name);

    private readonly record struct Rank(int Score, string Name, string Directory);

    // Orders weaker matches first: a lower score, or at the same score the name and then the
    // folder that sort later.
    private sealed class RankComparer : IComparer<Rank>
    {
        public static readonly RankComparer Instance = new();

        public int Compare(Rank x, Rank y)
        {
            int byScore = x.Score.CompareTo(y.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int byName = string.Compare(y.Name, x.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(y.Directory, x.Directory, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class PathKeyComparer : IEqualityComparer<PathKey>
    {
        public static readonly PathKeyComparer Instance = new();

        public bool Equals(PathKey x, PathKey y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(PathKey key) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Directory),
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
