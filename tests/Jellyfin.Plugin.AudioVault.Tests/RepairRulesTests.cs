using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class RepairRulesTests
{
    private static readonly DateTime Refreshed = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, null, true)] // created by the scan, never refreshed
    [InlineData(false, "", true)]
    [InlineData(true, null, true)] // refreshed, yet without a key it still hides
    [InlineData(false, "tmdb-123", true)] // a refresh whose providers failed leaves the date unset
    [InlineData(true, "tmdb-123", false)]
    public void NeedsRefreshUntilJellyfinFinishedTheFirstRefresh(bool refreshed, string? key, bool expected) =>
        Assert.Equal(expected, RepairRules.NeedsRefresh(refreshed ? Refreshed : default, key));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("7711eb708e195260a5a3480304cc6cb8", true)]
    public void SearchableOnlyWithAPresentationKey(string? key, bool expected) =>
        Assert.Equal(expected, RepairRules.IsSearchable(key));

    [Theory]
    [InlineData("/config/data/audiovault/library/movies/A (2001)/A (2001) - English.strm", "/config/data/audiovault/library/movies", true)]
    [InlineData("/config/data/audiovault/library/movies/A (2001)/A.strm", "/config/data/audiovault/library/movies/", true)]
    [InlineData("/config/data/audiovault/library/movies", "/config/data/audiovault/library/movies", false)]
    [InlineData("/config/data/audiovault/library/movies2/A.strm", "/config/data/audiovault/library/movies", false)]
    [InlineData("/media/movies/A.mkv", "/config/data/audiovault/library/movies", false)]
    [InlineData(@"D:\av\shows\Show\Season 1\e1.strm", @"D:\av\shows", true)]
    [InlineData(null, "/config/data/audiovault/library/movies", false)]
    [InlineData("/x/a.strm", "", false)]
    public void OnlyItemsInsideTheLibraryFolder(string? path, string root, bool expected) =>
        Assert.Equal(expected, RepairRules.IsUnder(path, root));

    [Fact]
    public void WaitsOnlyWhileTheLibraryScanRuns()
    {
        Assert.True(RepairRules.LibraryScanRunning([("AudioVaultFullSync", false), ("RefreshLibrary", true)]));
        Assert.False(RepairRules.LibraryScanRunning([("RefreshLibrary", false), ("AudioVaultFullSync", true)]));
        Assert.False(RepairRules.LibraryScanRunning([]));
    }

    [Fact]
    public void APassQueuesAtMostItsLimit()
    {
        var queue = new RepairQueue(TimeSpan.FromHours(6));
        var ids = Ids(5);

        var first = queue.Take(ids, Now, limit: 3);
        var second = queue.Take(ids, Now.AddMinutes(30), limit: 3);

        Assert.Equal(ids[..3], first);
        Assert.Equal(ids[3..], second);
    }

    [Fact]
    public void ZeroLimitQueuesNothingAndNoLimitQueuesAll()
    {
        var queue = new RepairQueue(TimeSpan.FromHours(6));
        Assert.Empty(queue.Take(Ids(3), Now, limit: 0));
        Assert.Equal(3, queue.Take(Ids(3), Now).Count);
    }

    [Fact]
    public void RecentlyQueuedItemsAreSkippedUntilTheyExpire()
    {
        var queue = new RepairQueue(TimeSpan.FromHours(6));
        var ids = Ids(2);
        queue.Take(ids, Now);

        Assert.Empty(queue.Take(ids, Now.AddHours(5)));
        Assert.Equal(ids, queue.Take(ids, Now.AddHours(6)));
    }

    [Fact]
    public void ExpiredEntriesAreForgotten()
    {
        var queue = new RepairQueue(TimeSpan.FromHours(6));
        queue.Take(Ids(4), Now);
        queue.Take([], Now.AddHours(7));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void AManualPassAlsoTakesRecentlyQueuedItemsButEachOnce()
    {
        var queue = new RepairQueue(TimeSpan.FromHours(6));
        var ids = Ids(3);
        queue.Take(ids, Now);

        var manual = queue.Take([.. ids, ids[0]], Now.AddMinutes(1), includeRecent: true);

        Assert.Equal(ids, manual);
        // And the automatic pass after it doesn't queue them again.
        Assert.Empty(queue.Take(ids, Now.AddHours(5)));
    }

    [Fact]
    public void CountsAreUnknownUntilTheFirstPass()
    {
        var counts = new RepairCounts();
        counts.Update(Guid.NewGuid(), RepairKind.Movie, waiting: true);
        Assert.Null(counts.Waiting(RepairKind.Movie));

        counts.Replace([]);
        Assert.Equal(0, counts.Waiting(RepairKind.Movie));
    }

    [Fact]
    public void CountsFollowRefreshesBetweenPasses()
    {
        var counts = new RepairCounts();
        var ids = Ids(4);
        counts.Replace([(ids[0], RepairKind.Movie), (ids[1], RepairKind.Movie), (ids[2], RepairKind.Episode), (ids[3], RepairKind.ShowFolder)]);
        Assert.Equal(2, counts.Waiting(RepairKind.Movie));
        Assert.Equal(1, counts.Waiting(RepairKind.Episode));
        Assert.Equal(1, counts.Waiting(RepairKind.ShowFolder));

        counts.Update(ids[0], RepairKind.Movie, waiting: false); // its refresh finished
        counts.Remove(ids[2]); // deleted
        var added = Guid.NewGuid();
        counts.Update(added, RepairKind.Episode, waiting: true); // a new item from the scan

        Assert.Equal(1, counts.Waiting(RepairKind.Movie));
        Assert.Equal(1, counts.Waiting(RepairKind.Episode));

        counts.Replace([]);
        Assert.Equal(0, counts.Waiting(RepairKind.Movie));
        Assert.Equal(0, counts.Waiting(RepairKind.ShowFolder));
    }

    private static Guid[] Ids(int n) => [.. Enumerable.Range(1, n).Select(i => new Guid(i, 0, 0, new byte[8]))];
}
