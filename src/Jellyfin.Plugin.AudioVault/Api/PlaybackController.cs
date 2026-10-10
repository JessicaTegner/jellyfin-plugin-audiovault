using AudioVault.Core.Media;
using AudioVault.Core.Policy;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Api;

/// <summary>
/// Target of the .strm files. Anonymous because ffprobe and some clients fetch the URL without Jellyfin auth;
/// the HMAC signature in the query string is what authorises the request. Who may download is decided per request:
/// a client's URL carries a ticket naming the user (added in PlaybackInfo); Jellyfin's own fetches of the .strm URL
/// carry none and fall back to the one recorded play intent for the entry. HEAD only describes the stream and never
/// prepares anything, so it can't start a download.
/// </summary>
[ApiController]
[Route("AudioVault")]
public sealed class PlaybackController(
    MediaJobManager jobs,
    PlaybackIntents intents,
    ProbeWindows probeWindows,
    AccountStore accounts,
    CacheStore cache,
    UserNotifier notifier,
    SoftFailTracker softFails,
    PlayNotices notices,
    AutoplayGuard autoplay,
    StartupService startup,
    PluginSettings settings,
    PlaybackReach reach,
    ILogger<PlaybackController> logger) : ControllerBase
{
    [HttpGet("Stream/{entryId}")]
    [HttpHead("Stream/{entryId}")]
    [AllowAnonymous]
    public async Task<IActionResult> Stream(
        [FromRoute] string entryId,
        [FromQuery] int? ep,
        [FromQuery] string? sig,
        [FromQuery(Name = PlaybackTicket.QueryName)] string? ticket,
        CancellationToken ct)
    {
        if (!settings.Signer.Verify(entryId, ep, sig))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var playing = PlaybackTicket.Read(PluginSettings.Config.SigningSecret, ticket, entryId, ep, DateTimeOffset.UtcNow);
        if (playing is not null)
        {
            // The player reached this address.
            reach.Arrived(playing, $"{Request.Scheme}://{Request.Host}{Request.PathBase}");
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            return Head(entryId, ep);
        }

        PlaybackIntent? intent = null;
        PlaybackTarget target;
        try
        {
            // Half-written files from before a restart are cleared first, so they can't be mistaken for new ones.
            await startup.CleanupDone.WaitAsync(ct).ConfigureAwait(false);
            if (playing is not null)
            {
                // The user's account as it is now, and only the saving the ticket was issued for: a removed or replaced
                // account (even one saved again with the same email) downloads nothing.
                var account = PlayAttempt.AccountFor(playing, accounts.GetForPlayback(playing.UserId), out var stale);
                if (!autoplay.IsDecided(playing.PlaySessionId) && jobs.Preflight(entryId, ep, account).NeedsDownload)
                {
                    // The same barrier as in PlaybackInfo: a queue's stop may arrive just after this request.
                    await autoplay.WaitForStopAsync(playing.SessionId, playing.PlaySessionId, AutoplayGuard.StopWait, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                }

                // The player is actually fetching this version, so this is a real play: a blocked one gets the
                // placeholder and its dialog after the player closes, a slow season download is announced.
                var preflight = notices.Attend(entryId, ep, account, stale, playing.UserId, playing.SessionId, playing.ItemId, playing.VersionId, playing.PlaySessionId);
                if (preflight.Status == PreflightStatus.NotFound)
                {
                    // Already explained by Attend.
                    return NotFound();
                }

                if (preflight.Status == PreflightStatus.Held)
                {
                    StopHeld(playing.SessionId, playing.PlaySessionId);
                }

                // Checked again when a download would actually start: being allowed to play what was cached or
                // downloading isn't being allowed to start a download.
                var heldNow = false;
                target = await jobs.PrepareAsync(
                        entryId,
                        ep,
                        preflight.Status == PreflightStatus.Ok ? account : null,
                        ct,
                        () => !(heldNow = autoplay.Hold(playing.SessionId, playing.PlaySessionId, playing.ItemId, startsDownload: true, DateTimeOffset.UtcNow)))
                    .ConfigureAwait(false);
                if (heldNow)
                {
                    Explain(entryId, ep, playing, null, UserMessages.AutoplayHeld);
                    StopHeld(playing.SessionId, playing.PlaySessionId);
                }
            }
            else
            {
                // Jellyfin's own fetch: the one live intent of the entry, but never for an information-only probe.
                var window = probeWindows.State(entryId, ep);
                var server = StreamRequesters.IsServer(Request.Headers.UserAgent);
                var found = intents.Find(entryId, ep);
                if (server && window == ProbeWindow.None)
                {
                    // Jellyfin transcodes or relays this stream for the one attempt waiting for it, so that player won't
                    // fetch it itself. With no single attempt to pin it on, nothing is cancelled.
                    reach.Relayed(found?.PlaySessionId);
                }

                intent = PlayAttempt.UnticketedIntent(server, found, accounts.IsCurrent, window);

                // An intent without a PlaySessionId was registered only once its download was allowed (and reserved).
                var attempt = intent?.PlaySessionId;
                bool MayStart() => attempt is null || !autoplay.Hold(null, attempt, null, startsDownload: true, DateTimeOffset.UtcNow);
                target = window != ProbeWindow.None
                    ? await jobs.PrepareForProbeAsync(entryId, ep, intent?.Account, ct, MayStart).ConfigureAwait(false)
                    : await jobs.PrepareAsync(entryId, ep, intent?.Account, ct, MayStart).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (QuotaExceededException ex)
        {
            logger.LogWarning("{Message}", ex.Message);
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            Response.Headers.RetryAfter = "3600";
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (EntryNotFoundException ex)
        {
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            return NotFound(ex.Message);
        }
        catch (ConversionUnavailableException ex)
        {
            // A server problem, not AudioVault's; nothing was downloaded or counted.
            logger.LogError("{Message}", ex.Message);
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (SourceException ex)
        {
            logger.LogError(ex, "AudioVault source error for {Entry}", entryId);
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            logger.LogError(ex, "Downloading {Entry} from AudioVault failed", entryId);
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            return StatusCode(StatusCodes.Status502BadGateway, "Downloading from AudioVault failed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Disk problems, or ffmpeg missing or failing.
            logger.LogError(ex, "Preparing {Entry} failed", entryId);
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Preparing this title failed.");
        }

        // What this attempt got: the player's own request, or Jellyfin's fetch on behalf of the attempt it matched.
        var kind = target switch { ReadyTarget => ServedKind.Ready, PlaceholderTarget => ServedKind.Placeholder, _ => ServedKind.Live };
        if (playing is not null)
        {
            autoplay.Served(playing.SessionId, playing.PlaySessionId, entryId, ep, kind, DateTimeOffset.UtcNow);
        }
        else if (intent is not null)
        {
            autoplay.Served(null, intent.PlaySessionId, entryId, ep, kind, DateTimeOffset.UtcNow);
        }

        switch (target)
        {
            case ReadyTarget ready:
                return PhysicalFile(ready.Path, "video/mp4", enableRangeProcessing: true);
            case PlaceholderTarget placeholder:
                Response.Headers.CacheControl = "no-store";
                return PhysicalFile(placeholder.Path, "video/mp4", enableRangeProcessing: true);
        }

        var live = (LiveTarget)target;
        Response.ContentType = "video/mp4";
        Response.Headers.AcceptRanges = "none";
        Response.Headers.CacheControl = "no-store";

        try
        {
            await LiveFileReader.CopyAsync(live.Path, live.Completion, Response.Body, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away; the conversion keeps running and will be cached.
        }
        catch (IOException ex) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Live stream of {Entry} ended early", entryId);
        }
        catch (Exception ex)
        {
            // The conversion failed part way: end the connection abnormally so the player doesn't take the cut-off
            // file for the whole title. The job reports the failure to whoever waits for it.
            logger.LogWarning(ex, "Live stream of {Entry} failed", entryId);
            Explain(entryId, ep, playing, intent, UserMessages.Describe(ex));
            HttpContext.Abort();
        }

        return new EmptyResult();
    }

    /// <summary>Headers only, from what is already on disk or running; never prepares, downloads or converts.</summary>
    private IActionResult Head(string entryId, int? ep)
    {
        var final = cache.FinalPath(entryId, ep);
        var running = jobs.ActiveJobProgress().Any(j => j.EntryId == entryId && (j.Episode == ep || j.Episode is null));
        var answer = PlayAttempt.Head(System.IO.File.Exists(final), running);
        if (answer == HeadAnswer.Ready)
        {
            return PhysicalFile(final, "video/mp4", enableRangeProcessing: true);
        }

        Response.ContentType = "video/mp4";
        Response.Headers.CacheControl = "no-store";
        if (answer == HeadAnswer.Live)
        {
            Response.Headers.AcceptRanges = "none";
        }

        return new EmptyResult();
    }

    /// <summary>A held queue item that is already playing is stopped, which ends the queue; one not started yet is stopped on its start.</summary>
    private void StopHeld(string? sessionId, string? playSessionId)
    {
        if (sessionId is not null && autoplay.IsCurrent(sessionId, playSessionId))
        {
            notifier.StopPlayback(sessionId, () => autoplay.IsCurrent(sessionId, playSessionId));
        }
    }

    /// <summary>
    /// Queues the explanation for the player that asked; it is shown once that player has closed. The failed attempt
    /// authorises no further downloads: later probes or transcoder fetches of it get nothing new.
    /// </summary>
    private void Explain(string entryId, int? ep, PlaybackTicket? playing, PlaybackIntent? intent, string message)
    {
        if (playing is not null)
        {
            intents.Retire(playing.UserId, entryId, ep, playing.PlaySessionId);
        }
        else if (intent is not null)
        {
            intents.Retire(intent.UserId, entryId, ep, intent.PlaySessionId);
        }

        // The job's own failure event explains the same failure too; the session hears it once.
        var session = playing?.SessionId ?? notifier.TakeWatcher(entryId, ep);
        softFails.ExplainFailure(session, entryId, ep, message);
    }
}
