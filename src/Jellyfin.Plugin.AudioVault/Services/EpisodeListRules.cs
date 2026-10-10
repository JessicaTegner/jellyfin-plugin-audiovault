using System.Globalization;
using AudioVault.Core.Library;

namespace Jellyfin.Plugin.AudioVault.Services;

public enum EpisodeListMethod
{
    /// <summary>Jellyfin hasn't identified the show yet; tried again once it has.</summary>
    WaitForIdentification,

    /// <summary>The whole season in one TMDb request.</summary>
    SeasonList,

    /// <summary>Jellyfin's providers asked one episode at a time.</summary>
    PerEpisode,
}

/// <param name="Seasons">Single-part seasons in the library.</param>
/// <param name="Waiting">Seasons still showing only episode 1.</param>
/// <param name="WaitingForShow">Of those, seasons whose show Jellyfin hadn't identified at the last pass; null before the first.</param>
/// <param name="Tried">Of those, seasons already asked for that had no list; they are asked again later.</param>
/// <param name="Failed">Of those, seasons whose last lookup failed (an error, not an empty answer); asked again sooner.</param>
public sealed record EpisodeListStatus(int Seasons, int Resolved, int Waiting, int? WaitingForShow, bool Running, int? Tried = null, int Failed = 0);

/// <summary>How a pending season's episode list is looked up. No Jellyfin types, so it can be tested on its own.</summary>
public static class EpisodeListRules
{
    /// <summary>A season the lookup had no list for is asked again after this.</summary>
    public static readonly TimeSpan RetryNothingFound = TimeSpan.FromHours(12);

    /// <summary>A season whose lookup failed is asked again after this.</summary>
    public static readonly TimeSpan RetryFailed = TimeSpan.FromHours(1);

    public static int? ParseTmdbId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <param name="seriesFound">Whether Jellyfin has the season's series at all (a scan may not have added it yet).</param>
    /// <param name="otherIds">Whether the show has a provider id besides TMDb's (e.g. TVDB), which the per-episode lookup can use.</param>
    public static EpisodeListMethod Choose(bool seriesFound, string? tmdbId, bool otherIds, string? displayOrder)
    {
        if (!seriesFound)
        {
            return EpisodeListMethod.WaitForIdentification;
        }

        if (ParseTmdbId(tmdbId) is null)
        {
            return otherIds ? EpisodeListMethod.PerEpisode : EpisodeListMethod.WaitForIdentification;
        }

        // Other display orders (absolute, DVD, story arcs...) renumber episodes; TMDb's season list is the aired order.
        return string.IsNullOrEmpty(displayOrder) ? EpisodeListMethod.SeasonList : EpisodeListMethod.PerEpisode;
    }

    /// <summary>A season's regular episode numbers, ascending: no specials (episode 0), duplicates or absurd numbers.</summary>
    public static IReadOnlyList<int> EpisodeNumbers(IEnumerable<long> numbers) =>
        [.. numbers.Where(n => n is > 0 and <= EpisodeCountStore.MaxEpisodes).Select(n => (int)n).Distinct().Order().Take(EpisodeCountStore.MaxEpisodes)];

    /// <summary>The count to store for a looked-up list, or null when it lists nothing.</summary>
    public static EpisodeCount? ToCount(IReadOnlyList<int> numbers) =>
        numbers.Count == 0 ? null : new EpisodeCount(numbers.Count, EpisodeCountOrigin.Metadata, numbers);

    /// <summary>What a lookup is keyed by: a season is looked up again sooner when its show's identity or order changes.</summary>
    /// <param name="identity">The show's TMDb id, or its other provider ids when it has none.</param>
    public static string LookupKey(string? identity, string? displayOrder) => (identity ?? string.Empty) + "/" + (displayOrder ?? string.Empty);

    /// <summary>
    /// The lookup key of a show as it is identified now: by its TMDb id, else its other provider ids, and its display
    /// order. A lookup's answer is only kept if the show's key is still the one it was looked up with.
    /// </summary>
    public static string ShowKey(IEnumerable<KeyValuePair<string, string>> providerIds, string? displayOrder)
    {
        var ids = providerIds.ToList();
        var tmdb = ids.FirstOrDefault(p => string.Equals(p.Key, TmdbKey, StringComparison.OrdinalIgnoreCase)).Value;
        return LookupKey(ParseTmdbId(tmdb) is null ? OtherIds(ids, TmdbKey) : tmdb, displayOrder);
    }

