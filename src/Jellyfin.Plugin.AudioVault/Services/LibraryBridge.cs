using System.Collections.Concurrent;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Connects Core's library and media events to Jellyfin's library manager.</summary>
public sealed class LibraryBridge(
    LibrarySyncService librarySync,
    EpisodeCountStore episodeCounts,
    PluginSettings settings,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    ISeasonEpisodeLookup seasonLookup,
    IFileSystem fileSystem,
    UserNotifier notifier,
    SoftFailTracker softFails,
    PlaybackIntents intents,
    LibraryScans scans,
    ILogger<LibraryBridge> logger) : IMediaEvents
{
    private static readonly TimeSpan LookupSpacing = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _passLock = new(1, 1);
    private readonly EpisodeListAttempts _attempts = new();
    private int _syncQueued;
    private DateTimeOffset _lastLookup;
    private Dictionary<string, PendingSeason[]> _pendingBySeries = [];

    // Shows of pending seasons Jellyfin hadn't identified at the last pass; null before the first pass.
    private ConcurrentDictionary<string, byte>? _unidentified;

    public IReadOnlyList<PendingSeason> PendingSeasons { get; private set; } = [];

    /// <summary>What the last library update couldn't do, or null.</summary>
    public string? LibraryProblem { get; private set; }

    /// <summary>Single-part seasons in the library, the ones an episode list is looked up for.</summary>
    public int ListableSeasons { get; private set; }

    public async Task<LibrarySyncResult> SyncLibraryAsync(bool scan, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = librarySync.Sync(settings.Library(), settings.StreamUrl, EntryOf);
            LibraryProblem = LibraryRules.Problem(result);
            PendingSeasons = result.PendingSeasons;
            ListableSeasons = result.ListableSeasons;
            _pendingBySeries = result.PendingSeasons
                .GroupBy(SeriesPath, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            if (scan && result.Changed)
            {
                libraryManager.QueueLibraryScan();
            }

            return result;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void MediaReady(string entryId, int? episode)
    {
        notifier.Unwatch(entryId, episode);
        var path = librarySync.FindStrmPath(entryId, episode);
        if (path is null || libraryManager.FindByPath(path, false) is not { } item)
        {
            return;
        }

        // The first-play probe saw a growing file without a duration; re-probe the finished MP4.
        providerManager.QueueRefresh(
            item.Id,
            new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                EnableRemoteContentProbe = true,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            },
            RefreshPriority.High);
    }

    /// <summary>
    /// A job failed, possibly after its players got a placeholder and their requests returned. The attempts that
    /// waited for it authorise no further downloads (a later transcoder fetch would start another one), and the
    /// failure is explained once the waiting player has closed; a dialog over a running player fights it for focus.
    /// </summary>
    public void MediaFailed(string entryId, int? episode, Exception error)
    {
        try
        {
            var retired = intents.RetireFailed(entryId, episode);
            logger.LogDebug("Preparing {Entry} episode {Episode} failed; retired {Count} play intents", entryId, episode, retired);
            softFails.ExplainFailure(notifier.TakeWatcher(entryId, episode), entryId, episode, UserMessages.Describe(error));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not handle the failure of {Entry}", entryId);
        }
    }

    public void EpisodesDiscovered(string entryId, int count)
    {
        if (Interlocked.Exchange(ref _syncQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Interlocked.Exchange(ref _syncQueued, 0);
            try
            {
                await SyncLibraryAsync(scan: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Library sync after episode discovery failed");
            }
        });
    }

    /// <summary>
    /// One pass over the seasons still waiting for an episode list; when any was found, the library is written again
    /// and scanned. Only one pass runs at a time, and none next to a library scan: a pass waits for a running scan,
    /// and pauses between lookups when one starts. False when an automatic pass was skipped because one is running.
    /// </summary>
    /// <param name="automatic">
    /// Automatic passes skip seasons whose lookup found nothing recently (unless their show was identified differently
    /// since) and don't wait for a running pass; a pass started by hand or by the daily trigger looks up all of them.
    /// </param>
    public async Task<bool> RunEpisodeListPassAsync(bool automatic, IProgress<double>? progress, CancellationToken ct)
    {
        if (automatic)
        {
            if (!await _passLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return false;
            }
        }
        else
        {
            await _passLock.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            if (await scans.WaitAsync(ct).ConfigureAwait(false))
            {
                logger.LogInformation("The episode-list pass waited for a library scan to finish");
            }

            if (librarySync.IndexedFiles == 0)
            {
                if (automatic)
                {
                    // The startup library update hasn't run yet; nothing is known to be pending.
                    return true;
                }

                await SyncLibraryAsync(scan: false, ct).ConfigureAwait(false);
            }

            var resolved = await ResolveEpisodeCountsAsync(automatic, progress, ct).ConfigureAwait(false);
            if (resolved > 0)
            {
                await SyncLibraryAsync(scan: true, ct).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _passLock.Release();
        }
    }

    public bool EpisodeListPassRunning => _passLock.CurrentCount == 0;

    /// <summary>When the earliest failed lookup is due again; the scheduler starts a pass then.</summary>
    public DateTimeOffset? NextFailedRetry => _attempts.NextFailedRetry();

    /// <summary>Counts for the status page, from the last library sync and the last pass; no lookups.</summary>
    public EpisodeListStatus EpisodeLists()
    {
        var unidentified = _unidentified;
        var waiting = 0;
        var waitingForShow = 0;
        var tried = 0;
        var failed = 0;
        foreach (var season in PendingSeasons)
        {
            // A pass may have found it since the last sync wrote the files.
            if (episodeCounts.Get(season.EntryId) is not null)
            {
                continue;
            }

            waiting++;
            if (unidentified?.ContainsKey(SeriesPath(season)) == true)
            {
                waitingForShow++;
            }
            else if (_attempts.Failed(season.EntryId))
            {
                failed++;
            }
            else if (_attempts.Tried(season.EntryId))
            {
                tried++;
            }
        }

        var seasons = Math.Max(ListableSeasons, waiting);
        return new EpisodeListStatus(seasons, seasons - waiting, waiting, unidentified is null ? null : waitingForShow, EpisodeListPassRunning, tried, failed);
    }

    /// <summary>
    /// A show in the library changed. True when it has pending seasons an automatic pass would now look up, such as
    /// when Jellyfin has just identified it on TMDb.
    /// </summary>
    public bool SeriesUpdated(string seriesPath, string? tmdbId, string? displayOrder)
    {
        if (EpisodeListRules.ParseTmdbId(tmdbId) is null || !_pendingBySeries.TryGetValue(seriesPath, out var seasons))
        {
            return false;
        }

        _unidentified?.TryRemove(seriesPath, out _);
        var key = EpisodeListRules.LookupKey(tmdbId, displayOrder);
        var now = DateTimeOffset.UtcNow;
        return seasons.Any(s => episodeCounts.Get(s.EntryId) is null && _attempts.IsDue(s.EntryId, key, now));
    }

    /// <summary>Learns how many episodes pending seasons have: TMDb's season list in one request, else Jellyfin's providers.</summary>
    private async Task<int> ResolveEpisodeCountsAsync(bool automatic, IProgress<double>? progress, CancellationToken ct)
    {
        var pending = PendingSeasons;
        var unidentified = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        int resolved = 0, lookedUp = 0, recent = 0, failed = 0, changed = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(100.0 * i / Math.Max(1, pending.Count));
            var season = pending[i];
            if (episodeCounts.Get(season.EntryId) is not null)
            {
                continue;
            }

            var seriesPath = SeriesPath(season);
            var series = libraryManager.FindByPath(seriesPath, true) as Series;
            var tmdbId = series?.GetProviderId(MetadataProvider.Tmdb);
            var otherIds = series is null ? null : EpisodeListRules.OtherIds(series.ProviderIds, nameof(MetadataProvider.Tmdb));
            var method = EpisodeListRules.Choose(series is not null, tmdbId, otherIds is not null, series?.DisplayOrder);
            if (series is null || method == EpisodeListMethod.WaitForIdentification)
            {
                // Not an attempt: the next pass after Jellyfin identifies the show tries it.
                unidentified[seriesPath] = 0;
                continue;
            }

            var key = EpisodeListRules.ShowKey(series.ProviderIds, series.DisplayOrder);
            if (automatic && !_attempts.IsDue(season.EntryId, key, DateTimeOffset.UtcNow))
            {
                recent++;
                continue;
            }

            lookedUp++;
            try
            {
                await scans.WaitAsync(ct).ConfigureAwait(false);
                IReadOnlyList<int>? numbers = null;
                if (method == EpisodeListMethod.SeasonList)
                {
                    await ThrottleAsync(ct).ConfigureAwait(false);
                    numbers = await seasonLookup.SeasonEpisodesAsync(
                            EpisodeListRules.ParseTmdbId(tmdbId)!.Value,
                            season.Season,
                            series.GetPreferredMetadataLanguage(),
                            series.GetPreferredMetadataCountryCode(),
                            ct)
                        .ConfigureAwait(false);
                }

                // Null: no TMDb id, another display order, or Jellyfin's TMDb client can't be used. The count is null
                // when the show was identified differently meanwhile.
                numbers ??= await CountEpisodesAsync(series, season.Season, key, ct).ConfigureAwait(false);

                // A scan that started meanwhile may be re-identifying the show: wait for it, then keep the answer only
                // if the show is still the one it was looked up for.
                await scans.WaitAsync(ct).ConfigureAwait(false);
                if (numbers is null || !SameShow(series, key))
                {
                    changed++;
                    logger.LogDebug("{Series} was identified differently during its lookup; season {Season} is looked up again", series.Name, season.Season);
                    continue;
                }

                if (EpisodeListRules.ToCount(numbers) is { } count)
                {
                    _attempts.Forget(season.EntryId);
                    if (episodeCounts.Set(season.EntryId, count))
                    {
                        resolved++;
                    }
                }
                else
                {
                    _attempts.Record(season.EntryId, key, DateTimeOffset.UtcNow + EpisodeListRules.RetryNothingFound, failed: false);
                }
            }
            catch (Exception ex) when (EpisodeCounter.IsFailure(ex, ct))
            {
                failed++;
                _attempts.Record(season.EntryId, key, DateTimeOffset.UtcNow + EpisodeListRules.RetryFailed, failed: true);
                logger.LogDebug(ex, "Episode lookup failed for {Series} season {Season}", series.Name, season.Season);
            }
        }

        _unidentified = unidentified;
        logger.LogInformation(
            "Resolved episode lists for {Resolved} of {Pending} seasons ({LookedUp} looked up, {Failed} failed, {Changed} shows identified differently meanwhile, {Unidentified} shows not identified yet, {Recent} tried recently)",
            resolved,
            pending.Count,
            lookedUp,
            failed,
            changed,
            unidentified.Count,
            recent);
        if (failed > 0)
        {
            logger.LogWarning("Episode-list lookups failed for {Failed} seasons; they are tried again in an hour. Debug logging has the errors", failed);
        }

        return resolved;
    }

    /// <summary>
    /// Jellyfin's remote providers for episodes, asked directly: the provider manager's search hides their errors as
    /// empty results, which would end the season early. Null when the show was identified differently meanwhile.
    /// </summary>
    private Task<IReadOnlyList<int>?> CountEpisodesAsync(Series series, int season, string key, CancellationToken ct)
    {
        var sources = providerManager.GetMetadataProviders<Episode>(series, libraryManager.GetLibraryOptions(series))
            .OfType<IRemoteSearchProvider<EpisodeInfo>>()
            .Select(p => new EpisodeSource(p.Name, async (ep, token) =>
            {
                var results = await p.GetSearchResults(
                        new EpisodeInfo
                        {
                            SeriesProviderIds = new Dictionary<string, string>(series.ProviderIds),
                            SeriesDisplayOrder = series.DisplayOrder,
                            ParentIndexNumber = season,
                            IndexNumber = ep,
                            MetadataLanguage = series.GetPreferredMetadataLanguage(),
                            MetadataCountryCode = series.GetPreferredMetadataCountryCode(),
                        },
                        token)
                    .ConfigureAwait(false);
                return results?.Any() == true;
            }))
            .ToList();

        return EpisodeCounter.CountAsync(
            async (ep, token) =>
            {
                await ThrottleAsync(token).ConfigureAwait(false);
                return await EpisodeCounter.AnyHasAsync(sources, ep, token).ConfigureAwait(false);
            },
            scans.WaitAsync,
            () => SameShow(series, key),
            ct);
    }

    private bool SameShow(Series series, string key) =>
        libraryManager.GetItemById(series.Id) is Series current && EpisodeListRules.ShowKey(current.ProviderIds, current.DisplayOrder) == key;

    /// <summary>Spaces lookups out; Jellyfin's cached answers come back quicker than this anyway.</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        var wait = _lastLookup + LookupSpacing - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        _lastLookup = DateTimeOffset.UtcNow;
    }

    private string SeriesPath(PendingSeason season) => Path.Combine(settings.ShowsPath, season.SeriesRelativePath);

    private static (string EntryId, int? Episode)? EntryOf(string link) =>
        Api.PlaybackIntentFilter.TryParse(link, out var entryId, out var episode) ? (entryId, episode) : null;
}

/// <summary>How the outcome of a library update is described on the settings page.</summary>
public static class LibraryRules
{
    public static string? Problem(LibrarySyncResult result)
    {
        var problems = new List<string>();
        if (result.Skipped > 0)
        {
            problems.Add(
                $"{N(result.Skipped)} library files weren't removed although the AudioVault catalogue no longer lists them for the library: "
                + "removing that many at once is held back in case the catalogue is incomplete or wrong. Run a full catalog sync; the Jellyfin log has details.");
        }

        if (result.Failed > 0)
        {
            problems.Add($"{N(result.Failed)} library files couldn't be written or deleted; the next library update tries again. The Jellyfin log has details.");
        }

        return problems.Count == 0 ? null : string.Join(' ', problems);
    }

    /// <summary>
    /// What keeps a library update from fully applying new settings, or null when it did: files that couldn't be written
    /// or deleted, and removals the safety rail held back, both need another try.
    /// </summary>
    public static string? Unfinished(LibrarySyncResult result)
    {
        var parts = new List<string>();
        if (result.Failed > 0)
        {
            parts.Add($"{N(result.Failed)} library files couldn't be written or deleted");
        }

        if (result.Skipped > 0)
        {
            parts.Add($"{N(result.Skipped)} library files weren't removed because the safety rail held them back");
        }

        return parts.Count == 0 ? null : string.Join(", and ", parts);
    }

    private static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
