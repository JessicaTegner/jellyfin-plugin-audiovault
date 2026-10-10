using AudioVault.Core.Library;

namespace AudioVault.Core.Tests;

public class LibraryLayoutTests
{
    private static readonly LibraryOptions Options = new() { MoviesPath = "/m", ShowsPath = "/s" };

    private static IReadOnlyDictionary<string, EpisodeCount> NoCounts => new Dictionary<string, EpisodeCount>();

    [Fact]
    public void VersionsOfOneMovieShareAFolder()
    {
        var layout = LibraryLayout.Build(
            [
                E.Movie("1", "Alien", 1979, tags: "UK"),
                E.Movie("2", "Alien", 1979, tags: "US"),
                E.Movie("3", "Alien", 1979, tts: true),
                E.Movie("4", "alien", 1979, language: "French"),
            ],
            Options with { IncludeOtherLanguages = true },
            NoCounts.GetValueOrDefault);

        var paths = layout.Files.ToDictionary(f => f.EntryId, f => f.RelativePath);
        Assert.Equal(Path.Combine("Alien (1979)", "Alien (1979) - UK.strm"), paths["1"]);
        Assert.Equal(Path.Combine("Alien (1979)", "Alien (1979) - US.strm"), paths["2"]);
        Assert.Equal(Path.Combine("Alien (1979)", "Alien (1979) - TTS.strm"), paths["3"]);
        Assert.Equal(Path.Combine("Alien (1979)", "Alien (1979) - French.strm"), paths["4"]);
    }

    [Fact]
    public void FiltersLanguagesAndTts()
    {
        var layout = LibraryLayout.Build(
            [E.Movie("1", "Alien", 1979), E.Movie("2", "Alien", 1979, language: "French"), E.Movie("3", "Alien", 1979, tts: true)],
            Options with { IncludeTts = false, IncludeOtherLanguages = false },
            NoCounts.GetValueOrDefault);

        Assert.Equal(["1"], layout.Files.Select(f => f.EntryId));
        Assert.Equal(Path.Combine("Alien (1979)", "Alien (1979) - English.strm"), layout.Files[0].RelativePath);
    }

    [Fact]
    public void OtherLanguagesAreIncludedByDefaultAfterThePreferredOne()
    {
        var layout = LibraryLayout.Build(
            [E.Movie("2", "Alien", 1979, language: "French"), E.Movie("1", "Alien", 1979), E.Movie("4", "Lagaan", 2001, language: "Hindi")],
            new LibraryOptions { MoviesPath = "/m", ShowsPath = "/s" },
            NoCounts.GetValueOrDefault);

        Assert.Equal(["1", "2", "4"], layout.Files.Select(f => f.EntryId).Order());
        var alien = layout.Files.Where(f => f.RelativePath.StartsWith("Alien", StringComparison.Ordinal)).Select(f => f.EntryId);
        Assert.Equal(["1", "2"], alien);
    }

    [Fact]
    public void DifferentYearsAreDifferentMovies()
    {
        var layout = LibraryLayout.Build([E.Movie("1", "Dune", 1984), E.Movie("2", "Dune", 2021)], Options, NoCounts.GetValueOrDefault);
        Assert.Equal(2, layout.Files.Select(f => Path.GetDirectoryName(f.RelativePath)).Distinct().Count());
    }

    [Fact]
    public void DuplicateLabelsGetDisambiguated()
    {
        var layout = LibraryLayout.Build([E.Movie("1", "Alien", 1979), E.Movie("2", "Alien", 1979)], Options, NoCounts.GetValueOrDefault);
        Assert.Equal(2, layout.Files.Select(f => f.RelativePath).Distinct().Count());
    }

    [Fact]
    public void UnknownSeasonsGetBootstrapEpisodeAndArePending()
    {
        var layout = LibraryLayout.Build(
            [E.Show("10", "Breaking Bad", 1, 2008), E.Show("11", "Breaking Bad", 2, 2009)],
            Options,
            NoCounts.GetValueOrDefault);

        Assert.Equal(
            [
                Path.Combine("Breaking Bad (2008)", "Season 01", "Breaking Bad (2008) S01E01.strm"),
                Path.Combine("Breaking Bad (2008)", "Season 02", "Breaking Bad (2008) S02E01.strm"),
            ],
            layout.Files.Select(f => f.RelativePath));
        Assert.Equal(["10", "11"], layout.PendingSeasons.Select(p => p.EntryId));
    }

