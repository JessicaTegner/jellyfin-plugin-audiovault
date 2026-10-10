using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Library;

public enum EpisodeCountOrigin
{
    Metadata,
    Archive
}

/// <param name="Episodes">The archive's own episode numbers, when known; null means 1..<paramref name="Count"/>.</param>
public sealed record EpisodeCount(int Count, EpisodeCountOrigin Origin, IReadOnlyList<int>? Episodes = null)
{
    /// <summary>The episode numbers to request from the media layer, ascending.</summary>
    public IReadOnlyList<int> EpisodeNumbers() =>
        Episodes is { Count: > 0 } known ? [.. known.Where(n => n > 0).Distinct().Order()] : [.. Enumerable.Range(1, Count)];

    public bool Equals(EpisodeCount? other) =>
        other is not null
        && Count == other.Count
        && Origin == other.Origin
        && (Episodes ?? []).SequenceEqual(other.Episodes ?? []);

    public override int GetHashCode() => HashCode.Combine(Count, Origin, Episodes?.Count ?? 0);
}

/// <summary>Known episode counts per season entry. Counts read from a downloaded archive win over metadata lookups.</summary>
public sealed class EpisodeCountStore
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, EpisodeCount> _counts;

    /// <summary>Never throws: unreadable or invalid counts are skipped and get relearned.</summary>
    public EpisodeCountStore(string dataDirectory, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        _path = Path.Combine(dataDirectory, "episode-counts.json");
        var stored = JsonFile.ReadContained<Dictionary<string, EpisodeCount?>>(_path, out var status);
        if (status == StateFileStatus.Unreadable)
        {
            logger.LogError("AudioVault state file {Path} could not be read; starting without it", _path);
        }

        _counts = [];
        foreach (var (id, count) in stored ?? [])
        {
            if (IsValid(count))
            {
                _counts[id] = count;
            }
        }

        if (stored is not null && _counts.Count < stored.Count)
        {
            logger.LogWarning("Skipped {Count} invalid records in {Path}", stored.Count - _counts.Count, _path);
        }
    }

    /// <summary>Bounds the episode list a single season part may produce.</summary>
    public const int MaxEpisodes = 10_000;

    private static bool IsValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] EpisodeCount? count) =>
        count is { Count: >= 0 and <= MaxEpisodes }
        && Enum.IsDefined(count.Origin)
        && (count.Episodes is null || count.Episodes.Count <= MaxEpisodes);

    public EpisodeCount? Get(string entryId)
    {
        lock (_lock)
        {
            return _counts.GetValueOrDefault(entryId);
        }
    }

    /// <summary>
    /// Records a count; true when it changed. Saved before it takes effect: if saving throws, nothing changes, so
    /// setting the same count again retries.
    /// </summary>
    public bool Set(string entryId, EpisodeCount count)
    {
        lock (_lock)
        {
            if (_counts.TryGetValue(entryId, out var existing)
                && (existing == count || (existing.Origin == EpisodeCountOrigin.Archive && count.Origin != EpisodeCountOrigin.Archive)))
            {
                return false;
            }

            JsonFile.Write(_path, new Dictionary<string, EpisodeCount>(_counts) { [entryId] = count });
            _counts[entryId] = count;
            return true;
        }
    }
}
