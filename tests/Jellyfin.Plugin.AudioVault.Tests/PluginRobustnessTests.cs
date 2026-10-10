using Jellyfin.Plugin.AudioVault.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class FailureDeliveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromHours(6);
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();

    private static PendingFailure Failure(PlayState prior) =>
        new(Now, "alice", Guid.NewGuid(), "blocked", new Dictionary<Guid, PlayState> { [Item] = prior });

    [Fact]
    public void StopAfterTheFallbackMessageStillRestoresTheState()
    {
        var watched = new PlayState(true, 2, 0, Now.UtcDateTime.AddDays(-1));
        var book = new PendingFailures();
        var failure = Failure(watched);
        book.Add(failure, [Item, Version]);

        // The 20-second fallback shows the message; the player reports stopping a minute later.
        Assert.True(book.MarkDelivered(failure));
        var taken = book.Take("alice", Item, null, Now + TimeSpan.FromMinutes(1), RestoreWindow, out var delivered);

        Assert.True(delivered);
        Assert.Equal(watched, taken!.PriorStates[Item]);
        Assert.Null(book.Take("alice", Version, Now + TimeSpan.FromMinutes(1), RestoreWindow));
    }

    [Fact]
    public void StopBeforeTheFallbackTakesTheMessageToo()
    {
        var book = new PendingFailures();
        var failure = Failure(new PlayState(false, 0, 0, null));
        book.Add(failure, [Item]);

        Assert.NotNull(book.Take("alice", Item, null, Now, RestoreWindow, out var delivered));
        Assert.False(delivered);

        // The fallback finds it gone and says nothing.
        Assert.False(book.MarkDelivered(failure));
    }

    [Fact]
    public void FallbackDeliversOnlyOnce()
    {
        var book = new PendingFailures();
        var failure = Failure(new PlayState(false, 0, 0, null));
        book.Add(failure, [Item]);

        Assert.True(book.MarkDelivered(failure));
        Assert.False(book.MarkDelivered(failure));
        Assert.NotNull(book.Find("alice", Item, out var delivered));
        Assert.True(delivered);
    }

    [Fact]
    public void RestorationExpiresAfterItsWindow()
    {
        var book = new PendingFailures();
        var failure = Failure(new PlayState(false, 0, 0, null));
        book.Add(failure, [Item]);
        book.MarkDelivered(failure);

        book.Prune(Now + RestoreWindow + TimeSpan.FromSeconds(1), RestoreWindow);

        Assert.Null(book.Find("alice", Item, out _));
    }

    private static PendingFailure Attempt(Guid user, string? playSessionId) =>
        new(Now, "alice", user, "blocked", new Dictionary<Guid, PlayState> { [Item] = new PlayState(true, 1, 0, null) })
        {
            PlaySessionId = playSessionId,
        };

    [Fact]
    public void SuccessfulRetryAfterTheFallbackRestoresNothingOnItsStop()
    {
        var user = Guid.NewGuid();
        var book = new PendingFailures();
        book.Issued("blocked-play", Now);
        var failure = Attempt(user, "blocked-play");
        book.Add(failure, [Item, Version]);
        Assert.True(book.MarkDelivered(failure));

        // The user fixes their account and plays again: that play goes ahead.
        book.Issued("good-play", Now);
        Assert.Equal(1, book.RetireAfterSuccess("alice", user, [Item, Version], "good-play"));

        Assert.Null(book.Take("alice", Item, "good-play", Now + TimeSpan.FromMinutes(5), RestoreWindow, out _));
        Assert.False(book.MarkDelivered(failure));
    }

    [Fact]
    public void LateStopOfTheBlockedAttemptAfterARetryIsIgnored()
    {
        var user = Guid.NewGuid();
        var book = new PendingFailures();
        book.Issued("blocked-play", Now);
        book.Add(Attempt(user, "blocked-play"), [Item]);

        book.Issued("good-play", Now);
        book.RetireAfterSuccess("alice", user, [Item], "good-play");
        Assert.Null(book.Take("alice", Item, "blocked-play", Now + TimeSpan.FromMinutes(5), RestoreWindow, out _));
        Assert.Null(book.Take("alice", Item, null, Now + TimeSpan.FromMinutes(5), RestoreWindow, out _));
    }

    [Fact]
    public void StartOfAnotherAttemptRetiresButTheBlockedOnesOwnStartDoesNot()
    {
        var book = new PendingFailures();
        book.Issued("blocked-play", Now);
        book.Add(Attempt(Guid.NewGuid(), "blocked-play"), [Item]);

        Assert.False(book.RetireOnStart("alice", Item, "blocked-play"));
        Assert.False(book.RetireOnStart("alice", Item, null));
        Assert.NotNull(book.Find("alice", Item, out _));

        book.Issued("good-play", Now);
        Assert.True(book.RetireOnStart("alice", Item, "good-play"));
        Assert.Null(book.Take("alice", Item, "blocked-play", Now, RestoreWindow, out _));
    }

    [Fact]
    public void StopOfAnotherAttemptLeavesTheFailureForItsOwnStop()
    {
        var book = new PendingFailures();
        var failure = Attempt(Guid.NewGuid(), "blocked-play");
        book.Add(failure, [Item]);

        Assert.Null(book.Take("alice", Item, "other-play", Now, RestoreWindow, out _));
        Assert.Same(failure, book.Take("alice", Item, "blocked-play", Now, RestoreWindow, out _));
    }

    [Fact]
    public void AnotherUsersSuccessDoesNotRetireAFailure()
    {
        var book = new PendingFailures();
        var failure = new PendingFailure(Now, null, Guid.NewGuid(), "blocked", new Dictionary<Guid, PlayState>());
        book.Add(failure, [Item]);

        book.Issued("bobs-play", Now + TimeSpan.FromSeconds(1));
        Assert.Equal(0, book.RetireAfterSuccess("bob", Guid.NewGuid(), [Item], "bobs-play"));
        Assert.Same(failure, book.Take("alice", Item, Now, RestoreWindow));
    }

    [Fact]
    public void DelayedStartOfAnEarlierBlockedAttemptKeepsTheNewerFailure()
    {
        // Two blocked attempts, P1 then P2, on the same device and item; P1's placeholder start arrives late.
        var user = Guid.NewGuid();
        var book = new PendingFailures();
        book.Issued("P1", Now);
        var first = Attempt(user, "P1");
        book.Add(first, [Item]);
        book.Issued("P2", Now);
        var second = Attempt(user, "P2");
        Assert.True(book.IsNewAttempt("P2", first, delivered: false));
        book.Add(second, [Item]);

        Assert.False(book.RetireOnStart("alice", Item, "P1"));

        Assert.Same(second, book.Find("alice", Item, out _));
        Assert.Same(second, book.Take("alice", Item, "P2", Now, RestoreWindow, out _));
    }

    [Fact]
    public void DelayedEventsOfAnOlderPlayNeitherRetireNorReplaceANewerFailure()
    {
        // P0 went ahead earlier; then the account was removed and P1 was blocked. P0's player is still fetching.
        var user = Guid.NewGuid();
        var book = new PendingFailures();
        book.Issued("P0", Now);
        book.Issued("P1", Now);
        var blocked = Attempt(user, "P1");
        book.Add(blocked, [Item]);

        Assert.Equal(0, book.RetireAfterSuccess("alice", user, [Item], "P0"));
        Assert.False(book.RetireOnStart("alice", Item, "P0"));
        Assert.False(book.IsNewAttempt("P0", blocked, delivered: true));
        Assert.Same(blocked, book.Find("alice", Item, out _));
    }

    [Fact]
    public void AStartThatWasNeverIssuedRetiresNothing()
    {
        var book = new PendingFailures();
        book.Issued("P1", Now);
        book.Add(Attempt(Guid.NewGuid(), "P1"), [Item]);

        Assert.False(book.RetireOnStart("alice", Item, "unknown"));
        Assert.NotNull(book.Find("alice", Item, out _));
    }

    [Fact]
    public void RepeatedFetchesOfOneAttemptAreNotNewAttempts()
    {
        var book = new PendingFailures();
        book.Issued("P1", Now);
        var failure = Attempt(Guid.NewGuid(), "P1");
        book.Add(failure, [Item]);

        Assert.False(book.IsNewAttempt("P1", failure, delivered: false));
        Assert.False(book.IsNewAttempt("P1", failure, delivered: true));

        // Without PlaySessionIds attempts can't be told apart: after the message, another fetch is another try.
        Assert.False(book.IsNewAttempt(null, failure, delivered: false));
        Assert.True(book.IsNewAttempt(null, failure, delivered: true));
    }
}

