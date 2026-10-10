using AudioVault.Core.Catalog;
using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using AudioVault.Sources.AudioVault;
using Jellyfin.Plugin.AudioVault.Api;
using Jellyfin.Plugin.AudioVault.Services;
using Jellyfin.Plugin.AudioVault.Tasks;
using MediaBrowser.Common.Configuration;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        var s = serviceCollection;
        s.TryAddSingleton<PluginHealth>();
        s.AddSingleton<PluginSettings>();
        s.AddSingleton<UserNotifier>();
        s.AddHostedService<GuardedHostedService<UserNotifier>>();
        s.AddSingleton<AutoplayGuard>();
        s.AddSingleton(sp => new AccountStore(
            sp.GetRequiredService<PluginSettings>(),
            sp.GetRequiredService<IApplicationPaths>(),
            sp.GetRequiredService<ILogger<AccountStore>>())
        {
            // A deleted or disabled Jellyfin user's account downloads nothing, whatever tickets or intents name it.
            OwnerIsActive = userId => UserAccountWatcher.IsActive(sp.GetRequiredService<IUserManager>().GetUserById(userId)),
        });
        s.AddSingleton<SoftFailTracker>();
        s.AddHostedService<GuardedHostedService<SoftFailTracker>>();
        s.AddHttpClient("AudioVault.FlareSolverr", c => c.Timeout = TimeSpan.FromSeconds(90));

        // The only line that knows which source is in use.
        s.AddSingleton<IContentSource>(sp => new AudioVaultSource(
            () => sp.GetRequiredService<PluginSettings>().AudioVault(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("AudioVault.FlareSolverr"),
            sp.GetRequiredService<ILogger<AudioVaultSource>>()));

        s.AddSingleton(sp => new CatalogStore(StateDirectory(sp), sp.GetRequiredService<ILogger<CatalogStore>>()));
        s.AddSingleton(sp => new EpisodeCountStore(StateDirectory(sp), sp.GetRequiredService<ILogger<EpisodeCountStore>>()));
        s.AddSingleton(sp => new DownloadQuota(StateDirectory(sp)));
        s.AddSingleton<CatalogSyncService>(sp => new CatalogSyncService(
            sp.GetRequiredService<IContentSource>(),
            sp.GetRequiredService<CatalogStore>(),
            sp.GetRequiredService<ILogger<CatalogSyncService>>()));
        s.AddSingleton<LibraryWriter>();
        s.AddSingleton<LibrarySyncService>();
        s.TryAddSingleton<JellyfinTmdbRuntimeLookup>();
        s.AddSingleton<ISeasonEpisodeLookup>(sp => sp.GetRequiredService<JellyfinTmdbRuntimeLookup>());
        s.AddSingleton<LibraryScans>();
        s.AddSingleton<LibraryBridge>();
        s.AddSingleton<IMediaEvents>(sp => sp.GetRequiredService<LibraryBridge>());
        s.AddSingleton<PublicUrlMonitor>();
        s.AddHostedService<GuardedHostedService<PublicUrlMonitor>>();
        s.AddSingleton<DateAddedService>();
        s.AddHostedService<GuardedHostedService<DateAddedService>>();
        s.AddSingleton(sp => new CacheStore(
            () => sp.GetRequiredService<PluginSettings>().CachePath,
            sp.GetRequiredService<ILogger<CacheStore>>()));
        s.AddSingleton(sp => new FfmpegConverter(
            () => sp.GetRequiredService<IMediaEncoder>().EncoderPath,
            sp.GetRequiredService<ILogger<FfmpegConverter>>()));
        s.AddSingleton(sp => new MediaJobManager(
            sp.GetRequiredService<IContentSource>(),
            sp.GetRequiredService<CatalogStore>(),
            sp.GetRequiredService<CacheStore>(),
            sp.GetRequiredService<FfmpegConverter>(),
            sp.GetRequiredService<DownloadQuota>(),
            sp.GetRequiredService<EpisodeCountStore>(),
            sp.GetRequiredService<IMediaEvents>(),
            () => sp.GetRequiredService<PluginSettings>().Media(),
            sp.GetRequiredService<ILogger<MediaJobManager>>())
        {
            // A download only opens for an account some user still has saved, in the same generation.
            AccountIsCurrent = account => sp.GetRequiredService<AccountStore>().IsCurrent(account),
        });
        s.AddSingleton<PlayNotices>();

        s.AddSingleton<PlaybackIntents>(_ => new PlaybackIntents());
        s.AddSingleton<ProbeWindows>();
        // Runs for every Jellyfin API action; it resolves the plugin's services itself so their failures stay contained.
        s.AddSingleton<PlaybackIntentFilter>();
        s.Configure<MvcOptions>(o => o.Filters.AddService<PlaybackIntentFilter>());

        s.AddSingleton<IScheduledTask, FullCatalogSyncTask>();
        s.AddSingleton<IScheduledTask, RecentCatalogSyncTask>();
        s.AddSingleton<IScheduledTask, EpisodeListTask>();
        s.AddSingleton<IScheduledTask, FixDateAddedTask>();
        s.AddSingleton<StartupService>();
        s.AddHostedService<GuardedHostedService<StartupService>>();
        s.AddSingleton<UserAccountWatcher>();
        s.AddHostedService<GuardedHostedService<UserAccountWatcher>>();
        s.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
        s.AddSingleton<MaintenanceService>();
        s.AddHostedService<GuardedHostedService<MaintenanceService>>();
        s.AddSingleton<RepairCounts>();
        s.AddSingleton<RefreshRepairService>();
        s.AddHostedService<GuardedHostedService<RefreshRepairService>>();
        s.AddSingleton<IScheduledTask, RepairUnprocessedTask>();
        s.AddSingleton<EpisodeListScheduler>();
        s.AddHostedService<GuardedHostedService<EpisodeListScheduler>>();
        s.AddSingleton<PlaybackReach>();
        s.AddHostedService<GuardedHostedService<PlaybackReach>>();
        s.AddSingleton<SetupProgress>();
        s.AddHostedService<GuardedHostedService<SetupProgress>>();
        s.AddSingleton<CompatibilityCheck>();
        s.AddHostedService<GuardedHostedService<CompatibilityCheck>>();
    }

    /// <summary>The plugin's data folder; first makes sure damaged state files that get set aside are logged.</summary>
    private static string StateDirectory(IServiceProvider sp)
    {
        if (global::AudioVault.Core.StateFileRecovery.Quarantined is null)
        {
            var logger = sp.GetRequiredService<ILogger<ServiceRegistrator>>();
            global::AudioVault.Core.StateFileRecovery.Quarantined = (path, movedTo, ex) =>
                logger.LogWarning(ex, "AudioVault state file {Path} was damaged and has been moved to {MovedTo}", path, movedTo ?? "(could not be moved)");
        }

        return sp.GetRequiredService<PluginSettings>().DataDirectory;
    }
}
