using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Shows plugin messages as dialogs in the Jellyfin client that asked to play something. Jellyfin drops messages for a
/// session without an open websocket, so those wait until the session is connected again and not playing.
/// </summary>
public sealed class UserNotifier(ISessionManager sessions, IAuthorizationContext auth, ILogger<UserNotifier> logger) : IHostedService
{
    /// <summary>How long an announcement about a running download is worth showing late.</summary>
    public static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KeepUndelivered = TimeSpan.FromHours(1);

    // jellyfin-web connects its websocket before it reports its capabilities and settles its page.
    private static readonly TimeSpan AfterReconnect = TimeSpan.FromSeconds(3);

    // These run jellyfin-web, whose dialog renders sanitized HTML; others (e.g. Android TV's toast) show text as is.
    private static readonly HashSet<string> HtmlClients = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jellyfin Web", "Jellyfin Android", "Jellyfin Media Player", "Jellyfin Desktop",
    };

    private static readonly TimeSpan WatchFor = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<(string EntryId, int? Episode), (string SessionId, DateTimeOffset At)> _watchers = new();
    private readonly ConcurrentDictionary<(string SessionId, string Text), DateTimeOffset> _recent = new();
    private readonly UndeliveredMessages _undelivered = new();

    public async Task<Caller> ResolveCallerAsync(HttpContext context)
    {
        try
        {
            var info = await auth.GetAuthorizationInfo(context).ConfigureAwait(false);
            var id = sessions.Sessions
                .Where(s => s.DeviceId == info.DeviceId && s.UserId == info.UserId)
                .OrderByDescending(s => s.LastActivityDate)
                .FirstOrDefault()?.Id;
            if (id is null)
            {
                logger.LogInformation("No session found for device {Device} user {User}", info.DeviceId, info.UserId);
            }

            return new Caller(id, info.UserId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the caller of a playback request");
            return new Caller(null, Guid.Empty);
        }
    }

    private SessionInfo? Find(string sessionId) => sessions.Sessions.FirstOrDefault(s => s.Id == sessionId);

    /// <summary>Whether the session has an open websocket; <see cref="SessionInfo.IsActive"/> is also true without one.</summary>
    private static bool IsConnected(SessionInfo session)
    {
        try
        {
            return session.SessionControllers.Any(c => c.IsSessionActive);
        }
        catch (ObjectDisposedException)
        {
            // The session ended.
            return false;
        }
    }

    /// <summary>Remember who is waiting for an entry, so failures later in the job reach them.</summary>
    public void Watch(string entryId, int? episode, string? sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _watchers)
        {
            if (now - value.At > WatchFor)
            {
                _watchers.TryRemove(key, out _);
            }
        }

        if (sessionId is not null)
        {
            _watchers[(entryId, episode)] = (sessionId, now);
        }
    }

    /// <summary>The entry is ready; nobody needs to hear about it failing.</summary>
    public void Unwatch(string entryId, int? episode) => _watchers.TryRemove((entryId, episode), out _);

    /// <summary>The session's player closed; it waits for nothing any more.</summary>
    public void ForgetSession(string sessionId)
    {
        foreach (var (key, value) in _watchers)
        {
            if (value.SessionId == sessionId)
            {
                _watchers.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Who is waiting for an entry, and stops remembering them.</summary>
    public string? TakeWatcher(string entryId, int? episode) =>
        _watchers.TryRemove((entryId, episode), out var watcher) && DateTimeOffset.UtcNow - watcher.At <= WatchFor
            ? watcher.SessionId
            : null;

    /// <param name="delay">Lets the client's own error dialog appear first so ours ends up on top.</param>
    /// <param name="keepFor">How long the message waits if the session isn't connected; by default an hour.</param>
    public void Send(string? sessionId, string text, TimeSpan delay = default, TimeSpan? keepFor = null)
    {
        if (sessionId is null)
        {
            logger.LogInformation("No session to show a message to; not shown: {Text}", MessageLinks.ToPlainText(text));
            return;
        }

        // The expiry counts from now, however long the message then waits.
        var expires = DateTimeOffset.UtcNow + (keepFor ?? KeepUndelivered);
        if (delay <= TimeSpan.Zero)
        {
            Deliver(sessionId, text, expires);
            return;
        }

        _ = Task.Delay(delay).ContinueWith(_ => Deliver(sessionId, text, expires), TaskScheduler.Default);
    }

    /// <summary>Stops the session's player, which also ends its queue. Nothing happens if it isn't connected.</summary>
    /// <param name="stillWanted">Checked again right before the command goes out; false drops it (the player moved on).</param>
    public void StopPlayback(string sessionId, Func<bool> stillWanted)
    {
        if (Find(sessionId) is not { } session || !IsConnected(session))
        {
            logger.LogInformation("Session {Session} isn't connected; its player can't be stopped", sessionId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!stillWanted())
                {
                    logger.LogInformation("Session {Session} has moved on; its player isn't stopped", sessionId);
                    return;
                }

                await sessions.SendPlaystateCommand(null!, sessionId, new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None)
                    .ConfigureAwait(false);
                logger.LogInformation("Stopped the player of session {Session}", sessionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not stop the player of session {Session}", sessionId);
            }
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessions.SessionControllerConnected += OnReconnected;
        sessions.CapabilitiesChanged += OnSessionChanged;
        sessions.SessionActivity += OnSessionChanged;
        sessions.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessions.SessionControllerConnected -= OnReconnected;
        sessions.CapabilitiesChanged -= OnSessionChanged;
        sessions.SessionActivity -= OnSessionChanged;
        sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues the message and, if the session can show it now, sends it. It stays queued until that send has completed
    /// with the session still connected, so a connection lost on the way only delays it.
    /// </summary>
    private void Deliver(string sessionId, string text, DateTimeOffset expires)
    {
        var now = DateTimeOffset.UtcNow;
        if (ShownRecently(sessionId, text, now))
        {
            // Clients often call PlaybackInfo several times per play; say each thing once.
            return;
        }

        if (!_undelivered.AddUntil(sessionId, text, expires, now))
        {
            // Already waiting or on its way.
            return;
        }

        var session = Find(sessionId);
        if (session is null || !IsConnected(session))
        {
            logger.LogInformation("Session {Session} isn't connected; the message waits until it is: {Text}", sessionId, MessageLinks.ToPlainText(text));
            return;
        }

        if (!session.SupportedCommands.Contains(GeneralCommandType.DisplayMessage))
        {
            _undelivered.Sent(sessionId, text);
            logger.LogInformation("Session {Session} can't show messages; not shown: {Text}", sessionId, MessageLinks.ToPlainText(text));
            return;
        }

        if (_undelivered.TakeOne(sessionId, text, now))
        {
            SendNow(sessionId, text);
        }
    }

    private bool ShownRecently(string sessionId, string text, DateTimeOffset now)
    {
        foreach (var (key, at) in _recent)
        {
            if (now - at > DedupeWindow)
            {
                _recent.TryRemove(key, out _);
            }
        }

        return _recent.ContainsKey((sessionId, text));
    }

    /// <summary>Sends a message taken from the queue; it goes back to waiting unless the send completes while connected.</summary>
    private void SendNow(string sessionId, string text)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var shown = text;
                var sent = await _undelivered.SendAsync(
                        sessionId,
                        text,
                        () => Find(sessionId) is { } session && IsConnected(session),
                        () =>
                        {
                            shown = Find(sessionId)?.Client is { } client && HtmlClients.Contains(client)
                                ? MessageLinks.ToHtml(text)
                                : MessageLinks.ToPlainText(text);

                            // No TimeoutMs: the web client then shows a dialog (announced by screen readers) instead of a brief toast.
                            return sessions.SendMessageCommand(null!, sessionId, new MessageCommand { Header = "AudioVault", Text = shown }, CancellationToken.None);
                        },
                        () => DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);
                if (sent)
                {
                    _recent[(sessionId, text)] = DateTimeOffset.UtcNow;
                    logger.LogInformation("Showed message to session {Session}: {Text}", sessionId, shown);
                }
                else
                {
                    logger.LogInformation("Session {Session} wasn't connected throughout sending a message, so it may not have been shown; it waits until the session is connected again", sessionId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not show a message to session {Session}; it waits until the session is connected again", sessionId);
            }
        });
    }

    /// <summary>Shows a session's waiting messages once it is connected, can show them and isn't playing.</summary>
    private void Flush(string? sessionId)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (sessionId is null || !_undelivered.HasAny(sessionId, now)
                || Find(sessionId) is not { } session || !IsConnected(session) || session.NowPlayingItem is not null
                || !session.SupportedCommands.Contains(GeneralCommandType.DisplayMessage))
            {
                return;
            }

            foreach (var text in _undelivered.Take(sessionId, now))
            {
                if (ShownRecently(sessionId, text, now))
                {
                    _undelivered.Sent(sessionId, text);
                    continue;
                }

                SendNow(sessionId, text);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show the waiting messages of session {Session}", sessionId);
        }
    }

    private void OnReconnected(object? sender, SessionEventArgs e)
    {
        var sessionId = e.SessionInfo?.Id;
        if (sessionId is not null && _undelivered.HasAny(sessionId, DateTimeOffset.UtcNow))
        {
            _ = Task.Delay(AfterReconnect).ContinueWith(_ => Flush(sessionId), TaskScheduler.Default);
        }
    }

    private void OnSessionChanged(object? sender, SessionEventArgs e) => Flush(e.SessionInfo?.Id);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => Flush(e.Session?.Id);
}

public sealed record Caller(string? SessionId, Guid UserId);
