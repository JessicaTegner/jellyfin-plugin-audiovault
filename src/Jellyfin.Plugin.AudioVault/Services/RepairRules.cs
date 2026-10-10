namespace Jellyfin.Plugin.AudioVault.Services;

public enum RepairKind
{
    Movie,
    Episode,

    /// <summary>A series or season.</summary>
    ShowFolder,
}

/// <summary>
/// Which items Jellyfin never finished processing. Jellyfin sets an item's PresentationUniqueKey and DateLastRefreshed
/// in its first metadata refresh; a library scan that is interrupted leaves items without them, and later scans don't
/// refresh them again. Search, Recently Added and the Movies tab group items by that key, so all items without one
/// collapse into a single hidden result.
/// </summary>
public static class RepairRules
{
    /// <summary>Key of Jellyfin's "Scan Media Library" task.</summary>
    public const string LibraryScanKey = "RefreshLibrary";

    public static bool NeedsRefresh(DateTime dateLastRefreshed, string? presentationUniqueKey) =>
        dateLastRefreshed == default || string.IsNullOrEmpty(presentationUniqueKey);

    public static bool IsSearchable(string? presentationUniqueKey) => !string.IsNullOrEmpty(presentationUniqueKey);

    /// <summary>Whether a path lies inside a library folder (not the folder itself).</summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var trimmed = root.TrimEnd('/', '\\');
        return path.Length > trimmed.Length + 1
            && path.StartsWith(trimmed, StringComparison.Ordinal)
            && path[trimmed.Length] is '/' or '\\';
    }

    /// <summary>Automatic passes wait while Jellyfin's library scan runs; it refreshes new items itself.</summary>
    public static bool LibraryScanRunning(IEnumerable<(string Key, bool Running)> tasks) =>
        tasks.Any(t => t.Running && string.Equals(t.Key, LibraryScanKey, StringComparison.Ordinal));
}

/// <summary>
/// Items queued for a refresh recently, so the next pass doesn't queue them again while Jellyfin is still working
/// through its refresh queue, or every pass when a refresh keeps failing.
/// </summary>
public sealed class RepairQueue(TimeSpan requeueAfter)
{
    private readonly Dictionary<Guid, DateTimeOffset> _queuedAt = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queuedAt.Count;
            }
        }
    }

    /// <summary>Picks up to <paramref name="limit"/> candidates not queued recently and records them as queued now.</summary>
    /// <param name="includeRecent">Take recently queued candidates too (a pass someone started by hand).</param>
    public IReadOnlyList<Guid> Take(IEnumerable<Guid> candidates, DateTimeOffset now, int? limit = null, bool includeRecent = false)
    {
        var taken = new List<Guid>();
        var seen = new HashSet<Guid>();
        lock (_lock)
        {
            foreach (var expired in _queuedAt.Where(q => now - q.Value >= requeueAfter).Select(q => q.Key).ToList())
            {
                _queuedAt.Remove(expired);
            }

            foreach (var id in candidates)
            {
                if (taken.Count >= limit)
                {
                    break;
                }

                if (!seen.Add(id) || (!includeRecent && _queuedAt.ContainsKey(id)))
                {
                    continue;
                }

                _queuedAt[id] = now;
                taken.Add(id);
            }
        }

        return taken;
    }
}

/// <summary>
/// How many AudioVault items are waiting for Jellyfin to fetch their details (and so can't be found yet), for the
/// status page. Filled by each repair pass and kept current between passes from Jellyfin's item events.
/// </summary>
public sealed class RepairCounts
{
    private readonly Dictionary<Guid, RepairKind> _waiting = [];
    private readonly Lock _lock = new();
    private bool _counted;

    /// <summary>Waiting items of a kind, or null before the first pass has counted them.</summary>
    public int? Waiting(RepairKind kind)
    {
        lock (_lock)
        {
            return _counted ? _waiting.Values.Count(k => k == kind) : null;
        }
    }

    /// <summary>The result of a complete pass replaces everything known so far.</summary>
    public void Replace(IEnumerable<(Guid Id, RepairKind Kind)> waiting)
    {
        lock (_lock)
        {
            _waiting.Clear();
            foreach (var (id, kind) in waiting)
            {
                _waiting[id] = kind;
            }

            _counted = true;
        }
    }

    public void Update(Guid id, RepairKind kind, bool waiting)
    {
        lock (_lock)
        {
            if (waiting)
            {
                _waiting[id] = kind;
            }
            else
            {
                _waiting.Remove(id);
            }
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _waiting.Remove(id);
        }
    }
}
