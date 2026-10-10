using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Plain-language text for messages shown to the person pressing play.</summary>
public static class UserMessages
{
    public static string Describe(PreflightResult result, int used, int limit, DateTimeOffset now, string accountPageUrl) => result.Status switch
    {
        PreflightStatus.NotFound => "This title is no longer on AudioVault, so it can't be played.",
        PreflightStatus.NoAccount =>
            "This title has to be downloaded from AudioVault first, and you haven't added your AudioVault account yet. " +
            $"To add it, open the {MessageLinks.Link("AudioVault account page", accountPageUrl)} in a web browser where you're signed in to Jellyfin. " +
            "Titles that have been played before still work.",
        PreflightStatus.SourceNotReady =>
            $"This title has to be downloaded from AudioVault first, which isn't possible right now. {result.Problem}" +
            (IsLoginProblem(result.Problem) ? $" Update your login on the {MessageLinks.Link("AudioVault account page", accountPageUrl)}." : string.Empty),
        PreflightStatus.QuotaExhausted =>
            $"Your daily AudioVault download limit is used up ({used} of {limit}). " +
            $"The next download is possible {Relative(result.NextDownloadAt!.Value - now)}. Titles you've played before still work.",
        PreflightStatus.ConversionUnavailable => ConversionUnavailable,
        _ => string.Empty,
    };

    /// <summary>Problems the user can fix themselves on the account page (texts from the AudioVault source).</summary>
    private static bool IsLoginProblem(string? problem) =>
        problem is not null
        && (problem.Contains("sign-in has expired", StringComparison.OrdinalIgnoreCase)
            || problem.Contains("rejected the email and password", StringComparison.OrdinalIgnoreCase)
            || problem.Contains("No AudioVault account", StringComparison.OrdinalIgnoreCase));

    /// <summary>ffmpeg can't run on the server; nothing was downloaded. The details are on the status page.</summary>
    public const string ConversionUnavailable =
        "Jellyfin can't convert AudioVault titles right now because its ffmpeg encoder isn't available. Ask your server admin to check the AudioVault status page.";

    /// <summary>The player still had a link from before the user's AudioVault account was changed on this server.</summary>
    public const string AccountChanged =
        "Your AudioVault account was changed on this server after this title was opened, so it wasn't downloaded. Play the title again to use the current account.";

    public static string Downloading(SourceEntry entry) =>
        $"Downloading {entry.Title} season {entry.Season ?? 1} from AudioVault. " +
        "Playback starts once the whole season has downloaded, usually within a few minutes. Later episodes will then start straight away.";

    /// <summary>The same text for every title a queue moved on to, so a long queue is explained once.</summary>
    public const string AutoplayHeld =
        "The player moved on to the next title by itself, so playback was stopped. That title isn't downloaded yet, and " +
        "downloading it would use one of your daily AudioVault downloads. To download it, play it yourself.";

    public static string Describe(Exception error) => error switch
    {
        QuotaExceededException => "The daily AudioVault download limit is used up. Titles you've played before still work.",
        ConversionUnavailableException => ConversionUnavailable,
        SourceFormatChangedException => "AudioVault's website has changed and the AudioVault plugin needs an update before new titles can be downloaded.",
        SourceException source => $"Downloading from AudioVault failed. {source.Message}",
        EntryNotFoundException => "This episode isn't in the season AudioVault provided.",
        HttpRequestException or IOException => "The connection to AudioVault dropped during the download. Try playing again.",
        TimeoutException or OperationCanceledException => "AudioVault did not answer in time. Try playing again.",
        UnauthorizedAccessException => "The server could not write to its AudioVault cache folder. Ask your server admin to check the folder settings and the Jellyfin log.",
        System.ComponentModel.Win32Exception => "The server could not start ffmpeg, which is needed to prepare titles. Ask your server admin to check the Jellyfin log.",
        InvalidOperationException => "Converting this title failed. Try playing again; if it keeps failing, ask your server admin to check the Jellyfin log.",
        _ => "Preparing this title failed. Try playing again; if it keeps failing, the Jellyfin log has details.",
    };

    public static string Relative(TimeSpan span) => span switch
    {
        { TotalMinutes: < 2 } => "in a minute or two",
        { TotalMinutes: < 60 } => $"in about {Math.Ceiling(span.TotalMinutes / 5) * 5:0} minutes",
        { TotalHours: < 1.5 } => "in about an hour",
        _ => $"in about {Math.Round(span.TotalHours):0} hours",
    };
}
