using System.Collections.Concurrent;
using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging;

namespace AudioVault.Core.Media;

public abstract record PlaybackTarget;

public sealed record ReadyTarget(string Path) : PlaybackTarget;

/// <summary>A fragmented MP4 that is still growing; read it until <see cref="Completion"/> finishes.</summary>
public sealed record LiveTarget(string Path, Task Completion) : PlaybackTarget;

/// <summary>A short black, silent clip served to metadata probes so they never cause a download.</summary>
public sealed record PlaceholderTarget(string Path) : PlaybackTarget;

/// <param name="MinFreeBytes">Downloads, extraction and conversions don't start with less free space than this on the
/// cache volume, and a running download stops when free space falls below half of it.</param>
/// <param name="MaxConcurrentConversions">ffmpeg conversions running at once, server-wide. Titles someone is waiting
/// to play go before background conversions of the rest of a season. Probe placeholders use a free slot or one extra
/// slot of their own (<see cref="MediaJobManager.PlaceholderSlots"/>), so they never wait behind whole downloads.</param>
public sealed record MediaOptions(
    int DailyDownloadLimit,
    long MaxCacheBytes,
    ConversionSettings Conversion,
    long MinFreeBytes = MediaOptions.DefaultMinFreeBytes,
    int MaxConcurrentConversions = MediaOptions.DefaultMaxConcurrentConversions,
    bool EvictWhenLowOnSpace = true)
{
    public const long DefaultMinFreeBytes = 2L * 1024 * 1024 * 1024;

    public const int DefaultMaxConcurrentConversions = 2;
}

public interface IMediaEvents
{
    void MediaReady(string entryId, int? episode);

    void EpisodesDiscovered(string entryId, int count);

    /// <summary>
    /// Preparing media failed. Raised once for every failed conversion job (with the job's episode, which for an old
    /// episode link can be the archive's own number), whether or not a request was waiting for it or already
    /// streaming, and once for a probe's detached preparation that failed (with the episode the probe asked for).
    /// Never twice for the same failure, entry and episode; requests that were waiting also get the exception.
    /// Not raised for jobs stopped by shutdown.
    /// </summary>
    void MediaFailed(string entryId, int? episode, Exception error);
}

public enum PreflightStatus
{
    Ok,
    NotFound,
    NoAccount,
    SourceNotReady,
    QuotaExhausted,

    /// <summary>ffmpeg can't run on this server, so nothing new can be prepared (see <see cref="PreflightResult.Problem"/>).</summary>
    ConversionUnavailable,

    /// <summary>The host held this download back (e.g. a player moving on through its queue by itself).</summary>
    Held,
}

/// <param name="NeedsDownload">Playing this will download from the source first.</param>
/// <param name="IsArchive">The download is a whole season, so playback starts only after it finishes.</param>
public sealed record PreflightResult(
    PreflightStatus Status,
    string? Problem,
    bool NeedsDownload,
    bool IsArchive,
    SourceEntry? Entry,
    DateTimeOffset? NextDownloadAt = null);

public sealed class EntryNotFoundException(string message) : Exception(message);

