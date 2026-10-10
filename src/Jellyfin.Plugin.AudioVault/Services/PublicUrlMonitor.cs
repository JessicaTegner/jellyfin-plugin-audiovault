using System.Net;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Rewrites the .strm files when the public URL changes, and accepts addresses detected by the settings page.</summary>
public sealed class PublicUrlMonitor(
    PluginSettings settings,
    LibraryBridge library,
    IConfigurationManager configurationManager,
    IServerApplicationHost host,
    IHttpClientFactory httpClientFactory,
    ILogger<PublicUrlMonitor> logger) : IHostedService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SelfCheckEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    // The URL the library files were last fully written with, and the one a rewrite is under way for.
    private string? _applied;
    private string? _pending;
    private int _generation;
    private int _failures;
    private (string Url, string? Problem, DateTimeOffset At)? _selfCheck;
    private int _selfChecking;
    private readonly HostAddresses _tailscale = new(SelfCheckEvery);

    /// <summary>Why the last detected address wasn't used, or null.</summary>
    public string? DetectionProblem { get; private set; }

    /// <summary>Why the library files still hold an old address, or null.</summary>
    public string? RewriteProblem { get; private set; }

    /// <summary>Whether the library files are being rewritten with a new address.</summary>
    public bool RewritePending
    {
        get
        {
            lock (_lock)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>
    /// Why this server couldn't fetch its own public URL at the last check, or null. Checks again in the background
    /// when the last check is older than 10 minutes or was for another address.
    /// </summary>
    public string? CachedSelfCheck(string url)
    {
        var last = _selfCheck;
        if ((last is not { } l || l.Url != url || DateTimeOffset.UtcNow - l.At > SelfCheckEvery) && Interlocked.Exchange(ref _selfChecking, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _selfCheck = (url, await ProbeAsync(url, _stopping.Token).ConfigureAwait(false), DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not check the public URL from this server");
                }
                finally
                {
                    Volatile.Write(ref _selfChecking, 0);
                }
            });
        }

        return last is { } known && known.Url == url ? known.Problem : null;
    }

    /// <summary>
    /// For an http *.ts.net address, the same address with the Tailscale IP its name resolves to here. The name's
    /// addresses are cached; the suggestion is built from the address as it is now (scheme, port and path).
    /// </summary>
    public async Task<string?> TailscaleAlternativeAsync(string url, CancellationToken ct)
    {
        // An https name can't be swapped for its IP (the certificate is for the name), so there's nothing to look up.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
            || !uri.Host.TrimEnd('.').EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var addresses = await _tailscale.ResolveAsync(
                uri.Host,
                async (host, token) =>
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(LookupTimeout);
                        return await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
                    {
                        logger.LogDebug(ex, "Could not resolve {Host}", host);
                        return [];
                    }
                },
                DateTimeOffset.UtcNow,
                ct)
            .ConfigureAwait(false);
        return PublicUrlRules.TailscaleAlternative(url, addresses);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The startup sync writes every file with this address.
            _applied = settings.PublicServerUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not work out the public server URL");
        }

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnPluginConfigurationChanged;
        }

        configurationManager.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnPluginConfigurationChanged;
        }

        configurationManager.NamedConfigurationUpdated -= OnNamedConfigurationUpdated;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Wait before retrying a failed rewrite: 1, 2, 4, 8, 16, then every 30 minutes.</summary>
    public static TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>Schedules a library rewrite if the effective URL differs from the one the files were last written with.</summary>
    public bool Check()
    {
        string url;
        try
        {
            url = settings.PublicServerUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not work out the public server URL");
            return false;
        }

        int generation;
        lock (_lock)
        {
            if (_applied is null)
            {
                _applied = url;
                return false;
            }

            if (string.Equals(_pending ?? _applied, url, StringComparison.Ordinal))
            {
                return false;
            }

            logger.LogInformation("Public server URL changed from {Old} to {New} ({Source}); rewriting library files", _applied, url, settings.PublicUrl.SourceText);
            _pending = url;
            _failures = 0;
            RewriteProblem = null;
            generation = ++_generation;
        }

        Schedule(url, generation, Debounce);
        return true;
    }

    private void Schedule(string url, int generation, TimeSpan delay)
    {
        var ct = _stopping.Token;
        _ = Task.Run(
            async () =>
            {
                string problem;
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        return;
                    }

                    var result = await library.SyncLibraryAsync(scan: true, ct).ConfigureAwait(false);
                    if (result.Failed == 0)
                    {
                        lock (_lock)
                        {
                            if (_generation == generation)
                            {
                                _applied = url;
                                _pending = null;
                                _failures = 0;
                                RewriteProblem = null;
                            }
                        }

                        logger.LogInformation("Library files rewritten for public server URL {Url} (changed: {Changed})", url, result.Changed);
                        return;
                    }

                    problem = $"{result.Failed} library files could not be rewritten with the new address {url}.";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Rewriting library files for the new public server URL failed");
                    problem = $"The library files could not be rewritten with the new address {url}: {ex.Message}";
                }

                TimeSpan retry;
                lock (_lock)
                {
                    if (_generation != generation)
                    {
                        return;
                    }

                    retry = RetryDelay(++_failures);
                    RewriteProblem = $"{problem} Some titles may not play until this works. Retrying in {retry.TotalMinutes:0} minutes; the Jellyfin log has details.";
                }

                logger.LogWarning("{Problem} Retrying in {Delay}", problem, retry);
                Schedule(url, generation, retry);
            },
            CancellationToken.None);
    }

    /// <summary>Stores the address the settings page was opened with if it's usable and the server can reach it.</summary>
    /// <returns>Whether the stored address changed.</returns>
    public async Task<bool> AcceptDetectedAsync(string? candidate, CancellationToken ct)
    {
        var url = PublicUrlRules.Normalize(candidate);
        if (url is null || PublicUrlRules.Problem(url) is not null)
        {
            // Opened via localhost or similar: not a problem worth reporting, just not usable.
            return false;
        }

        var stored = PluginSettings.Config.DetectedServerUrl;
        if (string.Equals(PublicUrlRules.Normalize(stored), url, StringComparison.OrdinalIgnoreCase))
        {
            DetectionProblem = null;
            return false;
        }

        if (!PublicUrlRules.ShouldReplaceDetected(stored, url) && await ProbeAsync(stored, ct).ConfigureAwait(false) is null)
        {
            // Keep the better address (https or a domain name) while it still works.
            return false;
        }

        if (await ProbeAsync(url, ct).ConfigureAwait(false) is { } problem)
        {
            DetectionProblem = $"This page was opened at {url}, but this server can't use that address itself: {problem}";
            return false;
        }

        DetectionProblem = null;
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("AudioVault plugin is not loaded");
        plugin.Configuration.DetectedServerUrl = url;
        plugin.SaveConfiguration();
        logger.LogInformation("Detected server address {Url} (was {Stored})", url, string.IsNullOrEmpty(stored) ? "none" : stored);
        Check();
        return true;
    }

    /// <summary>Fetches {url}/System/Info/Public; null if this server answered, else why not.</summary>
    public async Task<string?> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync(url.TrimEnd('/') + "/System/Info/Public", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return $"it answered with status {(int)response.StatusCode}.";
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? id = null;
            try
            {
                using var json = JsonDocument.Parse(body);
                id = json.RootElement.TryGetProperty("Id", out var value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
            }

            return id is not null && NormalizeId(id) == NormalizeId(host.SystemId)
                ? null
                : "something other than this Jellyfin server answered.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return ex.Message;
        }
    }

    private static string NormalizeId(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private void OnPluginConfigurationChanged(object? sender, BasePluginConfiguration e) => Check();

    private void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (e.Key.Equals(NetworkConfigurationStore.StoreKey, StringComparison.Ordinal))
        {
            Check();
        }
    }
}

/// <summary>Addresses host names resolved to, kept for a while per name.</summary>
public sealed class HostAddresses(TimeSpan keep)
{
    private const int MaxHosts = 20;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (IPAddress[] Addresses, DateTimeOffset At)> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IPAddress[]> ResolveAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, DateTimeOffset now, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_hosts.TryGetValue(host, out var cached) && now - cached.At < keep)
            {
                return cached.Addresses;
            }
        }

        var addresses = await resolve(host, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _hosts[host] = (addresses, now);
            foreach (var old in _hosts.OrderBy(h => h.Value.At).Take(Math.Max(0, _hosts.Count - MaxHosts)).Select(h => h.Key).ToList())
            {
                _hosts.Remove(old);
            }
        }

        return addresses;
    }
}