public class GuardedHostedServiceTests
{
    private sealed class Broken : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Working : IHostedService
    {
        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailsToStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new IOException("state file unreadable");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (IHost Host, PluginHealth Health) Build(Action<IServiceCollection> register)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<PluginHealth>();
        register(builder.Services);
        var host = builder.Build();
        return (host, host.Services.GetRequiredService<PluginHealth>());
    }

    [Fact]
    public async Task HostStartsWhenAServiceCantBeCreated()
    {
        var (host, health) = Build(s =>
        {
            // Like a store whose state file can't be read, thrown while the service is being constructed.
            s.AddSingleton<Broken>(_ => throw new InvalidDataException("catalog.json is damaged"));
            s.AddHostedService<GuardedHostedService<Broken>>();
        });

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains("only partly working", health.Problem);
        Assert.Contains("catalog.json is damaged", health.Problem);
    }

    [Fact]
    public async Task HostStartsWhenAServiceFailsToStart()
    {
        var (host, health) = Build(s =>
        {
            s.AddSingleton<FailsToStart>();
            s.AddHostedService<GuardedHostedService<FailsToStart>>();
        });

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains("state file unreadable", health.Problem);
    }

    [Fact]
    public async Task WorkingServicesStartAndStopAsBefore()
    {
        var (host, health) = Build(s =>
        {
            s.AddSingleton<Working>();
            s.AddHostedService<GuardedHostedService<Working>>();
        });

        await host.StartAsync();
        await host.StopAsync();

        var working = host.Services.GetRequiredService<Working>();
        Assert.True(working.Started);
        Assert.True(working.Stopped);
        Assert.Null(health.Problem);
    }

    [Fact]
    public void ProblemsAreClearedWhenAPartRecovers()
    {
        var health = new PluginHealth();
        health.Failed("A", "A broke.");
        Assert.NotNull(health.Problem);

        health.Recovered("A");
        Assert.Null(health.Problem);
    }
}
