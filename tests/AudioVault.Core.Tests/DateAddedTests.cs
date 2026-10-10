using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

public sealed class DateAddedTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(T0);
    private readonly FakeSource _source = new();

    public DateAddedTests()
    {
        Store = new CatalogStore(_dir.Path);
        Sync = new CatalogSyncService(_source, Store, NullLogger<CatalogSyncService>.Instance, _time);
    }

    private CatalogStore Store { get; }

    private CatalogSyncService Sync { get; }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task BaselineEntriesAreDatedInIdOrder()
    {
        _source.All = [E.Movie("30", "C", 2000), E.Movie("10", "A", 2000), E.Movie("25", "B", 2000)];
        await Sync.SyncFullAsync(null, default);

        Assert.Equal(T0, Store.DateAdded("30"));
        Assert.Equal(T0 - (DateAddedRules.BaselineStep * 5), Store.DateAdded("25"));
        Assert.Equal(T0 - (DateAddedRules.BaselineStep * 20), Store.DateAdded("10"));
    }

    [Fact]
    public async Task LaterEntriesUseFirstSeen()
    {
        _source.All = [E.Movie("10", "A", 2000), E.Movie("20", "B", 2000)];
        await Sync.SyncFullAsync(null, default);

        _time.Now = T0.AddHours(6);
        _source.Recent = [E.Movie("21", "New", 2001)];
        await Sync.SyncRecentAsync(default);

        _time.Now = T0.AddDays(7);
        _source.All = [.. _source.All, E.Movie("21", "New", 2001), E.Movie("22", "Newer", 2001)];
        await Sync.SyncFullAsync(null, default);

        Assert.Equal(T0.AddHours(6), Store.DateAdded("21"));
        Assert.Equal(T0.AddDays(7), Store.DateAdded("22"));
        Assert.Equal(T0, Store.DateAdded("20"));
        Assert.Equal(T0, Store.Dates.BaselineAt);
    }

    [Fact]
    public async Task FirstRecentSnapshotBumpsNothing()
    {
        _source.All = Enumerable.Range(1, 10).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList();
        await Sync.SyncFullAsync(null, default);

        _time.Now = T0.AddHours(6);
        _source.Recent = [E.Movie("3", "M3", 2000), E.Movie("9", "M9", 2000), E.Movie("10", "M10", 2000)];
        var result = await Sync.SyncRecentAsync(default);

        Assert.Empty(result.Bumped);
        Assert.Equal(T0 - (DateAddedRules.BaselineStep * 7), Store.DateAdded("3"));
    }

    [Fact]
    public async Task OldIdReappearingInRecentIsBumped()
    {
        _source.All = Enumerable.Range(1, 10).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList();
        await Sync.SyncFullAsync(null, default);
        _time.Now = T0.AddHours(6);
        _source.Recent = [E.Movie("9", "M9", 2000), E.Movie("10", "M10", 2000)];
        await Sync.SyncRecentAsync(default);

        _time.Now = T0.AddHours(12);
        _source.Recent = [E.Movie("4", "M4", 2000), E.Movie("9", "M9", 2000), E.Movie("10", "M10", 2000)];
        var result = await Sync.SyncRecentAsync(default);

        Assert.Equal(["4"], result.Bumped);
        Assert.Equal(T0.AddHours(12), Store.DateAdded("4"));
        Assert.Equal(T0 - (DateAddedRules.BaselineStep * 1), Store.DateAdded("9"));

        // Still in the table next time: not bumped again.
        _time.Now = T0.AddHours(18);
        Assert.Empty((await Sync.SyncRecentAsync(default)).Bumped);
        Assert.Equal(T0.AddHours(12), Store.DateAdded("4"));
        Assert.Equal(T0.AddHours(12), new CatalogStore(_dir.Path).DateAdded("4"));
    }

    [Fact]
    public async Task NewHigherIdsInRecentAreNotBumped()
    {
        _source.All = Enumerable.Range(1, 10).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList();
        await Sync.SyncFullAsync(null, default);
        _time.Now = T0.AddHours(6);
        _source.Recent = [E.Movie("10", "M10", 2000)];
        await Sync.SyncRecentAsync(default);

        _time.Now = T0.AddHours(12);
        _source.Recent = [E.Movie("11", "M11", 2001), E.Movie("10", "M10", 2000)];
        var result = await Sync.SyncRecentAsync(default);

        Assert.Empty(result.Bumped);
        Assert.Equal(T0.AddHours(12), Store.DateAdded("11"));

        // Picked up by a full sync between two recent syncs: newer than the last snapshot, so not a re-upload.
        _time.Now = T0.AddHours(14);
        _source.All = [.. _source.All, E.Movie("11", "M11", 2001), E.Movie("12", "M12", 2001), E.Movie("13", "M13", 2001)];
        await Sync.SyncFullAsync(null, default);
        _time.Now = T0.AddHours(18);
        _source.Recent = [E.Movie("13", "M13", 2001), E.Movie("12", "M12", 2001), E.Movie("11", "M11", 2001)];

        Assert.Empty((await Sync.SyncRecentAsync(default)).Bumped);
        Assert.Equal(T0.AddHours(14), Store.DateAdded("12"));
    }

    [Fact]
    public async Task EmptyRecentListKeepsSnapshot()
    {
        _source.All = Enumerable.Range(1, 10).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList();
        await Sync.SyncFullAsync(null, default);
        _source.Recent = [E.Movie("9", "M9", 2000), E.Movie("10", "M10", 2000)];
        await Sync.SyncRecentAsync(default);

        _source.Recent = [];
        await Sync.SyncRecentAsync(default);
        _source.Recent = [E.Movie("9", "M9", 2000), E.Movie("10", "M10", 2000)];

        Assert.Empty((await Sync.SyncRecentAsync(default)).Bumped);
    }

    [Fact]
    public void CatalogWithoutDatesLoadsAndInfersBaseline()
    {
        static string Entry(string id, DateTimeOffset seen) =>
            $$"""{"Entry":{"Id":"{{id}}","Kind":"Movie","Title":"T{{id}}","Year":2000,"Season":null,"Part":null,"Version":{"Language":"English","IsTts":false,"Tags":[]},"RawName":"T{{id}}"},"FirstSeen":"{{seen:O}}","LastSeen":"{{seen:O}}"}""";
        File.WriteAllText(
            Path.Combine(_dir.Path, "catalog.json"),
            $"[{Entry("100", T0)},{Entry("40", T0.AddSeconds(2))},{Entry("101", T0.AddDays(3))}]");

        var store = new CatalogStore(_dir.Path);

        Assert.Equal(3, store.Count);
        Assert.Null(store.All().Single(e => e.Entry.Id == "100").BumpedAt);
        Assert.Equal(T0.AddSeconds(2), store.Dates.BaselineAt);
        Assert.Equal(T0.AddSeconds(2), store.DateAdded("100"));
        Assert.Equal(T0.AddSeconds(2) - (DateAddedRules.BaselineStep * 60), store.DateAdded("40"));
        Assert.Equal(T0.AddDays(3), store.DateAdded("101"));
    }

    [Fact]
    public async Task EpisodesShareTheSeasonDate()
    {
        _source.All = [E.Show("5", "Show", 1, 2000), E.Show("8", "Show", 2, 2001), E.Movie("9", "Film", 2002)];
        await Sync.SyncFullAsync(null, default);
        var counts = new EpisodeCountStore(_dir.Path);
        counts.Set("5", new EpisodeCount(3, EpisodeCountOrigin.Metadata));
        var library = new LibrarySyncService(Store, counts, new LibraryWriter(NullLogger<LibraryWriter>.Instance), NullLogger<LibrarySyncService>.Instance);
        var options = new LibraryOptions { MoviesPath = Path.Combine(_dir.Path, "movies"), ShowsPath = Path.Combine(_dir.Path, "shows") };
        library.Sync(options, (id, ep) => $"http://x/{id}?ep={ep}");
        Assert.Equal((1, 4), library.IndexedByRoot);

        for (var ep = 1; ep <= 3; ep++)
        {
            var path = library.FindStrmPath("5", ep)!;
            Assert.Equal(("5", (int?)ep), library.FindEntry(path));
            Assert.Equal(Store.DateAdded("5"), library.DateAddedFor(path));
        }

        Assert.Equal(T0 - (DateAddedRules.BaselineStep * 4), library.DateAddedFor(library.FindStrmPath("5", 1)!));
        var season1 = Path.GetDirectoryName(library.FindStrmPath("5", 1)!)!;
        Assert.Equal(Store.DateAdded("5"), library.DateAddedFor(season1));
        Assert.Equal(Store.DateAdded("8"), library.DateAddedFor(Path.GetDirectoryName(season1)!));
        Assert.Equal(T0, library.DateAddedFor(library.FindStrmPath("9", null)!));
        Assert.Null(library.DateAddedFor(Path.Combine(_dir.Path, "elsewhere.strm")));
    }
}
