using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AudioVault.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string FlareSolverrUrl { get; set; } = string.Empty;

    /// <summary>Base URL written into .strm files; must be reachable by the server and by clients. Empty = detect.</summary>
    public string PublicServerUrl { get; set; } = string.Empty;

    /// <summary>The address an admin opened the settings page with, verified reachable from the server.</summary>
    public string DetectedServerUrl { get; set; } = string.Empty;

    public string MoviesPath { get; set; } = string.Empty;

    public string ShowsPath { get; set; } = string.Empty;

    public string CachePath { get; set; } = string.Empty;

    public string PreferredLanguage { get; set; } = "English";

    public bool IncludeOtherLanguages { get; set; } = true;

    public bool IncludeTts { get; set; } = true;

    /// <summary>Per AudioVault account.</summary>
    public int DailyDownloadLimit { get; set; } = 20;

    /// <summary>0 = unlimited.</summary>
    public int MaxCacheGb { get; set; }

    public int AudioBitrateKbps { get; set; } = 96;

    public string SigningSecret { get; set; } = string.Empty;

    /// <summary>Adds an "AudioVault account" link to the side menu of the Jellyfin web client.</summary>
    public bool ShowAccountMenuLink { get; set; } = true;

    /// <summary>Off by default: signing out ends "remember me" on every device using that AudioVault account.</summary>
    public bool SignOutDeletedUsers { get; set; }

    /// <summary>Evict idle cached titles when free disk space runs low, instead of refusing new plays.</summary>
    public bool EvictWhenLowOnSpace { get; set; } = true;
}
