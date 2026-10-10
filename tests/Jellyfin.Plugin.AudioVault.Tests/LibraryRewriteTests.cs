using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AudioVault.Tests;

/// <summary>Review round 6, finding 8: a failed rewrite for new version settings is retried and reported.</summary>
public class LibraryRewriteTests
{
    private static readonly VersionSettings English = new("ENGLISH", true, true);
    private static readonly VersionSettings EnglishOnly = new("ENGLISH", false, true);
    private static readonly VersionSettings NoTts = new("ENGLISH", false, false);

    [Fact]
    public void UnchangedSettingsNeedNoRewrite()
    {
        var rewrite = new SettingsRewrite<VersionSettings>(English);

        Assert.Null(rewrite.Request(English));
        Assert.False(rewrite.Pending);
    }

    [Fact]
    public void AFailedRewriteIsRetriedAndReportedUntilItWorks()
    {
        // A temporary disk or permission failure while turning off other languages.
        var rewrite = new SettingsRewrite<VersionSettings>(English);
        var generation = rewrite.Request(EnglishOnly)!.Value;
        Assert.True(rewrite.Pending);

        Assert.Equal(TimeSpan.FromMinutes(1), rewrite.Failed(generation, "3 library files couldn't be updated."));
        Assert.StartsWith("3 library files couldn't be updated. Retrying in 1 minute;", rewrite.Problem);
        Assert.Equal(TimeSpan.FromMinutes(2), rewrite.Failed(generation, "Still failing."));
        Assert.Equal(English, rewrite.Applied);
        Assert.True(rewrite.Pending);

        rewrite.Succeeded(generation, EnglishOnly);
        Assert.Null(rewrite.Problem);
        Assert.Equal(EnglishOnly, rewrite.Applied);
        Assert.False(rewrite.Pending);
    }

    [Fact]
    public void SavingTheSameSettingsAgainRetriesAtOnce()
    {
        var rewrite = new SettingsRewrite<VersionSettings>(English);
        var first = rewrite.Request(EnglishOnly)!.Value;

        // While the first attempt runs, saving again changes nothing.
        Assert.Null(rewrite.Request(EnglishOnly));

        rewrite.Failed(first, "Disk full.");
        var retry = rewrite.Request(EnglishOnly);
        Assert.NotNull(retry);
        Assert.False(rewrite.IsCurrent(first));

        // The old attempt's retry schedule is now stale.
        Assert.Null(rewrite.Failed(first, "Late failure."));
        Assert.Equal(TimeSpan.FromMinutes(1), rewrite.Failed(retry.Value, "Disk full."));
    }

    [Fact]
    public void ANewerChangeReplacesAPendingOne()
    {
        var rewrite = new SettingsRewrite<VersionSettings>(English);
        var first = rewrite.Request(EnglishOnly)!.Value;
        var second = rewrite.Request(NoTts)!.Value;

        rewrite.Succeeded(first, EnglishOnly);
        Assert.Equal(English, rewrite.Applied);

        rewrite.Succeeded(second, NoTts);
        Assert.Equal(NoTts, rewrite.Applied);
        Assert.Null(rewrite.Request(NoTts));
    }

    [Fact]
    public void ChangingBackAfterAFailureStillRewrites()
    {
        var rewrite = new SettingsRewrite<VersionSettings>(English);
        var generation = rewrite.Request(EnglishOnly)!.Value;
        rewrite.Failed(generation, "Disk full.");

        // The files may be half-way between the two; write them for the settings in force again.
        Assert.NotNull(rewrite.Request(English));
    }

    [Fact]
    public void SkippedDeletionsAndFailuresAreReported()
    {
        var clean = new LibrarySyncResult(new WriteResult(3, 2, false), new WriteResult(0, 0, false), []);
        Assert.Null(LibraryRules.Problem(clean));

        var skipped = new LibrarySyncResult(new WriteResult(0, 0, true, Skipped: 1200), new WriteResult(0, 0, false, Failed: 2), []);
        var problem = LibraryRules.Problem(skipped)!;
        Assert.Contains("1,200 library files weren't removed", problem);
        Assert.Contains("2 library files couldn't be written or deleted", problem);
    }

