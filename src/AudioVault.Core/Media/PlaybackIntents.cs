using System.Collections.Concurrent;
using AudioVault.Core.Sources;

namespace AudioVault.Core.Media;

/// <summary>One user's recorded choice to play an entry, bound to that playback attempt.</summary>
/// <param name="PlaySessionId">The attempt's PlaySessionId, once the media server has issued it; null before.</param>
/// <param name="Items">The media-server items of the attempt (the item and the version), to match its stop.</param>
public sealed record PlaybackIntent(Guid UserId, SourceAccount Account, string? PlaySessionId, IReadOnlyCollection<Guid> Items);

/// <summary>
/// Remembers which entries a user actually chose to play, per user. Media servers probe every version of an item
/// before playback and re-read the original stream URL when they transcode, so only intended entries may trigger a
/// download from such requests. A request can't say whose it is, so an intent only counts while it is the only one
/// for that entry: with two users waiting on the same title, server-side requests download nothing.
/// An intent belongs to one playback attempt: it is retired when that attempt's preparation fails or its playback
/// stops, so a failed or finished play can't authorise later downloads.
/// </summary>
public sealed class PlaybackIntents(TimeProvider? time = null)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string, int?, Guid), (DateTimeOffset At, PlaybackIntent Intent, long Sequence)> _intents = new();

    // Orders registrations, so a failure retires only the intents registered before it.
    private long _sequence;

    public void Register(string entryId, int? episode, SourceAccount account) => Register(entryId, episode, Guid.Empty, account);

    public void Register(string entryId, int? episode, Guid userId, SourceAccount account, string? playSessionId = null, IReadOnlyCollection<Guid>? items = null)
    {
        var now = _time.GetUtcNow();
        // A new registration is a new attempt (or the same one, now with its PlaySessionId): it replaces the old one.
        _intents[(entryId, episode, userId)] = (now, new PlaybackIntent(userId, account, playSessionId, items ?? []), Interlocked.Increment(ref _sequence));
        foreach (var (key, value) in _intents)
        {
            if (now - value.At > Ttl)
            {
                _intents.TryRemove(KeyValuePair.Create(key, value));
            }
        }
    }

    /// <summary>Forgets every intent of a user, e.g. when their account is removed or replaced.</summary>
    public void Revoke(Guid userId)
    {
        foreach (var key in _intents.Keys)
        {
            if (key.Item3 == userId)
            {
                _intents.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// The user's attempt at this entry failed or was blocked: it authorises nothing more. A newer attempt (both
    /// PlaySessionIds known and different) keeps its intent.
    /// </summary>
    public bool Retire(Guid userId, string entryId, int? episode, string? playSessionId = null)
    {
        var key = (entryId, episode, userId);
        return _intents.TryGetValue(key, out var value)
            && (string.IsNullOrEmpty(playSessionId) || string.IsNullOrEmpty(value.Intent.PlaySessionId)
                || string.Equals(playSessionId, value.Intent.PlaySessionId, StringComparison.Ordinal))
            && _intents.TryRemove(KeyValuePair.Create(key, value));
    }

    /// <summary>
    /// Preparing this entry failed, possibly after its players got a placeholder: every intent for it registered
    /// before now authorises nothing more. One registered meanwhile (a newer attempt) stays.
    /// </summary>
    public int RetireFailed(string entryId, int? episode)
    {
        var upTo = Interlocked.Read(ref _sequence);
        var retired = 0;
        foreach (var (key, value) in _intents)
        {
            if (key.Item1 == entryId && key.Item2 == episode && value.Sequence <= upTo
                && _intents.TryRemove(KeyValuePair.Create(key, value)))
            {
                retired++;
            }
        }

        return retired;
    }

    /// <summary>
    /// A player stopped: retires the intents of that attempt. Matched by PlaySessionId when both sides know it, otherwise
    /// by user and item. An intent of another attempt (a different PlaySessionId) stays.
    /// </summary>
    public int RetireStopped(string? playSessionId, IReadOnlyCollection<Guid> items, IReadOnlyCollection<Guid> users)
    {
        var retired = 0;
        foreach (var (key, value) in _intents)
        {
            var intent = value.Intent;
            var matches = !string.IsNullOrEmpty(playSessionId) && !string.IsNullOrEmpty(intent.PlaySessionId)
                ? string.Equals(playSessionId, intent.PlaySessionId, StringComparison.Ordinal)
                : users.Contains(intent.UserId) && intent.Items.Any(items.Contains);
            // Only the matched intent: a newer attempt registered under the same key since then stays.
            if (matches && _intents.TryRemove(KeyValuePair.Create(key, value)))
            {
                retired++;
            }
        }

        return retired;
    }

    /// <summary>The one user who recently chose to play this; null if nobody did or several did.</summary>
    public PlaybackIntent? Find(string entryId, int? episode)
    {
        var now = _time.GetUtcNow();
        PlaybackIntent? found = null;
        foreach (var (key, value) in _intents)
        {
            if (key.Item1 != entryId || key.Item2 != episode || now - value.At > Ttl)
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = value.Intent;
        }

        return found;
    }

    /// <summary>The account of the one user who recently chose to play this; null if nobody did or several did.</summary>
    public SourceAccount? AccountFor(string entryId, int? episode) => Find(entryId, episode)?.Account;
}
