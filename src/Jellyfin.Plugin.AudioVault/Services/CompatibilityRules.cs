namespace Jellyfin.Plugin.AudioVault.Services;

public enum MenuLinkCheck
{
    /// <summary>The link is turned off on the settings page.</summary>
    Off,
    Ok,
    NotReached,
    NoWebClient,
    UnexpectedFormat,
}

/// <param name="PlaybackFilterReached">Whether Jellyfin ran the plugin's MVC filter; null while that can't be told yet.</param>
/// <param name="LibraryScanTask">Whether Jellyfin's library scan task has its usual key; null while tasks aren't known.</param>
public sealed record CompatibilityFacts(
    string JellyfinVersion,
    InternalsProbe Internals,
    bool? PlaybackFilterReached,
    MenuLinkCheck MenuLink,
    bool? LibraryScanTask);

/// <param name="Severe">New titles can't be played at all.</param>
public sealed record CompatibilityIssue(string Key, string Message, bool Severe);

/// <summary>What still works when a Jellyfin internal the plugin relies on doesn't bind. No Jellyfin types.</summary>
public static class CompatibilityRules
{
    public const string StepKey = "compatibility";
    public const string StepTitle = "Jellyfin compatibility";

    private const string AccountPageNote = "Users can still open the account page at /AudioVault/Account on this server.";

    public static IReadOnlyList<CompatibilityIssue> Issues(CompatibilityFacts f)
    {
        var v = f.JellyfinVersion;
        var p = f.Internals;
        var issues = new List<CompatibilityIssue>();
        if (!p.PlaybackInfoAction || f.PlaybackFilterReached == false)
        {
            var why = f.PlaybackFilterReached == false
                ? $"Jellyfin {v} doesn't run the plugin's playback hook"
                : $"The plugin can't find Jellyfin's playback request on this Jellyfin version ({v})";
            issues.Add(new("playback", why + ", so pressing play can't start a download: only titles already in the cache play, and others play a short silence.", true));
        }
        else if (!p.PlayStartSignal)
        {
            issues.Add(new(
                "play-start",
                $"On this Jellyfin version ({v}) the plugin can't tell when a player starts playback. Titles still download when the player fetches them, but their length shows only after the first play, and a title Jellyfin has to transcode plays a short silence until it has been played directly once.",
                false));
        }

        if (!p.TmdbClient)
        {
            issues.Add(new(
                "tmdb",
                $"Jellyfin's TMDb client can't be used on this version ({v}): titles nobody has played show no length, and episode lists use the slower per-episode lookup.",
                false));
        }
        else
        {
            if (!p.TmdbSeasons)
            {
                issues.Add(new("tmdb-seasons", $"Fast episode lists aren't available on this Jellyfin version ({v}); using the slower per-episode lookup.", false));
            }

            if (!p.TmdbImageLanguages)
            {
                issues.Add(new(
                    "tmdb-images",
                    $"On this Jellyfin version ({v}) TMDb lookups can't ask for the image languages Jellyfin uses; runtimes and episode lists still work, but some artwork may be missing or in another language.",
                    false));
            }
        }

        if (!p.SessionMessages)
        {
            issues.Add(new(
                "messages",
                $"On this Jellyfin version ({v}) the plugin can't show messages in players or stop a player's queue, so a play that is blocked (for example without an AudioVault login) plays a short silence without a dialog. The settings page and the server log still say what happened.",
                false));
        }

        if (f.LibraryScanTask == false)
        {
            issues.Add(new(
                "scan-task",
                $"Jellyfin's library scan task isn't where the plugin expects it on this version ({v}): the setup checklist can't show scan progress, and the automatic episode-list and repair passes may run during a scan.",
                false));
        }

        var menu = f.MenuLink switch
        {
            MenuLinkCheck.NotReached => $"The \"AudioVault account\" link can't be added to the web client's side menu on this Jellyfin version ({v}).",
            MenuLinkCheck.UnexpectedFormat => $"The web client's config.json isn't in the format the plugin expects on this Jellyfin version ({v}), so the \"AudioVault account\" side-menu link can't be added.",
            MenuLinkCheck.NoWebClient => "This server doesn't serve the web client itself, so the \"AudioVault account\" link can't be added to its side menu.",
            _ => null,
        };
        if (menu is null && !p.WebPath && f.MenuLink != MenuLinkCheck.Off)
        {
            menu = $"The web client's folder can't be found on this Jellyfin version ({v}), so the \"AudioVault account\" side-menu link can't be added.";
        }

        if (menu is not null)
        {
            issues.Add(new("menu-link", menu + " " + AccountPageNote, false));
        }

        return issues;
    }

    public static SetupStep Step(CompatibilityFacts f)
    {
        var issues = Issues(f);
        if (issues.Count == 0)
        {
            return new SetupStep(StepKey, StepTitle, SetupState.Done, $"Jellyfin {f.JellyfinVersion}: everything the plugin uses from Jellyfin is available.");
        }

        var detail = string.Join(' ', issues.Select(i => i.Message));
        return issues.Any(i => i.Severe)
            ? new SetupStep(StepKey, StepTitle, SetupState.Problem, detail + " Check for a plugin update for this Jellyfin version.")
            : new SetupStep(StepKey, StepTitle, SetupState.Done, $"Jellyfin {f.JellyfinVersion} works, with limits. " + detail);
    }

    /// <summary>The menu link check from what the server found; Jellyfin-free so it can be tested.</summary>
    public static MenuLinkCheck MenuLink(bool enabled, bool? middlewareReached, bool configFound, bool configUsable) =>
        !enabled ? MenuLinkCheck.Off
        : middlewareReached == false ? MenuLinkCheck.NotReached
        : !configFound ? MenuLinkCheck.NoWebClient
        : !configUsable ? MenuLinkCheck.UnexpectedFormat
        : MenuLinkCheck.Ok;
}

/// <summary>Set by the plugin's hooks when Jellyfin runs them, so the compatibility check can tell they are wired in.</summary>
public static class HookSignals
{
    private static volatile bool _filter;
    private static volatile bool _middleware;

    public static bool PlaybackFilterReached => _filter;

    public static bool MenuLinkMiddlewareReached => _middleware;

    public static void FilterRan() => _filter = true;

    public static void MiddlewareRan() => _middleware = true;
}
