using System.Runtime.CompilerServices;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging;

namespace AudioVault.Sources.AudioVault;

public sealed class AudioVaultSource : IContentSource, IDisposable
{
    private readonly Func<AudioVaultOptions> _options;
    private readonly FlareSolverrClient _flareSolverr;
    private readonly ILogger _logger;
    private readonly RequestThrottle _throttle = new();
    private readonly SourceCooldown _cooldown;
    private readonly LoginFailures _loginFailures = new();

    // The current session per account key, plus the anonymous one under AnonymousKey. Replaced sessions are retired:
    // disposed only once the requests and downloads still using them are done.
    private readonly Dictionary<string, AudioVaultHttp> _sessions = new();
    private readonly Lock _sessionsLock = new();
    private readonly Func<System.Net.CookieContainer, HttpMessageHandler>? _handlerFactory;

    public AudioVaultSource(Func<AudioVaultOptions> options, HttpClient flareSolverrHttp, ILogger<AudioVaultSource> logger)
        : this(options, flareSolverrHttp, logger, null)
    {
    }

    /// <param name="handlerFactory">Tests only: replaces the network with a fake site.</param>
    internal AudioVaultSource(
        Func<AudioVaultOptions> options,
        HttpClient flareSolverrHttp,
        ILogger<AudioVaultSource> logger,
        Func<System.Net.CookieContainer, HttpMessageHandler>? handlerFactory)
    {
        _options = options;
        _flareSolverr = new FlareSolverrClient(flareSolverrHttp);
        _logger = logger;
        _handlerFactory = handlerFactory;
        _cooldown = new SourceCooldown(stateDirectory: () => _options().StateDirectory, logger: logger);
    }

    // Not a possible account key, which is a trimmed login.
    private const string AnonymousKey = " anonymous";

    public string Id => "audiovault";

    // AudioVault bans accounts above 25 downloads a day or with parallel connections per download.
    public SourceLimits Limits { get; } = new(MaxDownloadsPerDay: 25, MaxConcurrentDownloads: 1);

    // About 140 pages per section today; far more means the pagination is being misread.
    private const int MaxListingPages = 2000;

    public async IAsyncEnumerable<SourceEntry> ListAllAsync(IProgress<double>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        var sections = new[] { ("movies", MediaKind.Movie), ("shows", MediaKind.Show) };
        for (var s = 0; s < sections.Length; s++)
        {
            var (path, kind) = sections[s];
            var lastPage = 1;
            for (var page = 1; page <= lastPage; page++)
            {
                var listing = ListingParser.ParseListing(await GetPageAsync($"/{path}?page={page}", ct).ConfigureAwait(false));
                if (page == 1)
                {
                    if (listing.Rows.Count == 0)
                    {
                        throw new SourceFormatChangedException($"/{path} page 1 has no rows");
                    }

                    if (listing.LastPage > MaxListingPages)
                    {
                        throw new SourceFormatChangedException($"/{path} claims {listing.LastPage} pages");
                    }

                    lastPage = listing.LastPage;
                }
                else if (Math.Abs(listing.LastPage - lastPage) > 2)
                {
                    // The current page isn't a link, so the last page sees one fewer; a sync can also see a page come or go.
                    throw new SourceFormatChangedException($"/{path} page {page} says there are {listing.LastPage} pages, page 1 said {lastPage}");
                }

                // A missing page would look like those entries were removed from AudioVault. Only the last may be empty.
                if (listing.Rows.Count == 0 && page < lastPage)
                {
                    throw new SourceFormatChangedException($"/{path} page {page} of {lastPage} has no rows");
                }

                foreach (var row in listing.Rows)
                {
                    yield return NameParser.Parse(row.Id, kind, row.Name);
                }

                progress?.Report(100.0 * (s + ((double)page / lastPage)) / sections.Length);
            }
        }
    }

    public async IAsyncEnumerable<SourceEntry> ListRecentAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var (shows, movies) = ListingParser.ParseRecent(await GetPageAsync("/", ct).ConfigureAwait(false));
        foreach (var row in movies)
        {
            yield return NameParser.Parse(row.Id, MediaKind.Movie, row.Name);
        }

