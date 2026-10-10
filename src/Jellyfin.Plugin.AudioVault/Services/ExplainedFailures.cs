using System.Collections.Concurrent;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Which sessions were just told that preparing an entry failed. One failure reaches the host twice (the waiting
/// stream request and the job's failure event); only the first explains it.
/// </summary>
public sealed class ExplainedFailures
{
    // Both reports of one failure arrive together; a later failure of a new attempt is explained again.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string Session, string EntryId, int? Episode), DateTimeOffset> _explained = new();

    /// <summary>True if this session wasn't told about a failure of the entry within <see cref="Window"/>.</summary>
    public bool TryClaim(string sessionId, string entryId, int? episode, DateTimeOffset now)
    {
        foreach (var (key, at) in _explained)
        {
            if (now - at > Window)
            {
                _explained.TryRemove(KeyValuePair.Create(key, at));
            }
        }

        var claimed = false;
        _explained.AddOrUpdate(
            (sessionId, entryId, episode),
            _ =>
            {
                claimed = true;
                return now;
            },
            (_, at) =>
            {
                claimed = now - at > Window;
                return claimed ? now : at;
            });
        return claimed;
    }
}
