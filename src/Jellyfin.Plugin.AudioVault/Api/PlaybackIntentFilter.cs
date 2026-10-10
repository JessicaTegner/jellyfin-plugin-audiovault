using System.Globalization;
using AudioVault.Core.Media;
using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AudioVault.Api;

/// <summary>
/// Hooks Jellyfin's PlaybackInfo call. Jellyfin probes every version of a .strm item inside that call; afterwards
/// this adds a ticket naming the user to each of our stream URLs in the response (the client's own request then says
/// whose account may download). Only when the client says it is starting playback of a known version does it run cheap
/// preflight checks, announce slow season downloads and, for requests that carry no ticket (Jellyfin's transcoder
/// re-reads the .strm file), record a per-user intent. Otherwise that waits for the ticketed stream request.
/// A failed check is a soft fail: no intent is recorded, so the client plays the short silent placeholder while
/// our dialog explains why. Returning a PlaybackInfo error instead makes jellyfin-web show two generic dialogs.
/// Registered for every MVC action of Jellyfin, so it resolves its services lazily and never lets its own failure
/// break a request.
/// </summary>
public sealed class PlaybackIntentFilter(IServiceProvider services, ILogger<PlaybackIntentFilter> logger) : IAsyncActionFilter
{
    private const string StreamMarker = "/AudioVault/Stream/";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        HookSignals.FilterRan();
        switch ((context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName)
        {
            case "MediaInfo":
                await PlaybackInfoAsync(context, next).ConfigureAwait(false);
                return;
            case "Playstate":
                await PlaystateAsync(context, next).ConfigureAwait(false);
                return;
            case "DynamicHls" or "Videos" or "Audio" or "UniversalAudio" or "HlsSegment":
                NoteRelayed(context);
                break;
        }

        await next().ConfigureAwait(false);
    }

    private async Task PlaybackInfoAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        Scope? scope = null;
        IDisposable? window = null;
        try
        {
            scope = new Scope(services);
            var probed = ProbedEntries(scope, context);
            if (probed is { Count: > 0 })
            {
                // Only a play's window lets Jellyfin's probes download; an information request's never does.
                var play = PlaybackSignals.IsPlay(context);
                if (play)
                {
                    await RegisterEarlyIntentAsync(scope, context, probed).ConfigureAwait(false);
                }

                window = scope.ProbeWindows.Open(probed, play);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "AudioVault could not prepare a playback info request; passing it through");
        }

        ActionExecutedContext executed;
        using (window)
        {
            executed = await next().ConfigureAwait(false);
        }

        if (scope is null)
        {
            return;
        }

