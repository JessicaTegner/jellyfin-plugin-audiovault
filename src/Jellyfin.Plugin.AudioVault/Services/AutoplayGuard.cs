namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>What a player's stream request was given.</summary>
public enum ServedKind
{
    Ready,
    Live,
    Placeholder,
}

/// <summary>A player's report that an attempt stopped, as far as the guard needs it.</summary>
/// <param name="Stream">The stopped item's stream, if it is one of ours.</param>
/// <param name="Runtime">The stopped item's length as the library knows it.</param>
/// <param name="Queue">The items of the player's queue sent with the stop; null when the client sent none.</param>
/// <param name="Cached">The stopped stream is in the cache, so an unseen short play of it was real media.</param>
public sealed record StopReport(
    string? PlaySessionId,
    (string EntryId, int? Episode)? Stream,
    TimeSpan? Position,
    TimeSpan? Runtime,
    bool Failed = false,
    string? NextMediaType = null,
    IReadOnlyCollection<Guid>? Queue = null,
    bool Cached = false);

/// <summary>
/// Keeps a player's queue from spending downloads: a play the queue moves on to by itself never starts a download; only
/// a deliberate press does. No client says reliably which is which (jellyfin-web sets NextMediaType on its state, not
/// in the stop report it sends), so every client is judged by time: a queue asks for its next item within about a
/// second of the stop, while a user who closed the player needs several seconds to pick something else.
/// After a stop that may let the queue move on (a natural end, a placeholder, a held or failed play, or anything the
/// guard can't tell), a play that would start a download within <see cref="Window"/> is held, unless its item isn't in
/// the queue the stop reported. A stop part-way through real media is the user's doing and holds nothing.
/// One exception: a stop at the end of real media that was actually listened to lets its one successor start one
/// download, so watching a show episode after episode keeps going.
/// Each attempt is decided once, by its PlaySessionId; whether it may start a download is kept apart from whether it
/// may play what is already there.
/// </summary>
public sealed class AutoplayGuard
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>A play of uncached media that stopped before this may have been a placeholder.</summary>
    public static readonly TimeSpan ShortPlay = TimeSpan.FromSeconds(10);

    /// <summary>How long a play request waits for the stop report of the session's previous play.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    /// <summary>A stop this close to the end of the item, or within <see cref="EndShare"/> of its length, may be its natural end.</summary>
    public static readonly TimeSpan EndTolerance = TimeSpan.FromSeconds(30);

    /// <summary>Library lengths can be rounded (e.g. from TMDb), so a natural end may come this share of the length early.</summary>
    public const double EndShare = 0.05;

    /// <summary>Real media counts as watched from this share of its length, or within <see cref="WatchedTail"/> of the end.</summary>
    public const double WatchedShare = 0.9;

    public static readonly TimeSpan WatchedTail = TimeSpan.FromMinutes(5);

    /// <summary>Shorter items never count as watched.</summary>
    public static readonly TimeSpan MinWatched = TimeSpan.FromMinutes(10);

    /// <summary>The time since the play started must cover this share of the item's length (faster speeds still count).</summary>
    public const double ListenedShare = 0.6;

    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RememberPlaying = TimeSpan.FromHours(12);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>The server issued an attempt for a play in this session (PlaybackInfo); it is now the newest.</summary>
    public void Issued(string? sessionId, string? playSessionId, DateTimeOffset now)
    {
        if (sessionId is null || string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        lock (_lock)
        {
            Prune(now);
            See(State(sessionId, now), playSessionId, now);
        }
    }

    /// <summary>A player's stream request for an attempt was answered; a placeholder outranks real media.</summary>
    /// <param name="sessionId">The player's session, when the request names it; it is now playing this attempt.</param>
    public void Served(string? sessionId, string? playSessionId, string entryId, int? episode, ServedKind kind, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        lock (_lock)
        {
            Prune(now);
            var state = sessionId is null ? null : State(sessionId, now);
            var attempt = See(state, playSessionId, now);
            attempt.StartedAt ??= now;
            if (attempt.Kind is not { } earlier || kind > earlier)
            {
                attempt.Kind = kind;
            }

            if (state is not null)
            {
                MarkPlaying(state, playSessionId, attempt);
            }
        }
    }

    /// <summary>A session reported the start of an attempt (any item, ours or not).</summary>
    public void Started(string? sessionId, string? playSessionId, Guid? itemId, DateTimeOffset now)
    {
        if (sessionId is null)
        {
            return;
        }

        lock (_lock)
        {
            Prune(now);
            var state = State(sessionId, now);
            if (string.IsNullOrEmpty(playSessionId))
            {
                MarkPlaying(state, string.Empty, null);
                return;
            }

            var attempt = See(state, playSessionId, now);
            attempt.StartedAt ??= now;
            if (attempt.Decision is null && !string.IsNullOrEmpty(state.Playing) && state.Playing != playSessionId)
            {
                // It replaced a play whose stop wasn't reported: whether the queue moved on is unknown.
                state.Restriction = new Restriction(now, null, Allowance.None);
            }

            MarkPlaying(state, playSessionId, attempt);

            // The queue may have moved on to this play: whatever restriction applies is bound to it now, before its
            // stream request, so the restriction can't lapse before the attempt is decided.
            if (attempt.Decision is null)
            {
                Decide(state, playSessionId, attempt, itemId, startsDownload: false, now);
            }
        }
    }

    /// <summary>A session reported a stop, read from the request before Jellyfin handles it.</summary>
    /// <returns>Whether the session's next play that would download is now held.</returns>
    public bool StopReported(string? sessionId, StopReport stop, DateTimeOffset now)
    {
        if (sessionId is null)
        {
            return false;
        }

        lock (_lock)
        {
            Prune(now);
            var state = State(sessionId, now);
            var playSessionId = string.IsNullOrEmpty(stop.PlaySessionId) ? null : stop.PlaySessionId;
            Stop(state, playSessionId);
            var attempt = playSessionId is null ? null : See(state, playSessionId, now);

            // The same stop reported again (jellyfin-web stops the old play once more on its way to the next) changes
            // nothing, and above all doesn't grant another download.
            if (attempt is { StoppedAt: not null })
            {
                return Restricted(state, null, now);
            }

            if (attempt is not null)
            {
                attempt.StoppedAt = now;
            }

            state.Restriction = MayMoveOn(attempt, stop)
                ? new Restriction(now, stop.Queue?.ToHashSet(), Watched(attempt, stop, now) ? Allowance.Available : Allowance.None)
                : null;
            return Restricted(state, null, now);
        }
    }

    /// <summary>Jellyfin reported a stop on its own (e.g. the session ended); the attempt is no longer playing.</summary>
    public void StopObserved(string? sessionId, string? playSessionId, DateTimeOffset now)
    {
        if (sessionId is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var state))
            {
                state.Touched = now;
                Stop(state, playSessionId);
            }
        }
    }

    /// <summary>
    /// If the session is playing another attempt whose stop hasn't been reported, waits up to <paramref name="timeout"/>
    /// for that report: a queue moving on sends it just before the next play request, which can be handled first.
    /// A stop that doesn't come in time leaves the guard uncertain, so it holds downloads as after a stop that may let
    /// the queue move on; a deliberate press after <see cref="Window"/> goes ahead.
    /// </summary>
    /// <param name="requester">The attempt asking, which may itself already be the one playing.</param>
    /// <returns>False if the wait ran out.</returns>
    public async Task<bool> WaitForStopAsync(string? sessionId, string? requester, TimeSpan timeout, DateTimeOffset now, CancellationToken ct)
    {
        Task stopped;
        string playing;
        SessionState state;
        lock (_lock)
        {
            if (sessionId is null || !_sessions.TryGetValue(sessionId, out state!) || state.Playing is not { } current
                || (!string.IsNullOrEmpty(requester) && string.Equals(current, requester, StringComparison.Ordinal)))
            {
                return true;
            }

            stopped = state.StopSeen.Task;
            playing = current;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = await Task.WhenAny(stopped, Task.Delay(timeout, cancel.Token)).ConfigureAwait(false);
        await cancel.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished == stopped)
        {
            return true;
        }

        lock (_lock)
        {
            if (string.Equals(state.Playing, playing, StringComparison.Ordinal))
            {
                state.Playing = null;
                state.StopSeen.TrySetResult();
                state.Restriction = new Restriction(now + timeout, null, Allowance.None);
            }
        }

        return false;
    }

    /// <summary>
    /// For a play request whose attempt isn't issued yet (Jellyfin's probes run inside PlaybackInfo): whether it may
    /// start a download. If that needs a watched item's allowance, the allowance is reserved for this item, so no
    /// other request can use it.
    /// </summary>
    public bool ReserveEarly(string? sessionId, Guid? itemId, DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            if (sessionId is null || !_sessions.TryGetValue(sessionId, out var state) || !Restricted(state, itemId, now))
            {
                return true;
            }

            var restriction = state.Restriction!;
            if (restriction.Allowance == Allowance.Available && itemId is { } item)
            {
                restriction.Allowance = Allowance.Reserved;
                restriction.ReservedFor = item;
                return true;
            }

            return false;
        }
    }

    /// <summary>Decides an attempt: true holds it. Later requests of the same attempt get the same answer.</summary>
    /// <param name="startsDownload">Playing it would start a new download (it isn't cached, local or downloading).</param>
    public bool Hold(string? sessionId, string? playSessionId, Guid? itemId, bool startsDownload, DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            var state = sessionId is null ? null : _sessions.GetValueOrDefault(sessionId);
            Attempt? attempt = null;
            if (!string.IsNullOrEmpty(playSessionId))
            {
                attempt = See(state, playSessionId, now);
                if (attempt.Decision is { } decided)
                {
                    if (startsDownload && !decided.Held && !decided.MayDownload)
                    {
                        // Allowed to play what was there, not to start a download.
                        attempt.Decision = decided with { Held = true };
                        return true;
                    }

                    return decided.Held;
                }
            }

            return Decide(state, playSessionId, attempt, itemId, startsDownload, now).Held;
        }
    }

    public bool IsDecided(string? playSessionId)
    {
        lock (_lock)
        {
            return !string.IsNullOrEmpty(playSessionId) && _attempts.TryGetValue(playSessionId, out var attempt) && attempt.Decision is not null;
        }
    }

    /// <summary>Whether this attempt was held.</summary>
    public bool IsHeld(string? playSessionId)
    {
        lock (_lock)
        {
            return !string.IsNullOrEmpty(playSessionId) && _attempts.TryGetValue(playSessionId, out var attempt) && attempt.Decision is { Held: true };
        }
    }

    /// <summary>
    /// Whether the session is still playing this attempt: it started, hasn't stopped or been replaced, and no newer
    /// attempt was issued or started in the session since.
    /// </summary>
    public bool IsCurrent(string? sessionId, string? playSessionId)
    {
        lock (_lock)
        {
            return sessionId is not null && !string.IsNullOrEmpty(playSessionId)
                && _sessions.TryGetValue(sessionId, out var state)
                && string.Equals(state.Playing, playSessionId, StringComparison.Ordinal)
                && _attempts.TryGetValue(playSessionId, out var attempt)
                && attempt.Sequence >= state.Newest;
        }
    }

    private Decision Decide(SessionState? state, string? playSessionId, Attempt? attempt, Guid? itemId, bool startsDownload, DateTimeOffset now)
    {
        var mayDownload = true;
        if (state is not null && Restricted(state, itemId, now))
        {
            // The first attempt decided after a watched item is its successor: it takes the one download, now or later.
            var restriction = state.Restriction!;
            var reservedForIt = restriction.Allowance == Allowance.Reserved && itemId is { } item && restriction.ReservedFor == item;
            if (restriction.Allowance == Allowance.Available || reservedForIt)
            {
                restriction.Allowance = Allowance.Used;
            }
            else
            {
                mayDownload = false;
            }
        }

        var decision = new Decision(startsDownload && !mayDownload, mayDownload);
        if (attempt is not null && playSessionId is not null)
        {
            attempt.Decision = decision;
        }

        return decision;
    }

    /// <summary>Whether a play in the session that would download is held now; for an item outside the stopped queue, no.</summary>
    private static bool Restricted(SessionState state, Guid? itemId, DateTimeOffset now) =>
        state.Restriction is { } r
        && now - r.At <= Window
        && (r.Queue is null || itemId is not { } item || r.Queue.Contains(item));

    /// <summary>Whether the queue may move on after this stop: anything but a stop part-way through real media.</summary>
    private static bool MayMoveOn(Attempt? attempt, StopReport stop)
    {
        if (attempt?.Decision is { Held: true } || attempt?.Kind == ServedKind.Placeholder || stop.Failed
            || !string.IsNullOrEmpty(stop.NextMediaType))
        {
            return true;
        }

        if (stop.Position is not { } p || stop.Runtime is not { } length || length <= TimeSpan.Zero)
        {
            return true;
        }

        // A short play of uncached media may have been a placeholder: a stream still converting, or one Jellyfin
        // relayed or transcoded (a cached one was real media).
        if (p < ShortPlay
            && (attempt?.Kind == ServedKind.Live || (attempt?.Kind is null && stop.Stream is not null && !stop.Cached)))
        {
            return true;
        }

        return p >= length - TimeSpan.FromTicks(Math.Max(EndTolerance.Ticks, (long)(length.Ticks * EndShare)));
    }

    /// <summary>
    /// Real media listened to (nearly) to the end: never a placeholder, a held, failed or short play, an unverified
    /// uncached play, or one that skipped ahead (too little time since it started).
    /// </summary>
    private static bool Watched(Attempt? attempt, StopReport stop, DateTimeOffset now)
    {
        if (attempt is null || attempt.StartedAt is not { } started || stop.Failed
            || stop.Position is not { } p || stop.Runtime is not { } length || length < MinWatched)
        {
            return false;
        }

        if (attempt.Decision is { Held: true } || attempt.Kind == ServedKind.Placeholder
            || (attempt.Kind is null && stop.Stream is not null && !stop.Cached))
        {
            return false;
        }

        var tail = TimeSpan.FromTicks(Math.Min(WatchedTail.Ticks, length.Ticks / 5));
        var nearEnd = p >= length * WatchedShare || length - p <= tail;
        return nearEnd && now - started >= length * ListenedShare;
    }

    private Attempt See(SessionState? state, string playSessionId, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(playSessionId, out var attempt))
        {
            _attempts[playSessionId] = attempt = new Attempt(++_sequence);
        }

        attempt.Seen = now;
        if (state is not null && attempt.Sequence > state.Newest)
        {
            state.Newest = attempt.Sequence;
        }

        return attempt;
    }

    private static void MarkPlaying(SessionState state, string playSessionId, Attempt? attempt)
    {
        // A late request or start of an attempt that has stopped, or that a newer attempt replaced, doesn't make it
        // play again.
        if (attempt is not null && (attempt.StoppedAt is not null || attempt.Sequence < state.Newest))
        {
            return;
        }

        if (state.StopSeen.Task.IsCompleted)
        {
            state.StopSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        state.Playing = playSessionId;
    }

    private static void Stop(SessionState state, string? playSessionId)
    {
        if (state.Playing is { } playing
            && (string.IsNullOrEmpty(playSessionId) || playing.Length == 0 || string.Equals(playing, playSessionId, StringComparison.Ordinal)))
        {
            state.Playing = null;
            state.StopSeen.TrySetResult();
        }
    }

    private SessionState State(string sessionId, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            _sessions[sessionId] = state = new SessionState();
        }

        state.Touched = now;
        return state;
    }

    private void Prune(DateTimeOffset now)
    {
        // What a play was served is kept while it is still playing, however long it runs.
        var playing = _sessions.Values.Select(v => v.Playing).Where(p => !string.IsNullOrEmpty(p)).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, value) in _attempts)
        {
            if (now - value.Seen > Remember && !(playing.Contains(key) && now - value.Seen <= RememberPlaying))
            {
                _attempts.Remove(key);
            }
        }

        foreach (var (key, state) in _sessions)
        {
            if (now - state.Touched > (state.Playing is null ? Remember : RememberPlaying))
            {
                state.StopSeen.TrySetResult();
                _sessions.Remove(key);
            }
        }
    }

    private enum Allowance
    {
        None,
        Available,
        Reserved,
        Used,
    }

    /// <param name="Held">The attempt gets the placeholder and is stopped.</param>
    /// <param name="MayDownload">The attempt may start a new download, not only play what is already there.</param>
    private readonly record struct Decision(bool Held, bool MayDownload);

    private sealed class Attempt(long sequence)
    {
        public long Sequence { get; } = sequence;

        public DateTimeOffset Seen { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public ServedKind? Kind { get; set; }

        public Decision? Decision { get; set; }

        public DateTimeOffset? StoppedAt { get; set; }
    }

    /// <summary>After a stop that may let the queue move on.</summary>
    /// <param name="Queue">The items the queue could move on to; null when unknown.</param>
    private sealed class Restriction(DateTimeOffset at, HashSet<Guid>? queue, Allowance allowance)
    {
        public DateTimeOffset At { get; } = at;

        public HashSet<Guid>? Queue { get; } = queue;

        public Allowance Allowance { get; set; } = allowance;

        public Guid? ReservedFor { get; set; }
    }

    private sealed class SessionState
    {
        /// <summary>The attempt the session plays whose stop hasn't been reported; empty when its id is unknown.</summary>
        public string? Playing { get; set; }

        public TaskCompletionSource StopSeen { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The newest attempt issued or seen in the session.</summary>
        public long Newest { get; set; }

        public Restriction? Restriction { get; set; }

        public DateTimeOffset Touched { get; set; }
    }
}