    [Fact]
    public void KnownSeasonsListEveryEpisode()
    {
        var counts = new Dictionary<string, EpisodeCount> { ["10"] = new(3, EpisodeCountOrigin.Metadata) };
        var layout = LibraryLayout.Build([E.Show("10", "Breaking Bad", 1, 2008)], Options, counts.GetValueOrDefault);

        Assert.Equal([1, 2, 3], layout.Files.Select(f => f.Episode!.Value));
        Assert.Empty(layout.PendingSeasons);
    }

    [Fact]
    public void SeriesYearComesFromSeasonOneOnly()
    {
        var layout = LibraryLayout.Build([E.Show("11", "Breaking Bad", 2, 2009)], Options, NoCounts.GetValueOrDefault);
        Assert.StartsWith("Breaking Bad" + Path.DirectorySeparatorChar, layout.Files[0].RelativePath);
    }

    [Fact]
    public void LaterSeasonPartsContinueNumberingOnceEarlierPartsAreKnown()
    {
        var entries = new[] { E.Show("1", "Money Heist", 5, 2021, part: 1), E.Show("2", "Money Heist", 5, 2021, part: 2) };

        var unknown = LibraryLayout.Build(entries, Options, NoCounts.GetValueOrDefault);
        Assert.Equal(["1"], unknown.Files.Select(f => f.EntryId));

        var counts = new Dictionary<string, EpisodeCount> { ["1"] = new(5, EpisodeCountOrigin.Archive) };
        var known = LibraryLayout.Build(entries, Options, counts.GetValueOrDefault);
        var part2 = known.Files.Where(f => f.EntryId == "2").ToList();
        Assert.Single(part2);
        Assert.EndsWith("S05E06.strm", part2[0].RelativePath);
        Assert.Equal(1, part2[0].Episode);
    }

    [Fact]
    public void PrefersHumanNarrationForShows()
    {
        var layout = LibraryLayout.Build(
            [E.Show("1", "Show", 1, 2020, tts: true), E.Show("2", "Show", 1, 2020)],
            Options,
            NoCounts.GetValueOrDefault);
        Assert.Equal("2", Assert.Single(layout.Files).EntryId);
    }

    private static Dictionary<string, string> SeriesFolders(LayoutResult layout) =>
        layout.Files.GroupBy(f => f.EntryId).ToDictionary(g => g.Key, g => g.First().RelativePath.Split(Path.DirectorySeparatorChar)[0]);

    [Fact]
    public void SameTitledProductionsBecomeSeparateSeries()
    {
        var layout = LibraryLayout.Build(
            [
                E.Show("1", "The Twilight Zone", 1, 1959),
                E.Show("2", "The Twilight Zone", 2, 1960),
                E.Show("3", "The Twilight Zone", 5, 1963),
                E.Show("4", "The Twilight Zone", 1, 2019),
                E.Show("5", "The Twilight Zone", 2, 2020),
            ],
            Options,
            NoCounts.GetValueOrDefault);

        var folders = SeriesFolders(layout);
        Assert.Equal(5, folders.Count);
        Assert.All(["1", "2", "3"], id => Assert.Equal("The Twilight Zone (1959)", folders[id]));
        Assert.All(["4", "5"], id => Assert.Equal("The Twilight Zone (2019)", folders[id]));
    }

    [Fact]
    public void RemakesWithFewSeasonsAreSeparated()
    {
        var layout = LibraryLayout.Build(
            [
                E.Show("1", "Avatar: The Last Airbender", 1, 2005),
                E.Show("2", "Avatar: The Last Airbender", 2, 2006),
                E.Show("3", "Avatar: The Last Airbender", 3, 2007),
                E.Show("4", "Avatar: The Last Airbender", 1, 2024),
                E.Show("5", "Utopia", 1, 2013),
                E.Show("6", "Utopia", 2, 2014),
                E.Show("7", "Utopia", 1, 2020),
            ],
            Options,
            NoCounts.GetValueOrDefault);

        var folders = SeriesFolders(layout);
        Assert.All(["1", "2", "3"], id => Assert.Equal("Avatar The Last Airbender (2005)", folders[id]));
        Assert.Equal("Avatar The Last Airbender (2024)", folders["4"]);
        Assert.All(["5", "6"], id => Assert.Equal("Utopia (2013)", folders[id]));
        Assert.Equal("Utopia (2020)", folders["7"]);
    }

