using System.IO.Compression;
using System.Net;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;

namespace AudioVault.Core.Tests;

public class FailureInfoTests
{
    [Fact]
    public async Task An_archive_entry_outside_its_folder_is_a_category_not_just_a_message()
    {
        using var dir = new TempDir();
        var zip = Path.Combine(dir.Path, "season.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var entry = await archive.CreateEntry("../my_private_show/episode_one.mp3").OpenAsync();
            entry.WriteByte(1);
        }

        var ex = await Assert.ThrowsAsync<SourceException>(
            () => ArchiveExtractor.ExtractAudioAsync(zip, Path.Combine(dir.Path, "out"), 10, 1000, _ => { }, default));

        Assert.Equal(new FailureInfo(FailureKind.ArchiveEntryOutsideFolder, "SourceException", null), FailureInfo.From(ex));
    }

    [Fact]
    public void Exceptions_are_classified_by_type_and_cause()
    {
        Assert.Equal(FailureKind.SourceLayoutChanged, FailureInfo.From(new SourceFormatChangedException("x")).Kind);
        Assert.Equal(FailureKind.DiskFull, FailureInfo.From(new InsufficientDiskSpaceException("x")).Kind);
        Assert.Equal(FailureKind.DownloadLimit, FailureInfo.From(new QuotaExceededException(5)).Kind);
        Assert.Equal(FailureKind.ConversionUnavailable, FailureInfo.From(new ConversionUnavailableException("x")).Kind);
        Assert.Equal(FailureKind.SourceUnavailable, FailureInfo.From(new SourceUnavailableException("x")).Kind);
        Assert.Equal(FailureKind.DownloadFailed, FailureInfo.From(new SourceException("x")).Kind);
        Assert.Equal(FailureKind.Disk, FailureInfo.From(new IOException("x")).Kind);
        Assert.Equal(FailureKind.Other, FailureInfo.From(new InvalidOperationException("x")).Kind);

        var wrapped = FailureInfo.From(new DownloadNotStartedException("x", new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(new FailureInfo(FailureKind.Network, "DownloadNotStartedException", 503), wrapped);
        Assert.Equal(429, FailureInfo.From(new SourceUnavailableException("x") { HttpStatus = 429 }).HttpStatus);
    }

    [Fact]
    public void A_recorded_failure_keeps_its_category_beside_the_message()
    {
        var tracker = new JobProgressTracker();
        tracker.Record("1", null, "Film", JobOutcomeKind.Failed, "full text", FailureInfo.From(new ConversionFailedException("ffmpeg exited")));

        var outcome = Assert.Single(tracker.RecentOutcomes());
        Assert.Equal(FailureKind.ConversionFailed, outcome.Failure?.Kind);
    }
}
