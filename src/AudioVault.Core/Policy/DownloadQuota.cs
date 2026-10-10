namespace AudioVault.Core.Policy;

public sealed record DownloadRecord(string EntryId, DateTimeOffset At, string Account = "");

/// <summary>
/// Rolling 24h download counter per source account, persisted so restarts don't reset it. It only sees this
/// server's downloads. If the ledger can't be read or holds anything invalid, every account is blocked for a full
/// window (fail closed).
/// </summary>
public sealed class DownloadQuota
{
    /// <summary>Account value of a record that blocks every account (written when the ledger was unreadable).</summary>
    public const string AllAccounts = "*";

    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Clock difference tolerated for records dated in the future.</summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<DownloadRecord> _records;
    private string? _problem;

    public DownloadQuota(string dataDirectory, TimeProvider? time = null)
    {
        _path = Path.Combine(dataDirectory, "downloads.json");
        _time = time ?? TimeProvider.System;
        _records = [];
        var now = _time.GetUtcNow();
        var blockedFrom = now;
        Exception? damage = null;
        try
        {
            // A damaged file stays in place until its blocking replacement is on disk, so a failed or interrupted
            // recovery is simply repeated at the next start.
            var records = JsonFile.ReadContained<List<DownloadRecord?>>(_path, out var status, out var parseError, setAside: false);
            switch (status)
            {
                case StateFileStatus.Missing when RecentlySetAside(now) is { } at:
                    // An earlier recovery moved the history aside but never saved the block that replaces it.
                    blockedFrom = at;
                    break;
                case StateFileStatus.Missing:
                    return;
                case StateFileStatus.Corrupt:
                    damage = parseError;
                    break;
                case StateFileStatus.Loaded when Invalid(records, now) is { } problem:
                    // Valid JSON that can't be trusted to hold every download (e.g. null, [null], missing fields).
                    damage = new InvalidDataException($"Download history is invalid: {problem}");
                    _records.AddRange(records?.Where(r => Invalid(r, now) is null).Select(r => r!) ?? []);
                    break;
                case StateFileStatus.Loaded:
                    _records.AddRange(records!.Select(r => r!));
                    return;
            }
        }
        catch (Exception)
        {
            // Never stop the plugin from starting; block instead.
        }

        // Unknown how much was used: assume everything until a full window has passed.
        _records.Add(new DownloadRecord("(download history unreadable)", blockedFrom, AllAccounts));
        if (damage is not null)
        {
            JsonFile.CopyAside(_path, damage, now);
        }

        Save();
    }

    /// <summary>
    /// Why the download history couldn't be saved, or null. A block for a damaged history that couldn't be saved
    /// still holds after a restart: the damaged file (or its set-aside copy) is found and blocks again.
    /// </summary>
    public string? Problem
    {
        get
        {
            lock (_lock)
            {
                return _problem;
            }
        }
    }

    public int Used(string account)
    {
        lock (_lock)
        {
            Prune();
            return _records.Count(r => r.Account == account);
        }
    }

    /// <summary>Set while every account is blocked because the download history was unreadable.</summary>
    public DateTimeOffset? BlockedUntil
    {
        get
        {
            lock (_lock)
            {
                Prune();
                return Blocked();
            }
        }
    }

    public int UsedByAll
    {
        get
        {
            lock (_lock)
            {
                Prune();
                return _records.Count(r => r.Account != AllAccounts);
            }
        }
    }

    /// <summary>When the account can download again, or null if it can now.</summary>
    public DateTimeOffset? NextAvailableAt(string account, int limit)
    {
        lock (_lock)
        {
            Prune();
            var mine = _records.Where(r => r.Account == account).OrderBy(r => r.At).ToList();
            DateTimeOffset? own = mine.Count < limit || mine.Count == 0 ? null : mine[mine.Count - Math.Max(1, limit)].At + Window;
            return Blocked() is { } blocked && (own is null || blocked > own) ? blocked : own;
        }
    }

    public bool TryAcquire(string entryId, string account, int limit)
    {
        lock (_lock)
        {
            Prune();
            if (Blocked() is not null || _records.Count(r => r.Account == account) >= limit)
            {
                return false;
            }

            _records.Add(new DownloadRecord(entryId, _time.GetUtcNow(), account));
            try
            {
                JsonFile.Write(_path, _records);
                _problem = null;
            }
            catch (Exception ex)
            {
                _problem = SaveProblem(ex);
                // Not saved, so a restart would forget it: don't let the download start.
                _records.RemoveAt(_records.Count - 1);
                throw;
            }

            return true;
        }
    }

    /// <summary>Undo the latest record for an entry. Only for downloads certain never to have reached the source.</summary>
    public void Refund(string entryId, string account)
    {
        lock (_lock)
        {
            var index = _records.FindLastIndex(r => r.EntryId == entryId && r.Account == account);
            if (index >= 0)
            {
                _records.RemoveAt(index);
                Save();
            }
        }
    }

    private static string? Invalid(List<DownloadRecord?>? records, DateTimeOffset now) =>
        records is null ? "no record list" : records.Select(r => Invalid(r, now)).FirstOrDefault(p => p is not null);

    private static string? Invalid(DownloadRecord? r, DateTimeOffset now) => r switch
    {
        null => "a record is null",
        { EntryId: null or "" } => "a record has no entry",
        { Account: null or "" } => "a record has no account",
        _ when r.At == default || r.At > now + FutureSkew => $"a record has an impossible time ({r.At:O})",
        _ => null,
    };

    /// <summary>When the newest copy of a damaged history set aside within the last window was made, if any.</summary>
    private DateTimeOffset? RecentlySetAside(DateTimeOffset now) =>
        JsonFile.CorruptCopies(_path)
            .Select(c => c.At > now ? now : c.At)
            .Where(at => now - at < Window)
            .Select(at => (DateTimeOffset?)at)
            .Max();

    /// <summary>Writes the history, recording instead of throwing when it can't.</summary>
    private void Save()
    {
        try
        {
            JsonFile.Write(_path, _records);
            _problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _problem = SaveProblem(ex);
        }
    }

    private string SaveProblem(Exception ex) =>
        $"The AudioVault download history {_path} can't be saved ({ex.Message}). "
        + "Check that the plugin's data folder is writable and the disk isn't full.";

    private DateTimeOffset? Blocked() =>
        _records.Where(r => r.Account == AllAccounts).Select(r => (DateTimeOffset?)(r.At + Window)).Max();

    private void Prune() => _records.RemoveAll(r => _time.GetUtcNow() - r.At > Window);
}

public sealed class QuotaExceededException(int limit)
    : Exception($"Daily download limit of {limit} reached; try again later.");
