namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Messages for sessions that couldn't receive them yet, kept until a send to the connected session has completed or
/// they expire. A message being sent stays here, marked as in flight; if the session isn't connected right before and
/// after the send, or the send fails, it waits again with its original expiry.
/// </summary>
public sealed class UndeliveredMessages
{
    public const int MaxPerSession = 5;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Message>> _bySession = new(StringComparer.Ordinal);

    /// <returns>False if the same text is already waiting or being sent, or nothing is kept.</returns>
    public bool Add(string sessionId, string text, DateTimeOffset now, TimeSpan keepFor) => AddUntil(sessionId, text, now + keepFor, now);

    /// <returns>False if the same text is already waiting or being sent, or it has expired.</returns>
    public bool AddUntil(string sessionId, string text, DateTimeOffset expires, DateTimeOffset now)
    {
        if (expires <= now)
        {
            return false;
        }

        lock (_lock)
        {
            Prune(now);
            if (!_bySession.TryGetValue(sessionId, out var waiting))
            {
                _bySession[sessionId] = waiting = [];
            }

            if (waiting.Any(m => m.Text == text))
            {
                return false;
            }

            if (waiting.Count >= MaxPerSession && waiting.FindIndex(m => !m.InFlight) is var oldest and >= 0)
            {
                waiting.RemoveAt(oldest);
            }

            waiting.Add(new Message(text, expires));
            return true;
        }
    }

    /// <summary>Whether the session has messages waiting that aren't being sent.</summary>
    public bool HasAny(string sessionId, DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            return _bySession.TryGetValue(sessionId, out var waiting) && waiting.Any(m => !m.InFlight);
        }
    }

    /// <summary>The session's waiting messages, oldest first, now marked as being sent.</summary>
    public IReadOnlyList<string> Take(string sessionId, DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            if (!_bySession.TryGetValue(sessionId, out var waiting))
            {
                return [];
            }

            var taken = waiting.Where(m => !m.InFlight).ToList();
            taken.ForEach(m => m.InFlight = true);
            return [.. taken.Select(m => m.Text)];
        }
    }

    /// <summary>Marks one waiting message as being sent; false if it isn't waiting.</summary>
    public bool TakeOne(string sessionId, string text, DateTimeOffset now)
    {
        lock (_lock)
        {
            Prune(now);
            if (!_bySession.TryGetValue(sessionId, out var waiting) || waiting.Find(m => m.Text == text && !m.InFlight) is not { } message)
            {
                return false;
            }

            message.InFlight = true;
            return true;
        }
    }

    /// <summary>The send completed: the message is gone.</summary>
    public void Sent(string sessionId, string text)
    {
        lock (_lock)
        {
            if (_bySession.TryGetValue(sessionId, out var waiting))
            {
                waiting.RemoveAll(m => m.Text == text);
                if (waiting.Count == 0)
                {
                    _bySession.Remove(sessionId);
                }
            }
        }
    }

    /// <summary>The send didn't happen or failed: the message waits again, until its original expiry.</summary>
    public void Failed(string sessionId, string text, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_bySession.TryGetValue(sessionId, out var waiting) && waiting.Find(m => m.Text == text) is { } message)
            {
                message.InFlight = false;
            }

            Prune(now);
        }
    }

    /// <summary>
    /// Sends a message taken for sending: only while the session is connected, checked right before; otherwise, or when
    /// the send fails, it waits again. The send's own exception is passed on.
    /// </summary>
    /// <returns>Whether the send completed with the session still connected.</returns>
    public async Task<bool> SendAsync(string sessionId, string text, Func<bool> connected, Func<Task> send, Func<DateTimeOffset> clock)
    {
        try
        {
            if (!connected())
            {
                Failed(sessionId, text, clock());
                return false;
            }

            await send().ConfigureAwait(false);

            // Jellyfin completes a send without sending when no socket is open any more, and gives no acknowledgement.
            if (!connected())
            {
                Failed(sessionId, text, clock());
                return false;
            }
        }
        catch
        {
            Failed(sessionId, text, clock());
            throw;
        }

        Sent(sessionId, text);
        return true;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (session, waiting) in _bySession)
        {
            waiting.RemoveAll(m => m.Expires <= now);
            if (waiting.Count == 0)
            {
                _bySession.Remove(session);
            }
        }
    }

    private sealed class Message(string text, DateTimeOffset expires)
    {
        public string Text { get; } = text;

        public DateTimeOffset Expires { get; } = expires;

        public bool InFlight { get; set; }
    }
}
