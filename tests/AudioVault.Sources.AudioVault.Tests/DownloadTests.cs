using System.Net;
using System.Net.Http.Headers;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

/// <summary>What a failed download open means for the account's daily quota, and when AudioVault is left alone.</summary>
public class DownloadTests
{
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>> OnDownload(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        async (request, ct) => request.RequestUri!.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal)
            ? await respond(request, ct)
            : null;

    private static async Task<SourceAccount> SignInAsync(AudioVaultSource source) =>
        new(FakeLaravel.Email, (await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default)).Credential);

    private static int Downloads(List<FakeLaravel> sites) =>
        sites.ToList().SelectMany(s => s.Requests).Count(r => r.StartsWith("GET /download/", StringComparison.Ordinal));

    [Fact]
    public async Task SignedInDownloadOpens()
    {
        var (source, _) = SignInTests.Create();
        await using var media = await source.OpenAsync("1", await SignInAsync(source), default);

        Assert.Equal(SourceMediaKind.SingleAudio, media.Kind);
    }

    [Theory]
    [InlineData("https://evil.test/file.mp3")]
    [InlineData("https://av.test.evil/file.mp3")]
    [InlineData("http://av.test/file.mp3")]
    [InlineData("http://127.0.0.1/admin")]
    public async Task RedirectsLeavingAudioVaultOrHttpsAreRefused(string location)
    {
        var (source, sites) = SignInTests.Create(intercept: OnDownload((_, _) => Task.FromResult(FakeLaravel.Redirect(location))));
        var account = await SignInAsync(source);

        // The download was requested, so it may count: a plain failure, not "not started".
        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));
        Assert.Contains("not following", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sites.SelectMany(s => s.Uris), u => u.ToString() == location);
    }

    [Fact]
    public async Task RedirectToAudioVaultSubdomainIsFollowed()
    {
        var (source, sites) = SignInTests.Create(intercept: (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host switch
        {
            "cdn.av.test" => FakeLaravel.Audio(),
            _ when request.RequestUri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal) =>
                FakeLaravel.Redirect("https://cdn.av.test/file.mp3"),
            _ => null,
        }));

        await using var media = await source.OpenAsync("1", await SignInAsync(source), default);

        Assert.Contains(sites.SelectMany(s => s.Uris), u => u.Host == "cdn.av.test");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RateLimitPausesTheWholeSourceAndBlocksQueuedDownloads(HttpStatusCode status)
    {
        var (source, sites) = SignInTests.Create(intercept: OnDownload((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)) },
        })));
        var account = await SignInAsync(source);

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));
        Assert.Equal(1, Downloads(sites));

        // Another account on the same server is paused too: the limit is on this server's IP.
        var other = new SourceAccount("other@example.com", account.Credential);
        Assert.False(source.GetReadiness(account).Ok);
        Assert.False(source.GetReadiness(other).Ok);

        // A job that was already queued sends nothing and may be refunded.
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("2", other, default));
        Assert.False((await source.CheckHealthAsync(null, default)).Ok);
        Assert.Equal(1, Downloads(sites));
    }

    [Fact]
    public async Task RetryAfterDateIsHonored()
    {
        var (source, _) = SignInTests.Create(intercept: OnDownload((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(2)) },
        })));
        var account = await SignInAsync(source);

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));

        var readiness = source.GetReadiness(account);
        Assert.False(readiness.Ok);
        Assert.True(readiness.Message.Contains($"until {DateTimeOffset.UtcNow.AddHours(2).ToLocalTime():HH}", StringComparison.Ordinal), readiness.Message);
    }

    [Fact]
    public async Task HtmlErrorStatusIsNotMistakenForAnExpiredSession()
    {
        var (source, sites) = SignInTests.Create(intercept: OnDownload((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html>Daily limit reached</html>", System.Text.Encoding.UTF8, "text/html"),
        })));
        var account = await SignInAsync(source);
        var requests = sites.Sum(s => s.Requests.Count);

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));

        // Straight to the download and no further: no re-login, no second attempt.
        Assert.Equal(1, Downloads(sites));
        Assert.Equal(requests + 2, sites.Sum(s => s.Requests.Count));
    }

    [Fact]
    public async Task HtmlPageWhileSignedInIsNotRetried()
    {
        var (source, sites) = SignInTests.Create(intercept: OnDownload((_, _) => Task.FromResult(FakeLaravel.Html("<html>Come back tomorrow</html>"))));
        var account = await SignInAsync(source);

        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));

        Assert.Equal(1, Downloads(sites));
    }

    [Fact]
    public async Task CloudflareBlockPausesTheSource()
    {
        var (source, sites) = SignInTests.Create(intercept: (_, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Headers = { { "cf-mitigated", "challenge" } },
        }));

        Assert.False((await source.CheckHealthAsync(null, default)).Ok);
        var sent = sites.Sum(s => s.Requests.Count);
        Assert.False((await source.CheckHealthAsync(null, default)).Ok);

        Assert.Equal(sent, sites.Sum(s => s.Requests.Count));
    }

    [Fact]
    public async Task ExpiredSignInMeansNotStarted()
    {
        var (source, sites) = SignInTests.Create();
        var stale = new AudioVaultCredential(new AudioVaultCredential.RememberToken("remember_web_abc", "revoked", "av.test", "/", null), null);

        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("1", new SourceAccount(FakeLaravel.Email, stale.Serialize()), default));

        Assert.Equal(0, Downloads(sites));
    }

    [Fact]
    public async Task StalledDownloadTimesOutAndMayHaveCounted()
    {
        var (source, _) = SignInTests.Create(
            intercept: OnDownload(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return FakeLaravel.Audio();
            }),
            configure: o => o with { DownloadHeadersTimeout = TimeSpan.FromMilliseconds(200) });
        var account = await SignInAsync(source);

        var ex = await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));
        Assert.Contains("did not respond", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalledSignInCheckBeforeTheDownloadMeansNotStarted()
    {
        var (source, _) = SignInTests.Create();
        var account = await SignInAsync(source);
        var (restarted, sites) = SignInTests.Create(
            intercept: async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath == "/login")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return null;
            },
            configure: o => o with { PageTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAsync<DownloadNotStartedException>(() => restarted.OpenAsync("1", account, default));
        Assert.Equal(0, Downloads(sites));
    }
}
