using System.Globalization;

namespace AudioVault.Core.Catalog;

/// <param name="BaselineAt">When the first full sync finished; entries first seen by then get dates in ID order.</param>
/// <param name="BaselineMaxId">Highest numeric ID at the baseline; it gets <paramref name="BaselineAt"/> itself.</param>
/// <param name="RecentSyncAt">When <paramref name="RecentIds"/> was taken.</param>
/// <param name="RecentIds">IDs in the source's Recent tables at the last recent sync; null before the first one.</param>
public sealed record CatalogDates(
    DateTimeOffset? BaselineAt,
    long BaselineMaxId,
    DateTimeOffset? RecentSyncAt,
    IReadOnlyList<string>? RecentIds)
{
    public static readonly CatalogDates Empty = new(null, 0, null, null);
}

/// <summary>
/// The "date added" shown in Jellyfin, chosen so Recently Added follows the source's upload order.
/// Source IDs increase with upload time.
/// </summary>
public static class DateAddedRules
{
    public static readonly TimeSpan BaselineStep = TimeSpan.FromHours(1);

    // A full sync stamps every entry with one time, so this only needs to absorb clock noise.
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromMinutes(10);
    private const long MaxSteps = 10_000_000;

    public static DateTimeOffset For(CatalogEntry entry, CatalogDates dates)
    {
        if (entry.BumpedAt is { } bumped)
        {
            return bumped;
        }

        if (dates.BaselineAt is { } baseline && entry.FirstSeen <= baseline && NumericId(entry.Entry.Id) is { } id)
        {
            return baseline - (BaselineStep * Math.Clamp(dates.BaselineMaxId - id, 0, MaxSteps));
        }

        return entry.FirstSeen;
    }

    public static long? NumericId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static long MaxId(IEnumerable<CatalogEntry> entries) =>
        entries.Select(e => NumericId(e.Entry.Id) ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>For catalogs saved before dates were tracked: the earliest batch of FirstSeen times was the first full sync.</summary>
    public static CatalogDates Infer(IReadOnlyCollection<CatalogEntry> entries)
    {
        // Entries without a recorded time can't tell when the first sync ran.
        entries = [.. entries.Where(e => e.FirstSeen != default)];
        if (entries.Count == 0)
        {
            return CatalogDates.Empty;
        }

        var first = entries.Min(e => e.FirstSeen);
        var baseline = entries.Where(e => e.FirstSeen <= first + BaselineWindow).ToList();
        return new CatalogDates(baseline.Max(e => e.FirstSeen), MaxId(baseline), null, null);
    }

    /// <summary>
    /// Re-upload detection: an old title re-uploaded on the source reappears in the Recent tables under its old ID.
    /// An entry is bumped when it shows up in the Recent tables, was already known at the previous recent sync but not
    /// in that snapshot, and its ID is below the highest known ID (so it isn't simply the newest upload).
    /// The first snapshot never bumps anything: those are the latest uploads, already in ID order.
    /// </summary>
    public static bool IsReupload(CatalogEntry known, CatalogDates dates, long maxKnownId) =>
        dates is { RecentIds: { } previous, RecentSyncAt: { } previousAt }
        && known.FirstSeen <= previousAt
        && !previous.Contains(known.Entry.Id)
        && NumericId(known.Entry.Id) is { } id
        && id < maxKnownId;
}
