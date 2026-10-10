using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging;

namespace AudioVault.Sources.AudioVault;

/// <summary>Session handling for audiovault.net: browser-like headers, Cloudflare fallback, Laravel login.</summary>
internal sealed class AudioVaultHttp : IDisposable
{
    private readonly Func<AudioVaultOptions> _options;
    private readonly FlareSolverrClient _flareSolverr;
    private readonly ILogger _logger;
    private readonly RequestThrottle _throttle;
    private readonly SourceCooldown _cooldown;
    private readonly LoginFailures _loginFailures;
    private readonly SourceAccount? _account;
    private readonly AudioVaultCredential? _credential;
    private readonly Func<CookieContainer, HttpMessageHandler>? _handlerFactory;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly CookieContainer _cookies;
    private readonly HttpClient _client;
    private readonly HttpClient _downloadClient;
    private readonly string _baseUrl;
    private readonly Lock _leaseLock = new();
    private int _leases;
    private bool _retired;
    private string? _userAgentOverride;
    private bool _loggedIn;

    /// <param name="account">Null for an anonymous session that can only read listings.</param>
    /// <param name="credential">Overrides the account's stored credential, e.g. a password while signing in.</param>
    public AudioVaultHttp(
        Func<AudioVaultOptions> options,
        SourceAccount? account,
        RequestThrottle throttle,
        SourceCooldown cooldown,
        LoginFailures loginFailures,
        FlareSolverrClient flareSolverr,
        ILogger logger,
        AudioVaultCredential? credential = null,
        Func<CookieContainer, HttpMessageHandler>? handlerFactory = null)
    {
        _handlerFactory = handlerFactory;
        _account = account;
        _credential = credential ?? AudioVaultCredential.Parse(account?.Credential);
        _throttle = throttle;
        _cooldown = cooldown;
        _loginFailures = loginFailures;
        _options = options;
        _flareSolverr = flareSolverr;
        _logger = logger;

        // A session belongs to one site: a changed BaseUrl gets a new session rather than this one changing under its requests.
        _baseUrl = options().BaseUrl;
        _cookies = NewCookieJar();
        _client = CreateClient(download: false);
        _downloadClient = handlerFactory is null ? CreateClient(download: true) : _client;
    }

    public string BaseUrl => _baseUrl;

    /// <summary>Counts a request or open download using this session, so retiring it can't dispose it underneath them.</summary>
    public void AddLease()
    {
        lock (_leaseLock)
        {
            ObjectDisposedException.ThrowIf(_retired && _leases == 0, this);
            _leases++;
        }
    }

    public void ReleaseLease()
    {
        lock (_leaseLock)
        {
            if (--_leases > 0 || !_retired)
            {
                return;
            }
        }

        Dispose();
    }

    /// <summary>Takes the session out of service: it is disposed once its last lease ends, or now if it has none.</summary>
    public void Retire()
    {
        lock (_leaseLock)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
            if (_leases > 0)
            {
                return;
            }
        }

