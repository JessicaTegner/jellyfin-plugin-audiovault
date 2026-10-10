using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

/// <summary>
/// A queue that moves on by itself never starts a download; a user's own play still does.
/// The incident: S17E01 played a placeholder for 5 s, then jellyfin-web went on to S18E01, a new season download.
/// </summary>
public class AutoplayGuardTests
{
    private const string Session = "session-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T14:15:59Z");
    private static readonly TimeSpan Episode = TimeSpan.FromMinutes(45);
    private static readonly Guid ItemA = Guid.NewGuid();
    private static readonly Guid ItemB = Guid.NewGuid();
    private static readonly Guid ItemC = Guid.NewGuid();
    private static readonly Guid[] Queue = [ItemA, ItemB, ItemC];

    /// <summary>
    /// jellyfin-web 12.2's stop when its queue moves on: the body is its PlayState plus the queue; NextMediaType isn't
    /// sent. The same attempt is then stopped once more on the way to the next item.
    /// </summary>
    private static void PlaceholderEnds(AutoplayGuard guard, string from, DateTimeOffset at, IReadOnlyCollection<Guid>? queue = null)
    {
        var stop = new StopReport(from, ("24545", 1), TimeSpan.FromSeconds(2), Episode, Queue: queue ?? Queue);
        guard.StopReported(Session, stop, at);
        guard.StopReported(Session, stop, at.AddMilliseconds(5));
    }

    private static AutoplayGuard PlayedPlaceholder(string attempt = "P1")
    {
        var guard = new AutoplayGuard();
        guard.Issued(Session, attempt, Now.AddSeconds(-7));
        guard.Served(Session, attempt, "24545", 1, ServedKind.Placeholder, Now.AddSeconds(-6));
        guard.Started(Session, attempt, ItemA, Now.AddSeconds(-5));
        return guard;
    }

    /// <summary>A cached movie played from <paramref name="startedAgo"/> and stopped at <paramref name="position"/>.</summary>
    private static AutoplayGuard Played(TimeSpan startedAgo, TimeSpan position, TimeSpan runtime, bool failed = false, string? next = null)
    {
        var guard = new AutoplayGuard();
        guard.Served(Session, "P1", "1", null, ServedKind.Ready, Now - startedAgo);
        guard.Started(Session, "P1", ItemA, Now - startedAgo);
        guard.StopReported(Session, new StopReport("P1", ("1", null), position, runtime, failed, next, Queue, Cached: true), Now);
        return guard;
    }

    [Fact]
    public void AStopWithoutNextMediaTypeStillHoldsTheQueue()
    {
        // Review round 7, finding 1: jellyfin-web never sends NextMediaType, so the client's name proves nothing.
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);

        Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        Assert.True(guard.IsHeld("P2"));

        // The same attempt's stream request gets the same answer, even much later.
        Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddMinutes(1)));
    }

    [Fact]
    public void CachedOrAlreadyDownloadingItemsStillPlay()
    {
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);

        Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: false, Now.AddSeconds(1)));
        Assert.False(guard.IsHeld("P2"));
    }

    [Fact]
    public void TheSameTitleAgainIsHeldToo()
    {
        // e.g. RepeatOne, or the title queued twice: appearing again proves nothing.
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);

        Assert.True(guard.Hold(Session, "P2", ItemA, startsDownload: true, Now.AddSeconds(1)));
    }

    [Fact]
    public void APressAfterTheWindowDownloads()
    {
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);

        Assert.False(guard.Hold(Session, "P2", ItemA, startsDownload: true, Now + AutoplayGuard.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AnItemOutsideTheStoppedQueueIsTheUsersOwnPick()
    {
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);

        Assert.False(guard.Hold(Session, "P2", Guid.NewGuid(), startsDownload: true, Now.AddSeconds(1)));
    }

    [Fact]
    public void WithoutAQueueEveryItemCouldBeNext()
    {
        var guard = PlayedPlaceholder();
        guard.StopReported(Session, new StopReport("P1", ("24545", 1), TimeSpan.FromSeconds(2), Episode), Now);

        Assert.True(guard.Hold(Session, "P2", Guid.NewGuid(), startsDownload: true, Now.AddSeconds(1)));
        Assert.True(guard.Hold(Session, "P3", null, startsDownload: true, Now.AddSeconds(1)));
    }

    [Fact]
    public void ADeliberateSwitchPartWayThroughRealMediaDownloads()
    {
        // jellyfin-web stops the current play (part-way) just before asking for the one the user picked.
        var guard = Played(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20), Episode);

        Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddMilliseconds(200)));
    }

    [Fact]
    public void TheNaturalEndOfAnUnwatchedItemHolds()
    {
        // Watched to the end, but only after skipping there.
        var guard = Played(TimeSpan.FromMinutes(2), Episode, Episode);

        Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Video")]
    public void AFailedStopOrANamedNextItemHolds(bool failed, string? next)
    {
        var guard = Played(TimeSpan.FromMinutes(20), failed || next is not null ? TimeSpan.FromMinutes(20) : Episode, Episode, failed, next);

        Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
    }

    [Fact]
    public void AStopWithoutAPositionOrLengthHolds()
    {
        var guard = new AutoplayGuard();
        guard.Started(Session, "P1", ItemA, Now.AddMinutes(-20));
        guard.StopReported(Session, new StopReport("P1", null, null, Episode), Now);
        Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));

        guard.Started(Session, "P3", ItemA, Now.AddMinutes(1));
        guard.StopReported(Session, new StopReport("P3", null, TimeSpan.FromMinutes(5), null), Now.AddMinutes(2));
        Assert.True(guard.Hold(Session, "P4", ItemB, startsDownload: true, Now.AddMinutes(2).AddSeconds(1)));
    }

    [Fact]
    public void ALaterDeliberateStopLiftsTheHold()
    {
        // A placeholder ended, the cached next item played, and the user picks something else part-way through it.
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);
        Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: false, Now.AddSeconds(1)));
        guard.Started(Session, "P2", ItemB, Now.AddSeconds(1));
        guard.StopReported(Session, new StopReport("P2", ("2", null), TimeSpan.FromSeconds(5), Episode, Cached: true), Now.AddSeconds(7));

        Assert.False(guard.Hold(Session, "P3", ItemC, startsDownload: true, Now.AddSeconds(8)));
    }

    [Fact]
    public void ARepeatedQueueKeepsBeingHeldAndAPressAfterwardsDownloads()
    {
        // [A, B, B] without a websocket: the player can't be told to stop, so each held placeholder ends and the queue
        // moves on again. Neither B downloads; pressing play on B afterwards does.
        var guard = PlayedPlaceholder("PA");
        PlaceholderEnds(guard, "PA", Now);

        Assert.True(guard.Hold(Session, "PB1", ItemB, startsDownload: true, Now.AddSeconds(1)));
        guard.Served(Session, "PB1", "24546", 1, ServedKind.Placeholder, Now.AddSeconds(2));
        guard.Started(Session, "PB1", ItemB, Now.AddSeconds(2));
        Assert.True(guard.IsCurrent(Session, "PB1"));
        PlaceholderEnds(guard, "PB1", Now.AddSeconds(4));

        Assert.True(guard.Hold(Session, "PB2", ItemB, startsDownload: true, Now.AddSeconds(5)));
        guard.Served(Session, "PB2", "24546", 1, ServedKind.Placeholder, Now.AddSeconds(6));
        guard.Started(Session, "PB2", ItemB, Now.AddSeconds(6));
        PlaceholderEnds(guard, "PB2", Now.AddSeconds(8));

        Assert.False(guard.Hold(Session, "PB3", ItemB, startsDownload: true, Now.AddSeconds(8) + AutoplayGuard.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void OnlyInThatSession()
    {
        var guard = PlayedPlaceholder();
        PlaceholderEnds(guard, "P1", Now);

        Assert.False(guard.Hold("session-2", "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        Assert.False(guard.Hold(null, "P3", ItemB, startsDownload: true, Now.AddSeconds(1)));
    }

    public class Watched
    {
        [Fact]
        public void TheQueueMovingOnAfterAWatchedItemMayDownloadOnce()
        {
            var guard = Played(TimeSpan.FromHours(2), TimeSpan.FromMinutes(118), TimeSpan.FromHours(2));

            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(2)));
            Assert.True(guard.Hold(Session, "P3", ItemC, startsDownload: true, Now.AddSeconds(3)));
        }

        [Theory]
        [InlineData(ServedKind.Ready, 30, true)]
        [InlineData(ServedKind.Live, 90, true)]
        [InlineData(ServedKind.Placeholder, 120, true)]
        [InlineData(ServedKind.Ready, 108, false)]
        [InlineData(ServedKind.Live, 116, false)]
        public void OnlyAWatchedRealPlayLetsTheQueueDownload(ServedKind kind, int minutes, bool held)
        {
            var guard = new AutoplayGuard();
            guard.Served(Session, "P1", "1", null, kind, Now.AddHours(-2));
            guard.Started(Session, "P1", ItemA, Now.AddHours(-2));
            guard.StopReported(Session, new StopReport("P1", ("1", null), TimeSpan.FromMinutes(minutes), TimeSpan.FromHours(2), Cached: kind == ServedKind.Ready), Now);

            // A stop part-way doesn't move the queue on; the item it would go to is held only after an end.
            var ended = minutes >= 108 || kind == ServedKind.Placeholder;
            Assert.Equal(held && ended, guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(2)));
        }

        [Fact]
        public void AWatchedPlayOfUnknownUncachedMediaDoesNotCount()
        {
            var guard = new AutoplayGuard();
            guard.Started(Session, "P1", ItemA, Now.AddHours(-2));
            guard.StopReported(Session, new StopReport("P1", ("1", null), TimeSpan.FromHours(2), TimeSpan.FromHours(2)), Now);

            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(2)));
        }

        [Fact]
        public void AShortItemNeverCounts()
        {
            // Review round 7, finding 9: a four-minute item that stopped after a second (or even at its end).
            var guard = Played(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(4), failed: true);
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));

            guard = Played(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4));
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        }

        [Fact]
        public void TheTailAllowanceShrinksWithTheItem()
        {
            // 22 minutes: stopping 5 minutes before the end isn't near the end.
            var length = TimeSpan.FromMinutes(22);
            Assert.True(Played(length, TimeSpan.FromMinutes(17), length, next: "Video").Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
            Assert.False(Played(length, TimeSpan.FromMinutes(21.9), length).Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        }

        [Fact]
        public void SkippingToTheEndIsNotListening()
        {
            Assert.True(Played(TimeSpan.FromMinutes(10), Episode, Episode).Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));

            // At 1.5x speed the time since the start still covers most of it.
            Assert.False(Played(TimeSpan.FromMinutes(30), Episode, Episode).Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        }

        [Fact]
        public void ARepeatedStopDoesNotGrantAnotherDownload()
        {
            // Review round 7, finding 4.
            var guard = Played(Episode, Episode, Episode);
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));

            guard.StopReported(Session, new StopReport("P1", ("1", null), Episode, Episode, Queue: Queue, Cached: true), Now.AddSeconds(2));
            Assert.True(guard.Hold(Session, "P3", ItemC, startsDownload: true, Now.AddSeconds(3)));
        }

        [Fact]
        public void AnEarlyProbeReservesTheAllowanceForItsItem()
        {
            // Review round 7, finding 4: two overlapping PlaybackInfo requests can't both authorise their probes.
            var guard = Played(Episode, Episode, Episode);

            Assert.True(guard.ReserveEarly(Session, ItemB, Now.AddSeconds(1)));
            Assert.False(guard.ReserveEarly(Session, ItemB, Now.AddSeconds(1)));
            Assert.False(guard.ReserveEarly(Session, ItemC, Now.AddSeconds(1)));

            // The response's attempt for that item gets it; another attempt doesn't.
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(2)));
            Assert.True(guard.Hold(Session, "P3", ItemB, startsDownload: true, Now.AddSeconds(2)));
        }

        [Fact]
        public void TheSuccessorKeepsItsDownloadForLater()
        {
            // The next item was downloading for someone else; that failed, and its stream request now needs a download.
            var guard = Played(Episode, Episode, Episode);
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: false, Now.AddSeconds(1)));
            Assert.True(guard.Hold(Session, "P3", ItemC, startsDownload: true, Now.AddSeconds(2)));

            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddMinutes(1)));
        }
    }

    /// <summary>Review round 7, findings 2, 3, 5 and 8: a restriction can't lapse before the attempt is decided.</summary>
    public class Binding
    {
        [Fact]
        public void PermissionToPlayWhatIsThereIsNotPermissionToDownload()
        {
            // Finding 5: allowed because another user was downloading it; that failed before the stream request.
            var guard = PlayedPlaceholder();
            PlaceholderEnds(guard, "P1", Now);
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: false, Now.AddSeconds(1)));

            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddMinutes(1)));
            Assert.True(guard.IsHeld("P2"));
        }

        [Fact]
        public void AnUnrestrictedAttemptMayDownloadLater()
        {
            var guard = new AutoplayGuard();
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: false, Now));
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddMinutes(1)));
        }

        [Fact]
        public void AStartBeforeTheStreamRequestBindsTheHold()
        {
            // Finding 3: P2 got its ticket from an information request, reported its start, then fetched the stream.
            var guard = PlayedPlaceholder();
            PlaceholderEnds(guard, "P1", Now);
            guard.Started(Session, "P2", ItemB, Now.AddSeconds(1));
            Assert.True(guard.IsDecided("P2"));

            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now + AutoplayGuard.Window + TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task AStopThatNeverComesHolds()
        {
            // Finding 2: P1's stop is delayed past the wait.
            var guard = new AutoplayGuard();
            guard.Started(Session, "P1", ItemA, Now);

            Assert.False(await guard.WaitForStopAsync(Session, "P2", TimeSpan.FromMilliseconds(50), Now.AddSeconds(5), CancellationToken.None));
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(5)));

            // The stop arriving afterwards doesn't release P2, and isn't waited for again.
            guard.StopReported(Session, new StopReport("P1", null, TimeSpan.FromMinutes(1), Episode), Now.AddSeconds(6));
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(30)));
            Assert.True(await guard.WaitForStopAsync(Session, "P3", TimeSpan.FromSeconds(10), Now.AddSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));

            // Pressing play again once the window has passed downloads.
            Assert.False(guard.Hold(Session, "P3", ItemB, startsDownload: true, Now.AddSeconds(30)));
        }

        [Fact]
        public void AStartReplacingAPlayWithoutAStopHolds()
        {
            var guard = new AutoplayGuard();
            guard.Started(Session, "P1", ItemA, Now);
            guard.Started(Session, "P2", ItemB, Now.AddMinutes(45));

            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddMinutes(46)));
        }

        [Fact]
        public async Task AnAttemptDoesNotWaitForItself()
        {
            var guard = new AutoplayGuard();
            guard.Started(Session, "P2", ItemB, Now);

            Assert.True(await guard.WaitForStopAsync(Session, "P2", TimeSpan.FromSeconds(10), Now, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task APlayRequestBeforeTheStopWaitsForIt()
        {
            var guard = PlayedPlaceholder();
            var waiting = guard.WaitForStopAsync(Session, "P2", TimeSpan.FromSeconds(10), Now, CancellationToken.None);
            Assert.False(waiting.IsCompleted);

            PlaceholderEnds(guard, "P1", Now);

            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        }

        [Fact]
        public async Task NothingPlayingMeansNoWait()
        {
            var guard = new AutoplayGuard();
            Assert.True(await guard.WaitForStopAsync(Session, null, TimeSpan.FromSeconds(10), Now, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void ALateRequestOfAStoppedAttemptDoesntMakeItPlayAgain()
        {
            var guard = PlayedPlaceholder();
            guard.StopReported(Session, new StopReport("P1", null, TimeSpan.FromSeconds(2), Episode), Now);
            guard.Served(Session, "P1", "24545", 1, ServedKind.Placeholder, Now.AddSeconds(1));

            Assert.False(guard.IsCurrent(Session, "P1"));
        }

        [Fact]
        public void ADelayedStartOfAHeldAttemptDoesNotReplaceANewerPlay()
        {
            // Finding 8: held P2's start arrives after the user's P3 started.
            var guard = PlayedPlaceholder();
            PlaceholderEnds(guard, "P1", Now);
            guard.Issued(Session, "P2", Now.AddSeconds(1));
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));

            guard.Issued(Session, "P3", Now.AddSeconds(20));
            guard.Started(Session, "P3", Guid.NewGuid(), Now.AddSeconds(21));
            guard.Started(Session, "P2", ItemB, Now.AddSeconds(22));

            Assert.False(guard.IsCurrent(Session, "P2"));
            Assert.True(guard.IsCurrent(Session, "P3"));
        }

        [Fact]
        public void AnAttemptIsNotCurrentOnceANewerOneIsIssued()
        {
            var guard = PlayedPlaceholder();
            PlaceholderEnds(guard, "P1", Now);
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
            guard.Started(Session, "P2", ItemB, Now.AddSeconds(2));
            Assert.True(guard.IsCurrent(Session, "P2"));

            guard.Issued(Session, "P3", Now.AddSeconds(3));
            Assert.False(guard.IsCurrent(Session, "P2"));

            // Jellyfin's own event for P2, arriving late, leaves P3 alone.
            guard.Started(Session, "P3", ItemC, Now.AddSeconds(4));
            guard.StopObserved(Session, "P2", Now.AddSeconds(5));
            Assert.True(guard.IsCurrent(Session, "P3"));
        }
    }

    /// <summary>Plays the guard didn't see served: Jellyfin relayed or transcoded them.</summary>
    public class Unseen
    {
        [Fact]
        public void AShortPlayOfAnUncachedTitleHolds()
        {
            var guard = new AutoplayGuard();

            Assert.True(guard.StopReported(Session, new StopReport(null, ("24545", 1), TimeSpan.FromSeconds(2), Episode), Now));
            Assert.True(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(3)));
        }

        [Fact]
        public void AShortPlayOfACachedTitleDoesNot()
        {
            // Review round 6, finding 3: a cached movie transcoded through Jellyfin, stopped after five seconds.
            var guard = new AutoplayGuard();

            Assert.False(guard.StopReported(Session, new StopReport("P1", ("24545", null), TimeSpan.FromSeconds(5), Episode, Cached: true), Now));
            Assert.False(guard.Hold(Session, "P2", ItemB, startsDownload: true, Now.AddSeconds(1)));
        }

        [Fact]
        public void AShortLiveStreamMayHaveBeenAPlaceholder()
        {
            var guard = new AutoplayGuard();
            guard.Served(Session, "P1", "24545", 1, ServedKind.Live, Now.AddSeconds(-6));

            Assert.True(guard.StopReported(Session, new StopReport("P1", ("24545", 1), TimeSpan.FromSeconds(5), Episode), Now));
        }

        [Fact]
        public void AServedKindRecordedForATranscodedAttemptCounts()
        {
            var guard = new AutoplayGuard();
            guard.Served(null, "P1", "24545", null, ServedKind.Ready, Now.AddSeconds(-5));

            Assert.False(guard.StopReported(Session, new StopReport("P1", ("24545", null), TimeSpan.FromSeconds(5), Episode), Now));
        }
    }
}

/// <summary>Reading the player's playback reports, and naming the attempt in its stream link.</summary>
public class PlaystateReportTests
{
    [Fact]
    public void AStopReportCarriesTheNextMediaType()
    {
        var item = Guid.NewGuid();
        var report = Api.PlaystateReport.Read(
            "ReportPlaybackStopped",
            new Dictionary<string, object?>
            {
                ["playbackStopInfo"] = new MediaBrowser.Model.Session.PlaybackStopInfo
                {
                    ItemId = item, PlaySessionId = "P1", PositionTicks = 50_000_000, NextMediaType = "Video", Failed = true,
                },
            })!;

        Assert.True(report.Stop);
        Assert.Equal(item, report.ItemId);
        Assert.Equal("P1", report.PlaySessionId);
        Assert.Equal("Video", report.NextMediaType);
        Assert.True(report.Failed);
        Assert.Equal(50_000_000, report.PositionTicks);
    }

    [Fact]
    public void AStartReportCarriesThePlayMethod()
    {
        var report = Api.PlaystateReport.Read(
            "ReportPlaybackStart",
            new Dictionary<string, object?>
            {
                ["playbackStartInfo"] = new MediaBrowser.Model.Session.PlaybackStartInfo
                {
                    PlaySessionId = "P1", PlayMethod = MediaBrowser.Model.Session.PlayMethod.DirectPlay,
                },
            })!;

        Assert.False(report.Stop);
        Assert.Equal(MediaBrowser.Model.Session.PlayMethod.DirectPlay, report.PlayMethod);
    }

    [Fact]
    public void LegacyEndpointsAreReadToo()
    {
        var stop = Api.PlaystateReport.Read(
            "OnPlaybackStoppedLegacy",
            new Dictionary<string, object?> { ["itemId"] = Guid.NewGuid(), ["nextMediaType"] = "Audio", ["positionTicks"] = (long?)10L, ["playSessionId"] = "P2" })!;
        Assert.True(stop.Stop);
        Assert.Equal("Audio", stop.NextMediaType);
        Assert.Equal(10L, stop.PositionTicks);

        var start = Api.PlaystateReport.Read(
            "OnPlaybackStart",
            new Dictionary<string, object?> { ["itemId"] = Guid.NewGuid(), ["playMethod"] = (MediaBrowser.Model.Session.PlayMethod?)MediaBrowser.Model.Session.PlayMethod.Transcode })!;
        Assert.Equal(MediaBrowser.Model.Session.PlayMethod.Transcode, start.PlayMethod);

        Assert.Null(Api.PlaystateReport.Read("ReportPlaybackProgress", new Dictionary<string, object?>()));
    }

    [Fact]
    public void TheStreamLinkNamesTheAttempt()
    {
        const string Url = "http://jf:8096/AudioVault/Stream/123?ep=2&sig=abc";
        var named = PlaybackTicket.WithPlaySession(PlaybackTicket.AddTo(Url, "tok"), "a1b2");
        Assert.Equal(Url + "&t=tok&PlaySessionId=a1b2", named);

        // Named again for a new attempt: replaced, not repeated.
        Assert.Equal(Url + "&t=tok&PlaySessionId=c3", PlaybackTicket.WithPlaySession(named, "c3"));
        Assert.Equal("http://jf/AudioVault/Stream/9?PlaySessionId=x", PlaybackTicket.WithPlaySession("http://jf/AudioVault/Stream/9", "x"));
        Assert.Equal("http://jf/AudioVault/Stream/9?ep=1", PlaybackTicket.WithPlaySession("http://jf/AudioVault/Stream/9?PlaySessionId=x&ep=1", null));

        Assert.True(Api.PlaybackIntentFilter.TryParse(named, out var entry, out var ep));
        Assert.Equal(("123", (int?)2), (entry, ep));
    }

    [Fact]
    public void AFallbackRequestTurnsDirectPlayOff()
    {
        Assert.True(Api.PlaybackSignals.DirectPlayDisabled([new("enableDirectPlay", false)]));
        Assert.True(Api.PlaybackSignals.DirectPlayDisabled([new("playbackInfoDto", new { EnableDirectPlay = (bool?)false })]));
        Assert.False(Api.PlaybackSignals.DirectPlayDisabled([new("playbackInfoDto", new { EnableDirectPlay = (bool?)null })]));
        Assert.False(Api.PlaybackSignals.DirectPlayDisabled([]));
    }
}
