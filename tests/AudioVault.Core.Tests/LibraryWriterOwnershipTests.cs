using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

public class LibraryWriterOwnershipTests
{
    private readonly LibraryWriter _writer = new(NullLogger<LibraryWriter>.Instance);

    [Fact]
    public void NeverDeletesFilesItDidNotWrite()
    {
        using var dir = new TempDir();
        var foreign = Path.Combine(dir.Path, "Home Videos", "clip.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "http://elsewhere/clip");

        var first = _writer.Sync(dir.Path, new Dictionary<string, string> { ["A/a.strm"] = "1" });
        var second = _writer.Sync(dir.Path, new Dictionary<string, string>());

        Assert.Equal(1, first.Foreign);
        Assert.Equal(1, second.Deleted);
        Assert.True(File.Exists(foreign));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "A")));
    }

    [Fact]
    public void InventoryWithNullPathsStillWorks()
    {
        using var dir = new TempDir();
        _writer.Sync(dir.Path, new Dictionary<string, string> { ["a.strm"] = "1" });
        File.WriteAllText(Path.Combine(dir.Path, LibraryWriter.InventoryFileName), "[null, \"a.strm\"]");

        var result = _writer.Sync(dir.Path, new Dictionary<string, string>());

        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public void NeverOverwritesForeignFiles()
    {
        using var dir = new TempDir();
        var foreign = Path.Combine(dir.Path, "a.strm");
        File.WriteAllText(foreign, "mine");

        _writer.Sync(dir.Path, new Dictionary<string, string> { ["a.strm"] = "theirs" });

        Assert.Equal("mine", File.ReadAllText(foreign));
    }

    [Fact]
    public void AdoptsLegacyFilesThatMatch()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "old.strm"), "http://x/AudioVault/Stream/1?sig=a\n");
        File.WriteAllText(Path.Combine(dir.Path, "user.strm"), "http://other/1\n");

        var result = _writer.Sync(dir.Path, new Dictionary<string, string>(), c => c.Contains("/AudioVault/Stream/"));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Foreign);
        Assert.True(File.Exists(Path.Combine(dir.Path, "user.strm")));
    }

    [Fact]
    public void FailedWritesAreCountedAndOthersProceed()
    {
        using var dir = new TempDir();
        // A directory where a file should go makes that one write fail.
        Directory.CreateDirectory(Path.Combine(dir.Path, "bad.strm"));

        var result = _writer.Sync(dir.Path, new Dictionary<string, string> { ["bad.strm"] = "1", ["good.strm"] = "2" });

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Written);
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir.Path, "good.strm")));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void RewritesLeaveNoTemporaryFiles()
    {
        using var dir = new TempDir();
        _writer.Sync(dir.Path, new Dictionary<string, string> { ["A/a.strm"] = "1" });
        var result = _writer.Sync(dir.Path, new Dictionary<string, string> { ["A/a.strm"] = "2" });

        Assert.Equal(1, result.Written);
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir.Path, "A", "a.strm")));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "A")));
    }

    [Fact]
    public void CaseOnlyRenameKeepsTheFileOnCaseInsensitiveFileSystems()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "probe"), string.Empty);
        var caseInsensitive = File.Exists(Path.Combine(dir.Path, "PROBE"));
        File.Delete(Path.Combine(dir.Path, "probe"));

        _writer.Sync(dir.Path, new Dictionary<string, string> { [Path.Combine("Alien", "Alien.strm")] = "1" });
        _writer.Sync(dir.Path, new Dictionary<string, string> { [Path.Combine("ALIEN", "ALIEN.strm")] = "1" });

        Assert.True(File.Exists(Path.Combine(dir.Path, "ALIEN", "ALIEN.strm")));
        Assert.Single(Directory.GetFiles(dir.Path, "*.strm", SearchOption.AllDirectories));
        Assert.Equal(caseInsensitive, File.Exists(Path.Combine(dir.Path, "Alien", "Alien.strm")));
    }

    [Theory]
    [InlineData("same", "same")]
    [InlineData("lib", "lib/shows")]
    [InlineData("lib/movies", "lib")]
    public void OverlappingFoldersAreRefused(string movies, string shows)
    {
        using var dir = new TempDir();
        var service = new LibrarySyncService(
            new CatalogStore(dir.Path),
            new EpisodeCountStore(dir.Path),
            _writer,
            NullLogger<LibrarySyncService>.Instance);
        var options = new LibraryOptions { MoviesPath = Path.Combine(dir.Path, movies), ShowsPath = Path.Combine(dir.Path, shows) };

        Assert.Throws<LibraryConfigurationException>(() => service.Sync(options, (id, ep) => id));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "lib")));
    }

    [Fact]
    public void RelativeFoldersAreRefused() =>
        Assert.Throws<LibraryConfigurationException>(() =>
            LibrarySyncService.ValidateFolders(new LibraryOptions { MoviesPath = "movies", ShowsPath = "/shows" }));

    [Fact]
    public void SiblingFoldersWithSharedPrefixAreAllowed() =>
        LibrarySyncService.ValidateFolders(new LibraryOptions { MoviesPath = "/media/av", ShowsPath = "/media/av-shows" });

    private static LibrarySyncService Service(TempDir dir, CatalogStore catalog) => new(
        catalog,
        new EpisodeCountStore(Path.Combine(dir.Path, "data")),
        new LibraryWriter(NullLogger<LibraryWriter>.Instance),
        NullLogger<LibrarySyncService>.Instance);

    private static string Link(string id, int? ep) => $"http://jf/AudioVault/Stream/{id}?sig=s" + (ep is { } n ? $"&ep={n}" : string.Empty);

    private static (string EntryId, int? Episode)? EntryOf(string link)
    {
        var at = link.IndexOf("/AudioVault/Stream/", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var parts = link[(at + 19)..].Split('?');
        var ep = parts.Length > 1 && parts[1].Split('&').FirstOrDefault(p => p.StartsWith("ep=", StringComparison.Ordinal)) is { } e
            ? int.Parse(e[3..], System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;
        return (parts[0], ep);
    }

    private static int StrmCount(string root) => Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Length;

    private static LibraryOptions Options(TempDir dir) =>
        new() { MoviesPath = Path.Combine(dir.Path, "m"), ShowsPath = Path.Combine(dir.Path, "s") };

    private static void Relabel(CatalogStore catalog, IEnumerable<AudioVault.Core.Sources.SourceEntry> entries) =>
        catalog.Replace(entries.Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

    private static CatalogStore Catalog(TempDir dir, IEnumerable<AudioVault.Core.Sources.SourceEntry> entries)
    {
        var catalog = new CatalogStore(Path.Combine(dir.Path, "data"));
        catalog.Replace(entries.Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        return catalog;
    }

    [Fact]
    public void TurningOffOtherLanguagesRemovesThemDespiteTheSafetyRail()
    {
        // Review round 6, finding 7: of 100 movies, 60 are described in other languages. Turning those off removes
        // their files; that is the admin's choice, not a lost catalogue.
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100)
            .Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20), language: i <= 60 ? "French" : "English"))
            .ToList();
        var service = Service(dir, Catalog(dir, entries));
        var options = new LibraryOptions { MoviesPath = Path.Combine(dir.Path, "m"), ShowsPath = Path.Combine(dir.Path, "s") };
        Assert.Equal(100, service.Sync(options, Link, EntryOf).Movies.Written);

        var result = service.Sync(options with { IncludeOtherLanguages = false }, Link, EntryOf);

        Assert.Equal(60, result.Movies.Deleted);
        Assert.False(result.Movies.DeletionsSkipped);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(40, Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void ALostCatalogueStillTripsTheSafetyRail()
    {
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100).Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20))).ToList();
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = new LibraryOptions { MoviesPath = Path.Combine(dir.Path, "m"), ShowsPath = Path.Combine(dir.Path, "s") };
        service.Sync(options, Link, EntryOf);

        // A partial read: 60 titles gone from the catalogue at once.
        catalog.Replace(entries.Skip(60).Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var result = service.Sync(options, Link, EntryOf);

        Assert.True(result.Movies.DeletionsSkipped);
        Assert.Equal(60, result.Skipped);
        Assert.Equal(0, result.Movies.Deleted);
        Assert.Equal(100, Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void ASettingsChangeDuringACatalogueLossRemovesOnlyWhatItExplains()
    {
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100)
            .Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20), tts: i > 90))
            .ToList();
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = new LibraryOptions { MoviesPath = Path.Combine(dir.Path, "m"), ShowsPath = Path.Combine(dir.Path, "s") };
        service.Sync(options, Link, EntryOf);

        // Titles 1 to 60 vanish from the catalogue; text-to-speech (91 to 100) is turned off.
        catalog.Replace(entries.Skip(60).Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var result = service.Sync(options with { IncludeTts = false }, Link, EntryOf);

        Assert.Equal(10, result.Movies.Deleted);
        Assert.Equal(60, result.Skipped);
        Assert.Equal(90, Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void WithoutAnExplanationTheRailAppliesAsBefore()
    {
        using var dir = new TempDir();
        var all = Enumerable.Range(1, 100).ToDictionary(i => $"{i}.strm", i => Link($"{i}", null));
        _writer.Sync(dir.Path, all);

        var result = _writer.Sync(dir.Path, all.Take(40).ToDictionary(kv => kv.Key, kv => kv.Value));

        Assert.True(result.DeletionsSkipped);
        Assert.Equal(60, result.Skipped);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void ARelabelledCatalogueWithUnchangedSettingsTripsTheSafetyRail()
    {
        // Review round 7, finding 6: an English-only library; a parseable but wrong catalogue now calls every title
        // French. The titles are still in the catalogue, but the settings didn't change, so the rail holds.
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100).Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20))).ToList();
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = Options(dir) with { IncludeOtherLanguages = false };
        service.Sync(options, Link, EntryOf);

        Relabel(catalog, entries.Select(e => e with { Version = e.Version with { Language = "French" } }));
        var result = service.Sync(options, Link, EntryOf);

        Assert.True(result.Movies.DeletionsSkipped);
        Assert.Equal(100, result.Skipped);
        Assert.Equal(0, result.Movies.Deleted);
        Assert.Equal(100, StrmCount(options.MoviesPath));
    }

    [Fact]
    public void ARelabelledCatalogueDoesNotExplainASettingsChangeItDidNotCause()
    {
        // Text-to-speech is turned off while the catalogue wrongly marks 80 human-narrated titles as TTS: only the 10
        // known to be TTS before go; the other 80 count against the rail.
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100)
            .Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20), tts: i > 90))
            .ToList();
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);

        Relabel(catalog, entries.Select(e => int.Parse(e.Id, System.Globalization.CultureInfo.InvariantCulture) <= 80 ? e with { Version = e.Version with { IsTts = true } } : e));
        var result = service.Sync(options with { IncludeTts = false }, Link, EntryOf);

        Assert.Equal(10, result.Movies.Deleted);
        Assert.Equal(80, result.Skipped);
        Assert.Equal(90, StrmCount(options.MoviesPath));
    }

    [Fact]
    public void TurningOffTtsRemovesTheTitlesKnownToBeTts()
    {
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100)
            .Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20), tts: i > 30))
            .ToList();
        var service = Service(dir, Catalog(dir, entries));
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);

        var result = service.Sync(options with { IncludeTts = false }, Link, EntryOf);

        Assert.Equal(70, result.Movies.Deleted);
        Assert.True(result.Complete);
        Assert.Equal(30, StrmCount(options.MoviesPath));
    }

    [Fact]
    public void WithoutARecordOfTheAppliedSettingsNothingIsExempt()
    {
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100)
            .Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20), tts: i > 30))
            .ToList();
        var service = Service(dir, Catalog(dir, entries));
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);
        File.Delete(Path.Combine(options.MoviesPath, LibrarySyncService.AppliedFileName));

        var result = service.Sync(options with { IncludeTts = false }, Link, EntryOf);

        Assert.Equal(70, result.Skipped);
        Assert.Equal(100, StrmCount(options.MoviesPath));
    }

    [Fact]
    public void ASettingsChangeHeldBackByTheRailIsAppliedOnceTheRailClears()
    {
        // Review round 7, finding 10: the record of the applied settings stays until a sync completes, so a retry
        // still recognises the removals the settings change explains.
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100)
            .Select(i => E.Movie($"{i}", $"Movie {i}", 2000 + (i % 20), tts: i > 90))
            .ToList();
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);

        Relabel(catalog, entries.Skip(60));
        var noTts = options with { IncludeTts = false };
        var first = service.Sync(noTts, Link, EntryOf);
        Assert.False(first.Complete);
        Assert.Equal(10, first.Movies.Deleted);

        Relabel(catalog, entries);
        var retry = service.Sync(noTts, Link, EntryOf);
        Assert.True(retry.Complete);
        Assert.Equal(90, StrmCount(options.MoviesPath));
    }

    [Fact]
    public void ARenamedTitleKeepsItsOldFileUntilTheNewOneIsWritten()
    {
        // Review round 7, finding 7: the new file can't be created, so the old one must stay.
        using var dir = new TempDir();
        var entries = new[] { E.Movie("1", "Alien", 1979) };
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);
        var old = Path.Combine(options.MoviesPath, "Alien (1979)", "Alien (1979) - English.strm");
        Assert.True(File.Exists(old));

        // A directory where the new file should go makes its write fail.
        Directory.CreateDirectory(Path.Combine(options.MoviesPath, "Aliens (1979)", "Aliens (1979) - English.strm"));
        Relabel(catalog, [E.Movie("1", "Aliens", 1979)]);
        var result = service.Sync(options, Link, EntryOf);

        Assert.True(File.Exists(old));
        Assert.Equal(0, result.Movies.Deleted);
        Assert.Equal(1, result.Deferred);
        Assert.True(result.Failed > 0);
        Assert.False(result.Complete);

        Directory.Delete(Path.Combine(options.MoviesPath, "Aliens (1979)", "Aliens (1979) - English.strm"));
        var retry = service.Sync(options, Link, EntryOf);
        Assert.True(retry.Complete);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(options.MoviesPath, "Aliens (1979)", "Aliens (1979) - English.strm")));
    }

    [Fact]
    public void AFileInTheWayOfARenameKeepsTheOldOne()
    {
        using var dir = new TempDir();
        var catalog = Catalog(dir, [E.Movie("1", "Alien", 1979)]);
        var service = Service(dir, catalog);
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);
        var blocker = Path.Combine(options.MoviesPath, "Aliens (1979)", "Aliens (1979) - English.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "http://elsewhere/1\n");

        Relabel(catalog, [E.Movie("1", "Aliens", 1979)]);
        var result = service.Sync(options, Link, EntryOf);

        Assert.True(File.Exists(Path.Combine(options.MoviesPath, "Alien (1979)", "Alien (1979) - English.strm")));
        Assert.Equal("http://elsewhere/1\n", File.ReadAllText(blocker));
        Assert.Equal(1, result.Deferred);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void AMassRenameRemovesOnlyTheOldFilesWhoseReplacementsWereWritten()
    {
        using var dir = new TempDir();
        var entries = Enumerable.Range(1, 100).Select(i => E.Movie($"{i}", $"Movie {i}", 2000)).ToList();
        var catalog = Catalog(dir, entries);
        var service = Service(dir, catalog);
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);

        // Every title gets a new name; the new files of titles 1 to 60 can't be written.
        foreach (var i in Enumerable.Range(1, 60))
        {
            Directory.CreateDirectory(Path.Combine(options.MoviesPath, $"Renamed {i} (2000)", $"Renamed {i} (2000) - English.strm"));
        }

        Relabel(catalog, entries.Select(e => e with { Title = "Renamed " + e.Id }));
        var result = service.Sync(options, Link, EntryOf);

        Assert.Equal(40, result.Movies.Written);
        Assert.Equal(40, result.Movies.Deleted);
        Assert.Equal(60, result.Deferred);
        Assert.Equal(100, StrmCount(options.MoviesPath));
        foreach (var i in Enumerable.Range(1, 60))
        {
            Assert.True(File.Exists(Path.Combine(options.MoviesPath, $"Movie {i} (2000)", $"Movie {i} (2000) - English.strm")));
        }
    }

    [Fact]
    public void AnEpisodeKeepsItsOldFileUntilItsReplacementIsWritten()
    {
        using var dir = new TempDir();
        var catalog = Catalog(dir, [E.Show("1", "Show", 1, 2000)]);
        var service = Service(dir, catalog);
        var options = Options(dir);
        service.Sync(options, Link, EntryOf);
        var old = Path.Combine(options.ShowsPath, "Show (2000)", "Season 01", "Show (2000) S01E01.strm");
        Assert.True(File.Exists(old));

        Directory.CreateDirectory(Path.Combine(options.ShowsPath, "New Show (2000)", "Season 01", "New Show (2000) S01E01.strm"));
        Relabel(catalog, [E.Show("1", "New Show", 1, 2000)]);
        var result = service.Sync(options, Link, EntryOf);

        Assert.True(File.Exists(old));
        Assert.Equal(1, result.Shows.Deferred);
    }
}