    [Fact]
    public void LaterPartsReleasedYearsLaterStayWithTheirSeason()
    {
        // Real catalog entries: part 2 of season 1 came out two years after part 1.
        var entries = new[]
        {
            E.Show("17022", "Pinocchio and Friends", 1, 2021, part: 1),
            E.Show("20509", "Pinocchio and Friends", 1, 2023, part: 2),
            E.Show("22218", "Pinocchio and Friends", 2, 2024),
            E.Show("10015", "Ghostwriter", 1, 2019, part: 1),
            E.Show("10016", "Ghostwriter", 1, 2020, part: 2),
            E.Show("11832", "Ghostwriter", 2, 2020, part: 1),
            E.Show("17630", "Ghostwriter", 3, 2022),
            E.Show("22380", "Pokémon Horizons: The Series", 1, 2023, part: 1),
            E.Show("23553", "Pokémon Horizons: The Series", 1, 2024, part: 3),
            E.Show("23642", "Pokémon Horizons: The Series", 1, 2024, part: 4),
        };
        var counts = new Dictionary<string, EpisodeCount>
        {
            ["17022"] = new(13, EpisodeCountOrigin.Archive),
            ["20509"] = new(13, EpisodeCountOrigin.Archive),
            ["10015"] = new(5, EpisodeCountOrigin.Archive),
            ["22380"] = new(12, EpisodeCountOrigin.Archive),
            ["23553"] = new(12, EpisodeCountOrigin.Archive),
        };

        foreach (var lookup in new Func<string, EpisodeCount?>[] { NoCounts.GetValueOrDefault, counts.GetValueOrDefault })
        {
            var folders = SeriesFolders(LibraryLayout.Build(entries, Options, lookup));
            Assert.Equal(
                ["Ghostwriter (2019)", "Pinocchio and Friends (2021)", "Pokémon Horizons The Series (2023)"],
                folders.Values.Distinct().Order(StringComparer.Ordinal));
        }

        var known = SeriesFolders(LibraryLayout.Build(entries, Options, counts.GetValueOrDefault));
        Assert.Equal("Pinocchio and Friends (2021)", known["20509"]);
        Assert.Equal("Pokémon Horizons The Series (2023)", known["23642"]);
    }

    [Fact]
    public void LaterPartFollowsItsSeasonRatherThanAnotherProduction()
    {
        // Vikings season 6 part 2 (2020) fits a 2017 premiere as well as 2013; it belongs with season 6 part 1.
        var layout = LibraryLayout.Build(
            [
                E.Show("1", "Vikings", 1, 2013),
                E.Show("2", "Vikings", 5, 2017),
                E.Show("3", "Vikings", 6, 2019, part: 1),
                E.Show("4", "Vikings", 6, 2020, part: 2),
                E.Show("5", "Vikings", 1, 2017),
            ],
            Options,
            new Dictionary<string, EpisodeCount> { ["3"] = new(10, EpisodeCountOrigin.Archive) }.GetValueOrDefault);

        var folders = SeriesFolders(layout);
        Assert.All(["1", "2", "3", "4"], id => Assert.Equal("Vikings (2013)", folders[id]));
        Assert.Equal("Vikings (2017)", folders["5"]);
    }

    [Fact]
    public void LaterPartDecadesAfterAnEarlierPartStartsItsOwnProduction()
    {
        var layout = LibraryLayout.Build(
            [
                E.Show("1", "The Twilight Zone", 1, 1959, part: 1),
                E.Show("2", "The Twilight Zone", 2, 1960),
                E.Show("3", "The Twilight Zone", 1, 2019, part: 2),
                E.Show("4", "The Twilight Zone", 2, 2020),
            ],
            Options,
            NoCounts.GetValueOrDefault);

        var folders = SeriesFolders(layout);
        Assert.All(["1", "2"], id => Assert.Equal("The Twilight Zone (1959)", folders[id]));
        Assert.All(["3", "4"], id => Assert.Equal("The Twilight Zone (2019)", folders[id]));
    }

