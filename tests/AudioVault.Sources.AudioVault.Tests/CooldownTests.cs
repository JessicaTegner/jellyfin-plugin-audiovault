using System.Net;
using System.Net.Http.Headers;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

/// <summary>Provider pauses: how long they last, and that a restart doesn't end them.</summary>
public sealed class CooldownTests : IDisposable
{
    private const string Site = "https://av.test";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "av-cooldown-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ProviderRetryAfterBeyondADayIsHonored()
    {
        var time = new ManualTime();
        var cooldown = new SourceCooldown(time);

        cooldown.Trip(TimeSpan.FromDays(3), "Slow down.", Site, fromProvider: true);
        time.Now += TimeSpan.FromDays(2.9);

        Assert.NotNull(cooldown.Active(Site, null));
        time.Now += TimeSpan.FromDays(0.2);
        Assert.Null(cooldown.Active(Site, null));
    }

    [Fact]
    public void LocallyChosenPausesAreCappedAtADay()
    {
        var time = new ManualTime();
        var cooldown = new SourceCooldown(time);

        cooldown.Trip(TimeSpan.FromDays(3), "Blocked.", Site);
        time.Now += TimeSpan.FromHours(25);

        Assert.Null(cooldown.Active(Site, null));
    }

    [Fact]
    public void PauseSurvivesARestart()
    {
        var time = new ManualTime();
        new SourceCooldown(time, () => _directory).Trip(TimeSpan.FromHours(2), "Slow down.", Site, fromProvider: true);

        var restarted = new SourceCooldown(time, () => _directory);
        Assert.StartsWith("Slow down.", restarted.Active(Site, null), StringComparison.Ordinal);

        time.Now += TimeSpan.FromHours(2);
        Assert.Null(restarted.Active(Site, null));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"Until":"2026-10-10T13:00:00+00:00","Message":null,"BaseUrl":null}""")]
    public void UnreadableStateIsIgnored(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, SourceCooldown.FileName), content);
        var cooldown = new SourceCooldown(new ManualTime(), () => _directory);

        Assert.Null(cooldown.Active(Site, null));

        // And it is replaced by the next pause.
        cooldown.Trip(TimeSpan.FromHours(1), "Slow down.", Site);
        Assert.NotNull(new SourceCooldown(new ManualTime(), () => _directory).Active(Site, null));
    }

    [Fact]
    public void WithoutAStateDirectoryNothingIsWritten()
    {
        new SourceCooldown(new ManualTime(), () => null).Trip(TimeSpan.FromHours(1), "Slow down.", Site);

        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task RestartedSourceStaysPausedAndSendsNothing()
    {
        var (source, _) = SignInTests.Create(
            intercept: (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(2)) } }
                    : null),
            configure: o => o with { StateDirectory = _directory });
        var account = new SourceAccount(FakeLaravel.Email, (await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default)).Credential);
        await Assert.ThrowsAsync<SourceUnavailableException>(() => source.OpenAsync("1", account, default));

        var (restarted, sites) = SignInTests.Create(configure: o => o with { StateDirectory = _directory });

        var readiness = restarted.GetReadiness(account);
        Assert.False(readiness.Ok);
        Assert.Contains($"{DateTimeOffset.UtcNow.AddDays(2).ToLocalTime():yyyy-MM-dd}", readiness.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => restarted.OpenAsync("2", account, default));
        Assert.False((await restarted.CheckHealthAsync(null, default)).Ok);
        Assert.Empty(sites.SelectMany(s => s.Requests));
    }
}