        Dispose();
    }

    public bool LoggedIn => _loggedIn;

    /// <summary>What to store after a successful sign-in: the remember-me token if AudioVault issued one.</summary>
    public AudioVaultCredential? ExportCredential() => AudioVaultCredential.FromCookies(_cookies) ?? _credential;

    public SourceAccount? Account => _account;

    /// <summary>Only looks: never signs in or changes the session.</summary>
    public SourceHealth GetReadiness() =>
        Readiness(_account, _credential, CooldownMessage(), _loginFailures.Active(_baseUrl, _account));

    /// <param name="loginFailure">A recent sign-in failure of this login and credential, if any.</param>
    public static SourceHealth Readiness(
        SourceAccount? account, AudioVaultCredential? credential, string? cooldown, string? loginFailure)
    {
        if (account is null || credential is not { IsUsable: true })
        {
            return new SourceHealth(false, "No AudioVault account has been added for you.");
        }

        if (cooldown is { } paused)
        {
            return new SourceHealth(false, paused);
        }

        // Fail fast instead of hammering AudioVault after a recent login failure.
        if (loginFailure is not null)
        {
            return new SourceHealth(false, loginFailure);
        }

        return new SourceHealth(true, "OK");
    }

    public async Task<string> GetPageAsync(string pathAndQuery, CancellationToken ct)
    {
        using var response = await SendAsync(() => Get(pathAndQuery), HttpCompletionOption.ResponseContentRead, download: false, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SourceUnavailableException($"GET {pathAndQuery} returned {(int)response.StatusCode}") { HttpStatus = (int)response.StatusCode };
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="DownloadNotStartedException"/> only when no download request can have reached AudioVault;
    /// any failure after one was sent may already count against the account's daily limit.
    /// </summary>
    /// <param name="owner">Disposed together with the returned media's stream.</param>
    public async Task<SourceMedia> OpenDownloadAsync(string id, IDisposable? owner, CancellationToken ct)
    {
        var sends = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                // Queued jobs get here long after their readiness check, so look again right before sending.
                if (GetReadiness() is { Ok: false } readiness)
                {
                    throw new DownloadNotStartedException(readiness.Message);
                }

                await EnsureLoggedInAsync(force: attempt > 0, ct).ConfigureAwait(false);
                if (CooldownMessage() is { } paused)
                {
                    throw new DownloadNotStartedException(paused);
                }

                response = await FollowRedirectsAsync(
                        new Uri(BaseUri, $"/download/{Uri.EscapeDataString(id)}"),
                        () => sends++,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not DownloadNotStartedException
                && (sends == 0 || (sends == 1 && IsConnectFailure(ex)))
                && (ex is SourceException or HttpRequestException
                    || (ex is OperationCanceledException && !ct.IsCancellationRequested)))
            {
                throw new DownloadNotStartedException(ex.Message, ex);
            }

            if (IsLoginRedirect(response))
            {
                // Session expired: Laravel's auth middleware turned the request away before it could count.
                response.Dispose();
                _loggedIn = false;
                sends = 0;
                continue;
            }

            // 429/503 were already handled in SendAsync; check the status before reading anything into the content.
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new SourceUnavailableException($"Download {id} returned {(int)status}") { HttpStatus = (int)status };
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                // A page instead of the file: either the session quietly expired, or AudioVault is showing a message
                // (such as a limit). Only the first is worth another try.
                response.Dispose();
                if (await IsSignedInOrUnknownAsync(ct).ConfigureAwait(false))
                {
                    throw new SourceUnavailableException($"AudioVault answered download {id} with a web page instead of the file.");
                }

                _loggedIn = false;
                continue;
            }

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            var isArchive = mediaType.Contains("zip", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("rar", StringComparison.OrdinalIgnoreCase)
                || (fileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false);
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new SourceMedia(
                new ResponseStream(stream, response, owner),
                isArchive ? SourceMediaKind.Archive : SourceMediaKind.SingleAudio,
                fileName,
                response.Content.Headers.ContentLength);
        }

        const string message = "AudioVault did not accept the sign-in for this download.";
        RecordLoginFailure(message);
        throw sends == 0 ? new DownloadNotStartedException(message) : new SourceUnavailableException(message);
    }

    /// <summary>Failures that happen before a request is on the wire: nothing reached AudioVault.</summary>
    private static bool IsConnectFailure(Exception ex) =>
        ex is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError,
        };

    private async Task<bool> IsSignedInOrUnknownAsync(CancellationToken ct)
    {
        try
        {
            return await IsSignedInAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException)
        {
            return true;
        }
    }

    public async Task EnsureLoggedInAsync(bool force, CancellationToken ct)
    {
        if (_loggedIn && !force)
        {
            return;
        }

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loggedIn && !force)
            {
                return;
            }

            if (_account is null || _credential is not { IsUsable: true })
            {
                throw new SourceUnavailableException("No AudioVault account has been added for you.");
            }

            if (string.IsNullOrEmpty(_credential.Password))
            {
                await CheckRememberTokenAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await PasswordLoginAsync(_account.Login, _credential.Password, ct).ConfigureAwait(false);
            }

            _loggedIn = true;
            _loginFailures.Clear(_baseUrl, _account);
            _logger.LogInformation("Signed in to AudioVault as {Account}", _account.Key);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task PasswordLoginAsync(string login, string password, CancellationToken ct)
    {
        var loginPage = await GetPageAsync("/login", ct).ConfigureAwait(false);
        var token = ListingParser.ParseCsrfToken(loginPage)
            ?? throw new SourceFormatChangedException("Login page has no CSRF token");

        using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "/login"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["_token"] = token,
                        ["email"] = login.Trim(),
                        ["password"] = password,
                        ["remember"] = "on",
                    }),
                },
                HttpCompletionOption.ResponseHeadersRead,
                download: false,
                ct)
            .ConfigureAwait(false);

        // Laravel redirects after both good and bad logins (a bad one goes "back" to the referring page),
        // so the redirect proves nothing. Ask whether we're actually signed in now.
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) || !await IsSignedInAsync(ct).ConfigureAwait(false))
        {
            const string message = "AudioVault rejected the email and password.";
            RecordLoginFailure(message);
            throw new SourceUnavailableException(message);
        }
    }

    /// <summary>
    /// Laravel's logout also rotates the user's remember token, which invalidates the copy we stored.
    /// True only once AudioVault no longer accepts that token.
    /// </summary>
    public async Task<bool> SignOutAsync(CancellationToken ct)
    {
        var token = AudioVaultCredential.FromCookies(_cookies)?.Token ?? _credential?.Token;
        if (!await IsSignedInAsync(ct).ConfigureAwait(false))
        {
            _loggedIn = false;
            return true;
        }

        var csrf = ListingParser.ParseCsrfToken(await GetPageAsync("/movies", ct).ConfigureAwait(false))
            ?? throw new SourceFormatChangedException("No CSRF token found for logout");
        using (var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "/logout"))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_token"] = csrf }),
                },
                HttpCompletionOption.ResponseHeadersRead,
                download: false,
                ct)
            .ConfigureAwait(false))
        {
            _logger.LogInformation("Signed out of AudioVault as {Account} ({Status})", _account?.Key, (int)response.StatusCode);
        }

        _loggedIn = false;

        // Logout also deletes the token cookie here, which would make the check pass regardless: test the old token itself.
        if (token is not null)
        {
            try
            {
                _cookies.Add(token.ToCookie(IsHttps));
            }
            catch (CookieException)
            {
                return false;
            }
        }

        return !await IsSignedInAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Laravel's guest middleware redirects signed-in visitors away from /login and shows guests the form.
    /// Anything else proves neither, so it throws.
    /// </summary>
    private async Task<bool> IsSignedInAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => Get("/login"), HttpCompletionOption.ResponseContentRead, download: false, ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
        {
            return !IsLoginRedirect(response);
        }

        if (response.IsSuccessStatusCode
            && ListingParser.ParseCsrfToken(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is not null)
        {
            return false;
        }

        throw new SourceUnavailableException($"GET /login returned {(int)response.StatusCode}") { HttpStatus = (int)response.StatusCode };
    }

    private async Task CheckRememberTokenAsync(CancellationToken ct)
    {
        if (await IsSignedInAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        const string message = "Your AudioVault sign-in has expired. Enter your AudioVault email and password again.";
        RecordLoginFailure(message);
        throw new SourceUnavailableException(message);
    }

    private void RecordLoginFailure(string message)
    {
        if (_account is not null)
        {
            _loginFailures.Record(_baseUrl, _account, message);
        }
    }

    private CookieContainer NewCookieJar()
    {
        var jar = new CookieContainer();
        if (_credential?.Token is { } token)
        {
            try
            {
                jar.Add(token.ToCookie(IsHttps));
            }
            catch (CookieException ex)
            {
                _logger.LogWarning(ex, "Stored AudioVault sign-in token for {Account} is unusable", _account?.Key);
            }
        }

        return jar;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_downloadClient, _client))
        {
            _downloadClient.Dispose();
        }

        _client.Dispose();
        _loginLock.Dispose();
    }

    private Uri BaseUri => new(_baseUrl.TrimEnd('/') + "/");

    private bool IsHttps => BaseUri.Scheme == Uri.UriSchemeHttps;

    private string? CooldownMessage() => _cooldown.Active(_baseUrl, _options().FlareSolverrUrl);

    private HttpRequestMessage Get(string pathAndQuery) => new(HttpMethod.Get, new Uri(BaseUri, pathAndQuery));

    private async Task<HttpResponseMessage> FollowRedirectsAsync(Uri uri, Action onSending, CancellationToken ct)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            var target = uri;
            var response = await SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, target),
                    HttpCompletionOption.ResponseHeadersRead,
                    download: true,
                    ct,
                    onSending)
                .ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                || response.Headers.Location is null
                || IsLoginRedirect(response))
            {
                return response;
            }

            uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            response.Dispose();
            if (!IsTrustedRedirect(uri))
            {
                throw new SourceUnavailableException(
                    $"AudioVault redirected the download to {uri.Scheme}://{uri.Authority}, which is not AudioVault over https; not following it.");
            }
        }

        throw new SourceUnavailableException("Too many redirects while downloading");
    }

    /// <summary>Only AudioVault's own host or its subdomains, and never from https down to http.</summary>
    private bool IsTrustedRedirect(Uri uri)
    {
        var site = BaseUri;
        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || (!IsHttps && uri.Scheme == Uri.UriSchemeHttp);
        var host = uri.IdnHost;
        return schemeOk
            && (host.Equals(site.IdnHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + site.IdnHost, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoginRedirect(HttpResponseMessage response) =>
        response.Headers.Location is { } location
        && (location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString).TrimEnd('/').EndsWith("/login", StringComparison.OrdinalIgnoreCase);

    /// <param name="download">Allows longer for the headers, and only retries after a challenge Cloudflare marked as one.</param>
    /// <param name="onSending">Called right before each request goes on the wire.</param>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> make,
        HttpCompletionOption completion,
        bool download,
        CancellationToken ct,
        Action? onSending = null)
    {
        var options = _options();
        var timeout = download ? options.DownloadHeadersTimeout : options.PageTimeout;
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);
            if (CooldownMessage() is { } paused)
            {
                throw new SourceUnavailableException(paused);
            }

            using var request = make();
            ApplyHeaders(request);
            HttpResponseMessage response;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(timeout);
                onSending?.Invoke();
                try
                {
                    response = await (download ? _downloadClient : _client).SendAsync(request, completion, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new SourceUnavailableException($"AudioVault did not respond within {timeout.TotalSeconds:0} seconds.", ex) { Kind = FailureKind.Timeout };
                }
            }

            if (!IsChallenge(response, strict: download))
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var status = (int)response.StatusCode;
                    var retryAfter = RetryAfter(response);
                    var wait = retryAfter ?? SourceCooldown.Default;
                    response.Dispose();
                    var reason = status == 429 ? "AudioVault asked this server to slow down." : "AudioVault is temporarily unavailable.";
                    _cooldown.Trip(wait, reason, _baseUrl, fromProvider: retryAfter is not null);
                    _logger.LogWarning("AudioVault returned {Status} for {Url}; pausing all requests for {Wait}", status, request.RequestUri, wait);
                    throw new SourceUnavailableException($"{reason} ({status})") { HttpStatus = status };
                }

                return response;
            }

            response.Dispose();
            var flareSolverr = options.FlareSolverrUrl;
            if (attempt > 0 || string.IsNullOrWhiteSpace(flareSolverr))
            {
                const string message = "Cloudflare is blocking this server from AudioVault. Set a FlareSolverr URL in the plugin settings.";
                _cooldown.Trip(SourceCooldown.Default, message, _baseUrl, flareSolverr ?? string.Empty);
                throw new SourceUnavailableException(message) { Kind = FailureKind.SourceBlocked };
            }

            // Solve on the home page, never the request itself: FlareSolverr's browser loading a download URL would download it.
            _logger.LogInformation("Cloudflare challenge on {Url}; asking FlareSolverr for clearance", request.RequestUri);
            Clearance clearance;
            try
            {
                clearance = await _flareSolverr.SolveAsync(flareSolverr, BaseUri.ToString(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SourceException or HttpRequestException or JsonException or NotSupportedException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                var message = "Cloudflare is blocking this server from AudioVault and FlareSolverr could not get past it: " + ex.Message;
                _cooldown.Trip(SourceCooldown.Default, message, _baseUrl, flareSolverr);
                throw new SourceUnavailableException(message, ex) { Kind = FailureKind.SourceBlocked };
            }

            _userAgentOverride = clearance.UserAgent;
            foreach (var cookie in clearance.Cookies)
            {
                _cookies.Add(cookie);
            }
        }
    }

    /// <summary>Retry-After as seconds or a date; null when absent or unreadable.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };

    /// <summary>
    /// Cloudflare marks its challenges with cf-mitigated. Every response passes through Cloudflare, so a 403 that only
    /// has its Server header is also taken as one, except for downloads, where retrying a real refusal could count twice.
    /// </summary>
    private static bool IsChallenge(HttpResponseMessage response, bool strict) =>
        response.Headers.Contains("cf-mitigated")
        || (!strict
            && response.StatusCode is HttpStatusCode.Forbidden
            && response.Headers.Server.Any(s => s.Product?.Name.Equals("cloudflare", StringComparison.OrdinalIgnoreCase) ?? false));

    private void ApplyHeaders(HttpRequestMessage request)
    {
        // Cloudflare lets requests through that look like a browser; a bare client gets challenged.
        request.Headers.UserAgent.ParseAdd(_userAgentOverride ?? _options().UserAgent);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.9));
        request.Headers.Referrer = BaseUri;
    }

    private Task ThrottleAsync(CancellationToken ct) =>
        _throttle.WaitAsync(_options().MinRequestGap, _options().MaxRequestGap, ct);

    /// <param name="download">
    /// Downloads get their own connections that never resend a request on their own: SocketsHttpHandler quietly
    /// repeats a GET when the connection closes before any response, and AudioVault may have counted the first.
    /// </param>
    private HttpClient CreateClient(bool download) => new(_handlerFactory?.Invoke(_cookies) ?? new SocketsHttpHandler
    {
        CookieContainer = _cookies,
        UseCookies = true,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PlaintextStreamFilter = download ? (context, _) => ValueTask.FromResult<Stream>(new NoReplayStream(context.PlaintextStream)) : null,
    })
    {
        // Deadlines are per request in SendAsync: a download's body may legitimately take hours.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Keeps the HTTP response alive for as long as the body is being read.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response, IDisposable? owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                owner?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
