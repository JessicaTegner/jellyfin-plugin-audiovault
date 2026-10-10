using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Samples the progress of the long first-load tasks (catalog read, library scan) every 30 seconds, so the settings
/// page can estimate the remaining time from the rate actually observed, even when it was just opened.
/// </summary>
public sealed class SetupProgress(ITaskManager taskManager, ILogger<SetupProgress> logger) : IHostedService, IDisposable
{
    public const string FullSyncKey = "AudioVaultFullSync";
    public const string RecentSyncKey = "AudioVaultRecentSync";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly string[] Keys = [FullSyncKey, RecentSyncKey, RepairRules.LibraryScanKey];

    private readonly ProgressRate _rate = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
    private Timer? _timer;

    /// <summary>Records the running tasks' progress now; returns the workers that were looked at.</summary>
    public IReadOnlyList<IScheduledTaskWorker> Sample()
    {
        var workers = taskManager.ScheduledTasks.Where(t => Keys.Contains(t.ScheduledTask.Key)).ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var worker in workers)
        {
            if (worker.State == TaskState.Running && worker.CurrentProgress is { } progress)
            {
                _rate.Observe(worker.ScheduledTask.Key, progress, now);
            }
        }

        return workers;
    }

    /// <summary>Time left for a running task at its observed rate, or null.</summary>
    public TimeSpan? Remaining(string key) => _rate.Remaining(key, 100, DateTimeOffset.UtcNow);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => SampleSafely(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void SampleSafely()
    {
        try
        {
            Sample();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not sample task progress");
        }
    }
}
