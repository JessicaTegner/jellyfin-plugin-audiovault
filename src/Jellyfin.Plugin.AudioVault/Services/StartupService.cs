using AudioVault.Core.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Clears half-written cache files and rebuilds the .strm index from the stored catalog. No network access.</summary>
public sealed class StartupService(MediaJobManager jobs, LibraryBridge library, ILogger<StartupService> logger) : IHostedService
{
    private readonly TaskCompletionSource _cleanupDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private Task _work = Task.CompletedTask;

    /// <summary>Completes once leftovers are gone; preparing media before then could have its new files deleted.</summary>
    public Task CleanupDone => _cleanupDone.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _work = Task.Run(
            async () =>
            {
                try
                {
                    // Only leftovers from before this start, so jobs started meanwhile keep their files.
                    await jobs.CleanupStaleAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AudioVault cache cleanup failed");
                }
                finally
                {
                    _cleanupDone.TrySetResult();
                }

                try
                {
                    await library.SyncLibraryAsync(scan: false, _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AudioVault startup library sync failed");
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
