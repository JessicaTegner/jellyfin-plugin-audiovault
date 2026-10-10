using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

public class EpisodeMapperTests
{
    [Fact]
    public void UsesSxxEyy() =>
        Assert.Equal(
            new Dictionary<int, string> { [1] = "Show S01E01.mp3", [2] = "Show S01E02.mp3", [10] = "Show S01E10.mp3" },
            EpisodeMapper.Map(["Show S01E10.mp3", "Show S01E02.mp3", "Show S01E01.mp3", "cover.jpg"]));

    [Fact]
    public void UsesEpisodeWord() =>
        Assert.Equal("b Episode 2.mp3", EpisodeMapper.Map(["a Episode 1.mp3", "b Episode 2.mp3"])[2]);

    [Fact]
    public void UsesLeadingNumbers() =>
        Assert.Equal("02 - Second.mp3", EpisodeMapper.Map(["01 - Pilot.mp3", "02 - Second.mp3"])[2]);

    [Fact]
    public void FallsBackToNaturalOrder()
    {
        var map = EpisodeMapper.Map(["Pilot part10.mp3", "Pilot part2.mp3", "Pilot part1.mp3"]);
        Assert.Equal(["Pilot part1.mp3", "Pilot part2.mp3", "Pilot part10.mp3"], map.OrderBy(kv => kv.Key).Select(kv => kv.Value));
    }

    [Fact]
    public void DuplicateNumbersFallBackToNaturalOrder() =>
        Assert.Equal(2, EpisodeMapper.Map(["S01E01 a.mp3", "S01E01 b.mp3"]).Count);

    [Fact]
    public void IgnoresMacResourceForks() =>
        Assert.Single(EpisodeMapper.Map(["S01E01.mp3", "._S01E01.mp3"]));
}

public class UrlSignerTests
{
    [Fact]
    public void RoundTrips()
    {
        var signer = new UrlSigner("secret");
        Assert.True(signer.Verify("123", 4, signer.Sign("123", 4)));
        Assert.False(signer.Verify("123", 5, signer.Sign("123", 4)));
        Assert.False(signer.Verify("124", 4, signer.Sign("123", 4)));
        Assert.False(new UrlSigner("other").Verify("123", 4, signer.Sign("123", 4)));
        Assert.False(signer.Verify("123", null, null));
    }
}

public class DownloadQuotaTests
{
    [Fact]
    public void EnforcesRollingWindowAndPersists()
    {
        using var dir = new TempDir();
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
        var quota = new DownloadQuota(dir.Path, time);

        Assert.True(quota.TryAcquire("a", "acc", 2));
        Assert.True(quota.TryAcquire("b", "acc", 2));
        Assert.False(quota.TryAcquire("c", "acc", 2));

        var reloaded = new DownloadQuota(dir.Path, time);
        Assert.Equal(2, reloaded.Used("acc"));

        time.Now = time.Now.AddHours(24).AddMinutes(1);
        Assert.True(reloaded.TryAcquire("c", "acc", 2));
    }

    [Fact]
    public void NextAvailableIsWhenTheOldestCountedDownloadExpires()
    {
        using var dir = new TempDir();
        var start = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var time = new ManualTime(start);
        var quota = new DownloadQuota(dir.Path, time);
        quota.TryAcquire("a", "acc", 2);
        time.Now = start.AddHours(1);
        quota.TryAcquire("b", "acc", 2);

        Assert.Equal(start.AddHours(24), quota.NextAvailableAt("acc", 2));
        Assert.Null(quota.NextAvailableAt("acc", 3));
    }

    [Fact]
    public void RefundReturnsTheSlot()
    {
        using var dir = new TempDir();
        var quota = new DownloadQuota(dir.Path);
        Assert.True(quota.TryAcquire("a", "acc", 1));
        Assert.True(quota.TryAcquire("a", "other", 1));
        quota.Refund("a", "acc");
        Assert.True(quota.TryAcquire("b", "acc", 1));
    }
}

public class CorruptStateTests
{
    [Fact]
    public void MalformedJsonIsMovedAsideAndReadsAsMissing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(JsonFile.Read<Dictionary<string, int>>(path, out var corrupt));

