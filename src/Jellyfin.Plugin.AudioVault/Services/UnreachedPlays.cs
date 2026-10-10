using System.Security.Cryptography;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>The player a PlaybackInfo response went to, as the settings page names it.</summary>
public sealed record PlayerInfo(string? SessionId, string DeviceId, string DeviceName, string Client, string? UserName);

public sealed record UnreachedReport(string DeviceName, string Client, string? UserName, string Url, int Count, DateTimeOffset LastAt);

/// <summary>
/// Evidence that players can't load the playback address: a direct play the player committed to whose ticketed stream
/// URL it never fetched. Only direct play counts (when Jellyfin transcodes, the server fetches the stream itself), and
/// only attempts this server issued playback info for, so a client playing its own downloaded copy isn't counted.
/// Playback info for a direct play only prepares an attempt; it is reported only once the player shows it tried: it
/// reported the start of a direct play, a stop marked as failed, or asked again with direct play turned off (the web
/// client's fallback after a failed load). A stop without any of these is an abandoned attempt. Everything is matched
/// by the attempt's PlaySessionId.
/// </summary>
public sealed class UnreachedPlays
{
    /// <summary>How long a player has to fetch the stream once its play is confirmed.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    public const int MaxAttempts = 2000;
    public const int MaxPerSession = 50;

    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan KeepReports = TimeSpan.FromDays(1);
    private const int MaxReports = 50;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string DeviceId, string Url), UnreachedReport> _reports = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _attempts.Count;
            }
        }
    }

    /// <summary>PlaybackInfo gave this player ticketed links to these streams of ours, at this address.</summary>
    /// <param name="directPlayDisabled">The player asked with direct play turned off, as a client does after a failed direct play.</param>
    /// <returns>Reports that became due meanwhile, for the log.</returns>
    public IReadOnlyList<UnreachedReport> Issued(
        string? playSessionId,
        PlayerInfo player,
        string url,
        IEnumerable<(string EntryId, int? Episode)> streams,
        DateTimeOffset now,
        bool directPlayDisabled = false)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return [];
        }

        lock (_lock)
        {
            var added = SweepLocked(now);
            var list = streams.ToList();
            if (directPlayDisabled && player.SessionId is { } session)
            {
                // The player falls back from a direct play it couldn't load.
                foreach (var earlier in _attempts.Values)
                {
                    if (earlier.Player.SessionId == session && earlier.ExpectedAt is { } at && now - at <= Window
                        && !earlier.Arrived && !earlier.Cancelled && earlier.Streams.Intersect(list).Any())
                    {
                        earlier.Confirmed = true;
                    }
                }
            }

            if (!_attempts.ContainsKey(playSessionId))
            {
                MakeRoom(player.SessionId);
                _attempts[playSessionId] = new Attempt(player, url, list, now);
            }

            return added;
        }
    }

    /// <summary>The player was given a direct play of this attempt; it counts once the player shows it tried.</summary>
    public void Prepared(string? playSessionId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (Find(playSessionId) is { Cancelled: false } attempt)
            {
                attempt.ExpectedAt ??= now;
            }
        }
    }

    /// <summary>Jellyfin reported the start of an attempt; only direct play is fetched by the player itself.</summary>
    public void Started(string? playSessionId, bool directPlay, bool held, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (Find(playSessionId) is not { } attempt)
            {
                return;
            }

            if (directPlay && !held && !attempt.Cancelled)
            {
                attempt.ExpectedAt ??= now;
                attempt.Confirmed = true;
            }
            else
            {
                attempt.Cancelled = true;
            }
        }
    }

    /// <summary>
    /// The player reported the attempt stopped. Unless it had fetched the stream or confirmed the play, it was
    /// abandoned; a stop marked as failed confirms it.
    /// </summary>
    public void Stopped(string? playSessionId, bool failed, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (Find(playSessionId) is not { Arrived: false, Cancelled: false } attempt)
            {
                return;
            }

            if (failed)
            {
                attempt.ExpectedAt ??= now;
                attempt.Confirmed = true;
            }
            else if (!attempt.Confirmed)
            {
                attempt.Cancelled = true;
            }
        }
    }

    public void Cancel(string? playSessionId)
    {
        lock (_lock)
        {
            if (Find(playSessionId) is { } attempt)
            {
                attempt.Cancelled = true;
            }
        }
    }

    /// <summary>Jellyfin itself streams this attempt (it transcodes or relays it), so its player won't fetch our link.</summary>
    public void Relayed(string? playSessionId) => Cancel(playSessionId);

    /// <summary>
    /// A ticketed stream request arrived: that attempt reached the server, and its device's report for that address no
    /// longer holds. A ticket without an attempt counts for the session's attempts at the address it arrived on.
    /// </summary>
    /// <param name="url">The address the request arrived on, normalized; used only when the ticket names no attempt.</param>
    public void Arrived(string? playSessionId, string? sessionId, string? url, DateTimeOffset now)
    {
        lock (_lock)
        {
            var reached = new HashSet<(string DeviceId, string Url)>();
            foreach (var (key, attempt) in _attempts)
            {
                var match = !string.IsNullOrEmpty(playSessionId)
                    ? key == playSessionId
                    : sessionId is not null && url is not null && attempt.Player.SessionId == sessionId
                        && string.Equals(attempt.Url, url, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    attempt.Arrived = true;
                    reached.Add((attempt.Player.DeviceId, attempt.Url));
                }
            }

            foreach (var key in _reports.Keys.Where(k => reached.Any(r => r.DeviceId == k.DeviceId && string.Equals(r.Url, k.Url, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _reports.Remove(key);
            }
        }
    }

    /// <summary>Turns overdue attempts into reports and forgets old ones; returns the new reports, for the log.</summary>
    public IReadOnlyList<UnreachedReport> Sweep(DateTimeOffset now)
    {
        lock (_lock)
        {
            return SweepLocked(now);
        }
    }

    /// <summary>Recent reports for this address, newest first.</summary>
    public IReadOnlyList<UnreachedReport> Reports(string url, DateTimeOffset now)
    {
        lock (_lock)
        {
            SweepLocked(now);
            return [.. _reports.Values.Where(r => string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.LastAt)];
        }
    }

    private List<UnreachedReport> SweepLocked(DateTimeOffset now)
    {
        var added = new List<UnreachedReport>();
        foreach (var attempt in _attempts.Values)
        {
            if (attempt.ExpectedAt is not { } at || !attempt.Confirmed || attempt.Arrived || attempt.Cancelled || attempt.Reported || now - at <= Window)
            {
                continue;
            }

            attempt.Reported = true;
            var p = attempt.Player;
            var key = (p.DeviceId, attempt.Url);
            var count = _reports.TryGetValue(key, out var earlier) ? earlier.Count + 1 : 1;
            var report = new UnreachedReport(p.DeviceName, p.Client, p.UserName, attempt.Url, count, earlier is not null && earlier.LastAt > at ? earlier.LastAt : at);
            _reports[key] = report;
            added.Add(report);
        }

        Prune(now);
        return added;
    }

    /// <summary>Keeps the attempts bounded: per session, and overall. The oldest go first.</summary>
    private void MakeRoom(string? sessionId)
    {
        if (sessionId is not null)
        {
            var mine = _attempts.Where(a => a.Value.Player.SessionId == sessionId).ToList();
            foreach (var old in mine.OrderBy(a => a.Value.IssuedAt).Take(Math.Max(0, mine.Count - MaxPerSession + 1)))
            {
                _attempts.Remove(old.Key);
            }
        }

        foreach (var old in _attempts.OrderBy(a => a.Value.IssuedAt).Take(Math.Max(0, _attempts.Count - MaxAttempts + 1)).ToList())
        {
            _attempts.Remove(old.Key);
        }
    }

    private Attempt? Find(string? playSessionId) =>
        !string.IsNullOrEmpty(playSessionId) && _attempts.TryGetValue(playSessionId, out var attempt) ? attempt : null;

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, attempt) in _attempts)
        {
            // Overdue attempts were reported just before; one confirmed late still gets its full window.
            var last = attempt.ExpectedAt is { } at && at > attempt.IssuedAt ? at : attempt.IssuedAt;
            if (now - last > Remember)
            {
                _attempts.Remove(key);
            }
        }

        foreach (var (key, report) in _reports)
        {
            if (now - report.LastAt > KeepReports)
            {
                _reports.Remove(key);
            }
        }

        foreach (var key in _reports.OrderByDescending(r => r.Value.LastAt).Skip(MaxReports).Select(r => r.Key).ToList())
        {
            _reports.Remove(key);
        }
    }

    private sealed class Attempt(PlayerInfo player, string url, List<(string EntryId, int? Episode)> streams, DateTimeOffset issuedAt)
    {
        public PlayerInfo Player { get; } = player;

        public string Url { get; } = url;

        public List<(string EntryId, int? Episode)> Streams { get; } = streams;

        public DateTimeOffset IssuedAt { get; } = issuedAt;

        public DateTimeOffset? ExpectedAt { get; set; }

        /// <summary>The player showed it tried to play this attempt directly.</summary>
        public bool Confirmed { get; set; }

        public bool Arrived { get; set; }

        public bool Cancelled { get; set; }

        public bool Reported { get; set; }
    }
}

