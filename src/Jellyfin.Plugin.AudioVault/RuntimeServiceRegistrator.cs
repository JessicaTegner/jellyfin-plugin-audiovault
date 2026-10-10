using Jellyfin.Plugin.AudioVault.Services;
using Jellyfin.Plugin.AudioVault.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.AudioVault;

/// <summary>Runtime estimates for titles that haven't been downloaded yet.</summary>
public sealed class RuntimeServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton<PluginHealth>();
        // One adapter, shared with the episode-list lookups (ServiceRegistrator), so it binds and warns once.
        serviceCollection.TryAddSingleton<JellyfinTmdbRuntimeLookup>();
        serviceCollection.AddSingleton<IRuntimeLookup>(sp => sp.GetRequiredService<JellyfinTmdbRuntimeLookup>());
        serviceCollection.AddSingleton<RuntimeEstimateService>();
        serviceCollection.AddHostedService<GuardedHostedService<RuntimeEstimateService>>();
        serviceCollection.AddSingleton<IScheduledTask, FillRuntimesTask>();
    }
}
