using System.IO.Compression;
using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

/// <summary>Failure handling of downloads and the cache. Runs no ffmpeg (conversions fail, which these tests don't reach or expect).</summary>
public sealed class MediaRobustnessTests : IDisposable
{
    private static readonly SourceAccount Alice = new("alice@example.com", "pw");

    private readonly TempDir _dir = new();
    private readonly FakeSource _source = new();
    private readonly CatalogStore _catalog;
    private readonly CacheStore _cache;
    private readonly DownloadQuota _quota;
    private long _maxCache;
    private bool _evictWhenLow = true;

    public MediaRobustnessTests()
    {
        _catalog = new CatalogStore(_dir.Path);
        _catalog.Replace(new[] { E.Movie("1", "A", 2020), E.Show("200", "S", 1, 2020), E.Show("201", "T", 1, 2020) }
            .Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        _cache = new CacheStore(() => Path.Combine(_dir.Path, "cache"), NullLogger<CacheStore>.Instance) { FreeSpace = _ => long.MaxValue };
        _quota = new DownloadQuota(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task OnlyACertainNotStartedFailureRefundsTheQuota()
    {
        using var jobs = CreateManager();

        _source.Open = _ => throw new DownloadNotStartedException("login page unreachable");
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.Equal(0, jobs.DownloadsUsed(Alice));

        _source.Open = _ => throw new HttpRequestException("connection reset after redirect");
        await Assert.ThrowsAsync<HttpRequestException>(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.Equal(1, jobs.DownloadsUsed(Alice));
    }

    [Fact]
    public async Task StalledDownloadFailsKeepsItsQuotaAndFreesTheAccount()
    {
        _source.Open = _ => new SourceMedia(new StallingStream(new byte[1000]), SourceMediaKind.Archive, "s.zip", null);
        using var jobs = CreateManager();
        jobs.DownloadInactivityTimeout = TimeSpan.FromMilliseconds(200);

        var error = await Assert.ThrowsAsync<SourceException>(() => jobs.PrepareAsync("200", 1, Alice, default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("stalled", error.Message);
        Assert.Equal(1, jobs.DownloadsUsed(Alice));

        await Assert.ThrowsAsync<SourceException>(() => jobs.PrepareAsync("201", 1, Alice, default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, _source.Opens);
        Assert.False(File.Exists(_cache.ArchivePath("200") + ".part"));
    }

    [Fact]
    public async Task LowDiskSpaceStopsADownloadBeforeItUsesQuota()
    {
        _cache.FreeSpace = _ => 100 * 1024 * 1024;
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        var error = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.Contains("Not enough free disk space", error.Message);
        Assert.Equal((0, 0), (_source.Opens, jobs.DownloadsUsed(Alice)));
    }

    [Theory]
    [InlineData("1", null)]
    [InlineData("200", 1)]
    public async Task MissingFfmpegFailsBeforeAnyDownloadOrQuota(string entryId, int? episode)
    {
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager(ffmpegLooksReady: false);

        var preflight = jobs.Preflight(entryId, episode, Alice);
        Assert.Equal(PreflightStatus.ConversionUnavailable, preflight.Status);
        Assert.Contains("ffmpeg was not found", preflight.Problem);
        Assert.Contains("ffmpeg was not found", jobs.ConversionProblem);

        var error = await Assert.ThrowsAsync<ConversionUnavailableException>(() => jobs.PrepareAsync(entryId, episode, Alice, default))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("nothing was downloaded", error.Message);
        Assert.Equal((0, 0), (_source.Opens, jobs.DownloadsUsed(Alice)));
        Assert.Equal(0, _quota.UsedByAll);
    }

    [Fact]
    public void AnFfmpegThatCantRunIsReported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var notExecutable = Path.Combine(_dir.Path, "ffmpeg");
        File.WriteAllText(notExecutable, "not a program");
        var converter = new FfmpegConverter(() => notExecutable, NullLogger<FfmpegConverter>.Instance);
        Assert.Contains("can't be started", converter.ReadinessProblem());

        var wrong = Path.Combine(_dir.Path, "fake-ffmpeg");
        File.WriteAllText(wrong, "#!/bin/sh\necho ' A....D mp2 MP2'\n");
        File.SetUnixFileMode(wrong, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Contains("lacks the libx264 and aac encoder", new FfmpegConverter(() => wrong, NullLogger<FfmpegConverter>.Instance).ReadinessProblem());
    }

    [FfmpegFact]
    public void RealFfmpegIsReady() =>
        Assert.Null(new FfmpegConverter(() => FfmpegTools.Ffmpeg, NullLogger<FfmpegConverter>.Instance).ReadinessProblem());

    [Fact]
    public async Task ADownloadedArchiveIsUnpackedInsteadOfDownloadedAgain()
    {
        Directory.CreateDirectory(_cache.EntryDir("200"));
        await File.WriteAllBytesAsync(_cache.ArchivePath("200"), Zip(("S/S01E01.mp3", 10), ("S/S01E02.mp3", 10), ("S/S01E05.mp3", 10)));
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        Assert.False(jobs.Preflight("200", 1, Alice).NeedsDownload);

        // The conversion itself fails here (no ffmpeg); the archive was recovered before that.
        await Assert.ThrowsAnyAsync<Exception>(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.Equal(0, _source.Opens);
        Assert.Equal([1, 2, 5], jobs.KnownEpisodes("200"));
        Assert.False(File.Exists(_cache.ArchivePath("200")));
        Assert.True(File.Exists(_cache.EpisodeMapPath("200")));
    }

    [Fact]
    public async Task EpisodePositionsResolveInASeasonPartNumberedFromSix()
    {
        Directory.CreateDirectory(_cache.EntryDir("200"));
        await File.WriteAllBytesAsync(_cache.ArchivePath("200"), Zip(("S01E06.mp3", 10), ("S01E07.mp3", 10)));
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        // Found as the archive's own episode 7 (the conversion then fails without ffmpeg), unlike a position past the end.
        var second = await Record.ExceptionAsync(() => jobs.PrepareAsync("200", 2, Alice, default));
        Assert.IsNotType<EntryNotFoundException>(second);
        Assert.Contains(jobs.RecentOutcomes(), o => o.Title.EndsWith("E07", StringComparison.Ordinal) && o.Kind == JobOutcomeKind.Failed);
        Assert.DoesNotContain(jobs.RecentOutcomes(), o => o.Title.EndsWith("E02", StringComparison.Ordinal));
        await Assert.ThrowsAsync<EntryNotFoundException>(() => jobs.PrepareAsync("200", 3, Alice, default));
        Assert.Equal([6, 7], jobs.KnownEpisodes("200"));
    }

    [Fact]
    public async Task AGapInTheNumberingIsNotFilledByTheNextEpisode()
    {
        Directory.CreateDirectory(_cache.EntryDir("200"));
        await File.WriteAllBytesAsync(_cache.ArchivePath("200"), Zip(("S01E01.mp3", 10), ("S01E03.mp3", 10), ("S01E05.mp3", 10)));
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        await Assert.ThrowsAsync<EntryNotFoundException>(() => jobs.PrepareAsync("200", 2, Alice, default));
        Assert.IsNotType<EntryNotFoundException>(await Record.ExceptionAsync(() => jobs.PrepareAsync("200", 3, Alice, default)));
        Assert.DoesNotContain(jobs.RecentOutcomes(), o => o.Title.EndsWith("E02", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new[] { 6, 7 }, 1, 6)]
    [InlineData(new[] { 6, 7 }, 2, 7)]
    [InlineData(new[] { 6, 7 }, 7, 7)]
    [InlineData(new[] { 6, 7 }, 3, null)]
    [InlineData(new[] { 1, 3, 5 }, 2, null)]
    [InlineData(new[] { 1, 3, 5 }, 5, 5)]
    [InlineData(new[] { 3, 4, 5, 6 }, 2, 4)]
    [InlineData(new int[0], 1, null)]
    public void EpisodeNumbersResolveToTheArchivesOwn(int[] numbers, int requested, int? expected) =>
        Assert.Equal(expected, MediaJobManager.CanonicalEpisode(numbers, requested));

    [Fact]
    public async Task AnUnpackedSeasonWithoutItsIndexIsReused()
    {
        Directory.CreateDirectory(_cache.ExtractDir("200"));
        await File.WriteAllBytesAsync(Path.Combine(_cache.ExtractDir("200"), "S01E03.mp3"), new byte[10]);
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        await Assert.ThrowsAnyAsync<Exception>(() => jobs.PrepareAsync("200", 3, Alice, default));
        Assert.Equal(0, _source.Opens);
        Assert.Equal([3], jobs.KnownEpisodes("200"));
        Assert.Null(jobs.KnownEpisodes("201"));
    }

    [Fact]
    public async Task RevokingAnAccountCancelsItsQueuedDownloads()
    {
        var first = new StallingStream(new byte[10]);
        _source.Open = id => id == "200"
            ? new SourceMedia(first, SourceMediaKind.Archive, "s.zip", null)
            : throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        var holding = jobs.PrepareAsync("200", 1, Alice, default);
        await WaitUntilAsync(() => _source.Opens == 1);
        var queued = jobs.PrepareAsync("201", 1, Alice, default);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(j => j.EntryId == "201"));

#pragma warning disable CS0618 // the login-wide revocation still works for hosts that use it
        jobs.RevokeAccount(Alice.Key);
#pragma warning restore CS0618

        var error = await Assert.ThrowsAsync<SourceUnavailableException>(() => queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("removed", error.Message);
        Assert.Equal(1, _source.Opens);
        Assert.Equal(1, jobs.DownloadsUsed(Alice));
        first.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => holding);
    }

    [Fact]
    public async Task AStaleAccountSnapshotStartsNothing()
    {
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();
        var generation = 1L;
        jobs.AccountIsCurrent = a => a.Generation == generation;

        // Revoked and replaced before this request (holding the old snapshot) arrived.
#pragma warning disable CS0618 // the login-wide revocation still works for hosts that use it
        jobs.RevokeAccount(Alice.Key);
#pragma warning restore CS0618
        var error = await Assert.ThrowsAsync<SourceUnavailableException>(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.Contains("removed or replaced", error.Message);
        Assert.Equal(PreflightStatus.NoAccount, jobs.Preflight("200", 1, Alice).Status);
        Assert.Equal((0, 0), (_source.Opens, jobs.DownloadsUsed(Alice)));
    }

    [Fact]
    public async Task ReplacingAnAccountFailsItsQueuedDownloadsWithoutUsingQuota()
    {
        var first = new StallingStream(new byte[10]);
        _source.Open = id => id == "200"
            ? new SourceMedia(first, SourceMediaKind.Archive, "s.zip", null)
            : throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();
        var current = Alice with { Generation = 1 };
        jobs.AccountIsCurrent = a => a == current;

        var holding = jobs.PrepareAsync("200", 1, current, default);
        await WaitUntilAsync(() => _source.Opens == 1);
        var queued = jobs.PrepareAsync("201", 1, current, default);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(j => j.EntryId == "201"));

        // Same login, new credentials; the host didn't revoke (or revoked too early to matter).
        current = Alice with { Credential = "new", Generation = 2 };
        first.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => holding);

        var error = await Assert.ThrowsAsync<SourceUnavailableException>(() => queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("removed or replaced", error.Message);
        Assert.Equal(1, _source.Opens);
        Assert.Equal(1, jobs.DownloadsUsed(Alice));
    }

    [Fact]
    public async Task ConcurrentRecoveryOfAStaleSeasonDownloadsItOnce()
    {
        // An index whose episode files are gone (converted and cleaned up, then the converted files evicted).
        Directory.CreateDirectory(_cache.EntryDir("200"));
        File.WriteAllText(_cache.EpisodeMapPath("200"), "{\"1\":\"extracted/S01E01.mp3\"}");
        var release = new TaskCompletionSource();
        _source.Open = _ =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return new SourceMedia(new MemoryStream(Zip(("S01E01.mp3", 10))), SourceMediaKind.Archive, "s.zip", null);
        };
        _source.Limits = new SourceLimits(25, 4);
        using var jobs = CreateManager();

        var bob = new SourceAccount("bob@example.com", "pw");
        var requests = new[] { Alice, bob, Alice, bob }.Select(a => Task.Run(() => jobs.PrepareAsync("200", 1, a, default))).ToList();
        await Task.Delay(300);
        release.SetResult();
        foreach (var request in requests)
        {
            // Conversions fail without ffmpeg; only the download count matters here.
            await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(1, _source.Opens);
        Assert.Equal([1], jobs.KnownEpisodes("200"));
    }

    [Fact]
    public async Task AMovieCachedJustAfterTheCacheCheckIsNotDownloadedAgain()
    {
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();
        // Another request's job commits the final file between this request's cache check and its job lookup.
        jobs.AfterCacheMiss = () =>
        {
            _cache.EnsureEntryDir("1");
            File.WriteAllText(_cache.FinalPath("1", null), "mp4");
        };

        var target = await jobs.PrepareAsync("1", null, Alice, default);

        Assert.Equal(_cache.FinalPath("1", null), Assert.IsType<ReadyTarget>(target).Path);
        Assert.Equal((0, 0), (_source.Opens, jobs.DownloadsUsed(Alice)));
    }

    [Fact]
    public void PlaybackStartingDuringEvictionKeepsItsFile()
    {
        _maxCache = 1;
        using var jobs = CreateManager();
        foreach (var id in new[] { "1", "200" })
        {
            _cache.EnsureEntryDir(id);
            File.WriteAllBytes(_cache.FinalPath(id, id == "1" ? null : 1), new byte[100]);
        }

        // Neither has a lease yet; playback of movie 1 begins after eviction listed it as a candidate.
        PlaybackTarget? started = null;
        _cache.BeforeEvictionCheck = name =>
        {
            if (name == "1" && started is null)
            {
                started = jobs.PrepareAsync("1", null, Alice, default).GetAwaiter().GetResult();
            }
        };
        File.SetLastWriteTimeUtc(_cache.EntryDir("1"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(_cache.EntryDir("200"), DateTime.UtcNow.AddHours(-1));

        jobs.EnforceCacheLimit(null);

        Assert.True(File.Exists(Assert.IsType<ReadyTarget>(started).Path));
        Assert.False(Directory.Exists(_cache.EntryDir("200")));
    }

    [Fact]
    public async Task ArchivesAreExtractedWithinLimits()
    {
        var zip = Path.Combine(_dir.Path, "a.zip");
        var dest = Path.Combine(_dir.Path, "out");
        File.WriteAllBytes(zip, Zip(("Show/S01E01.mp3", 100), ("Show/readme.txt", 5), ("Show/._S01E01.mp3", 5)));

        long asked = -1;
        await ArchiveExtractor.ExtractAudioAsync(zip, dest, 10, 1000, n => asked = n, default);
        Assert.Equal(100, asked);
        Assert.Equal(["S01E01.mp3"], Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Select(Path.GetFileName));

        Assert.Throws<SourceException>(() => ArchiveExtractor.ExtractAudioAsync(zip, dest, 2, 1000, _ => { }, default).GetAwaiter().GetResult());
        Assert.Throws<SourceException>(() => ArchiveExtractor.ExtractAudioAsync(zip, dest, 10, 99, _ => { }, default).GetAwaiter().GetResult());
        Assert.Throws<InsufficientDiskSpaceException>(() =>
            ArchiveExtractor.ExtractAudioAsync(zip, dest, 10, 1000, _ => throw new InsufficientDiskSpaceException("full"), default).GetAwaiter().GetResult());

        // A failed extraction leaves the previous complete one alone.
        Assert.True(File.Exists(Path.Combine(dest, "Show", "S01E01.mp3")));
        Assert.False(Directory.Exists(dest + ".tmp"));

        File.WriteAllBytes(zip, Zip(("../escape.mp3", 10)));
        Assert.Throws<SourceException>(() => ArchiveExtractor.ExtractAudioAsync(zip, dest, 10, 1000, _ => { }, default).GetAwaiter().GetResult());
        Assert.False(File.Exists(Path.Combine(_dir.Path, "escape.mp3")));
    }

    [Fact]
    public async Task FailedLiveWriterIsNotAFinishedStream()
    {
        var path = Path.Combine(_dir.Path, "live.mp4");
        await File.WriteAllBytesAsync(path, new byte[5000]);
        using var received = new MemoryStream();

        var writer = Task.FromException(new InvalidOperationException("ffmpeg exited with 1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveFileReader.CopyAsync(path, writer, received, default));
        Assert.Equal(5000, received.Length);

        using var complete = new MemoryStream();
        await LiveFileReader.CopyAsync(path, Task.CompletedTask, complete, default);
        Assert.Equal(5000, complete.Length);
    }

    [Fact]
    public async Task ConversionGateLetsForegroundWorkGoFirst()
    {
        var gate = new ConversionGate(() => 1);
        var first = await gate.EnterAsync(() => false, default);
        var background = gate.EnterAsync(() => false, default);
        var foreground = gate.EnterAsync(() => true, default);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(() => true, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        first.Dispose();
        first.Dispose();
        var next = await foreground.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(background.IsCompleted);
        Assert.Equal(1, gate.Running);

        next.Dispose();
        (await background.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, gate.Running);
    }

    [Fact]
    public void EvictionSkipsPlaceholdersAndBusyEntries()
    {
        foreach (var name in new[] { "old", "busy" })
        {
            File.WriteAllBytes(Path.Combine(_cache.EnsureEntryDir(name), "f"), new byte[100]);
        }

        Directory.CreateDirectory(Path.Combine(_cache.Root, CacheStore.PlaceholderDirName));
        File.WriteAllBytes(Path.Combine(_cache.Root, CacheStore.PlaceholderDirName, "f"), new byte[100]);

        _cache.EnforceLimit(1, new HashSet<string> { "busy" });

        Assert.False(Directory.Exists(Path.Combine(_cache.Root, "old")));
        Assert.True(Directory.Exists(Path.Combine(_cache.Root, "busy")));
        Assert.True(File.Exists(Path.Combine(_cache.Root, CacheStore.PlaceholderDirName, "f")));
    }

    [Fact]
    public async Task CachedUsageIsCountedInTheBackground()
    {
        Directory.CreateDirectory(_cache.EntryDir("1"));
        await File.WriteAllBytesAsync(Path.Combine(_cache.EntryDir("1"), "f"), new byte[1234]);

        _ = _cache.CachedTotalBytes;
        await WaitUntilAsync(() => _cache.CachedTotalBytes == 1234);
        Assert.Equal(1234, _cache.TotalBytes());
    }

    [Fact]
    public void StartupCleanupKeepsFilesOfRunningJobs()
    {
        Directory.CreateDirectory(_cache.EntryDir("1"));
        var stale = Path.Combine(_cache.EntryDir("1"), "main.live.mp4");
        var running = Path.Combine(_cache.EntryDir("1"), "e001.live.mp4");
        var keep = _cache.FinalPath("1", null);
        foreach (var f in new[] { stale, running, keep })
        {
            File.WriteAllText(f, "x");
        }

        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        _cache.CleanupStale(DateTime.UtcNow.AddMinutes(-1));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(running));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void PinnedCacheKeepsItsFolder()
    {
        var root = Path.Combine(_dir.Path, "one");
        var cache = new CacheStore(() => root, NullLogger<CacheStore>.Instance);
        var pinned = cache.Pinned();
        root = Path.Combine(_dir.Path, "two");

        Assert.StartsWith(Path.Combine(_dir.Path, "one"), pinned.LivePath("1", null));
        Assert.StartsWith(Path.Combine(_dir.Path, "two"), cache.LivePath("1", null));
    }

    [Fact]
    public void RealFreeSpaceCanBeMeasured() =>
        Assert.True(new CacheStore(() => _dir.Path, NullLogger<CacheStore>.Instance).FreeSpace(_dir.Path) > 0);

    [Fact]
    public async Task ACachedSourceInAForeignFolderIsNeitherConvertedNorDeleted()
    {
        var entry = Directory.CreateDirectory(_cache.EntryDir("1")).FullName;
        File.WriteAllText(_cache.SourcePath("1"), "someone's audio");
        File.WriteAllText(Path.Combine(entry, "notes.txt"), "mine");
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        Assert.True(jobs.Preflight("1", null, Alice).NeedsDownload);
        await Assert.ThrowsAsync<CacheFolderConflictException>(() => jobs.PrepareAsync("1", null, Alice, default));

        Assert.Equal(["notes.txt", "source.audio"], Directory.GetFileSystemEntries(entry).Select(Path.GetFileName).Order());
        Assert.Equal("someone's audio", File.ReadAllText(_cache.SourcePath("1")));
        Assert.Equal(0, _source.Opens);
    }

    [Fact]
    public async Task ASavedEpisodeListInAForeignFolderIsNotUsed()
    {
        var entry = Directory.CreateDirectory(_cache.ExtractDir("200")).Parent!.FullName;
        File.WriteAllText(Path.Combine(_cache.ExtractDir("200"), "S01E01.mp3"), "x");
        File.WriteAllText(_cache.EpisodeMapPath("200"), "{\"1\":\"extracted/S01E01.mp3\"}");
        File.WriteAllText(Path.Combine(entry, "notes.txt"), "mine");
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        Assert.Null(jobs.KnownEpisodes("200"));
        Assert.True(jobs.Preflight("200", 1, Alice).NeedsDownload);
        await Assert.ThrowsAsync<CacheFolderConflictException>(() => jobs.PrepareAsync("200", 1, Alice, default));

        Assert.True(File.Exists(Path.Combine(_cache.ExtractDir("200"), "S01E01.mp3")));
        Assert.True(File.Exists(_cache.EpisodeMapPath("200")));
        Assert.False(File.Exists(_cache.LivePath("200", 1)));
        Assert.Equal(0, _source.Opens);
    }

    [Fact]
    public async Task EpisodeFilesOutsideTheExtractFolderAreIgnored()
    {
        var outside = Path.Combine(_dir.Path, "victim.mp3");
        File.WriteAllText(outside, "keep");
        var linked = Path.Combine(_dir.Path, "linked.mp3");
        File.WriteAllText(linked, "keep too");
        _cache.EnsureEntryDir("200");
        Directory.CreateDirectory(_cache.ExtractDir("200"));
        File.WriteAllText(Path.Combine(_cache.ExtractDir("200"), "S01E02.mp3"), "x");
        File.CreateSymbolicLink(Path.Combine(_cache.ExtractDir("200"), "S01E03.mp3"), linked);
        File.WriteAllText(_cache.EpisodeMapPath("200"), "{\"1\":\"../../victim.mp3\",\"2\":\"extracted/S01E02.mp3\"}");
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        Assert.Null(jobs.KnownEpisodes("200"));

        // The bad index is replaced from the extract folder; the linked file isn't taken for an episode.
        await Record.ExceptionAsync(() => jobs.PrepareAsync("200", 2, Alice, default));
        Assert.Equal([2], jobs.KnownEpisodes("200"));
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal("keep too", File.ReadAllText(linked));
        Assert.Equal(0, _source.Opens);
    }

    [Fact]
    public async Task AnEntryFolderThatIsALinkIsRefused()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir.Path, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, CacheStore.EntryMarkerName), string.Empty);
        File.WriteAllText(Path.Combine(elsewhere, "source.audio"), "x");
        _cache.EnsureRoot();
        Directory.CreateSymbolicLink(_cache.EntryDir("1"), elsewhere);
        _maxCache = 1;
        using var jobs = CreateManager();

        Assert.False(_cache.OwnsEntry("1"));
        await Assert.ThrowsAsync<CacheFolderConflictException>(() => jobs.PrepareAsync("1", null, Alice, default));
        jobs.EnforceCacheLimit(null);
        Assert.True(File.Exists(Path.Combine(elsewhere, "source.audio")));
    }

    [Fact]
    public async Task ASavedEpisodeListRestoresLostEpisodeCounts()
    {
        _cache.EnsureEntryDir("200");
        Directory.CreateDirectory(_cache.ExtractDir("200"));
        File.WriteAllText(Path.Combine(_cache.ExtractDir("200"), "S01E06.mp3"), "x");
        File.WriteAllText(Path.Combine(_cache.ExtractDir("200"), "S01E09.mp3"), "x");
        File.WriteAllText(_cache.EpisodeMapPath("200"), "{\"6\":\"extracted/S01E06.mp3\",\"9\":\"extracted/S01E09.mp3\"}");
        File.WriteAllText(Path.Combine(_dir.Path, "episode-counts.json"), "{damaged");
        _source.Open = _ => throw new InvalidOperationException("must not download");
        var events = new RecordingEvents();
        var counts = new EpisodeCountStore(_dir.Path);
        counts.Set("200", new EpisodeCount(1, EpisodeCountOrigin.Metadata));
        using var jobs = CreateManager(events: events, counts: counts);

        // At startup, from the season already in the cache.
        await jobs.CleanupStaleAsync();
        Assert.Equal(new EpisodeCount(2, EpisodeCountOrigin.Archive, [6, 9]), counts.Get("200"));
        Assert.Equal([("200", 2)], events.Discovered);
        Assert.Equal(new EpisodeCount(2, EpisodeCountOrigin.Archive, [6, 9]), new EpisodeCountStore(_dir.Path).Get("200"));

        // And when the saved list is used, if the counts were lost while running.
        var fresh = new EpisodeCountStore(Path.Combine(_dir.Path, "other"));
        using var again = CreateManager(events: events, counts: fresh);
        await Record.ExceptionAsync(() => again.PrepareAsync("200", 6, Alice, default));
        Assert.Equal([6, 9], fresh.Get("200")!.Episodes);
        Assert.Equal(2, events.Discovered.Count);
        Assert.Equal(0, _source.Opens);
    }

    [Fact]
    public async Task ReplacingOneUsersAccountKeepsAnotherUsersQueuedDownload()
    {
        var first = new StallingStream(new byte[10]);
        _source.Open = id => id == "200"
            ? new SourceMedia(first, SourceMediaKind.Archive, "s.zip", null)
            : new SourceMedia(new MemoryStream(Zip(("S01E01.mp3", 10))), SourceMediaKind.Archive, "s.zip", null);
        using var jobs = CreateManager();
        var alice = Alice with { Owner = "alice", Generation = 1 };
        var bob = Alice with { Owner = "bob", Generation = 1 };
        var aliceNow = alice;
        jobs.AccountIsCurrent = a => a.Owner != "alice" || a == aliceNow;

        // Both saved the same login: one slot, one quota. Alice's download holds the slot; Bob's waits.
        var holding = jobs.PrepareAsync("200", 1, alice, default);
        await WaitUntilAsync(() => _source.Opens == 1);
        var bobs = jobs.PrepareAsync("201", 1, bob, default);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(j => j.EntryId == "201"));

        // Alice saves new credentials for the same login.
        aliceNow = alice with { Credential = "new", Generation = 2 };
        jobs.RevokeOwner("alice", keepGeneration: 2);
        await Task.Delay(100);
        Assert.False(bobs.IsCompleted);

        first.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => holding);
        var outcome = await Record.ExceptionAsync(() => bobs.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(outcome is SourceUnavailableException, outcome?.Message);
        Assert.Equal(2, _source.Opens);
        Assert.Equal([1], jobs.KnownEpisodes("201"));
    }

    [Fact]
    public async Task RevokingAnOwnerCancelsOnlyTheirQueuedDownloads()
    {
        var first = new StallingStream(new byte[10]);
        _source.Open = id => id == "200"
            ? new SourceMedia(first, SourceMediaKind.Archive, "s.zip", null)
            : throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();
        var alice = Alice with { Owner = "alice", Generation = 1 };

        var holding = jobs.PrepareAsync("200", 1, Alice with { Owner = "bob" }, default);
        await WaitUntilAsync(() => _source.Opens == 1);
        var queued = jobs.PrepareAsync("201", 1, alice, default);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(j => j.EntryId == "201"));

        jobs.RevokeOwner("alice");

        var error = await Assert.ThrowsAsync<SourceUnavailableException>(() => queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("removed", error.Message);
        Assert.False(holding.IsCompleted);
        first.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => holding);
        Assert.Equal(1, _source.Opens);
    }

    [Fact]
    public async Task LowSpaceEvictsIdleCachedTitlesInsteadOfRefusing()
    {
        // The volume has room for the minimum free space only once the idle title is gone.
        foreach (var id in new[] { "1", "201" })
        {
            _cache.EnsureEntryDir(id);
            File.WriteAllBytes(_cache.FinalPath(id, id == "1" ? null : 1), new byte[1000]);
        }

        var budget = MediaOptions.DefaultMinFreeBytes + 1500;
        _cache.FreeSpace = _ => budget - _cache.TotalBytes();
        _source.Open = _ => new SourceMedia(new MemoryStream(Zip(("S01E01.mp3", 10))), SourceMediaKind.Archive, "s.zip", null);
        using var jobs = CreateManager();
        jobs.PlaybackLease = TimeSpan.FromMilliseconds(1);

        // Both were played, but their leases have run out.
        Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("1", null, Alice, default));
        Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("201", 1, Alice, default));
        await Task.Delay(50);
        File.SetLastWriteTimeUtc(_cache.EntryDir("1"), DateTime.UtcNow.AddHours(-2));

        // Conversion fails without ffmpeg; the download and unpacking needed the space.
        await Record.ExceptionAsync(() => jobs.PrepareAsync("200", 1, Alice, default));

        Assert.Equal(1, _source.Opens);
        Assert.Equal([1], jobs.KnownEpisodes("200"));
        Assert.False(Directory.Exists(_cache.EntryDir("1")));
        Assert.True(File.Exists(_cache.FinalPath("201", 1)));
    }

    [Fact]
    public async Task LowSpaceRefusesWithoutEvictingWhenAutomaticEvictionIsOff()
    {
        _evictWhenLow = false;
        _cache.EnsureEntryDir("1");
        File.WriteAllBytes(_cache.FinalPath("1", null), new byte[1000]);
        var budget = MediaOptions.DefaultMinFreeBytes + 500;
        _cache.FreeSpace = _ => budget - _cache.TotalBytes();
        _source.Open = _ => new SourceMedia(new MemoryStream(Zip(("S01E01.mp3", 10))), SourceMediaKind.Archive, "s.zip", null);
        using var jobs = CreateManager();
        jobs.PlaybackLease = TimeSpan.FromMilliseconds(1);
        Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("1", null, Alice, default));
        await Task.Delay(50);

        await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() => jobs.PrepareAsync("200", 1, Alice, default));

        Assert.Equal(0, _source.Opens);
        Assert.True(File.Exists(_cache.FinalPath("1", null)));
    }

    [Fact]
    public async Task LowSpaceKeepsLeasedTitlesAndRefuses()
    {
        _cache.EnsureEntryDir("1");
        File.WriteAllBytes(_cache.FinalPath("1", null), new byte[1000]);
        _cache.FreeSpace = _ => MediaOptions.DefaultMinFreeBytes + 500 - _cache.TotalBytes();
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();
        jobs.TouchLease("1");

        await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.True(File.Exists(_cache.FinalPath("1", null)));
        Assert.Equal((0, 0), (_source.Opens, jobs.DownloadsUsed(Alice)));
    }

    [Fact]
    public async Task MaintenanceEnforcesTheLimitOnceLeasesExpire()
    {
        _maxCache = 1500;
        foreach (var id in new[] { "1", "201" })
        {
            _cache.EnsureEntryDir(id);
            File.WriteAllBytes(_cache.FinalPath(id, id == "1" ? null : 1), new byte[1000]);
        }

        using var jobs = CreateManager();
        jobs.PlaybackLease = TimeSpan.FromMilliseconds(200);
        jobs.TouchLease("1");
        jobs.TouchLease("201");
        File.SetLastWriteTimeUtc(_cache.EntryDir("1"), DateTime.UtcNow.AddHours(-2));

        await jobs.MaintainCacheAsync();
        Assert.True(Directory.Exists(_cache.EntryDir("1")));

        await Task.Delay(300);
        await jobs.MaintainCacheAsync();
        Assert.False(Directory.Exists(_cache.EntryDir("1")));
        Assert.True(Directory.Exists(_cache.EntryDir("201")));
    }

    [Fact]
    public async Task ASeasonIsConvertedInItsOwnCacheFolderWhenTheSettingChangesDuringItsDownload()
    {
        var root = Path.Combine(_dir.Path, "cache");
        var cache = new CacheStore(() => root, NullLogger<CacheStore>.Instance) { FreeSpace = _ => long.MaxValue };
        var download = new StallingStream(Zip(("S01E01.mp3", 10), ("S01E02.mp3", 10)));
        _source.Open = _ => new SourceMedia(download, SourceMediaKind.Archive, "s.zip", null);
        using var jobs = CreateManager(cache: cache);
        var play = jobs.PrepareAsync("200", 1, Alice, default);
        await WaitUntilAsync(() => _source.Opens == 1);

        // Mid-download, the cache moves to a folder that already has a link by the season's name.
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir.Path, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, CacheStore.EntryMarkerName), string.Empty);
        File.WriteAllText(Path.Combine(elsewhere, "e001.live.mp4"), "keep");
        var moved = Directory.CreateDirectory(Path.Combine(_dir.Path, "cache2")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(moved, "200"), elsewhere);
        root = moved;
        download.Finish();

        // Conversion fails without ffmpeg, after its output folder was prepared.
        await Record.ExceptionAsync(() => play.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntilAsync(() => jobs.ActiveJobs.Count == 0);
        await Task.Delay(200);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(elsewhere, "e001.live.mp4")));
        Assert.Equal(2, Directory.GetFileSystemEntries(elsewhere).Length);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "cache", "200", "extracted", "S01E01.mp3")));
        Assert.Equal(1, _source.Opens);
    }

    [Fact]
    public async Task PreparationFailingAfterAProbeWasAnsweredIsReportedOnce()
    {
        // Season 201 holds the account's only download slot; the probed titles queue behind it.
        var holding = new StallingStream(new byte[10]);
        _source.Open = id => id switch
        {
            "201" => new SourceMedia(holding, SourceMediaKind.Archive, "s.zip", null),
            "200" => throw new SourceException("gone from the site"),
            _ => new SourceMedia(new MemoryStream(new byte[10]), SourceMediaKind.SingleAudio, "a.mp3", 10),
        };
        _cache.EnsureRoot();
        File.WriteAllText(_cache.PlaceholderPath(), "x");
        var events = new RecordingEvents();
        using var jobs = CreateManager(events: events);
        var play = jobs.PrepareAsync("201", 1, Alice, default);
        await WaitUntilAsync(() => _source.Opens == 1);

        // Answered with placeholders while their preparation waits.
        Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("1", null, Alice, default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("200", 2, Alice, default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(events.Failed);

        // The movie's conversion fails (no ffmpeg), the season's download is refused.
        holding.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => play);
        await WaitUntilAsync(() => events.Failed.Count >= 2);
        await Task.Delay(300);

        Assert.Equal([("1", null), ("200", 2)], events.Failed.Where(f => f.Item1 != "201").Order());
    }

    [Fact]
    public async Task AJobFailureIsReportedOnceEvenWithACallerWaiting()
    {
        _source.Open = _ => new SourceMedia(new MemoryStream(new byte[10]), SourceMediaKind.SingleAudio, "a.mp3", 10);
        var events = new RecordingEvents();
        using var jobs = CreateManager(events: events);

        await Assert.ThrowsAnyAsync<Exception>(() => jobs.PrepareAsync("1", null, Alice, default));
        await WaitUntilAsync(() => events.Failed.Count == 1);
        await Task.Delay(200);

        Assert.Equal([("1", null)], events.Failed);
    }

    [Fact]
    public async Task ProbesDontWaitForASeasonThatIsDownloading()
    {
        var download = new StallingStream(new byte[10]);
        _source.Open = _ => new SourceMedia(download, SourceMediaKind.Archive, "s.zip", null);
        Directory.CreateDirectory(Path.Combine(_cache.Root, CacheStore.PlaceholderDirName));
        File.WriteAllText(_cache.PlaceholderPath(1800), "x");
        using var jobs = CreateManager();
        jobs.ExpectedDuration = (_, _) => TimeSpan.FromMinutes(30);
        var play = jobs.PrepareAsync("200", 1, Alice, default);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(p => p.Phase == JobPhase.Downloading));

        // Detail pages of the season (no account), and the player's own probe, while the transfer runs.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var probe in new[] { jobs.PrepareForProbeAsync("200", 2, null, default), jobs.PrepareForProbeAsync("200", 1, Alice, default) })
        {
            Assert.Equal(_cache.PlaceholderPath(1800), Assert.IsType<PlaceholderTarget>(await probe.WaitAsync(TimeSpan.FromSeconds(5))).Path);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"probes took {watch.Elapsed}");
        Assert.False(play.IsCompleted);
        Assert.Equal(1, _source.Opens);
        download.Fail();
        await Assert.ThrowsAnyAsync<Exception>(() => play);
    }

    [Fact]
    public async Task EpisodeCountsUnsavedAtDownloadAreSavedOnALaterUse()
    {
        var blocker = Path.Combine(_dir.Path, "blocked");
        File.WriteAllText(blocker, "a file where the data folder should be");
        var counts = new EpisodeCountStore(Path.Combine(blocker, "data"));
        var events = new RecordingEvents();
        _source.Open = _ => new SourceMedia(new MemoryStream(Zip(("S01E01.mp3", 10), ("S01E03.mp3", 10))), SourceMediaKind.Archive, "s.zip", null);
        using var jobs = CreateManager(events: events, counts: counts);

        // Downloaded, but the counts can't be saved (conversion fails without ffmpeg either way).
        await Record.ExceptionAsync(() => jobs.PrepareAsync("200", 1, Alice, default));
        Assert.Equal([1, 3], jobs.KnownEpisodes("200"));
        Assert.Null(counts.Get("200"));

        // Saving works again: the next use of the kept season records them.
        File.Delete(blocker);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (counts.Get("200") is null && DateTime.UtcNow < deadline)
        {
            await Record.ExceptionAsync(() => jobs.PrepareAsync("200", 1, Alice, default));
            await Task.Delay(50);
        }

        Assert.Equal(new EpisodeCount(2, EpisodeCountOrigin.Archive, [1, 3]), counts.Get("200"));
        Assert.Contains(("200", 2), events.Discovered);
        Assert.Equal(1, _source.Opens);
    }

    private MediaJobManager CreateManager(bool ffmpegLooksReady = true, IMediaEvents? events = null, EpisodeCountStore? counts = null, CacheStore? cache = null) => new(
        _source,
        _catalog,
        cache ?? _cache,
        new FfmpegConverter(() => Path.Combine(_dir.Path, "no-ffmpeg"), NullLogger<FfmpegConverter>.Instance)
        {
            // These tests stop before conversion; only the readiness tests see that ffmpeg is missing.
            ReadinessProbe = ffmpegLooksReady ? _ => null : FfmpegConverterProbe,
        },
        _quota,
        counts ?? new EpisodeCountStore(_dir.Path),
        events ?? new NoEvents(),
        () => new MediaOptions(25, _maxCache, new ConversionSettings(), EvictWhenLowOnSpace: _evictWhenLow),
        NullLogger<MediaJobManager>.Instance);

    private static string? FfmpegConverterProbe(string path) =>
        new FfmpegConverter(() => path, NullLogger<FfmpegConverter>.Instance).ReadinessProblem();

    private static byte[] Zip(params (string Name, int Size)[] files)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, size) in files)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(new byte[size]);
            }
        }

        return zip.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    private sealed class RecordingEvents : IMediaEvents
    {
        public List<(string, int)> Discovered { get; } = [];

        public List<(string, int?)> Failed { get; } = [];

        public void MediaReady(string entryId, int? episode)
        {
        }

        public void EpisodesDiscovered(string entryId, int count)
        {
            lock (Discovered)
            {
                Discovered.Add((entryId, count));
            }
        }

        public void MediaFailed(string entryId, int? episode, Exception error)
        {
            lock (Failed)
            {
                Failed.Add((entryId, episode));
            }
        }
    }

    private sealed class NoEvents : IMediaEvents
    {
        public void MediaReady(string entryId, int? episode)
        {
        }

        public void EpisodesDiscovered(string entryId, int count)
        {
        }

        public void MediaFailed(string entryId, int? episode, Exception error)
        {
        }
    }

    /// <summary>Delivers its data, then waits (honouring cancellation) until <see cref="Fail"/> or <see cref="Finish"/>.</summary>
    private sealed class StallingStream(byte[] data) : Stream
    {
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public void Fail() => _failed.TrySetException(new IOException("connection lost"));

        /// <summary>Ends the download normally.</summary>
        public void Finish() => _failed.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length)
            {
                await _failed.Task.WaitAsync(cancellationToken);
            }

            var n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
