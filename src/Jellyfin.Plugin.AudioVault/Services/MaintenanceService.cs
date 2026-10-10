using AudioVault.Core.Media;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Housekeeping that can't wait for the next play: every few minutes the cache limit is applied (cached titles whose
/// playback leases expired become evictable) and accounts of deleted users are removed. A settings save that changes
/// the cache size or folder applies the limit right away, and one that changes which versions are included rewrites
/// the library files, retrying until that works.
/// </summary>
public sealed class MaintenanceService(MediaJobManager jobs, UserAccountWatcher userAccounts, LibraryBridge library, ILogger<MaintenanceService> logger)
    : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private (int Gb, string Path)? _cacheSettings;
    private SettingsRewrite<VersionSettings>? _versions;

    /// <summary>Why the library files don't match the version settings yet, or null.</summary>
    public string? VersionRewriteProblem => _versions?.Problem;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            _cacheSettings = (plugin.Configuration.MaxCacheGb, plugin.Configuration.CachePath);

            // The startup library update writes the files with these.
            _versions = new SettingsRewrite<VersionSettings>(Versions(plugin.Configuration));
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        // Not right at startup: Jellyfin's users and the cache cleanup should be in place first.
        _timer = new Timer(_ => _ = RunAsync(), null, FirstRun, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private async Task RunAsync()
    {
        await jobs.MaintainCacheAsync().ConfigureAwait(false);
        try
        {
            await userAccounts.RemoveOrphansAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the AudioVault accounts of deleted users failed");
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (e is not Configuration.PluginConfiguration config)
        {
            return;
        }

        var now = (config.MaxCacheGb, config.CachePath);
        if (_cacheSettings != now)
        {
            _cacheSettings = now;
            _ = jobs.MaintainCacheAsync();
        }

        // Saving the same settings again while a rewrite keeps failing tries it again now.
        var versions = Versions(config);
        if (_versions?.Request(versions) is { } generation)
        {
            Schedule(versions, generation, Debounce);
        }
    }

    private static VersionSettings Versions(Configuration.PluginConfiguration c) =>
        new((c.PreferredLanguage ?? string.Empty).Trim().ToUpperInvariant(), c.IncludeOtherLanguages, c.IncludeTts);

    private void Schedule(VersionSettings target, int generation, TimeSpan delay)
    {
        var rewrite = _versions!;
        var ct = _stopping.Token;
        _ = Task.Run(
            async () =>
            {
                string problem;
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    if (!rewrite.IsCurrent(generation))
                    {
                        return;
                    }

                    var result = await library.SyncLibraryAsync(scan: true, ct).ConfigureAwait(false);
                    if (LibraryRules.Unfinished(result) is not { } unfinished)
                    {
                        rewrite.Succeeded(generation, target);
                        logger.LogInformation("Library files rewritten for the new version settings (changed: {Changed})", result.Changed);
                        return;
                    }

                    problem = $"{unfinished} for the new version settings, so some titles may still be listed or missing.";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Rewriting the library files for the new version settings failed");
                    problem = $"The library files couldn't be updated for the new version settings: {ex.Message}";
                }

                if (rewrite.Failed(generation, problem) is { } retry)
                {
                    logger.LogWarning("{Problem} Retrying in {Delay}", problem, retry);
                    Schedule(target, generation, retry);
                }
            },
            CancellationToken.None);
    }
}

/// <summary>The settings that decide which versions are written to the library.</summary>
public sealed record VersionSettings(string Language, bool OtherLanguages, bool Tts);

/// <summary>
/// Settings the library files must be rewritten for: the ones last applied and the ones wanted. A failed rewrite is
/// retried with growing delays (as for a new playback address) and reported until one works; asking again for the
/// settings that keep failing retries at once.
/// </summary>
public sealed class SettingsRewrite<T>(T applied)
    where T : IEquatable<T>
{
    private readonly Lock _lock = new();
    private int _generation;
    private int _failures;
    private bool _running;

    public T Applied { get; private set; } = applied;

    public T Desired { get; private set; } = applied;

    /// <summary>Why the last attempt failed and when it is tried again, or null.</summary>
    public string? Problem { get; private set; }

    public bool Pending
    {
        get
        {
            lock (_lock)
            {
                return _running || !Desired.Equals(Applied);
            }
        }
    }

    /// <summary>The settings are now <paramref name="desired"/>: the rewrite to run (its generation), or null if none is needed.</summary>
    public int? Request(T desired)
    {
        lock (_lock)
        {
            var failing = Problem is not null;
            if (desired.Equals(Desired) && !failing && (_running || desired.Equals(Applied)))
            {
                return null;
            }

            Desired = desired;
            _failures = 0;
            _running = true;
            return ++_generation;
        }
    }

    public bool IsCurrent(int generation)
    {
        lock (_lock)
        {
            return generation == _generation;
        }
    }

    public void Succeeded(int generation, T target)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }

            Applied = target;
            Problem = null;
            _failures = 0;
            _running = false;
        }
    }

    /// <returns>When to try again, or null if a newer request took over.</returns>
    public TimeSpan? Failed(int generation, string problem)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return null;
            }

            var retry = PublicUrlMonitor.RetryDelay(++_failures);
            var minutes = (int)Math.Round(retry.TotalMinutes);
            Problem = $"{problem} Retrying in {minutes} {(minutes == 1 ? "minute" : "minutes")}; the Jellyfin log has details.";
            return retry;
        }
    }
}
