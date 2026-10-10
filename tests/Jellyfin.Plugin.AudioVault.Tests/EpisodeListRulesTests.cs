using AudioVault.Core.Library;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class EpisodeListRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SeasonListDropsSpecialsAndDuplicatesAndSorts() =>
        Assert.Equal([1, 2, 3, 4], EpisodeListRules.EpisodeNumbers([3, 0, 1, 2, 2, 4, -1]));

    [Fact]
    public void SparseNumberingIsKept() =>
        Assert.Equal([1, 2, 5, 9], EpisodeListRules.EpisodeNumbers([9, 5, 2, 1]));

    [Fact]
    public void AbsurdNumbersAreDropped() =>
        Assert.Equal([1], EpisodeListRules.EpisodeNumbers([1, EpisodeCountStore.MaxEpisodes + 1L, long.MaxValue]));

    [Fact]
    public void OnlySpecialsMeansNothingFound()
    {
        var numbers = EpisodeListRules.EpisodeNumbers([0, 0]);

        Assert.Empty(numbers);
        Assert.Null(EpisodeListRules.ToCount(numbers));
    }

    [Fact]
    public void FoundListIsStoredAsMetadataWithItsNumbers()
    {
        var count = EpisodeListRules.ToCount([1, 2, 5]);

        Assert.Equal(new EpisodeCount(3, EpisodeCountOrigin.Metadata, [1, 2, 5]), count);
        Assert.Equal([1, 2, 5], count!.EpisodeNumbers());
    }

    [Theory]
    [InlineData(true, "1399", false, null, EpisodeListMethod.SeasonList)]
    [InlineData(true, "1399", true, "", EpisodeListMethod.SeasonList)]
    [InlineData(true, "1399", false, "absolute", EpisodeListMethod.PerEpisode)]
    [InlineData(true, "1399", false, "dvd", EpisodeListMethod.PerEpisode)]
    [InlineData(true, null, false, null, EpisodeListMethod.WaitForIdentification)]
    [InlineData(true, "", false, null, EpisodeListMethod.WaitForIdentification)]
    [InlineData(true, "tt0944947", false, null, EpisodeListMethod.WaitForIdentification)]
    [InlineData(true, "0", false, null, EpisodeListMethod.WaitForIdentification)]
    [InlineData(true, null, true, null, EpisodeListMethod.PerEpisode)]
    [InlineData(true, "0", true, null, EpisodeListMethod.PerEpisode)]
    [InlineData(false, "1399", true, null, EpisodeListMethod.WaitForIdentification)]
    public void ChoosesTheLookup(bool seriesFound, string? tmdbId, bool otherIds, string? displayOrder, EpisodeListMethod expected) =>
        Assert.Equal(expected, EpisodeListRules.Choose(seriesFound, tmdbId, otherIds, displayOrder));

    [Fact]
    public void OtherIdsIgnoreTmdbAndBlanksAndAreStable()
    {
        var a = EpisodeListRules.OtherIds(new Dictionary<string, string> { ["Tvdb"] = "81189", ["Tmdb"] = "1396", ["Imdb"] = " " }, "Tmdb");
        var b = EpisodeListRules.OtherIds(new Dictionary<string, string> { ["Imdb"] = "tt0903747", ["Tvdb"] = "81189" }, "Tmdb");

        Assert.Equal("Tvdb=81189", a);
        Assert.Equal("Imdb=tt0903747;Tvdb=81189", b);
        Assert.Null(EpisodeListRules.OtherIds(new Dictionary<string, string> { ["Tmdb"] = "1396" }, "Tmdb"));
    }

    [Fact]
    public void FailedSeasonWaitsUnlessTheShowIsIdentifiedDifferently()
    {
        var attempts = new EpisodeListAttempts();
        var key = EpisodeListRules.LookupKey("1399", null);
        Assert.True(attempts.IsDue("10", key, Now));

        attempts.Record("10", key, Now.AddHours(12));

        Assert.False(attempts.IsDue("10", key, Now.AddHours(1)));
        Assert.True(attempts.IsDue("10", key, Now.AddHours(12)));
        Assert.True(attempts.IsDue("10", EpisodeListRules.LookupKey("1400", null), Now.AddHours(1)));
        Assert.True(attempts.IsDue("10", EpisodeListRules.LookupKey("1399", "absolute"), Now.AddHours(1)));
        Assert.True(attempts.IsDue("11", key, Now.AddHours(1)));

        attempts.Forget("10");
        Assert.True(attempts.IsDue("10", key, Now.AddHours(1)));
    }

    [Fact]
    public void FirstRoutinePassAfterStartupThenEveryInterval()
    {
        var plan = Plan();
        plan.Start(Now, TimeSpan.FromMinutes(10));

        Assert.False(plan.ShouldStart(Now.AddMinutes(9), scanRunning: false));
        Assert.True(plan.ShouldStart(Now.AddMinutes(10), scanRunning: false));

        plan.Started(Now.AddMinutes(10));
        Assert.False(plan.ShouldStart(Now.AddHours(4), scanRunning: false));
        Assert.True(plan.ShouldStart(Now.AddMinutes(10).AddHours(4), scanRunning: false));
    }

    [Fact]
    public void NeverStartsDuringALibraryScan()
    {
        var plan = Started();
        plan.Request(Now, TimeSpan.FromMinutes(1));

        Assert.False(plan.ShouldStart(Now.AddMinutes(5), scanRunning: true));
        Assert.True(plan.ShouldStart(Now.AddMinutes(5), scanRunning: false));
    }

    [Fact]
    public void AfterAScanWaitsAWhileEvenWhenAlreadyDue()
    {
        var plan = Started();
        plan.Request(Now, TimeSpan.FromMinutes(1));

        // The scan finished at minute 30, long after the request fell due.
        plan.HoldUntil(Now.AddMinutes(32));
        plan.Request(Now.AddMinutes(30), TimeSpan.FromMinutes(2));

        Assert.False(plan.ShouldStart(Now.AddMinutes(31), scanRunning: false));
        Assert.True(plan.ShouldStart(Now.AddMinutes(32), scanRunning: false));
    }

    [Fact]
    public void RequestsInQuickSuccessionShareOnePass()
    {
        var plan = Started();
        plan.Request(Now, TimeSpan.FromMinutes(1));
        plan.Request(Now.AddSeconds(40), TimeSpan.FromMinutes(1));

        Assert.False(plan.ShouldStart(Now.AddMinutes(1), scanRunning: false));
        Assert.True(plan.ShouldStart(Now.AddSeconds(100), scanRunning: false));

        plan.Started(Now.AddSeconds(100));
        Assert.False(plan.ShouldStart(Now.AddMinutes(30), scanRunning: false));
    }

    [Fact]
    public void ASteadyTrickleOfRequestsStillStartsAPass()
    {
        var plan = Started();
        for (var s = 0; s <= 900; s += 30)
        {
            plan.Request(Now.AddSeconds(s), TimeSpan.FromMinutes(1));
        }

        // Every request pushes it back, but not beyond ten minutes after the first.
        Assert.True(plan.ShouldStart(Now.AddMinutes(10), scanRunning: false));
    }

    [Fact]
    public void RequestDuringAPassStartsAnotherAfterIt()
    {
        var plan = Started();
        plan.Request(Now, TimeSpan.FromMinutes(1));
        plan.Started(Now.AddMinutes(1));

        plan.Request(Now.AddMinutes(5), TimeSpan.FromMinutes(1));

        Assert.True(plan.ShouldStart(Now.AddMinutes(6), scanRunning: false));
    }

    [Fact]
    public void FailuresAreKeptApartFromEmptyAnswers()
    {
        var attempts = new EpisodeListAttempts();
        var key = EpisodeListRules.LookupKey("1399", null);
        attempts.Record("10", key, Now + EpisodeListRules.RetryNothingFound, failed: false);
        attempts.Record("11", key, Now + EpisodeListRules.RetryFailed, failed: true);
        attempts.Record("12", key, Now.AddMinutes(30), failed: true);

        Assert.True(attempts.Tried("10"));
        Assert.False(attempts.Failed("10"));
        Assert.True(attempts.Failed("11"));
        Assert.False(attempts.Tried("11"));
        Assert.Equal(Now.AddMinutes(30), attempts.NextFailedRetry());
        Assert.True(attempts.IsDue("11", key, Now.AddHours(1)));

        // A later empty answer replaces the failure.
        attempts.Record("11", key, Now.AddHours(12), failed: false);
        attempts.Forget("12");
        Assert.True(attempts.Tried("11"));
        Assert.Null(attempts.NextFailedRetry());
    }

    [Fact]
    public void FailedLookupsBringThePassForward()
    {
        var plan = Started();
        Assert.False(plan.ShouldStart(Now.AddHours(1), scanRunning: false));

        plan.RetryAt(Now.AddHours(1));
        Assert.True(plan.ShouldStart(Now.AddHours(1), scanRunning: false));
        Assert.False(plan.ShouldStart(Now.AddHours(1), scanRunning: true));

        // A later retry time doesn't push the routine pass back.
        plan.Started(Now.AddHours(1));
        plan.RetryAt(Now.AddHours(9));
        Assert.True(plan.ShouldStart(Now.AddHours(5), scanRunning: false));
    }

    [Fact]
    public void ARetryAPassAlreadyCoveredStartsNothing()
    {
        // A failed season whose show is no longer identified keeps its old retry time; that mustn't start a pass every tick.
        var plan = Started();
        plan.RetryAt(Now.AddHours(-2));
        Assert.False(plan.ShouldStart(Now, scanRunning: false));
    }

    [Fact]
    public void AShowsKeyFollowsItsIdentity()
    {
        var tmdb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "1399", ["Tvdb"] = "121361" };
        Assert.Equal(EpisodeListRules.LookupKey("1399", null), EpisodeListRules.ShowKey(tmdb, null));
        Assert.Equal(EpisodeListRules.LookupKey("1399", "absolute"), EpisodeListRules.ShowKey(tmdb, "absolute"));

        // A scan re-identifies the show while a lookup runs: the answer is for another show.
        var renamed = new Dictionary<string, string>(tmdb) { ["Tmdb"] = "1400" };
        Assert.NotEqual(EpisodeListRules.ShowKey(tmdb, null), EpisodeListRules.ShowKey(renamed, null));

        var tvdbOnly = new Dictionary<string, string> { ["Tvdb"] = "121361" };
        Assert.Equal(EpisodeListRules.LookupKey("Tvdb=121361", null), EpisodeListRules.ShowKey(tvdbOnly, null));
    }

    [Fact]
    public async Task PassesWaitForALibraryScan()
    {
        // Review round 6, finding 9: every pass, the daily and manual ones too, waits while a scan runs.
        var checks = 0;
        var waited = await ScanGate.WaitWhileAsync(() => ++checks < 3, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.True(waited);
        Assert.Equal(3, checks);

        Assert.False(await ScanGate.WaitWhileAsync(() => false, TimeSpan.FromMinutes(1), CancellationToken.None));

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanGate.WaitWhileAsync(() => true, TimeSpan.FromMinutes(1), cancel.Token));
    }

    [Fact]
    public async Task AnOutageOnEpisodeOneIsAFailureNotAnEmptySeason()
    {
        // Review round 7, finding 12: the provider manager turned this into an empty result and "nothing found".
        var tmdb = Source("TheMovieDb", _ => throw new HttpRequestException("TMDb unreachable"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Count([tmdb]));
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.True(EpisodeCounter.IsFailure(error, CancellationToken.None));
    }

    [Fact]
    public async Task AnOutageAfterEpisodeThreeDoesNotRecordAShortSeason()
    {
        var tmdb = Source("TheMovieDb", ep => ep <= 3 ? true : throw new HttpRequestException("TMDb unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Count([tmdb]));
    }

    [Fact]
    public async Task AProviderTimeoutIsAFailure()
    {
        var tmdb = Source("TheMovieDb", ep => ep <= 2 ? true : throw new TaskCanceledException("HttpClient.Timeout elapsed"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Count([tmdb]));
        Assert.IsType<TaskCanceledException>(error.InnerException);

        // At the pass level too: a timeout is a failed lookup; only the pass's own cancellation isn't.
        Assert.True(EpisodeCounter.IsFailure(new TaskCanceledException(), CancellationToken.None));
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        Assert.False(EpisodeCounter.IsFailure(new OperationCanceledException(stopping.Token), stopping.Token));
        Assert.True(EpisodeCounter.IsFailure(new HttpRequestException(), stopping.Token));
    }

    [Fact]
    public async Task ThePassItselfBeingStoppedIsNotAFailure()
    {
        using var stopping = new CancellationTokenSource();
        var tmdb = Source("TheMovieDb", _ =>
        {
            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => Count([tmdb], ct: stopping.Token));
    }

    [Fact]
    public async Task OneProviderFailingWhileAnotherHasTheEpisodeStillCounts()
    {
        var tvdb = Source("TheTVDB", ep => ep == 2 ? throw new HttpRequestException("down") : false);
        var tmdb = Source("TheMovieDb", ep => ep <= 4);

        Assert.Equal([1, 2, 3, 4], await Count([tvdb, tmdb]));

        // But when the one that failed might have had episode 5, the end isn't known.
        tvdb = Source("TheTVDB", _ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Count([tvdb, tmdb]));
    }

    [Fact]
    public async Task TheEndIsConfirmedByAskingAgain()
    {
        var asked = new List<int>();
        var flaky = 0;

        // Episode 4 is reported missing once, as some providers do for a passing error.
        var tvdb = Source("TheTVDB", ep =>
        {
            asked.Add(ep);
            return ep < 4 || (ep == 4 && ++flaky > 1) || (ep is > 4 and <= 6);
        });

        Assert.Equal([1, 2, 3, 4, 5, 6], await Count([tvdb]));
        Assert.Equal([1, 2, 3, 4, 4, 5, 6, 7, 7], asked);
    }

    [Fact]
    public async Task AVerifiedEmptySeasonIsNothingFound()
    {
        var tmdb = Source("TheMovieDb", _ => false);

        var numbers = await Count([tmdb]);

        Assert.Empty(numbers!);
        Assert.Null(EpisodeListRules.ToCount(numbers!));
    }

    [Fact]
    public async Task NoProviderThatCanAnswerIsAFailure() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Count([]));

    [Fact]
    public async Task AScanStartingMidLoopPausesTheNextEpisodeRequest()
    {
        // Review round 7, finding 13: the per-episode loop kept asking while a library scan ran.
        var scanning = false;
        var scanChecksWhileRunning = 0;
        var askedDuringScan = new List<int>();
        var asked = new List<int>();

        Task<bool> Exists(int ep, CancellationToken ct)
        {
            if (scanning)
            {
                askedDuringScan.Add(ep);
            }

            asked.Add(ep);
            if (ep == 1)
            {
                scanning = true;
            }

            return Task.FromResult(ep <= 3);
        }

        var numbers = await EpisodeCounter.CountAsync(
            Exists,
            ct => ScanGate.WaitWhileAsync(
                () =>
                {
                    if (scanning && ++scanChecksWhileRunning == 3)
                    {
                        scanning = false;
                    }

                    return scanning;
                },
                TimeSpan.FromMilliseconds(1),
                ct),
            () => true,
            CancellationToken.None);

        Assert.Equal([1, 2, 3], numbers);
        Assert.Empty(askedDuringScan);
        Assert.Equal(3, scanChecksWhileRunning);
    }

    [Fact]
    public async Task AShowIdentifiedDifferentlyDuringAScanStopsTheCount()
    {
        var waits = 0;
        var asked = 0;

        var numbers = await EpisodeCounter.CountAsync(
            (_, _) => Task.FromResult(++asked > 0),
            _ => Task.FromResult(++waits == 3),
            () => false,
            CancellationToken.None);

        Assert.Null(numbers);
        Assert.Equal(2, asked);
    }

    private static EpisodeSource Source(string name, Func<int, bool> has) => new(name, (ep, _) => Task.FromResult(has(ep)));

    private static Task<IReadOnlyList<int>?> Count(IReadOnlyList<EpisodeSource> sources, CancellationToken ct = default) =>
        EpisodeCounter.CountAsync(
            (ep, token) => EpisodeCounter.AnyHasAsync(sources, ep, token),
            _ => Task.FromResult(false),
            () => true,
            ct);

    private static EpisodePassPlan Plan() => new(TimeSpan.FromHours(4), TimeSpan.FromMinutes(10));

    // Past the routine first pass, so only requests start one.
    private static EpisodePassPlan Started()
    {
        var plan = Plan();
        plan.Start(Now.AddHours(-1), TimeSpan.Zero);
        plan.Started(Now.AddHours(-1));
        return plan;
    }
}