    [Fact]
    public void VersionsOfOneSeasonWithSlightlyDifferentYearsStayOneSeries()
    {
        var layout = LibraryLayout.Build(
            [E.Show("1", "Dark", 1, 2017), E.Show("2", "Dark", 1, 2018, tts: true), E.Show("3", "Dark", 3, 2020)],
            Options,
            NoCounts.GetValueOrDefault);

        Assert.Equal(["Dark (2017)"], SeriesFolders(layout).Values.Distinct());
    }

    [Fact]
    public void ArchiveEpisodeNumbersAreKept()
    {
        var counts = new Dictionary<string, EpisodeCount> { ["10"] = new(3, EpisodeCountOrigin.Archive, [1, 2, 10]) };
        var layout = LibraryLayout.Build([E.Show("10", "Show", 1, 2008)], Options, counts.GetValueOrDefault);

        Assert.Equal([1, 2, 10], layout.Files.Select(f => f.Episode!.Value));
        Assert.EndsWith("S01E10.strm", layout.Files[2].RelativePath);
    }

    [Fact]
    public void SparseMetadataNumbersAreKept()
    {
        var counts = new Dictionary<string, EpisodeCount> { ["10"] = new(3, EpisodeCountOrigin.Metadata, [1, 2, 5]) };
        var layout = LibraryLayout.Build([E.Show("10", "Show", 1, 2008)], Options, counts.GetValueOrDefault);

        Assert.Equal([1, 2, 5], layout.Files.Select(f => f.Episode!.Value));
        Assert.EndsWith("S01E05.strm", layout.Files[2].RelativePath);
        Assert.Empty(layout.PendingSeasons);
    }

    [Fact]
    public void MetadataListOfAPartDoesNotContinueIntoLaterParts()
    {
        // Lists are only looked up for single-part seasons; one learned before a second part appeared stays on part 1.
        var counts = new Dictionary<string, EpisodeCount> { ["1"] = new(3, EpisodeCountOrigin.Metadata, [1, 2, 3]) };
        var layout = LibraryLayout.Build(
            [E.Show("1", "Money Heist", 5, 2021, part: 1), E.Show("2", "Money Heist", 5, 2021, part: 2), E.Show("3", "Other", 1, 2020)],
            Options,
            counts.GetValueOrDefault);

        Assert.Equal([1, 2, 3], layout.Files.Where(f => f.EntryId == "1").Select(f => f.Episode!.Value));
        Assert.DoesNotContain(layout.Files, f => f.EntryId == "2");
        Assert.Equal(["3"], layout.PendingSeasons.Select(p => p.EntryId));
        Assert.Equal(1, layout.ListableSeasons);
    }

    [Fact]
    public void LaterPartNumberedSeasonWideKeepsItsNumbers()
    {
        var counts = new Dictionary<string, EpisodeCount>
        {
            ["1"] = new(5, EpisodeCountOrigin.Archive, [1, 2, 3, 4, 5]),
            ["2"] = new(5, EpisodeCountOrigin.Archive, [6, 7, 8, 9, 10]),
        };
        var layout = LibraryLayout.Build(
            [E.Show("1", "Money Heist", 5, 2021, part: 1), E.Show("2", "Money Heist", 5, 2021, part: 2)],
            Options,
            counts.GetValueOrDefault);

        var part2 = layout.Files.Where(f => f.EntryId == "2").ToList();
        Assert.Equal([6, 7, 8, 9, 10], part2.Select(f => f.Episode!.Value));
        Assert.EndsWith("S05E06.strm", part2[0].RelativePath);
        Assert.Equal(10, layout.Files.Select(f => f.RelativePath).Distinct().Count());
    }

    [Fact]
    public void LaterPartNumberedFromOneContinuesTheSeason()
    {
        var counts = new Dictionary<string, EpisodeCount>
        {
            ["1"] = new(5, EpisodeCountOrigin.Archive, [1, 2, 3, 4, 5]),
            ["2"] = new(2, EpisodeCountOrigin.Archive, [1, 2]),
        };
        var layout = LibraryLayout.Build(
            [E.Show("1", "Money Heist", 5, 2021, part: 1), E.Show("2", "Money Heist", 5, 2021, part: 2)],
            Options,
            counts.GetValueOrDefault);

        var part2 = layout.Files.Where(f => f.EntryId == "2").ToList();
        Assert.Equal([1, 2], part2.Select(f => f.Episode!.Value));
        Assert.EndsWith("S05E06.strm", part2[0].RelativePath);
        Assert.EndsWith("S05E07.strm", part2[1].RelativePath);
    }

