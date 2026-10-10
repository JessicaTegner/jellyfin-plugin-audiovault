using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Whether Jellyfin's library scan is running; metadata work of the plugin waits for it.</summary>
public sealed class LibraryScans(ITaskManager taskManager)
{
    public bool Running =>
        RepairRules.LibraryScanRunning(taskManager.ScheduledTasks.Select(t => (t.ScheduledTask.Key, t.State == TaskState.Running)));

    /// <returns>Whether it had to wait.</returns>
    public Task<bool> WaitAsync(CancellationToken ct) => ScanGate.WaitWhileAsync(() => Running, ScanGate.Poll, ct);
}
