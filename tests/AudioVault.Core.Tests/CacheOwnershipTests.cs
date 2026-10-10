using AudioVault.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

/// <summary>The cache only ever deletes what the plugin created.</summary>
public sealed class CacheOwnershipTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ANewOrEmptyFolderIsClaimed()
    {
        var fresh = Cache("new");
        Assert.Null(fresh.OwnershipProblem);
        Assert.True(fresh.EnsureRoot());
        Assert.True(File.Exists(Path.Combine(fresh.Root, CacheStore.OwnerMarkerName)));

        var empty = Cache("empty");
        Directory.CreateDirectory(empty.Root);
        Assert.Null(empty.OwnershipProblem);
        empty.EnsureEntryDir("1");
        Assert.True(File.Exists(Path.Combine(empty.EntryDir("1"), CacheStore.EntryMarkerName)));
        Assert.Null(empty.OwnershipProblem);
    }

    [Fact]
    public void AFolderWithOtherFilesIsUsedButNeverCleaned()
    {
        var cache = Cache("shared");
        var photos = Path.Combine(cache.Root, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllBytes(Path.Combine(photos, "a.jpg"), new byte[1000]);
        File.WriteAllText(Path.Combine(photos, "upload.part"), "x");

        Assert.Contains("already holds other files", cache.OwnershipProblem);
        Assert.False(cache.EnsureRoot());

        // Downloads still work, but nothing here is ever deleted, not even the plugin's own entries.
        File.WriteAllText(Path.Combine(cache.EnsureEntryDir("1"), "main.live.mp4"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(cache.EntryDir("1"), "main.live.mp4"), DateTime.UtcNow.AddDays(-1));
        cache.EnforceLimit(1, new HashSet<string>());
        cache.CleanupStale(DateTime.UtcNow);

        Assert.True(File.Exists(Path.Combine(photos, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(photos, "upload.part")));
        Assert.True(File.Exists(Path.Combine(cache.EntryDir("1"), "main.live.mp4")));
        Assert.False(File.Exists(Path.Combine(cache.Root, CacheStore.OwnerMarkerName)));
    }

    [Fact]
    public void InAnOwnedFolderOnlyMarkedEntriesAreEvictedOrCleaned()
    {
        var cache = Cache("owned");
        File.WriteAllBytes(Path.Combine(cache.EnsureEntryDir("1"), "main.mp4"), new byte[1000]);
        File.WriteAllText(Path.Combine(cache.EntryDir("1"), "main.live.mp4"), "x");

        // Something an administrator put there later.
        var foreign = Path.Combine(cache.Root, "notes");
        Directory.CreateDirectory(foreign);
        File.WriteAllBytes(Path.Combine(foreign, "big.bin"), new byte[5000]);
        File.WriteAllText(Path.Combine(foreign, "draft.part"), "x");
        File.WriteAllText(Path.Combine(cache.Root, "loose.tmp.mp4"), "x");

        cache.CleanupStale();
        Assert.False(File.Exists(Path.Combine(cache.EntryDir("1"), "main.live.mp4")));
        Assert.True(File.Exists(Path.Combine(foreign, "draft.part")));
        Assert.True(File.Exists(Path.Combine(cache.Root, "loose.tmp.mp4")));

        cache.EnforceLimit(1, new HashSet<string>());
        Assert.False(Directory.Exists(cache.EntryDir("1")));
        Assert.True(File.Exists(Path.Combine(foreign, "big.bin")));
    }

    [Fact]
    public void ACacheFromBeforeTheMarkersIsAdopted()
    {
        var cache = Cache("legacy");
        Directory.CreateDirectory(cache.EntryDir("100"));
        File.WriteAllBytes(cache.FinalPath("100", null), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(cache.EntryDir("200"), "extracted"));
        File.WriteAllText(cache.EpisodeMapPath("200"), "{}");
        Directory.CreateDirectory(Path.Combine(cache.Root, CacheStore.PlaceholderDirName));
        File.WriteAllText(cache.PlaceholderPath(30), "x");
        File.WriteAllText(cache.PlaceholderPath(), "x");

        Assert.Null(cache.OwnershipProblem);
        cache.EnforceLimit(1, new HashSet<string> { "200" });

        Assert.False(Directory.Exists(cache.EntryDir("100")));
        Assert.True(File.Exists(Path.Combine(cache.EntryDir("200"), CacheStore.EntryMarkerName)));
        Assert.True(File.Exists(cache.PlaceholderPath(30)));
    }

    [Fact]
    public void AFolderMixingOldCacheAndOtherFilesIsNotAdopted()
    {
        var cache = Cache("mixed");
        Directory.CreateDirectory(cache.EntryDir("100"));
        File.WriteAllBytes(cache.FinalPath("100", null), new byte[1000]);
        File.WriteAllText(Path.Combine(cache.EntryDir("100"), "notes.txt"), "mine");

        Assert.NotNull(cache.OwnershipProblem);
        cache.EnforceLimit(1, new HashSet<string>());
        Assert.True(File.Exists(cache.FinalPath("100", null)));
    }

    [Fact]
    public void AnExtractFolderWithOtherFilesIsNotTakenForAnOldCache()
    {
        var cache = Cache("foreign-extracted");
        var important = Path.Combine(cache.EntryDir("42"), "extracted", "important.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(important)!);
        File.WriteAllText(important, "keep me");

        Assert.NotNull(cache.OwnershipProblem);
        Assert.False(cache.EnsureRoot());
        cache.EnforceLimit(1, new HashSet<string>());
        cache.CleanupStale();

        Assert.True(File.Exists(important));
        Assert.False(File.Exists(Path.Combine(cache.EntryDir("42"), CacheStore.EntryMarkerName)));
    }

    [Fact]
    public void AnOldSeasonWithOnlyAudioIsAdopted()
    {
        var cache = Cache("old-season");
        var episode = Path.Combine(cache.ExtractDir("200"), "S", "S01E01.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(episode)!);
        File.WriteAllBytes(episode, new byte[1000]);
        File.WriteAllText(cache.EpisodeMapPath("200"), "{\"1\":\"extracted/S/S01E01.mp3\"}");

        Assert.Null(cache.OwnershipProblem);
        Assert.True(cache.EnsureRoot());
        Assert.True(File.Exists(Path.Combine(cache.EntryDir("200"), CacheStore.EntryMarkerName)));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("extracted/notes.txt")]
    [InlineData(".hidden")]
    public void AnExistingFolderByAnEntrysNameIsNeitherClaimedNorUsed(string foreignFile)
    {
        var cache = Cache("collision");
        Assert.True(cache.EnsureRoot());
        var foreign = Path.Combine(cache.EntryDir("100"), foreignFile);
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "mine");

        var error = Assert.Throws<CacheFolderConflictException>(() => cache.EnsureEntryDir("100"));
        Assert.Contains("didn't create", error.Message);
        cache.EnforceLimit(1, new HashSet<string>());

        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(Path.Combine(cache.EntryDir("100"), CacheStore.EntryMarkerName)));
    }

    [Fact]
    public void AnEmptyOrOldEntryFolderIsClaimed()
    {
        var cache = Cache("claim");
        Assert.True(cache.EnsureRoot());
        Directory.CreateDirectory(cache.EntryDir("1"));
        Directory.CreateDirectory(cache.EntryDir("2"));
        File.WriteAllText(cache.SourcePath("2") + ".part", "x");

        cache.EnsureEntryDir("1");
        cache.EnsureEntryDir("2");

        Assert.True(File.Exists(Path.Combine(cache.EntryDir("1"), CacheStore.EntryMarkerName)));
        Assert.True(File.Exists(Path.Combine(cache.EntryDir("2"), CacheStore.EntryMarkerName)));
    }

    private CacheStore Cache(string name) =>
        new(() => Path.Combine(_dir.Path, name), NullLogger<CacheStore>.Instance) { FreeSpace = _ => long.MaxValue };
}
