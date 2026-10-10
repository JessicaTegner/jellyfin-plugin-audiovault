using System.Net;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class UnreachedPlaysTests
{
    private const string Url = "http://jf.tail1234.ts.net:8096";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlayerInfo Tv = new("session-tv", "device-tv", "Living room TV", "Jellyfin Android TV", "sam");
    private static readonly PlayerInfo Phone = new("session-phone", "device-phone", "Phone", "Jellyfin Android", "jess");
    private static readonly (string, int?)[] Movie = [("1001", null)];

    private static DateTimeOffset Later => Now + UnreachedPlays.Window + TimeSpan.FromSeconds(1);

    private static UnreachedPlays Issued(PlayerInfo player, string playSessionId = "ps1")
    {
        var plays = new UnreachedPlays();
        plays.Issued(playSessionId, player, Url, Movie, Now);
        return plays;
    }

    /// <summary>A direct play the player confirmed by reporting its start, as Android TV does before loading.</summary>
    private static UnreachedPlays Confirmed(PlayerInfo player, string playSessionId = "ps1")
    {
        var plays = Issued(player, playSessionId);
        plays.Prepared(playSessionId, Now);
        plays.Started(playSessionId, directPlay: true, held: false, Now.AddSeconds(1));
        return plays;
    }

    [Fact]
    public void ConfirmedDirectPlayWithoutARequestIsReported()
    {
        var plays = Confirmed(Tv);
        Assert.Empty(plays.Reports(Url, Now.AddSeconds(30)));

        var report = Assert.Single(plays.Reports(Url, Later));
        Assert.Equal("Living room TV", report.DeviceName);
        Assert.Equal("Jellyfin Android TV", report.Client);
        Assert.Equal(1, report.Count);
        Assert.Equal(Now, report.LastAt);
    }

    [Fact]
    public void PreparationAloneIsNotReported()
    {
        // Review round 6, finding 12: playback info for a direct play, then the user cancels before the player
        // fetches anything. Nothing confirms the play, so nothing is reported.
        var plays = Issued(Tv);
        plays.Prepared("ps1", Now);
        Assert.Empty(plays.Reports(Url, Later));
        Assert.Empty(plays.Reports(Url, Now.AddHours(1)));
    }

    [Fact]
    public void AnAbandonedPlayIsCancelledByItsStop()
    {
        var plays = Issued(Tv);
        plays.Prepared("ps1", Now);
        plays.Stopped("ps1", failed: false, Now.AddSeconds(2));
        plays.Started("ps1", directPlay: true, held: false, Now.AddSeconds(3));
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void AFailedStopConfirmsThePlay()
    {
        var plays = Issued(Tv);
        plays.Prepared("ps1", Now);
        plays.Stopped("ps1", failed: true, Now.AddSeconds(2));
        Assert.Single(plays.Reports(Url, Later));
    }

    [Fact]
    public void AStopAfterAConfirmedStartDoesntHideTheFailure()
    {
        var plays = Confirmed(Tv);
        plays.Stopped("ps1", failed: false, Now.AddSeconds(20));
        Assert.Single(plays.Reports(Url, Later));
    }

    [Fact]
    public void FallingBackFromADirectPlayConfirmsIt()
    {
        // jellyfin-web couldn't load the link and asks again with direct play off; Jellyfin then transcodes the new
        // attempt, which cancels only that one.
        var plays = Issued(Phone);
        plays.Prepared("ps1", Now);
        plays.Issued("ps2", Phone, Url, Movie, Now.AddSeconds(3), directPlayDisabled: true);
        plays.Relayed("ps2");
        plays.Started("ps2", directPlay: false, held: false, Now.AddSeconds(5));

        var report = Assert.Single(plays.Reports(Url, Later));
        Assert.Equal("Phone", report.DeviceName);
    }

    [Fact]
    public void AnotherSessionsFallbackConfirmsNothing()
    {
        var plays = Issued(Tv);
        plays.Prepared("ps1", Now);
        plays.Issued("ps2", Phone, Url, Movie, Now.AddSeconds(3), directPlayDisabled: true);
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void ARequestFromThePlayerIsNotReported()
    {
        var plays = Confirmed(Tv);
        plays.Arrived("ps1", "session-tv", Url, Now.AddSeconds(2));
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void ARequestBeforeTheStartIsNotReported()
    {
        var plays = Issued(Tv);
        plays.Arrived("ps1", "session-tv", Url, Now);
        plays.Started("ps1", directPlay: true, held: false, Now.AddSeconds(3));
        Assert.Empty(plays.Reports(Url, Later.AddSeconds(3)));
    }

    [Fact]
    public void AnOldLinkArrivingDoesNotClearANewAttempt()
    {
        // Review round 7, finding 17: the address changed from A to B; an old ticketed request still reaches A while
        // the new attempt can't reach B.
        const string Old = "http://192.168.1.20:8096";
        var plays = new UnreachedPlays();
        plays.Issued("ps-old", Tv, Old, Movie, Now.AddMinutes(-5));
        plays.Issued("ps1", Tv, Url, Movie, Now);
        plays.Prepared("ps1", Now);
        plays.Started("ps1", directPlay: true, held: false, Now.AddSeconds(1));

        plays.Arrived("ps-old", "session-tv", Old, Now.AddSeconds(2));
        Assert.Single(plays.Reports(Url, Later));

        // Nor does it clear the device's report for the new address.
        plays.Arrived("ps-old", "session-tv", Old, Later.AddSeconds(1));
        Assert.Single(plays.Reports(Url, Later.AddSeconds(2)));
    }

    [Fact]
    public void ATicketWithoutAnAttemptCountsOnlyAtItsAddress()
    {
        var plays = Confirmed(Tv);
        plays.Arrived(null, "session-tv", "http://192.168.1.20:8096", Now.AddSeconds(2));
        Assert.Single(plays.Reports(Url, Later));

        plays = Confirmed(Tv);
        plays.Arrived(null, "session-tv", Url, Now.AddSeconds(2));
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void ProbesAndInformationRequestsExpectNothing()
    {
        // PlaybackInfo without a play: links handed out, nothing confirmed.
        var plays = Issued(Tv);
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void TranscodedAndHeldStartsAreNotReported()
    {
        var plays = Issued(Tv);
        plays.Started("ps1", directPlay: false, held: false, Now);
        Assert.Empty(plays.Reports(Url, Later));

        plays = Issued(Tv);
        plays.Started("ps1", directPlay: true, held: true, Now);
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void ATranscodeStartCancelsAnEarlierExpectation()
    {
        var plays = Issued(Tv);
        plays.Prepared("ps1", Now);
        plays.Started("ps1", directPlay: false, held: false, Now.AddSeconds(2));
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void JellyfinStreamingTheAttemptCancelsIt()
    {
        var plays = Confirmed(Tv);
        plays.Relayed("ps1");
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void AnotherPlayersServerFetchKeepsTheReport()
    {
        // Review round 6, finding 13: Alice's TV can't reach the address; Bob's play of the same movie is transcoded.
        // Jellyfin streaming Bob's attempt says nothing about Alice's.
        var plays = Confirmed(Tv, "alice");
        plays.Issued("bob", Phone, Url, Movie, Now.AddSeconds(5));
        plays.Prepared("bob", Now.AddSeconds(5));
        plays.Relayed("bob");
        plays.Relayed(null);

        var report = Assert.Single(plays.Reports(Url, Later));
        Assert.Equal("Living room TV", report.DeviceName);
    }

    [Fact]
    public void StartsWithoutPlaybackInfoAreIgnored()
    {
        // e.g. a client playing its own downloaded copy.
        var plays = new UnreachedPlays();
        plays.Started("offline", directPlay: true, held: false, Now);
        plays.Prepared("offline", Now);
        Assert.Empty(plays.Reports(Url, Later));
    }

    [Fact]
    public void RepeatedFailuresCountPerDeviceAndALaterRequestClearsThem()
    {
        var plays = Confirmed(Tv);
        plays.Issued("ps2", Tv, Url, Movie, Now.AddMinutes(5));
        plays.Started("ps2", directPlay: true, held: false, Now.AddMinutes(5));
        plays.Issued("ps3", Phone, Url, Movie, Now.AddMinutes(5));
        plays.Started("ps3", directPlay: true, held: false, Now.AddMinutes(5));
        plays.Arrived("ps3", "session-phone", Url, Now.AddMinutes(5));

        var report = Assert.Single(plays.Reports(Url, Now.AddMinutes(10)));
        Assert.Equal(2, report.Count);
        Assert.Equal(Now.AddMinutes(5), report.LastAt);

        plays.Issued("ps4", Tv, Url, Movie, Now.AddMinutes(20));
        plays.Arrived("ps4", "session-tv", Url, Now.AddMinutes(20));
        Assert.Empty(plays.Reports(Url, Now.AddMinutes(21)));
    }

    [Fact]
    public void ReportsBelongToTheirAddress()
    {
        var plays = Confirmed(Tv);
        Assert.Single(plays.Reports(Url, Later));
        Assert.Empty(plays.Reports("http://100.100.1.2:8096", Later));
    }

    [Fact]
    public void ReportsExpireAfterADay()
    {
        var plays = Confirmed(Tv);
        Assert.Single(plays.Reports(Url, Later));
        Assert.Empty(plays.Reports(Url, Now.AddDays(2)));
    }

    [Fact]
    public void OverdueAttemptsAreSweptWhenNewOnesArrive()
    {
        var plays = Confirmed(Tv);
        var added = plays.Issued("ps2", Phone, Url, Movie, Later);
        Assert.Single(added);
    }

    [Fact]
    public void AttemptsDontPileUp()
    {
        // Review round 6, finding 6: playback info asked for again and again, nothing ever started, settings page
        // closed. Old attempts go by time, and there is a hard limit per session and overall.
        var plays = new UnreachedPlays();
        for (var i = 0; i < 100; i++)
        {
            plays.Issued($"old{i}", Tv, Url, Movie, Now.AddSeconds(i));
            plays.Prepared($"old{i}", Now.AddSeconds(i));
        }

        Assert.Equal(UnreachedPlays.MaxPerSession, plays.Count);

        plays.Issued("new", Phone, Url, Movie, Now.AddDays(2));
        Assert.Equal(1, plays.Count);

        for (var i = 0; i < UnreachedPlays.MaxAttempts + 10; i++)
        {
            plays.Issued($"many{i}", Phone with { SessionId = $"s{i}" }, Url, Movie, Now.AddDays(2));
        }

        Assert.Equal(UnreachedPlays.MaxAttempts, plays.Count);
    }

    [Fact]
    public void ATimedSweepNeedsNoOtherActivity()
    {
        var plays = Confirmed(Tv);
        Assert.Single(plays.Sweep(Later));
        Assert.Empty(plays.Sweep(Later.AddSeconds(30)));
        plays.Sweep(Now.AddHours(1));
        Assert.Equal(0, plays.Count);
    }

    private const string Laptop = "admin/laptop";
    private const string AdminPhone = "admin2/phone";

    [Fact]
    public void ReachProbeOutcomes()
    {
        var probes = new ReachProbes();
        var seen = probes.Create(Url, Laptop, Now);
        probes.Seen(seen, Now.AddSeconds(1));
        Assert.Equal(BrowserOutcome.Reached, probes.Finish(seen, Laptop, loaded: true, mixed: false, Now.AddSeconds(1))!.Outcome);

        var other = probes.Create(Url, Laptop, Now);
        Assert.Equal(BrowserOutcome.OtherServer, probes.Finish(other, Laptop, loaded: true, mixed: false, Now.AddSeconds(1))!.Outcome);

        var none = probes.Create(Url, Laptop, Now);
        var failed = probes.Finish(none, Laptop, loaded: false, mixed: true, Now.AddSeconds(10))!;
        Assert.Equal(BrowserOutcome.NotReached, failed.Outcome);
        Assert.True(failed.Mixed);
        Assert.Same(failed, probes.LastFor(Laptop));
    }

    [Fact]
    public void EachBrowserSeesOnlyItsOwnResult()
    {
        // Review round 6, finding 14: one admin's phone fails while another admin's laptop succeeds, in either order.
        var probes = new ReachProbes();
        var laptop = probes.Create(Url, Laptop, Now);
        var phone = probes.Create(Url, AdminPhone, Now);
        probes.Seen(laptop, Now.AddSeconds(1));
        probes.Finish(laptop, Laptop, loaded: true, mixed: false, Now.AddSeconds(1));
        probes.Finish(phone, AdminPhone, loaded: false, mixed: false, Now.AddSeconds(10));

        Assert.Equal(BrowserOutcome.Reached, probes.LastFor(Laptop)!.Outcome);
        Assert.Equal(BrowserOutcome.NotReached, probes.LastFor(AdminPhone)!.Outcome);
        Assert.Null(probes.LastFor("someone/else"));
    }

    [Fact]
    public void AnotherBrowserCantFinishACheck()
    {
        var probes = new ReachProbes();
        var nonce = probes.Create(Url, Laptop, Now);
        Assert.Null(probes.Finish(nonce, AdminPhone, loaded: false, mixed: false, Now));
        Assert.NotNull(probes.Finish(nonce, Laptop, loaded: true, mixed: false, Now));
        Assert.Null(probes.LastFor(AdminPhone));
    }

    [Fact]
    public void OwnersAreUserAndDevice()
    {
        var user = Guid.NewGuid();
        Assert.NotEqual(ReachProbes.Owner(user, "laptop"), ReachProbes.Owner(user, "phone"));
        Assert.NotEqual(ReachProbes.Owner(user, "laptop"), ReachProbes.Owner(Guid.NewGuid(), "laptop"));
    }

    [Fact]
    public void ReachProbesIgnoreUnknownExpiredAndReusedNonces()
    {
        var probes = new ReachProbes();
        probes.Seen("made-up", Now);
        Assert.Null(probes.Finish("made-up", Laptop, true, false, Now));

        var nonce = probes.Create(Url, Laptop, Now);
        Assert.Matches("^[0-9a-f]{32}$", nonce);
        Assert.Null(probes.Finish(nonce, Laptop, true, false, Now.AddMinutes(5)));

        var once = probes.Create(Url, Laptop, Now);
        Assert.NotNull(probes.Finish(once, Laptop, true, false, Now));
        Assert.Null(probes.Finish(once, Laptop, true, false, Now));
    }

    [Fact]
    public void TailscaleAlternativeReplacesTheNameForPlainHttp()
    {
        IPAddress[] resolved = [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("100.64.0.7")];
        Assert.Equal("http://100.64.0.7:8096", PublicUrlRules.TailscaleAlternative(Url, resolved));
        Assert.Null(PublicUrlRules.TailscaleAlternative("https://jf.tail1234.ts.net", resolved));
        Assert.Null(PublicUrlRules.TailscaleAlternative("http://jf.example.com", resolved));
        Assert.Null(PublicUrlRules.TailscaleAlternative(Url, [IPAddress.Parse("192.168.1.5")]));
    }

    [Fact]
    public async Task TheSuggestionFollowsTheCurrentAddress()
    {
        // Review round 6, finding 15: the name's addresses are cached, not the suggestion, so a new port, scheme or
        // path shows at once.
        var cache = new HostAddresses(TimeSpan.FromMinutes(10));
        var lookups = 0;
        Task<IPAddress[]> Resolve(string host, CancellationToken ct)
        {
            lookups++;
            return Task.FromResult<IPAddress[]>([IPAddress.Parse("100.64.0.7")]);
        }

        async Task<string?> Suggest(string url) =>
            PublicUrlRules.TailscaleAlternative(url, await cache.ResolveAsync(new Uri(url).Host, Resolve, Now, CancellationToken.None));

        Assert.Null(await Suggest("https://jf.tail1234.ts.net"));
        Assert.Equal("http://100.64.0.7:8096", await Suggest("http://jf.tail1234.ts.net:8096"));
        Assert.Equal("http://100.64.0.7:18096", await Suggest("http://jf.tail1234.ts.net:18096"));
        Assert.Equal("http://100.64.0.7:8096/jellyfin", await Suggest("http://jf.tail1234.ts.net:8096/jellyfin"));
        Assert.Equal(1, lookups);

        await cache.ResolveAsync("jf.tail1234.ts.net", Resolve, Now.AddMinutes(11), CancellationToken.None);
        Assert.Equal(2, lookups);
    }
}
