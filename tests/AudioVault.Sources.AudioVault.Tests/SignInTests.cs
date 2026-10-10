using System.Net;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Sources.AudioVault.Tests;

/// <summary>A fake AudioVault that behaves like Laravel's session login.</summary>
internal sealed class FakeLaravel(CookieContainer jar) : HttpMessageHandler
{
    public const string Email = "real@example.com";
    public const string Password = "correct horse";

    public bool IssueRememberToken { get; init; } = true;

    /// <summary>Laravel rotates the remember token on logout; false simulates a logout that didn't take.</summary>
    public bool RotateTokenOnLogout { get; init; } = true;

    /// <summary>Answers a request instead of the fake site when it returns a response.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; init; }

    public List<string> Requests { get; } = [];

    public List<Uri> Uris { get; } = [];

    public bool LoggedOut { get; private set; }

    /// <summary>Set when the session's HttpClient is disposed, which would cancel its requests still running.</summary>
    public bool Disposed { get; private set; }

    private bool _tokenRevoked;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Requests.Add($"{request.Method} {uri.AbsolutePath}");
        Uris.Add(uri);
        if (Intercept is not null && await Intercept(request, ct) is { } intercepted)
        {
            return intercepted;
        }

        var cookies = jar.GetCookies(uri);
        var signedIn = cookies["laravel_session"]?.Value == "auth"
            || (cookies["remember_web_abc"]?.Value == "token" && !_tokenRevoked);

        if (uri.AbsolutePath == "/login" && request.Method == HttpMethod.Get)
        {
            return signedIn ? Redirect("/home") : Html("<form><input type=\"hidden\" name=\"_token\" value=\"csrf\"></form>");
        }

        if (uri.AbsolutePath == "/login" && request.Method == HttpMethod.Post)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            var ok = form.Contains("email=real%40example.com", StringComparison.Ordinal)
                && form.Contains("password=correct+horse", StringComparison.Ordinal);
            if (!ok)
            {
                // Laravel's "back()": wherever the Referer pointed, often not /login.
                return Redirect(request.Headers.Referrer?.AbsolutePath ?? "/");
            }

            jar.Add(new Cookie("laravel_session", "auth", "/", uri.Host));
            if (IssueRememberToken)
            {
                jar.Add(new Cookie("remember_web_abc", "token", "/", uri.Host) { Expires = DateTime.UtcNow.AddYears(5) });
            }

            return Redirect("/home");
        }

        if (uri.AbsolutePath == "/logout" && request.Method == HttpMethod.Post)
        {
            LoggedOut = true;
            jar.Add(new Cookie("laravel_session", "guest", "/", uri.Host));
            if (RotateTokenOnLogout)
            {
                _tokenRevoked = true;
                foreach (Cookie c in jar.GetCookies(uri))
                {
                    if (c.Name.StartsWith("remember_web_", StringComparison.Ordinal))
                    {
                        c.Expired = true;
                    }
                }
            }

            return Redirect("/");
        }

        if (uri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal))
        {
            return signedIn ? Audio() : Redirect("/login");
        }

        if (uri.AbsolutePath == "/movies")
        {
            return Html("""
                <input type="hidden" name="_token" value="csrf"><table><tbody><tr><td>1</td><td>Alien (1979)</td><td><a href="https://av.test/download/1">Download</a></td></tr></tbody></table>
                """);
        }

        return Html("<html></html>");
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    public static HttpResponseMessage Audio() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/mpeg") } } };

    public static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };
}

public class SignInTests
{
    internal static (AudioVaultSource Source, List<FakeLaravel> Sites) Create(
        bool issueToken = true,
        bool rotateTokenOnLogout = true,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? intercept = null,
        Func<AudioVaultOptions, AudioVaultOptions>? configure = null)
    {
        var sites = new List<FakeLaravel>();
        var options = new AudioVaultOptions { BaseUrl = "https://av.test", MinRequestGap = TimeSpan.Zero, MaxRequestGap = TimeSpan.Zero };
        options = configure?.Invoke(options) ?? options;
        var source = new AudioVaultSource(
            () => options,
            new HttpClient(),
            NullLogger<AudioVaultSource>.Instance,
            jar =>
            {
                var site = new FakeLaravel(jar) { IssueRememberToken = issueToken, RotateTokenOnLogout = rotateTokenOnLogout, Intercept = intercept };
                lock (sites)
                {
                    sites.Add(site);
                }

                return site;
            });
        return (source, sites);
    }

