using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class CompatibilityRulesTests
{
    private static readonly InternalsProbe AllBind = new(true, true, true, true, true, true, true);

    private static CompatibilityFacts Facts(
        InternalsProbe? probe = null,
        bool? filterReached = true,
        MenuLinkCheck menu = MenuLinkCheck.Ok,
        bool? scanTask = true) =>
        new("12.2.0", probe ?? AllBind, filterReached, menu, scanTask);

    [Fact]
    public void EverythingBindingIsDoneAndNamesTheVersion()
    {
        Assert.Empty(CompatibilityRules.Issues(Facts()));
        var step = CompatibilityRules.Step(Facts());
        Assert.Equal(SetupState.Done, step.State);
        Assert.Equal("compatibility", step.Key);
        Assert.Contains("Jellyfin 12.2.0", step.Detail);
    }

    [Fact]
    public void UnknownReachIsNotAProblem()
    {
        Assert.Empty(CompatibilityRules.Issues(Facts(filterReached: null, scanTask: null)));
    }

    [Fact]
    public void MissingSeasonLookupFallsBackToPerEpisode()
    {
        var issue = Assert.Single(CompatibilityRules.Issues(Facts(AllBind with { TmdbSeasons = false })));
        Assert.Equal("tmdb-seasons", issue.Key);
        Assert.False(issue.Severe);
        Assert.Equal("Fast episode lists aren't available on this Jellyfin version (12.2.0); using the slower per-episode lookup.", issue.Message);
    }

    [Fact]
    public void NoTmdbClientIsOneMessageNotThree()
    {
        var issue = Assert.Single(CompatibilityRules.Issues(Facts(AllBind with { TmdbClient = false, TmdbSeasons = false, TmdbImageLanguages = false })));
        Assert.Equal("tmdb", issue.Key);
        Assert.Contains("per-episode lookup", issue.Message);
        Assert.Contains("no length", issue.Message);
    }

    [Fact]
    public void ImageLanguagesSayLookupsStillWork()
    {
        var issue = Assert.Single(CompatibilityRules.Issues(Facts(AllBind with { TmdbImageLanguages = false })));
        Assert.Contains("still work", issue.Message);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NoPlaybackHookIsSevereAndBlocksSetup(bool actionBinds, bool filterReached)
    {
        var facts = Facts(AllBind with { PlaybackInfoAction = actionBinds }, filterReached);
        var issue = Assert.Single(CompatibilityRules.Issues(facts));
        Assert.True(issue.Severe);
        Assert.Contains("only titles already in the cache play", issue.Message);
        var step = CompatibilityRules.Step(facts);
        Assert.Equal(SetupState.Problem, step.State);
        Assert.False(SetupRules.Complete([step]));
    }

    [Fact]
    public void MissingPlayStartSignalIsALimitNotAProblem()
    {
        var facts = Facts(AllBind with { PlayStartSignal = false });
        var issue = Assert.Single(CompatibilityRules.Issues(facts));
        Assert.Equal("play-start", issue.Key);
        Assert.False(issue.Severe);
        Assert.Contains("still download", issue.Message);
        var step = CompatibilityRules.Step(facts);
        Assert.Equal(SetupState.Done, step.State);
        Assert.StartsWith("Jellyfin 12.2.0 works, with limits.", step.Detail);
    }

    [Fact]
    public void PlayStartIsNotReportedWhenThePlaybackHookIsMissingAnyway()
    {
        var issues = CompatibilityRules.Issues(Facts(AllBind with { PlaybackInfoAction = false, PlayStartSignal = false }));
        Assert.Equal(["playback"], issues.Select(i => i.Key));
    }

    [Fact]
    public void SessionMessagesSayTheSettingsPageStillTells()
    {
        var issue = Assert.Single(CompatibilityRules.Issues(Facts(AllBind with { SessionMessages = false })));
        Assert.Equal("messages", issue.Key);
        Assert.Contains("settings page and the server log still say what happened", issue.Message);
    }

    [Fact]
    public void MissingScanTaskIsReported()
    {
        var issue = Assert.Single(CompatibilityRules.Issues(Facts(scanTask: false)));
        Assert.Equal("scan-task", issue.Key);
    }

    [Theory]
    [InlineData(MenuLinkCheck.NotReached)]
    [InlineData(MenuLinkCheck.NoWebClient)]
    [InlineData(MenuLinkCheck.UnexpectedFormat)]
    public void MenuLinkProblemsPointToTheAccountPage(MenuLinkCheck menu)
    {
        var issue = Assert.Single(CompatibilityRules.Issues(Facts(menu: menu)));
        Assert.Equal("menu-link", issue.Key);
        Assert.EndsWith("Users can still open the account page at /AudioVault/Account on this server.", issue.Message);
    }

    [Fact]
    public void MenuLinkTurnedOffIsNeverAProblem()
    {
        Assert.Empty(CompatibilityRules.Issues(Facts(AllBind with { WebPath = false }, menu: MenuLinkCheck.Off)));
    }

    [Theory]
    [InlineData(false, null, false, false, MenuLinkCheck.Off)]
    [InlineData(true, false, true, true, MenuLinkCheck.NotReached)]
    [InlineData(true, null, false, false, MenuLinkCheck.NoWebClient)]
    [InlineData(true, true, true, false, MenuLinkCheck.UnexpectedFormat)]
    [InlineData(true, true, true, true, MenuLinkCheck.Ok)]
    [InlineData(true, null, true, true, MenuLinkCheck.Ok)]
    public void MenuLinkCheckFromWhatTheServerFound(bool enabled, bool? reached, bool found, bool usable, MenuLinkCheck expected)
    {
        Assert.Equal(expected, CompatibilityRules.MenuLink(enabled, reached, found, usable));
    }

    [Fact]
    public void SetupChecklistGetsTheCompatibilityStepLast()
    {
        var facts = new SetupFacts(
            new AddressFacts("https://jf.example.com", null, false, null, null, null),
            new LibraryFacts(true, true, null),
            new CatalogFacts(10, 5, 5, true, null, false, null, null, null, null),
            new MatchingFacts(5, 5, 5, 5, 5, 5, false, null, null),
            new EpisodeListStatus(5, 5, 0, 0, false, 0),
            new AccountFacts(true, false, "me@example.com", 1, 1),
            Facts(AllBind with { TmdbSeasons = false }));
        var steps = SetupRules.Build(facts);
        Assert.Equal("compatibility", steps[^1].Key);
        Assert.True(SetupRules.Complete(steps));
        Assert.Equal(6, SetupRules.Build(facts with { Compatibility = null }).Count);
    }
}