    private const string TmdbKey = "Tmdb";

    /// <summary>A stable string of a show's non-TMDb provider ids, or null when it has none.</summary>
    public static string? OtherIds(IEnumerable<KeyValuePair<string, string>> providerIds, string tmdbKey)
    {
        var ids = providerIds
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) && !string.Equals(p.Key, tmdbKey, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key + "=" + p.Value.Trim())
            .Order(StringComparer.Ordinal)
            .ToList();
        return ids.Count == 0 ? null : string.Join(';', ids);
    }
}

/// <summary>
/// Seasons whose lookup found nothing or failed, so automatic passes don't ask again every time. A different lookup
/// key (the show was identified differently) makes a season due at once; passes started by hand ask regardless.
/// Failures are kept apart from empty answers: they say nothing about the season, and are asked again sooner.
/// </summary>
public sealed class EpisodeListAttempts
{
    private readonly Dictionary<string, (string Key, DateTimeOffset Due, bool Failed)> _attempts = [];
    private readonly Lock _lock = new();

    public bool IsDue(string entryId, string key, DateTimeOffset now)
    {
        lock (_lock)
        {
            return !_attempts.TryGetValue(entryId, out var a) || a.Key != key || now >= a.Due;
        }
    }

    /// <summary>The lookup answered, with no list.</summary>
    public void Record(string entryId, string key, DateTimeOffset retryAt) => Record(entryId, key, retryAt, failed: false);

    public void Record(string entryId, string key, DateTimeOffset retryAt, bool failed)
    {
        lock (_lock)
        {
            _attempts[entryId] = (key, retryAt, failed);
        }
    }

    /// <summary>The season was asked for and TMDb (or the providers) had no list.</summary>
    public bool Tried(string entryId)
    {
        lock (_lock)
        {
            return _attempts.TryGetValue(entryId, out var a) && !a.Failed;
        }
    }

    /// <summary>The season's last lookup failed.</summary>
    public bool Failed(string entryId)
    {
        lock (_lock)
        {
            return _attempts.TryGetValue(entryId, out var a) && a.Failed;
        }
    }

    /// <summary>When the earliest failed lookup is due again, or null if none failed.</summary>
    public DateTimeOffset? NextFailedRetry()
    {
        lock (_lock)
        {
            return _attempts.Values.Where(a => a.Failed).Select(a => (DateTimeOffset?)a.Due).Min();
        }
    }

    public void Forget(string entryId)
    {
        lock (_lock)
        {
            _attempts.Remove(entryId);
        }
    }
}

/// <summary>Waits out a library scan: episode-list passes don't do metadata work next to one.</summary>
public static class ScanGate
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);

    /// <returns>Whether it had to wait.</returns>
    public static async Task<bool> WaitWhileAsync(Func<bool> scanning, TimeSpan poll, CancellationToken ct)
    {
        var waited = false;
        while (scanning())
        {
            waited = true;
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }

        return waited;
    }
}

/// <summary>A metadata provider asked whether a season has a given episode. Throws when it can't tell.</summary>
public sealed record EpisodeSource(string Name, Func<int, CancellationToken, Task<bool>> HasEpisodeAsync);

/// <summary>
/// The per-episode lookup: asks for episodes 1, 2, 3... until one isn't there. Only an answer every provider gave
/// without an error, twice in a row, ends the season; anything less throws, so the season counts as failed and no
/// short or empty list is kept.
/// </summary>
public static class EpisodeCounter
{
    public const int MaxEpisodes = 200;

    /// <summary>Whether the pass's own cancellation stopped it; any other error, a timeout too, is a failed lookup.</summary>
    public static bool IsFailure(Exception ex, CancellationToken passToken) =>
        ex is not OperationCanceledException || !passToken.IsCancellationRequested;

