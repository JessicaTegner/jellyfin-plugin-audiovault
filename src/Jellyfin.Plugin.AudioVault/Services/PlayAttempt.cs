using AudioVault.Core.Media;
using AudioVault.Core.Sources;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>What a HEAD request for a stream gets. HEAD only asks about the stream, so it never prepares anything.</summary>
public enum HeadAnswer
{
    /// <summary>The cached file's own headers (length, ranges).</summary>
    Ready,

    /// <summary>The headers of a stream that is still being written.</summary>
    Live,

    /// <summary>An MP4 of unknown length; nothing is downloaded or converted for it.</summary>
    Placeholder,
}

/// <summary>Decisions about a play attempt that don't need Jellyfin, kept apart so they can be tested.</summary>
public static class PlayAttempt
{
    public static HeadAnswer Head(bool cached, bool jobRunning) =>
        cached ? HeadAnswer.Ready : jobRunning ? HeadAnswer.Live : HeadAnswer.Placeholder;

    /// <summary>
    /// The account a ticketed stream request may download with: the user's account as it is now, if the ticket was
    /// issued for exactly that saving of it. <paramref name="stale"/> is true when the user has an account but the
    /// ticket belongs to an earlier one (removed and added again, or replaced, even with the same email).
    /// </summary>
    public static SourceAccount? AccountFor(PlaybackTicket ticket, SourceAccount? current, out bool stale)
    {
        stale = current is not null && !ticket.Authorizes(current);
        return stale ? null : ticket.Authorizes(current) ? current : null;
    }

    /// <summary>
    /// Whose intent an unticketed stream request (Jellyfin's own fetch of the .strm URL) may download with: only a
    /// server request, only a live intent of an account that is still current, and never while the only probe window
    /// open for the entry is information-only (nobody pressed play, so nothing may download). Outside any window
    /// this is the transcoder re-reading the URL during a play.
    /// </summary>
    public static PlaybackIntent? UnticketedIntent(bool serverAgent, PlaybackIntent? intent, Func<SourceAccount, bool> isCurrent, ProbeWindow window) =>
        serverAgent && intent is not null && window != ProbeWindow.Information && isCurrent(intent.Account) ? intent : null;

    /// <summary>The dialog text for a blocked play, or null when the play can go ahead.</summary>
    public static string? BlockedMessage(PreflightResult preflight, bool staleTicket, int used, int limit, DateTimeOffset now, string accountPageUrl) =>
        preflight.Status == PreflightStatus.Ok ? null
        : staleTicket && preflight.Status == PreflightStatus.NoAccount ? UserMessages.AccountChanged
        : UserMessages.Describe(preflight, used, limit, now, accountPageUrl);
}
