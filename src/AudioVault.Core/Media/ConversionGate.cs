namespace AudioVault.Core.Media;

/// <summary>
/// Server-wide limit on simultaneous conversions. Waiters someone is waiting to play go first; background ones
/// (season backfills) take what's left. A waiter's priority is re-read each time a slot frees up, so a background
/// job that someone starts watching moves to the front.
/// </summary>
public sealed class ConversionGate(Func<int> limit)
{
    private readonly Lock _lock = new();
    private readonly List<Waiter> _waiting = [];
    private int _running;

    public int Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>A slot if one is free and nobody is waiting, without queueing; otherwise null.</summary>
    public IDisposable? TryEnter()
    {
        lock (_lock)
        {
            if (_running < Limit() && _waiting.Count == 0)
            {
                _running++;
                return new Lease(this);
            }
        }

        return null;
    }

    /// <param name="whileQueued">Called if the caller has to queue; the result is disposed once it stops waiting.</param>
    public async Task<IDisposable> EnterAsync(Func<bool> isForeground, CancellationToken ct, Func<IDisposable>? whileQueued = null)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (_running < Limit() && (isForeground() ? !_waiting.Any(w => w.IsForeground()) : _waiting.Count == 0))
            {
                _running++;
                return new Lease(this);
            }

            waiter = new Waiter(isForeground);
            _waiting.Add(waiter);
        }

        using var queued = whileQueued?.Invoke();
        using (ct.Register(() =>
        {
            lock (_lock)
            {
                if (_waiting.Remove(waiter))
                {
                    waiter.Ready.TrySetCanceled(ct);
                }
            }
        }))
        {
            await waiter.Ready.Task.ConfigureAwait(false);
        }

        return new Lease(this);
    }

    private int Limit() => Math.Max(1, limit());

    private void Release()
    {
        lock (_lock)
        {
            _running--;
            while (_running < Limit() && _waiting.Count > 0)
            {
                var next = _waiting.FirstOrDefault(w => w.IsForeground()) ?? _waiting[0];
                _waiting.Remove(next);
                _running++;
                next.Ready.TrySetResult();
            }
        }
    }

    private sealed class Waiter(Func<bool> isForeground)
    {
        public Func<bool> IsForeground { get; } = isForeground;

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(ConversionGate owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
