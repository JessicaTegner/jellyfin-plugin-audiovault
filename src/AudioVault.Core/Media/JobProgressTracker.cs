using System.Collections.Concurrent;
using AudioVault.Core.Sources;

namespace AudioVault.Core.Media;

public enum JobPhase
{
    Waiting,
    Downloading,
    Converting,
}

/// <param name="Id">Unique to one job for as long as it runs, so a poller can match rows across polls.</param>
public sealed record JobProgress(string Id, string EntryId, int? Episode, string Title, JobPhase Phase, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? Math.Min(100, BytesReceived * 100.0 / TotalBytes.Value) : null;
}

public enum JobOutcomeKind
{
    /// <summary>A movie is converted and cached.</summary>
    MovieReady,

    /// <summary>A season archive is downloaded; its episodes are converted one by one afterwards.</summary>
    SeasonDownloaded,

    Failed,
}

/// <param name="Sequence">Increases with every outcome, so a poller can tell which ones it has already seen.</param>
/// <param name="Failure">The error as a category, for reports that must not quote <paramref name="Error"/>.</param>
public sealed record JobOutcome(long Sequence, string EntryId, int? Episode, string Title, JobOutcomeKind Kind, string? Error, DateTimeOffset At, FailureInfo? Failure = null);

/// <summary>Live progress of downloads and conversions, and the last few results, for the status page.</summary>
public sealed class JobProgressTracker(TimeProvider? time = null)
{
    private const int MaxOutcomes = 20;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Tracked, byte> _active = new();
    private readonly Lock _lock = new();
    private readonly Queue<JobOutcome> _outcomes = new();
    private long _sequence;
    private long _order;

    public Tracked Start(string entryId, int? episode, string title, JobPhase phase)
    {
        var tracked = new Tracked(this, Interlocked.Increment(ref _order), entryId, episode, title, phase);
        _active[tracked] = 0;
        return tracked;
    }

    /// <summary>Oldest first. A conversion fed by a running download shows as the download.</summary>
    public IReadOnlyList<JobProgress> Active()
    {
        var all = _active.Keys.OrderBy(t => t.Order).Select(t => (t.Order, Progress: t.Snapshot())).ToList();
        var downloading = all.Where(a => a.Progress.Phase != JobPhase.Converting)
            .Select(a => (a.Progress.EntryId, a.Progress.Episode))
            .ToHashSet();
        return all.Where(a => a.Progress.Phase != JobPhase.Converting || !downloading.Contains((a.Progress.EntryId, a.Progress.Episode)))
            .Select(a => a.Progress)
            .ToList();
    }

    public void Record(string entryId, int? episode, string title, JobOutcomeKind kind, string? error = null, FailureInfo? failure = null)
    {
        lock (_lock)
        {
            _outcomes.Enqueue(new JobOutcome(++_sequence, entryId, episode, title, kind, error, _time.GetUtcNow(), failure));
            while (_outcomes.Count > MaxOutcomes)
            {
                _outcomes.Dequeue();
            }
        }
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<JobOutcome> RecentOutcomes()
    {
        lock (_lock)
        {
            return [.. _outcomes];
        }
    }

    public sealed class Tracked : IDisposable
    {
        private readonly JobProgressTracker _owner;
        private long _bytes;
        private long _total = -1;
        private volatile JobPhase _phase;

        internal Tracked(JobProgressTracker owner, long order, string entryId, int? episode, string title, JobPhase phase)
        {
            _owner = owner;
            Order = order;
            EntryId = entryId;
            Episode = episode;
            Title = title;
            _phase = phase;
        }

        internal long Order { get; }

        public string EntryId { get; }

        public int? Episode { get; }

        public string Title { get; }

        public JobPhase Phase
        {
            get => _phase;
            set => _phase = value;
        }

        public void SetTotal(long? total) => Interlocked.Exchange(ref _total, total ?? -1);

        public void AddBytes(int count) => Interlocked.Add(ref _bytes, count);

        public JobProgress Snapshot()
        {
            var total = Interlocked.Read(ref _total);
            return new JobProgress(FormattableString.Invariant($"{EntryId}|{Episode}|{Order}"), EntryId, Episode, Title, _phase, Interlocked.Read(ref _bytes), total >= 0 ? total : null);
        }

        public void Dispose() => _owner._active.TryRemove(this, out _);
    }
}
