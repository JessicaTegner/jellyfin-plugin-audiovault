using System.Collections.Concurrent;

namespace AudioVault.Core.Media;

/// <summary>What kind of probe window is open for an entry.</summary>
public enum ProbeWindow
{
    /// <summary>Nobody is building playback info for it.</summary>
    None,

    /// <summary>Only information requests (detail pages, version lists): nobody pressed play.</summary>
    Information,

    /// <summary>At least one request that starts playback.</summary>
    Play,
}

/// <summary>
/// Marks entries whose media-server item is currently being probed. Probe requests and playback requests look the
/// same on the wire; what tells them apart is that the server probes while it is building playback info. Windows of
/// requests that start playback are told apart from information-only ones: only the former may download.
/// </summary>
public sealed class ProbeWindows
{
    private readonly ConcurrentDictionary<(string, int?), (int Info, int Play)> _open = new();

    public IDisposable Open(IReadOnlyCollection<(string EntryId, int? Episode)> entries, bool play = false)
    {
        foreach (var key in entries)
        {
            _open.AddOrUpdate(key, play ? (0, 1) : (1, 0), (_, n) => play ? (n.Info, n.Play + 1) : (n.Info + 1, n.Play));
        }

        return new Closer(this, entries, play);
    }

    public bool IsOpen(string entryId, int? episode) => _open.ContainsKey((entryId, episode));

    public ProbeWindow State(string entryId, int? episode) =>
        !_open.TryGetValue((entryId, episode), out var n) ? ProbeWindow.None
        : n.Play > 0 ? ProbeWindow.Play
        : ProbeWindow.Information;

    private void Close(IEnumerable<(string, int?)> entries, bool play)
    {
        foreach (var key in entries)
        {
            var left = _open.AddOrUpdate(key, (0, 0), (_, n) => play ? (n.Info, n.Play - 1) : (n.Info - 1, n.Play));
            if (left.Info <= 0 && left.Play <= 0)
            {
                _open.TryRemove(new KeyValuePair<(string, int?), (int, int)>(key, left));
            }
        }
    }

    private sealed class Closer(ProbeWindows owner, IReadOnlyCollection<(string, int?)> entries, bool play) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                owner.Close(entries, play);
            }
        }
    }
}
