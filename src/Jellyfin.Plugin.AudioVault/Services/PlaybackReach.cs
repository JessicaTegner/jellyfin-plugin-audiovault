using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Whether players and the admin's browser can load the playback address; feeds <see cref="UnreachedPlays"/>. Playback
/// reports reach it from <see cref="Api.PlaybackIntentFilter"/>, in the request itself; overdue attempts are also
/// swept on a timer, so they neither wait for the next play nor pile up.
/// </summary>
public sealed class PlaybackReach(
    ISessionManager sessions,
    PluginSettings settings,
    ILogger<PlaybackReach> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(30);

    private readonly UnreachedPlays _plays = new();
    private Timer? _timer;

    public ReachProbes Probes { get; } = new();

    /// <summary>Never throws: playback info must not fail because of this check.</summary>
    /// <param name="link">One of the stream links handed out; the address in it is the one the player will use.</param>
    /// <param name="directPlayDisabled">The player asked with direct play turned off.</param>
    public void Issued(Caller caller, string? playSessionId, string link, IEnumerable<(string EntryId, int? Episode)> streams, bool directPlayDisabled)
    {
        try
        {
            if (caller.SessionId is null || sessions.Sessions.FirstOrDefault(s => s.Id == caller.SessionId) is not { } session)
            {
                return;
            }

            var player = new PlayerInfo(session.Id, session.DeviceId ?? session.Id, session.DeviceName ?? "an unnamed device", session.Client ?? "an unknown app", session.UserName);
            var marker = link.IndexOf("/AudioVault/Stream/", StringComparison.OrdinalIgnoreCase);
            var address = marker > 0 ? PublicUrlRules.Normalize(link[..marker]) ?? link[..marker] : settings.PublicServerUrl;
            Log(_plays.Issued(playSessionId, player, address, streams, DateTimeOffset.UtcNow, directPlayDisabled));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not note a playback info response for the playback address check");
        }
    }

    public void Prepared(string? playSessionId) => _plays.Prepared(playSessionId, DateTimeOffset.UtcNow);

    public void Started(string? playSessionId, bool directPlay, bool held) => _plays.Started(playSessionId, directPlay, held, DateTimeOffset.UtcNow);

    public void Stopped(string? playSessionId, bool failed) => _plays.Stopped(playSessionId, failed, DateTimeOffset.UtcNow);

    public void Relayed(string? playSessionId) => _plays.Relayed(playSessionId);

    /// <param name="address">The scheme, host and base path the request arrived on.</param>
    public void Arrived(PlaybackTicket ticket, string address) =>
        _plays.Arrived(ticket.PlaySessionId, ticket.SessionId, PublicUrlRules.Normalize(address) ?? address, DateTimeOffset.UtcNow);

    public IReadOnlyList<UnreachedReport> Reports(string url)
    {
        var now = DateTimeOffset.UtcNow;
        Log(_plays.Sweep(now));
        return _plays.Reports(PublicUrlRules.Normalize(url) ?? url, now);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Sweep(), null, SweepEvery, SweepEvery);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void Sweep()
    {
        try
        {
            Log(_plays.Sweep(DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check for players that didn't load the playback address");
        }
    }

    private void Log(IReadOnlyList<UnreachedReport> added)
    {
        foreach (var r in added)
        {
            logger.LogWarning(
                "A player on {Device} ({Client}, {User}) confirmed a direct play but never requested its stream at {Url} within {Seconds} seconds; it may not reach that address",
                r.DeviceName,
                r.Client,
                r.UserName,
                r.Url,
                UnreachedPlays.Window.TotalSeconds);
        }
    }
}
