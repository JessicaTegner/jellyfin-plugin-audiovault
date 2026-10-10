using System.Diagnostics;
using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

public sealed class FirstPlayLengthTests : IDisposable
{
    private static readonly SourceAccount Alice = new("alice@example.com", "token");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [FfmpegTheory]
    [InlineData("-b:a", "128k", false)]
    [InlineData("-q:a", "6", false)]
    [InlineData("-b:a", "64k", true)]
    public async Task EstimatesMp3LengthFromItsStart(string mode, string value, bool withTag)
    {
        var path = Path.Combine(_dir.Path, "a.mp3");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=300:duration=95", "-c:a", "libmp3lame", mode, value };
        if (withTag)
        {
            args.AddRange(["-metadata", "title=" + new string('x', 3000), "-id3v2_version", "3"]);
        }

        args.Add(path);
        await RunAsync(FfmpegTools.Ffmpeg, [.. args]);

        var estimate = Mp3Duration.FromFile(path);
        Assert.NotNull(estimate);
        Assert.InRange(estimate.Value.TotalSeconds, 94, 96.5);
    }

    [Fact]
    public void GarbageHasNoLength() =>
        Assert.Null(Mp3Duration.Estimate(new byte[4096], 4096));

    [Fact]
    public void ProbeWindowsNestAndClose()
    {
        var windows = new ProbeWindows();
        var a = windows.Open([("1", null)]);
        var b = windows.Open([("1", null), ("2", 3)]);
        Assert.True(windows.IsOpen("1", null));
        a.Dispose();
        Assert.True(windows.IsOpen("1", null));
        b.Dispose();
        b.Dispose();
        Assert.False(windows.IsOpen("1", null));
        Assert.False(windows.IsOpen("2", 3));
    }

    [FfmpegFact]
    public async Task ProbeDuringFirstDownloadGetsPlaceholderWithTheRealLength()
    {
        var mp3Path = Path.Combine(_dir.Path, "m.mp3");
        await RunAsync(FfmpegTools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=300:duration=600", "-c:a", "libmp3lame", "-b:a", "64k", mp3Path);
        var bytes = await File.ReadAllBytesAsync(mp3Path);

        var catalog = new CatalogStore(_dir.Path);
        catalog.Replace([new CatalogEntry(E.Movie("1", "Long", 2020), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        var gate = new SlowStream(bytes, releaseAfter: 200_000);
        var source = new FakeSource { Open = _ => new SourceMedia(gate, SourceMediaKind.SingleAudio, "m.mp3", bytes.Length) };
        using var jobs = new MediaJobManager(
            source,
            catalog,
            new CacheStore(() => Path.Combine(_dir.Path, "cache"), NullLogger<CacheStore>.Instance),
            new FfmpegConverter(() => FfmpegTools.Ffmpeg, NullLogger<FfmpegConverter>.Instance),
            new DownloadQuota(_dir.Path),
            new EpisodeCountStore(_dir.Path),
            new NoEvents(),
            () => new MediaOptions(25, 0, new ConversionSettings()),
            NullLogger<MediaJobManager>.Instance);

        var probe = Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("1", null, Alice, default));
        var playback = await jobs.PrepareAsync("1", null, Alice, default);

        Assert.IsType<LiveTarget>(playback);
        var probed = await RunAsync(FfmpegTools.Ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", probe.Path);
        Assert.InRange(double.Parse(probed.Trim(), System.Globalization.CultureInfo.InvariantCulture), 599, 602);
        Assert.True(new FileInfo(probe.Path).Length < 20_000);
        Assert.Equal(1, source.Opens);

        gate.Release();
        await ((LiveTarget)playback).Completion;
    }

    [FfmpegFact]
    public async Task ProbeOfTitleThatWontDownloadGetsTheExpectedLength()
    {
        var catalog = new CatalogStore(_dir.Path);
        catalog.Replace([new CatalogEntry(E.Movie("1", "Long", 2020), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        var source = new FakeSource();
        using var jobs = new MediaJobManager(
            source,
            catalog,
            new CacheStore(() => Path.Combine(_dir.Path, "cache"), NullLogger<CacheStore>.Instance),
            new FfmpegConverter(() => FfmpegTools.Ffmpeg, NullLogger<FfmpegConverter>.Instance),
            new DownloadQuota(_dir.Path),
            new EpisodeCountStore(_dir.Path),
            new NoEvents(),
            () => new MediaOptions(25, 0, new ConversionSettings()),
            NullLogger<MediaJobManager>.Instance)
        {
            ExpectedDuration = (entry, ep) => entry == "1" && ep is null ? TimeSpan.FromMinutes(97) : null,
        };

        var probe = Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("1", null, null, default));
        var playback = Assert.IsType<PlaceholderTarget>(await jobs.PrepareAsync("1", null, null, default));

        var ffprobe = FfmpegTools.Ffprobe;
        var probed = await RunAsync(ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", probe.Path);
        Assert.InRange(double.Parse(probed.Trim(), System.Globalization.CultureInfo.InvariantCulture), 97 * 60 - 1, 97 * 60 + 2);
        var played = await RunAsync(ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", playback.Path);
        Assert.InRange(double.Parse(played.Trim(), System.Globalization.CultureInfo.InvariantCulture), 1.5, 2.5);
        Assert.Equal(0, source.Opens);

        jobs.ExpectedDuration = null;
        var unknown = Assert.IsType<PlaceholderTarget>(await jobs.PrepareForProbeAsync("1", null, null, default));
        Assert.Equal(playback.Path, unknown.Path);
    }

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

    /// <summary>Delivers the first bytes at once, then stalls like a slow download until released.</summary>
    private sealed class SlowStream(byte[] data, int releaseAfter) : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= releaseAfter)
            {
                await _released.Task.WaitAsync(cancellationToken);
            }

            var n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
