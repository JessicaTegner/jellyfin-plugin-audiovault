using AudioVault.Core.Media;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

/// <summary>
/// A failed preparation (even one whose player already got a placeholder) authorises no more downloads, a stop retires
/// only its own attempt's intent, and concurrent blocked fetches can't overwrite a newer attempt's failure.
/// </summary>
public class FailedPreparationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly SourceAccount AliceAccount = new("alice@example.com", "a");
    private static readonly SourceAccount BobAccount = new("bob@example.com", "b");
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();

    // Jellyfin's transcoder fetching the .strm URL: no ticket, server user agent, no probe window.
    private static PlaybackIntent? TranscoderFetch(PlaybackIntents intents, string entryId = "1", int? episode = null) =>
        PlayAttempt.UnticketedIntent(true, intents.Find(entryId, episode), _ => true, ProbeWindow.None);

    [Fact]
    public void QueuedFailureLeavesNoDownloadAuthorizationForALaterTranscoderFetch()
    {
        // Alice pressed play; the probe got a placeholder and preparation went on detached, then failed.
        var intents = new PlaybackIntents();
        intents.Register("1", null, Alice, AliceAccount, "P1", [Item, Version]);
        intents.Register("1", 2, Alice, AliceAccount, "P1", [Item]);
        Assert.NotNull(TranscoderFetch(intents));

        Assert.Equal(1, intents.RetireFailed("1", null));

        Assert.Null(TranscoderFetch(intents));

        // Other episodes' attempts aren't the failed one.
        Assert.NotNull(TranscoderFetch(intents, "1", 2));
    }

    [Fact]
    public void AFailureRetiresEveryEarlierAttemptButNotOneRegisteredAfterIt()
    {
        var intents = new PlaybackIntents();
        intents.Register("1", null, Alice, AliceAccount, "P1", [Item]);
        intents.Register("1", null, Bob, BobAccount, "P2", [Item]);

        Assert.Equal(2, intents.RetireFailed("1", null));
        Assert.Null(TranscoderFetch(intents));

        // A new play after the failure may download again.
        intents.Register("1", null, Alice, AliceAccount, "P3", [Item]);
        Assert.Equal("P3", TranscoderFetch(intents)!.PlaySessionId);
    }

    [Fact]
    public async Task AStopDoesNotRemoveANewerIntentRegisteredWhileItRuns()
    {
        for (var i = 0; i < 2000; i++)
        {
            var intents = new PlaybackIntents();
            intents.Register("1", null, Alice, AliceAccount, "P1", [Item]);
            using var start = new Barrier(2);
            var stop = Task.Run(() =>
            {
                start.SignalAndWait();
                intents.RetireStopped("P1", [Item], [Alice]);
            });
            var replace = Task.Run(() =>
            {
                start.SignalAndWait();
                intents.Register("1", null, Alice, AliceAccount, "P2", [Item]);
            });
            await Task.WhenAll(stop, replace);

            Assert.Equal("P2", intents.Find("1", null)?.PlaySessionId);
        }
    }

    [Fact]
    public void ExplainsOneFailureOnceButANewOneAgain()
    {
        var explained = new ExplainedFailures();

        Assert.True(explained.TryClaim("alice", "1", null, Now));
        Assert.False(explained.TryClaim("alice", "1", null, Now.AddSeconds(1)));
        Assert.True(explained.TryClaim("alice", "1", 2, Now));
        Assert.True(explained.TryClaim("bob", "1", null, Now));
        Assert.True(explained.TryClaim("alice", "1", null, Now + ExplainedFailures.Window + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void AStaleFetchDoesNotReplaceANewerAttemptsFailure()
    {
        // P1 was issued before P2, but P2's blocked fetch is recorded first.
        var book = new PendingFailures();
        book.Issued("P1", Now);
        book.Issued("P2", Now);
        var before = new PlayState(true, 3, 0, null);

        var second = Record(book, "P2", "second", before);
        Assert.NotNull(second);
        Assert.Null(Record(book, "P1", "first", new PlayState(false, 0, 0, null)));

        Assert.Same(second, book.Take("alice", Item, "P2", Now, TimeSpan.FromHours(1), out _));
    }

    [Fact]
    public void ANewerAttemptKeepsTheEarliestWatchedState()
    {
        var book = new PendingFailures();
        book.Issued("P1", Now);
        book.Issued("P2", Now);
        var before = new PlayState(false, 0, 1234, null);
        Record(book, "P1", "first", before);

        // By now the placeholder of P1 "played" to the end; P2's capture would see that.
        var second = Record(book, "P2", "second", new PlayState(true, 1, 0, Now.UtcDateTime));

        Assert.Equal(before, second!.PriorStates[Item]);
        Assert.Equal("second", book.Find("alice", Item, out _)!.Message);
    }

    [Fact]
    public async Task ConcurrentBlockedFetchesCompletingOutOfOrderKeepTheNewestAttempt()
    {
        for (var i = 0; i < 500; i++)
        {
            var book = new PendingFailures();
            var original = new PlayState(false, 0, 42, null);
            book.Issued("P0", Now);
            Record(book, "P0", "zero", original);
            book.Issued("P1", Now);
            book.Issued("P2", Now);

            // Both see P0's record; whichever finishes last, P2's record must stand.
            using var start = new Barrier(2);
            var first = Task.Run(() =>
            {
                start.SignalAndWait();
                return Record(book, "P1", "one", new PlayState(true, 1, 0, null));
            });
            var second = Task.Run(() =>
            {
                start.SignalAndWait();
                return Record(book, "P2", "two", new PlayState(true, 1, 0, null));
            });
            await Task.WhenAll(first, second);

            var waiting = book.Find("alice", Item, out _);
            Assert.Equal("P2", waiting!.PlaySessionId);
            Assert.Equal(original, waiting.PriorStates[Item]);
            Assert.Same(waiting, book.Take("alice", Version, "P2", Now, TimeSpan.FromHours(1), out _));
        }
    }

    private static PendingFailure? Record(PendingFailures book, string playSessionId, string message, PlayState state) =>
        book.Record(Now, "alice", Alice, message, playSessionId, Item, [Item, Version], () => new Dictionary<Guid, PlayState> { [Item] = state });
}