        try
        {
            await AfterAsync(scope, context, executed).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "AudioVault could not handle a playback info response; passing it through");
        }
    }

    /// <summary>
    /// A player's start or stop report, read before Jellyfin handles it: Jellyfin raises its own playback events later
    /// and asynchronously, after the player's next request may already have been handled.
    /// </summary>
    private async Task PlaystateAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        Scope? scope = null;
        Caller? caller = null;
        PlaystateReport? report = null;
        try
        {
            report = PlaystateReport.Read((context.ActionDescriptor as ControllerActionDescriptor)?.ActionName, context.ActionArguments);
            if (report is not null)
            {
                scope = new Scope(services);
                caller = await scope.Notifier.ResolveCallerAsync(context.HttpContext).ConfigureAwait(false);
                BeforePlaystate(scope, caller, report);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AudioVault could not read a playback report; passing it through");
        }

        await next().ConfigureAwait(false);

        if (report is { Stop: false } && scope is not null && caller?.SessionId is { } session)
        {
            try
            {
                // A held queue item: stopping the player ends the queue, so it doesn't move on to the next title.
                // Checked again just before sending, so a newer play the user started meanwhile isn't stopped.
                var playSessionId = report.PlaySessionId;
                var autoplay = scope.Autoplay;
                if (autoplay.IsHeld(playSessionId))
                {
                    scope.Notifier.StopPlayback(session, () => autoplay.IsCurrent(session, playSessionId));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AudioVault could not stop a held play");
            }
        }
    }

    private static void BeforePlaystate(Scope scope, Caller caller, PlaystateReport report)
    {
        var now = DateTimeOffset.UtcNow;
        var playSessionId = string.IsNullOrEmpty(report.PlaySessionId) ? null : report.PlaySessionId;
        if (!report.Stop)
        {
            scope.Autoplay.Started(caller.SessionId, playSessionId, report.ItemId == Guid.Empty ? null : report.ItemId, now);
            scope.Reach.Started(playSessionId, report.PlayMethod == PlayMethod.DirectPlay, scope.Autoplay.IsHeld(playSessionId));
            return;
        }

        var stream = StoppedStream(scope, report);
        var cached = stream is { } s && File.Exists(scope.Cache.FinalPath(s.EntryId, s.Episode));
        var position = report.PositionTicks is { } ticks ? TimeSpan.FromTicks(ticks) : (TimeSpan?)null;
        var runtime = report.ItemId == Guid.Empty ? null : scope.LibraryManager.GetItemById(report.ItemId)?.RunTimeTicks;
        scope.Autoplay.StopReported(
            caller.SessionId,
            new StopReport(
                playSessionId,
                stream,
                position,
                runtime is { } length ? TimeSpan.FromTicks(length) : null,
                report.Failed,
                report.NextMediaType,
                report.Queue,
                cached),
            now);
        scope.Reach.Stopped(playSessionId, report.Failed);

        // The stopped attempt authorises no more downloads; Jellyfin's own stop event does the same, later.
        if (caller.UserId != Guid.Empty)
        {
            Guid[] items = [.. new[] { report.ItemId, Guid.TryParse(report.MediaSourceId, out var version) ? version : Guid.Empty }.Where(g => g != Guid.Empty)];
            scope.Intents.RetireStopped(playSessionId, items, [caller.UserId]);
        }
    }

    /// <summary>The stream of the version that stopped, if it is one of ours.</summary>
    private static (string EntryId, int? Episode)? StoppedStream(Scope scope, PlaystateReport report)
    {
        var path = Guid.TryParse(report.MediaSourceId, out var version) && scope.LibraryManager.GetItemById(version) is { Path: { } versionPath }
            ? versionPath
            : report.ItemId == Guid.Empty ? null : scope.LibraryManager.GetItemById(report.ItemId)?.Path;
        return path is null ? null : ReadEntry(path);
    }

    /// <summary>Jellyfin streams this attempt itself (transcoding or relaying), so the player won't fetch our link.</summary>
    private void NoteRelayed(ActionExecutingContext context)
    {
        try
        {
            if (context.ActionArguments.TryGetValue("playSessionId", out var value) && value is string { Length: > 0 } playSessionId)
            {
                services.GetRequiredService<PlaybackReach>().Relayed(playSessionId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not note a stream relayed by Jellyfin");
        }
    }

    private static async Task AfterAsync(Scope scope, ActionExecutingContext context, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: PlaybackInfoResponse response } || response.MediaSources.Count == 0)
        {
            return;
        }

        var ours = response.MediaSources.Where(m => TryParse(m, out _, out _)).ToList();
        if (ours.Count == 0)
        {
            return;
        }

        var caller = await scope.Notifier.ResolveCallerAsync(context.HttpContext).ConfigureAwait(false);
        var account = caller.UserId == Guid.Empty ? null : scope.Accounts.GetForPlayback(caller.UserId);
        scope.SoftFails.Issued(response.PlaySessionId);
        if (PlaybackSignals.IsPlay(context))
        {
            scope.Autoplay.Issued(caller.SessionId, response.PlaySessionId, DateTimeOffset.UtcNow);
        }

        Guid? itemId = context.ActionArguments.TryGetValue("itemId", out var item) && item is Guid id ? id : null;
        if (caller.UserId != Guid.Empty)
        {
            var secret = PluginSettings.Config.SigningSecret;
            var expires = DateTimeOffset.UtcNow + PlaybackTicket.Lifetime;
            foreach (var source in ours)
            {
                TryParse(source, out var entryId, out var ep);
                var token = new PlaybackTicket(caller.UserId, account?.Key ?? string.Empty, entryId, ep, caller.SessionId, expires)
                {
                    Generation = account?.Generation ?? 0,
                    ItemId = itemId,
                    VersionId = Guid.TryParse(source.Id, out var versionId) ? versionId : null,
                    PlaySessionId = response.PlaySessionId,
                }.Create(secret);
                source.Path = PlaybackTicket.WithPlaySession(PlaybackTicket.AddTo(source.Path, token), response.PlaySessionId);
            }

            scope.Reach.Issued(
                caller,
                response.PlaySessionId,
                ours[0].Path,
                ours.Select(m => TryParse(m, out var e, out var n) ? (e, n) : default),
                PlaybackSignals.IsPlay(context) && PlaybackSignals.DirectPlayDisabled(context.ActionArguments));
        }

        // Information requests (detail pages, version lists) and plays whose version isn't known yet stop here. Their
        // dialogs and announcements wait for the ticketed stream request, which names the version actually played.
        if (!PlaybackSignals.IsPlay(context) || SelectedSource(context, response) is not { } chosen
            || !TryParse(chosen, out var chosenEntry, out var chosenEpisode))
        {
            return;
        }

        Guid? chosenVersion = Guid.TryParse(chosen.Id, out var v) ? v : null;
        if (scope.Jobs.Preflight(chosenEntry, chosenEpisode, account).NeedsDownload)
        {
            // A queue moving on may have sent the stop of its last play just before this request; see it first.
            await scope.Autoplay.WaitForStopAsync(caller.SessionId, response.PlaySessionId, AutoplayGuard.StopWait, DateTimeOffset.UtcNow, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }

        var preflight = scope.Notices.Attend(
            chosenEntry,
            chosenEpisode,
            account,
            staleTicket: false,
            caller.UserId,
            caller.SessionId,
            itemId,
            chosenVersion,
            response.PlaySessionId);

        // Only for Jellyfin's own fetches of the .strm URL (transcoding, relaying); clients carry the ticket. Bound to
        // this attempt, so its stop or failure retires it.
        if (preflight.Status == PreflightStatus.Ok && account is not null)
        {
            scope.Intents.Register(chosenEntry, chosenEpisode, caller.UserId, account, response.PlaySessionId, [.. new[] { itemId, chosenVersion }.OfType<Guid>()]);
        }

        // A direct play of a play that goes ahead: the player itself should now fetch the ticketed link, once it shows
        // it tried. Held or blocked attempts are left out; their players may be stopped before they fetch anything.
        if (preflight.Status == PreflightStatus.Ok && chosen.SupportsDirectPlay)
        {
            scope.Reach.Prepared(response.PlaySessionId);
        }
    }

    /// <summary>The version the client is playing: the one it named, or the only one there is.</summary>
    private static MediaSourceInfo? SelectedSource(ActionExecutingContext context, PlaybackInfoResponse response)
    {
        var requested = RequestedMediaSourceId(context);
        return !string.IsNullOrEmpty(requested)
            ? response.MediaSources.FirstOrDefault(m => string.Equals(m.Id, requested, StringComparison.OrdinalIgnoreCase))
            : response.MediaSources.Count == 1 ? response.MediaSources[0] : null;
    }

    private static string? RequestedMediaSourceId(ActionExecutingContext context)
    {
        var requested = context.ActionArguments.TryGetValue("mediaSourceId", out var q) ? q as string : null;
        return string.IsNullOrEmpty(requested)
            // The posted PlaybackInfoDto lives in Jellyfin.Api, which plugins can't reference.
            ? context.ActionArguments.Values
                .Select(v => v?.GetType().GetProperty("MediaSourceId")?.GetValue(v) as string)
                .FirstOrDefault(v => !string.IsNullOrEmpty(v))
            : requested;
    }

    /// <summary>
    /// Jellyfin probes inside PlaybackInfo, before this filter can see which version the client picks. When the client
    /// is starting playback and the choice is unambiguous (one version, or the client named it), record the intent first
    /// so that probe starts the download and Jellyfin learns the real length on the very first play. Seasons are
    /// skipped: their whole archive must download before anything is known, which would hold up PlaybackInfo for minutes.
    /// </summary>
    private static async Task RegisterEarlyIntentAsync(Scope scope, ActionExecutingContext context, IReadOnlyList<(string EntryId, int? Episode)> probed)
    {
        (string EntryId, int? Episode)? target = null;
        var requested = RequestedMediaSourceId(context);
        if (!string.IsNullOrEmpty(requested))
        {
            if (Guid.TryParse(requested, out var versionId)
                && scope.LibraryManager.GetItemById(versionId) is { Path: { } versionPath }
                && ReadEntry(versionPath) is { } chosen
                && probed.Contains(chosen))
            {
                target = chosen;
            }
        }
        else if (probed.Count == 1)
        {
            target = probed[0];
        }

        if (target is not { } t)
        {
            return;
        }

        var caller = await scope.Notifier.ResolveCallerAsync(context.HttpContext).ConfigureAwait(false);
        if (caller.UserId == Guid.Empty
            || scope.Accounts.GetForPlayback(caller.UserId) is not { } account
            || scope.Jobs.Preflight(t.EntryId, t.Episode, account) is not { Status: PreflightStatus.Ok } preflight
            || !preflight.NeedsDownload
            || preflight.IsArchive)
        {
            // Only a download needs the intent; what is cached or already downloading plays without one.
            return;
        }

        // A queue moving on may have sent the stop of its last play just before this request; see it first.
        await scope.Autoplay.WaitForStopAsync(caller.SessionId, null, AutoplayGuard.StopWait, DateTimeOffset.UtcNow, context.HttpContext.RequestAborted).ConfigureAwait(false);
        Guid? itemId = context.ActionArguments.TryGetValue("itemId", out var item) && item is Guid id ? id : null;
        if (scope.Autoplay.ReserveEarly(caller.SessionId, itemId, DateTimeOffset.UtcNow))
        {
            // Its PlaySessionId isn't issued yet; the response adds it. Until then the stop is matched by user and item.
            Guid? versionId = Guid.TryParse(requested, out var version) ? version : null;
            scope.Intents.Register(t.EntryId, t.Episode, caller.UserId, account, null, [.. new[] { itemId, versionId }.OfType<Guid>()]);
        }
    }

    internal static (string EntryId, int? Episode)? ReadEntry(string strmPath)
    {
        try
        {
            return strmPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                && TryParse(File.ReadAllText(strmPath).Trim(), out var entryId, out var episode)
                ? (entryId, episode)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>While Jellyfin builds playback info it probes every version; these are the entries it will probe.</summary>
    private static List<(string EntryId, int? Episode)>? ProbedEntries(Scope scope, ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("itemId", out var id) || id is not Guid itemId
            || scope.LibraryManager.GetItemById(itemId) is not { Path: { } path } item
            || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Versions of a movie are the .strm files in its folder; an episode is just its own file.
        var files = item is MediaBrowser.Controller.Entities.Movies.Movie
            ? Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.strm")
            : [path];
        return files.Select(ReadEntry).OfType<(string EntryId, int? Episode)>().ToList();
    }

    internal static bool TryParse(MediaSourceInfo source, out string entryId, out int? episode) =>
        TryParse(source.Path, out entryId, out episode);

    public static bool TryParse(string? path, out string entryId, out int? episode)
    {
        entryId = string.Empty;
        episode = null;
        if (path is null
            || !path.Contains(StreamMarker, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return false;
        }

        entryId = Uri.UnescapeDataString(uri.AbsolutePath[(uri.AbsolutePath.IndexOf(StreamMarker, StringComparison.OrdinalIgnoreCase) + StreamMarker.Length)..]);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "ep" && int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ep))
            {
                episode = ep;
            }
        }

        return entryId.Length > 0;
    }

    /// <summary>The plugin services a request needs, resolved when it needs them.</summary>
    private sealed class Scope(IServiceProvider services)
    {
        public ILibraryManager LibraryManager { get; } = services.GetRequiredService<ILibraryManager>();

        public ProbeWindows ProbeWindows => services.GetRequiredService<ProbeWindows>();

        public PlaybackIntents Intents => services.GetRequiredService<PlaybackIntents>();

        public MediaJobManager Jobs => services.GetRequiredService<MediaJobManager>();

        public UserNotifier Notifier => services.GetRequiredService<UserNotifier>();

        public PlayNotices Notices => services.GetRequiredService<PlayNotices>();

        public SoftFailTracker SoftFails => services.GetRequiredService<SoftFailTracker>();

        public AutoplayGuard Autoplay => services.GetRequiredService<AutoplayGuard>();

        public AccountStore Accounts => services.GetRequiredService<AccountStore>();

        public PluginSettings Settings => services.GetRequiredService<PluginSettings>();

        public PlaybackReach Reach => services.GetRequiredService<PlaybackReach>();

        public ISessionManager Sessions => services.GetRequiredService<ISessionManager>();

        public IUserManager Users => services.GetRequiredService<IUserManager>();

        public CacheStore Cache => services.GetRequiredService<CacheStore>();
    }
}

/// <summary>Whether a PlaybackInfo request starts playback, rather than only asking about the item.</summary>
public static class PlaybackSignals
{
    /// <summary>
    /// jellyfin-web posts PlaybackInfo with AutoOpenLiveStream=true exactly when it is about to play (its isPlayback
    /// option); detail pages and version lists post false or use GET.
    /// </summary>
    public static bool IsPlay(string? actionName, IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        if (!string.Equals(actionName, "GetPostedPlaybackInfo", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (name, value) in arguments)
        {
            if (string.Equals(name, "autoOpenLiveStream", StringComparison.OrdinalIgnoreCase) && value is true)
            {
                return true;
            }

            // The posted PlaybackInfoDto lives in Jellyfin.Api, which plugins can't reference.
            if (value?.GetType().GetProperty("AutoOpenLiveStream")?.GetValue(value) is true)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPlay(ActionExecutingContext context) =>
        IsPlay((context.ActionDescriptor as ControllerActionDescriptor)?.ActionName, context.ActionArguments);

    /// <summary>The request turns direct play off, as jellyfin-web does when it falls back after a direct play failed.</summary>
    public static bool DirectPlayDisabled(IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        foreach (var (name, value) in arguments)
        {
            if (string.Equals(name, "enableDirectPlay", StringComparison.OrdinalIgnoreCase) && value is false)
            {
                return true;
            }

            if (value?.GetType().GetProperty("EnableDirectPlay")?.GetValue(value) is false)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A player's report that playback started or stopped, from any of Jellyfin's endpoints for it.</summary>
public sealed record PlaystateReport(
    bool Stop,
    Guid ItemId,
    string? MediaSourceId,
    string? PlaySessionId,
    long? PositionTicks,
    string? NextMediaType = null,
    bool Failed = false,
    PlayMethod? PlayMethod = null,
    IReadOnlyList<Guid>? Queue = null)
{
    public static PlaystateReport? Read(string? actionName, IDictionary<string, object?> arguments)
    {
        T? Arg<T>(string name) => arguments.TryGetValue(name, out var value) && value is T typed ? typed : default;

        switch (actionName)
        {
            case "ReportPlaybackStart" when arguments.Values.OfType<PlaybackStartInfo>().FirstOrDefault() is { } start:
                return new PlaystateReport(false, start.ItemId, start.MediaSourceId, start.PlaySessionId, start.PositionTicks, PlayMethod: start.PlayMethod);
            case "ReportPlaybackStopped" when arguments.Values.OfType<PlaybackStopInfo>().FirstOrDefault() is { } stop:
                return new PlaystateReport(
                    true,
                    stop.ItemId,
                    stop.MediaSourceId,
                    stop.PlaySessionId,
                    stop.PositionTicks,
                    stop.NextMediaType,
                    stop.Failed,
                    Queue: stop.NowPlayingQueue?.Select(q => q.Id).Where(id => id != Guid.Empty).ToList());
            case "OnPlaybackStart" or "OnPlaybackStartLegacy":
                return new PlaystateReport(false, Arg<Guid>("itemId"), Arg<string>("mediaSourceId"), Arg<string>("playSessionId"), null, PlayMethod: Arg<PlayMethod?>("playMethod"));
            case "OnPlaybackStopped" or "OnPlaybackStoppedLegacy":
                return new PlaystateReport(true, Arg<Guid>("itemId"), Arg<string>("mediaSourceId"), Arg<string>("playSessionId"), Arg<long?>("positionTicks"), Arg<string>("nextMediaType"));
            default:
                return null;
        }
    }
}