    [Fact]
    public async Task WrongPasswordIsRejectedEvenThoughLaravelRedirectsAwayFromLogin()
    {
        var (source, _) = Create();
        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.SignInAsync("asd@aidjasif.dk", "garbage", default));
        Assert.Contains("rejected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoodLoginStoresTheRememberTokenNotThePassword()
    {
        var (source, _) = Create();
        var signIn = await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default);

        Assert.False(signIn.ContainsPassword);
        Assert.DoesNotContain(FakeLaravel.Password, signIn.Credential, StringComparison.Ordinal);
        Assert.Contains("remember_web_abc", signIn.Credential, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutRememberTokenThePasswordIsKept()
    {
        var (source, _) = Create(issueToken: false);
        var signIn = await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default);

        Assert.True(signIn.ContainsPassword);
    }

    [Fact]
    public async Task StoredTokenSignsInWithoutPassword()
    {
        var (source, sites) = Create();
        var signIn = await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default);

        // A fresh source, as after a server restart: only the stored credential is available.
        var (restarted, restartedSites) = Create();
        var account = new SourceAccount(FakeLaravel.Email, signIn.Credential);
        var health = await restarted.CheckHealthAsync(account, default);

        Assert.True(health.Ok, health.Message);
        Assert.DoesNotContain(restartedSites.SelectMany(s => s.Requests), r => r == "POST /login");
    }

    [Fact]
    public async Task ExpiredTokenAsksToSignInAgain()
    {
        var (source, _) = Create();
        var stale = new AudioVaultCredential(new AudioVaultCredential.RememberToken("remember_web_abc", "revoked", "av.test", "/", null), null);
        var health = await source.CheckHealthAsync(new SourceAccount(FakeLaravel.Email, stale.Serialize()), default);

        Assert.False(health.Ok);
        Assert.Contains("expired", health.Message, StringComparison.Ordinal);
        Assert.False(source.GetReadiness(new SourceAccount(FakeLaravel.Email, stale.Serialize())).Ok);
    }

    [Fact]
    public async Task SignOutPostsLogoutAtTheSite()
    {
        var (source, sites) = Create();
        var signIn = await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default);

        var confirmed = await source.SignOutAsync(new SourceAccount(FakeLaravel.Email, signIn.Credential), default);

        Assert.Contains(sites, s => s.LoggedOut);
        Assert.True(confirmed);
    }

    [Fact]
    public async Task SignOutIsNotConfirmedWhileTheTokenStillWorks()
    {
        var (source, sites) = Create(rotateTokenOnLogout: false);
        var signIn = await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default);

        var confirmed = await source.SignOutAsync(new SourceAccount(FakeLaravel.Email, signIn.Credential), default);

        Assert.Contains(sites, s => s.LoggedOut);
        Assert.False(confirmed);
    }

    [Fact]
    public async Task SignOutFailureIsReportedNotThrown()
    {
        var (source, _) = Create();
        var signIn = await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default);
        var (broken, _) = Create(intercept: (_, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.False(await broken.SignOutAsync(new SourceAccount(FakeLaravel.Email, signIn.Credential), default));
    }

    [Fact]
    public void RestoredTokenOnlyTravelsOverHttps()
    {
        // Stored before the Secure flag was kept.
        var old = AudioVaultCredential.Parse("""{"t":{"n":"remember_web_abc","v":"token","d":"av.test","p":"/","e":null},"pw":null}""")!;
        var jar = new CookieContainer();
        jar.Add(old.Token!.ToCookie(https: true));

        Assert.Empty(jar.GetCookies(new Uri("http://av.test/")));
        Assert.Single(jar.GetCookies(new Uri("https://av.test/")));
        Assert.True(AudioVaultCredential.FromCookies(jar)!.Token!.Secure);
    }
}
