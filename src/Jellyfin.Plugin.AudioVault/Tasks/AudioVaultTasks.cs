using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Tasks;

public sealed class FullCatalogSyncTask(
    CatalogSyncService catalogSync,
    LibraryBridge library,
    DateAddedService dates,
    IServiceProvider services,
    ILogger<FullCatalogSyncTask> logger) : IScheduledTask
{
    public string Name => "Full catalog sync";

    public string Key => "AudioVaultFullSync";

    public string Description =>
        "Reads the complete AudioVault catalog (metadata only) and updates the .strm libraries. "
        + "Scheduled runs start up to an hour after their set time, at random.";

    public string Category => "AudioVault";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (StartedBySchedule())
        {
            var delay = ScheduleJitter.RandomDelay();
            logger.LogInformation("Scheduled full catalog sync waits {Minutes:0} minutes before starting", delay.TotalMinutes);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var result = await catalogSync.SyncFullAsync(new Progress<double>(p => progress.Report(p * 0.95)), cancellationToken).ConfigureAwait(false);
        await library.SyncLibraryAsync(scan: true, cancellationToken).ConfigureAwait(false);
        dates.Refresh(result.Bumped);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var (day, time) = ScheduleJitter.WeeklySlot(Plugin.Instance?.Configuration.SigningSecret ?? string.Empty);
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.WeeklyTrigger, DayOfWeek = day, TimeOfDayTicks = time.Ticks },
        ];
    }

    // Resolved lazily: the task manager itself is built from the registered tasks.
    private bool StartedBySchedule()
    {
        var worker = services.GetService<ITaskManager>()?.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == Key);
        var slots = (worker?.Triggers ?? [])
            .Where(t => t.Type is TaskTriggerInfoType.WeeklyTrigger or TaskTriggerInfoType.DailyTrigger && t.TimeOfDayTicks is not null)
            .Select(t => (t.Type == TaskTriggerInfoType.WeeklyTrigger ? t.DayOfWeek : (DayOfWeek?)null, TimeSpan.FromTicks(t.TimeOfDayTicks!.Value)));
        return ScheduleJitter.IsScheduledStart(DateTime.Now, slots);
    }
}

public sealed class RecentCatalogSyncTask(
    CatalogSyncService catalogSync,
    CatalogStore catalog,
    LibraryBridge library,
    DateAddedService dates) : IScheduledTask
{
    public string Name => "Recent uploads sync";

    public string Key => "AudioVaultRecentSync";

    public string Description => "Picks up new AudioVault uploads from the home page. Runs a full sync if the catalog is empty.";

    public string Category => "AudioVault";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = catalog.Count == 0
            ? await catalogSync.SyncFullAsync(new Progress<double>(p => progress.Report(p * 0.95)), cancellationToken).ConfigureAwait(false)
            : await catalogSync.SyncRecentAsync(cancellationToken).ConfigureAwait(false);

        await library.SyncLibraryAsync(scan: true, cancellationToken).ConfigureAwait(false);
        dates.Refresh(result.Bumped);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
    ];
}

public sealed class EpisodeListTask(LibraryBridge library) : IScheduledTask
{
    public string Name => "Resolve episode lists";

    public string Key => "AudioVaultEpisodeLists";

    public string Description =>
        "Looks up episode lists for AudioVault seasons (one TMDb request per season) so every episode is listed before download. "
        + "Also runs on its own after library scans, when Jellyfin identifies a show, and every few hours.";

    public string Category => "AudioVault";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Waits for an automatic pass that is already running, then looks up every pending season.
        await library.RunEpisodeListPassAsync(automatic: false, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks },
    ];
}

public sealed class FixDateAddedTask(DateAddedService dates, LibraryBridge library, LibrarySyncService librarySync) : IScheduledTask
{
    public string Name => "Fix date added order";

    public string Key => "AudioVaultFixDateAdded";

    public string Description =>
        "Sets the date added of existing AudioVault items so Recently Added follows AudioVault's upload order. New items get it automatically.";

    public string Category => "AudioVault";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (librarySync.IndexedFiles == 0)
        {
            await library.SyncLibraryAsync(scan: false, cancellationToken).ConfigureAwait(false);
        }

        await dates.FixAllAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

public sealed class RepairUnprocessedTask(RefreshRepairService repair) : IScheduledTask
{
    public string Name => "Repair unprocessed items";

    public string Key => "AudioVaultRepairUnprocessed";

    public string Description =>
        "Asks Jellyfin to fetch the details of every AudioVault title its library scan added but never finished, so they "
        + "can be found in search and Recently Added. This also happens on its own every half hour, a thousand titles at a time.";

    public string Category => "AudioVault";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await repair.RepairAsync(limit: null, automatic: false, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
