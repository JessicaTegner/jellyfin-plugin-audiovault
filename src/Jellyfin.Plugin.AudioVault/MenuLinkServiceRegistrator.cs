using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AudioVault;

/// <summary>Jellyfin invokes every IPluginServiceRegistrator in a plugin's assembly, inside the host's ConfigureServices.</summary>
public sealed class MenuLinkServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) =>
        serviceCollection.AddTransient<IStartupFilter, AccountMenuLinkStartupFilter>();
}
