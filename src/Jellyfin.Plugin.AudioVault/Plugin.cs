using Jellyfin.Plugin.AudioVault.Configuration;
using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AudioVault;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        if (string.IsNullOrEmpty(Configuration.SigningSecret))
        {
            Configuration.SigningSecret = global::AudioVault.Core.Library.UrlSigner.NewSecret();
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "AudioVault";

    public override Guid Id => Guid.Parse("4a6f2c1e-7b0d-4c5e-9a43-2f8b6d1e0a57");

    public override string Description => "Browse and play audio-described movies and shows from AudioVault on demand.";

    /// <summary>Why folders entered on the settings page at the last save were not used, or null.</summary>
    public string? FolderProblem { get; private set; }

    /// <summary>Keeps the previous value of any folder that isn't safe for the plugin to delete files in.</summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration next)
        {
            var check = FolderRules.Choose(next.MoviesPath, next.ShowsPath, next.CachePath, PluginSettings.FolderContextFor(ApplicationPaths));
            var previous = Configuration;
            next.MoviesPath = Keep(next.MoviesPath, previous.MoviesPath, check.Folders.Movies);
            next.ShowsPath = Keep(next.ShowsPath, previous.ShowsPath, check.Folders.Shows);
            next.CachePath = Keep(next.CachePath, previous.CachePath, check.Folders.Cache);
            FolderProblem = check.Problems.Count == 0
                ? null
                : string.Join(' ', check.Problems) + " The previous folder setting was kept.";
        }

        base.UpdateConfiguration(configuration);
    }

    private static string Keep(string entered, string previous, string usable) =>
        string.IsNullOrWhiteSpace(entered) || string.Equals(FolderRules.Normalize(entered), usable, StringComparison.Ordinal)
            ? entered
            : previous;

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        },
    ];
}