    [Fact]
    public void RemovalsHeldBackByTheRailKeepTheRewritePendingUntilARetryCompletes()
    {
        // Review round 7, finding 10: text-to-speech is turned off while 60 other titles are missing from a partial
        // catalogue. The rail keeps those 60, so the rewrite isn't done: it stays pending and is retried.
        var dir = Directory.CreateTempSubdirectory("avrewrite").FullName;
        try
        {
            var entries = Enumerable.Range(1, 100)
                .Select(i => new SourceEntry($"{i}", MediaKind.Movie, $"Movie {i}", 2000 + (i % 20), null, null, new VersionInfo("English", i > 90, []), $"Movie {i}"))
                .ToList();
            var catalog = new CatalogStore(Path.Combine(dir, "data"));
            void Load(IEnumerable<SourceEntry> e) => catalog.Replace(e.Select(x => new CatalogEntry(x, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
            Load(entries);
            var library = new LibrarySyncService(
                catalog,
                new EpisodeCountStore(Path.Combine(dir, "data")),
                new LibraryWriter(NullLogger<LibraryWriter>.Instance),
                NullLogger<LibrarySyncService>.Instance);
            var options = new LibraryOptions { MoviesPath = Path.Combine(dir, "m"), ShowsPath = Path.Combine(dir, "s") };
            static string Link(string id, int? ep) => $"http://jf/AudioVault/Stream/{id}?sig=s";
            static (string, int?)? EntryOf(string link) =>
                Api.PlaybackIntentFilter.TryParse(link, out var id, out var ep) ? (id, ep) : null;
            library.Sync(options, Link, EntryOf);

            var rewrite = new SettingsRewrite<VersionSettings>(English);
            var noTts = new VersionSettings("ENGLISH", true, false);
            var generation = rewrite.Request(noTts)!.Value;
            Load(entries.Skip(60));
            var first = library.Sync(options with { IncludeTts = false }, Link, EntryOf);

            Assert.Equal(0, first.Failed);
            Assert.Equal(60, first.Skipped);
            var problem = LibraryRules.Unfinished(first);
            Assert.NotNull(problem);
            Assert.Equal(TimeSpan.FromMinutes(1), rewrite.Failed(generation, problem));
            Assert.True(rewrite.Pending);
            Assert.Equal(English, rewrite.Applied);

            // Saving the same settings again retries at once; once the catalogue is whole the rewrite completes.
            var retry = rewrite.Request(noTts)!.Value;
            Load(entries);
            var second = library.Sync(options with { IncludeTts = false }, Link, EntryOf);
            Assert.Null(LibraryRules.Unfinished(second));
            rewrite.Succeeded(retry, noTts);
            Assert.False(rewrite.Pending);
            Assert.Equal(90, Directory.GetFiles(options.MoviesPath, "*.strm", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnUpstreamMassRemovalAloneStartsNoRewrite()
    {
        // The rail holds files back, but the settings didn't change: nothing is scheduled, so nothing loops.
        var rewrite = new SettingsRewrite<VersionSettings>(English);
        Assert.Null(rewrite.Request(English));

        var held = new LibrarySyncResult(new WriteResult(0, 0, true, Skipped: 80), new WriteResult(0, 0, false), []);
        Assert.Contains("80 library files weren't removed", LibraryRules.Unfinished(held));
        Assert.Null(LibraryRules.Unfinished(new LibrarySyncResult(new WriteResult(5, 5, false), new WriteResult(0, 0, false), [])));
    }

    [Fact]
    public void ARewriteHeldBackByTheRailBacksOff()
    {
        var rewrite = new SettingsRewrite<VersionSettings>(English);
        var generation = rewrite.Request(NoTts)!.Value;
        var problem = LibraryRules.Unfinished(new LibrarySyncResult(new WriteResult(0, 10, true, Skipped: 80), new WriteResult(0, 0, false), []))!;

        Assert.Equal(
            new[] { 1, 2, 4, 8, 16, 30, 30 }.Select(m => TimeSpan.FromMinutes(m)),
            Enumerable.Range(0, 7).Select(_ => rewrite.Failed(generation, problem)!.Value));
        Assert.True(rewrite.Pending);
    }
}
