using System.Diagnostics;
using System.IO.Compression;
using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

/// <summary>Runs the real ffmpeg on generated audio; see <see cref="FfmpegTools"/> for when that is skipped.</summary>
public sealed class MediaJobManagerTests : IDisposable
{
    private static readonly SourceAccount Alice = new("Alice@Example.com ", "pw");
    private static readonly SourceAccount Bob = new("bob@example.com", "pw");

    private readonly TempDir _dir = new();
    private readonly FakeSource _source = new();
    private readonly Events _events = new();
    private readonly CatalogStore _catalog;
    private int _limit = 25;
    private long _maxCache;
    private string _cacheRoot;

    public MediaJobManagerTests()
    {
        _catalog = new CatalogStore(_dir.Path);
        _cacheRoot = Path.Combine(_dir.Path, "cache");
    }

    public void Dispose() => _dir.Dispose();

    [FfmpegFact]
    public async Task MovieStreamsLiveThenServesFromCache()
    {
        var mp3 = await MakeMp3Async("movie.mp3", 6);
        Seed(E.Movie("100", "Test Movie", 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "movie.mp3", mp3.Length);
        using var jobs = CreateManager();

        var target = await jobs.PrepareAsync("100", null, Alice, default);
        using var received = new MemoryStream();
        if (target is LiveTarget live)
        {
            await LiveFileReader.CopyAsync(live.Path, live.Completion, received, default);
            await live.Completion;
        }

        var again = Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("100", null, Alice, default));
        var probe = await ProbeAsync(again.Path);
        Assert.Contains("h264", probe);
        Assert.Contains("aac", probe);
        Assert.InRange(Duration(probe), 5.8, 6.2);
        Assert.Equal(1, _source.Opens);
        Assert.Contains(("100", (int?)null), _events.Ready);
        Assert.Empty(jobs.ActiveJobProgress());
        var outcome = Assert.Single(jobs.RecentOutcomes());
        Assert.Equal((JobOutcomeKind.MovieReady, "Test Movie"), (outcome.Kind, outcome.Title));
        if (target is LiveTarget)
        {
            Assert.True(received.Length > 10_000);
        }
    }

