using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Catalog;

/// <param name="BumpedAt">Set when the entry was re-uploaded at the source; overrides its date added.</param>
public sealed record CatalogEntry(
    SourceEntry Entry,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset? BumpedAt = null);

public sealed class CatalogStore
{
    private readonly string _path;
    private readonly string _datesPath;
    private readonly string _recoveryPath;
    private readonly Lock _lock = new();
    private Dictionary<string, CatalogEntry> _entries;
    private CatalogDates _dates;
    private bool _replacedByFullListing;

    /// <summary>Never throws: damaged state is set aside or skipped so Jellyfin can still start.</summary>
    public CatalogStore(string dataDirectory, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        _path = Path.Combine(dataDirectory, "catalog.json");
        _datesPath = Path.Combine(dataDirectory, "catalog-dates.json");
        _recoveryPath = Path.Combine(dataDirectory, "catalog-recovery.json");
        var (stored, readable) = ReadState<List<CatalogEntry?>>(_path, logger);
        _entries = Validate(stored, _path, logger, out var dropped);
        var storedDates = ReadState<CatalogDates>(_datesPath, logger).Value;
        _dates = storedDates ?? DateAddedRules.Infer(_entries.Values);

        // Only a stored empty list means the catalog really is empty. A missing catalog next to the dates of an
        // earlier full sync was lost, too.
        var damaged = _entries.Count == 0
            && (!readable || (stored is not { Count: 0 } && File.Exists(_path)) || (stored is null && storedDates?.BaselineAt is not null));
        if (RecoveryPending() || damaged || dropped > 0)
        {
            // Kept on disk until a full sync, so another restart doesn't take a partial catalog for the whole one.
            // Records that were dropped are titles missing from the catalog: mirroring it would delete their files.
            // The usable entries still serve lookups meanwhile.
            RequireFullSync(
                damaged ? "the catalog file could not be loaded" : dropped > 0 ? $"{dropped} catalog records were damaged" : null,
                logger);
        }
    }

    /// <summary>
    /// True while the catalog is known to be incomplete (its file was damaged or lost, or a library made from a
    /// fuller catalog exists) until a full sync replaces it, across restarts. Callers must not mirror it then (e.g.
    /// delete the library's files).
    /// </summary>
    public bool Unreadable
    {
        get
        {
            lock (_lock)
            {
                return field;
            }
        }

        private set;
    }

    /// <summary>Whether the entries came from a full listing (in this run, or at an earlier one per the stored dates).</summary>
    public bool HasFullListing
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count > 0 && (_replacedByFullListing || _dates.BaselineAt is not null);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public CatalogDates Dates
    {
        get
        {
            lock (_lock)
            {
                return _dates;
            }
        }
    }

    public IReadOnlyList<CatalogEntry> All()
    {
        lock (_lock)
        {
            return [.. _entries.Values];
        }
    }

    public SourceEntry? Get(string id)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var e) ? e.Entry : null;
        }
    }

    /// <summary>The date added to show in Jellyfin; see <see cref="DateAddedRules"/>.</summary>
    public DateTimeOffset? DateAdded(string id)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var e) ? DateAddedRules.For(e, _dates) : null;
        }
    }

    /// <param name="complete">Whether <paramref name="entries"/> come from a full listing; a partial one leaves
    /// <see cref="Unreadable"/> set, as the catalog is still missing most of its entries.</param>
    public void Replace(IEnumerable<CatalogEntry> entries, CatalogDates? dates = null, bool complete = true)
    {
        lock (_lock)
        {
            _entries = entries.ToDictionary(e => e.Entry.Id);
            JsonFile.Write(_path, _entries.Values.ToList());
            if (complete)
            {
                _replacedByFullListing = true;
                Unreadable = false;
                ClearRecovery();
            }
            if (dates is not null)
            {
                _dates = dates;
                JsonFile.Write(_datesPath, dates);
            }
        }
    }

    /// <summary>Marks the catalog as incomplete (see <see cref="Unreadable"/>) until the next full sync.</summary>
    public void RequireFullSync(string reason) => RequireFullSync(reason, NullLogger.Instance);

    private void RequireFullSync(string? reason, ILogger logger)
    {
        lock (_lock)
        {
            Unreadable = true;
            if (reason is null && RecoveryPending())
            {
                return;
            }

            try
            {
                JsonFile.Write(_recoveryPath, new CatalogRecovery(DateTimeOffset.UtcNow, reason ?? "an earlier recovery is unfinished"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                logger.LogError(ex, "Could not record that the AudioVault catalog needs a full sync");
            }
        }
    }

    private bool RecoveryPending()
    {
        try
        {
            // Any content counts: even a damaged marker means recovery isn't done.
            return File.Exists(_recoveryPath) || Directory.Exists(_recoveryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private void ClearRecovery()
    {
        try
        {
            File.Delete(_recoveryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only in memory then: after a restart the library waits for one more full sync.
        }
    }

    private sealed record CatalogRecovery(DateTimeOffset Since, string Reason);

    private static (T? Value, bool Readable) ReadState<T>(string path, ILogger logger)
        where T : class
    {
        var value = JsonFile.ReadContained<T>(path, out var status);
        if (status == StateFileStatus.Unreadable)
        {
            logger.LogError("AudioVault state file {Path} could not be read; starting without it", path);
        }

        return (value, status is StateFileStatus.Missing or StateFileStatus.Loaded);
    }

    /// <summary>
    /// Keeps the usable records of a stored catalog: entries missing an ID, title or version are dropped, and of
    /// duplicate IDs the most recently seen wins. A file that needed repair is copied aside before it gets rewritten.
    /// </summary>
    /// <param name="dropped">Records that couldn't be used at all (duplicates don't count: their title is kept).</param>
    private static Dictionary<string, CatalogEntry> Validate(List<CatalogEntry?>? stored, string path, ILogger logger, out int dropped)
    {
        var entries = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        var invalid = 0;
        dropped = 0;
        foreach (var record in stored ?? [])
        {
            if (Repair(record) is not { } entry)
            {
                invalid++;
                dropped++;
            }
            else if (entries.TryGetValue(entry.Entry.Id, out var other))
            {
                invalid++;
                if (entry.LastSeen > other.LastSeen)
                {
                    entries[entry.Entry.Id] = entry;
                }
            }
            else
            {
                entries[entry.Entry.Id] = entry;
            }
        }

        if (invalid > 0)
        {
            var copy = $"{path}.invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Copy(path, copy, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                copy = "(could not be copied)";
            }

            logger.LogWarning(
                "AudioVault catalog {Path} had {Invalid} invalid or duplicate entries, which were skipped; the original was kept as {Copy}",
                path,
                invalid,
                copy);
        }

        return entries;
    }

    private static CatalogEntry? Repair(CatalogEntry? record)
    {
        if (record?.Entry is not { } e
            || string.IsNullOrWhiteSpace(e.Id)
            || string.IsNullOrWhiteSpace(e.Title)
            || !Enum.IsDefined(e.Kind)
            || e.Version?.Language is null)
        {
            return null;
        }

        var entry = e with
        {
            RawName = e.RawName ?? e.Title,
            Version = e.Version with { Tags = e.Version.Tags?.Where(t => t is not null).ToList() ?? [] }
        };

        // A missing timestamp reads as year 1; borrow the other one rather than date the entry to then.
        var firstSeen = record.FirstSeen == default ? record.LastSeen : record.FirstSeen;
        var lastSeen = record.LastSeen == default ? firstSeen : record.LastSeen;
        return record with { Entry = entry, FirstSeen = firstSeen, LastSeen = lastSeen };
    }
}
