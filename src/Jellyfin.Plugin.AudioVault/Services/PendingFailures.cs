namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>A user's watched state of an item, as it was before a blocked play attempt.</summary>
public sealed record PlayState(bool Played, int PlayCount, long PlaybackPositionTicks, DateTime? LastPlayedDate);

/// <summary>A message waiting for a player to close, and the watched state to put back then.</summary>
/// <param name="PriorStates">Per item id (the item and the version played), the state before the attempt.</param>
public sealed record PendingFailure(
    DateTimeOffset At,
    string? SessionId,
    Guid UserId,
    string Message,
    IReadOnlyDictionary<Guid, PlayState> PriorStates)
{
    /// <summary>The blocked attempt's PlaySessionId, which the player reports with its start and stop; null if unknown.</summary>
    public string? PlaySessionId { get; init; }

    /// <summary>Whether a player's report with this PlaySessionId is about another attempt (only when both are known).</summary>
    public bool IsOtherAttempt(string? playSessionId) =>
        !string.IsNullOrEmpty(PlaySessionId) && !string.IsNullOrEmpty(playSessionId)
        && !string.Equals(PlaySessionId, playSessionId, StringComparison.Ordinal);
}

/// <summary>
/// Pending failures keyed by playback session and item, so two people failing on the same title don't take each
/// other's messages. A failure whose session wasn't known matches a stop of that item from any session.
/// Telling the user and restoring the watched state are separate: a failure whose message was already shown (because
/// the player didn't report stopping in time) stays here, so a late stop still puts the old state back.
/// A successful attempt at the item retires it: from then on the watched state is that attempt's, and no stop (the
/// new attempt's, or a late one of the blocked attempt) may put the old state back.
/// Attempts are ordered by when their PlaySessionId was issued, so a delayed event of an earlier attempt (its start,
/// a repeated fetch) can't retire or replace the record of a newer one.
/// </summary>
public sealed class PendingFailures
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Session, Guid Item), PendingFailure> _items = [];
    private readonly HashSet<PendingFailure> _delivered = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (long Sequence, DateTimeOffset At, bool Blocked)> _attempts = new(StringComparer.Ordinal);
    private long _nextSequence;

    /// <summary>A PlaySessionId was issued (PlaybackInfo answered): it is newer than every one issued before.</summary>
    public void Issued(string? playSessionId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        lock (_lock)
        {
            if (!_attempts.ContainsKey(playSessionId))
            {
                _attempts[playSessionId] = (++_nextSequence, now, false);
            }
        }
    }

    /// <summary>
    /// Whether a blocked fetch is a new attempt that replaces <paramref name="existing"/>, rather than another fetch of
    /// the same attempt or a delayed one of an earlier attempt.
    /// </summary>
    public bool IsNewAttempt(string? playSessionId, PendingFailure existing, bool delivered)
    {
        lock (_lock)
        {
            return IsNewAttemptLocked(playSessionId, existing, delivered);
        }
    }

    /// <summary>
    /// Records a blocked fetch in one step: checks it against the failure already waiting for the item and, if it is a
    /// new attempt, replaces that failure, keeping the watched state from before the earliest attempt. Null when the
    /// fetch is another of the waiting attempt's or a delayed one of an earlier attempt.
    /// </summary>
    /// <param name="capture">Reads the watched state now; only called when no earlier attempt's state is kept.</param>
    public PendingFailure? Record(
        DateTimeOffset now,
        string? sessionId,
        Guid userId,
        string message,
        string? playSessionId,
        Guid itemId,
        IReadOnlyCollection<Guid> itemIds,
        Func<IReadOnlyDictionary<Guid, PlayState>> capture)
    {
        lock (_lock)
        {
            var existing = _items.GetValueOrDefault((sessionId ?? string.Empty, itemId));
            if (existing is not null && !IsNewAttemptLocked(playSessionId, existing, _delivered.Contains(existing)))
            {
                return null;
            }

            // A new attempt before the last one's stop arrived: the state from before the first attempt is the one to restore.
            var states = existing?.PriorStates ?? capture();
            var pending = new PendingFailure(now, sessionId, userId, message, states) { PlaySessionId = playSessionId };
            AddLocked(pending, itemIds);
            return pending;
        }
    }

    public void Add(PendingFailure pending, IEnumerable<Guid> itemIds)
    {
        lock (_lock)
        {
            AddLocked(pending, itemIds);
        }
    }

    /// <summary>The failure waiting for this session's stop of the item, if any, and whether its message was shown.</summary>
    public PendingFailure? Find(string? sessionId, Guid itemId, out bool delivered)
    {
        lock (_lock)
        {
            var found = _items.GetValueOrDefault((sessionId ?? string.Empty, itemId));
            delivered = found is not null && _delivered.Contains(found);
            return found;
        }
    }

    /// <summary>Takes the failure for a stopped item, if one is waiting and not older than <paramref name="window"/>.</summary>
    public PendingFailure? Take(string? sessionId, Guid itemId, DateTimeOffset now, TimeSpan window) =>
        Take(sessionId, itemId, null, now, window, out _);

    /// <param name="playSessionId">The stopped attempt's PlaySessionId, if the player reported one. A stop of another
    /// attempt than the blocked one leaves the failure waiting for its own stop.</param>
    /// <param name="delivered">Whether its message was already shown, so only the watched state is left to restore.</param>
    public PendingFailure? Take(string? sessionId, Guid itemId, string? playSessionId, DateTimeOffset now, TimeSpan window, out bool delivered)
    {
        lock (_lock)
        {
            delivered = false;
            if (!_items.TryGetValue((sessionId ?? string.Empty, itemId), out var pending)
                && !_items.TryGetValue((string.Empty, itemId), out pending))
            {
                return null;
            }

            if (pending.IsOtherAttempt(playSessionId))
            {
                return null;
            }

            RemoveAll(pending);
            delivered = _delivered.Remove(pending);
            return now - pending.At <= window ? pending : null;
        }
    }

    /// <summary>
    /// Records that the message was shown without the player stopping first. True only the first time, and only while
    /// the failure is still waiting; it stays waiting for its stop.
    /// </summary>
    public bool MarkDelivered(PendingFailure pending)
    {
        lock (_lock)
        {
            return IsWaiting(pending) && _delivered.Add(pending);
        }
    }

    /// <summary>Removes a failure if it is still waiting; true if it was.</summary>
    public bool Remove(PendingFailure pending)
    {
        lock (_lock)
        {
            _delivered.Remove(pending);
            return RemoveAll(pending);
        }
    }

    /// <summary>
    /// A play of these items that is going ahead (not blocked): retires the user's failures of them in this session,
    /// and those whose session wasn't known, if that play is newer than the failed attempt. A play whose PlaySessionId
    /// isn't known can't be placed, so it retires nothing. Returns how many were retired.
    /// </summary>
    public int RetireAfterSuccess(string? sessionId, Guid userId, IEnumerable<Guid> itemIds, string? playSessionId)
    {
        lock (_lock)
        {
            var retired = 0;
            foreach (var id in itemIds)
            {
                foreach (var key in new[] { (sessionId ?? string.Empty, id), (string.Empty, id) })
                {
                    if (_items.TryGetValue(key, out var pending) && pending.UserId == userId
                        && IsNewerSuccess(playSessionId, pending) && RemoveAll(pending))
                    {
                        _delivered.Remove(pending);
                        retired++;
                    }
                }
            }

            return retired;
        }
    }

    /// <summary>
    /// A player in this session started the item: if that is a newer attempt that wasn't blocked itself, the failure is
    /// retired. The blocked attempt's own start (with its placeholder), and a delayed start of an earlier blocked
    /// attempt, are not.
    /// </summary>
    public bool RetireOnStart(string? sessionId, Guid itemId, string? playSessionId)
    {
        lock (_lock)
        {
            if (sessionId is null || !_items.TryGetValue((sessionId, itemId), out var pending) || !IsNewerSuccess(playSessionId, pending))
            {
                return false;
            }

            _delivered.Remove(pending);
            return RemoveAll(pending);
        }
    }

    /// <summary>Drops failures older than <paramref name="window"/>.</summary>
    public void Prune(DateTimeOffset now, TimeSpan window)
    {
        lock (_lock)
        {
            foreach (var (key, value) in _items.ToList())
            {
                if (now - value.At > window)
                {
                    _items.Remove(key);
                }
            }

            _delivered.RemoveWhere(p => !IsWaiting(p));
            foreach (var (id, attempt) in _attempts.ToList())
            {
                if (now - attempt.At > window)
                {
                    _attempts.Remove(id);
                }
            }
        }
    }

    // Evidence that a play went ahead after the failure: an attempt issued later that wasn't blocked. When the failed
    // attempt's PlaySessionId isn't known, an attempt issued after the failure was recorded is newer.
    private bool IsNewerSuccess(string? playSessionId, PendingFailure pending) =>
        !string.IsNullOrEmpty(playSessionId)
        && !string.Equals(playSessionId, pending.PlaySessionId, StringComparison.Ordinal)
        && _attempts.TryGetValue(playSessionId, out var attempt) && !attempt.Blocked
        && (pending.PlaySessionId is { } failed && _attempts.TryGetValue(failed, out var f)
            ? attempt.Sequence > f.Sequence
            : attempt.At > pending.At);

    private bool IsNewAttemptLocked(string? playSessionId, PendingFailure existing, bool delivered) =>
        string.IsNullOrEmpty(playSessionId) || string.IsNullOrEmpty(existing.PlaySessionId)
            // Attempts can't be told apart: once the message was shown, another fetch is taken as another try.
            ? delivered
            : existing.IsOtherAttempt(playSessionId) && !IsOlder(playSessionId, existing.PlaySessionId);

    private bool IsOlder(string a, string b) =>
        _attempts.TryGetValue(a, out var x) && _attempts.TryGetValue(b, out var y) && x.Sequence < y.Sequence;

    private void AddLocked(PendingFailure pending, IEnumerable<Guid> itemIds)
    {
        if (!string.IsNullOrEmpty(pending.PlaySessionId))
        {
            var known = _attempts.TryGetValue(pending.PlaySessionId, out var a) ? a : (Sequence: ++_nextSequence, At: pending.At, Blocked: false);
            _attempts[pending.PlaySessionId] = known with { Blocked = true };
        }

        foreach (var id in itemIds)
        {
            _items[(pending.SessionId ?? string.Empty, id)] = pending;
        }
    }

    private bool IsWaiting(PendingFailure pending) => _items.Values.Any(v => ReferenceEquals(v, pending));

    private bool RemoveAll(PendingFailure pending)
    {
        var removed = false;
        foreach (var (key, value) in _items.ToList())
        {
            if (ReferenceEquals(value, pending))
            {
                removed |= _items.Remove(key);
            }
        }

        return removed;
    }
}
