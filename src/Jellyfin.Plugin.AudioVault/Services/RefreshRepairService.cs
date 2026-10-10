using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Queues a metadata refresh for AudioVault items whose first refresh Jellyfin never ran (an interrupted library scan
/// leaves them so, and later scans don't pick them up again). Until then they can't be found in search, Recently
/// Added or the Movies tab. Runs a while after startup, every half hour and after each library scan, and counts the
/// waiting items for the status page. Refreshes never probe the .strm links, so nothing is downloaded.
/// </summary>
public sealed class RefreshRepairService(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    ITaskManager taskManager,
    PluginSettings settings,
    RepairCounts counts,
    ILogger<RefreshRepairService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstPass = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AfterScan = TimeSpan.FromMinutes(1);
    private const int PassLimit = 1000;
    private const int PageSize = 2000;

    private readonly RepairQueue _queued = new(TimeSpan.FromHours(6));
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private (string Movies, string Shows)? _roots;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted += OnTaskCompleted;
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemUpdated += OnItemChanged;
        libraryManager.ItemRemoved += OnItemRemoved;
        _timer = new Timer(_ => _ = RunAutomaticAsync(), null, FirstPass, Interval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted -= OnTaskCompleted;
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemUpdated -= OnItemChanged;
        libraryManager.ItemRemoved -= OnItemRemoved;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    /// <summary>One pass over both libraries. Returns how many items were queued.</summary>
    /// <param name="limit">Most items to queue, or null for all that need it.</param>
    /// <param name="automatic">Automatic passes skip items queued in the last hours and don't run during a library scan.</param>
    public async Task<int> RepairAsync(int? limit, bool automatic, IProgress<double>? progress, CancellationToken ct)
    {
        await _pass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (automatic && ScanRunning())
            {
                logger.LogDebug("Jellyfin's library scan is running; the AudioVault repair pass waits for it to finish");
                return 0;
            }

            return await Task.Run(() => Pass(limit, automatic, progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task RunAutomaticAsync()
    {
        try
        {
            await RepairAsync(PassLimit, automatic: true, null, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Checking for AudioVault items Jellyfin never finished processing failed");
        }
    }

    private int Pass(int? limit, bool automatic, IProgress<double>? progress, CancellationToken ct)
    {
        var roots = (Movies: settings.MoviesPath, Shows: settings.ShowsPath);
        _roots = roots;
        var folders = libraryManager.GetVirtualFolders();
        var libraries = new[]
            {
                (Root: roots.Movies, Kinds: new[] { BaseItemKind.Movie }),
                (Root: roots.Shows, Kinds: new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode }),
            }
            .Select(l => (l.Root, l.Kinds, Folder: folders.FirstOrDefault(f => (f.Locations ?? []).Contains(l.Root, StringComparer.Ordinal))))
            .Where(l => l.Folder is not null && Guid.TryParse(l.Folder.ItemId, out _))
            .Select(l => new InternalItemsQuery
            {
                IncludeItemTypes = l.Kinds,
                AncestorIds = [Guid.Parse(l.Folder!.ItemId)],
                Recursive = true,
                IsVirtualItem = false,
                // Without a user Jellyfin doesn't group by PresentationUniqueKey anyway; this states it.
                GroupByPresentationUniqueKey = false,
            })
            .ToList();

        // Ids first, then the items in batches by id: paging by sort order would skip items whenever a refresh renames one.
        var ids = libraries.SelectMany(q => libraryManager.GetItemIds(q)).Distinct().ToList();
        var waiting = new Dictionary<Guid, RepairKind>();
        var directory = new DirectoryService(fileSystem);
        var now = DateTimeOffset.UtcNow;
        var needing = 0;
        var queued = 0;
        for (var start = 0; start < ids.Count; start += PageSize)
        {
            ct.ThrowIfCancellationRequested();
            // Only the item rows: no images, user data or provider ids.
            var page = libraryManager.GetItemList(new InternalItemsQuery
            {
                ItemIds = [.. ids.Skip(start).Take(PageSize)],
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false },
            });
            var candidates = new List<Guid>();
            foreach (var item in page)
            {
                if (Kind(item, roots) is not { } kind)
                {
                    continue;
                }

                if (!RepairRules.IsSearchable(item.PresentationUniqueKey))
                {
                    waiting[item.Id] = kind;
                }

                if (RepairRules.NeedsRefresh(item.DateLastRefreshed, item.PresentationUniqueKey))
                {
                    candidates.Add(item.Id);
                }
            }

            needing += candidates.Count;
            // A manual pass also queues items an earlier pass queued.
            var take = _queued.Take(candidates, now, limit - queued, includeRecent: !automatic);
            foreach (var id in take)
            {
                // A first refresh in Default mode runs every metadata provider, like the scan would have.
                providerManager.QueueRefresh(
                    id,
                    new MetadataRefreshOptions(directory)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false,
                        ReplaceAllImages = false,
                        // Never ffprobe the .strm links: no stream requests, no downloads.
                        EnableRemoteContentProbe = false,
                    },
                    RefreshPriority.Low);
            }

            queued += take.Count;
            progress?.Report(100.0 * Math.Min(ids.Count, start + PageSize) / ids.Count);
        }

        counts.Replace(waiting.Select(w => (w.Key, w.Value)));
        if (queued > 0)
        {
            logger.LogInformation(
                "Queued metadata refresh for {Queued} AudioVault items that Jellyfin never finished processing ({Needing} need it, {Waiting} not searchable yet)",
                queued,
                needing,
                waiting.Count);
        }
        else
        {
            logger.LogDebug("No AudioVault items to repair ({Needing} need a refresh and were queued recently)", needing);
        }

        return queued;
    }

    private bool ScanRunning() =>
        RepairRules.LibraryScanRunning(taskManager.ScheduledTasks.Select(t => (t.ScheduledTask.Key, t.State == TaskState.Running)));

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Task?.ScheduledTask.Key != RepairRules.LibraryScanKey)
        {
            return;
        }

        // A moment later, so whatever the scan queued itself is on its way.
        _ = Task.Delay(AfterScan, _stopping.Token).ContinueWith(
            _ => RunAutomaticAsync(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    // Keeps the status page's counts current between passes: a finished refresh makes an item searchable.
    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (_roots is { } roots && e.Item is { } item && Kind(item, roots) is { } kind)
            {
                counts.Update(item.Id, kind, !RepairRules.IsSearchable(item.PresentationUniqueKey));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not update the AudioVault searchable count");
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { } item)
        {
            counts.Remove(item.Id);
        }
    }

    /// <summary>The kind of an AudioVault item, or null for anything else.</summary>
    private static RepairKind? Kind(BaseItem item, (string Movies, string Shows) roots) => item switch
    {
        { IsVirtualItem: true } => null,
        Movie when RepairRules.IsUnder(item.Path, roots.Movies) => RepairKind.Movie,
        Episode when RepairRules.IsUnder(item.Path, roots.Shows) => RepairKind.Episode,
        Series when RepairRules.IsUnder(item.Path, roots.Shows) => RepairKind.ShowFolder,
        // A season without a folder of its own has no path; it belongs to its series.
        Season season when RepairRules.IsUnder(item.Path ?? season.Series?.Path, roots.Shows) => RepairKind.ShowFolder,
        _ => null,
    };
}
