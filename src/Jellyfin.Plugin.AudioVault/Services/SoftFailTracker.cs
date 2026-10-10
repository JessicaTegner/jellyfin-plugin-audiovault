using System.Collections.Concurrent;
using AudioVault.Core.Media;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// When preflight fails we let the client play the 2-second placeholder instead of returning an error
/// (jellyfin-web shows two generic dialogs for any PlaybackInfo error). Once the player has closed, this shows our
/// explanation (a dialog over a running player fights it for screen-reader focus) and undoes any watched state.
/// </summary>
public sealed class SoftFailTracker(
    ISessionManager sessions,
    IUserDataManager userData,
    IUserManager users,
    ILibraryManager libraryManager,
    PlaybackIntents intents,
    UserNotifier notifier,
    AutoplayGuard autoplay,
    ILogger<SoftFailTracker> logger) : IHostedService
{
    // The message goes out at the latest after Fallback; the watched state is kept for the player's stop much longer,
    // since buffering or a client reporting late can delay it well past the message.
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan Fallback = TimeSpan.FromSeconds(20);

    private readonly PendingFailures _pending = new();
    private readonly ConcurrentDictionary<string, PendingFailure> _bySession = new();
    private readonly ExplainedFailures _explained = new();

    /// <summary>A blocked play: remembers the user's watched state now, to put it back once the placeholder has played.</summary>
    /// <param name="playSessionId">The attempt's PlaySessionId from PlaybackInfo, if known, to tell its stop from others.</param>
    public void Track(Guid itemId, Guid versionId, string? sessionId, string? playSessionId, Guid userId, string message)
    {
        var now = DateTimeOffset.UtcNow;
        _pending.Prune(now, RestoreWindow);

        // A player fetches the stream several times per attempt; the first fetch's record stands. A delayed fetch of an
        // earlier attempt doesn't replace a newer one's record either. Checked and replaced in one step, so concurrent
        // fetches can't overwrite a newer attempt's record.
        if (_pending.Record(now, sessionId, userId, message, playSessionId, itemId, [itemId, versionId], () => Capture(userId, [itemId, versionId])) is not { } pending)
        {
            return;
        }

        // If the client doesn't report playback stopping soon, still tell the user; the state waits for the stop.
        _ = Task.Delay(Fallback).ContinueWith(
            _ =>
            {
                if (_pending.MarkDelivered(pending))
                {
                    notifier.Send(sessionId, message);
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// A play that is going ahead: an earlier blocked attempt at the item no longer restores anything, so neither this
    /// attempt's stop nor a late stop of the blocked one undoes this attempt's progress.
    /// </summary>
    /// <param name="playSessionId">The attempt going ahead; only an attempt newer than the blocked one retires it.</param>
    public void Succeeded(Guid? itemId, Guid? versionId, string? sessionId, Guid userId, string? playSessionId)
    {
        Guid[] ids = [.. new[] { itemId, versionId }.OfType<Guid>()];
        if (ids.Length > 0 && _pending.RetireAfterSuccess(sessionId, userId, ids, playSessionId) > 0)
        {
            logger.LogDebug("A play of {Item} went ahead; the earlier blocked attempt's watched state is no longer restored", itemId ?? versionId);
        }
    }

    /// <summary>PlaybackInfo issued this PlaySessionId: attempts are ordered by it.</summary>
    public void Issued(string? playSessionId) => _pending.Issued(playSessionId, DateTimeOffset.UtcNow);

    /// <summary>
    /// Preparing an entry failed: explains it once that session's player has closed. The stream request and the job's
    /// failure event both report the same failure; the session hears about it once.
    /// </summary>
    public void ExplainFailure(string? sessionId, string entryId, int? episode, string message)
    {
        if (sessionId is not null && _explained.TryClaim(sessionId, entryId, episode, DateTimeOffset.UtcNow))
        {
            NotifyAfterStop(sessionId, message);
        }
    }

    /// <summary>A stream that failed while the player waited for it: explains once that session's player has closed.</summary>
    public void NotifyAfterStop(string? sessionId, string message)
    {
        if (sessionId is null)
        {
            return;
        }

        // A player fetches a stream several times per attempt; one message is enough.
        if (_bySession.TryGetValue(sessionId, out var waiting) && waiting.Message == message)
        {
            return;
        }

        var pending = new PendingFailure(DateTimeOffset.UtcNow, sessionId, Guid.Empty, message, new Dictionary<Guid, PlayState>());
        _bySession[sessionId] = pending;
        _ = Task.Delay(Fallback).ContinueWith(
            _ =>
            {
                if (_bySession.TryRemove(new KeyValuePair<string, PendingFailure>(sessionId, pending)))
                {
                    notifier.Send(sessionId, message);
                }
            },
            TaskScheduler.Default);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessions.PlaybackStart += OnPlaybackStart;
        sessions.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessions.PlaybackStart -= OnPlaybackStart;
        sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    // The stopped attempt authorises no more downloads (e.g. by Jellyfin's transcoder or a later probe).
    private void RetireIntents(PlaybackStopEventArgs e)
    {
        try
        {
            var items = new List<Guid>();
            if (e.Item is { } item)
            {
                items.Add(item.Id);
            }

            if (Guid.TryParse(e.MediaSourceId, out var version))
            {
                items.Add(version);
            }

            intents.RetireStopped(e.PlaySessionId, items, [.. e.Users.Select(u => u.Id)]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not retire the play intents of a stopped player");
        }
    }

    private Dictionary<Guid, PlayState> Capture(Guid userId, IEnumerable<Guid> itemIds)
    {
        var states = new Dictionary<Guid, PlayState>();
        try
        {
            if (users.GetUserById(userId) is not { } user)
            {
                return states;
            }

            foreach (var id in itemIds.Distinct())
            {
                if (libraryManager.GetItemById(id) is { } item && userData.GetUserData(user, item) is { } data)
                {
                    states[id] = new PlayState(data.Played, data.PlayCount, data.PlaybackPositionTicks, data.LastPlayedDate);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the watched state before a blocked play");
        }

        return states;
    }

    // A newer attempt than a blocked one started (the blocked one's own start has its PlaySessionId).
    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is { } item)
        {
            _pending.RetireOnStart(e.Session?.Id, item.Id, e.PlaySessionId);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        RetireIntents(e);
        var sessionId = e.Session?.Id;
        // The player's own report was read when it arrived (PlaybackIntentFilter); this also covers stops Jellyfin
        // reports by itself, such as when a session ends.
        autoplay.StopObserved(sessionId, e.PlaySessionId, DateTimeOffset.UtcNow);

        if (sessionId is not null)
        {
            notifier.ForgetSession(sessionId);
            if (_bySession.TryRemove(sessionId, out var failed))
            {
                notifier.Send(sessionId, failed.Message);
            }
        }

        if (e.Item is not { } item || _pending.Take(sessionId, item.Id, e.PlaySessionId, DateTimeOffset.UtcNow, RestoreWindow, out var delivered) is not { } pending)
        {
            return;
        }

        if (!delivered)
        {
            notifier.Send(sessionId ?? pending.SessionId, pending.Message);
        }

        if (!pending.PriorStates.TryGetValue(item.Id, out var prior))
        {
            logger.LogInformation("No earlier watched state of {Item} to restore after a blocked play", item.Name);
            return;
        }

        try
        {
            var user = e.Users.FirstOrDefault(u => u.Id == pending.UserId) ?? users.GetUserById(pending.UserId);
            if (user is null || userData.GetUserData(user, item) is not { } data)
            {
                return;
            }

            // The placeholder "played" to the end; put back exactly what was there before.
            data.Played = prior.Played;
            data.PlayCount = prior.PlayCount;
            data.PlaybackPositionTicks = prior.PlaybackPositionTicks;
            data.LastPlayedDate = prior.LastPlayedDate;
            userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore the watched state of {Item} after a blocked play", item.Name);
        }
    }
}
