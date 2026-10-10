using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging;

namespace AudioVault.Core.Catalog;

public sealed record SyncResult(int Added, int Updated, int Removed, bool RemovalsSkipped, int Total)
{
    /// <summary>Known entries detected as re-uploaded; their date added moved to now.</summary>
    public IReadOnlyList<string> Bumped { get; init; } = [];
}

public sealed class CatalogSyncService(
    IContentSource source,
    CatalogStore store,
    ILogger<CatalogSyncService> logger,
    TimeProvider? time = null)
{
    public const double MaxRemovalFraction = 0.2;

    /// <summary>More removals than this in one full sync must be confirmed by the next full sync.</summary>
    public const int MaxUnconfirmedRemovals = 50;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Full and recent syncs each read, merge and replace the whole store; running them together loses updates.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string> _unconfirmedRemovals = [];

    public async Task<SyncResult> SyncFullAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fresh = new Dictionary<string, SourceEntry>();
            await foreach (var entry in source.ListAllAsync(progress, ct).ConfigureAwait(false))
            {
                fresh[entry.Id] = entry;
            }

            if (fresh.Count == 0)
            {
                throw new SourceFormatChangedException($"Source '{source.Id}' returned an empty catalog; refusing to sync.");
            }

            return Merge(fresh.Values, recent: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncResult> SyncRecentAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fresh = new List<SourceEntry>();
            await foreach (var entry in source.ListRecentAsync(ct).ConfigureAwait(false))
            {
                fresh.Add(entry);
            }

            return Merge(fresh, recent: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SyncResult Merge(IReadOnlyCollection<SourceEntry> fresh, bool recent)
    {
        var now = _time.GetUtcNow();
        var existing = store.All().ToDictionary(e => e.Entry.Id);
        var result = new Dictionary<string, CatalogEntry>(existing);
        var seen = new HashSet<string>();
        var dates = store.Dates;
        var maxKnownId = DateAddedRules.MaxId(existing.Values);
        var bumped = new List<string>();
        int added = 0, updated = 0;
        bool allowRemovals = !recent;

        foreach (var entry in fresh)
        {
            seen.Add(entry.Id);
            if (existing.TryGetValue(entry.Id, out var old))
            {
                if (!SameEntry(old.Entry, entry))
                {
                    updated++;
                }

                var merged = old with { Entry = entry, LastSeen = now };
                if (recent && DateAddedRules.IsReupload(old, dates, maxKnownId))
                {
                    merged = merged with { BumpedAt = now };
                    bumped.Add(entry.Id);
                }

                result[entry.Id] = merged;
            }
            else
            {
                added++;
                result[entry.Id] = new CatalogEntry(entry, now, now);
            }
        }

        int removed = 0;
        bool skipped = false;
        if (allowRemovals)
        {
            var missing = existing.Keys.Where(id => !seen.Contains(id)).ToList();
            var tooMany = existing.Count > 0 && missing.Count > existing.Count * MaxRemovalFraction;
            if (tooMany)
            {
                skipped = true;
                logger.LogWarning(
                    "Sync would remove {Missing} of {Total} entries; skipping removals as a safety measure",
                    missing.Count,
                    existing.Count);
            }
            else
            {
                // A listing page that silently came back empty drops a block of entries; that rarely repeats exactly,
                // so a large batch is only removed once the previous full sync was missing the same entries too.
                var confirmed = missing.Count > MaxUnconfirmedRemovals
                    ? missing.Where(_unconfirmedRemovals.Contains).ToList()
                    : missing;
                if (confirmed.Count < missing.Count)
                {
                    skipped = true;
                    logger.LogWarning(
                        "Sync is missing {Missing} known entries; removing only {Confirmed} confirmed by the previous sync",
                        missing.Count,
                        confirmed.Count);
                }

                foreach (var id in confirmed)
                {
                    result.Remove(id);
                }

                removed = confirmed.Count;
            }

            _unconfirmedRemovals = tooMany ? [] : [.. missing];
        }

        if (!recent && dates.BaselineAt is null)
        {
            dates = dates with { BaselineAt = now, BaselineMaxId = DateAddedRules.MaxId(result.Values) };
        }

        // An empty Recent list is more likely a parsing hiccup than reality; keeping the old snapshot avoids false bumps next time.
        if (recent && fresh.Count > 0)
        {
            dates = dates with { RecentSyncAt = now, RecentIds = [.. seen] };
        }

        store.Replace(result.Values, dates, complete: !recent);
        logger.LogInformation(
            "Catalog sync: {Added} added, {Updated} updated, {Removed} removed, {Bumped} re-uploaded, {Total} total",
            added,
            updated,
            removed,
            bumped.Count,
            result.Count);
        return new SyncResult(added, updated, removed, skipped, result.Count) { Bumped = bumped };
    }

    // Records with list members compare by reference, so compare tags by value.
    private static bool SameEntry(SourceEntry a, SourceEntry b) =>
        a with { Version = b.Version } == b
        && a.Version.Language == b.Version.Language
        && a.Version.IsTts == b.Version.IsTts
        && a.Version.Tags.SequenceEqual(b.Version.Tags);
}
