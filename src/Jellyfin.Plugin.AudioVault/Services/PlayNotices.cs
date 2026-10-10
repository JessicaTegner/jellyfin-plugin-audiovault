using AudioVault.Core.Media;
using AudioVault.Core.Sources;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// What a confirmed play of one version sets in motion besides the stream: the dialog for a blocked play (shown once
/// the player has closed) or, when it can go ahead, watching the job for failures and announcing a slow season
/// download. Called only for real plays: PlaybackInfo with a play signal and a known version, or the ticketed
/// stream request itself. Repeated calls for the same attempt (players fetch a stream several times) are harmless.
/// A player's queue moving on by itself is held like a blocked play (see <see cref="AutoplayGuard"/>).
/// </summary>
public sealed class PlayNotices(
    MediaJobManager jobs,
    SoftFailTracker softFails,
    UserNotifier notifier,
    PluginSettings settings,
    PlaybackIntents intents,
    AutoplayGuard autoplay)
{
    public PreflightResult Attend(
        string entryId,
        int? episode,
        SourceAccount? account,
        bool staleTicket,
        Guid userId,
        string? sessionId,
        Guid? itemId,
        Guid? versionId,
        string? playSessionId = null)
    {
        var preflight = jobs.Preflight(entryId, episode, account);
        var message = PlayAttempt.BlockedMessage(
            preflight,
            staleTicket,
            account is null ? 0 : jobs.DownloadsUsed(account),
            jobs.DownloadLimit,
            DateTimeOffset.UtcNow,
            settings.AccountPageUrl);
        var startsDownload = message is null && preflight.NeedsDownload;
        if (autoplay.Hold(sessionId, playSessionId, itemId, startsDownload, DateTimeOffset.UtcNow))
        {
            message = UserMessages.AutoplayHeld;
            preflight = preflight with { Status = PreflightStatus.Held };
        }

        if (message is not null)
        {
            // A blocked attempt authorises nothing, not even Jellyfin's own fetches of the stream.
            intents.Retire(userId, entryId, episode, playSessionId);
            if (itemId is { } item && versionId is { } version)
            {
                softFails.Track(item, version, sessionId, playSessionId, userId, message);
            }
            else
            {
                softFails.NotifyAfterStop(sessionId, message);
            }

            return preflight;
        }

        softFails.Succeeded(itemId, versionId, sessionId, userId, playSessionId);
        notifier.Watch(entryId, episode, sessionId);
        if (preflight is { NeedsDownload: true, IsArchive: true, Entry: { } season })
        {
            notifier.Send(sessionId, UserMessages.Downloading(season), keepFor: UserNotifier.AnnouncementLifetime);
        }

        return preflight;
    }
}