    /// <param name="exists">Asks the providers for one episode; throws when they can't tell.</param>
    /// <param name="waitForScan">Waits out a library scan; true when it had to.</param>
    /// <param name="sameShow">Whether the show is still identified as it was when the lookup started.</param>
    /// <returns>The episode numbers, empty when the season has none; null when the show was identified differently meanwhile.</returns>
    public static async Task<IReadOnlyList<int>?> CountAsync(
        Func<int, CancellationToken, Task<bool>> exists,
        Func<CancellationToken, Task<bool>> waitForScan,
        Func<bool> sameShow,
        CancellationToken ct)
    {
        var count = 0;
        var missingOnce = false;
        while (count < MaxEpisodes)
        {
            if (await waitForScan(ct).ConfigureAwait(false) && !sameShow())
            {
                return null;
            }

            if (await exists(count + 1, ct).ConfigureAwait(false))
            {
                count++;
                missingOnce = false;
            }
            else if (missingOnce)
            {
                break;
            }
            else
            {
                // Asked again before it ends the season: a provider may report a passing error as "not found".
                missingOnce = true;
            }
        }

        return [.. Enumerable.Range(1, count)];
    }

    /// <summary>True when any provider has the episode, false when all answered that none has it.</summary>
    public static async Task<bool> AnyHasAsync(IReadOnlyList<EpisodeSource> sources, int episode, CancellationToken ct)
    {
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No metadata provider enabled for this library can look up episodes");
        }

        Exception? error = null;
        foreach (var source in sources)
        {
            try
            {
                if (await source.HasEpisodeAsync(episode, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                error ??= new InvalidOperationException($"{source.Name} couldn't look up episode {episode}", ex);
            }
        }

        return error is null ? false : throw error;
    }
}

/// <summary>
/// When the next automatic episode-list pass starts: a while after startup and then every few hours, soon after a
/// library scan, and shortly after shows get identified (requests in quick succession share one pass). Never while a
/// library scan runs.
/// </summary>
public sealed class EpisodePassPlan(TimeSpan interval, TimeSpan maxDebounce)
{
    private readonly Lock _lock = new();
    private DateTimeOffset _nextRoutine = DateTimeOffset.MaxValue;
    private DateTimeOffset? _due;
    private DateTimeOffset? _firstRequest;
    private DateTimeOffset _hold = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStarted = DateTimeOffset.MinValue;

    public void Start(DateTimeOffset now, TimeSpan firstPass)
    {
        lock (_lock)
        {
            _nextRoutine = now + firstPass;
        }
    }

    /// <summary>Asks for a pass after <paramref name="delay"/>; each request pushes it back, up to the debounce limit.</summary>
    public void Request(DateTimeOffset now, TimeSpan delay)
    {
        lock (_lock)
        {
            _firstRequest ??= now;
            var due = _due is { } earlier && earlier > now + delay ? earlier : now + delay;
            var latest = _firstRequest.Value + maxDebounce;
            _due = due < latest ? due : latest;
        }
    }

    /// <summary>Nothing starts before <paramref name="until"/> (a library scan just finished; Jellyfin is still settling).</summary>
    public void HoldUntil(DateTimeOffset until)
    {
        lock (_lock)
        {
            if (until > _hold)
            {
                _hold = until;
            }
        }
    }

    /// <summary>
    /// A pass at <paramref name="at"/> at the latest, e.g. when failed lookups are due again. A time a pass has already
    /// started after is ignored: that pass asked for them (or couldn't, say because the show is no longer identified).
    /// </summary>
    public void RetryAt(DateTimeOffset at)
    {
        lock (_lock)
        {
            if (at > _lastStarted && at < _nextRoutine)
            {
                _nextRoutine = at;
            }
        }
    }

    public bool ShouldStart(DateTimeOffset now, bool scanRunning)
    {
        lock (_lock)
        {
            return !scanRunning && now >= _hold && (now >= _nextRoutine || _due <= now);
        }
    }

    /// <summary>A pass starts: it covers every request so far.</summary>
    public void Started(DateTimeOffset now)
    {
        lock (_lock)
        {
            _due = null;
            _firstRequest = null;
            _nextRoutine = now + interval;
            _lastStarted = now;
        }
    }
}