public sealed class MediaJobManager : IDisposable
{
    /// <summary>ffmpeg runs reserved for probe placeholders beyond <see cref="MediaOptions.MaxConcurrentConversions"/>.</summary>
    public const int PlaceholderSlots = 1;

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);
    private static readonly long SpaceCheckInterval = 64L * 1024 * 1024;

    private readonly IContentSource _source;
    private readonly CatalogStore _catalog;
    private readonly CacheStore _cache;
    private readonly FfmpegConverter _converter;
    private readonly DownloadQuota _quota;
    private readonly EpisodeCountStore _episodeCounts;
    private readonly IMediaEvents _events;
    private readonly Func<MediaOptions> _options;
    private readonly ILogger<MediaJobManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadSlots = new();
    private readonly ConcurrentDictionary<RevocationKey, CancellationTokenSource> _revocations = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Lazy<ConversionJob>> _conversions = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ArchiveMap>>> _archives = new();
    private readonly ConcurrentDictionary<string, CapacityState> _archiveCapacity = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _backfills = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _placeholders = new();
    private readonly ConcurrentDictionary<string, DateTime> _leases = new();
    private readonly ConcurrentDictionary<string, Lock> _entryLocks = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, HashSet<(string, int?)>> _reported = new();
    private readonly Lock _accountLock = new();
    private readonly JobProgressTracker _progress = new();
    private readonly ConversionGate _gate;
    private readonly ConversionGate _placeholderGate = new(() => PlaceholderSlots);
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private int _disposed;
    private int _maintaining;

    public MediaJobManager(
        IContentSource source,
        CatalogStore catalog,
        CacheStore cache,
        FfmpegConverter converter,
        DownloadQuota quota,
        EpisodeCountStore episodeCounts,
        IMediaEvents events,
        Func<MediaOptions> options,
        ILogger<MediaJobManager> logger)
    {
        _source = source;
        _catalog = catalog;
        _cache = cache;
        _converter = converter;
        _quota = quota;
        _episodeCounts = episodeCounts;
        _events = events;
        _options = options;
        _logger = logger;
        _gate = new ConversionGate(() => _options().MaxConcurrentConversions);
    }

    /// <summary>Why media can't be converted on this server (e.g. ffmpeg missing), or null. No AudioVault contact.</summary>
    public string? ConversionProblem
    {
        get
        {
            try
            {
                return _converter.ReadinessProblem();
            }
            catch (Exception ex)
            {
                return $"Checking ffmpeg failed ({ex.Message}).";
            }
        }
    }

    public int DownloadLimit => Math.Min(_options().DailyDownloadLimit, _source.Limits.MaxDownloadsPerDay);

    public IReadOnlyCollection<string> ActiveJobs => [.. _conversions.Keys];

    /// <summary>Expected length of an entry (e.g. from metadata), used for probes of titles that won't download.</summary>
    public Func<string, int?, TimeSpan?>? ExpectedDuration { get; set; }

    /// <summary>A download that receives nothing for this long fails (its quota stays used) and frees the account.</summary>
    public TimeSpan DownloadInactivityTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>A cached title isn't evicted until this long after it was last requested for playback.</summary>
    public TimeSpan PlaybackLease { get; set; } = TimeSpan.FromHours(4);

    public int MaxArchiveEntries { get; set; } = ArchiveExtractor.DefaultMaxEntries;

    public long MaxArchiveBytes { get; set; } = ArchiveExtractor.DefaultMaxBytes;

    /// <summary>
    /// Set by the host: false once an account snapshot is stale (removed, or its credentials replaced; see
    /// <see cref="SourceAccount.Generation"/>). Checked when a request starts or joins a download and again,
    /// atomically with <see cref="RevokeAccount"/>, right before quota is reserved and the download opened.
    /// </summary>
    public Func<SourceAccount, bool>? AccountIsCurrent { get; set; }

    /// <summary>How long a probe waits for a season being downloaded or unpacked before it gets a placeholder.</summary>
    internal TimeSpan ProbeArchiveGrace { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Test hook: runs in <see cref="PrepareAsync"/> after the cache check missed, before jobs are looked up.</summary>
    internal Action? AfterCacheMiss { get; set; }

    /// <summary>Downloads (including ones waiting for a download slot) and conversions running now.</summary>
    public IReadOnlyList<JobProgress> ActiveJobProgress() => _progress.Active();

    /// <summary>The last few finished movies, downloaded seasons and failures, oldest first.</summary>
    public IReadOnlyList<JobOutcome> RecentOutcomes() => _progress.RecentOutcomes();

    /// <param name="account">Whose account downloads if needed. Null for requests nobody chose to play (e.g. metadata
    /// probes): they get a placeholder unless the media is already local.</param>
    /// <param name="mayStartDownload">Asked just before a new download would start; false gives the placeholder instead.</param>
    public Task<PlaybackTarget> PrepareAsync(string entryId, int? episode, SourceAccount? account, CancellationToken ct, Func<bool>? mayStartDownload = null) =>
        PrepareCoreAsync(entryId, episode, account, null, mayStartDownload, ct);

    /// <param name="watch">Told which job or season download the request waits for, so a probe can tell when that is
    /// queued for capacity.</param>
    private async Task<PlaybackTarget> PrepareCoreAsync(
        string entryId, int? episode, SourceAccount? account, ProbeWatch? watch, Func<bool>? mayStartDownload, CancellationToken ct)
    {
        if (TryLeaseReady(entryId, episode, out var ready))
        {
            return new ReadyTarget(ready);
        }

        AfterCacheMiss?.Invoke();
        var entry = _catalog.Get(entryId) ?? throw new EntryNotFoundException($"Unknown entry {entryId}");
        if (account is not null && !IsCurrent(account))
        {
            throw Revoked();
        }

        var revoked = account is null ? CancellationToken.None : RevocationToken(account);
        var jobEpisode = episode;
        ConversionJob job;
        if (_conversions.TryGetValue(JobKey(entryId, episode), out var running))
        {
            job = running.Value;
            job.Foreground = true;
        }
        else if (entry.Kind == MediaKind.Movie && episode is null)
        {
            if (!HasLocalSource(entryId) && (account is null || mayStartDownload?.Invoke() == false))
            {
                return new PlaceholderTarget(await PlaceholderAsync(null, ct).ConfigureAwait(false));
            }

            job = GetOrStart(entry, null, true, null, j => RunMovieAsync(entry, account, revoked, j));
        }
        else
        {
            if (!HasLocalArchive(entryId, episode ?? 1) && (account is null || mayStartDownload?.Invoke() == false))
            {
                return new PlaceholderTarget(await PlaceholderAsync(null, ct).ConfigureAwait(false));
            }

            watch?.Follow(_archiveCapacity.GetOrAdd(entryId, _ => new CapacityState()), null);
            var (canonical, file, archive) = await ResolveEpisodeFileAsync(entry, episode ?? 1, account, revoked, watch, ct).ConfigureAwait(false);
            jobEpisode = episode is null ? null : canonical;

            // The season's own cache folder: it may not be the configured one if that changed since it was downloaded.
            if (TryLeaseReady(archive.Cache, entryId, jobEpisode, out ready))
            {
                StartBackfill(entry);
                return new ReadyTarget(ready);
            }

            // The requested episode first, so it streams live ahead of the rest of the season.
            job = GetOrStart(entry, jobEpisode, true, archive.Cache, j => RunFileConversionAsync(entry, jobEpisode, file, j));
            StartBackfill(entry);
        }

        watch?.Follow(job.Capacity, job.Duration.Task);
        await job.Started.Task.WaitAsync(ct).ConfigureAwait(false);
        return TryLeaseReady(job.Cache, entryId, jobEpisode, out ready)
            ? new ReadyTarget(ready)
            : new LiveTarget(job.LivePath, job.Completion);
    }

    /// <summary>
    /// For media-server probes (as opposed to playback). While a title is still being prepared, the live stream
    /// can't state its length, so the probe gets a tiny file with the right length instead.
    /// </summary>
    public async Task<PlaybackTarget> PrepareForProbeAsync(string entryId, int? episode, SourceAccount? account, CancellationToken ct, Func<bool>? mayStartDownload = null)
    {
        var watch = new ProbeWatch();

        // Not cancelled with the probe: work it started goes on, and its failure must still be reported.
        var prepare = PrepareCoreAsync(entryId, episode, account, watch, mayStartDownload, CancellationToken.None);
        var observed = false;
        PlaybackTarget target;
        try
        {
            while (!prepare.IsCompleted)
            {
                var (state, jobLength, acquisition, changed) = watch.Current();
                if (acquisition is { IsCompleted: false } && state is not { IsWaiting: true })
                {
                    // A season already on disk only needs indexing; give that a moment.
                    await Task.WhenAny(acquisition, Task.Delay(ProbeArchiveGrace, ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                if (state is { IsWaiting: true } || acquisition is { IsCompleted: false })
                {
                    // Queued behind other downloads or conversions, or the season is still downloading or unpacking:
                    // the work goes on, but the probe doesn't wait for it.
                    var known = jobLength is { IsCompletedSuccessfully: true } ? jobLength.Result : null;
                    var length = known ?? ExpectedDuration?.Invoke(entryId, episode);
                    return new PlaceholderTarget(await PlaceholderAsync(length is { TotalSeconds: >= 1 } ? length : null, ct).ConfigureAwait(false));
                }

                await Task.WhenAny(state is null ? [prepare, changed] : [prepare, changed, state.Queued]).WaitAsync(ct).ConfigureAwait(false);
            }

            observed = true;
            target = await prepare.ConfigureAwait(false);
        }
        finally
        {
            if (!observed)
            {
                // Nobody awaits the preparation any more: its failure is reported as an event instead.
                _ = prepare.ContinueWith(
                    t => ReportFailure(entryId, episode, t.Exception!.InnerException ?? t.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
        }

        if (target is PlaceholderTarget)
        {
            // Nothing will download, but the server should still record roughly how long the title is.
            return ExpectedDuration?.Invoke(entryId, episode) is { TotalSeconds: >= 1 } expected
                ? new PlaceholderTarget(await PlaceholderAsync(expected, ct).ConfigureAwait(false))
                : target;
        }

        // Found by path: an old episode link may have been resolved to the archive's own episode number.
        var running = target is LiveTarget live
            ? _conversions.Values.FirstOrDefault(l => l.IsValueCreated && l.Value.LivePath == live.Path)
            : null;
        if (running is null)
        {
            return target;
        }

        TimeSpan? duration;
        try
        {
            duration = await running.Value.Duration.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            duration = null;
        }

        return duration is { TotalSeconds: >= 1 } d
            ? new PlaceholderTarget(await PlaceholderAsync(d, ct).ConfigureAwait(false))
            : target;
    }

    /// <summary>Cheap checks before a play attempt. No network, no downloads.</summary>
    public PreflightResult Preflight(string entryId, int? episode, SourceAccount? account)
    {
        var entry = _catalog.Get(entryId);
        if (entry is null)
        {
            return new PreflightResult(PreflightStatus.NotFound, "This title is no longer in the catalog.", false, false, null);
        }

        var isArchive = entry.Kind == MediaKind.Show || episode is not null;
        if (File.Exists(_cache.FinalPath(entryId, episode)) || _conversions.ContainsKey(JobKey(entryId, episode)))
        {
            return new PreflightResult(PreflightStatus.Ok, null, false, isArchive, entry);
        }

        var local = isArchive ? HasLocalArchive(entryId, episode ?? 1) : HasLocalSource(entryId);
        if (ConversionProblem is { } conversionProblem)
        {
            return new PreflightResult(PreflightStatus.ConversionUnavailable, conversionProblem, !local, isArchive, entry);
        }

        if (local)
        {
            return new PreflightResult(PreflightStatus.Ok, null, false, isArchive, entry);
        }

        if (account is null || !IsCurrent(account))
        {
            return new PreflightResult(PreflightStatus.NoAccount, null, true, isArchive, entry);
        }

        var readiness = _source.GetReadiness(account);
        if (!readiness.Ok)
        {
            return new PreflightResult(PreflightStatus.SourceNotReady, readiness.Message, true, isArchive, entry);
        }

        if (_quota.NextAvailableAt(account.Key, DownloadLimit) is { } next)
        {
            return new PreflightResult(PreflightStatus.QuotaExhausted, null, true, isArchive, entry, next);
        }

        return new PreflightResult(PreflightStatus.Ok, null, true, isArchive, entry);
    }

    public int DownloadsUsed(SourceAccount account) => _quota.Used(account.Key);

    /// <summary>The episode numbers found in a downloaded season, or null if it hasn't been downloaded (or was evicted).</summary>
    public IReadOnlyList<int>? KnownEpisodes(string entryId) =>
        ReadEpisodeMap(_cache, entryId) is { } map ? [.. map.Keys.Order()] : null;

    /// <summary>Keeps a title out of cache eviction for <see cref="PlaybackLease"/> from now.</summary>
    public void TouchLease(string entryId)
    {
        lock (EntryLock(entryId))
        {
            _leases[entryId] = DateTime.UtcNow + PlaybackLease;
        }
    }

    /// <summary>
    /// Downloads for this provider login that haven't reached the source yet (still waiting for a slot, or about to
    /// open) fail instead of starting, whoever's they are. Downloads already transferring finish.
    /// </summary>
    [Obsolete("Cancels every user sharing the login; use RevokeOwner.")]
    public void RevokeAccount(string accountKey) => Revoke(k => k.Login == accountKey);

    /// <summary>
    /// Downloads requested with <paramref name="owner"/>'s account (<see cref="SourceAccount.Owner"/>) that haven't
    /// reached the source yet fail instead of starting; other users sharing the same login keep theirs. With
    /// <paramref name="keepGeneration"/>, work holding that (current) saving is left alone. Downloads already
    /// transferring finish. Make <see cref="AccountIsCurrent"/> reject the old snapshot before calling this: requests
    /// holding it are then refused too, even ones that only reach the download after this call.
    /// </summary>
    public void RevokeOwner(string owner, long? keepGeneration = null) =>
        Revoke(k => k.Owner == owner && k.Generation != keepGeneration);

    private void Revoke(Func<RevocationKey, bool> match)
    {
        lock (_accountLock)
        {
            foreach (var key in _revocations.Keys.Where(match).ToList())
            {
                if (_revocations.TryRemove(key, out var cts))
                {
                    cts.Cancel();
                }
            }
        }
    }

    /// <summary>
    /// One bounded pass applying the cache size limit, for when it can't wait for the next finished conversion:
    /// periodically (playback leases expire) and after the limit is lowered. Concurrent calls share one pass.
    /// Never throws.
    /// </summary>
    public Task MaintainCacheAsync()
    {
        if (Interlocked.Exchange(ref _maintaining, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                EnforceCacheLimit(null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cache maintenance failed");
            }
            finally
            {
                Interlocked.Exchange(ref _maintaining, 0);
            }
        });
    }

    /// <summary>Applies the cache size limit now. Entries in use, and <paramref name="keepEntryId"/>, stay.</summary>
    internal void EnforceCacheLimit(string? keepEntryId) => _cache.EnforceLimit(_options().MaxCacheBytes, Evictor(keepEntryId));

    /// <summary>Decides and deletes one eviction candidate: never busy entries or <paramref name="keepEntryId"/>.</summary>
    private Func<string, Func<bool>, bool> Evictor(string? keepEntryId)
    {
        var keep = keepEntryId is null ? null : DirName(keepEntryId);
        return (name, delete) =>
        {
            // Under the entry's lock, so a playback lease taken meanwhile is either seen here or sees the deletion.
            lock (_entryLocks.GetOrAdd(name, _ => new Lock()))
            {
                return name != keep && !BusyEntries().Contains(name) && delete();
            }
        };
    }

    /// <summary>Like <see cref="CacheStore.EnsureFreeSpace(long, long)"/>, first evicting idle cached titles if that frees enough.</summary>
    private void EnsureSpace(CacheStore cache, string entryId, long minFreeBytes, long needed = 0) =>
        cache.EnsureFreeSpace(minFreeBytes, needed, _options().EvictWhenLowOnSpace ? Evictor(entryId) : null);

    /// <summary>
    /// Deletes half-written files left by a previous run (files of jobs started by this instance are kept) and
    /// re-learns episode counts from the seasons already in the cache.
    /// </summary>
    public Task CleanupStaleAsync() => Task.Run(() =>
    {
        try
        {
            _cache.CleanupStale(_startedUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleaning up the media cache failed");
        }

        try
        {
            ReconcileEpisodeCounts();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking episode counts against the media cache failed");
        }
    });

    /// <summary>Cancels running jobs and waits briefly for them to stop.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        var running = _conversions.Values.Where(l => l.IsValueCreated).Select(l => l.Value.Completion)
            .Concat(_archives.Values.Where(l => l.IsValueCreated).Select(l => (Task)l.Value))
            .Concat(_backfills.Values.Where(l => l.IsValueCreated).Select(l => l.Value))
            .Concat(_placeholders.Values.Where(l => l.IsValueCreated).Select(l => (Task)l.Value))
            .ToArray();
        bool stopped;
        try
        {
            stopped = Task.WhenAll(running).Wait(ShutdownGrace);
        }
        catch (AggregateException)
        {
            stopped = true;
        }

        if (!stopped)
        {
            // Leave the rest to the garbage collector rather than pull semaphores from under running jobs.
            _logger.LogWarning("Some media jobs were still stopping at shutdown");
            return;
        }

        _shutdown.Dispose();
        foreach (var slot in _downloadSlots.Values)
        {
            slot.Dispose();
        }
    }

    private static string JobKey(string entryId, int? episode) => $"{entryId}|{episode}";

    // Per user and saving: shared logins share slots and quota, but one user's replacement cancels only their work.
    private CancellationToken RevocationToken(SourceAccount account) =>
        _revocations.GetOrAdd(new RevocationKey(account.Key, account.Owner, account.Generation), _ => new CancellationTokenSource()).Token;

    private bool IsCurrent(SourceAccount account)
    {
        try
        {
            return AccountIsCurrent?.Invoke(account) ?? true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking whether account {Account} is current failed", account.Key);
            return false;
        }
    }

    private Lock EntryLock(string entryId) => _entryLocks.GetOrAdd(DirName(entryId), _ => new Lock());

    /// <summary>Takes a playback lease, then reports whether the final file exists, atomically with eviction.</summary>
    private bool TryLeaseReady(string entryId, int? episode, out string path) => TryLeaseReady(_cache, entryId, episode, out path);

    private bool TryLeaseReady(CacheStore cache, string entryId, int? episode, out string path)
    {
        lock (EntryLock(entryId))
        {
            _leases[entryId] = DateTime.UtcNow + PlaybackLease;
            return cache.TryGetReady(entryId, episode, out path);
        }
    }

    /// <summary>Raises <see cref="IMediaEvents.MediaFailed"/> once per failure, entry and episode. Never throws.</summary>
    private void ReportFailure(string entryId, int? episode, Exception error)
    {
        if (error is OperationCanceledException && _shutdown.IsCancellationRequested)
        {
            return;
        }

        var reported = _reported.GetValue(error, _ => []);
        lock (reported)
        {
            if (!reported.Add((entryId, episode)))
            {
                return;
            }
        }

        try
        {
            _events.MediaFailed(entryId, episode, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reporting the failure of {Entry} episode {Episode} failed", entryId, episode);
        }
    }

    /// <summary>
    /// The archive's own number for a requested episode. Links made before a season part was downloaded count
    /// 1..n, while the part may number its files 6..10: a number below the archive's first is read as a position.
    /// Any other missing number is not found (a gap in 1, 3, 5 is not episode 3).
    /// </summary>
    internal static int? CanonicalEpisode(IEnumerable<int> numbers, int episode)
    {
        var sorted = numbers.Order().ToList();
        if (sorted.Contains(episode))
        {
            return episode;
        }

        return sorted.Count > 0 && episode >= 1 && episode <= sorted.Count && episode < sorted[0]
            ? sorted[episode - 1]
            : null;
    }

    /// <summary>
    /// A season's saved episode files (full paths), or null: none saved, the entry folder isn't the plugin's, or any
    /// file lies outside its extract folder. Only files there are ever converted, then deleted.
    /// </summary>
    private static Dictionary<int, string>? ReadEpisodeMap(CacheStore cache, string entryId)
    {
        if (!File.Exists(cache.EpisodeMapPath(entryId)) || !cache.OwnsEntry(entryId))
        {
            return null;
        }

        var saved = JsonFile.ReadContained<Dictionary<int, string?>>(cache.EpisodeMapPath(entryId), out _);
        if (saved is not { Count: > 0 })
        {
            return null;
        }

        var entryDir = cache.EntryDir(entryId);
        var isExtracted = ExtractedFileCheck(cache, entryId);
        var map = new Dictionary<int, string>();
        foreach (var (episode, relative) in saved)
        {
            var full = string.IsNullOrEmpty(relative) ? null : Path.GetFullPath(Path.Combine(entryDir, relative));
            if (full is null || !isExtracted(full))
            {
                return null;
            }

            map[episode] = full;
        }

        return map;
    }

    /// <summary>
    /// Whether a file lies physically inside the entry's extract folder and so inside the entry folder: neither a
    /// relative path nor a link leads out of it.
    /// </summary>
    private static Func<string, bool> ExtractedFileCheck(CacheStore cache, string entryId)
    {
        var entryDir = PathIdentity.Physical(cache.EntryDir(entryId));
        var extractDir = PathIdentity.Physical(cache.ExtractDir(entryId));
        return path =>
        {
            var physical = PathIdentity.Physical(path);
            return PathIdentity.IsBeneath(physical, extractDir) && PathIdentity.IsBeneath(physical, entryDir);
        };
    }

    private bool HasLocalSource(string entryId) => File.Exists(_cache.SourcePath(entryId)) && _cache.OwnsEntry(entryId);

    private bool HasLocalArchive(string entryId, int episode)
    {
        if (_archives.TryGetValue(entryId, out var acquiring) && !acquiring.Value.IsCompleted)
        {
            return true;
        }

        var map = ReadEpisodeMap(_cache, entryId);
        if (map is null)
        {
            // A finished download that wasn't unpacked or indexed yet; acquiring it won't download again.
            return (File.Exists(_cache.ArchivePath(entryId)) || Directory.Exists(_cache.ExtractDir(entryId))) && _cache.OwnsEntry(entryId);
        }

        return CanonicalEpisode(map.Keys, episode) is { } canonical
            && (File.Exists(_cache.FinalPath(entryId, canonical)) || File.Exists(map[canonical]));
    }

    /// <summary>The 2-second probe clip, or with <paramref name="duration"/> one that reports that length.</summary>
    private async Task<string> PlaceholderAsync(TimeSpan? duration, CancellationToken ct)
    {
        var seconds = duration is { } d ? (int)Math.Ceiling(d.TotalSeconds) : (int?)null;
        var path = seconds is { } s ? _cache.PlaceholderPath(s) : _cache.PlaceholderPath();
        for (var attempt = 0; ; attempt++)
        {
            var lazy = _placeholders.GetOrAdd(path, p => new Lazy<Task<string>>(() => Task.Run(
                async () =>
                {
                    if (File.Exists(p))
                    {
                        return p;
                    }

                    // A free conversion slot, else the reserved one: never behind downloads holding every slot.
                    using (_gate.TryEnter() ?? await _placeholderGate.EnterAsync(() => true, _shutdown.Token).ConfigureAwait(false))
                    {
                        if (!File.Exists(p))
                        {
                            _cache.EnsureRoot();
                            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                            var tmp = $"{p}.{Guid.NewGuid():N}.tmp.mp4";
                            try
                            {
                                var settings = _options().Conversion;
                                await (seconds is { } secs
                                        ? _converter.CreatePlaceholderAsync(tmp, settings, TimeSpan.FromSeconds(secs), _shutdown.Token)
                                        : _converter.CreatePlaceholderAsync(tmp, settings, _shutdown.Token))
                                    .ConfigureAwait(false);
                                File.Move(tmp, p, overwrite: true);
                            }
                            finally
                            {
                                CacheStore.TryDelete(tmp);
                            }
                        }
                    }

                    return p;
                },
                CancellationToken.None)));
            string result;
            try
            {
                result = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
            }
            catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
            {
                // Don't remember failures: the next probe tries again.
                _placeholders.TryRemove(KeyValuePair.Create(path, lazy));
                throw;
            }

            if (File.Exists(result) || attempt > 0)
            {
                return result;
            }

            // Deleted since it was made (e.g. the cache folder was cleared): make it again.
            _placeholders.TryRemove(KeyValuePair.Create(path, lazy));
        }
    }

    /// <returns>The archive's own number for <paramref name="episode"/> (see <see cref="CanonicalEpisode"/>), its
    /// audio file, and the season it came from.</returns>
    private async Task<(int Episode, string File, ArchiveMap Archive)> ResolveEpisodeFileAsync(
        SourceEntry entry,
        int episode,
        SourceAccount? account,
        CancellationToken revoked,
        ProbeWatch? watch,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (acquisition, archive) = await GetArchiveAsync(entry, account, revoked, watch, ct).ConfigureAwait(false);
            var map = archive.Files;
            if (CanonicalEpisode(map.Keys, episode) is not { } canonical)
            {
                throw new EntryNotFoundException($"Entry {entry.Id} has no episode {episode} ({map.Count} found)");
            }

            if (canonical != episode)
            {
                _logger.LogInformation("Entry {Entry} has no episode {Episode}; playing its episode {Actual} (position {Episode})", entry.Id, episode, canonical, episode);
            }

            var file = map[canonical];
            if (File.Exists(file) || File.Exists(archive.Cache.FinalPath(entry.Id, canonical)) || attempt > 0)
            {
                return (canonical, file, archive);
            }

            // Source audio was cleaned up after conversion but the converted file is gone too: fetch again. Only
            // the request that removes this exact acquisition cleans up; others join the replacement.
            lock (EntryLock(entry.Id))
            {
                if (_archives.TryRemove(KeyValuePair.Create(entry.Id, acquisition)) && archive.Cache.OwnsEntry(entry.Id))
                {
                    CacheStore.TryDelete(archive.Cache.EpisodeMapPath(entry.Id));
                    CacheStore.TryDeleteDirectory(archive.Cache.ExtractDir(entry.Id));
                }
            }
        }
    }

    /// <param name="pinned">The cache folder to work in (a season's own); null for the configured one.</param>
    private ConversionJob GetOrStart(SourceEntry entry, int? episode, bool foreground, CacheStore? pinned, Func<ConversionJob, Task> run)
    {
        var entryId = entry.Id;
        var key = JobKey(entryId, episode);
        var lazy = _conversions.GetOrAdd(key, k => new Lazy<ConversionJob>(() =>
        {
            var cache = pinned ?? _cache.Pinned();
            var job = new ConversionJob(cache, cache.LivePath(entryId, episode)) { Foreground = foreground };
            job.Completion = Task.Run(
                async () =>
                {
                    using var tracked = _progress.Start(entryId, episode, Title(entry, episode), JobPhase.Converting);
                    try
                    {
                        if (File.Exists(cache.FinalPath(entryId, episode)))
                        {
                            // A job that finished just before this one was created already cached it.
                            return;
                        }

                        await run(job).ConfigureAwait(false);
                        if (entry.Kind == MediaKind.Movie && episode is null)
                        {
                            _progress.Record(entryId, null, tracked.Title, JobOutcomeKind.MovieReady);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Preparing {Entry} episode {Episode} failed", entryId, episode);
                        RecordFailure(entryId, episode, tracked.Title, ex);

                        // Whether or not anyone still waits: a probe may have been answered and gone already.
                        ReportFailure(entryId, episode, ex);
                        job.Started.TrySetException(ex);
                        throw;
                    }
                    finally
                    {
                        _conversions.TryRemove(key, out _);
                        job.Started.TrySetResult();
                        job.Duration.TrySetResult(null);
                    }
                },
                CancellationToken.None);
            return job;
        }));
        var result = lazy.Value;
        if (foreground)
        {
            result.Foreground = true;
        }

        return result;
    }

    private async Task RunMovieAsync(SourceEntry entry, SourceAccount? account, CancellationToken revoked, ConversionJob job)
    {
        var ct = _shutdown.Token;
        var cache = job.Cache;
        var sourcePath = cache.SourcePath(entry.Id);

        // Before trusting anything already there: a folder of that name the plugin didn't make is refused.
        cache.EnsureEntryDir(entry.Id);
        if (File.Exists(sourcePath))
        {
            await RunFileConversionAsync(entry, null, sourcePath, job).ConfigureAwait(false);
            return;
        }

        string? archiveFirstFile = null;
        await WithDownloadAsync(
            entry,
            account,
            revoked,
            cache,
            job.Capacity,
            token => _gate.EnterAsync(() => job.Foreground, token, job.Capacity.Wait),
            async (media, onBytes) =>
            {
                if (media.Kind == SourceMediaKind.Archive)
                {
                    var map = await StoreArchiveAsync(entry, media, onBytes, cache, ct).ConfigureAwait(false);
                    archiveFirstFile = map.OrderBy(kv => kv.Key).First().Value;
                    return;
                }

                await DownloadAndConvertMovieAsync(entry, media, sourcePath, job, onBytes, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (archiveFirstFile is not null)
        {
            await RunFileConversionAsync(entry, null, archiveFirstFile, job).ConfigureAwait(false);
            return;
        }

        await FinalizeAsync(entry, null, sourcePath, job, ct).ConfigureAwait(false);
    }

    private async Task DownloadAndConvertMovieAsync(
        SourceEntry entry,
        SourceMedia media,
        string sourcePath,
        ConversionJob job,
        Action<int> onBytes,
        CancellationToken ct)
    {
        try
        {
            var live = PrepareLive(job, entry.Id);
            var part = sourcePath + ".part";
            var downloaded = false;
            var conversion = _converter.ConvertAsync(
                string.Empty,
                async (stdin, token) =>
                {
                    await using (var file = File.Create(part))
                    {
                        await PumpAsync(media, file, stdin, job.Cache, entry.Id, token, head => job.Duration.TrySetResult(Mp3Duration.Estimate(head, media.Length)), onBytes)
                            .ConfigureAwait(false);
                    }

                    File.Move(part, sourcePath, overwrite: true);
                    downloaded = true;
                },
                live,
                Title(entry, null),
                _options().Conversion,
                ct);
            await SignalWhenStartedAsync(conversion, job).ConfigureAwait(false);
            if (!downloaded)
            {
                throw new SourceException($"Download of {entry.Id} did not complete") { Kind = FailureKind.DownloadIncomplete };
            }
        }
        finally
        {
            CacheStore.TryDelete(sourcePath + ".part");
        }
    }

    private async Task RunFileConversionAsync(SourceEntry entry, int? episode, string input, ConversionJob job)
    {
        job.Duration.TrySetResult(Mp3Duration.FromFile(input));
        var ct = _shutdown.Token;
        using (await _gate.EnterAsync(() => job.Foreground, ct, job.Capacity.Wait).ConfigureAwait(false))
        {
            EnsureSpace(job.Cache, entry.Id, _options().MinFreeBytes);
            var live = PrepareLive(job, entry.Id);
            var conversion = _converter.ConvertAsync(input, null, live, Title(entry, episode), _options().Conversion, ct);
            await SignalWhenStartedAsync(conversion, job).ConfigureAwait(false);
        }

        await FinalizeAsync(entry, episode, input, job, ct).ConfigureAwait(false);
    }

    private static string PrepareLive(ConversionJob job, string entryId)
    {
        // Refuses a folder the plugin didn't make (or a link) before anything in it is replaced.
        job.Cache.EnsureEntryDir(entryId);
        CacheStore.TryDelete(job.LivePath);
        return job.LivePath;
    }

    private static async Task SignalWhenStartedAsync(Task conversion, ConversionJob job)
    {
        while (!conversion.IsCompleted)
        {
            if (File.Exists(job.LivePath) && new FileInfo(job.LivePath).Length > 0)
            {
                job.Started.TrySetResult();
                break;
            }

            await Task.WhenAny(conversion, Task.Delay(100)).ConfigureAwait(false);
        }

        await conversion.ConfigureAwait(false);
    }

    private async Task FinalizeAsync(SourceEntry entry, int? episode, string sourceFile, ConversionJob job, CancellationToken ct)
    {
        var cache = job.Cache;
        var live = cache.LivePath(entry.Id, episode);
        var remux = cache.RemuxPath(entry.Id, episode);
        using (await _gate.EnterAsync(() => job.Foreground, ct).ConfigureAwait(false))
        {
            cache.EnsureEntryDir(entry.Id);
            await _converter.RemuxAsync(live, remux, ct).ConfigureAwait(false);
        }

        File.Move(remux, cache.FinalPath(entry.Id, episode), overwrite: true);
        CacheStore.TryDelete(sourceFile);

        // Readers that already opened the live file keep streaming from it; remove it once they are likely done.
        _ = Task.Delay(TimeSpan.FromMinutes(10), CancellationToken.None)
            .ContinueWith(_ => CacheStore.TryDelete(live), TaskScheduler.Default);

        _logger.LogInformation("Cached {Entry} episode {Episode}", entry.Id, episode);
        _events.MediaReady(entry.Id, episode);
        try
        {
            EnforceCacheLimit(entry.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cache eviction failed");
        }
    }

    /// <summary>Cache folder names of entries being prepared, downloaded or played.</summary>
    private HashSet<string> BusyEntries()
    {
        var ids = _conversions.Keys.Concat(_backfills.Keys).Select(k => k.Split('|')[0]).ToList();
        ids.AddRange(_archives.Where(a => !a.Value.IsValueCreated || !a.Value.Value.IsCompleted).Select(a => a.Key));
        var now = DateTime.UtcNow;
        foreach (var (id, until) in _leases)
        {
            if (until > now)
            {
                ids.Add(id);
            }
            else
            {
                _leases.TryRemove(KeyValuePair.Create(id, until));
            }
        }

        return ids.Select(DirName).ToHashSet();
    }

    private string DirName(string entryId) => Path.GetFileName(_cache.EntryDir(entryId));

    /// <returns>The acquisition (to recognise it later) and the season's episode files.</returns>
    /// <param name="watch">Told about the acquisition, so a probe needn't wait for a season download.</param>
    private async Task<(Lazy<Task<ArchiveMap>> Acquisition, ArchiveMap Archive)> GetArchiveAsync(
        SourceEntry entry,
        SourceAccount? account,
        CancellationToken revoked,
        ProbeWatch? watch,
        CancellationToken ct)
    {
        Lazy<Task<ArchiveMap>>? mine = null;
        mine = new Lazy<Task<ArchiveMap>>(() => Task.Run(
            async () =>
            {
                try
                {
                    return await AcquireArchiveAsync(entry, account, revoked, _shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _archives.TryRemove(KeyValuePair.Create(entry.Id, mine!));
                    RecordFailure(entry.Id, null, DownloadTitle(entry), ex);
                    throw;
                }
            },
            CancellationToken.None));
        Lazy<Task<ArchiveMap>> lazy;
        lock (EntryLock(entry.Id))
        {
            // Same lock as stale-state cleanup, so a new acquisition never starts from files being deleted.
            lazy = _archives.GetOrAdd(entry.Id, mine);
        }

        watch?.Follow(_archiveCapacity.GetOrAdd(entry.Id, _ => new CapacityState()), null, lazy.Value);
        var archive = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        if (archive.CountsPending && ReconcileEpisodeCount(entry, archive.Files))
        {
            // Saving the counts failed when the season was acquired; a kept acquisition retries on each use.
            archive.CountsPending = false;
        }

        return (lazy, archive);
    }

    private async Task<ArchiveMap> AcquireArchiveAsync(
        SourceEntry entry,
        SourceAccount? account,
        CancellationToken revoked,
        CancellationToken ct)
    {
        var cache = _cache.Pinned();

        // Before trusting anything already there: a folder of that name the plugin didn't make is refused.
        cache.EnsureEntryDir(entry.Id);

        // A saved map: the counts may have been lost since it was saved.
        var map = ReadEpisodeMap(cache, entry.Id) ?? await RecoverArchiveAsync(entry, cache, ct).ConfigureAwait(false);
        if (map is null)
        {
            IReadOnlyDictionary<int, string> result = new Dictionary<int, string>();
            await WithDownloadAsync(
                    entry,
                    account,
                    revoked,
                    cache,
                    _archiveCapacity.GetOrAdd(entry.Id, _ => new CapacityState()),
                    null,
                    async (media, onBytes) => result = await StoreArchiveAsync(entry, media, onBytes, cache, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
            _progress.Record(entry.Id, null, DownloadTitle(entry), JobOutcomeKind.SeasonDownloaded);
            map = result;
        }

        // Its episodes are converted into this same folder, even if the configured cache folder changes meanwhile.
        return new ArchiveMap(map, cache) { CountsPending = !ReconcileEpisodeCount(entry, map) };
    }

    /// <summary>Uses a season that was downloaded before but not fully unpacked or indexed, instead of downloading again.</summary>
    private async Task<IReadOnlyDictionary<int, string>?> RecoverArchiveAsync(SourceEntry entry, CacheStore cache, CancellationToken ct)
    {
        var archive = cache.ArchivePath(entry.Id);
        if (File.Exists(archive))
        {
            _logger.LogInformation("Unpacking the already downloaded archive of {Entry}", entry.Id);
            try
            {
                return await UnpackArchiveAsync(entry, archive, null, cache, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "The downloaded archive of {Entry} is damaged; downloading again", entry.Id);
                CacheStore.TryDelete(archive);
                return null;
            }
        }

        var extracted = cache.ExtractDir(entry.Id);
        if (Directory.Exists(extracted))
        {
            // Only ever renamed into place once complete, and removed whenever its files are known to be partial.
            if (Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).Any(EpisodeMapper.IsAudio))
            {
                return CommitEpisodeMap(entry, cache);
            }

            CacheStore.TryDeleteDirectory(extracted);
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<int, string>> StoreArchiveAsync(
        SourceEntry entry,
        SourceMedia media,
        Action<int> onBytes,
        CacheStore cache,
        CancellationToken ct)
    {
        var archive = cache.ArchivePath(entry.Id);
        var part = archive + ".part";
        try
        {
            await using (var file = File.Create(part))
            {
                await PumpAsync(media, file, null, cache, entry.Id, ct, null, onBytes).ConfigureAwait(false);
            }

            File.Move(part, archive, overwrite: true);
        }
        finally
        {
            CacheStore.TryDelete(part);
        }

        return await UnpackArchiveAsync(entry, archive, media.FileName, cache, ct).ConfigureAwait(false);
    }

    /// <summary>Unpacks a complete download into the extract folder, records its episodes, then deletes it.</summary>
    private async Task<IReadOnlyDictionary<int, string>> UnpackArchiveAsync(
        SourceEntry entry,
        string archive,
        string? fileName,
        CacheStore cache,
        CancellationToken ct)
    {
        var extractDir = cache.ExtractDir(entry.Id);
        if (IsZip(archive))
        {
            var minFree = _options().MinFreeBytes;
            await ArchiveExtractor.ExtractAudioAsync(
                    archive,
                    extractDir,
                    MaxArchiveEntries,
                    MaxArchiveBytes,
                    needed => EnsureSpace(cache, entry.Id, minFree, needed),
                    ct)
                .ConfigureAwait(false);
            var map = CommitEpisodeMap(entry, cache);
            CacheStore.TryDelete(archive);
            return map;
        }

        // Not an archive after all: treat the single file as episode 1.
        var tmp = extractDir + ".tmp";
        CacheStore.TryDeleteDirectory(tmp);
        Directory.CreateDirectory(tmp);
        File.Move(archive, Path.Combine(tmp, "01" + (Path.GetExtension(fileName) is { Length: > 0 } ext ? ext : ".mp3")), overwrite: true);
        CacheStore.TryDeleteDirectory(extractDir);
        Directory.Move(tmp, extractDir);
        return CommitEpisodeMap(entry, cache);
    }

    private IReadOnlyDictionary<int, string> CommitEpisodeMap(SourceEntry entry, CacheStore cache)
    {
        var isExtracted = ExtractedFileCheck(cache, entry.Id);
        var map = EpisodeMapper.Map(Directory.EnumerateFiles(cache.ExtractDir(entry.Id), "*", SearchOption.AllDirectories)
            .Where(isExtracted));
        if (map.Count == 0)
        {
            throw new SourceException($"Download of {entry.Id} contained no audio files") { Kind = FailureKind.NoAudio };
        }

        var entryDir = cache.EntryDir(entry.Id);
        JsonFile.Write(cache.EpisodeMapPath(entry.Id), map.ToDictionary(kv => kv.Key, kv => Path.GetRelativePath(entryDir, kv.Value)));
        _logger.LogInformation("Entry {Entry} contains {Count} episodes", entry.Id, map.Count);
        return map;
    }

    /// <summary>
    /// Records a season's episodes as found in its archive, announcing them when that is news. Never throws.
    /// False when they couldn't be saved.
    /// </summary>
    private bool ReconcileEpisodeCount(SourceEntry entry, IReadOnlyDictionary<int, string> map)
    {
        if (entry.Kind != MediaKind.Show || map.Count == 0)
        {
            return true;
        }

        bool changed;
        try
        {
            changed = _episodeCounts.Set(entry.Id, new EpisodeCount(map.Count, EpisodeCountOrigin.Archive, [.. map.Keys.Order()]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not recorded; the next use of the season tries again.
            _logger.LogWarning(ex, "Could not save the episode count of {Entry}", entry.Id);
            return false;
        }

        if (changed)
        {
            _events.EpisodesDiscovered(entry.Id, map.Count);
        }

        return true;
    }

    /// <summary>Re-learns episode counts from the seasons in the cache, e.g. after the counts file was lost.</summary>
    private void ReconcileEpisodeCounts()
    {
        foreach (var catalogEntry in _catalog.All())
        {
            if (catalogEntry.Entry.Kind == MediaKind.Show && ReadEpisodeMap(_cache, catalogEntry.Entry.Id) is { } map)
            {
                ReconcileEpisodeCount(catalogEntry.Entry, map);
            }
        }
    }

    private void StartBackfill(SourceEntry entry)
    {
        Lazy<Task>? mine = null;
        mine = new Lazy<Task>(() => Task.Run(
            async () =>
            {
                try
                {
                    var (_, archive) = await GetArchiveAsync(entry, null, CancellationToken.None, null, _shutdown.Token).ConfigureAwait(false);
                    foreach (var (ep, file) in archive.Files.OrderBy(kv => kv.Key))
                    {
                        if (_shutdown.IsCancellationRequested || archive.Cache.TryGetReady(entry.Id, ep, out _) || !File.Exists(file))
                        {
                            continue;
                        }

                        try
                        {
                            await GetOrStart(entry, ep, false, archive.Cache, j => RunFileConversionAsync(entry, ep, file, j)).Completion
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Background conversion of {Entry} episode {Episode} failed", entry.Id, ep);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background conversion of {Entry} stopped", entry.Id);
                }
                finally
                {
                    _backfills.TryRemove(KeyValuePair.Create(entry.Id, mine!));
                }
            },
            CancellationToken.None));
        _ = _backfills.GetOrAdd(entry.Id, mine).Value;
    }

    /// <param name="beforeOpen">Runs once the account's download slot is held, before anything is reserved or opened.</param>
    private async Task WithDownloadAsync(
        SourceEntry entry,
        SourceAccount? account,
        CancellationToken revoked,
        CacheStore cache,
        CapacityState capacity,
        Func<CancellationToken, Task<IDisposable>>? beforeOpen,
        Func<SourceMedia, Action<int>, Task> use)
    {
        if (account is null)
        {
            throw new SourceUnavailableException("No account available to download this title.") { Kind = FailureKind.NoAccount };
        }

        // Nothing downloaded could become playable; don't spend the account's allowance on it.
        EnsureCanConvert();
        var ct = _shutdown.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, revoked);
        using var tracked = _progress.Start(entry.Id, null, DownloadTitle(entry), JobPhase.Waiting);
        var slot = _downloadSlots.GetOrAdd(account.Key, _ => new SemaphoreSlim(Math.Max(1, _source.Limits.MaxConcurrentDownloads)));
        try
        {
            if (!slot.Wait(0))
            {
                using (capacity.Wait())
                {
                    await slot.WaitAsync(linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Revoked();
        }

        IDisposable? held = null;
        try
        {
            try
            {
                held = beforeOpen is null ? null : await beforeOpen(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (revoked.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw Revoked();
            }

            EnsureSpace(cache, entry.Id, _options().MinFreeBytes);
            EnsureCanConvert();
            lock (_accountLock)
            {
                // Atomic with RevokeAccount: a snapshot replaced or removed by now reserves nothing.
                if (revoked.IsCancellationRequested || !IsCurrent(account))
                {
                    throw Revoked();
                }

                if (!_quota.TryAcquire(entry.Id, account.Key, DownloadLimit))
                {
                    throw new QuotaExceededException(DownloadLimit);
                }
            }

            tracked.Phase = JobPhase.Downloading;

            _logger.LogInformation("Downloading {Entry} ({Name}) with account {Account}", entry.Id, entry.RawName, account.Key);
            SourceMedia media;
            try
            {
                media = await _source.OpenAsync(entry.Id, account, linked.Token).ConfigureAwait(false);
            }
            catch (DownloadNotStartedException)
            {
                // Only the source can be sure nothing reached the site; anything else may have been counted.
                _quota.Refund(entry.Id, account.Key);
                throw;
            }
            catch (OperationCanceledException) when (revoked.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw Revoked();
            }

            tracked.SetTotal(media.Length);
            await using (media.ConfigureAwait(false))
            {
                await use(media, tracked.AddBytes).ConfigureAwait(false);
            }
        }
        finally
        {
            held?.Dispose();
            slot.Release();
        }
    }

    private void EnsureCanConvert()
    {
        if (ConversionProblem is { } problem)
        {
            throw new ConversionUnavailableException($"Media can't be converted on this server, so nothing was downloaded. {problem}");
        }
    }

    private static SourceUnavailableException Revoked() =>
        new("The account was removed or replaced before this download started. Sign in again and retry.");

    /// <param name="onHead">Called once with the first <see cref="Mp3Duration.HeadSize"/> bytes (or all of a smaller file).</param>
    private async Task PumpAsync(
        SourceMedia media,
        Stream file,
        Stream? tee,
        CacheStore cache,
        string entryId,
        CancellationToken ct,
        Action<byte[]>? onHead,
        Action<int> onBytes)
    {
        var buffer = new byte[128 * 1024];
        var head = onHead is null ? null : new MemoryStream();
        var timeout = DownloadInactivityTimeout;
        var floor = _options().MinFreeBytes / 2;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        long sinceSpaceCheck = 0;
        while (true)
        {
            int read;
            stall.CancelAfter(timeout);
            try
            {
                read = await media.Content.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException
                                       && stall.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new SourceException($"Download stalled: nothing received for {timeout.TotalSeconds:0} seconds", ex) { Kind = FailureKind.DownloadIncomplete };
            }

            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            onBytes(read);
            if ((sinceSpaceCheck += read) >= SpaceCheckInterval)
            {
                sinceSpaceCheck = 0;
                EnsureSpace(cache, entryId, floor);
            }

            if (head is not null)
            {
                head.Write(buffer, 0, (int)Math.Min(read, Mp3Duration.HeadSize - head.Length));
                if (head.Length >= Mp3Duration.HeadSize)
                {
                    onHead!(head.ToArray());
                    head = null;
                }
            }

            if (tee is not null)
            {
                try
                {
                    await tee.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // ffmpeg exited early; keep downloading so the source is cached for a retry.
                    tee = null;
                }
            }
        }

        if (head is not null)
        {
            onHead!(head.ToArray());
        }

        if (media.Length is { } expected && expected != total)
        {
            throw new SourceException($"Download truncated: got {total} of {expected} bytes") { Kind = FailureKind.DownloadIncomplete };
        }
    }

    private static bool IsZip(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var f = File.OpenRead(path);
        return f.Read(magic) == 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4;
    }

    private static string DownloadTitle(SourceEntry entry) =>
        entry.Kind == MediaKind.Show ? $"{entry.Title} season {entry.Season ?? 1}" : entry.Title;

    private void RecordFailure(string entryId, int? episode, string title, Exception ex)
    {
        if (ex is not OperationCanceledException)
        {
            _progress.Record(entryId, episode, title, JobOutcomeKind.Failed, ex.Message, FailureInfo.From(ex));
        }
    }

    private static string Title(SourceEntry entry, int? episode) =>
        episode is { } e ? $"{entry.Title} S{entry.Season ?? 1:00}E{e:00}" : entry.Title;

    private sealed class ConversionJob(CacheStore cache, string livePath)
    {
        private volatile bool _foreground;

        /// <summary>Paths for this job, fixed when it started even if the cache folder setting changes.</summary>
        public CacheStore Cache { get; } = cache;

        public string LivePath { get; } = livePath;

        /// <summary>Someone is waiting to play this (as opposed to a background conversion of the rest of a season).</summary>
        public bool Foreground
        {
            get => _foreground;
            set => _foreground = value;
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the job is queued for a download or conversion slot.</summary>
        public CapacityState Capacity { get; } = new();

        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>The finished file's length, known shortly after the source starts arriving (null if it can't be worked out).</summary>
        public TaskCompletionSource<TimeSpan?> Duration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RevocationKey(string Login, string? Owner, long Generation);

    /// <summary>A season's episode files, and the cache folder they (and their conversions) belong in.</summary>
    private sealed class ArchiveMap(IReadOnlyDictionary<int, string> files, CacheStore cache)
    {
        private volatile bool _countsPending;

        public IReadOnlyDictionary<int, string> Files { get; } = files;

        public CacheStore Cache { get; } = cache;

        /// <summary>Its episode count couldn't be saved yet.</summary>
        public bool CountsPending
        {
            get => _countsPending;
            set => _countsPending = value;
        }
    }

    /// <summary>Whether work is waiting for a download or conversion slot; probes don't wait behind such work.</summary>
    private sealed class CapacityState
    {
        private readonly Lock _lock = new();
        private int _waiting;
        private TaskCompletionSource _queued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsWaiting
        {
            get
            {
                lock (_lock)
                {
                    return _waiting > 0;
                }
            }
        }

        /// <summary>Completes when the work is queued: at once if it is now, else the next time it queues.</summary>
        public Task Queued
        {
            get
            {
                lock (_lock)
                {
                    return _queued.Task;
                }
            }
        }

        /// <summary>Marks the work as queued until disposed.</summary>
        public IDisposable Wait()
        {
            lock (_lock)
            {
                _waiting++;
                _queued.TrySetResult();
            }

            return new Release(this);
        }

        private sealed class Release(CapacityState owner) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) != 0)
                {
                    return;
                }

                lock (owner._lock)
                {
                    if (--owner._waiting == 0)
                    {
                        owner._queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
        }
    }

    /// <summary>What a probe's preparation currently waits for: a season download, then the episode's conversion.</summary>
    private sealed class ProbeWatch
    {
        private readonly Lock _lock = new();
        private CapacityState? _state;
        private Task<TimeSpan?>? _duration;
        private Task? _acquisition;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="acquisition">The season download (and unpacking) waited for, if any.</param>
        public void Follow(CapacityState state, Task<TimeSpan?>? duration, Task? acquisition = null)
        {
            TaskCompletionSource previous;
            lock (_lock)
            {
                (_state, _duration, _acquisition, previous) = (state, duration, acquisition, _changed);
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            previous.TrySetResult();
        }

        /// <returns>The state followed now, its job's length when known, the season acquisition waited for, and a
        /// task completing when that changes.</returns>
        public (CapacityState? State, Task<TimeSpan?>? Duration, Task? Acquisition, Task Changed) Current()
        {
            lock (_lock)
            {
                return (_state, _duration, _acquisition, _changed.Task);
            }
        }
    }
}
