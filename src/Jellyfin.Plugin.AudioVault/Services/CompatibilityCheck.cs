using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Checks at startup that the Jellyfin internals the plugin relies on still bind, and logs a warning once for each one
/// that doesn't. Whether the filter and middleware are reached can only be told inside a request, so the settings
/// page's status request completes the picture.
/// </summary>
public sealed class CompatibilityCheck(
    IServerApplicationHost host,
    IServerConfigurationManager config,
    ITaskManager tasks,
    ILogger<CompatibilityCheck> logger) : IHostedService
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private InternalsProbe? _probe;
    private (string Path, DateTime Written, bool Usable)? _webConfig;

    public string JellyfinVersion => host.ApplicationVersionString;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Facts(inRequest: false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <param name="inRequest">Called while Jellyfin handles an API request, which the filter and middleware must have seen.</param>
    public CompatibilityFacts Facts(bool inRequest)
    {
        InternalsProbe probe;
        lock (_lock)
        {
            probe = _probe ??= JellyfinInternals.Probe(JellyfinInternals.LoadRunning);
        }

        var enabled = Plugin.Instance?.Configuration.ShowAccountMenuLink == true;
        var (found, usable) = enabled ? WebConfig() : (true, true);
        var facts = new CompatibilityFacts(
            JellyfinVersion,
            probe,
            HookSignals.PlaybackFilterReached ? true : inRequest ? false : null,
            CompatibilityRules.MenuLink(enabled, HookSignals.MenuLinkMiddlewareReached ? true : inRequest ? false : null, found, usable),
            LibraryScanTask());
        foreach (var issue in CompatibilityRules.Issues(facts))
        {
            bool first;
            lock (_lock)
            {
                first = _warned.Add(issue.Key);
            }

            if (first)
            {
                logger.LogWarning("AudioVault compatibility: {Message}", issue.Message);
            }
        }

        return facts;
    }

    private bool? LibraryScanTask()
    {
        try
        {
            var all = tasks.ScheduledTasks.ToList();
            return all.Count == 0 ? null : all.Any(t => t.ScheduledTask.Key == RepairRules.LibraryScanKey);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug(ex, "Could not list Jellyfin's scheduled tasks");
            return null;
        }
    }

    /// <summary>Whether the web client's config.json exists and the menu link can be added to it; re-read when it changes.</summary>
    private (bool Found, bool Usable) WebConfig()
    {
        try
        {
            var file = Path.Combine(config.ApplicationPaths.WebPath, "config.json");
            if (!File.Exists(file))
            {
                return (false, false);
            }

            var written = File.GetLastWriteTimeUtc(file);
            lock (_lock)
            {
                if (_webConfig is { } cached && cached.Path == file && cached.Written == written)
                {
                    return (true, cached.Usable);
                }
            }

            var usable = WebConfigMenuLink.AddLink(File.ReadAllText(file), WebConfigMenuLink.AccountPath) is not null;
            lock (_lock)
            {
                _webConfig = (file, written, usable);
            }

            return (true, usable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(ex, "Could not read the web client's config.json");
            return (false, false);
        }
    }
}
