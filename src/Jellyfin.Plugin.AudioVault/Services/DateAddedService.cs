using System.Threading.Channels;
using AudioVault.Core.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Keeps DateCreated of our items on the catalog's date added, so Recently Added follows upload order instead of
/// .strm file creation times. Jellyfin resets DateCreated from the file when a .strm changes, so this re-applies it
/// whenever an item is added or updated.
/// </summary>
public sealed class DateAddedService(
    ILibraryManager libraryManager,
    LibrarySyncService librarySync,
    ILogger<DateAddedService> logger) : IHostedService
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    private readonly Channel<BaseItem> _queue = Channel.CreateUnbounded<BaseItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private Task _worker = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemUpdated += OnItemChanged;
        _worker = Task.Run(WorkAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemUpdated -= OnItemChanged;
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-applies dates to the items of entries whose date changed without their files changing (re-uploads).</summary>
    public void Refresh(IEnumerable<string> entryIds)
    {
        foreach (var path in entryIds.SelectMany(librarySync.FindStrmPaths))
        {
            if (libraryManager.FindByPath(path, false) is { } item)
            {
                Enqueue(item);
            }
        }
    }

    /// <summary>Sets the date of every existing item in the AudioVault libraries.</summary>
    public async Task<int> FixAllAsync(IProgress<double> progress, CancellationToken ct)
    {
        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Season, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
        });

        var fixedCount = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 100 == 0)
            {
                progress.Report(100.0 * i / items.Count);
            }

            if (await ApplyAsync(items[i], ct).ConfigureAwait(false))
            {
                fixedCount++;
            }
        }

        logger.LogInformation("Set date added on {Fixed} of {Total} library items", fixedCount, items.Count);
        return fixedCount;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is { } item && Desired(item) is { } desired && Differs(item, desired))
            {
                Enqueue(item);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check the date added of a changed item");
        }
    }

    private void Enqueue(BaseItem item)
    {
        _queue.Writer.TryWrite(item);

        // A new episode can change its season's and series' newest date.
        if (item is Episode episode)
        {
            foreach (var parent in new BaseItem?[] { episode.Season, episode.Series })
            {
                if (parent is not null && Desired(parent) is { } d && Differs(parent, d))
                {
                    _queue.Writer.TryWrite(parent);
                }
            }
        }
    }

    private async Task WorkAsync()
    {
        var ct = _stopping.Token;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ApplyAsync(item, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not set date added of {Item}", item.Path);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ApplyAsync(BaseItem item, CancellationToken ct)
    {
        if (Desired(item) is not { } desired || !Differs(item, desired))
        {
            return false;
        }

        item.DateCreated = desired;
        // Our own save raises ItemUpdated again; it is a no-op then because the date already matches.
        // None still saves to the database but skips metadata savers, so no .nfo files appear next to our .strm files.
        await item.UpdateToRepositoryAsync(ItemUpdateType.None, ct).ConfigureAwait(false);
        return true;
    }

    private DateTime? Desired(BaseItem item) =>
        item.Path is { Length: > 0 } path ? librarySync.DateAddedFor(path)?.UtcDateTime : null;

    private static bool Differs(BaseItem item, DateTime desired) => (item.DateCreated - desired).Duration() > Tolerance;
}
