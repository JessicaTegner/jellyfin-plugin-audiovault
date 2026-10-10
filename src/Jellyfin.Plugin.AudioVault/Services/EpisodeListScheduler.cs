using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Starts episode-list passes when they can find something: a couple of minutes after each library scan, soon after
/// Jellyfin identifies a show with pending seasons, when failed lookups are due again, and every few hours as a
/// fallback (the daily *Resolve episode lists* task stays as well). Never during a library scan, and never next to
/// another pass.
/// </summary>
public sealed class EpisodeListScheduler(
    LibraryBridge library,
    ILibraryManager libraryManager,
    ITaskManager taskManager,
    LibraryScans scans,
    PluginSettings settings,
    ILogger<EpisodeListScheduler> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstPass = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly TimeSpan AfterScan = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AfterIdentified = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxDebounce = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    private readonly EpisodePassPlan _plan = new(Interval, MaxDebounce);
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _plan.Start(DateTimeOffset.UtcNow, FirstPass);
        taskManager.TaskCompleted += OnTaskCompleted;
        libraryManager.ItemUpdated += OnItemUpdated;
        _loop = Task.Run(LoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted -= OnTaskCompleted;
        libraryManager.ItemUpdated -= OnItemUpdated;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _stopping.Dispose();

    private async Task LoopAsync()
    {
        var ct = _stopping.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, ct).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                if (library.NextFailedRetry is { } retry)
                {
                    _plan.RetryAt(retry);
                }

                if (!_plan.ShouldStart(now, scans.Running))
                {
                    continue;
                }

                _plan.Started(now);
                if (!await library.RunEpisodeListPassAsync(automatic: true, null, ct).ConfigureAwait(false))
                {
                    // A pass started by hand is running; check again once it's done.
                    _plan.Request(DateTimeOffset.UtcNow, Tick);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automatic AudioVault episode-list pass failed");
            }
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Task?.ScheduledTask.Key != RepairRules.LibraryScanKey)
        {
            return;
        }

        // The scan has just identified new shows; give the refreshes it started a moment to finish.
        var now = DateTimeOffset.UtcNow;
        _plan.HoldUntil(now + AfterScan);
        _plan.Request(now, AfterScan);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is Series series
                && RepairRules.IsUnder(series.Path, settings.ShowsPath)
                && library.SeriesUpdated(series.Path, series.GetProviderId(MetadataProvider.Tmdb), series.DisplayOrder))
            {
                _plan.Request(DateTimeOffset.UtcNow, AfterIdentified);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check an updated show for pending episode lists");
        }
    }
}
