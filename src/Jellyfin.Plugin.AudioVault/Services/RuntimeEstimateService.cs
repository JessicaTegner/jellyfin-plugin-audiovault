using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Jellyfin learns runtimes only by probing media, and our titles aren't downloaded until played. This fills in the
/// runtime from TMDb for items that have none (or the placeholder's ~2 s), and tells probes of titles that won't
/// download how long their placeholder should be. A real probe of the cached file always wins.
/// </summary>
public sealed class RuntimeEstimateService(
    ILibraryManager libraryManager,
    LibrarySyncService librarySync,
    MediaJobManager jobs,
    IRuntimeLookup lookup,
    ILogger<RuntimeEstimateService> logger) : IHostedService
{
    private static readonly TimeSpan LookupSpacing = TimeSpan.FromMilliseconds(250);

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    // Keyed by .strm path, and by folder for movies (all versions of a movie share its runtime).
    private readonly ConcurrentDictionary<string, TimeSpan> _estimates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTimeOffset _lastLookup;
    private readonly CancellationTokenSource _stopping = new();
    private Task _worker = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!lookup.Available)
        {
            return Task.CompletedTask;
        }

        jobs.ExpectedDuration = ExpectedDuration;
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemUpdated += OnItemChanged;
        _worker = Task.Run(WorkAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemUpdated -= OnItemChanged;
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fills in runtimes of every existing AudioVault movie and episode that has none.</summary>
    public async Task<int> FillAllAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (!lookup.Available)
        {
            return 0;
        }

        var items = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false,
            })
            .Where(i => IsOurs(i) && !RuntimeRules.HasRealRuntime(i.RunTimeTicks))
            .ToList();

        var filled = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / items.Count);
            try
            {
                if (await FillAsync(items[i], ct).ConfigureAwait(false))
                {
                    filled++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not fill in the runtime of {Item}", items[i].Path);
            }
        }

        logger.LogInformation("Filled in runtimes of {Filled} of {Total} items without one", filled, items.Count);
        return filled;
    }

    /// <summary>Probe placeholder length for an entry: its item's real runtime, else the TMDb estimate, else unknown.</summary>
    public TimeSpan? ExpectedDuration(string entryId, int? episode)
    {
        try
        {
            if (librarySync.FindStrmPath(entryId, episode) is not { } path)
            {
                return null;
            }

            var item = libraryManager.FindByPath(path, false);
            if (RuntimeRules.ProbeLength(item?.RunTimeTicks, _estimates.TryGetValue(path, out var known) ? known : null) is { } length)
            {
                return length;
            }

            return episode is null ? FolderEstimate(path) : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No expected length for {Entry} episode {Episode}", entryId, episode);
            return null;
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is { } item && IsOurs(item) && !RuntimeRules.HasRealRuntime(item.RunTimeTicks) && _queued.TryAdd(item.Id, 0))
            {
                _queue.Writer.TryWrite(item.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check the runtime of a changed item");
        }
    }

    private async Task WorkAsync()
    {
        var ct = _stopping.Token;
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _queued.TryRemove(id, out _);
                try
                {
                    if (libraryManager.GetItemById(id) is { } item)
                    {
                        await FillAsync(item, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not fill in the runtime of item {Item}", id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> FillAsync(BaseItem item, CancellationToken ct)
    {
        if (!IsOurs(item) || RuntimeRules.HasRealRuntime(item.RunTimeTicks))
        {
            return false;
        }

        var estimate = await EstimateAsync(item, ct).ConfigureAwait(false);
        if (estimate is null)
        {
            return false;
        }

        _estimates[item.Path] = estimate.Value;

        // The lookup took a moment; a re-probe of freshly cached media may have set the real runtime meanwhile.
        var current = libraryManager.GetItemById(item.Id) ?? item;
        if (RuntimeRules.ToApply(current.RunTimeTicks, estimate) is not { } ticks)
        {
            return false;
        }

        current.RunTimeTicks = ticks;
        // Our own save raises ItemUpdated again; the runtime is then no longer empty, so nothing is queued.
        await current.UpdateToRepositoryAsync(ItemUpdateType.None, ct).ConfigureAwait(false);
        logger.LogDebug("Estimated runtime of {Item}: {Runtime}", current.Path, estimate);
        return true;
    }

    private async Task<TimeSpan?> EstimateAsync(BaseItem item, CancellationToken ct)
    {
        var language = item.GetPreferredMetadataLanguage();
        var country = item.GetPreferredMetadataCountryCode();
        switch (item)
        {
            case Movie movie:
            {
                var folder = Path.GetDirectoryName(movie.Path);
                if (folder is not null && _estimates.TryGetValue(folder, out var shared))
                {
                    return shared;
                }

                if ((TmdbId(movie) ?? TmdbId(Owner(movie))) is not { } id)
                {
                    return null;
                }

                await ThrottleAsync(ct).ConfigureAwait(false);
                var runtime = await lookup.MovieAsync(id, language, country, ct).ConfigureAwait(false);
                if (runtime is { } r && folder is not null)
                {
                    _estimates[folder] = r;
                }

                return runtime;
            }

            case Episode episode:
            {
                var series = episode.Series;
                var season = episode.ParentIndexNumber ?? episode.Season?.IndexNumber;
                if (TmdbId(series) is not { } id || season is null || episode.IndexNumber is not { } number)
                {
                    return null;
                }

                await ThrottleAsync(ct).ConfigureAwait(false);
                return await lookup.EpisodeAsync(id, season.Value, number, series!.DisplayOrder ?? string.Empty, language, country, ct)
                    .ConfigureAwait(false);
            }

            default:
                return null;
        }
    }

    /// <summary>Alternate versions of a movie may not carry provider ids themselves.</summary>
    private BaseItem? Owner(Movie movie)
    {
        if (movie.PrimaryVersionId is { } primary && libraryManager.GetItemById(primary) is { } p)
        {
            return p;
        }

        return movie.OwnerId != Guid.Empty ? libraryManager.GetItemById(movie.OwnerId) : null;
    }

    private TimeSpan? FolderEstimate(string strmPath)
    {
        if (Path.GetDirectoryName(strmPath) is not { } folder)
        {
            return null;
        }

        if (_estimates.TryGetValue(folder, out var known))
        {
            return known;
        }

        // After a restart: another version of the same movie may already have one stored.
        foreach (var sibling in Directory.EnumerateFiles(folder, "*.strm"))
        {
            if (libraryManager.FindByPath(sibling, false) is { } other && RuntimeRules.HasRealRuntime(other.RunTimeTicks))
            {
                var runtime = TimeSpan.FromTicks(other.RunTimeTicks!.Value);
                _estimates[folder] = runtime;
                return runtime;
            }
        }

        return null;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastLookup + LookupSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _lastLookup = DateTimeOffset.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private bool IsOurs(BaseItem item) =>
        item is Movie or Episode
        && item.Path is { } path
        && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
        && librarySync.FindEntry(path) is not null;

    private static int? TmdbId(BaseItem? item) =>
        item?.GetProviderId(MetadataProvider.Tmdb) is { } value
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
