using AudioVault.Core.Media;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Api;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class PlaybackTicketTests
{
    private const string Secret = "0123456789ABCDEF";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private static readonly Guid User = Guid.Parse("8d3c1f8a-7a43-4b55-9f0e-2c7d1f0b9a11");

    private static PlaybackTicket Ticket(int? ep = 3) =>
        new(User, "alice@example.com", "12345", ep, "session-1", Now + PlaybackTicket.Lifetime);

    [Fact]
    public void RoundTrips()
    {
        var read = PlaybackTicket.Read(Secret, Ticket().Create(Secret), "12345", 3, Now);

        Assert.Equal(Ticket(), read);
    }

    [Fact]
    public void CarriesThePlaySessionId()
    {
        var ticket = Ticket() with { PlaySessionId = "abc|def" };

        Assert.Equal("abc|def", PlaybackTicket.Read(Secret, ticket.Create(Secret), "12345", 3, Now)!.PlaySessionId);
    }

    [Fact]
    public void ReadsTicketsIssuedBeforeThePlaySessionId()
    {
        // The 9-part payload of earlier versions, signed the same way.
        var payload = string.Join('|', User.ToString("N"), "alice%40example.com", "12345", "3", "session-1",
            (Now + PlaybackTicket.Lifetime).ToUnixTimeSeconds(), "0", string.Empty, string.Empty);
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var encoded = B64(System.Text.Encoding.UTF8.GetBytes(payload));
        var mac = B64(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("ticket:" + Secret), System.Text.Encoding.ASCII.GetBytes(encoded)));

        var read = PlaybackTicket.Read(Secret, encoded + "." + mac, "12345", 3, Now);

        Assert.Equal(Ticket(), read);
        Assert.Null(read!.PlaySessionId);
    }

    [Fact]
    public void MovieTicketHasNoEpisode() =>
        Assert.NotNull(PlaybackTicket.Read(Secret, Ticket(null).Create(Secret), "12345", null, Now));

    [Fact]
    public void ExpiresAfterItsLifetime()
    {
        var token = Ticket().Create(Secret);

        Assert.NotNull(PlaybackTicket.Read(Secret, token, "12345", 3, Now + PlaybackTicket.Lifetime - TimeSpan.FromSeconds(1)));
        Assert.Null(PlaybackTicket.Read(Secret, token, "12345", 3, Now + PlaybackTicket.Lifetime));
    }

    [Theory]
    [InlineData("12346", 3)]
    [InlineData("12345", 4)]
    [InlineData("12345", null)]
    public void OnlyValidForItsOwnStream(string entryId, int? ep) =>
        Assert.Null(PlaybackTicket.Read(Secret, Ticket().Create(Secret), entryId, ep, Now));

    [Fact]
    public void RejectsAnotherSecret() =>
        Assert.Null(PlaybackTicket.Read("another secret", Ticket().Create(Secret), "12345", 3, Now));

    [Fact]
    public void RejectsTamperedPayload()
    {
        var token = Ticket().Create(Secret);
        var other = (Ticket() with { UserId = Guid.NewGuid() }).Create(Secret);
        var forged = other.Split('.')[0] + "." + token.Split('.')[1];

        Assert.Null(PlaybackTicket.Read(Secret, forged, "12345", 3, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void RejectsGarbage(string? token) =>
        Assert.Null(PlaybackTicket.Read(Secret, token, "12345", 3, Now));

    [Fact]
    public void KeepsSeparatorsInFields()
    {
        var ticket = Ticket() with { AccountKey = "a|b.c@example.com", EntryId = "x|y" };

        Assert.Equal(ticket, PlaybackTicket.Read(Secret, ticket.Create(Secret), "x|y", 3, Now));
    }

    [Fact]
    public void AddToAppendsAndReplaces()
    {
        const string Url = "https://jf.example.com/AudioVault/Stream/1?sig=abc&ep=2";

        var once = PlaybackTicket.AddTo(Url, "T1");
        Assert.Equal(Url + "&t=T1", once);
        Assert.Equal(Url + "&t=T2", PlaybackTicket.AddTo(once, "T2"));
    }

    [Fact]
    public void TicketedUrlStillParsesAsOurStream()
    {
        var url = PlaybackTicket.AddTo("https://jf.example.com/AudioVault/Stream/77?sig=abc&ep=2", Ticket().Create(Secret));

        Assert.True(PlaybackIntentFilter.TryParse(url, out var entryId, out var ep));
        Assert.Equal("77", entryId);
        Assert.Equal(2, ep);
    }

    [Theory]
    [InlineData("Lavf/61.7.100", true)]
    [InlineData("Jellyfin-Server/12.2.0", true)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0)", false)]
    [InlineData(null, false)]
    public void RecognisesJellyfinsOwnRequests(string? userAgent, bool server) =>
        Assert.Equal(server, StreamRequesters.IsServer(userAgent));
}

public class PlaybackSignalTests
{
    private sealed class PostedDto
    {
        public bool? AutoOpenLiveStream { get; set; }
    }

    [Fact]
    public void PostedPlaybackWithAutoOpenIsPlay() =>
        Assert.True(PlaybackSignals.IsPlay("GetPostedPlaybackInfo", [new("playbackInfoDto", new PostedDto { AutoOpenLiveStream = true })]));

    [Fact]
    public void QueryAutoOpenIsPlay() =>
        Assert.True(PlaybackSignals.IsPlay("GetPostedPlaybackInfo", [new("autoOpenLiveStream", true)]));

    [Fact]
    public void InformationRequestsAreNotPlay()
    {
        Assert.False(PlaybackSignals.IsPlay("GetPostedPlaybackInfo", [new("playbackInfoDto", new PostedDto { AutoOpenLiveStream = false })]));
        Assert.False(PlaybackSignals.IsPlay("GetPostedPlaybackInfo", [new("playbackInfoDto", null)]));
        Assert.False(PlaybackSignals.IsPlay("GetPlaybackInfo", [new("autoOpenLiveStream", true)]));
    }
}

public class PerUserIntentTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly SourceAccount AliceAccount = new("alice@example.com", "a");
    private static readonly SourceAccount BobAccount = new("bob@example.com", "b");

    [Fact]
    public void OneUsersIntentIsUsed()
    {
        var intents = new PlaybackIntents();
        intents.Register("1", null, Alice, AliceAccount);

        Assert.Equal(AliceAccount, intents.AccountFor("1", null));
    }

    [Fact]
    public void TwoUsersOnTheSameEntryAreAmbiguous()
    {
        var intents = new PlaybackIntents();
        intents.Register("1", null, Alice, AliceAccount);
        intents.Register("1", null, Bob, BobAccount);

        Assert.Null(intents.AccountFor("1", null));
    }

    [Fact]
    public void RevokingAUserRemovesOnlyTheirIntents()
    {
        var intents = new PlaybackIntents();
        intents.Register("1", null, Alice, AliceAccount);
        intents.Register("1", null, Bob, BobAccount);
        intents.Register("2", null, Alice, AliceAccount);

        intents.Revoke(Alice);

        Assert.Equal(BobAccount, intents.AccountFor("1", null));
        Assert.Null(intents.AccountFor("2", null));
    }

    private static PlaybackIntent? Unticketed(PlaybackIntents intents, ProbeWindows windows, bool server = true) =>
        PlayAttempt.UnticketedIntent(server, intents.Find("1", null), _ => true, windows.State("1", null));

    [Fact]
    public void FailedPlayThenInformationRequestDownloadsNothing()
    {
        var intents = new PlaybackIntents();
        var windows = new ProbeWindows();
        var item = Guid.NewGuid();
        intents.Register("1", null, Alice, AliceAccount, "P1", [item]);

        // Preparing Alice's play fails (say, the network drops) and her player closes.
        Assert.True(intents.Retire(Alice, "1", null, "P1"));

        // Anyone's detail page then makes Jellyfin probe the title: nothing names an account, so nothing opens.
        using (windows.Open([("1", null)]))
        {
            Assert.Null(Unticketed(intents, windows));
        }

        Assert.Null(Unticketed(intents, windows));
    }

    [Fact]
    public void InformationOnlyProbesNeverUseAnIntent()
    {
        var intents = new PlaybackIntents();
        var windows = new ProbeWindows();
        intents.Register("1", null, Alice, AliceAccount, "P1", [Guid.NewGuid()]);

        using (windows.Open([("1", null)]))
        {
            Assert.Equal(ProbeWindow.Information, windows.State("1", null));
            Assert.Null(Unticketed(intents, windows));

            // Alice presses play meanwhile: her play's probe may download.
            using (windows.Open([("1", null)], play: true))
            {
                Assert.Equal(ProbeWindow.Play, windows.State("1", null));
                Assert.Equal(AliceAccount, Unticketed(intents, windows)!.Account);
            }

            Assert.Null(Unticketed(intents, windows));
        }

        // Jellyfin's transcoder during her play, outside any probe: the one live intent still counts.
        Assert.Equal(ProbeWindow.None, windows.State("1", null));
        Assert.Equal(Alice, Unticketed(intents, windows)!.UserId);

        // But only for Jellyfin's own requests, and only for a current account.
        Assert.Null(Unticketed(intents, windows, server: false));
        Assert.Null(PlayAttempt.UnticketedIntent(true, intents.Find("1", null), _ => false, ProbeWindow.None));
    }

    [Fact]
    public void StoppingAPlayRetiresOnlyThatAttemptsIntent()
    {
        var intents = new PlaybackIntents();
        var item = Guid.NewGuid();
        intents.Register("1", null, Alice, AliceAccount, "P1", [item]);
        intents.Register("2", null, Bob, BobAccount, null, [item]);

        // Another attempt's stop leaves Alice's intent.
        Assert.Equal(0, intents.RetireStopped("P0", [item], [Alice]));
        Assert.NotNull(intents.Find("1", null));

        Assert.Equal(1, intents.RetireStopped("P1", [item], [Alice]));
        Assert.Null(intents.Find("1", null));

        // An intent registered before its PlaySessionId was known is matched by user and item.
        Assert.Equal(0, intents.RetireStopped("P2", [item], [Alice]));
        Assert.Equal(1, intents.RetireStopped("P2", [item], [Bob]));
        Assert.Null(intents.Find("2", null));
    }

    [Fact]
    public void FailureOfAnOlderAttemptKeepsTheNewerOnesIntent()
    {
        var intents = new PlaybackIntents();
        intents.Register("1", null, Alice, AliceAccount, "P2", []);

        Assert.False(intents.Retire(Alice, "1", null, "P1"));
        Assert.NotNull(intents.Find("1", null));
        Assert.True(intents.Retire(Alice, "1", null, null));
    }
}

public class PendingFailuresTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();

    private static PendingFailure Failure(string? session, string message, PlayState? prior = null) =>
        new(Now, session, Guid.NewGuid(), message, prior is null ? new Dictionary<Guid, PlayState>() : new Dictionary<Guid, PlayState> { [Item] = prior });

    [Fact]
    public void SessionsKeepTheirOwnMessages()
    {
        var book = new PendingFailures();
        book.Add(Failure("alice", "for alice"), [Item, Version]);
        book.Add(Failure("bob", "for bob"), [Item, Version]);

        Assert.Equal("for bob", book.Take("bob", Item, Now, Window)?.Message);
        Assert.Equal("for alice", book.Take("alice", Version, Now, Window)?.Message);
        Assert.Null(book.Take("alice", Item, Now, Window));
    }

    [Fact]
    public void StopOfAnotherSessionDoesNotConsumeIt()
    {
        var book = new PendingFailures();
        book.Add(Failure("alice", "for alice"), [Item]);

        Assert.Null(book.Take("bob", Item, Now, Window));
        Assert.NotNull(book.Take("alice", Item, Now, Window));
    }

    [Fact]
    public void UnknownSessionMatchesAnyStopOfTheItem()
    {
        var book = new PendingFailures();
        book.Add(Failure(null, "unknown"), [Item]);

        Assert.Equal("unknown", book.Take("whoever", Item, Now, Window)?.Message);
    }

    [Fact]
    public void KeepsTheStateFromBeforeTheAttempt()
    {
        var watched = new PlayState(true, 3, 0, Now.UtcDateTime.AddDays(-2));
        var book = new PendingFailures();
        book.Add(Failure("alice", "x", watched), [Item, Version]);

        Assert.Equal(watched, book.Take("alice", Item, Now, Window)!.PriorStates[Item]);
    }

    [Fact]
    public void ExpiredFailuresAreDropped()
    {
        var book = new PendingFailures();
        book.Add(Failure("alice", "x"), [Item]);

        Assert.Null(book.Take("alice", Item, Now + Window + TimeSpan.FromSeconds(1), Window));
    }

    [Fact]
    public void RemoveIsOnlyTrueOnce()
    {
        var book = new PendingFailures();
        var failure = Failure("alice", "x");
        book.Add(failure, [Item, Version]);

        Assert.True(book.Remove(failure));
        Assert.False(book.Remove(failure));
        Assert.Null(book.Take("alice", Version, Now, Window));
    }
}

public class PrivateFileTests
{
    [Fact]
    public void WritesOwnerOnlyFile()
    {
        var dir = Directory.CreateTempSubdirectory("av-private-");
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "secret.txt");
            PrivateFile.Write(path, "first");
            PrivateFile.Write(path, "second");

            Assert.Equal("second", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