/// <summary>
/// Checks from an admin's browser: the page loads the ping image at the playback address with a one-time nonce. The
/// nonce arriving here proves that browser reached this server at that address; the image loading alone would only
/// prove that something answered there. Each result belongs to the browser that ran the check (its user and device).
/// </summary>
public sealed class ReachProbes
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const int MaxOpen = 20;
    private const int MaxResults = 50;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Url, string Owner, DateTimeOffset Created, bool Seen)> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserCheck> _last = new(StringComparer.Ordinal);

    /// <summary>Who ran a check: the user and the device (browser) they ran it from.</summary>
    public static string Owner(Guid userId, string? deviceId) => userId.ToString("N") + "/" + (deviceId ?? string.Empty);

    public string Create(string url, string owner, DateTimeOffset now)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            Prune(now);
            foreach (var oldest in _open.OrderBy(p => p.Value.Created).Take(Math.Max(0, _open.Count - MaxOpen + 1)).Select(p => p.Key).ToList())
            {
                _open.Remove(oldest);
            }

            _open[nonce] = (url, owner, now, false);
        }

        return nonce;
    }

    /// <summary>The ping endpoint received this nonce. Unknown nonces are ignored.</summary>
    public void Seen(string? nonce, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(nonce))
        {
            return;
        }

        lock (_lock)
        {
            if (_open.TryGetValue(nonce, out var probe) && now - probe.Created <= Lifetime)
            {
                _open[nonce] = probe with { Seen = true };
            }
        }
    }

    /// <summary>The page is done waiting: the result, or null for an unknown or expired nonce or another owner's.</summary>
    /// <param name="loaded">Whether the browser loaded the image.</param>
    /// <param name="mixed">The page is open over https and the address is http.</param>
    public BrowserCheck? Finish(string nonce, string owner, bool loaded, bool mixed, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(nonce, out var probe) || probe.Owner != owner)
            {
                return null;
            }

            _open.Remove(nonce);
            if (now - probe.Created > Lifetime)
            {
                return null;
            }

            var outcome = probe.Seen ? BrowserOutcome.Reached : loaded ? BrowserOutcome.OtherServer : BrowserOutcome.NotReached;
            var result = new BrowserCheck(probe.Url, outcome, mixed && outcome != BrowserOutcome.Reached, now);
            _last[owner] = result;
            foreach (var oldest in _last.OrderBy(p => p.Value.At).Take(Math.Max(0, _last.Count - MaxResults)).Select(p => p.Key).ToList())
            {
                _last.Remove(oldest);
            }

            return result;
        }
    }

    /// <summary>The last check this browser ran, if any.</summary>
    public BrowserCheck? LastFor(string owner)
    {
        lock (_lock)
        {
            return _last.GetValueOrDefault(owner);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, probe) in _open)
        {
            if (now - probe.Created > Lifetime)
            {
                _open.Remove(key);
            }
        }
    }
}
