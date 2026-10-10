using System.Diagnostics;
using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

/// <summary>
/// Every ffmpeg run is bounded (conversions by their limit, probe placeholders by a free slot or their one reserved
/// slot) and stopped at shutdown. Uses a stand-in ffmpeg script that records how many copies run at once (Unix only).
/// </summary>
public sealed class FfmpegSchedulingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly CatalogStore _catalog;
    private readonly CacheStore _cache;
    private readonly string _runs;
    private readonly FakeSource _source = new();

    public FfmpegSchedulingTests()
    {
        _catalog = new CatalogStore(_dir.Path);
        _catalog.Replace(Enumerable.Range(1, 6).Select(i => E.Movie($"{i}", $"M{i}", 2020))
            .Select(e => new CatalogEntry(e, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        _cache = new CacheStore(() => Path.Combine(_dir.Path, "cache"), NullLogger<CacheStore>.Instance) { FreeSpace = _ => long.MaxValue };
        _runs = Path.Combine(_dir.Path, "runs");
        Directory.CreateDirectory(_runs);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task PlaceholdersStayWithinTheConversionLimit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var jobs = CreateManager(FakeFfmpeg(seconds: "0.3"), limit: 2);
        jobs.ExpectedDuration = (id, _) => TimeSpan.FromSeconds(10 + int.Parse(id));

        var probes = Enumerable.Range(1, 6).Select(i => jobs.PrepareForProbeAsync($"{i}", null, null, default)).ToList();
        var targets = await Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(targets, t => Assert.True(File.Exists(Assert.IsType<PlaceholderTarget>(t).Path)));
        var counts = File.ReadAllLines(Path.Combine(_runs, "counts")).Select(int.Parse).ToList();
        Assert.Equal(7, counts.Count); // the plain probe clip plus one per length
        Assert.InRange(counts.Max(), 1, 2 + MediaJobManager.PlaceholderSlots);
    }

    [Fact]
    public async Task ProbesDontWaitForMoviesHoldingEveryConversionSlot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Two movie downloads that never finish, each holding a conversion slot.
        _source.Limits = new(25, 2);
        _source.Open = _ => new SourceMedia(new EndlessStream(), SourceMediaKind.SingleAudio, "a.mp3", null);
        using var jobs = CreateManager(FakeFfmpeg(seconds: "0.2"), limit: 2);
        jobs.ExpectedDuration = (id, _) => TimeSpan.FromSeconds(10 + int.Parse(id));
        using var stop = new CancellationTokenSource();
        var account = new SourceAccount("alice@example.com", "pw");
        var movies = new[] { "1", "2" }.Select(id => jobs.PrepareAsync(id, null, account, stop.Token)).ToList();
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Count(p => p.Phase == JobPhase.Downloading) == 2);

        var watch = Stopwatch.StartNew();
        var probes = await Task.WhenAll(new[] { "3", "4" }.Select(id => jobs.PrepareForProbeAsync(id, null, null, default)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(probes, t => Assert.True(File.Exists(Assert.IsType<PlaceholderTarget>(t).Path)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"probes took {watch.Elapsed}");
        Assert.All(movies, m => Assert.False(m.IsCompleted));
        stop.Cancel();
    }

    [Fact]
    public async Task ProbeOfATitleQueuedForTheDownloadSlotDoesNotWait()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // One download at a time for the account: movie 1 holds it, movie 2 is queued behind it.
        _source.Limits = new(25, 1);
        _source.Open = _ => new SourceMedia(new EndlessStream(), SourceMediaKind.SingleAudio, "a.mp3", null);
        using var jobs = CreateManager(FakeFfmpeg(seconds: "0.2"), limit: 2);
        jobs.ExpectedDuration = (id, _) => TimeSpan.FromSeconds(10 + int.Parse(id));
        using var stop = new CancellationTokenSource();
        var account = new SourceAccount("alice@example.com", "pw");
        var holding = jobs.PrepareAsync("1", null, account, stop.Token);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(p => p.Phase == JobPhase.Downloading));
        var queued = jobs.PrepareAsync("2", null, account, stop.Token);

        var probe = await jobs.PrepareForProbeAsync("2", null, account, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(_cache.PlaceholderPath(12), Assert.IsType<PlaceholderTarget>(probe).Path);
        Assert.False(queued.IsCompleted);
        Assert.Equal(1, _source.Opens);

        // A probe that starts the queued download itself doesn't wait either.
        var started = await jobs.PrepareForProbeAsync("3", null, account, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(_cache.PlaceholderPath(13), Assert.IsType<PlaceholderTarget>(started).Path);
        Assert.False(holding.IsCompleted);
        stop.Cancel();
    }

    [Fact]
    public async Task ProbeOfATitleQueuedForAConversionSlotDoesNotWait()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Movie 1's download holds the only conversion slot; movie 2 has its source and waits to convert.
        _source.Limits = new(25, 2);
        _source.Open = _ => new SourceMedia(new EndlessStream(), SourceMediaKind.SingleAudio, "a.mp3", null);
        _cache.EnsureEntryDir("2");
        File.WriteAllBytes(_cache.SourcePath("2"), new byte[100]);
        using var jobs = CreateManager(FakeFfmpeg(seconds: "0.2"), limit: 1);
        jobs.ExpectedDuration = (id, _) => TimeSpan.FromSeconds(10 + int.Parse(id));
        using var stop = new CancellationTokenSource();
        var holding = jobs.PrepareAsync("1", null, new SourceAccount("alice@example.com", "pw"), stop.Token);
        await WaitUntilAsync(() => jobs.ActiveJobProgress().Any(p => p.Phase == JobPhase.Downloading));

        var probe = await jobs.PrepareForProbeAsync("2", null, null, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(_cache.PlaceholderPath(12), Assert.IsType<PlaceholderTarget>(probe).Path);
        Assert.Contains("2", jobs.ActiveJobs.Select(k => k.Split('|')[0]));
        Assert.True(File.Exists(_cache.SourcePath("2")));
        Assert.False(holding.IsCompleted);
        stop.Cancel();
    }

    [Fact]
    public async Task ShutdownStopsRunningAndQueuedPlaceholders()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var jobs = CreateManager(FakeFfmpeg(seconds: "30"), limit: 1);
        jobs.ExpectedDuration = (id, _) => TimeSpan.FromSeconds(10 + int.Parse(id));
        _cache.EnsureRoot();
        File.WriteAllText(_cache.PlaceholderPath(), "x");

        // One free conversion slot plus the reserved one: two run, the third waits.
        var running = new[] { "1", "2" }.Select(id => jobs.PrepareForProbeAsync(id, null, null, default)).ToList();
        await WaitUntilAsync(() => Started() == 2);
        var queued = jobs.PrepareForProbeAsync("3", null, null, default);
        await Task.Delay(300);
        Assert.Equal(2, Started());

        var watch = Stopwatch.StartNew();
        jobs.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"shutdown took {watch.Elapsed}");

        foreach (var probe in running.Append(queued))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await Task.Delay(300);
        Assert.Equal(2, Started());
    }

    [Fact]
    public async Task AFailedInputStopsFfmpegAndItsChildrenBeforeReturning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Records its own pid and its child's, then runs until killed without reading its input.
        var pids = Path.Combine(_dir.Path, "pids");
        var path = Path.Combine(_dir.Path, "ffmpeg-slow.sh");
        File.WriteAllText(path, $"""
            #!/bin/sh
            echo $$ >> "{pids}"
            sleep 30 &
            echo $! >> "{pids}"
            wait
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var converter = new FfmpegConverter(() => path, NullLogger<FfmpegConverter>.Instance);

        var error = await Assert.ThrowsAsync<IOException>(() => converter.ConvertAsync(
                string.Empty,
                async (stdin, ct) =>
                {
                    await stdin.WriteAsync(new byte[10], ct);
                    await WaitUntilAsync(() => File.Exists(pids) && File.ReadAllLines(pids).Length == 2);
                    throw new IOException("disk full");
                },
                Path.Combine(_dir.Path, "out.mp4"),
                null,
                new ConversionSettings(),
                default))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("disk full", error.Message);
        var (shell, child) = (File.ReadAllLines(pids)[0], File.ReadAllLines(pids)[1]);
        Assert.False(IsRunning(shell), "the ffmpeg process outlived the failed conversion");

        // Killed too; its reparented process may take a moment to be reaped.
        await WaitUntilAsync(() => !IsRunning(child));
    }

    private static bool IsRunning(string pid)
    {
        using var check = Process.Start(new ProcessStartInfo("/bin/kill", ["-0", pid]) { RedirectStandardError = true })!;
        check.WaitForExit();
        return check.ExitCode == 0;
    }

    private int Started() => File.Exists(Path.Combine(_runs, "counts")) ? File.ReadAllLines(Path.Combine(_runs, "counts")).Length : 0;

    /// <summary>Records concurrent runs, waits, then writes the output file (ffmpeg's last argument).</summary>
    private string FakeFfmpeg(string seconds)
    {
        var path = Path.Combine(_dir.Path, "ffmpeg.sh");
        File.WriteAllText(path, $"""
            #!/bin/sh
            mkdir "{_runs}/run.$$"
            ls -d "{_runs}"/run.* | wc -l | tr -d ' ' >> "{_runs}/counts"
            sleep {seconds}
            for last; do :; done
            : > "$last"
            rmdir "{_runs}/run.$$"
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private MediaJobManager CreateManager(string ffmpeg, int limit) => new(
        _source,
        _catalog,
        _cache,
        new FfmpegConverter(() => ffmpeg, NullLogger<FfmpegConverter>.Instance) { ReadinessProbe = _ => null },
        new DownloadQuota(_dir.Path),
        new EpisodeCountStore(_dir.Path),
        new NoEvents(),
        () => new MediaOptions(25, 0, new ConversionSettings(), MaxConcurrentConversions: limit),
        NullLogger<MediaJobManager>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    /// <summary>A download that keeps sending a little data until cancelled.</summary>
    private sealed class EndlessStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(50, cancellationToken);
            buffer.Span[0] = 0;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
}
