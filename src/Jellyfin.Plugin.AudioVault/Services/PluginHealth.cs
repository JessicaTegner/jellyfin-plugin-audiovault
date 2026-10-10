using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Parts of the plugin that could not start, for the settings page. Has no dependencies, so it can't fail itself.</summary>
public sealed class PluginHealth
{
    private readonly ConcurrentDictionary<string, string> _problems = new(StringComparer.Ordinal);

    /// <summary>One sentence per part that isn't working, or null when everything started.</summary>
    public string? Problem => _problems.IsEmpty
        ? null
        : "The AudioVault plugin is only partly working: " + string.Join(" ", _problems.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
            + " Jellyfin itself keeps working. The server log has the details.";

    public void Failed(string part, string what) => _problems[part] = what;

    public void Recovered(string part) => _problems.TryRemove(part, out _);
}

/// <summary>
/// What Jellyfin's host starts in place of a plugin background service. It has no dependencies that can fail; the
/// service and everything it needs (stores reading state files, for instance) are created only in StartAsync, where a
/// failure is logged and shown on the settings page instead of stopping Jellyfin from starting.
/// </summary>
public sealed class GuardedHostedService<T>(IServiceProvider services, PluginHealth health, ILogger<GuardedHostedService<T>> logger) : IHostedService
    where T : class, IHostedService
{
    private IHostedService? _inner;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var part = typeof(T).Name;
        try
        {
            var inner = services.GetRequiredService<T>();
            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
            _inner = inner;
            health.Recovered(part);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AudioVault plugin degraded: {Service} could not start", part);
            health.Failed(part, $"{Describe(part)} could not start ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_inner is not { } inner)
        {
            return;
        }

        try
        {
            await inner.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AudioVault {Service} did not stop cleanly", typeof(T).Name);
        }
    }

    private static string Describe(string part) => part switch
    {
        nameof(StartupService) => "Cache cleanup and the startup library update",
        nameof(SoftFailTracker) => "Explaining blocked plays after the player closes",
        nameof(UserNotifier) => "Showing messages to players that were disconnected",
        nameof(DateAddedService) => "Keeping the Recently Added order",
        nameof(RuntimeEstimateService) => "Filling in runtimes",
        nameof(PublicUrlMonitor) => "Updating playback links when the public URL changes",
        nameof(UserAccountWatcher) => "Revoking downloads of disabled Jellyfin users",
        nameof(MaintenanceService) => "Keeping the cache within its size limit",
        nameof(RefreshRepairService) => "Finishing titles Jellyfin's library scan left unprocessed",
        nameof(EpisodeListScheduler) => "Looking up episode lists automatically",
        nameof(PlaybackReach) => "Noticing players that can't reach the playback address",
        nameof(SetupProgress) => "Estimating how long the first catalog read and library scan take",
        nameof(CompatibilityCheck) => "Checking which Jellyfin features the plugin can use",
        _ => part,
    };
}
