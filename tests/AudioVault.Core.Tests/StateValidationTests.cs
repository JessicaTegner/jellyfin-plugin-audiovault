using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

/// <summary>Stores must load whatever is usable from damaged state files rather than stop Jellyfin from starting.</summary>
public class StateValidationTests
{
    private const string Seen = "\"FirstSeen\":\"2025-01-01T00:00:00+00:00\",\"LastSeen\":\"2025-01-02T00:00:00+00:00\"";

    private static string Entry(string id, string title = "\"Alien\"", string version = "{\"Language\":\"English\",\"IsTts\":false,\"Tags\":[]}") =>
        $"{{\"Id\":\"{id}\",\"Kind\":\"Movie\",\"Title\":{title},\"Year\":1979,\"Version\":{version},\"RawName\":\"Alien (1979)\"}}";

    [Fact]
    public void CatalogSkipsInvalidEntriesAndKeepsTheRest()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "catalog.json");
        File.WriteAllText(path, $$$"""
            [
              null,
              {{{{Seen}}}},
              {"Entry":null,{{{Seen}}}},
              {"Entry":{{{Entry("1")}}},{{{Seen}}}},
              {"Entry":{{{Entry("1", "\"Aliens\"")}}},"FirstSeen":"2025-01-01T00:00:00+00:00","LastSeen":"2025-02-01T00:00:00+00:00"},
              {"Entry":{{{Entry("2", "null")}}},{{{Seen}}}},
              {"Entry":{{{Entry("3", version: "null")}}},{{{Seen}}}},
              {"Entry":{"Id":"4","Kind":9,"Title":"X","Version":{"Language":"English","IsTts":false,"Tags":[]}},{{{Seen}}}},
              {"Entry":{"Id":"5","Kind":"Show","Title":"Dark","Season":1,"Version":{"Language":"English","IsTts":false,"Tags":null}},"LastSeen":"2025-03-01T00:00:00+00:00"},
              {"Entry":{"Id":"","Kind":"Movie","Title":"X","Version":{"Language":"English","IsTts":false,"Tags":[]}},{{{Seen}}}}
            ]
            """);

        var store = new CatalogStore(dir.Path);

        // Usable for lookups, but titles were lost: the library waits for a full listing before mirroring it.
        Assert.True(store.Unreadable);
        Assert.Equal(["1", "5"], store.All().Select(e => e.Entry.Id).Order());
        Assert.Equal("Aliens", store.Get("1")!.Title); // the most recently seen duplicate wins
        var dark = store.All().Single(e => e.Entry.Id == "5");
        Assert.Empty(dark.Entry.Version.Tags);
        Assert.Equal("Dark", dark.Entry.RawName);
        Assert.Equal(dark.LastSeen, dark.FirstSeen);
        Assert.NotNull(store.DateAdded("5"));
        Assert.Single(Directory.GetFiles(dir.Path, "catalog.json.invalid-*")); // the original is kept for inspection

        // Usable for layout without null references.
        LibraryLayout.Build(store.All().Select(e => e.Entry), new LibraryOptions { MoviesPath = "/m", ShowsPath = "/s" }, _ => null);
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("[null]", true)]
    [InlineData("{not json", true)]
    [InlineData("[]", false)]
    public void CatalogThatLoadsNothingIsFlaggedUnreadable(string content, bool unreadable)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "catalog.json"), content);
        File.WriteAllText(Path.Combine(dir.Path, "catalog-dates.json"), "null");

        var store = new CatalogStore(dir.Path);

        Assert.Equal(0, store.Count);
        Assert.Equal(unreadable, store.Unreadable);
        Assert.Equal(CatalogDates.Empty, store.Dates);
    }

    [Fact]
    public void MissingCatalogIsNotUnreadable()
    {
        using var dir = new TempDir();
        Assert.False(new CatalogStore(dir.Path).Unreadable);
    }

    [Fact]
    public void UnreadableCatalogFileDoesNotThrow()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "catalog.json");
        var dates = Path.Combine(dir.Path, "catalog-dates.json");
        File.WriteAllText(path, $"[{{\"Entry\":{Entry("1")},{Seen}}}]");
        File.WriteAllText(dates, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);
        File.SetUnixFileMode(dates, UnixFileMode.None);
        try
        {
            var store = new CatalogStore(dir.Path);
            Assert.True(store.Unreadable);
            Assert.Equal(0, store.Count);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(dates, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void LibraryIsLeftAloneUntilAFullSyncReplacesAnUnreadableCatalog()
    {
        using var dir = new TempDir();
        var data = Path.Combine(dir.Path, "data");
        var movies = Path.Combine(dir.Path, "movies");
        var shows = Path.Combine(dir.Path, "shows");
        var options = new LibraryOptions { MoviesPath = movies, ShowsPath = shows };
        var writer = new LibraryWriter(NullLogger<LibraryWriter>.Instance);

        var good = new CatalogStore(data);
        good.Replace([new CatalogEntry(E.Movie("1", "Alien", 1979), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        new LibrarySyncService(good, new EpisodeCountStore(data), writer, NullLogger<LibrarySyncService>.Instance)
            .Sync(options, (id, _) => "http://x/" + id);
        var strm = Directory.GetFiles(movies, "*.strm", SearchOption.AllDirectories).Single();

        File.WriteAllText(Path.Combine(data, "catalog.json"), "{damaged");
        var damaged = new CatalogStore(data);
        var library = new LibrarySyncService(damaged, new EpisodeCountStore(data), writer, NullLogger<LibrarySyncService>.Instance);

        Assert.Throws<InvalidOperationException>(() => library.Sync(options, (id, _) => "http://x/" + id));
        Assert.True(File.Exists(strm));

        // A recent-only listing is not the whole catalog yet.
        damaged.Replace([new CatalogEntry(E.Movie("2", "Dune", 2021), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], complete: false);
        Assert.True(damaged.Unreadable);

        damaged.Replace([new CatalogEntry(E.Movie("1", "Alien", 1979), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        Assert.False(damaged.Unreadable);
        library.Sync(options, (id, _) => "http://x/" + id);
        Assert.True(File.Exists(strm));
    }

    [Fact]
    public void RecoveryOutlastsRestartsUntilAFullSync()
    {
        using var dir = new TempDir();
        var (data, options, writer, strm) = LibraryWithOneMovie(dir);
        File.WriteAllText(Path.Combine(data, "catalog.json"), "{damaged");
        Assert.True(new CatalogStore(data).Unreadable);

        // The damaged file was set aside, so this start finds no catalog at all.
        var second = new CatalogStore(data);
        Assert.True(second.Unreadable);
        Assert.Throws<InvalidOperationException>(() => Library(second, data, writer).Sync(options, (id, _) => "http://x/" + id));
        Assert.True(new CatalogStore(data).Unreadable);
        Assert.True(File.Exists(strm));

        second.Replace([Alien()]);
        Assert.False(second.Unreadable);
        Assert.False(new CatalogStore(data).Unreadable);
    }

    [Fact]
    public void ARecentOnlyRecoveryIsStillIncompleteAfterARestart()
    {
        using var dir = new TempDir();
        var (data, options, writer, strm) = LibraryWithOneMovie(dir);
        File.WriteAllText(Path.Combine(data, "catalog.json"), "{damaged");
        new CatalogStore(data).Replace([new CatalogEntry(E.Movie("2", "Dune", 2021), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], complete: false);

        var restarted = new CatalogStore(data);
        Assert.True(restarted.Unreadable);
        Assert.Equal(1, restarted.Count);
        Assert.Throws<InvalidOperationException>(() => Library(restarted, data, writer).Sync(options, (id, _) => "http://x/" + id));
        Assert.True(File.Exists(strm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALostCatalogNextToAnExistingLibraryWaitsForAFullSync(bool recentFirst)
    {
        using var dir = new TempDir();
        var (data, options, writer, strm) = LibraryWithOneMovie(dir);
        File.Delete(Path.Combine(data, "catalog.json"));
        File.Delete(Path.Combine(data, "catalog-dates.json"));

        var lost = new CatalogStore(data);
        Assert.False(lost.Unreadable);
        if (recentFirst)
        {
            lost.Replace([new CatalogEntry(E.Movie("2", "Dune", 2021), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], CatalogDates.Empty, complete: false);
        }

        var library = Library(lost, data, writer);
        Assert.Throws<InvalidOperationException>(() => library.Sync(options, (id, _) => "http://x/" + id));
        Assert.True(File.Exists(strm));
        Assert.True(new CatalogStore(data).Unreadable);

        lost.Replace([Alien()]);
        library.Sync(options, (id, _) => "http://x/" + id);
        Assert.True(File.Exists(strm));
    }

    [Fact]
    public void AMissingCatalogWithDatesOfAFullSyncWasLost()
    {
        using var dir = new TempDir();
        File.WriteAllText(
            Path.Combine(dir.Path, "catalog-dates.json"),
            "{\"BaselineAt\":\"2025-01-01T00:00:00+00:00\",\"BaselineMaxId\":5,\"RecentSyncAt\":null,\"RecentIds\":null}");
        Assert.True(new CatalogStore(dir.Path).Unreadable);
    }

    private static CatalogEntry Alien() => new(E.Movie("1", "Alien", 1979), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static LibrarySyncService Library(CatalogStore catalog, string data, LibraryWriter writer) =>
        new(catalog, new EpisodeCountStore(data), writer, NullLogger<LibrarySyncService>.Instance);

    private static (string Data, LibraryOptions Options, LibraryWriter Writer, string Strm) LibraryWithOneMovie(TempDir dir)
    {
        var data = Path.Combine(dir.Path, "data");
        var options = new LibraryOptions { MoviesPath = Path.Combine(dir.Path, "movies"), ShowsPath = Path.Combine(dir.Path, "shows") };
        var writer = new LibraryWriter(NullLogger<LibraryWriter>.Instance);
        var good = new CatalogStore(data);
        good.Replace([Alien()], CatalogDates.Empty with { BaselineAt = DateTimeOffset.UtcNow });
        Library(good, data, writer).Sync(options, (id, _) => "http://x/" + id);
        return (data, options, writer, Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories).Single());
    }

    [Fact]
    public void EpisodeCountsSkipInvalidRecords()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "episode-counts.json"), """
            {
              "a": null,
              "b": {"Count": -1, "Origin": "Archive"},
              "c": {"Count": 3, "Origin": "Archive", "Episodes": [1, 2, 4]},
              "d": {"Count": 2, "Origin": 7},
              "e": {"Count": 2000000000, "Origin": "Metadata"},
              "f": {"Count": 2}
            }
            """);

        var store = new EpisodeCountStore(dir.Path);

        Assert.Null(store.Get("a"));
        Assert.Null(store.Get("b"));
        Assert.Equal([1, 2, 4], store.Get("c")!.EpisodeNumbers());
        Assert.Null(store.Get("d"));
        Assert.Null(store.Get("e"));
        Assert.Equal(new EpisodeCount(2, EpisodeCountOrigin.Metadata), store.Get("f"));
        Assert.True(store.Set("a", new EpisodeCount(5, EpisodeCountOrigin.Metadata)));
        Assert.Equal(5, new EpisodeCountStore(dir.Path).Get("a")!.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    public void EpisodeCountsStartEmptyFromDamagedFiles(string content)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "episode-counts.json"), content);
        Assert.Null(new EpisodeCountStore(dir.Path).Get("a"));
    }

    [Fact]
    public void UnreadableEpisodeCountsDoNotThrow()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "episode-counts.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            Assert.Null(new EpisodeCountStore(dir.Path).Get("a"));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void APartlyDamagedCatalogKeepsLookupsButNotMirroring()
    {
        using var dir = new TempDir();
        var data = Path.Combine(dir.Path, "data");
        var options = new LibraryOptions { MoviesPath = Path.Combine(dir.Path, "movies"), ShowsPath = Path.Combine(dir.Path, "shows") };
        var writer = new LibraryWriter(NullLogger<LibraryWriter>.Instance);
        var good = new CatalogStore(data);
        good.Replace(
            [Alien(), new CatalogEntry(E.Movie("2", "Dune", 2021), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            CatalogDates.Empty with { BaselineAt = DateTimeOffset.UtcNow });
        Library(good, data, writer).Sync(options, (id, _) => "http://x/" + id);
        var strms = Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories);
        Assert.Equal(2, strms.Length);

        // One record of a complete catalog is damaged; the rest still loads.
        var path = Path.Combine(data, "catalog.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Title\":\"Dune\"", "\"Title\":null", StringComparison.Ordinal));
        var damaged = new CatalogStore(data);
        Assert.Equal(1, damaged.Count);
        Assert.NotNull(damaged.Get("1"));
        Assert.True(damaged.Unreadable);
        Assert.Throws<InvalidOperationException>(() => Library(damaged, data, writer).Sync(options, (id, _) => "http://x/" + id));
        Assert.All(strms, f => Assert.True(File.Exists(f)));

        // Still required after a restart, until a full listing replaces the catalog.
        var restarted = new CatalogStore(data);
        Assert.True(restarted.Unreadable);
        Assert.Throws<InvalidOperationException>(() => Library(restarted, data, writer).Sync(options, (id, _) => "http://x/" + id));
        Assert.All(strms, f => Assert.True(File.Exists(f)));

        restarted.Replace([Alien()]);
        Library(restarted, data, writer).Sync(options, (id, _) => "http://x/" + id);
        Assert.Single(Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories));
        Assert.False(new CatalogStore(data).Unreadable);
    }

    [Fact]
    public void AnEpisodeCountThatCouldNotBeSavedIsRetried()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "episode-counts.json");
        var store = new EpisodeCountStore(dir.Path);
        var count = new EpisodeCount(3, EpisodeCountOrigin.Archive, [1, 2, 3]);

        // A folder in the file's place makes every write fail.
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<IOException>(() => store.Set("a", count));
        Assert.Null(store.Get("a"));

        Directory.Delete(path);
        Assert.True(store.Set("a", count));
        Assert.Equal(count, new EpisodeCountStore(dir.Path).Get("a"));
        Assert.False(store.Set("a", count));
    }
}
