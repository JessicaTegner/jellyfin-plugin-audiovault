using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class SetupRulesTests
{
    private const string Url = "https://jf.example.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly AddressFacts GoodAddress = new(Url, null, false, null, null, null);
    private static readonly LibraryFacts BothLibraries = new(true, true, null);
    private static readonly CatalogFacts ReadCatalog = new(26000, 14000, 12000, true, null, false, null, null, Now, null);
    private static readonly MatchingFacts AllMatched = new(12000, 12000, 12000, 98000, 98000, 98000, false, null, null);
    private static readonly EpisodeListStatus AllLists = new(10000, 10000, 0, 0, false, 0);
    private static readonly AccountFacts SignedIn = new(true, false, "me@example.com", 1, 1);

    private static SetupFacts Facts(
        AddressFacts? address = null,
        LibraryFacts? libraries = null,
        CatalogFacts? catalog = null,
        MatchingFacts? matching = null,
        EpisodeListStatus? lists = null,
        AccountFacts? account = null) =>
        new(address ?? GoodAddress, libraries ?? BothLibraries, catalog ?? ReadCatalog, matching ?? AllMatched, lists ?? AllLists, account ?? SignedIn);

    private static SetupStep Step(SetupFacts f, string key) => SetupRules.Build(f).Single(s => s.Key == key);

    [Fact]
    public void EverythingDoneIsComplete()
    {
        var steps = SetupRules.Build(Facts());
        Assert.Equal(["address", "libraries", "catalog", "matching", "episodes", "account"], steps.Select(s => s.Key));
        Assert.All(steps, s => Assert.Equal(SetupState.Done, s.State));
        Assert.True(SetupRules.Complete(steps));
    }

    [Fact]
    public void AfterARestartCountsAreInProgressNotWaitingOrDone()
    {
        var f = Facts(
            matching: new MatchingFacts(0, null, null, 0, null, null, false, null, null),
            lists: new EpisodeListStatus(0, 0, 0, null, false, null));

        Assert.Equal(SetupState.InProgress, Step(f, "matching").State);
        Assert.Equal(SetupState.InProgress, Step(f, "episodes").State);
        Assert.False(SetupRules.Complete(SetupRules.Build(f)));
    }

    [Fact]
    public void FreshInstallWaitsOnTheAdmin()
    {
        var steps = SetupRules.Build(Facts(
            libraries: new LibraryFacts(false, false, null),
            catalog: new CatalogFacts(0, 0, 0, false, null, false, null, null, null, null),
            matching: new MatchingFacts(0, null, null, 0, null, null, false, null, null),
            lists: new EpisodeListStatus(0, 0, 0, null, false),
            account: new AccountFacts(false, false, null, 0, 1)));
        Assert.False(SetupRules.Complete(steps));
        var libraries = steps.Single(s => s.Key == "libraries");
        Assert.Equal(SetupState.Waiting, libraries.State);
        Assert.Equal(SetupAction.LibraryOptions, libraries.Action);
        var catalog = steps.Single(s => s.Key == "catalog");
        Assert.Equal(SetupAction.SyncRecent, catalog.Action);
        Assert.Contains("about 10 minutes", catalog.Detail, StringComparison.Ordinal);
        Assert.Equal(SetupState.Waiting, steps.Single(s => s.Key == "matching").State);
        Assert.Equal(SetupAction.AccountPage, steps.Single(s => s.Key == "account").Action);
    }

    [Fact]
    public void AddressProblemsAndReportsAreProblems()
    {
        Assert.Equal(SetupState.Problem, Step(Facts(address: GoodAddress with { Problem = "Only local." }), "address").State);

        var report = new UnreachedReport("Living room TV", "Jellyfin Android TV", "sam", Url, 2, Now);
        var step = Step(Facts(address: GoodAddress with { Unreached = report }), "address");
        Assert.Equal(SetupState.Problem, step.State);
        Assert.Equal(SetupAction.PublicUrl, step.Action);
        Assert.Equal(Now, step.At);
        Assert.Contains("A player on Living room TV (Jellyfin Android TV, sam) couldn't load the playback address (last at {at}, 2 times).", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AdviceDoesNotBlockTheAddress()
    {
        var step = Step(Facts(address: GoodAddress with { Advice = "jf.ts.net is a Tailscale MagicDNS name." }), "address");
        Assert.Equal(SetupState.Done, step.State);
        Assert.Contains("Note: jf.ts.net", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RewritingIsInProgress() =>
        Assert.Equal(SetupState.InProgress, Step(Facts(address: GoodAddress with { Rewriting = true }), "address").State);

    [Fact]
    public void BrowserCheckCountsOnlyForTheAddressInUse()
    {
        var failed = new BrowserCheck(Url + "/", BrowserOutcome.NotReached, false, Now);
        Assert.Equal(SetupState.Problem, Step(Facts(address: GoodAddress with { Browser = failed }), "address").State);

        var old = failed with { Url = "http://old.example.com" };
        Assert.Equal(SetupState.Done, Step(Facts(address: GoodAddress with { Browser = old }), "address").State);

        var reached = failed with { Outcome = BrowserOutcome.Reached };
        Assert.Contains("and this browser reached it.", Step(Facts(address: GoodAddress with { Browser = reached }), "address").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserTextExplainsMixedContent()
    {
        var text = SetupRules.BrowserText(new BrowserCheck("http://jf:8096", BrowserOutcome.NotReached, true, Now));
        Assert.Contains("https", text, StringComparison.Ordinal);
        Assert.Contains("not from this Jellyfin server", SetupRules.BrowserText(new BrowserCheck(Url, BrowserOutcome.OtherServer, false, Now)), StringComparison.Ordinal);
    }

    [Fact]
    public void FolderProblemIsALibraryProblem() =>
        Assert.Equal(SetupState.Problem, Step(Facts(libraries: new LibraryFacts(true, true, "Bad folder.")), "libraries").State);

    [Fact]
    public void OneMissingLibraryIsNamed()
    {
        var step = Step(Facts(libraries: new LibraryFacts(true, false, null)), "libraries");
        Assert.Equal(SetupState.Waiting, step.State);
        Assert.Contains("the shows folder", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogReadShowsProgressAndEstimate()
    {
        var step = Step(Facts(catalog: new CatalogFacts(0, 0, 0, false, null, true, 42.7, TimeSpan.FromMinutes(6), null, null)), "catalog");
        Assert.Equal(SetupState.InProgress, step.State);
        Assert.Contains("42% done, about 6 minutes left", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogDoneSaysWhen()
    {
        var step = Step(Facts(), "catalog");
        Assert.Equal("26,000 titles (14,000 movies, 12,000 show seasons), last read {at}.", step.Detail);
        Assert.Equal(Now, step.At);
    }

    [Fact]
    public void CatalogProblemsOfferTheRightSync()
    {
        Assert.Equal(SetupAction.SyncFull, Step(Facts(catalog: ReadCatalog with { Problem = "Damaged." }), "catalog").Action);
        var failed = Step(Facts(catalog: new CatalogFacts(0, 0, 0, false, null, false, null, null, null, "Cloudflare.")), "catalog");
        Assert.Equal(SetupState.Problem, failed.State);
        Assert.Equal(SetupAction.SyncRecent, failed.Action);
    }

    [Fact]
    public void RecentSyncDuringNormalUseKeepsCatalogDone() =>
        Assert.Equal(SetupState.Done, Step(Facts(catalog: ReadCatalog with { Running = true, Progress = 10 }), "catalog").State);

    [Fact]
    public void MatchingInProgressExplainsHours()
    {
        var step = Step(Facts(matching: new MatchingFacts(12000, 8000, 5000, 98000, 20000, 10000, true, 38, TimeSpan.FromHours(3))), "matching");
        Assert.Equal(SetupState.InProgress, step.State);
        Assert.Equal(
            "8,000 of 12,000 movies and 20,000 of 98,000 episodes added; 5,000 movies and 10,000 episodes searchable. "
            + "Library scan 38%, about 3 hours left. Titles appear in search gradually; the first time takes hours.",
            step.Detail);
    }

    [Fact]
    public void MatchingBeforeCountingSaysSo()
    {
        var step = Step(Facts(matching: new MatchingFacts(12140, 12140, null, 98842, 98789, null, true, 48, TimeSpan.FromMinutes(6))), "matching");
        Assert.Equal(SetupState.InProgress, step.State);
        Assert.Equal(
            "12,140 movies and 98,789 of 98,842 episodes added; counting how many are searchable. "
            + "Library scan 48%, about 6 minutes left. Titles appear in search gradually; the first time takes hours.",
            step.Detail);
    }

    [Fact]
    public void MatchingWithoutAScanMentionsTheRepairAndWaitingShows()
    {
        var step = Step(Facts(matching: new MatchingFacts(100, 100, 90, 1000, 1000, 800, false, null, null, 12)), "matching");
        Assert.Equal(
            "100 movies and 1,000 episodes added; 90 movies and 800 episodes searchable, and 12 shows or seasons wait for their details. "
            + "Titles appear in search gradually; the plugin asks Jellyfin to finish any an interrupted scan left waiting.",
            step.Detail);
    }

    [Fact]
    public void AFewNewTitlesDontUndoMatching()
    {
        var step = Step(Facts(matching: new MatchingFacts(12000, 12000, 12000, 98842, 98786, 98786, true, 5, null)), "matching");
        Assert.Equal(SetupState.Done, step.State);
        Assert.Equal("12,000 movies and 98,786 of 98,842 episodes searchable; the rest are new and wait for the next library scan.", step.Detail);
        Assert.Equal("All 12,000 movies and 98,000 episodes are searchable.", Step(Facts(), "matching").Detail);
    }

    [Fact]
    public void MatchingWaitsForLibrariesAndCatalog()
    {
        Assert.Equal(SetupState.Waiting, Step(Facts(libraries: new LibraryFacts(true, false, null)), "matching").State);
        Assert.Equal(SetupState.Waiting, Step(Facts(
            catalog: new CatalogFacts(0, 0, 0, false, null, false, null, null, null, null),
            matching: new MatchingFacts(0, 0, 0, 0, 0, 0, false, null, null)), "matching").State);
    }

    [Fact]
    public void EpisodeListsBeforeFirstPassAreInProgress()
    {
        var step = Step(Facts(lists: new EpisodeListStatus(10387, 9370, 1017, null, false)), "episodes");
        Assert.Equal(SetupState.InProgress, step.State);
        Assert.Equal(
            "9,370 of 10,387 seasons have their episode list; 1,017 are looked up automatically after library scans. Until then a season shows only episode 1.",
            step.Detail);
    }

    [Fact]
    public void EpisodeListsRunningAreInProgress() =>
        Assert.Equal(SetupState.InProgress, Step(Facts(lists: new EpisodeListStatus(100, 50, 50, 0, true, 0)), "episodes").State);

    [Fact]
    public void UntriedSeasonsAreInProgress()
    {
        var step = Step(Facts(lists: new EpisodeListStatus(100, 50, 50, 10, false, 20)), "episodes");
        Assert.Equal(SetupState.InProgress, step.State);
        Assert.Equal(
            "50 of 100 seasons have their episode list; 20 are looked up automatically after library scans and 10 once Jellyfin identifies their show. "
            + "Until then a season shows only episode 1.",
            step.Detail);
    }

    [Fact]
    public void SeasonsWaitingForTheirShowWaitWhileMatchingRuns()
    {
        var matching = new MatchingFacts(12000, 6000, 6000, 98000, 50000, 50000, true, 50, null);
        Assert.Equal(SetupState.Waiting, Step(Facts(matching: matching, lists: new EpisodeListStatus(100, 50, 50, 30, false, 20)), "episodes").State);
    }

    [Fact]
    public void SeasonsTMDbHasNothingForCountAsDone()
    {
        var step = Step(Facts(lists: new EpisodeListStatus(10387, 9370, 1017, 17, false, 1000)), "episodes");
        Assert.Equal(SetupState.Done, step.State);
        Assert.Contains("1,017 have no list on TMDb yet", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FailingLookupsAreAProblemWithTheirRealRetry()
    {
        // Review round 6, finding 10: every lookup fails (TMDb outage). That isn't "TMDb has no list", and setup
        // isn't complete.
        var step = Step(Facts(lists: new EpisodeListStatus(10387, 0, 10387, 0, false, 0, Failed: 10387)), "episodes");
        Assert.Equal(SetupState.Problem, step.State);
        Assert.Equal(
            "0 of 10,387 seasons have their episode list; looking up 10,387 seasons failed, for example because TMDb couldn't be reached. "
            + "They are tried again every hour; the Jellyfin log has details. Until then a season shows only episode 1.",
            step.Detail);
        Assert.False(SetupRules.Complete(SetupRules.Build(Facts(lists: new EpisodeListStatus(10387, 0, 10387, 0, false, 0, Failed: 10387)))));
    }

    [Fact]
    public void ASingleFailureAmongEmptyAnswersIsStillAProblem()
    {
        var step = Step(Facts(lists: new EpisodeListStatus(100, 50, 50, 0, false, 49, Failed: 1)), "episodes");
        Assert.Equal(SetupState.Problem, step.State);
        Assert.Contains("looking up 1 season failed", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAnswersMentionTheirOwnRetry()
    {
        var step = Step(Facts(lists: new EpisodeListStatus(100, 50, 50, 0, false, 50)), "episodes");
        Assert.Equal(SetupState.Done, step.State);
        Assert.EndsWith("checked again every 12 hours.", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EpisodeOneIsMentionedOncePerStep()
    {
        EpisodeListStatus[] lists =
        [
            new(0, 0, 0, null, false), new(10387, 9370, 1017, null, false), new(100, 50, 50, 0, true, 0),
            new(100, 50, 50, 10, false, 20), new(100, 50, 50, 30, false, 20), new(10387, 9370, 1017, 17, false, 1000),
            new(100, 50, 50, 0, false, 0, Failed: 50),
        ];
        var matching = new MatchingFacts(12000, 6000, 6000, 98000, 50000, 50000, true, 50, null);
        foreach (var l in lists)
        {
            var detail = Step(Facts(matching: matching, lists: l), "episodes").Detail;
            Assert.True(detail.Split("episode 1").Length <= 2, detail);
        }

        var noShows = Step(Facts(libraries: new LibraryFacts(true, false, null)), "episodes").Detail;
        Assert.Equal("Starts once the shows library exists. Until then a season shows only episode 1.", noShows);
    }

    [Fact]
    public void AccountStates()
    {
        Assert.Equal(SetupState.Problem, Step(Facts(account: new AccountFacts(false, true, null, 1, 3)), "account").State);
        var waiting = Step(Facts(account: new AccountFacts(false, false, null, 1, 3)), "account");
        Assert.Equal(SetupState.Waiting, waiting.State);
        Assert.Contains("1 of 3 Jellyfin users have one.", waiting.Detail, StringComparison.Ordinal);
        Assert.Equal("Signed in as me@example.com.", Step(Facts(), "account").Detail);
    }

    [Theory]
    [InlineData(20, "less than a minute")]
    [InlineData(60, "about 1 minute")]
    [InlineData(330, "about 6 minutes")]
    [InlineData(1420, "about 25 minutes")]
    [InlineData(5400, "about 2 hours")]
    [InlineData(4 * 3600 + 1000, "about 4 hours")]
    public void AboutRoundsSensibly(int seconds, string expected) =>
        Assert.Equal(expected, SetupRules.About(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ProgressRateUsesTheObservedRate()
    {
        var rate = new ProgressRate(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
        rate.Observe("scan", 10, Now);
        Assert.Null(rate.Remaining("scan", 100, Now));
        rate.Observe("scan", 12, Now.AddSeconds(30));
        Assert.Null(rate.Remaining("scan", 100, Now.AddSeconds(30)));
        rate.Observe("scan", 14, Now.AddMinutes(2));
        // 4 points in 2 minutes: 86 more take 43 minutes.
        Assert.Equal(TimeSpan.FromMinutes(43), rate.Remaining("scan", 100, Now.AddMinutes(2)));
    }

    [Fact]
    public void ProgressRateRestartsOnANewRunAndForgetsStaleSamples()
    {
        var rate = new ProgressRate(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
        rate.Observe("scan", 80, Now);
        rate.Observe("scan", 90, Now.AddMinutes(5));
        rate.Observe("scan", 1, Now.AddMinutes(6));
        Assert.Null(rate.Remaining("scan", 100, Now.AddMinutes(6)));

        rate.Observe("scan", 3, Now.AddMinutes(8));
        Assert.NotNull(rate.Remaining("scan", 100, Now.AddMinutes(8)));
        Assert.Null(rate.Remaining("scan", 100, Now.AddMinutes(30)));
    }

    [Fact]
    public void ProgressRateWithoutProgressHasNoEstimate()
    {
        var rate = new ProgressRate(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
        rate.Observe("sync", 50, Now);
        rate.Observe("sync", 50, Now.AddMinutes(5));
        Assert.Null(rate.Remaining("sync", 100, Now.AddMinutes(5)));
    }

    [Fact]
    public void ProgressRateStalledAfterAFastStartHasNoEstimate()
    {
        var rate = new ProgressRate(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
        rate.Observe("scan", 0, Now);
        rate.Observe("scan", 48.8, Now.AddMinutes(2));
        Assert.NotNull(rate.Remaining("scan", 100, Now.AddMinutes(2)));

        rate.Observe("scan", 48.8, Now.AddMinutes(5));
        Assert.Null(rate.Remaining("scan", 100, Now.AddMinutes(5)));
    }
}