    [FfmpegFact]
    public async Task SeasonArchiveIsDownloadedOnceAndAllEpisodesConverted()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var ep in new[] { 1, 2, 3 })
            {
                var bytes = await MakeMp3Async($"ep{ep}.mp3", 2);
                await using var entry = await archive.CreateEntry($"Show/Show S01E{ep:00}.mp3").OpenAsync();
                await entry.WriteAsync(bytes);
            }

            archive.CreateEntry("Show/readme.txt");
        }

        var zipBytes = zip.ToArray();
        Seed(E.Show("200", "Test Show", 1, 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(zipBytes), SourceMediaKind.Archive, "show.zip", zipBytes.Length);
        using var jobs = CreateManager();

        var target = await jobs.PrepareAsync("200", 2, Alice, default);
        if (target is LiveTarget live)
        {
            await live.Completion;
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (_events.Ready.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        foreach (var ep in new[] { 1, 2, 3 })
        {
            Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("200", ep, Alice, default));
        }

        Assert.Equal(1, _source.Opens);
        Assert.Equal(("200", 3), Assert.Single(_events.Discovered));
        var outcome = Assert.Single(jobs.RecentOutcomes());
        Assert.Equal((JobOutcomeKind.SeasonDownloaded, "Test Show season 1"), (outcome.Kind, outcome.Title));
        await Assert.ThrowsAsync<EntryNotFoundException>(() => jobs.PrepareAsync("200", 4, Alice, default));
    }

    [FfmpegFact]
    public async Task SeasonEpisodeStreamsLiveWhileConvertingAndProbesGetItsLength()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (ep, seconds) in new[] { (1, 2), (2, 600) })
            {
                var bytes = await MakeMp3Async($"ep{ep}.mp3", seconds);
                await using var entry = await archive.CreateEntry($"Show S01E{ep:00}.mp3").OpenAsync();
                await entry.WriteAsync(bytes);
            }
        }

        var zipBytes = zip.ToArray();
        Seed(E.Show("200", "Test Show", 1, 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(zipBytes), SourceMediaKind.Archive, "show.zip", zipBytes.Length);
        using var jobs = CreateManager();

        var live = Assert.IsType<LiveTarget>(await jobs.PrepareAsync("200", 2, Alice, default));
        Assert.False(live.Completion.IsCompleted);
        var probe = Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("200", 2, null, default));
        Assert.InRange(Duration(await ProbeAsync(probe.Path)), 599, 602);

        using var received = new MemoryStream();
        await LiveFileReader.CopyAsync(live.Path, live.Completion, received, default);
        Assert.True(received.Length > 100_000);

        var cached = Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("200", 2, Alice, default));
        Assert.InRange(Duration(await ProbeAsync(cached.Path)), 599, 602);
        Assert.Equal(1, _source.Opens);
    }

    [FfmpegFact]
    public async Task AnOldEpisodeLinkPlaysAndCachesTheArchivesOwnEpisodeOnce()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var ep in new[] { 6, 7 })
            {
                var bytes = await MakeMp3Async($"p{ep}.mp3", 2);
                await using var entry = await archive.CreateEntry($"Show S01E{ep:00}.mp3").OpenAsync();
                await entry.WriteAsync(bytes);
            }
        }

        var zipBytes = zip.ToArray();
        Seed(E.Show("200", "Test Show", 1, 2020, part: 2));
        _source.Open = _ => new SourceMedia(new MemoryStream(zipBytes), SourceMediaKind.Archive, "show.zip", zipBytes.Length);
        using var jobs = CreateManager();

        // Linked as episode 1 before the download showed this part numbers its episodes 6 and 7.
        if (await jobs.PrepareAsync("200", 1, Alice, default) is LiveTarget live)
        {
            await live.Completion;
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (_events.Ready.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        await Task.Delay(500);
        var cache = new CacheStore(() => _cacheRoot, NullLogger<CacheStore>.Instance);
        Assert.Equal([("200", (int?)6), ("200", 7)], _events.Ready.Order());
        Assert.Equal(cache.FinalPath("200", 6), Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("200", 1, Alice, default)).Path);
        Assert.Equal(cache.FinalPath("200", 7), Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("200", 2, Alice, default)).Path);
        Assert.Equal(cache.FinalPath("200", 7), Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("200", 7, Alice, default)).Path);
        Assert.False(File.Exists(cache.FinalPath("200", 1)));
        Assert.InRange(Duration(await ProbeAsync(cache.FinalPath("200", 6))), 1.8, 2.2);
        Assert.Equal(1, _source.Opens);
        Assert.DoesNotContain(jobs.RecentOutcomes(), o => o.Kind == JobOutcomeKind.Failed);
    }

    [FfmpegFact]
    public async Task ADownloadRefusedWhenItWouldStartGetsAPlaceholder()
    {
        // Review round 7, finding 5: permission was checked again only when a new download would actually start.
        Seed(E.Movie("1", "A", 2020), E.Show("2", "S", 1, 2020));
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();
        var asked = 0;

        Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("1", null, Alice, default, () => ++asked < 0));
        Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("2", 3, Alice, default, () => ++asked < 0));
        Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("1", null, Alice, default, () => ++asked < 0));
        Assert.Equal(3, asked);
        Assert.Equal(0, _source.Opens);
    }

    [FfmpegFact]
    public async Task ProbesGetAPlaceholderInsteadOfADownload()
    {
        Seed(E.Movie("1", "A", 2020), E.Show("2", "S", 1, 2020));
        _source.Open = _ => throw new InvalidOperationException("must not download");
        using var jobs = CreateManager();

        var movie = Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("1", null, null, default));
        Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("2", 3, null, default));

        var probe = await ProbeAsync(movie.Path);
        Assert.Contains("h264", probe);
        Assert.Contains("aac", probe);
        Assert.Equal(0, _source.Opens);
    }

    [FfmpegFact]
    public async Task ProbeJoinsARunningOrCachedJobWithoutPlaceholder()
    {
        var mp3 = await MakeMp3Async("m.mp3", 3);
        Seed(E.Movie("1", "A", 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "a.mp3", mp3.Length);
        using var jobs = CreateManager();

        var played = await jobs.PrepareAsync("1", null, Alice, default);
        var probed = await jobs.PrepareAsync("1", null, null, default);

        Assert.IsNotType<PlaceholderTarget>(probed);
        if (played is LiveTarget live)
        {
            await live.Completion;
        }

        Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("1", null, null, default));
        Assert.Equal(1, _source.Opens);
    }

    [Fact]
    public void PreflightReportsWhyPlaybackCannotStart()
    {
        Seed(E.Movie("1", "A", 2020), E.Show("2", "S", 1, 2020));
        using var jobs = CreateManager();

        Assert.Equal(PreflightStatus.NotFound, jobs.Preflight("404", null, Alice).Status);

        var ok = jobs.Preflight("2", 1, Alice);
        Assert.Equal((PreflightStatus.Ok, true, true), (ok.Status, ok.NeedsDownload, ok.IsArchive));

        _source.Readiness = new SourceHealth(false, "no login");
        var notReady = jobs.Preflight("1", null, Alice);
        Assert.Equal((PreflightStatus.SourceNotReady, "no login"), (notReady.Status, notReady.Problem));

        _source.Readiness = new SourceHealth(true, "ok");
        _limit = 1;
        new DownloadQuota(_dir.Path).TryAcquire("x", Alice.Key, 1);
        var exhausted = CreateManager().Preflight("1", null, Alice);
        Assert.Equal(PreflightStatus.QuotaExhausted, exhausted.Status);
        Assert.NotNull(exhausted.NextDownloadAt);
    }

    [FfmpegFact]
    public async Task PreflightPassesForCachedTitlesEvenWithoutSource()
    {
        var mp3 = await MakeMp3Async("c.mp3", 1);
        Seed(E.Movie("1", "A", 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "a.mp3", mp3.Length);
        using var jobs = CreateManager();
        if (await jobs.PrepareAsync("1", null, Alice, default) is LiveTarget live)
        {
            await live.Completion;
        }

        _source.Readiness = new SourceHealth(false, "no login");
        var result = jobs.Preflight("1", null, Alice);
        Assert.Equal((PreflightStatus.Ok, false), (result.Status, result.NeedsDownload));
    }

    [Fact]
    public void PreflightWithoutAccountNeedsOneOnlyForDownloads()
    {
        Seed(E.Movie("1", "A", 2020));
        using var jobs = CreateManager();

        Assert.Equal(PreflightStatus.NoAccount, jobs.Preflight("1", null, null).Status);
        Assert.Equal(PreflightStatus.NotFound, jobs.Preflight("404", null, null).Status);
    }

    [FfmpegFact]
    public async Task EachAccountHasItsOwnDailyLimit()
    {
        var mp3 = await MakeMp3Async("q2.mp3", 1);
        Seed(E.Movie("1", "A", 2020), E.Movie("2", "B", 2020), E.Movie("3", "C", 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "a.mp3", mp3.Length);
        _limit = 1;
        using var jobs = CreateManager();

        if (await jobs.PrepareAsync("1", null, Alice, default) is LiveTarget first)
        {
            await first.Completion;
        }

        Assert.Equal(PreflightStatus.QuotaExhausted, jobs.Preflight("2", null, Alice).Status);
        Assert.Equal(PreflightStatus.Ok, jobs.Preflight("2", null, Bob).Status);
        if (await jobs.PrepareAsync("2", null, Bob, default) is LiveTarget second)
        {
            await second.Completion;
        }

        Assert.Equal(["alice@example.com", "bob@example.com"], _source.OpenedWith);
        Assert.Equal(1, jobs.DownloadsUsed(Alice));
        Assert.Equal(1, jobs.DownloadsUsed(Bob));

        // Cached titles play for anyone, with or without an account.
        Assert.IsType<ReadyTarget>(await jobs.PrepareAsync("1", null, null, default));
        Assert.Equal(PreflightStatus.Ok, jobs.Preflight("1", null, null).Status);
    }

    [FfmpegFact]
    public async Task QuotaStopsDownloadsBeforeTheyStart()
    {
        var mp3 = await MakeMp3Async("q.mp3", 1);
        Seed(E.Movie("1", "A", 2020), E.Movie("2", "B", 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "a.mp3", mp3.Length);
        _limit = 1;
        using var jobs = CreateManager();

        var first = await jobs.PrepareAsync("1", null, Alice, default);
        if (first is LiveTarget live)
        {
            await live.Completion;
        }

        await Assert.ThrowsAsync<QuotaExceededException>(() => jobs.PrepareAsync("2", null, Alice, default));
        Assert.Equal(1, _source.Opens);
        Assert.Equal(
            [(JobOutcomeKind.MovieReady, "A"), (JobOutcomeKind.Failed, "B")],
            jobs.RecentOutcomes().Select(o => (o.Kind, o.Title)));
    }

    [FfmpegFact]
    public async Task TitlesBeingPlayedAreNotEvicted()
    {
        var mp3 = await MakeMp3Async("l.mp3", 1);
        Seed(E.Movie("1", "A", 2020), E.Movie("2", "B", 2020), E.Movie("3", "C", 2020));
        _source.Open = _ => new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "a.mp3", mp3.Length);
        _maxCache = 1;
        var cache = new CacheStore(() => _cacheRoot, NullLogger<CacheStore>.Instance);

        using (var jobs = CreateManager())
        {
            foreach (var id in new[] { "1", "2" })
            {
                if (await jobs.PrepareAsync(id, null, Alice, default) is LiveTarget live)
                {
                    await live.Completion;
                }
            }

            // Both were requested for playback recently, so neither was evicted to make room.
            Assert.True(File.Exists(cache.FinalPath("1", null)));
            Assert.True(File.Exists(cache.FinalPath("2", null)));
        }

        using (var fresh = CreateManager())
        {
            if (await fresh.PrepareAsync("3", null, Alice, default) is LiveTarget live)
            {
                await live.Completion;
            }
        }

        Assert.False(File.Exists(cache.FinalPath("1", null)));
        Assert.False(File.Exists(cache.FinalPath("2", null)));
        Assert.True(File.Exists(cache.FinalPath("3", null)));
    }

    [FfmpegFact]
    public async Task DeletedPlaceholderIsMadeAgain()
    {
        Seed(E.Movie("1", "A", 2020));
        using var jobs = CreateManager();

        var first = Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("1", null, null, default));
        File.Delete(first.Path);
        var second = Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("1", null, null, default));

        Assert.True(File.Exists(second.Path));
    }

    [FfmpegFact]
    public async Task ChangingTheCacheFolderMidJobKeepsTheJobInItsFolder()
    {
        var mp3 = await MakeMp3Async("r.mp3", 1);
        Seed(E.Movie("1", "A", 2020));
        var oldRoot = _cacheRoot;
        _source.Open = _ =>
        {
            _cacheRoot = Path.Combine(_dir.Path, "moved");
            return new SourceMedia(new MemoryStream(mp3), SourceMediaKind.SingleAudio, "a.mp3", mp3.Length);
        };
        using var jobs = CreateManager();

        if (await jobs.PrepareAsync("1", null, Alice, default) is LiveTarget live)
        {
            await live.Completion;
        }

        Assert.True(File.Exists(new CacheStore(() => oldRoot, NullLogger<CacheStore>.Instance).FinalPath("1", null)));
    }

    private MediaJobManager CreateManager() => new(
        _source,
        _catalog,
        new CacheStore(() => _cacheRoot, NullLogger<CacheStore>.Instance),
        new FfmpegConverter(() => FfmpegTools.Ffmpeg, NullLogger<FfmpegConverter>.Instance),
        new DownloadQuota(_dir.Path),
        new EpisodeCountStore(_dir.Path),
        _events,
        () => new MediaOptions(_limit, _maxCache, new ConversionSettings()),
        NullLogger<MediaJobManager>.Instance);

    private void Seed(params SourceEntry[] entries) =>
        _catalog.Replace(entries.Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

    private async Task<byte[]> MakeMp3Async(string name, int seconds)
    {
        var path = Path.Combine(_dir.Path, name);
        await RunAsync(FfmpegTools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-c:a", "libmp3lame", "-b:a", "64k", path);
        return await File.ReadAllBytesAsync(path);
    }

    private static Task<string> ProbeAsync(string path) =>
        RunAsync(FfmpegTools.Ffprobe, "-v", "error", "-show_entries", "stream=codec_name:format=duration", "-of", "default=nw=1", path);

    private static double Duration(string probe) =>
        double.Parse(probe.Split('\n').First(l => l.StartsWith("duration=", StringComparison.Ordinal))["duration=".Length..], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, stderr);
        return stdout;
    }

    private sealed class Events : IMediaEvents
    {
        public List<(string, int?)> Ready { get; } = [];

        public List<(string, int)> Discovered { get; } = [];

        public void MediaReady(string entryId, int? episode)
        {
            lock (Ready)
            {
                Ready.Add((entryId, episode));
            }
        }

        public void EpisodesDiscovered(string entryId, int count) => Discovered.Add((entryId, count));

        public void MediaFailed(string entryId, int? episode, Exception error)
        {
        }
    }
}