        foreach (var row in shows)
        {
            yield return NameParser.Parse(row.Id, MediaKind.Show, row.Name);
        }
    }

    public async Task<SourceHealth> CheckHealthAsync(SourceAccount? account, CancellationToken ct)
    {
        try
        {
            var listing = ListingParser.ParseListing(await GetPageAsync("/movies?page=1", ct).ConfigureAwait(false));
            if (listing.Rows.Count == 0)
            {
                return new SourceHealth(false, "AudioVault layout changed: movie listing is empty");
            }

            if (account is null)
            {
                return new SourceHealth(true, $"OK: catalog readable, {listing.LastPage} movie pages");
            }

            using var lease = Lease(account);
            await lease.Session.EnsureLoggedInAsync(force: true, ct).ConfigureAwait(false);
            return new SourceHealth(true, "OK: logged in");
        }
        catch (SourceFormatChangedException ex)
        {
            return new SourceHealth(false, "AudioVault layout changed, parser needs an update: " + ex.Message, FailureInfo.From(ex));
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException or TaskCanceledException)
        {
            return new SourceHealth(false, ex.Message, FailureInfo.From(ex));
        }
    }

    /// <summary>
    /// Only returns the credential to store. The account's shared session is left alone: it switches to the new
    /// credential the first time an account carrying it is used, i.e. once the caller has saved it.
    /// </summary>
    public async Task<SourceSignIn> SignInAsync(string login, string password, CancellationToken ct)
    {
        var withPassword = new AudioVaultCredential(null, password);
        var probe = new SourceAccount(login, withPassword.Serialize());
        using var session = Http(probe, withPassword);
        await session.EnsureLoggedInAsync(force: true, ct).ConfigureAwait(false);
        var stored = session.ExportCredential()!;
        var account = new SourceAccount(login, stored.Serialize());
        if (stored.Token is null)
        {
            _logger.LogWarning("AudioVault issued no remember-me token for {Account}; the password will be stored instead", account.Key);
        }

        return new SourceSignIn(account.Credential, stored.Token is null);
    }

    public async Task<bool> SignOutAsync(SourceAccount account, CancellationToken ct)
    {
        // Logging out rotates the account's token at AudioVault, so whatever session it has is finished either way.
        AudioVaultHttp session;
        lock (_sessionsLock)
        {
            session = _sessions.Remove(account.Key, out var existing) && Matches(existing, account) ? existing : Http(account);
            session.AddLease();
            if (existing is not null && !ReferenceEquals(existing, session))
            {
                existing.Retire();
            }
        }

        try
        {
            if (await session.SignOutAsync(ct).ConfigureAwait(false))
            {
                return true;
            }

            _logger.LogWarning("AudioVault still accepts the sign-in token of {Account} after logging out", account.Key);
            return false;
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not sign out of AudioVault as {Account}; the stored token is deleted anyway", account.Key);
            return false;
        }
        finally
        {
            session.ReleaseLease();
            session.Retire();
        }
    }

    /// <summary>Only looks: never creates, replaces or signs in a session.</summary>
    public SourceHealth GetReadiness(SourceAccount account)
    {
        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(account.Key, out var session) && Matches(session, account))
            {
                return session.GetReadiness();
            }
        }

        var options = _options();
        return AudioVaultHttp.Readiness(
            account,
            AudioVaultCredential.Parse(account.Credential),
            _cooldown.Active(options.BaseUrl, options.FlareSolverrUrl),
            _loginFailures.Active(options.BaseUrl, account));
    }

    public async Task<SourceMedia> OpenAsync(string entryId, SourceAccount account, CancellationToken ct)
    {
        // The lease lasts as long as the download's stream, which keeps the session's connection alive.
        var lease = Lease(account);
        try
        {
            return await lease.Session.OpenDownloadAsync(entryId, lease, ct).ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_sessionsLock)
        {
            foreach (var session in _sessions.Values)
            {
                session.Retire();
            }

            _sessions.Clear();
        }

        _throttle.Dispose();
    }

    private async Task<string> GetPageAsync(string pathAndQuery, CancellationToken ct)
    {
        using var lease = Lease(null);
        return await lease.Session.GetPageAsync(pathAndQuery, ct).ConfigureAwait(false);
    }

    private AudioVaultHttp Http(SourceAccount? account, AudioVaultCredential? credential = null) =>
        new(_options, account, _throttle, _cooldown, _loginFailures, _flareSolverr, _logger, credential, _handlerFactory);

    // A session is the provider's: one site, login and credential. The host's generation and owner are left out, so
    // users who saved the same credentials share one session (and its sign-in), like they share the account's quota.
    private bool Matches(AudioVaultHttp session, SourceAccount? account) =>
        string.Equals(session.Account?.Credential, account?.Credential, StringComparison.Ordinal)
        && session.BaseUrl == _options().BaseUrl;

    /// <summary>
    /// The account's shared session (anonymous for null), held until the lease is disposed. A changed credential or
    /// site replaces the session; the replaced one keeps serving its leases and is disposed after the last one.
    /// </summary>
    private SessionLease Lease(SourceAccount? account)
    {
        var key = account?.Key ?? AnonymousKey;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(key, out var session) || !Matches(session, account))
            {
                var replaced = session;
                session = Http(account);
                _sessions[key] = session;
                replaced?.Retire();
            }

            session.AddLease();
            return new SessionLease(session);
        }
    }

    private sealed class SessionLease(AudioVaultHttp session) : IDisposable
    {
        private int _released;

        public AudioVaultHttp Session => session;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                session.ReleaseLease();
            }
        }
    }
}