    [Fact]
    public void EpisodeCountsRoundTripWithAndWithoutNumbers()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "episode-counts.json"), """{"old":{"Count":4,"Origin":"Archive"}}""");
        var store = new EpisodeCountStore(dir.Path);
        Assert.Equal([1, 2, 3, 4], store.Get("old")!.EpisodeNumbers());

        Assert.True(store.Set("new", new EpisodeCount(2, EpisodeCountOrigin.Archive, [3, 7])));
        Assert.False(store.Set("new", new EpisodeCount(2, EpisodeCountOrigin.Archive, [3, 7])));
        Assert.Equal([3, 7], new EpisodeCountStore(dir.Path).Get("new")!.EpisodeNumbers());
    }

    [Theory]
    [InlineData("AC/DC: Live", "AC DC Live")]
    [InlineData("What?", "What")]
    [InlineData("...", "Untitled")]
    public void SanitizesNames(string input, string expected) => Assert.Equal(expected, LibraryLayout.SafeName(input));

    [Fact]
    public void LabelsThatSanitizeAlikeStayDistinct()
    {
        var layout = LibraryLayout.Build(
            [E.Movie("1", "Example", 2020, tags: "UK/US"), E.Movie("2", "Example", 2020, tags: "UK:US"), E.Movie("3", "Example", 2020, tags: "UK US")],
            Options,
            NoCounts.GetValueOrDefault);

        var paths = layout.Files.Select(f => f.RelativePath).ToList();
        Assert.Equal(3, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(Path.Combine("Example (2020)", "Example (2020) - UK US.strm"), paths);
        Assert.All(paths, p => Assert.StartsWith(Path.Combine("Example (2020)", "Example (2020) - UK US"), p));
    }

    [Fact]
    public void SeparateTitlesLandingOnOneFolderNameStayDistinct()
    {
        // A year in the title itself makes a separate title with the same folder name.
        var layout = LibraryLayout.Build(
            [E.Movie("7", "Big (2020)", null), E.Movie("5", "Big", 2020), E.Show("9", "Big (2020)", 1, null), E.Show("8", "Big", 1, 2020)],
            Options,
            NoCounts.GetValueOrDefault);

        Assert.Equal(4, layout.Files.Select(f => (f.Root, f.RelativePath.ToUpperInvariant())).Distinct().Count());
        Assert.Equal(Path.Combine("Big (2020)", "Big (2020) - English.strm"), layout.Files.Single(f => f.EntryId == "5").RelativePath);
        Assert.Equal(Path.Combine("Big (2020)", "Big (2020) - English [7].strm"), layout.Files.Single(f => f.EntryId == "7").RelativePath);
        Assert.StartsWith(Path.Combine("Big (2020)", "Season 01"), layout.Files.Single(f => f.EntryId == "8").RelativePath);
        Assert.StartsWith(Path.Combine("Big (2020) [9]", "Season 01"), layout.Files.Single(f => f.EntryId == "9").RelativePath);
    }

    [Fact]
    public void SyncingLabelsThatSanitizeAlikeWritesBoth()
    {
        using var dir = new TempDir();
        var catalog = new AudioVault.Core.Catalog.CatalogStore(Path.Combine(dir.Path, "data"));
        catalog.Replace(new[] { E.Movie("1", "Example", 2020, tags: "UK/US"), E.Movie("2", "Example", 2020, tags: "UK:US") }
            .Select(e => new AudioVault.Core.Catalog.CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var service = new LibrarySyncService(
            catalog,
            new EpisodeCountStore(Path.Combine(dir.Path, "data")),
            new LibraryWriter(Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryWriter>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LibrarySyncService>.Instance);

        var result = service.Sync(new LibraryOptions { MoviesPath = Path.Combine(dir.Path, "m"), ShowsPath = Path.Combine(dir.Path, "s") }, (id, _) => "http://x/" + id);

        Assert.Equal(2, result.Movies.Written);
        Assert.NotEqual(service.FindStrmPath("1", null), service.FindStrmPath("2", null));
    }
}
