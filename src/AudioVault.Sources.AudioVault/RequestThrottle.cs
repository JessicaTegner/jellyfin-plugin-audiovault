namespace AudioVault.Sources.AudioVault;

/// <summary>Spaces out requests to the site across all sessions, so more accounts never means more load.</summary>
internal sealed class RequestThrottle : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public async Task WaitAsync(TimeSpan minGap, TimeSpan maxGap, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + NextGap(minGap, maxGap) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _last = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    internal static TimeSpan NextGap(TimeSpan minGap, TimeSpan maxGap) =>
        maxGap <= minGap ? minGap : minGap + ((maxGap - minGap) * Random.Shared.NextDouble());
}
