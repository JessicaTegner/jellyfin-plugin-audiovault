using AudioVault.Core.Library;
using AudioVault.Core.Media;
using AudioVault.Sources.AudioVault;
using Jellyfin.Plugin.AudioVault.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Extensions;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Translates the Jellyfin plugin configuration into the option types Core and the source expect.</summary>
public sealed class PluginSettings(
    IApplicationPaths paths,
    IServerApplicationHost host,
    IConfigurationManager configurationManager,
    IConfiguration startupConfig)
{
    public static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("AudioVault plugin is not loaded");

    public string DataDirectory => Path.Combine(paths.DataPath, "audiovault");

    public string MoviesPath => Folders.Folders.Movies;

    public string ShowsPath => Folders.Folders.Shows;

    public string CachePath => Folders.Folders.Cache;

    /// <summary>The folders in use; a configured folder that isn't safe to delete files in is replaced by the default.</summary>
    public FolderChoice Folders => FolderRules.Choose(Config.MoviesPath, Config.ShowsPath, Config.CachePath, FolderContextFor(paths));

    public static FolderContext FolderContextFor(IApplicationPaths paths)
    {
        var data = Path.Combine(paths.DataPath, "audiovault");
        string[] contains =
        [
            paths.ProgramDataPath, paths.DataPath, paths.CachePath, data,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        string[] forbidden =
        [
            paths.ConfigurationDirectoryPath, paths.LogDirectoryPath, paths.WebPath, paths.ProgramSystemPath,
            paths.PluginsPath, paths.TempDirectory, paths.ImageCachePath,
        ];
        return new FolderContext(
            new FolderSet(
                Path.Combine(data, "library", "movies"),
                Path.Combine(data, "library", "shows"),
                Path.Combine(paths.CachePath, "audiovault")),
            [.. contains.Where(p => !string.IsNullOrEmpty(p))],
            [.. forbidden.Where(p => !string.IsNullOrEmpty(p))]);
    }

    /// <summary>The address written into playback links, and where it came from.</summary>
    public PublicUrlChoice PublicUrl =>
        PublicUrlRules.Choose(Config.PublicServerUrl, Published(), Config.DetectedServerUrl, host.GetApiUrlForLocalAccess(null, false));

    /// <summary>Every address configured or detected for this server, to suggest instead of one only some devices reach.</summary>
    public IReadOnlyList<string> KnownAddresses
    {
        get
        {
            var (everyone, some) = PublicUrlRules.PublishedUrls(Published());
            return [.. everyone, .. some, Config.DetectedServerUrl ?? string.Empty, Config.PublicServerUrl ?? string.Empty];
        }
    }

    private PublishedUriSettings Published()
    {
        var network = configurationManager.GetNetworkConfiguration();
        return new PublishedUriSettings(
            startupConfig[ConfigurationExtensions.AddressOverrideKey],
            network.PublishedServerUriBySubnet ?? [],
            network.BaseUrl,
            host.ListenWithHttps,
            host.HttpPort,
            host.HttpsPort);
    }

    public string PublicServerUrl => PublicUrl.Url;

    public bool HasExplicitPublicUrl => !string.IsNullOrWhiteSpace(Config.PublicServerUrl);

    /// <summary>Why players on other devices can't use the public URL, or null if it looks fine.</summary>
    public string? PublicUrlProblem => PublicUrl.Problem;

    public UrlSigner Signer => new(Config.SigningSecret);

    public LibraryOptions Library() => new()
    {
        MoviesPath = MoviesPath,
        ShowsPath = ShowsPath,
        PreferredLanguage = Or(Config.PreferredLanguage, "English"),
        IncludeOtherLanguages = Config.IncludeOtherLanguages,
        IncludeTts = Config.IncludeTts,
    };

    public AudioVaultOptions AudioVault() => new()
    {
        FlareSolverrUrl = string.IsNullOrWhiteSpace(Config.FlareSolverrUrl) ? null : Config.FlareSolverrUrl.Trim(),
        StateDirectory = DataDirectory,
    };

    public MediaOptions Media() => new(
        Math.Clamp(Config.DailyDownloadLimit, 1, 25),
        Math.Max(0, Config.MaxCacheGb) * 1024L * 1024 * 1024,
        new ConversionSettings(AudioBitrateKbps: Math.Clamp(Config.AudioBitrateKbps, 32, 256)),
        EvictWhenLowOnSpace: Config.EvictWhenLowOnSpace);

    public string StreamUrl(string entryId, int? episode)
    {
        var url = $"{PublicServerUrl}/AudioVault/Stream/{Uri.EscapeDataString(entryId)}?sig={Signer.Sign(entryId, episode)}";
        return episode is { } e ? $"{url}&ep={e}" : url;
    }

    public string AccountPageUrl => $"{PublicServerUrl}/AudioVault/Account";

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