        Assert.True(corrupt);
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(Assert.Single(Directory.GetFiles(dir.Path, "state.json.corrupt-*"))));
        Assert.Null(JsonFile.Read<Dictionary<string, int>>(path, out corrupt));
        Assert.False(corrupt);
    }

    [Fact]
    public void DamagedDownloadHistoryBlocksEveryAccountForADay()
    {
        using var dir = new TempDir();
        var start = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var time = new ManualTime(start);
        File.WriteAllText(Path.Combine(dir.Path, "downloads.json"), "[{\"EntryId\":");

        var quota = new DownloadQuota(dir.Path, time);
        Assert.False(quota.TryAcquire("a", "acc", 25));
        Assert.Equal(start.AddHours(24), quota.NextAvailableAt("other", 25));
        Assert.Equal(0, quota.UsedByAll);

        // The block survives a restart, then expires.
        var reloaded = new DownloadQuota(dir.Path, time);
        Assert.Equal(start.AddHours(24), reloaded.BlockedUntil);
        time.Now = start.AddHours(24).AddMinutes(1);
        Assert.Null(reloaded.BlockedUntil);
        Assert.True(reloaded.TryAcquire("a", "acc", 25));
    }

    [Fact]
    public void ARecoveryThatCouldNotBeSavedStillBlocksAfterRestarts()
    {
        using var dir = new TempDir();
        var start = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var time = new ManualTime(start);
        var path = Path.Combine(dir.Path, "downloads.json");
        File.WriteAllText(path, "[{\"EntryId\":");
        var obstruction = Directory.CreateDirectory(path + ".tmp"); // the replacement can't be written (like a full disk)

        var quota = new DownloadQuota(dir.Path, time);
        Assert.Equal(start.AddHours(24), quota.BlockedUntil);
        Assert.Contains("can't be saved", quota.Problem);
        Assert.True(File.Exists(path)); // kept until its replacement is saved

        time.Now = start.AddHours(1);
        var restarted = new DownloadQuota(dir.Path, time);
        Assert.NotNull(restarted.BlockedUntil);
        Assert.False(restarted.TryAcquire("a", "acc", 25));

        obstruction.Delete();
        var recovered = new DownloadQuota(dir.Path, time);
        Assert.Null(recovered.Problem);
        Assert.NotNull(new DownloadQuota(dir.Path, time).BlockedUntil);
    }

    [Fact]
    public void AHistorySetAsideWithoutAReplacementBlocksForTheRestOfItsDay()
    {
        using var dir = new TempDir();
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
        File.WriteAllText(Path.Combine(dir.Path, "downloads.json.corrupt-20261010110000"), "{broken");
        File.WriteAllText(Path.Combine(dir.Path, "downloads.json.corrupt-20261001110000"), "{broken");

        var quota = new DownloadQuota(dir.Path, time);

        Assert.Equal(DateTimeOffset.Parse("2026-10-11T11:00:00Z"), quota.BlockedUntil);
        Assert.Equal(DateTimeOffset.Parse("2026-10-11T11:00:00Z"), new DownloadQuota(dir.Path, time).BlockedUntil);
    }

    [Fact]
    public void AnOldSetAsideHistoryDoesNotBlock()
    {
        using var dir = new TempDir();
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
        File.WriteAllText(Path.Combine(dir.Path, "downloads.json.corrupt-20261008110000"), "{broken");

        Assert.Null(new DownloadQuota(dir.Path, time).BlockedUntil);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"EntryId\":\"1\",\"At\":\"2026-10-10T11:00:00Z\",\"Account\":null}]")]
    [InlineData("[{\"EntryId\":\"1\",\"At\":\"2026-10-10T11:00:00Z\"}]")]
    [InlineData("[{\"EntryId\":\"\",\"At\":\"2026-10-10T11:00:00Z\",\"Account\":\"acc\"}]")]
    [InlineData("[{\"Account\":\"acc\",\"At\":\"2026-10-10T11:00:00Z\"}]")]
    [InlineData("[{\"EntryId\":\"1\",\"Account\":\"acc\"}]")]
    [InlineData("[{\"EntryId\":\"1\",\"At\":\"2026-10-12T00:00:00Z\",\"Account\":\"acc\"}]")]
    public void ValidJsonThatCantBeTrustedBlocksLikeDamagedJson(string json)
    {
        using var dir = new TempDir();
        var start = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var time = new ManualTime(start);
        var path = Path.Combine(dir.Path, "downloads.json");
        File.WriteAllText(path, json);

        var quota = new DownloadQuota(dir.Path, time);

        Assert.Equal(start.AddHours(24), quota.BlockedUntil);
        Assert.False(quota.TryAcquire("a", "acc", 25));
        Assert.Equal(json, File.ReadAllText(Assert.Single(Directory.GetFiles(dir.Path, "downloads.json.corrupt-*"))));
        Assert.Equal(start.AddHours(24), new DownloadQuota(dir.Path, time).BlockedUntil);
    }

    [Fact]
    public void UnreadableDownloadHistoryBlocksAndMissingStartsFresh()
    {
        using var dir = new TempDir();
        Assert.Null(new DownloadQuota(dir.Path).BlockedUntil);

        Directory.CreateDirectory(Path.Combine(dir.Path, "downloads.json"));
        var quota = new DownloadQuota(dir.Path);
        Assert.NotNull(quota.BlockedUntil);
        Assert.False(quota.TryAcquire("a", "acc", 25));
    }

    [Fact]
    public void ValidHistoryIsKept()
    {
        using var dir = new TempDir();
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
        File.WriteAllText(
            Path.Combine(dir.Path, "downloads.json"),
            "[{\"EntryId\":\"1\",\"At\":\"2026-10-10T11:00:00Z\",\"Account\":\"acc\"},{\"EntryId\":\"2\",\"At\":\"2026-10-01T11:00:00Z\",\"Account\":\"acc\"}]");

        var quota = new DownloadQuota(dir.Path, time);

        Assert.Null(quota.BlockedUntil);
        Assert.Equal(1, quota.Used("acc"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.corrupt-*"));
    }

    [Fact]
    public void ContainedReadsReportInsteadOfThrowing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        Assert.Null(JsonFile.ReadContained<List<int>>(path, out var status));
        Assert.Equal(StateFileStatus.Missing, status);

        Directory.CreateDirectory(path);
        Assert.Null(JsonFile.ReadContained<List<int>>(path, out status));
        Assert.Equal(StateFileStatus.Unreadable, status);
        Directory.Delete(path);

        File.WriteAllText(path, "null");
        Assert.Null(JsonFile.ReadContained<List<int>>(path, out status));
        Assert.Equal(StateFileStatus.Loaded, status);

        File.WriteAllText(path, "[1,");
        Assert.Null(JsonFile.ReadContained<List<int>>(path, out status));
        Assert.Equal(StateFileStatus.Corrupt, status);
    }
}

