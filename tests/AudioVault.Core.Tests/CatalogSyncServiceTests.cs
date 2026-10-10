using System.Runtime.CompilerServices;
using AudioVault.Core.Catalog;
using AudioVault.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioVault.Core.Tests;

internal sealed class FakeSource : IContentSource
{
    public List<SourceEntry> All { get; set; } = [];

    public List<SourceEntry> Recent { get; set; } = [];

    /// <summary>Full listings wait for this before returning anything.</summary>
    public Task ListGate { get; set; } = Task.CompletedTask;

    public Func<string, SourceMedia>? Open { get; set; }

    public int Opens { get; private set; }

    public string Id => "fake";

    public SourceLimits Limits { get; set; } = new(25, 1);

    public async IAsyncEnumerable<SourceEntry> ListAllAsync(IProgress<double>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        await ListGate;
        foreach (var e in All)
        {
            await Task.Yield();
            yield return e;
        }
    }

    public async IAsyncEnumerable<SourceEntry> ListRecentAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in Recent)
        {
            await Task.Yield();
            yield return e;
        }
    }

    public Task<SourceHealth> CheckHealthAsync(SourceAccount? account, CancellationToken ct) => Task.FromResult(new SourceHealth(true, "ok"));

    public SourceHealth Readiness { get; set; } = new(true, "ok");

    public Task<bool> SignOutAsync(SourceAccount account, CancellationToken ct) => Task.FromResult(true);

    public Task<SourceSignIn> SignInAsync(string login, string password, CancellationToken ct) =>
        Task.FromResult(new SourceSignIn("token", false));

    public SourceHealth GetReadiness(SourceAccount account) => Readiness;

    public List<string> OpenedWith { get; } = [];

    public Task<SourceMedia> OpenAsync(string entryId, SourceAccount account, CancellationToken ct)
    {
        Opens++;
        OpenedWith.Add(account.Key);
        return Task.FromResult(Open!(entryId));
    }
}

public class CatalogSyncServiceTests
{
    [Fact]
    public async Task EmptyCatalogIsRejected()
    {
        using var dir = new TempDir();
        var sync = new CatalogSyncService(new FakeSource(), new CatalogStore(dir.Path), NullLogger<CatalogSyncService>.Instance);

        await Assert.ThrowsAsync<SourceFormatChangedException>(() => sync.SyncFullAsync(null, default));
    }

    [Fact]
    public async Task FullSyncAddsUpdatesAndRemoves()
    {
        using var dir = new TempDir();
        var store = new CatalogStore(dir.Path);
        var source = new FakeSource { All = Enumerable.Range(1, 10).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList() };
        var sync = new CatalogSyncService(source, store, NullLogger<CatalogSyncService>.Instance);
        await sync.SyncFullAsync(null, default);

        source.All = [.. source.All.Skip(1).Select(e => e.Id == "2" ? e with { Title = "Renamed" } : e), E.Movie("11", "New", 2001)];
        var result = await sync.SyncFullAsync(null, default);

        Assert.Equal((1, 1, 1), (result.Added, result.Updated, result.Removed));
        Assert.Equal("Renamed", new CatalogStore(dir.Path).Get("2")!.Title);
    }

    [Fact]
    public async Task MassRemovalIsSkipped()
    {
        using var dir = new TempDir();
        var store = new CatalogStore(dir.Path);
        var source = new FakeSource { All = Enumerable.Range(1, 10).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList() };
        var sync = new CatalogSyncService(source, store, NullLogger<CatalogSyncService>.Instance);
        await sync.SyncFullAsync(null, default);

        source.All = [.. source.All.Take(5)];
        var result = await sync.SyncFullAsync(null, default);

        Assert.True(result.RemovalsSkipped);
        Assert.Equal(10, store.Count);
    }

    [Fact]
    public async Task RecentSyncNeverRemoves()
    {
        using var dir = new TempDir();
        var store = new CatalogStore(dir.Path);
        var source = new FakeSource { All = [E.Movie("1", "A", 2000), E.Movie("2", "B", 2000)], Recent = [E.Movie("3", "C", 2001)] };
        var sync = new CatalogSyncService(source, store, NullLogger<CatalogSyncService>.Instance);
        await sync.SyncFullAsync(null, default);

        await sync.SyncRecentAsync(default);

        Assert.Equal(3, store.Count);
    }

    [Fact]
    public async Task LargeRemovalNeedsConfirmationByTheNextSync()
    {
        using var dir = new TempDir();
        var store = new CatalogStore(dir.Path);
        var all = Enumerable.Range(1, 1000).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList();
        var source = new FakeSource { All = all };
        var sync = new CatalogSyncService(source, store, NullLogger<CatalogSyncService>.Instance);
        await sync.SyncFullAsync(null, default);

        // An empty middle page drops 100 entries once...
        source.All = [.. all.Where(e => int.Parse(e.Id) is <= 400 or > 500)];
        var glitch = await sync.SyncFullAsync(null, default);
        Assert.True(glitch.RemovalsSkipped);
        Assert.Equal(0, glitch.Removed);

        // ...and a different one the next time: neither is removed.
        source.All = [.. all.Where(e => int.Parse(e.Id) is <= 600 or > 700)];
        var other = await sync.SyncFullAsync(null, default);
        Assert.Equal(0, other.Removed);
        Assert.Equal(1000, store.Count);

        // Entries missing twice in a row really are gone.
        var confirmed = await sync.SyncFullAsync(null, default);
        Assert.Equal(100, confirmed.Removed);
        Assert.False(confirmed.RemovalsSkipped);
        Assert.Equal(900, store.Count);
    }

    [Fact]
    public async Task SmallRemovalsApplyImmediately()
    {
        using var dir = new TempDir();
        var store = new CatalogStore(dir.Path);
        var all = Enumerable.Range(1, 1000).Select(i => E.Movie($"{i}", $"M{i}", 2000)).ToList();
        var source = new FakeSource { All = all };
        var sync = new CatalogSyncService(source, store, NullLogger<CatalogSyncService>.Instance);
        await sync.SyncFullAsync(null, default);

        source.All = [.. all.Skip(CatalogSyncService.MaxUnconfirmedRemovals)];
        var result = await sync.SyncFullAsync(null, default);

        Assert.Equal(CatalogSyncService.MaxUnconfirmedRemovals, result.Removed);
    }

    [Fact]
    public async Task ConcurrentFullAndRecentSyncsKeepBothUpdates()
    {
        using var dir = new TempDir();
        var store = new CatalogStore(dir.Path);
        var gate = new TaskCompletionSource();
        var source = new FakeSource
        {
            All = [E.Movie("1", "A", 2000), E.Movie("2", "B", 2000)],
            Recent = [E.Movie("3", "C", 2001)],
            ListGate = gate.Task,
        };
        var sync = new CatalogSyncService(source, store, NullLogger<CatalogSyncService>.Instance);

        var full = sync.SyncFullAsync(null, default);
        var recent = sync.SyncRecentAsync(default);
        await Task.Delay(100);
        Assert.False(recent.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(full, recent);

        Assert.Equal(["1", "2", "3"], store.All().Select(e => e.Entry.Id).Order());
    }
}
