using AudioVault.Core.Library;
using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AudioVault.Tasks;

public sealed class FillRuntimesTask(RuntimeEstimateService runtimes, LibraryBridge library, LibrarySyncService librarySync) : IScheduledTask
{
    public string Name => "Fill in runtimes";

    public string Key => "AudioVaultFillRuntimes";

    public string Description =>
        "Looks up runtimes on TMDb for AudioVault titles that haven't been played yet, so their details show a length. New items get it automatically.";

    public string Category => "AudioVault";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (librarySync.IndexedFiles == 0)
        {
            await library.SyncLibraryAsync(scan: false, cancellationToken).ConfigureAwait(false);
        }

        await runtimes.FillAllAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
