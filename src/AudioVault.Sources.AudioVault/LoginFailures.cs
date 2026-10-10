using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault;

/// <summary>
/// Recent sign-in failures per site, login and stored credential. Kept by the source rather than the session, so
/// neither another user saving the same credentials nor a replaced session can reset the pause; only a different
/// credential (e.g. after signing in again) gets to try right away.
/// </summary>
internal sealed class LoginFailures
{
    public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);

    private readonly Dictionary<(string BaseUrl, string Key, string Credential), (DateTimeOffset At, string Message)> _failures = new();
    private readonly Lock _lock = new();

    public void Record(string baseUrl, SourceAccount account, string message)
    {
        lock (_lock)
        {
            // Expired entries are dropped here, so the table only holds what is still pausing someone.
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in _failures.Where(f => now - f.Value.At >= Backoff).Select(f => f.Key).ToList())
            {
                _failures.Remove(stale);
            }

            _failures[(baseUrl, account.Key, account.Credential)] = (now, message);
        }
    }

    public void Clear(string baseUrl, SourceAccount account)
    {
        lock (_lock)
        {
            _failures.Remove((baseUrl, account.Key, account.Credential));
        }
    }

    /// <summary>The failure message while the pause lasts, otherwise null.</summary>
    public string? Active(string baseUrl, SourceAccount? account)
    {
        if (account is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _failures.TryGetValue((baseUrl, account.Key, account.Credential), out var failure)
                && DateTimeOffset.UtcNow - failure.At < Backoff
                ? failure.Message
                : null;
        }
    }
}