public class LibraryWriterTests
{
    [Fact]
    public void WritesChangesAndRemovesOrphans()
    {
        using var dir = new TempDir();
        var writer = new LibraryWriter(NullLogger<LibraryWriter>.Instance);

        var first = writer.Sync(dir.Path, new Dictionary<string, string> { ["A/a.strm"] = "1", ["B/b.strm"] = "2" });
        Assert.Equal(2, first.Written);

        var second = writer.Sync(dir.Path, new Dictionary<string, string> { ["A/a.strm"] = "1" });
        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.Deleted);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "B")));
    }

    [Fact]
    public void RefusesMassDeletion()
    {
        using var dir = new TempDir();
        var writer = new LibraryWriter(NullLogger<LibraryWriter>.Instance);
        writer.Sync(dir.Path, Enumerable.Range(0, 100).ToDictionary(i => $"{i}.strm", i => "x"));

        var result = writer.Sync(dir.Path, new Dictionary<string, string>());

        Assert.True(result.DeletionsSkipped);
        Assert.Equal(100, Directory.GetFiles(dir.Path, "*.strm").Length);
    }
}

public class PlaybackIntentsTests
{
    [Fact]
    public void ExpiresAfterFiveMinutes()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
        var intents = new PlaybackIntents(time);
        var account = new AudioVault.Core.Sources.SourceAccount("a@b.c", "pw");
        intents.Register("1", 2, account);

        Assert.Equal(account, intents.AccountFor("1", 2));
        Assert.Null(intents.AccountFor("1", 3));
        Assert.Null(intents.AccountFor("1", null));

        time.Now = time.Now.AddMinutes(6);
        Assert.Null(intents.AccountFor("1", 2));
    }
}
